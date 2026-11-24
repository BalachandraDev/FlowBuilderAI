namespace FlowCapture.Application;

/// <summary>Defaults for AI calls. Admin-editable overrides live in SystemSetting (Ai.Model, Ai.MaxTokens ...).</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>Gemini (free tier, the default) | Anthropic | Demo. An administrator can change it in the app.</summary>
    public string Provider { get; set; } = "Gemini";
    /// <summary>Empty = pick the best current Gemini Flash model the key can use. An administrator can choose one in the app.</summary>
    public string GeminiModel { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-5-5";
    public int MaxTokens { get; set; } = 16000;
    /// <summary>low | medium | high | max. Empty = model default.</summary>
    public string Effort { get; set; } = "medium";
    /// <summary>When true, sends thinking = between_tools (no up-front reasoning) for lower latency.</summary>
    public bool DisableThinking { get; set; }
    public long MonthlyTokenLimitGlobal { get; set; }            // 0 = unlimited
    public decimal PricePerMTokInput { get; set; } = 2.00m;      // USD per million tokens, Sonnet 5.5 list price
    public decimal PricePerMTokOutput { get; set; } = 10.00m;
    public decimal PricePerMTokCacheRead { get; set; } = 0.10m;
    public decimal CacheWriteMultiplier { get; set; } = 1.25m;
}

/// <summary>Google AI Studio (Gemini API). The key normally comes from the admin dashboard; this is the fallback for a fixed deployment.</summary>
public sealed class GeminiOptions
{
    public const string Section = "Gemini";
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class CaptureOptions
{
    public const string Section = "Capture";
    public int AudioMaxMinutes { get; set; } = 30;
    public int MaxChunkBytes { get; set; } = 8 * 1024 * 1024;
    public long MaxAudioBytes { get; set; } = 300L * 1024 * 1024;
    public string DefaultLanguage { get; set; } = "en-IN";
}

public sealed class SecurityOptions
{
    public const string Section = "Security";
    public string JwtIssuer { get; set; } = "flowcapture";
    public string JwtAudience { get; set; } = "flowcapture-clients";
    public string JwtSigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}
