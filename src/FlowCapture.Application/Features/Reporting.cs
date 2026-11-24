using System.Globalization;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Analysis;
using FlowCapture.Application.Common;
using FlowCapture.Application.Services;
using FlowCapture.Domain;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.ReportingFeatures;

public sealed record GetDashboardQuery(Guid ProjectId) : IRequest<DashboardDto>;
public sealed record GetPendingQuery(Guid ProjectId) : IRequest<IReadOnlyList<PendingItemDto>>;
public sealed record GetUsageQuery(Guid? ProjectId, string? Month) : IRequest<AiUsageDto>;
public sealed record GetAuditQuery(string? EntityType, string? EntityId, Guid? ProjectId, DateTime? From, DateTime? To, Guid? UserId, int Page, int PageSize)
    : IRequest<PagedResult<AuditLogDto>>;
public sealed record GetJobQuery(Guid JobId) : IRequest<JobDto>;

public sealed class GetUsageValidator : AbstractValidator<GetUsageQuery>
{
    public GetUsageValidator() =>
        RuleFor(x => x.Month).Must(m => m is null || DateTime.TryParseExact(m, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .WithMessage("Month must look like 2026-11.");
}

public sealed class ReportingHandlers(
    IAppDbContext db, IProjectAccess access, ICurrentUser user, IClock clock, ISettingsService settings) :
    IRequestHandler<GetDashboardQuery, DashboardDto>,
    IRequestHandler<GetPendingQuery, IReadOnlyList<PendingItemDto>>,
    IRequestHandler<GetUsageQuery, AiUsageDto>,
    IRequestHandler<GetAuditQuery, PagedResult<AuditLogDto>>,
    IRequestHandler<GetJobQuery, JobDto>
{
    private sealed record FlowRow(Guid Id, Guid ProjectDepartmentId, string MenuName, FlowStatus Status, int CurrentVersionNo,
        DateTime LastActivityAt, int Entries, int OpenQuestions, int BlockingQuestions);

    private async Task<List<FlowRow>> LoadFlowRowsAsync(Guid projectId, CancellationToken ct) =>
        await db.Flows.AsNoTracking().Where(f => f.ProjectDepartment!.ProjectId == projectId)
            .Select(f => new FlowRow(f.Id, f.ProjectDepartmentId, f.MenuName, f.Status, f.CurrentVersionNo, f.LastActivityAt,
                f.Entries.Count(e => !e.IsDeleted),
                f.Clarifications.Count(c => c.Status == ClarificationStatus.Open),
                f.Clarifications.Count(c => c.Status == ClarificationStatus.Open && c.IsBlocking))).ToListAsync(ct);

    private static bool Analysed(FlowRow f) => f.Status != FlowStatus.Draft;
    private static bool Confirmed(FlowRow f) => f.Status is FlowStatus.Confirmed or FlowStatus.DocumentsGenerated;

    public async Task<DashboardDto> Handle(GetDashboardQuery r, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == r.ProjectId, ct)) throw new NotFoundException("Project", r.ProjectId);
        await access.EnsureMemberAsync(r.ProjectId, ct);

        var flows = await LoadFlowRowsAsync(r.ProjectId, ct);
        var pds = await db.ProjectDepartments.AsNoTracking().Where(p => p.ProjectId == r.ProjectId && p.IsActive)
            .Select(p => new { p.Id, p.ModuleCode, Name = p.CustomName ?? p.Department!.Name }).OrderBy(p => p.ModuleCode).ToListAsync(ct);

        var departments = pds.Select(pd =>
        {
            var fl = flows.Where(f => f.ProjectDepartmentId == pd.Id).ToList();
            return new DepartmentProgressDto(pd.Id, pd.ModuleCode, pd.Name, fl.Sum(f => f.Entries), fl.Count,
                fl.Count(Analysed), fl.Count(Confirmed), fl.Count(f => f.Status == FlowStatus.DocumentsGenerated), fl.Sum(f => f.OpenQuestions));
        }).ToList();

        var usage = await UsageAsync(r.ProjectId, clock.UtcNow, ct);
        return new DashboardDto(r.ProjectId, pds.Count, flows.Count,
            flows.Count(f => f.Entries > 0), flows.Count(Analysed), flows.Count(Confirmed), flows.Count(f => f.Status == FlowStatus.DocumentsGenerated),
            flows.Sum(f => f.OpenQuestions), flows.Sum(f => f.BlockingQuestions),
            flows.Count == 0 ? null : flows.Max(f => f.LastActivityAt), departments, usage);
    }

    public async Task<IReadOnlyList<PendingItemDto>> Handle(GetPendingQuery r, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == r.ProjectId, ct)) throw new NotFoundException("Project", r.ProjectId);
        await access.EnsureLeadAsync(r.ProjectId, ct);

        var flows = await LoadFlowRowsAsync(r.ProjectId, ct);
        var pdNames = await db.ProjectDepartments.AsNoTracking().Where(p => p.ProjectId == r.ProjectId)
            .ToDictionaryAsync(p => p.Id, p => p.ModuleCode, ct);
        var latest = (await db.FlowVersions.AsNoTracking().Where(v => v.Flow!.ProjectDepartment!.ProjectId == r.ProjectId)
            .Select(v => new { v.FlowId, v.CreatedAt }).ToListAsync(ct))
            .GroupBy(v => v.FlowId).ToDictionary(g => g.Key, g => g.Max(x => x.CreatedAt));
        var newer = (await db.CaptureEntries.AsNoTracking().Where(e => !e.IsDeleted && e.Flow!.ProjectDepartment!.ProjectId == r.ProjectId)
            .Select(e => new { e.FlowId, e.UpdatedAt }).ToListAsync(ct))
            .GroupBy(e => e.FlowId).ToDictionary(g => g.Key, g => g.Select(x => x.UpdatedAt).ToList());

        var items = new List<PendingItemDto>();
        foreach (var f in flows)
        {
            var title = $"{pdNames.GetValueOrDefault(f.ProjectDepartmentId, "?")} / {f.MenuName}";
            var entryTimes = newer.GetValueOrDefault(f.Id) ?? [];
            var count = latest.TryGetValue(f.Id, out var last) ? entryTimes.Count(t => t > last) : entryTimes.Count;
            if (count > 0)
                items.Add(new PendingItemDto("UnanalysedEntries", f.Id, title,
                    $"{count} new or changed input(s) have not been analysed yet.", f.LastActivityAt));
            if (f.OpenQuestions > 0)
                items.Add(new PendingItemDto("OpenQuestions", f.Id, title,
                    $"{f.OpenQuestions} open question(s), {f.BlockingQuestions} blocking.", f.LastActivityAt));
            if (f.Status == FlowStatus.UnderReview)
                items.Add(new PendingItemDto("AwaitingConfirm", f.Id, title, "Under review. Waiting for confirmation.", f.LastActivityAt));
            if (f.Status == FlowStatus.Confirmed)
                items.Add(new PendingItemDto("AwaitingDocument", f.Id, title, "Confirmed. Documents have not been generated.", f.LastActivityAt));
        }

        var proposed = await db.CrossFlowLinks.AsNoTracking().Where(l => l.ProjectId == r.ProjectId && l.Status == LinkStatus.Proposed).ToListAsync(ct);
        if (proposed.Count > 0)
            items.Add(new PendingItemDto("LinkToDecide", null, "Cross-department hand-offs", $"{proposed.Count} proposed hand-off(s) need a decision.", proposed.Min(l => l.CreatedAt)));

        var docs = await db.DocumentVersions.AsNoTracking()
            .Where(v => v.ApprovalStatus == ApprovalStatus.Pending && v.FinalizedAt != null && db.GeneratedDocuments.Any(d => d.Id == v.GeneratedDocumentId && d.ProjectId == r.ProjectId))
            .Select(v => v.GeneratedAt).ToListAsync(ct);
        if (docs.Count > 0)
            items.Add(new PendingItemDto("DocumentToApprove", null, "Documents", $"{docs.Count} final document version(s) wait for the administrator to verify.", docs.Min()));

        return items.OrderBy(i => i.Since).ToList();
    }

    public async Task<AiUsageDto> Handle(GetUsageQuery r, CancellationToken ct)
    {
        var month = r.Month is null ? clock.UtcNow : DateTime.ParseExact(r.Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        if (r.ProjectId is { } pid)
        {
            await access.EnsureLeadAsync(pid, ct);
            return await UsageAsync(pid, month, ct);
        }
        if (!user.IsInRole(AppRoles.Admin)) throw new ForbiddenException("Only an administrator can see usage across all projects.");
        return await UsageAsync(null, month, ct);
    }

    private async Task<AiUsageDto> UsageAsync(Guid? projectId, DateTime month, CancellationToken ct)
    {
        var start = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        var q = db.AnalysisRuns.AsNoTracking().Where(x => x.CreatedAt >= start && x.CreatedAt < end);
        if (projectId is { } p) q = q.Where(x => x.ProjectId == p);
        var rows = await q.Select(x => new { x.InputTokens, x.OutputTokens, x.EstimatedCost }).ToListAsync(ct);

        long? limit = null;
        if (projectId is { } pj)
        {
            var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == pj, ct);
            var cfg = await settings.GetAiAsync(ct);
            limit = project.MonthlyAiTokenLimit ?? (cfg.MonthlyTokenLimitGlobal > 0 ? cfg.MonthlyTokenLimitGlobal : null);
        }
        return new AiUsageDto(start.ToString("yyyy-MM", CultureInfo.InvariantCulture), rows.Sum(x => (long)x.InputTokens), rows.Sum(x => (long)x.OutputTokens),
            rows.Sum(x => x.EstimatedCost), limit, rows.Count);
    }

    public async Task<PagedResult<AuditLogDto>> Handle(GetAuditQuery r, CancellationToken ct)
    {
        if (r.ProjectId is { } pid) await access.EnsureLeadAsync(pid, ct);
        else if (!user.IsInRole(AppRoles.Admin)) throw new ForbiddenException("Give a project, or ask an administrator.");

        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (r.ProjectId is { } p) q = q.Where(a => a.ProjectId == p);
        if (!string.IsNullOrWhiteSpace(r.EntityType)) q = q.Where(a => a.EntityType == r.EntityType);
        if (!string.IsNullOrWhiteSpace(r.EntityId)) q = q.Where(a => a.EntityId == r.EntityId);
        if (r.UserId is { } u) q = q.Where(a => a.UserId == u);
        if (r.From is { } from) q = q.Where(a => a.At >= from);
        if (r.To is { } to) q = q.Where(a => a.At <= to);

        var page = Math.Max(1, r.Page);
        var size = Math.Clamp(r.PageSize, 1, 100);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var names = await db.Users.AsNoTracking().Where(x => rows.Select(a => a.UserId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        return new PagedResult<AuditLogDto>(rows.Select(a => new AuditLogDto(a.Id, a.At, a.UserId,
            a.UserId is { } id ? names.GetValueOrDefault(id) : null, a.Action, a.EntityType, a.EntityId, a.ProjectId, a.OldValues, a.NewValues, a.CorrelationId)).ToList(),
            page, size, total);
    }

    public async Task<JobDto> Handle(GetJobQuery r, CancellationToken ct)
    {
        var run = await db.AnalysisRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.JobId, ct);
        if (run is not null)
        {
            if (run.RequestedBy != user.UserId) await access.EnsureLeadAsync(run.ProjectId, ct);
            return new JobDto(run.Id, run.Scope == AnalysisScope.Whole ? "ProjectAnalysis" : "FlowAnalysis", run.Status.ToString(),
                run.Status == AnalysisStatus.Succeeded ? 100 : null, run.ErrorDetail, run.ResultFlowVersionId, run.CreatedAt);
        }

        var entry = await db.CaptureEntries.AsNoTracking().Include(e => e.Transcript).Include(e => e.Flow!).ThenInclude(f => f.ProjectDepartment)
            .FirstOrDefaultAsync(e => e.Id == r.JobId, ct);
        if (entry?.Transcript is { } t)
        {
            await access.EnsureMemberAsync(entry.Flow!.ProjectDepartment!.ProjectId, ct);
            var state = t.Status == TranscriptStatus.Ready ? "Succeeded" : t.Status.ToString();
            return new JobDto(entry.Id, "Transcription", state, t.Status == TranscriptStatus.Ready ? 100 : null, t.Error, entry.Id, t.CreatedAt);
        }
        var docJob = await db.DocumentJobRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.JobId, ct);
        if (docJob is not null)
        {
            if (docJob.RequestedBy != user.UserId) await access.EnsureMemberAsync(docJob.ProjectId, ct);
            return DocumentFeatures.DocumentHandlers.ToDto(docJob);
        }
        throw new NotFoundException("Job", r.JobId);
    }
}
