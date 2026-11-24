using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace FlowCapture.Infrastructure.Maintenance;

public sealed class MaintenanceOptions
{
    public const string Section = "Maintenance";
    /// <summary>Run the clean-up in the background. Turn off when a separate scheduler calls POST /api/v1/admin/maintenance/run.</summary>
    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = 6;
    /// <summary>A queued or running job that has not moved for this long is marked failed (the server restarted, or the job was lost).</summary>
    public int StuckJobMinutes { get; set; } = 30;
    /// <summary>Sign-in tokens are removed this many days after they expired.</summary>
    public int RefreshTokenGraceDays { get; set; } = 7;
    /// <summary>Stored responses for retried requests are removed after this many days.</summary>
    public int IdempotencyDays { get; set; } = 7;
}

public sealed record MaintenanceReport(int StuckAnalyses, int StuckDocuments, int StuckTranscripts, int RefreshTokensRemoved, int IdempotencyRecordsRemoved)
{
    public int Total => StuckAnalyses + StuckDocuments + StuckTranscripts + RefreshTokensRemoved + IdempotencyRecordsRemoved;
}

/// <summary>
/// Housekeeping that keeps the system healthy without anybody watching it: frees jobs that were interrupted (otherwise a lost job
/// would block the user's next one for ever) and removes data that has no use any more. The audit trail is never deleted here:
/// how long to keep it is a policy decision (see docs/06-deployment.md).
/// </summary>
public sealed class MaintenanceService(IAppDbContext db, IClock clock, IOptions<MaintenanceOptions> options, ILogger<MaintenanceService> log)
{
    public const string InterruptedCode = "interrupted";
    private const string InterruptedText = "The job was interrupted (the server restarted or lost it). Start it again.";

    public async Task<MaintenanceReport> RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.UtcNow;
        var stuckBefore = now.AddMinutes(-Math.Max(1, o.StuckJobMinutes));

        var analyses = await db.AnalysisRuns
            .Where(r => (r.Status == AnalysisStatus.Queued || r.Status == AnalysisStatus.Running) && r.UpdatedAt < stuckBefore).ToListAsync(ct);
        foreach (var r in analyses) { r.Status = AnalysisStatus.Failed; r.ErrorCode = InterruptedCode; r.ErrorDetail = InterruptedText; r.FinishedAt = now; }

        var documents = await db.DocumentJobRuns
            .Where(r => (r.Status == DocJobStatus.Queued || r.Status == DocJobStatus.Running) && r.UpdatedAt < stuckBefore).ToListAsync(ct);
        foreach (var r in documents) { r.Status = DocJobStatus.Failed; r.ErrorCode = InterruptedCode; r.ErrorDetail = InterruptedText; r.FinishedAt = now; }

        var transcripts = await db.Transcripts
            .Where(t => (t.Status == TranscriptStatus.Queued || t.Status == TranscriptStatus.Processing) && t.UpdatedAt < stuckBefore).ToListAsync(ct);
        foreach (var t in transcripts) { t.Status = TranscriptStatus.Failed; t.Error = "Transcription was interrupted. Use Transcribe again."; }

        await db.SaveChangesAsync(ct);

        var tokenCutoff = now.AddDays(-Math.Max(1, o.RefreshTokenGraceDays));
        var tokens = await db.RefreshTokens.Where(t => t.ExpiresAt < tokenCutoff).ExecuteDeleteAsync(ct);
        var idempotencyCutoff = now.AddDays(-Math.Max(1, o.IdempotencyDays));
        var keys = await db.IdempotencyRecords.Where(r => r.CreatedAt < idempotencyCutoff).ExecuteDeleteAsync(ct);

        var report = new MaintenanceReport(analyses.Count, documents.Count, transcripts.Count, tokens, keys);
        if (report.Total > 0) log.LogInformation("Maintenance: {@Report}", report);
        return report;
    }
}

/// <summary>Runs the maintenance two minutes after start (to recover from a crash quickly), then every few hours.</summary>
public sealed class MaintenanceHostedService(IServiceScopeFactory scopes, IOptions<MaintenanceOptions> options, ILogger<MaintenanceHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, options.Value.IntervalHours)));
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<MaintenanceService>().RunAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Maintenance failed; it will run again later");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
