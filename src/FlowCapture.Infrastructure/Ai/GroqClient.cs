using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Services;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Infrastructure.Ai;

/// <summary>Groq cannot be used right now (the key is wrong, the free allowance is used up, or the service is busy). The caller may use another AI instead.</summary>
public sealed class GroqUnavailableException(string message, bool retryable, bool keyProblem = false) : Exception(message)
{
    public bool Retryable { get; } = retryable;
    public bool KeyProblem { get; } = keyProblem;
}

/// <summary>What Groq last said about the free allowance of one model, taken from the headers of its answers.</summary>
public sealed record GroqLimit(string Model, int? RequestsLimit, int? RequestsRemaining, string? RequestsReset, int? TokensLimit, int? TokensRemaining, string? TokensReset, DateTime At);

/// <summary>
/// Groq's free account (OpenAI compatible). Used for voice notes: Whisper turns a recording into text on Groq's own allowance, which leaves the
/// Gemini allowance for analysing the notes and reading photos. Only the recording itself is sent; never the key in a log or an error.
/// </summary>
public sealed class GroqClient(HttpClient http, ISettingsService settings, ILogger<GroqClient> log, IAiQuota? quota = null)
{
    private const string Hint = "Hospital software requirements: ward, nurse, doctor, discharge, billing, OPD, IPD, MRD, case sheet, pharmacy, laboratory.";

    /// <summary>The latest allowance Groq reported for each model, shown to the administrator.</summary>
    public static readonly ConcurrentDictionary<string, GroqLimit> Limits = new();

    public async Task<(string Text, string Model)> TranscribeAsync(byte[] audio, string mimeType, string fileName, CancellationToken ct)
    {
        var g = await settings.GetGroqAsync(ct);
        if (g.ApiKey is null) throw new GroqUnavailableException("No Groq key has been saved.", retryable: false, keyProblem: true);
        if (quota is not null)
        {
            // the administrator's daily limit for Groq: at the limit the recording is not sent (the caller may use Gemini instead)
            try { await quota.EnsureAvailableAsync("Groq", ct); }
            catch (AiServiceException e) when (e.Code == "ai_quota_reached") { throw new GroqUnavailableException(e.Message, retryable: false); }
        }

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType) ? "audio/wav" : mimeType);
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "recording.wav" : fileName);
        form.Add(new StringContent(g.SttModel), "model");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0"), "temperature");
        form.Add(new StringContent(Hint), "prompt");          // spelling of the usual terms; it is not an instruction to the model

        using var req = new HttpRequestMessage(HttpMethod.Post, "audio/transcriptions") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", g.ApiKey);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage res;
        string body;
        try
        {
            res = await http.SendAsync(req, ct);
            body = await res.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Groq {Model}: no answer ({Kind}) after {Ms} ms", g.SttModel, e.GetType().Name, timer.ElapsedMilliseconds);
            throw new GroqUnavailableException("Groq did not answer in time.", retryable: true);
        }

        Remember(g.SttModel, res);
        log.LogInformation("Groq {Model}: HTTP {Status} in {Ms} ms (recording {Kb} KB)", g.SttModel, (int)res.StatusCode, timer.ElapsedMilliseconds, audio.Length / 1024);

        if (res.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            return (text.Trim(), g.SttModel);
        }

        var reason = ReasonOf(body);
        throw res.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new GroqUnavailableException("The Groq key was not accepted. Check it in the Admin dashboard.", retryable: false, keyProblem: true),
            HttpStatusCode.TooManyRequests => new GroqUnavailableException("The free Groq allowance is used up for now" + RetryText(res) + ".", retryable: true),
            HttpStatusCode.RequestEntityTooLarge => new SttException("This recording is too large to transcribe. Record shorter notes (up to about 7 minutes each).", retryable: false),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new SttException("The recording could not be read as audio" + (reason.Length > 0 ? " (" + reason + ")" : "") + ".", retryable: false),
            HttpStatusCode.NotFound => new GroqUnavailableException("Groq does not know the voice model '" + g.SttModel + "'. Check the model name in the Admin dashboard.", retryable: false, keyProblem: true),
            _ => new GroqUnavailableException("Groq is busy or unavailable (" + (int)res.StatusCode + ").", retryable: true),
        };
    }

    /// <summary>Asks Groq which models the key can use: proves the key without spending any of the allowance.</summary>
    public async Task<int> CountModelsAsync(CancellationToken ct)
    {
        var g = await settings.GetGroqAsync(ct);
        if (g.ApiKey is null) throw new GroqUnavailableException("No Groq key has been saved yet.", retryable: false, keyProblem: true);
        using var req = new HttpRequestMessage(HttpMethod.Get, "models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", g.ApiKey);
        try
        {
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new GroqUnavailableException("The Groq key was not accepted.", retryable: false, keyProblem: true);
            if (!res.IsSuccessStatusCode) throw new GroqUnavailableException("Groq answered with an error (" + (int)res.StatusCode + ").", retryable: true);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("data", out var data) ? data.GetArrayLength() : 0;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new GroqUnavailableException("Groq did not answer.", retryable: true);
        }
    }

    private static void Remember(string model, HttpResponseMessage res)
    {
        int? Num(string name) => res.Headers.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), out var n) ? n : null;
        string? Txt(string name) => res.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
        var limit = Num("x-ratelimit-limit-requests");
        if (limit is null && Num("x-ratelimit-limit-tokens") is null) return;
        Limits[model] = new GroqLimit(model, limit, Num("x-ratelimit-remaining-requests"), Txt("x-ratelimit-reset-requests"),
            Num("x-ratelimit-limit-tokens"), Num("x-ratelimit-remaining-tokens"), Txt("x-ratelimit-reset-tokens"), DateTime.UtcNow);
    }

    private static string RetryText(HttpResponseMessage res) =>
        res.Headers.TryGetValues("retry-after", out var v) && int.TryParse(v.FirstOrDefault(), out var s) && s > 0
            ? $"; it can be used again in about {(s >= 90 ? (s / 60) + " minutes" : s + " seconds")}" : "";

    private static string ReasonOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var m = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
            return m.Length > 160 ? m[..160] : m;
        }
        catch (JsonException) { return ""; }
    }
}

/// <summary>Voice notes to text with Groq's Whisper. Meant for recordings up to about seven minutes.</summary>
public sealed class GroqSpeechToText(GroqClient client) : ISpeechToTextService
{
    private const long MaxBytes = 24 * 1024 * 1024;       // Groq's free account takes files up to 25 MB

    public async Task<SttResult> TranscribeAsync(Stream audio, string contentType, string fileName, IReadOnlyList<string> candidateLocales, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await audio.CopyToAsync(ms, ct);
        if (ms.Length > MaxBytes)
            throw new SttException("This recording is too long to transcribe in one go. Record shorter notes (up to about 7 minutes each).", retryable: false);

        var (text, model) = await client.TranscribeAsync(ms.ToArray(), GeminiSpeechToText.MimeFor(contentType), fileName, ct);
        if (text.Length == 0)
            throw new SttException("No speech was detected in the recording.", retryable: false);
        return new SttResult(text, candidateLocales.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "en-IN", null, null, "groq", 0, Math.Max(1, text.Length / 4), model);
    }
}
