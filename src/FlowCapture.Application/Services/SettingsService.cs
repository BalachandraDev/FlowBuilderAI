using FlowCapture.Domain;
using FlowCapture.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlowCapture.Application.Services;

public sealed record AiSettings(
    string Model, int MaxTokens, string? Effort, bool DisableThinking, long MonthlyTokenLimitGlobal,
    decimal PricePerMTokInput, decimal PricePerMTokOutput, decimal PricePerMTokCacheRead, decimal CacheWriteMultiplier);

/// <summary>Which AI service is used and how to reach it. The key is null when none has been set.</summary>
public sealed record AiProviderSettings(string Provider, string? GeminiApiKey, string GeminiModel, bool KeyFromDashboard);

/// <summary>The free Groq account, used for voice-to-text (Whisper). The key is null when none has been saved.</summary>
public sealed record GroqSettings(string? ApiKey, string SttModel, bool UseForVoice, bool KeyFromDashboard);

public interface ISettingsService
{
    Task<GroqSettings> GetGroqAsync(CancellationToken ct = default);
    Task<AiSettings> GetAiAsync(CancellationToken ct = default);
    Task<AiProviderSettings> GetProviderAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetNameDictionaryAsync(CancellationToken ct = default);
    Task<int> GetAudioMaxMinutesAsync(CancellationToken ct = default);
    /// <summary>A yes/no setting, with the value to use when the administrator has not set it.</summary>
    Task<bool> GetFlagAsync(string key, bool fallback, CancellationToken ct = default);
    /// <summary>Every editable setting. Secrets are never included.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    /// <summary>Saves (encrypted) or, when the value is empty, removes a secret such as the AI key.</summary>
    Task SetSecretAsync(string key, string? plain, CancellationToken ct = default);
}

/// <summary>Effective settings = configuration defaults overridden by rows in SystemSetting (admin editable).</summary>
public sealed class SettingsService(
    IAppDbContext db, IOptions<AiOptions> ai, IOptions<GeminiOptions> gemini, IOptions<CaptureOptions> capture,
    ISecretProtector secrets, IClock clock, ICurrentUser user) : ISettingsService
{
    public static readonly string[] AllowedKeys =
    [
        "Ai.Provider", "Ai.GeminiModel", "Ai.VoiceProvider", "Ai.GroqSttModel",
        "Ai.Limit.Gemini.Requests", "Ai.Limit.Gemini.Tokens", "Ai.Limit.Groq.Requests", "Ai.Limit.Groq.Tokens",
        "Ai.Model", "Ai.MaxTokens", "Ai.Effort", "Ai.DisableThinking", "Ai.MonthlyTokenLimitGlobal",
        "Ai.PricePerMTokInput", "Ai.PricePerMTokOutput", "Ai.PricePerMTokCacheRead",
        "Audio.MaxMinutes", "Pii.NameDictionary",
        FlagAiWording, FlagAllowMedia,
    ];

    /// <summary>When on, the AI rewrites the document's wording (slower, uses tokens). Off: the document is built from the analysed workflow in seconds.</summary>
    public const string FlagAiWording = "Documents.AiWording";
    /// <summary>When off, photos and recordings are not sent to the AI service (they may hold patient details that text masking cannot hide).</summary>
    public const string FlagAllowMedia = "Ai.AllowMedia";

    public static readonly string[] Providers = ["Gemini", "Anthropic", "Demo"];
    public const string GeminiKeySetting = "Secret.GeminiApiKey";
    public const string GroqKeySetting = "Secret.GroqApiKey";
    /// <summary>Who turns voice notes into text: Groq (its own free allowance, fast) or Gemini. Empty means Groq when a Groq key is saved.</summary>
    public static readonly string[] VoiceProviders = ["Groq", "Gemini"];
    public const string DefaultGroqSttModel = "whisper-large-v3-turbo";
    private const string SecretPrefix = "Secret.";

    public async Task<AiSettings> GetAiAsync(CancellationToken ct = default)
    {
        var o = ai.Value;
        var s = await GetAllAsync(ct);
        string? Get(string k) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        return new AiSettings(
            Get("Ai.Model") ?? o.Model,
            int.TryParse(Get("Ai.MaxTokens"), out var mt) ? mt : o.MaxTokens,
            Get("Ai.Effort") ?? (string.IsNullOrWhiteSpace(o.Effort) ? null : o.Effort),
            bool.TryParse(Get("Ai.DisableThinking"), out var dt) ? dt : o.DisableThinking,
            long.TryParse(Get("Ai.MonthlyTokenLimitGlobal"), out var lim) ? lim : o.MonthlyTokenLimitGlobal,
            decimal.TryParse(Get("Ai.PricePerMTokInput"), out var pi) ? pi : o.PricePerMTokInput,
            decimal.TryParse(Get("Ai.PricePerMTokOutput"), out var po) ? po : o.PricePerMTokOutput,
            decimal.TryParse(Get("Ai.PricePerMTokCacheRead"), out var pc) ? pc : o.PricePerMTokCacheRead,
            o.CacheWriteMultiplier);
    }

    public async Task<AiProviderSettings> GetProviderAsync(CancellationToken ct = default)
    {
        var rows = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == "Ai.Provider" || x.Key == "Ai.GeminiModel" || x.Key == GeminiKeySetting).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        string? Row(string k) => rows.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        var provider = Providers.FirstOrDefault(p => p.Equals(Row("Ai.Provider") ?? ai.Value.Provider, StringComparison.OrdinalIgnoreCase)) ?? "Gemini";
        var stored = Row(GeminiKeySetting) is { } enc ? secrets.Unprotect(enc) : null;
        var key = !string.IsNullOrWhiteSpace(stored) ? stored : (string.IsNullOrWhiteSpace(gemini.Value.ApiKey) ? null : gemini.Value.ApiKey);
        return new AiProviderSettings(provider, key, Row("Ai.GeminiModel") ?? ai.Value.GeminiModel ?? "", !string.IsNullOrWhiteSpace(stored));
    }

    public async Task<GroqSettings> GetGroqAsync(CancellationToken ct = default)
    {
        var rows = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == GroqKeySetting || x.Key == "Ai.VoiceProvider" || x.Key == "Ai.GroqSttModel").ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        string? Row(string k) => rows.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        var key = Row(GroqKeySetting) is { } enc ? secrets.Unprotect(enc) : null;
        var has = !string.IsNullOrWhiteSpace(key);
        var voice = VoiceProviders.FirstOrDefault(p => p.Equals(Row("Ai.VoiceProvider"), StringComparison.OrdinalIgnoreCase));
        return new GroqSettings(has ? key : null, Row("Ai.GroqSttModel") ?? DefaultGroqSttModel, has && voice != "Gemini", has);
    }

    public async Task<IReadOnlyList<string>> GetNameDictionaryAsync(CancellationToken ct = default)
    {
        var s = await GetAllAsync(ct);
        return s.TryGetValue("Pii.NameDictionary", out var v)
            ? v.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    public async Task<int> GetAudioMaxMinutesAsync(CancellationToken ct = default)
    {
        var s = await GetAllAsync(ct);
        return s.TryGetValue("Audio.MaxMinutes", out var v) && int.TryParse(v, out var m) ? m : capture.Value.AudioMaxMinutes;
    }

    public async Task<bool> GetFlagAsync(string key, bool fallback, CancellationToken ct = default)
    {
        var value = await db.SystemSettings.AsNoTracking().Where(x => x.Key == key).Select(x => x.Value).FirstOrDefaultAsync(ct);
        return bool.TryParse(value, out var b) ? b : fallback;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
        await db.SystemSettings.AsNoTracking().Where(x => !x.Key.StartsWith(SecretPrefix)).ToDictionaryAsync(x => x.Key, x => x.Value, ct);

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        if (!AllowedKeys.Contains(key) && !ModelQuotaService.IsModelLimitKey(key)) throw new DomainException($"Unknown setting '{key}'.", "unknown_setting");
        if (key == "Ai.Provider" && !Providers.Contains(value, StringComparer.OrdinalIgnoreCase))
            throw new DomainException($"The AI provider must be one of: {string.Join(", ", Providers)}.", "invalid_provider");
        if (key == "Ai.VoiceProvider" && !string.IsNullOrEmpty(value) && !VoiceProviders.Contains(value, StringComparer.OrdinalIgnoreCase))
            throw new DomainException($"Voice-to-text must be done by one of: {string.Join(", ", VoiceProviders)}.", "invalid_provider");
        await UpsertAsync(key, key == "Ai.Provider" ? Providers.First(p => p.Equals(value, StringComparison.OrdinalIgnoreCase))
            : key == "Ai.VoiceProvider" && value.Length > 0 ? VoiceProviders.First(p => p.Equals(value, StringComparison.OrdinalIgnoreCase)) : value, ct);
    }

    public async Task SetSecretAsync(string key, string? plain, CancellationToken ct = default)
    {
        if (key != GeminiKeySetting && key != GroqKeySetting) throw new DomainException($"Unknown secret '{key}'.", "unknown_setting");
        await UpsertAsync(key, string.IsNullOrWhiteSpace(plain) ? "" : secrets.Protect(plain.Trim()), ct);
    }

    private async Task UpsertAsync(string key, string value, CancellationToken ct)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row is null) db.SystemSettings.Add(row = new SystemSetting { Key = key });
        row.Value = value;
        row.UpdatedAt = clock.UtcNow;
        row.UpdatedBy = user.UserId;
        await db.SaveChangesAsync(ct);
    }
}
