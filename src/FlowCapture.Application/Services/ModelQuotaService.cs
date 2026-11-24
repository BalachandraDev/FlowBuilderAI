using System.Text.RegularExpressions;
using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using Microsoft.EntityFrameworkCore;
using FlowCapture.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;

namespace FlowCapture.Application.Services;

/// <summary>The limits of one model. Null means the app does not limit it (Google still does).</summary>
public sealed record ModelLimits(long? PerDay, long? PerMinute, long? TokensPerMinute, bool IsPreset);

/// <summary>What one model has used against its limits right now.</summary>
public sealed record ModelQuota(string Model, long UsedToday, long UsedLastMinute, long TokensLastMinute, ModelLimits Limits, DateTime ResetsAtUtc, DateTime? OldestInMinuteUtc, bool GoogleSaidDayIsUsed, DateTime NowUtc)
{
    public bool DayReached => GoogleSaidDayIsUsed || (Limits.PerDay is > 0 && UsedToday >= Limits.PerDay);

    /// <summary>Too many requests, or too many tokens, in the last minute for one more request of about <paramref name="estimatedTokens"/> tokens.</summary>
    public bool MinuteBlocked(long estimatedTokens) =>
        (Limits.PerMinute is > 0 && UsedLastMinute >= Limits.PerMinute) || (Limits.TokensPerMinute is > 0 && UsedLastMinute > 0 && TokensLastMinute + estimatedTokens > Limits.TokensPerMinute);

    /// <summary>How long until the minute window has room again.</summary>
    public TimeSpan WaitForMinute => OldestInMinuteUtc is { } oldest ? TimeSpan.FromSeconds(Math.Clamp((oldest.AddSeconds(61) - NowUtc).TotalSeconds, 1, 61)) : TimeSpan.Zero;

    /// <summary>Requests left today, or null when the app does not limit this model per day.</summary>
    public long? LeftToday => Limits.PerDay is > 0 ? (GoogleSaidDayIsUsed ? 0 : Math.Max(0, Limits.PerDay.Value - UsedToday)) : (GoogleSaidDayIsUsed ? 0 : null);

    public string Describe()
    {
        var wait = ResetsAtUtc - NowUtc;
        var when = wait.TotalHours < 1 ? $"in {(int)Math.Max(1, Math.Ceiling(wait.TotalMinutes))} min" : $"in {(int)wait.TotalHours} h {wait.Minutes} min";
        var used = Limits.PerDay is > 0 ? $"{UsedToday:N0} of {Limits.PerDay:N0} requests" : $"{UsedToday:N0} requests";
        return $"{Pretty(Model)}: {used} today, renews {when}";
    }

    /// <summary>"gemini-3.8-flash" becomes "Gemini 3.8 Flash".</summary>
    public static string Pretty(string model) => string.Join(' ', model.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Length > 0 ? char.ToUpperInvariant(p[0]) + p[1..] : p));
}

/// <summary>What the free Gemini has left today across the models the app would use: for everybody to see, and for the app to decide.</summary>
public sealed record AiCapacity(bool Reached, long? RequestsLeft, DateTime ResetsAtUtc, string? Message, DateTime NowUtc);

public interface IModelQuota
{
    /// <summary>The limits and the use of each model (the use is counted from every request the app sent to it).</summary>
    Task<IReadOnlyList<ModelQuota>> GetManyAsync(IEnumerable<string> models, CancellationToken ct = default);
    /// <summary>Every model with a limit or with use today, for the admin page.</summary>
    Task<IReadOnlyList<ModelQuota>> GetKnownAsync(CancellationToken ct = default);
    Task<AiCapacity> GetCapacityAsync(CancellationToken ct = default);
    /// <summary>Writes down one request that was sent. Never throws: counting must not break a call.</summary>
    Task RecordAsync(string provider, string model, string outcome, int inputTokens, int outputTokens, CancellationToken ct = default);
}

/// <summary>Set by the Gemini client when it learns which models the key can use, so the capacity can be worked out without asking Google.</summary>
public static class GeminiModelCache
{
    public static volatile IReadOnlyList<string> Models = [];
}

/// <summary>
/// Free Gemini limits are per model: requests a minute, tokens a minute and requests a day (for example 5, 250,000 and 20 for Gemini 3.8 Flash), and Google
/// counts the requests that failed as well. The app counts every request it sends, per model, and stops sending to a model that is at a limit.
/// The first limits come from the administrator's Google AI Studio page and can be changed in AI setup.
/// </summary>
public sealed class ModelQuotaService(IAppDbContext db, ISettingsService settings, IClock clock, IServiceScopeFactory scopes) : IModelQuota
{
    private const string Prefix = "Ai.ModelLimit.";
    private static readonly Regex KeyShape = new(@"^Ai\.ModelLimit\.[a-z0-9][a-z0-9.\-]{1,80}\.(PerDay|PerMinute|TokensPerMinute)$", RegexOptions.Compiled);
    public static bool IsModelLimitKey(string key) => KeyShape.IsMatch(key);
    public static string KeyFor(string model, string what) => $"{Prefix}{model}.{what}";
    public static readonly string[] Whats = ["PerDay", "PerMinute", "TokensPerMinute"];

    /// <summary>What Google AI Studio showed for this key's project on 18 November 2026 (free tier). Changed in AI setup; 0 there removes a limit.</summary>
    public static readonly IReadOnlyDictionary<string, (long PerDay, long PerMinute, long TokensPerMinute)> Presets = new Dictionary<string, (long, long, long)>
    {
        ["gemini-3.8-flash"] = (20, 5, 250_000),
        ["gemini-3.6-flash"] = (20, 5, 250_000),
        ["gemini-3.5-flash-lite"] = (500, 15, 250_000),
    };

    private static (DateTime Start, DateTime End) Day(DateTime now) => AiQuotaService.DayOf("Gemini", now);

    public async Task<IReadOnlyList<ModelQuota>> GetManyAsync(IEnumerable<string> models, CancellationToken ct = default)
    {
        var names = models.Select(m => m.Replace("models/", "")).Distinct().ToList();
        var now = clock.UtcNow;
        var (start, end) = Day(now);
        var all = await settings.GetAllAsync(ct);
        var rows = await db.AiAttempts.AsNoTracking().Where(a => a.Provider == "Gemini" && a.At >= start && names.Contains(a.Model))
            .Select(a => new { a.Model, a.At, a.Outcome, a.InputTokens, a.OutputTokens }).ToListAsync(ct);
        var minuteAgo = now.AddSeconds(-60);
        return names.Select(m =>
        {
            var mine = rows.Where(r => r.Model == m).ToList();
            var recent = mine.Where(r => r.At > minuteAgo).ToList();
            return new ModelQuota(m, mine.Count(r => r.Outcome != "daily_limit"), recent.Count, recent.Sum(r => (long)r.InputTokens + r.OutputTokens),
                LimitsOf(m, all), end, recent.Count == 0 ? null : recent.Min(r => r.At), mine.Any(r => r.Outcome == "daily_limit"), now);
        }).ToList();
    }

    private static ModelLimits LimitsOf(string model, IReadOnlyDictionary<string, string> all)
    {
        Presets.TryGetValue(model, out var preset);
        var hasPreset = Presets.ContainsKey(model);
        // a saved value wins: a number is the limit, 0 means none; nothing saved means the preset (if the model has one)
        long? Pick(string what, long presetValue) =>
            all.TryGetValue(KeyFor(model, what), out var v) && long.TryParse(v, out var n) ? (n > 0 ? n : null) : (hasPreset ? presetValue : null);
        var saved = Whats.Any(w => all.ContainsKey(KeyFor(model, w)));
        return new ModelLimits(Pick("PerDay", preset.PerDay), Pick("PerMinute", preset.PerMinute), Pick("TokensPerMinute", preset.TokensPerMinute), hasPreset && !saved);
    }

    public async Task<IReadOnlyList<ModelQuota>> GetKnownAsync(CancellationToken ct = default)
    {
        var (start, _) = Day(clock.UtcNow);
        var seen = await db.AiAttempts.AsNoTracking().Where(a => a.Provider == "Gemini" && a.At >= start).Select(a => a.Model).Distinct().ToListAsync(ct);
        var all = await settings.GetAllAsync(ct);
        var configured = all.Keys.Where(IsModelLimitKey).Select(k => k[Prefix.Length..k.LastIndexOf('.')]);
        var models = Presets.Keys.Concat(seen).Concat(configured).Concat(GeminiModelCache.Models).Distinct().OrderBy(m => m).ToList();
        return await GetManyAsync(models, ct);
    }

    public async Task<AiCapacity> GetCapacityAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var (_, end) = Day(now);
        var named = (await settings.GetProviderAsync(ct)).GeminiModel;
        var models = string.IsNullOrWhiteSpace(named) ? GeminiModelCache.Models.Concat(Presets.Keys.Where(_ => GeminiModelCache.Models.Count == 0)).Distinct().ToList() : [named.Replace("models/", "")];
        if (models.Count == 0) return new AiCapacity(false, null, end, null, now);
        var quotas = await GetManyAsync(models, ct);
        var reached = quotas.All(q => q.DayReached);
        var unlimited = quotas.Any(q => !q.DayReached && q.LeftToday is null);
        long? left = unlimited ? null : quotas.Sum(q => q.LeftToday ?? 0);
        var message = reached
            ? "The free Gemini AI has used all its requests for today (" + string.Join("; ", quotas.Select(q => q.Describe())) + "). It works again when the day renews; nothing is lost."
            : null;
        return new AiCapacity(reached, left, end, message, now);
    }

    public async Task RecordAsync(string provider, string model, string outcome, int inputTokens, int outputTokens, CancellationToken ct = default)
    {
        try
        {
            // its own scope and its own save: the caller may be in the middle of something else
            using var scope = scopes.CreateScope();
            var own = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            own.AiAttempts.Add(new AiAttempt { At = clock.UtcNow, Provider = provider, Model = model.Replace("models/", ""), Outcome = outcome, InputTokens = Math.Max(0, inputTokens), OutputTokens = Math.Max(0, outputTokens) });
            await own.SaveChangesAsync(CancellationToken.None);
            if (Random.Shared.Next(60) == 0)
            {
                var old = clock.UtcNow.AddDays(-7);
                own.AiAttempts.RemoveRange(await own.AiAttempts.Where(a => a.At < old).Take(500).ToListAsync(CancellationToken.None));
                await own.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception) { /* counting must never break an AI call */ }
    }
}
/// <summary>Quotas as the screens get them.</summary>
public static class ModelQuotaMapping
{
    public static AiModelQuotaDto ToDto(this ModelQuota q) => new(q.Model, ModelQuota.Pretty(q.Model), q.UsedToday, q.Limits.PerDay, q.UsedLastMinute, q.Limits.PerMinute, q.TokensLastMinute, q.Limits.TokensPerMinute, q.DayReached, q.ResetsAtUtc, q.Limits.IsPreset);

    public static AiCapacityDto ToDto(this AiCapacity c) => new(c.Reached, c.RequestsLeft, c.ResetsAtUtc, c.Message);
}
