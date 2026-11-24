using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Services;

/// <summary>What one AI service has used in its current free-allowance day, against the limit the administrator set.</summary>
public sealed record AiQuota(string Provider, long UsedRequests, long UsedTokens, long? LimitRequests, long? LimitTokens, DateTime ResetsAtUtc, DateTime NowUtc)
{
    public bool RequestsReached => LimitRequests is > 0 && UsedRequests >= LimitRequests;
    public bool TokensReached => LimitTokens is > 0 && UsedTokens >= LimitTokens;
    public bool Reached => RequestsReached || TokensReached;

    /// <summary>Plain words for the person who is told no, with when it renews.</summary>
    public string Message
    {
        get
        {
            string N(long? n) => (n ?? 0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            var what = RequestsReached ? $"{N(UsedRequests)} of {N(LimitRequests)} requests" : $"{N(UsedTokens)} of {N(LimitTokens)} tokens";
            var wait = ResetsAtUtc - NowUtc;
            var when = wait.TotalMinutes < 1 ? "in under a minute" : wait.TotalHours < 1 ? $"in {(int)Math.Ceiling(wait.TotalMinutes)} min" : $"in {(int)wait.TotalHours} h {wait.Minutes} min";
            return $"The daily free limit for {Provider} is reached ({what} today). Nothing more is sent to it until it renews {when}. An administrator can change the limit on the AI setup page.";
        }
    }
}

public interface IAiQuota
{
    Task<AiQuota> GetAsync(string provider, CancellationToken ct = default);
    Task<IReadOnlyList<AiQuota>> GetAllAsync(CancellationToken ct = default);
    /// <summary>Does nothing while the AI is under its limits; throws <c>ai_quota_reached</c> (not retryable) when it is not.</summary>
    Task EnsureAvailableAsync(string provider, CancellationToken ct = default);
}

/// <summary>
/// Free AI accounts have daily allowances. The administrator tells the app the limits of each account (requests and tokens a day); the app counts what each
/// AI has really used since its day began (from the usage records) and stops sending to an AI that is at its limit, so a free allowance is never
/// overrun and a paid plan is never started by accident. A limit of 0 or none means the app does not limit that AI (the provider still does).
/// Gemini's day ends at midnight Pacific time and Groq's at midnight UTC.
/// </summary>
public sealed class AiQuotaService(IAppDbContext db, ISettingsService settings, IClock clock) : IAiQuota
{
    public static readonly string[] Providers = ["Gemini", "Groq"];
    public static string SettingKey(string provider, string what) => $"Ai.Limit.{provider}.{what}";
    public static readonly string[] AllLimitKeys = Providers.SelectMany(p => new[] { SettingKey(p, "Requests"), SettingKey(p, "Tokens") }).ToArray();

    /// <summary>Where this AI's day starts and ends, in UTC.</summary>
    public static (DateTime Start, DateTime End) DayOf(string provider, DateTime utcNow)
    {
        if (!provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            var d = utcNow.Date;
            return (d, d.AddDays(1));
        }
        TimeZoneInfo pacific;
        try { pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
        catch (TimeZoneNotFoundException) { pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, pacific);
        var startLocal = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        var endLocal = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
        return (TimeZoneInfo.ConvertTimeToUtc(startLocal, pacific), TimeZoneInfo.ConvertTimeToUtc(endLocal, pacific));
    }

    public async Task<AiQuota> GetAsync(string provider, CancellationToken ct = default)
    {
        var name = Providers.FirstOrDefault(p => p.Equals(provider, StringComparison.OrdinalIgnoreCase)) ?? provider;
        var now = clock.UtcNow;
        var (start, end) = DayOf(name, now);
        var rows = db.AiUsages.AsNoTracking().Where(x => x.Provider == name && x.At >= start && x.At < end);
        var requests = await rows.CountAsync(ct);
        var tokens = requests == 0 ? 0 : await rows.SumAsync(x => (long)x.InputTokens + x.OutputTokens, ct);

        var all = await settings.GetAllAsync(ct);
        long? Limit(string what) => all.TryGetValue(SettingKey(name, what), out var v) && long.TryParse(v, out var n) && n > 0 ? n : null;
        return new AiQuota(name, requests, tokens, Limit("Requests"), Limit("Tokens"), end, now);
    }

    public async Task<IReadOnlyList<AiQuota>> GetAllAsync(CancellationToken ct = default)
    {
        var list = new List<AiQuota>();
        foreach (var p in Providers) list.Add(await GetAsync(p, ct));
        return list;
    }

    public async Task EnsureAvailableAsync(string provider, CancellationToken ct = default)
    {
        var q = await GetAsync(provider, ct);
        if (q.Reached) throw new AiServiceException("ai_quota_reached", q.Message, retryable: false);
    }
}