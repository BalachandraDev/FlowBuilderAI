using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Services;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Infrastructure.Ai;

/// <summary>One part of a Gemini message: text, or a file (audio or an image) sent inline.</summary>
public sealed record GeminiPart(string? Text, string? MimeType = null, byte[]? Data = null)
{
    public static GeminiPart OfText(string text) => new(text);
    public static GeminiPart OfFile(string mimeType, byte[] data) => new(null, mimeType, data);
}

/// <param name="LowThinking">The task is extraction or transcription, not puzzle solving: ask the model to think briefly. Thinking is what makes a call slow.</param>
/// <param name="Thinking">How hard to think: "low", "medium" or "high". Reading a long set of notes into a complete structure needs "medium"; transcribing a recording does not. Overrides <paramref name="LowThinking"/>.</param>
/// <param name="TimeoutSeconds">How long one attempt may take before it is given up and the next model is tried. Default 120.</param>
public sealed record GeminiRequest(
    string SystemPrompt, IReadOnlyList<GeminiPart> Parts, JsonObject? JsonSchema = null, bool JsonOutput = false,
    int MaxOutputTokens = 16000, double Temperature = 0.2, string? Model = null, bool LowThinking = false, int? TimeoutSeconds = null, string? Thinking = null);

public sealed record GeminiReply(string Text, int InputTokens, int OutputTokens, string FinishReason, string Model, int DurationMs);
public sealed record GeminiModelInfo(string Id, string DisplayName);

/// <summary>
/// The Gemini API from Google AI Studio, which has a free tier. One key does text, images and audio, so one client serves the analysis,
/// the voice transcription and the photo reading. The key comes from the admin dashboard and is sent in a header, never in the address.
///
/// The free tier is often busy (HTTP 503) or limited per model (HTTP 429), and the newest model is the busiest. So when the administrator
/// leaves the model on Automatic, the client keeps a short list of the best models the key can use, gives every attempt a time limit, and
/// on a busy or limited answer moves straight on to the next model instead of waiting and asking the same one again. A model that just
/// failed is put last for a few minutes. When the administrator names a model, that model alone is used.
/// </summary>
public sealed partial class GeminiClient(HttpClient http, ISettingsService settings, ILogger<GeminiClient> log, IAiQuota? quota = null, IModelQuota? modelQuota = null)
{
    private static readonly ConcurrentDictionary<string, (List<string> Models, DateTime Until)> ModelLists = new();
    private static readonly ConcurrentDictionary<string, (int Fails, DateTime Until)> Health = new();
    private static readonly ConcurrentDictionary<string, byte> NoThinkingConfig = new();
    private const int MaxAttempts = 3;               // for one named model
    private const int MaxFallbackAttempts = 6;       // across the automatic list
    private const int DefaultTimeoutSeconds = 120;
    /// <summary>After this long without an answer no further model is tried: the person is told, and can try again.</summary>
    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(200);

    /// <summary>Waits before a retry. Tests replace it so they do not really wait.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, ct) => Task.Delay(t, ct);

    public async Task<(string Key, string Model)> ResolveAsync(string? modelOverride, CancellationToken ct)
    {
        var (key, models, _) = await CandidatesAsync(modelOverride, ct);
        return (key, models[0]);
    }

    private static string KeyId(string key) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    /// <summary>The models to try, best first. One when the administrator named it, otherwise the key's best few with recent failures last.</summary>
    private async Task<(string Key, List<string> Models, bool Automatic)> CandidatesAsync(string? modelOverride, CancellationToken ct)
    {
        var s = await settings.GetProviderAsync(ct);
        if (string.IsNullOrWhiteSpace(s.GeminiApiKey))
            throw new AiServiceException("ai_not_configured", "The AI is not set up yet. Ask an administrator to add the free Gemini key in the Admin dashboard.");
        var named = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride : s.GeminiModel;
        if (!string.IsNullOrWhiteSpace(named)) return (s.GeminiApiKey, [named.Replace("models/", "")], false);

        var all = await AutoModelsAsync(s.GeminiApiKey, ct);
        var id = KeyId(s.GeminiApiKey);
        return (s.GeminiApiKey, [.. all.Where(m => !IsBusy(id, m)), .. all.Where(m => IsBusy(id, m))], true);
    }

    // ------------------------------------------------------------------ models

    public async Task<IReadOnlyList<GeminiModelInfo>> ListModelsAsync(string key, CancellationToken ct)
    {
        var all = new List<GeminiModelInfo>();
        string? page = null;
        for (var i = 0; i < 5; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "v1beta/models?pageSize=200" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)));
            req.Headers.Add("x-goog-api-key", key);
            var (status, body) = await SendAsync(req, 30, ct);
            if (status != HttpStatusCode.OK) throw ToException(status, body);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("models", out var models))
                foreach (var m in models.EnumerateArray())
                {
                    var methods = m.TryGetProperty("supportedGenerationMethods", out var sm) ? sm.EnumerateArray().Select(x => x.GetString()).ToList() : [];
                    if (!methods.Contains("generateContent")) continue;
                    var name = (m.GetProperty("name").GetString() ?? "").Replace("models/", "");
                    all.Add(new GeminiModelInfo(name, m.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? name : name));
                }
            page = doc.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
            if (string.IsNullOrEmpty(page)) break;
        }
        return all;
    }

    private async Task<List<string>> AutoModelsAsync(string key, CancellationToken ct)
    {
        var cacheKey = KeyId(key);
        if (ModelLists.TryGetValue(cacheKey, out var hit) && hit.Until > DateTime.UtcNow) { GeminiModelCache.Models = hit.Models; return hit.Models; }
        var models = await ListModelsAsync(key, ct);
        var ranked = Rank(models.Select(m => m.Id)).ToList();
        if (ranked.Count == 0) throw new AiServiceException("ai_bad_request", "This Gemini key cannot use any text model. Check the key in the Admin dashboard.");
        ModelLists[cacheKey] = (ranked, DateTime.UtcNow.AddHours(1));
        GeminiModelCache.Models = ranked;
        log.LogInformation("Gemini models to use, best first: {Models}", string.Join(", ", ranked));
        return ranked;
    }

    /// <summary>The newest stable Flash model (not Lite, preview, image, audio-only or experimental), or the best of what is left.</summary>
    public static string? PickBest(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        static double Version(string id) => VersionOf().Match(id) is { Success: true } m ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        static bool Special(string id) => SpecialModel().IsMatch(id);

        IEnumerable<string> Flash(bool allowSpecial) => list.Where(id => id.Contains("flash", StringComparison.OrdinalIgnoreCase) && !id.Contains("lite", StringComparison.OrdinalIgnoreCase)
            && (allowSpecial || !Special(id)));
        return Flash(false).OrderByDescending(Version).ThenBy(id => id.Length).FirstOrDefault()
               ?? Flash(true).OrderByDescending(Version).ThenBy(id => id.Length).FirstOrDefault()
               ?? list.Where(id => id.Contains("flash", StringComparison.OrdinalIgnoreCase)).OrderByDescending(Version).FirstOrDefault()
               ?? list.Where(id => id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) && !Special(id)).OrderByDescending(Version).FirstOrDefault()
               ?? list.FirstOrDefault();
    }

    /// <summary>
    /// The few models worth trying, best first: the one <see cref="PickBest"/> chooses, then the other stable Flash models (each model has its
    /// own free quota and its own load), then stable Flash-Lite, then previews. Models that cannot write text (image, audio, embedding) are never listed.
    /// </summary>
    public static IReadOnlyList<string> Rank(IEnumerable<string> ids, int max = 5)
    {
        var list = ids.ToList();
        static double Version(string id) => VersionOf().Match(id) is { Success: true } m ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        static int Generation(string id) => (int)Math.Floor(Version(id));                  // 3.8 and 3.5 are the same generation: they share the same busy servers
        static bool Flash(string id) => id.Contains("flash", StringComparison.OrdinalIgnoreCase);
        static bool Lite(string id) => id.Contains("lite", StringComparison.OrdinalIgnoreCase);
        static bool Special(string id) => SpecialModel().IsMatch(id);
        static bool TextPreview(string id) => id.Contains("preview", StringComparison.OrdinalIgnoreCase) && !NotText().IsMatch(id);
        IEnumerable<string> Ordered(Func<string, bool> where) => list.Where(where).OrderByDescending(Version).ThenBy(id => id.Length);

        var stable = Ordered(id => Flash(id) && !Lite(id) && !Special(id)).ToList();
        var lite = Ordered(id => Flash(id) && Lite(id) && !Special(id)).ToList();

        // When the newest model is overloaded, its sister models from the same generation usually are too. So after the best one come the
        // best of the OTHER generations (each generation has its own capacity and its own free quota), then Lite, and only then more of the same.
        var result = new List<string>();
        if (PickBest(list) is { } first) result.Add(first);
        var firstGeneration = result.Count > 0 ? Generation(result[0]) : -1;
        result.AddRange(stable.Where(id => Generation(id) != firstGeneration).GroupBy(Generation).OrderByDescending(g => g.Key).Select(g => g.First()));
        result.AddRange(lite.GroupBy(Generation).OrderByDescending(g => g.Key).Select(g => g.First()));
        result.AddRange(stable);
        result.AddRange(Ordered(id => Flash(id) && TextPreview(id)));
        return result.Distinct().Take(max).ToList();
    }

    /// <summary>The models a call would try, best first. Used by the key test.</summary>
    public async Task<IReadOnlyList<string>> CandidateModelsAsync(CancellationToken ct) => (await CandidatesAsync(null, ct)).Models;

    public sealed record GeminiProbe(string Model, bool Ok, int Milliseconds, string Message, int InputTokens, int OutputTokens);

    /// <summary>Asks every model for one word at the same time, each with its own time limit, and says which answered and how fast. For the admin's key test.</summary>
    public async Task<IReadOnlyList<GeminiProbe>> ProbeAsync(IReadOnlyList<string> models, CancellationToken ct)
    {
        var s = await settings.GetProviderAsync(ct);
        if (string.IsNullOrWhiteSpace(s.GeminiApiKey)) throw new AiServiceException("ai_not_configured", "The AI is not set up yet.");
        var key = s.GeminiApiKey;
        var request = new GeminiRequest("", [GeminiPart.OfText("Reply with the single word OK.")], MaxOutputTokens: 2048, Temperature: 0, LowThinking: true, TimeoutSeconds: 30);
        // a test asks every model, and each question is one request of its daily allowance: a model that has used its day is not asked
        var quotas = modelQuota is null ? [] : await modelQuota.GetManyAsync(models, ct);
        var tasks = models.Select(async model =>
        {
            if (quotas.FirstOrDefault(q => q.Model == model) is { DayReached: true } spent)
                return new GeminiProbe(model, false, 0, "Daily limit used up (" + spent.Describe().Split(": ", 2)[1] + "). Not asked, to keep the requests it has.", 0, 0);
            var timer = Stopwatch.StartNew();
            try
            {
                var reply = await AttemptAsync(key, model, request, timer, 1, 1, ct);
                Health.TryRemove(KeyId(key) + "|" + model, out _);                  // what the test learns is used by the next analysis
                return new GeminiProbe(model, true, (int)timer.ElapsedMilliseconds, "Answered.", reply.InputTokens, reply.OutputTokens);
            }
            catch (AiServiceException e)
            {
                if (CanTryNext(e, true)) MarkBusy(KeyId(key), model, e);
                return new GeminiProbe(model, false, (int)timer.ElapsedMilliseconds, e.Data.Contains("modelGone") ? "No longer available." : e.Code switch
                {
                    "ai_unavailable" => "Busy or not answering.", "ai_rate_limited" => "Free limit reached for now.", "ai_auth" => "Not allowed for this key.", _ => e.Message,
                }, 0, 0);
            }
        });
        return await Task.WhenAll(tasks);
    }

    [GeneratedRegex(@"gemini-(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)] private static partial Regex VersionOf();
    [GeneratedRegex(@"preview|exp|image|tts|live|audio|embedding|robotics|computer|thinking|dialog|native", RegexOptions.IgnoreCase)] private static partial Regex SpecialModel();
    [GeneratedRegex(@"image|tts|live|audio|embedding|robotics|computer|dialog|native", RegexOptions.IgnoreCase)] private static partial Regex NotText();

    // ------------------------------------------------------------------ a model that just failed goes last for a while

    private static bool IsBusy(string keyId, string model) => Health.TryGetValue(keyId + "|" + model, out var h) && h.Until > DateTime.UtcNow;

    private static void MarkBusy(string keyId, string model, AiServiceException e)
    {
        var k = keyId + "|" + model;
        var fails = Health.TryGetValue(k, out var h) ? h.Fails + 1 : 1;
        var minutes = e.Data.Contains("modelGone") ? 60 : e.Data.Contains("perDay") ? 30 : Math.Min(30, 4 << Math.Min(fails - 1, 3));   // 4, 8, 16, 30 minutes; a used-up daily quota or a retired model stays that way
        Health[k] = (fails, DateTime.UtcNow.AddMinutes(minutes));
    }

    /// <summary>A model that failed before gets a short time limit for a while, so trying it again can never cost a long wait.</summary>
    internal static int ProbationSeconds = 25;
    /// <summary>How long a model may stay silent while others could still be asked.</summary>
    internal static int FirstTrySeconds = 35;
    /// <summary>After this long without an answer the next model is asked as well (0 turns it off).</summary>
    internal static int HedgeSeconds = 12;
    private static bool OnProbation(string keyId, string model) => Health.ContainsKey(keyId + "|" + model);

    private static bool IsTransient(AiServiceException e) => e.Code is "ai_rate_limited" or "ai_unavailable";

    /// <summary>Another model may do better: it is busy or limited, or (only when the model was not named by the admin) it no longer exists.</summary>
    private static bool CanTryNext(AiServiceException e, bool automatic) => IsTransient(e) || (automatic && e.Data.Contains("modelGone"));

    // ------------------------------------------------------------------ generating

    /// <summary>
    /// Photos and recordings are read by the AI one request each, and someone can attach a folder of them at once. Only a few are sent at the same
    /// moment, so they do not all hit the free quota together and fail; the rest wait their turn (the waiting is not counted in the time limits).
    /// </summary>
    internal static readonly SemaphoreSlim MediaGate = new(3, 3);

    public async Task<GeminiReply> GenerateAsync(GeminiRequest r, CancellationToken ct)
    {
        // the administrator's daily limit for this free account: at the limit, nothing more is sent to it
        if (quota is not null) await quota.EnsureAvailableAsync("Gemini", ct);
        var media = r.Parts.Any(p => p.Data is not null);
        if (media) await MediaGate.WaitAsync(ct);
        try { return await GenerateCoreAsync(r, ct); }
        finally { if (media) MediaGate.Release(); }
    }

    private async Task<GeminiReply> GenerateCoreAsync(GeminiRequest r, CancellationToken ct)
    {
        var (key, models, automatic) = await CandidatesAsync(r.Model, ct);
        models = await UsableModelsAsync(models, r, ct);
        var id = KeyId(key);
        var limit = models.Count == 1 ? MaxAttempts : Math.Min(MaxFallbackAttempts, models.Count * 2);
        var sw = Stopwatch.StartNew();
        AiServiceException? last = null;
        var tried = new List<string>();

        for (var attempt = 1; attempt <= limit; attempt++)
        {
            var model = models[(attempt - 1) % models.Count];
            if (attempt > 1 && sw.Elapsed > TimeBudget)
            {
                log.LogWarning("Gemini gave no answer in {Seconds}s across {Count} attempt(s): giving up", (int)sw.Elapsed.TotalSeconds, attempt - 1);
                break;
            }
            if (attempt > 1 && (attempt - 1) % models.Count == 0)
            {
                // every model had its turn: only now is waiting worth it
                var wait = last?.Data["retryAfter"] as TimeSpan? ?? TimeSpan.FromSeconds(models.Count == 1 ? (attempt == 2 ? 5 : 15) : (attempt - 1) / models.Count == 1 ? 3 : 8);
                log.LogWarning("Every Gemini model tried was busy or limited; waiting {Seconds}s before another round", (int)wait.TotalSeconds);
                await Delay(wait, ct);
            }
            // Each model is asked with its own time limit. With other models to fall back on, no single one may hold the answer for long: a usual
            // answer takes 5-30 s, so a model silent for FirstTrySeconds is given up. Only the last attempt gets the full time. (A very large
            // request gets a little longer: 1 s more per 1000 tokens read.)
            GeminiRequest RequestFor(string m, int att)
            {
                var full = r.TimeoutSeconds ?? DefaultTimeoutSeconds;
                var seconds = automatic && models.Count > 1 && att < limit ? Math.Min(full, FirstTrySeconds + (int)(EstimateTokens(r) / 1000)) : full;
                if (automatic && OnProbation(id, m)) seconds = Math.Min(seconds, ProbationSeconds);
                return seconds == full ? r : r with { TimeoutSeconds = seconds };
            }

            // The model is asked; if it has not answered within HedgeSeconds the next model is asked too, without giving up on the first, and the
            // first good answer wins. A model that hangs therefore costs HedgeSeconds, not its whole time limit. (Only with models to spare.)
            using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var running = new List<(string Model, Task<GeminiReply> Task)> { (model, AttemptAsync(key, model, RequestFor(model, attempt), sw, attempt, limit, race.Token)) };
            try
            {
                if (automatic && models.Count > 1 && attempt < limit && HedgeSeconds > 0)
                {
                    var silent = await Task.WhenAny(running[0].Task, Task.Delay(TimeSpan.FromSeconds(HedgeSeconds), ct)) != running[0].Task;
                    if (silent)
                    {
                        attempt++;
                        var next = models[(attempt - 1) % models.Count];
                        log.LogWarning("Gemini {Model} has not answered in {Seconds}s; asking {Next} as well and taking the first answer", model, HedgeSeconds, next);
                        running.Add((next, AttemptAsync(key, next, RequestFor(next, attempt), sw, attempt, limit, race.Token)));
                    }
                }

                while (running.Count > 0)
                {
                    var finished = await Task.WhenAny(running.Select(x => x.Task));
                    var winner = running.First(x => x.Task == finished);
                    running.Remove(winner);
                    try
                    {
                        var reply = await finished;
                        Health.TryRemove(id + "|" + winner.Model, out _);
                        if (tried.Count > 0) log.LogInformation("Gemini answered with {Model} after {Failed} failed attempt(s) on {Tried}", winner.Model, tried.Count, string.Join(", ", tried));
                        return reply;
                    }
                    catch (AiServiceException e) when (CanTryNext(e, automatic))
                    {
                        last = e;
                        tried.Add(winner.Model);
                        if (automatic) MarkBusy(id, winner.Model, e);
                        if (running.Count == 0 && attempt < limit && models.Count > 1) log.LogWarning("Gemini {Model} is busy or limited ({Code}); trying {Next}", winner.Model, e.Code, models[attempt % models.Count]);
                    }
                }
            }
            finally
            {
                // whoever is still waiting for an answer is let go (its result is not wanted), and its outcome is looked at so nothing is left unobserved
                race.Cancel();
                foreach (var (_, loser) in running) _ = loser.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        var distinct = tried.Distinct().ToList();
        throw last!.Code == "ai_unavailable" && distinct.Count > 1
            ? new AiServiceException("ai_unavailable", $"Gemini is overloaded right now: {distinct.Count} models were tried and none answered. Nothing was lost. Try again in a minute or two.", retryable: true)
            : last;
    }

    /// <summary>A rough size of a request in tokens, to see whether it fits what is left of a model's tokens-a-minute.</summary>
    private static long EstimateTokens(GeminiRequest r) =>
        (r.SystemPrompt.Length + r.Parts.Sum(p => p.Data is null ? p.Text?.Length ?? 0 : 6000) + (r.JsonSchema?.ToJsonString().Length ?? 0)) / 4 + 1500;

    /// <summary>
    /// Keeps the models that have room: not at their limit for the day, and not at their limit for the minute. When only the per-minute limit stops them all,
    /// the call waits for the window to clear (at most a minute); when every model has used its day, it says so and sends nothing.
    /// </summary>
    private async Task<List<string>> UsableModelsAsync(List<string> models, GeminiRequest r, CancellationToken ct)
    {
        if (modelQuota is null) return models;
        var estimate = EstimateTokens(r);
        for (var round = 0; ; round++)
        {
            var quotas = await modelQuota.GetManyAsync(models, ct);
            var usable = models.Where(m => quotas.First(q => q.Model == m) is var q && !q.DayReached && !q.MinuteBlocked(estimate)).ToList();
            if (usable.Count > 0)
            {
                if (usable.Count < models.Count) log.LogInformation("Gemini models with room right now: {Usable} (left out: {Left})", string.Join(", ", usable), string.Join(", ", models.Except(usable)));
                return usable;
            }
            var waiting = quotas.Where(q => !q.DayReached).ToList();
            if (waiting.Count == 0 || round >= 1)
                throw new AiServiceException("ai_quota_reached", "The free Gemini AI has used all its requests for today: " + string.Join("; ", quotas.Select(q => q.Describe())) +
                    ". Nothing more is sent until the day renews (midnight Pacific time), and nothing is lost. An administrator can see the limits in AI setup.", retryable: false);
            var wait = waiting.Min(q => q.WaitForMinute);
            log.LogInformation("Every Gemini model is at its per-minute limit; waiting {Seconds}s", (int)wait.TotalSeconds);
            await Delay(wait + TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>Writes down one request that went to Google (counting never breaks a call).</summary>
    private Task CountAsync(string model, string outcome, int inputTokens = 0, int outputTokens = 0) =>
        modelQuota is null ? Task.CompletedTask : modelQuota.RecordAsync("Gemini", model, outcome, inputTokens, outputTokens);

    /// <summary>One model, one go: with the schema, and if the model refuses it, once more with the schema in the instructions; likewise for the thinking setting.</summary>
    private async Task<GeminiReply> AttemptAsync(string key, string model, GeminiRequest r, Stopwatch sw, int attempt, int limit, CancellationToken ct)
    {
        var request = r;
        var withSchema = r.JsonSchema is not null;
        var level = r.Thinking ?? (r.LowThinking ? "low" : null);
        var thinking = level is not null && ThinkingFor(model, level) is not null && !NoThinkingConfig.ContainsKey(model);
        for (var step = 0; ; step++)
        {
            try
            {
                return await SendOnceAsync(key, model, request, withSchema, thinking ? level : null, sw, attempt, limit, ct);
            }
            catch (AiServiceException e) when (e.Code == "ai_schema_rejected" && withSchema && step < 3)
            {
                // Some models accept only part of JSON Schema. Ask again with the schema written into the instructions instead.
                log.LogWarning("Gemini model {Model} rejected the response schema; asking again with the schema in the prompt", model);
                request = r with
                {
                    SystemPrompt = r.SystemPrompt + "\n\nYour whole answer must be one JSON object that follows this JSON Schema exactly:\n" + r.JsonSchema!.ToJsonString(),
                    JsonSchema = null, JsonOutput = true,
                };
                withSchema = false;
            }
            catch (AiServiceException e) when (e.Code == "ai_thinking_rejected" && thinking && step < 3)
            {
                log.LogWarning("Gemini model {Model} does not accept the thinking setting; asking again without it", model);
                NoThinkingConfig[model] = 1;
                thinking = false;
            }
        }
    }

    /// <summary>How to tell a model to think briefly. Null when its family has no such setting.</summary>
    private static JsonObject? ThinkingFor(string model, string level) =>
        model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase) ? new JsonObject { ["thinkingLevel"] = level }
        : model.Contains("2.5-flash", StringComparison.OrdinalIgnoreCase) && !model.Contains("lite", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject { ["thinkingBudget"] = level == "high" ? 8192 : level == "medium" ? 4096 : 1024 }
        : null;

    private async Task<GeminiReply> SendOnceAsync(string key, string model, GeminiRequest r, bool withSchema, string? thinkingLevel, Stopwatch sw, int attempt, int limit, CancellationToken ct)
    {
        var parts = new JsonArray();
        foreach (var p in r.Parts)
            parts.Add(p.Data is null
                ? new JsonObject { ["text"] = p.Text }
                : new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = p.MimeType, ["data"] = Convert.ToBase64String(p.Data) } });

        var config = new JsonObject { ["temperature"] = r.Temperature, ["maxOutputTokens"] = r.MaxOutputTokens };
        if (r.JsonOutput || withSchema) config["responseMimeType"] = "application/json";
        if (withSchema) config["responseJsonSchema"] = JsonNode.Parse(r.JsonSchema!.ToJsonString());
        if (thinkingLevel is not null && ThinkingFor(model, thinkingLevel) is { } think) config["thinkingConfig"] = think;

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = config,
        };
        if (!string.IsNullOrWhiteSpace(r.SystemPrompt))
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = r.SystemPrompt }) };
        var json = body.ToJsonString();

        using var req = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", key);
        var attemptTimer = Stopwatch.StartNew();
        HttpStatusCode status; string text;
        try { (status, text) = await SendAsync(req, r.TimeoutSeconds ?? DefaultTimeoutSeconds, ct, model, attempt, limit); }
        catch (AiServiceException) { await CountAsync(model, "no_answer"); throw; }

        // Timing only: never the key, the prompt or the answer. This is how a slow or throttled call is told apart from a slow model.
        log.LogInformation("Gemini {Model} attempt {Attempt}/{Max}: HTTP {Status} in {AttemptMs} ms (request {RequestKb} KB, whole call so far {TotalMs} ms{Reason})",
            model, attempt, limit, (int)status, attemptTimer.ElapsedMilliseconds, json.Length / 1024, sw.ElapsedMilliseconds, status == HttpStatusCode.OK ? "" : ", reason: " + ReasonOf(text));

        if (status == HttpStatusCode.OK)
        {
            var reply = Parse(text, model, (int)sw.ElapsedMilliseconds);
            await CountAsync(model, "ok", reply.InputTokens, reply.OutputTokens);
            // how many free tokens this one call used (the answer includes the AI's thinking): the numbers only, never the text
            log.LogInformation("Gemini {Model} used {InputTokens} input and {OutputTokens} output tokens (thinking {Thinking})", model, reply.InputTokens, reply.OutputTokens, thinkingLevel ?? "default");
            return reply;
        }

        await CountAsync(model, PerDay().IsMatch(text) ? "daily_limit" : status == HttpStatusCode.TooManyRequests ? "limited" : (int)status >= 500 ? "busy" : "error");
        var ex = ToException(status, text);
        if (status == HttpStatusCode.BadRequest && withSchema && Regex.IsMatch(Message(text), "schema|response_?schema|json_schema|\\$ref|additionalProperties", RegexOptions.IgnoreCase))
            throw new AiServiceException("ai_schema_rejected", ex.Message);
        if (status == HttpStatusCode.BadRequest && thinkingLevel is not null && Regex.IsMatch(Message(text), "thinking", RegexOptions.IgnoreCase))
            throw new AiServiceException("ai_thinking_rejected", ex.Message);
        if (status == HttpStatusCode.NotFound) ex.Data["modelGone"] = true;
        if (PerDay().IsMatch(text)) ex.Data["perDay"] = true;
        if (RetryDelay(text) is { } hint) ex.Data["retryAfter"] = hint;
        throw ex;
    }

    private static GeminiReply Parse(string body, string model, int ms)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        int Tokens(string name) => root.TryGetProperty("usageMetadata", out var u) && u.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
        var input = Tokens("promptTokenCount");
        var output = Tokens("candidatesTokenCount") + Tokens("thoughtsTokenCount");

        if (!root.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0)
        {
            var reason = root.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out var br) ? br.GetString() : null;
            throw new AiServiceException("ai_refusal", reason is null
                ? "The AI returned no answer."
                : "The AI declined to process this input (" + reason + "). Review the text and try again.");
        }

        var cand = cands[0];
        var finish = cand.TryGetProperty("finishReason", out var fr) ? fr.GetString() ?? "" : "";
        var sb = new StringBuilder();
        if (cand.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
            foreach (var p in parts.EnumerateArray())
            {
                if (p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True) continue;
                if (p.TryGetProperty("text", out var t)) sb.Append(t.GetString());
            }

        if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "RECITATION" or "SPII")
            throw new AiServiceException("ai_refusal", "The AI declined to process this input. Review the text and try again.");
        if (sb.Length == 0)
            throw new AiServiceException(finish == "MAX_TOKENS" ? "ai_truncated" : "ai_no_tool_call",
                finish == "MAX_TOKENS" ? "The AI output was cut off before it finished. The flow may be too large for one analysis." : "The AI did not return an answer.", retryable: true);
        return new GeminiReply(sb.ToString(), input, output, finish, model, ms);
    }

    // ------------------------------------------------------------------ http

    /// <summary>Sends one request. It is given up after <paramref name="timeoutSeconds"/>: a model that hangs must not hold the whole analysis.</summary>
    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage req, int timeoutSeconds, CancellationToken ct, string? model = null, int attempt = 1, int limit = 1)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var res = await http.SendAsync(req, timeout.Token);
            return (res.StatusCode, await res.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (HttpRequestException e)
        {
            log.LogWarning(e, "Gemini could not be reached");
            throw new AiServiceException("ai_unavailable", "The AI service could not be reached. Check the internet connection of the server and try again.", retryable: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Gemini {Model} attempt {Attempt}/{Max} gave no answer within {Seconds}s and was given up", model ?? "(model list)", attempt, limit, timeoutSeconds);
            throw new AiServiceException("ai_unavailable", "The AI service took too long to answer. Try again.", retryable: true);
        }
    }

    /// <summary>The provider's own status word (for example RESOURCE_EXHAUSTED), which says whether a limit is per minute, per day or the model is just busy.</summary>
    private static string ReasonOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var e)) return "none given";
            var status = e.TryGetProperty("status", out var s) ? s.GetString() : null;
            var quota = Regex.Match(e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "", @"(PerMinute|PerDay|per minute|per day)", RegexOptions.IgnoreCase);
            return (status ?? "unknown") + (quota.Success ? " (" + quota.Value + ")" : "");
        }
        catch (JsonException) { return "unreadable"; }
    }

    private static string Message(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    private static TimeSpan? RetryDelay(string body)
    {
        var m = RetryIn().Match(body);
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var s) && s <= 60 ? TimeSpan.FromSeconds(Math.Ceiling(s) + 1) : null;
    }

    [GeneratedRegex(@"""retryDelay""\s*:\s*""(\d+(?:\.\d+)?)s""")] private static partial Regex RetryIn();
    [GeneratedRegex(@"PerDay|per day", RegexOptions.IgnoreCase)] private static partial Regex PerDay();

    public static AiServiceException ToException(HttpStatusCode status, string body)
    {
        var message = Message(body);
        if (message.Contains("API key", StringComparison.OrdinalIgnoreCase) && status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new AiServiceException("ai_auth", "Gemini rejected the key. Ask an administrator to check or replace it in the Admin dashboard.");
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AiServiceException("ai_auth", "Gemini rejected the key or this model is not allowed for it. Ask an administrator to check it in the Admin dashboard."),
            HttpStatusCode.NotFound => new AiServiceException("ai_bad_request", "Gemini does not know the chosen model. Ask an administrator to pick another one in the Admin dashboard."),
            HttpStatusCode.TooManyRequests => new AiServiceException("ai_rate_limited", "The free AI limit has been reached for now (per minute or per day). Wait a few minutes and try again.", retryable: true),
            HttpStatusCode.BadRequest => new AiServiceException("ai_bad_request", "Gemini rejected the request" + (message.Length > 0 ? ": " + (message.Length > 200 ? message[..200] : message) : ".")),
            _ when (int)status >= 500 => new AiServiceException("ai_unavailable", "Gemini is temporarily unavailable. Try again in a few minutes.", retryable: true),
            _ => new AiServiceException("ai_error", "The AI call failed (" + (int)status + ")."),
        };
    }
}
