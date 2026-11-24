using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Application.Services;

/// <summary>
/// Writes one row per AI call, so the AI usage page can show how many tokens were used, by what, by whom and for which project.
/// Never lets a failure to write the row break the work that was already done.
/// </summary>
public sealed class AiUsageRecorder(IAppDbContext db, IClock clock, ISettingsService settings, ILogger<AiUsageRecorder> log)
{
    public async Task RecordAsync(AiUsageKind kind, Guid? userId, Guid? projectId, Guid? flowId, string? provider, string? model,
        int inputTokens, int outputTokens, int durationMs, bool success = true, CancellationToken ct = default)
    {
        try
        {
            provider ??= (await settings.GetProviderAsync(ct)).Provider;
            db.AiUsages.Add(new AiUsage
            {
                At = clock.UtcNow, UserId = userId, ProjectId = projectId, FlowId = flowId, Kind = kind,
                Provider = Clip(provider, 40), Model = Clip(model ?? "", 80),
                InputTokens = Math.Max(0, inputTokens), OutputTokens = Math.Max(0, outputTokens), DurationMs = Math.Max(0, durationMs), Success = success,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not record the AI usage of a {Kind} call", kind);
        }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
