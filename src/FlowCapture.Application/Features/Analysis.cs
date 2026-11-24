using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Analysis;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.AnalysisFeatures;

public sealed record StartPartAnalysisCommand(Guid FlowId, StartAnalysisRequest Body) : IRequest<AnalysisRunDto>;
public sealed record StartDepartmentAnalysisCommand(Guid ProjectId, Guid ProjectDepartmentId) : IRequest<DepartmentAnalysisResultDto>;
public sealed record StartWholeAnalysisCommand(Guid ProjectId, Guid[]? FlowIds) : IRequest<AnalysisRunDto>;
public sealed record GetRunQuery(Guid RunId) : IRequest<AnalysisRunDto>;
public sealed record ListRunsQuery(Guid FlowId) : IRequest<IReadOnlyList<AnalysisRunDto>>;
public sealed record CancelRunCommand(Guid RunId) : IRequest<AnalysisRunDto>;
public sealed record ListLinksQuery(Guid ProjectId, string? Status) : IRequest<IReadOnlyList<CrossFlowLinkDto>>;
public sealed record DecideLinkCommand(Guid LinkId, string Status) : IRequest<CrossFlowLinkDto>;

public sealed class StartPartAnalysisValidator : AbstractValidator<StartPartAnalysisCommand>
{
    public StartPartAnalysisValidator() =>
        RuleFor(x => x.Body.Mode).NotEmpty().Must(m => Enum.TryParse<AnalysisMode>(m, true, out _)).WithMessage("Mode must be Initial or Reanalyse.");
}

public sealed class DecideLinkValidator : AbstractValidator<DecideLinkCommand>
{
    public DecideLinkValidator() =>
        RuleFor(x => x.Status).Must(s => s.Equals("Accepted", StringComparison.OrdinalIgnoreCase) || s.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Status must be Accepted or Rejected.");
}

public sealed class AnalysisHandlers(
    IAppDbContext db, IProjectAccess access, ICurrentUser user, IClock clock, AnalysisQueueService queue) :
    IRequestHandler<StartPartAnalysisCommand, AnalysisRunDto>,
    IRequestHandler<StartDepartmentAnalysisCommand, DepartmentAnalysisResultDto>,
    IRequestHandler<StartWholeAnalysisCommand, AnalysisRunDto>,
    IRequestHandler<GetRunQuery, AnalysisRunDto>,
    IRequestHandler<ListRunsQuery, IReadOnlyList<AnalysisRunDto>>,
    IRequestHandler<CancelRunCommand, AnalysisRunDto>,
    IRequestHandler<ListLinksQuery, IReadOnlyList<CrossFlowLinkDto>>,
    IRequestHandler<DecideLinkCommand, CrossFlowLinkDto>
{
    public Task<AnalysisRunDto> Handle(StartPartAnalysisCommand r, CancellationToken ct) =>
        queue.StartPartAsync(r.FlowId, r.Body.Mode, r.Body.EntryIds, ct, r.Body.Force);

    public async Task<DepartmentAnalysisResultDto> Handle(StartDepartmentAnalysisCommand r, CancellationToken ct)
    {
        var pd = await db.ProjectDepartments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.ProjectDepartmentId && x.ProjectId == r.ProjectId, ct)
                 ?? throw new NotFoundException("Project department", r.ProjectDepartmentId);
        await access.EnsureCanCaptureAsync(r.ProjectId, pd.Id, ct);

        var flows = await db.Flows.AsNoTracking().Where(f => f.ProjectDepartmentId == pd.Id)
            .Select(f => new { f.Id, f.MenuName, f.CurrentVersionNo }).OrderBy(f => f.MenuName).ToListAsync(ct);
        var runs = new List<AnalysisRunDto>();
        var skipped = new List<SkippedFlowDto>();
        foreach (var f in flows)
        {
            try
            {
                runs.Add(await queue.StartPartAsync(f.Id, f.CurrentVersionNo == 0 ? "Initial" : "Reanalyse", null, ct));
            }
            catch (AiBudgetExceededException) { throw; }
            catch (DomainException e)
            {
                skipped.Add(new SkippedFlowDto(f.Id, f.MenuName, e.Message));
            }
        }
        return new DepartmentAnalysisResultDto(runs, skipped);
    }

    public Task<AnalysisRunDto> Handle(StartWholeAnalysisCommand r, CancellationToken ct) => queue.StartWholeAsync(r.ProjectId, r.FlowIds, ct);

    public async Task<AnalysisRunDto> Handle(GetRunQuery r, CancellationToken ct)
    {
        var run = await db.AnalysisRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.RunId, ct) ?? throw new NotFoundException("Analysis run", r.RunId);
        await access.EnsureMemberAsync(run.ProjectId, ct);
        return run.ToDto();
    }

    public async Task<IReadOnlyList<AnalysisRunDto>> Handle(ListRunsQuery r, CancellationToken ct)
    {
        var projectId = await db.Flows.AsNoTracking().Where(f => f.Id == r.FlowId).Select(f => (Guid?)f.ProjectDepartment!.ProjectId).FirstOrDefaultAsync(ct)
                        ?? throw new NotFoundException("Flow", r.FlowId);
        await access.EnsureMemberAsync(projectId, ct);
        var rows = await db.AnalysisRuns.AsNoTracking().Where(x => x.FlowId == r.FlowId).OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync(ct);
        return rows.Select(x => x.ToDto()).ToList();
    }

    public async Task<AnalysisRunDto> Handle(CancelRunCommand r, CancellationToken ct)
    {
        var run = await db.AnalysisRuns.FirstOrDefaultAsync(x => x.Id == r.RunId, ct) ?? throw new NotFoundException("Analysis run", r.RunId);
        if (run.RequestedBy != user.UserId) await access.EnsureLeadAsync(run.ProjectId, ct);
        if (run.Status != AnalysisStatus.Queued)
            throw new DomainException("Only a run that has not started yet can be cancelled.", "not_cancellable");
        run.Status = AnalysisStatus.Cancelled;
        run.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return run.ToDto();
    }

    public async Task<IReadOnlyList<CrossFlowLinkDto>> Handle(ListLinksQuery r, CancellationToken ct)
    {
        await access.EnsureMemberAsync(r.ProjectId, ct);
        var q = db.CrossFlowLinks.AsNoTracking().Where(l => l.ProjectId == r.ProjectId);
        if (!string.IsNullOrWhiteSpace(r.Status))
        {
            if (!Enum.TryParse<LinkStatus>(r.Status, true, out var s)) throw new DomainException("Unknown status.", "invalid_status");
            q = q.Where(l => l.Status == s);
        }
        var rows = await q.OrderBy(l => l.CreatedAt).ToListAsync(ct);
        return rows.Select(l => l.ToDto()).ToList();
    }

    public async Task<CrossFlowLinkDto> Handle(DecideLinkCommand r, CancellationToken ct)
    {
        var link = await db.CrossFlowLinks.FirstOrDefaultAsync(l => l.Id == r.LinkId, ct) ?? throw new NotFoundException("Link", r.LinkId);
        await access.EnsureLeadAsync(link.ProjectId, ct);
        link.Status = Enum.Parse<LinkStatus>(r.Status, true);
        link.DecidedBy = user.RequireUserId();
        link.DecidedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return link.ToDto();
    }
}
