using System.Text.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Application.Services;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Analysis;

/// <summary>Validates a request, checks the AI budget, creates the AnalysisRun and hands it to the job scheduler.</summary>
public sealed class AnalysisQueueService(
    IAppDbContext db, IProjectAccess access, ICurrentUser user, IClock clock,
    ISettingsService settings, AnalysisInputBuilder inputs, IJobScheduler jobs, IAiQuota quota, IModelQuota modelQuota)
{
    /// <param name="force">Analyse again even though nothing was added or answered since the current version was made.</param>
    public async Task<AnalysisRunDto> StartPartAsync(Guid flowId, string mode, IReadOnlyCollection<Guid>? entryIds, CancellationToken ct, bool force = false)
    {
        var flow = await db.Flows.AsNoTracking().Include(f => f.ProjectDepartment)
            .FirstOrDefaultAsync(f => f.Id == flowId, ct) ?? throw new NotFoundException("Flow", flowId);
        var pd = flow.ProjectDepartment!;
        await access.EnsureCanCaptureAsync(pd.ProjectId, pd.Id, ct);

        if (!Enum.TryParse<AnalysisMode>(mode, true, out var parsed))
            throw new DomainException("Mode must be Initial or Reanalyse.", "invalid_mode");
        if (parsed == AnalysisMode.Initial && flow.CurrentVersionNo > 0)
            throw new DomainException("This flow already has an analysed version. Use Reanalyse so your edits and answers are kept.", "already_analysed");
        if (parsed == AnalysisMode.Reanalyse && flow.CurrentVersionNo == 0)
            throw new DomainException("This flow has not been analysed yet. Use Initial.", "not_analysed");

        await EnsureBudgetAsync(pd.ProjectId, ct);
        await EnsureAiAllowanceAsync(ct);

        // Analysing again when no note was added, changed or removed and no question was answered since the current version was made can only
        // spend free tokens to get the same result. The person is told, and can still choose to do it.
        if (parsed == AnalysisMode.Reanalyse && !force && flow.CurrentVersionNo > 0 && entryIds is not { Count: > 0 })
        {
            var current = await db.FlowVersions.AsNoTracking().Where(v => v.FlowId == flowId && v.VersionNo == flow.CurrentVersionNo && v.Source == VersionSource.Analysis).Select(v => (DateTime?)v.CreatedAt).FirstOrDefaultAsync(ct);     // an edit or rollback by a person is a change the AI should see
            // the questions the analysis itself raised are saved a moment after the version, so the time the run finished counts as well
            var finished = await db.AnalysisRuns.AsNoTracking().Where(r => r.FlowId == flowId && r.ResultVersionNo == flow.CurrentVersionNo && r.FinishedAt != null).Select(r => r.FinishedAt).OrderByDescending(t => t).FirstOrDefaultAsync(ct);
            if (current is { } made
                && (finished is { } done && done > made ? done : made) is var madeAt
                && !await db.CaptureEntries.AsNoTracking().AnyAsync(e => e.FlowId == flowId && e.UpdatedAt > madeAt, ct)
                && !await db.ClarificationItems.AsNoTracking().AnyAsync(c => c.FlowId == flowId && c.UpdatedAt > madeAt, ct))
                throw new DomainException($"Nothing has been added, changed or answered since version {flow.CurrentVersionNo}, so analysing again would use the free AI tokens for the same result. Add notes or answer questions first, or choose Analyse anyway.", "nothing_new");
        }

        // Builds and validates the input now so the user gets an immediate error (e.g. transcripts still pending).
        var input = await inputs.BuildPartAsync(flowId, parsed, entryIds, ct);
        var existing = await db.AnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r =>
            r.FlowId == flowId && r.InputHash == input.InputHash &&
            (r.Status == AnalysisStatus.Queued || r.Status == AnalysisStatus.Running), ct);
        if (existing is not null) return existing.ToDto();

        var cfg = await settings.GetAiAsync(ct);
        var run = new AnalysisRun
        {
            Scope = AnalysisScope.Part, Mode = parsed, ProjectId = pd.ProjectId, FlowId = flowId,
            Status = AnalysisStatus.Queued, InputHash = input.InputHash, Model = cfg.Model,
            RequestedBy = user.RequireUserId(),
            ParametersJson = JsonSerializer.Serialize(new { entryIds = entryIds?.ToArray() ?? [] }),
        };
        db.AnalysisRuns.Add(run);
        await db.SaveChangesAsync(ct);
        jobs.EnqueueAnalysis(run.Id);
        return run.ToDto();
    }

    public async Task<AnalysisRunDto> StartWholeAsync(Guid projectId, IReadOnlyCollection<Guid>? flowIds, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) throw new NotFoundException("Project", projectId);
        await access.EnsureLeadAsync(projectId, ct);
        await EnsureBudgetAsync(projectId, ct);
        await EnsureAiAllowanceAsync(ct);

        var input = await inputs.BuildWholeAsync(projectId, flowIds, ct);
        var existing = await db.AnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r =>
            r.ProjectId == projectId && r.Scope == AnalysisScope.Whole && r.InputHash == input.InputHash &&
            (r.Status == AnalysisStatus.Queued || r.Status == AnalysisStatus.Running), ct);
        if (existing is not null) return existing.ToDto();

        var cfg = await settings.GetAiAsync(ct);
        var run = new AnalysisRun
        {
            Scope = AnalysisScope.Whole, Mode = AnalysisMode.Initial, ProjectId = projectId, FlowId = null,
            Status = AnalysisStatus.Queued, InputHash = input.InputHash, Model = cfg.Model,
            RequestedBy = user.RequireUserId(),
            ParametersJson = JsonSerializer.Serialize(new { flowIds = flowIds?.ToArray() ?? [] }),
        };
        db.AnalysisRuns.Add(run);
        await db.SaveChangesAsync(ct);
        jobs.EnqueueAnalysis(run.Id);
        return run.ToDto();
    }

    /// <summary>Tells the person at once (instead of after a wait in the queue) when the AI that would do the analysis has used its daily free allowance.</summary>
    private async Task EnsureAiAllowanceAsync(CancellationToken ct)
    {
        if ((await settings.GetProviderAsync(ct)).Provider != "Gemini") return;
        var q = await quota.GetAsync("Gemini", ct);
        if (q.Reached) throw new DomainException(q.Message, "ai_quota_reached");
        // every model has used its own requests for the day: say so now, not after a wait
        var capacity = await modelQuota.GetCapacityAsync(ct);
        if (capacity.Reached) throw new DomainException(capacity.Message ?? "The free Gemini AI has used its requests for today.", "ai_quota_reached");
    }

    /// <summary>Refuses new runs once the project's (or the global) monthly token limit is used up.</summary>
    public async Task EnsureBudgetAsync(Guid projectId, CancellationToken ct)
    {
        var cfg = await settings.GetAiAsync(ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        var limit = project.MonthlyAiTokenLimit ?? (cfg.MonthlyTokenLimitGlobal > 0 ? cfg.MonthlyTokenLimitGlobal : (long?)null);
        if (limit is null or <= 0) return;

        var used = await MonthlyTokensAsync(projectId, clock.UtcNow, ct);
        if (used >= limit)
            throw new AiBudgetExceededException(
                $"The monthly AI limit for this project has been reached ({used:N0} of {limit:N0} tokens). Ask an administrator to raise it.");
    }

    public async Task<long> MonthlyTokensAsync(Guid projectId, DateTime now, CancellationToken ct)
    {
        var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = await db.AnalysisRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.CreatedAt >= start)
            .Select(r => new { r.InputTokens, r.OutputTokens }).ToListAsync(ct);
        return rows.Sum(r => (long)r.InputTokens + r.OutputTokens);
    }
}
