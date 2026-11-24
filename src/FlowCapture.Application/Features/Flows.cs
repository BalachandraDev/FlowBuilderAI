using System.Text.Json;
using System.Text.Json.Nodes;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Analysis;
using FlowCapture.Application.Common;
using FlowCapture.Application.Flows;
using FlowCapture.Domain;
using FlowCapture.Shared.Content;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.FlowFeatures;

public sealed record ListFlowsQuery(Guid ProjectId, Guid? DepartmentId, string? Status) : IRequest<IReadOnlyList<FlowDto>>;
public sealed record CreateFlowResult(FlowDto Flow, bool Created);
public sealed record CreateFlowCommand(Guid ProjectId, CreateFlowRequest Body) : IRequest<CreateFlowResult>;
public sealed record GetFlowQuery(Guid Id) : IRequest<FlowDto>;
public sealed record RenameFlowCommand(Guid Id, RenameFlowRequest Body) : IRequest<FlowDto>;
public sealed record DeleteFlowCommand(Guid Id) : IRequest;
public sealed record GetFlowContentQuery(Guid Id) : IRequest<FlowContentDto>;
public sealed record UpdateFlowContentCommand(Guid Id, UpdateFlowContentRequest Body) : IRequest<FlowContentDto>;
public sealed record SubmitReviewCommand(Guid Id) : IRequest<FlowDto>;
public sealed record ConfirmFlowCommand(Guid Id, int VersionNo) : IRequest<FlowDto>;
public sealed record ReopenFlowCommand(Guid Id) : IRequest<FlowDto>;
public sealed record ListVersionsQuery(Guid FlowId) : IRequest<IReadOnlyList<FlowVersionSummaryDto>>;
public sealed record GetVersionQuery(Guid FlowId, int VersionNo) : IRequest<FlowContentDto>;
public sealed record DiffVersionsQuery(Guid FlowId, int From, int To) : IRequest<FlowDiffDto>;
public sealed record RollbackCommand(Guid FlowId, int VersionNo) : IRequest<FlowContentDto>;

public sealed class CreateFlowValidator : AbstractValidator<CreateFlowCommand>
{
    public CreateFlowValidator() =>
        RuleFor(x => x.Body.MenuName).NotEmpty().MaximumLength(200);
}

public sealed class RenameFlowValidator : AbstractValidator<RenameFlowCommand>
{
    public RenameFlowValidator() =>
        RuleFor(x => x.Body.MenuName).NotEmpty().WithMessage("The heading cannot be empty.").MaximumLength(200).WithMessage("The heading can have at most 200 letters.");
}

public sealed class FlowHandlers(
    IAppDbContext db, IProjectAccess access, ICurrentUser user, IClock clock, FlowVersionWriter writer, PromptComposer composer) :
    IRequestHandler<ListFlowsQuery, IReadOnlyList<FlowDto>>,
    IRequestHandler<CreateFlowCommand, CreateFlowResult>,
    IRequestHandler<GetFlowQuery, FlowDto>,
    IRequestHandler<RenameFlowCommand, FlowDto>,
    IRequestHandler<DeleteFlowCommand>,
    IRequestHandler<GetFlowContentQuery, FlowContentDto>,
    IRequestHandler<UpdateFlowContentCommand, FlowContentDto>,
    IRequestHandler<SubmitReviewCommand, FlowDto>,
    IRequestHandler<ConfirmFlowCommand, FlowDto>,
    IRequestHandler<ReopenFlowCommand, FlowDto>,
    IRequestHandler<ListVersionsQuery, IReadOnlyList<FlowVersionSummaryDto>>,
    IRequestHandler<GetVersionQuery, FlowContentDto>,
    IRequestHandler<DiffVersionsQuery, FlowDiffDto>,
    IRequestHandler<RollbackCommand, FlowContentDto>
{
    // ------------------------------------------------------------------ queries

    public async Task<IReadOnlyList<FlowDto>> Handle(ListFlowsQuery r, CancellationToken ct)
    {
        await access.EnsureMemberAsync(r.ProjectId, ct);
        var q = db.Flows.AsNoTracking().Where(f => f.ProjectDepartment!.ProjectId == r.ProjectId);
        if (r.DepartmentId is { } d) q = q.Where(f => f.ProjectDepartmentId == d);
        if (!string.IsNullOrWhiteSpace(r.Status))
        {
            if (!Enum.TryParse<FlowStatus>(r.Status, true, out var s)) throw new DomainException("Unknown flow status.", "invalid_status");
            q = q.Where(f => f.Status == s);
        }
        return await q.OrderBy(f => f.ProjectDepartment!.ModuleCode).ThenBy(f => f.MenuName).Select(Mapping.FlowProjection).ToListAsync(ct);
    }

    public async Task<FlowDto> Handle(GetFlowQuery r, CancellationToken ct) => await LoadDtoAsync(r.Id, ct, requireMember: true);

    /// <summary>
    /// Changes the heading (name) of a request. Its number (NR-003) stays. Names are unique inside a module, so a name another request already has is refused.
    /// The implementation document keeps the number and gets the new title; a PDF that was already made keeps the old title until it is made again.
    /// Anybody who may capture for the module can do it.
    /// </summary>
    public async Task<FlowDto> Handle(RenameFlowCommand r, CancellationToken ct)
    {
        var flow = await db.Flows.AsNoTracking().Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == r.Id, ct) ?? throw new NotFoundException("Flow", r.Id);
        await access.EnsureCanCaptureAsync(flow.ProjectDepartment!.ProjectId, flow.ProjectDepartmentId, ct);

        var name = r.Body.MenuName.Trim();
        if (name == flow.MenuName) return await LoadDtoAsync(flow.Id, ct, false);
        var lower = name.ToLower();
        if (await db.Flows.AsNoTracking().AnyAsync(f => f.ProjectDepartmentId == flow.ProjectDepartmentId && f.Id != flow.Id && f.MenuName.ToLower() == lower, ct))
            throw new ConflictException($"This module already has a request called \"{name}\". Choose another heading.");

        var now = clock.UtcNow;
        try
        {
            await db.Flows.Where(f => f.Id == flow.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.MenuName, name).SetProperty(f => f.UpdatedAt, now).SetProperty(f => f.LastActivityAt, now), ct);
        }
        catch (DbUpdateException) { throw new ConflictException($"This module already has a request called \"{name}\". Choose another heading."); }
        await db.GeneratedDocuments.Where(d => d.FlowId == flow.Id && d.Scope == DocumentScope.Guide).ExecuteUpdateAsync(s => s.SetProperty(d => d.Title, name), ct);
        return await LoadDtoAsync(flow.Id, ct, false);
    }

    /// <summary>
    /// Deletes a request (an NR or a CR): it leaves every list and cannot be opened, but nothing is erased (its notes, versions and documents stay, so it can be
    /// brought back). Its name is freed for a new request, and its number is never used again. Anybody who may capture for its module can delete it.
    /// </summary>
    public async Task Handle(DeleteFlowCommand r, CancellationToken ct)
    {
        var flow = await db.Flows.Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == r.Id, ct) ?? throw new NotFoundException("Flow", r.Id);
        await access.EnsureCanCaptureAsync(flow.ProjectDepartment!.ProjectId, flow.ProjectDepartmentId, ct);
        var now = clock.UtcNow;
        flow.DeletedAt = now;
        // names are unique inside a module: the old name is released, and the old one stays recognisable if it is ever restored
        var tag = $" [deleted {flow.RequestNo:000} {now:yyyyMMddHHmmss}]";
        flow.MenuName = (flow.MenuName.Length + tag.Length > 200 ? flow.MenuName[..(200 - tag.Length)] : flow.MenuName) + tag;
        await db.SaveChangesAsync(ct);
    }

    public async Task<CreateFlowResult> Handle(CreateFlowCommand r, CancellationToken ct)
    {
        var pd = await db.ProjectDepartments.AsNoTracking()
                     .FirstOrDefaultAsync(x => x.Id == r.Body.ProjectDepartmentId && x.ProjectId == r.ProjectId && x.IsActive, ct)
                 ?? throw new NotFoundException("Project department", r.Body.ProjectDepartmentId);
        await access.EnsureCanCaptureAsync(r.ProjectId, pd.Id, ct);

        var name = r.Body.MenuName.Trim();
        var existing = await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.ProjectDepartmentId == pd.Id && f.MenuName.ToLower() == name.ToLower(), ct);
        if (existing is not null) return new CreateFlowResult(await LoadDtoAsync(existing.Id, ct, false), false);

        var kind = string.IsNullOrWhiteSpace(r.Body.Kind) ? RequestKind.NR
            : Enum.TryParse<RequestKind>(r.Body.Kind.Trim(), true, out var k) && Enum.IsDefined(k) ? k
            : throw new DomainException("A request is either NR (a new requirement), CR (a change request) or BUG (a bug to fix).", "invalid_kind");

        // the next number inside this module and kind; two people adding at the same moment get a new try, never a duplicate
        for (var attempt = 1; ; attempt++)
        {
            // deleted requests count too: a number is never given out twice
            var next = (await db.Flows.IgnoreQueryFilters().AsNoTracking().Where(f => f.ProjectDepartmentId == pd.Id && f.Kind == kind).MaxAsync(f => (int?)f.RequestNo, ct) ?? 0) + 1;
            var flow = new Flow { ProjectDepartmentId = pd.Id, MenuName = name, Kind = kind, RequestNo = next, LastActivityAt = clock.UtcNow };
            db.Flows.Add(flow);
            try
            {
                await db.SaveChangesAsync(ct);
                return new CreateFlowResult(await LoadDtoAsync(flow.Id, ct, false), true);
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                db.Flows.Remove(flow);
                if (db is DbContext context) context.ChangeTracker.Clear();
            }
        }
    }

    public async Task<FlowContentDto> Handle(GetFlowContentQuery r, CancellationToken ct)
    {
        var flow = await RequireFlowAsync(r.Id, requireMember: true, ct);
        return await ReadVersionAsync(flow, flow.CurrentVersionNo, ct);
    }

    public async Task<IReadOnlyList<FlowVersionSummaryDto>> Handle(ListVersionsQuery r, CancellationToken ct)
    {
        await RequireFlowAsync(r.FlowId, requireMember: true, ct);
        return await db.FlowVersions.AsNoTracking().Where(v => v.FlowId == r.FlowId).OrderByDescending(v => v.VersionNo)
            .Select(v => new FlowVersionSummaryDto(v.VersionNo, v.Source.ToString(), v.CreatedByUser == null ? "System" : v.CreatedByUser.DisplayName,
                v.CreatedAt, v.ChangeSummary, v.IsConfirmed)).ToListAsync(ct);
    }

    public async Task<FlowContentDto> Handle(GetVersionQuery r, CancellationToken ct)
    {
        var flow = await RequireFlowAsync(r.FlowId, requireMember: true, ct);
        return await ReadVersionAsync(flow, r.VersionNo, ct);
    }

    public async Task<FlowDiffDto> Handle(DiffVersionsQuery r, CancellationToken ct)
    {
        await RequireFlowAsync(r.FlowId, requireMember: true, ct);
        var a = await LoadNodeAsync(r.FlowId, r.From, ct);
        var b = await LoadNodeAsync(r.FlowId, r.To, ct);
        return new FlowDiffDto(r.From, r.To, FlowDiffer.Diff(a, b));
    }

    // ------------------------------------------------------------------ edits

    public async Task<FlowContentDto> Handle(UpdateFlowContentCommand r, CancellationToken ct)
    {
        var flow = await RequireFlowAsync(r.Id, requireMember: false, ct, tracked: true);
        var pd = flow.ProjectDepartment!;
        await access.EnsureCanCaptureAsync(pd.ProjectId, pd.Id, ct);

        if (flow.CurrentVersionNo == 0)
            throw new DomainException("The flow has no analysed content yet. Run an analysis first.", "not_analysed");
        if (r.Body.BaseVersionNo != flow.CurrentVersionNo)
            throw new ConflictException(
                $"The flow is now at version {flow.CurrentVersionNo}, but you edited version {r.Body.BaseVersionNo}. Reload and apply your change again.",
                await ReadVersionAsync(flow, flow.CurrentVersionNo, ct));

        var node = JsonNode.Parse(r.Body.Content.GetRawText()) as JsonObject
                   ?? throw new DomainException("The content must be a JSON object.", "invalid_content");
        var validator = new FlowContentValidator(await composer.GetFlowSchemaAsync(ct));
        var result = validator.Validate(node, FlowValidationContext.Manual(pd.DisplayName(), pd.ModuleCode));
        if (!result.IsValid)
            throw new ValidationException(result.Errors.Select(e => new FluentValidation.Results.ValidationFailure("content", e)));

        var locked = (r.Body.LockedIds ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var known = FlowLockEnforcer.LockableScalars.ToHashSet(StringComparer.Ordinal);
        foreach (var c in FlowContentValidator.Collections)
            if (node[c] is JsonArray arr)
                foreach (var o in arr.OfType<JsonObject>())
                    if (o[FlowLockEnforcer.IdProperty(c)]?.GetValue<string>() is { } id) known.Add(id);
        var unknownLocks = locked.Where(l => !known.Contains(l)).ToList();
        if (unknownLocks.Count > 0)
            throw new DomainException($"These locked ids do not exist in the content: {string.Join(", ", unknownLocks)}.", "invalid_lock");

        // Record what the user changed so the review screen and the next re-analysis can show it.
        var previous = await LoadNodeAsync(flow.Id, flow.CurrentVersionNo, ct);
        var diff = FlowDiffer.Diff(previous, node);
        var content = result.Content!;
        content.ChangeLog = diff.Take(100).Select(d => new ChangeLogItem
        {
            ItemId = d.ItemId, Change = d.Change, Summary = $"{d.Change} manually in {d.Section}.",
        }).ToList();
        var summary = r.Body.ChangeNote ?? (diff.Count == 0 ? "Saved without changes" : $"Manual edit: {diff.Count} change(s)");

        try
        {
            await writer.AddAsync(flow, content, VersionSource.Manual, null, locked, summary, user.RequireUserId(), ct);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new ConflictException("The flow was changed by someone else while you were saving. Reload and try again.");
        }
        return await ReadVersionAsync(flow, flow.CurrentVersionNo, ct);
    }

    public async Task<FlowContentDto> Handle(RollbackCommand r, CancellationToken ct)
    {
        var flow = await RequireFlowAsync(r.FlowId, requireMember: false, ct, tracked: true);
        await access.EnsureLeadAsync(flow.ProjectDepartment!.ProjectId, ct);
        var source = await db.FlowVersions.AsNoTracking().FirstOrDefaultAsync(v => v.FlowId == flow.Id && v.VersionNo == r.VersionNo, ct)
                     ?? throw new NotFoundException("Flow version", r.VersionNo);
        if (r.VersionNo == flow.CurrentVersionNo) throw new DomainException("That is already the current version.", "already_current");

        var content = FlowJson.Deserialize(source.Content);
        content.ChangeLog = [];
        var locked = JsonSerializer.Deserialize<string[]>(source.LockedIds) ?? [];
        await writer.AddAsync(flow, content, VersionSource.Rollback, null, locked, $"Rollback to version {r.VersionNo}", user.RequireUserId(), ct);
        await db.SaveChangesAsync(ct);
        return await ReadVersionAsync(flow, flow.CurrentVersionNo, ct);
    }

    // ------------------------------------------------------------------ lifecycle

    public async Task<FlowDto> Handle(SubmitReviewCommand r, CancellationToken ct)
    {
        var flow = await RequireFlowAsync(r.Id, requireMember: false, ct, tracked: true);
        await access.EnsureCanCaptureAsync(flow.ProjectDepartment!.ProjectId, flow.ProjectDepartmentId, ct);
        flow.Status = FlowStatusMachine.SubmitForReview(flow.Status);
        flow.LastActivityAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return await LoadDtoAsync(flow.Id, ct, false);
    }

    public async Task<FlowDto> Handle(ConfirmFlowCommand r, CancellationToken ct)
    {
        var flow = await RequireFlowAsync(r.Id, requireMember: false, ct, tracked: true);
        var pd = flow.ProjectDepartment!;
        await access.EnsureLeadAsync(pd.ProjectId, ct);

        var next = FlowStatusMachine.Confirm(flow.Status);        // throws unless UnderReview
        if (r.VersionNo != flow.CurrentVersionNo)
            throw new DomainException($"Version {r.VersionNo} is not the current version ({flow.CurrentVersionNo}). Review the latest version before confirming.", "stale_version");

        var blocking = await db.ClarificationItems.CountAsync(c => c.FlowId == flow.Id && c.Status == ClarificationStatus.Open && c.IsBlocking, ct);
        if (blocking > 0)
            throw new DomainException($"{blocking} blocking question(s) are still open. Answer or dismiss them before confirming.", "blocking_questions");

        var version = await db.FlowVersions.FirstAsync(v => v.FlowId == flow.Id && v.VersionNo == r.VersionNo, ct);
        var validator = new FlowContentValidator(await composer.GetFlowSchemaAsync(ct));
        var check = validator.Validate((JsonObject)JsonNode.Parse(version.Content)!, FlowValidationContext.Manual(pd.DisplayName(), pd.ModuleCode));
        if (!check.IsValid)
            throw new DomainException("The content is not valid and cannot be confirmed: " + string.Join(" | ", check.Errors.Take(5)), "invalid_content");

        flow.Status = next;
        flow.ConfirmedVersionNo = version.VersionNo;
        flow.ConfirmedBy = user.RequireUserId();
        flow.ConfirmedAt = clock.UtcNow;
        flow.LastActivityAt = clock.UtcNow;
        version.IsConfirmed = true;
        await db.SaveChangesAsync(ct);
        return await LoadDtoAsync(flow.Id, ct, false);
    }

    public async Task<FlowDto> Handle(ReopenFlowCommand r, CancellationToken ct)
    {
        var flow = await RequireFlowAsync(r.Id, requireMember: false, ct, tracked: true);
        await access.EnsureLeadAsync(flow.ProjectDepartment!.ProjectId, ct);
        flow.Status = FlowStatusMachine.Reopen(flow.Status);
        flow.ConfirmedVersionNo = null;
        flow.LastActivityAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return await LoadDtoAsync(flow.Id, ct, false);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Flow> RequireFlowAsync(Guid id, bool requireMember, CancellationToken ct, bool tracked = false)
    {
        var q = db.Flows.Include(f => f.ProjectDepartment!).ThenInclude(p => p.Department).AsQueryable();
        if (!tracked) q = q.AsNoTracking();
        var flow = await q.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Flow", id);
        if (requireMember) await access.EnsureMemberAsync(flow.ProjectDepartment!.ProjectId, ct);
        return flow;
    }

    private async Task<FlowDto> LoadDtoAsync(Guid id, CancellationToken ct, bool requireMember)
    {
        var dto = await db.Flows.AsNoTracking().Where(f => f.Id == id).Select(Mapping.FlowProjection).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("Flow", id);
        if (requireMember) await access.EnsureMemberAsync(dto.ProjectId, ct);
        return dto;
    }

    private async Task<FlowContentDto> ReadVersionAsync(Flow flow, int versionNo, CancellationToken ct)
    {
        var v = await db.FlowVersions.AsNoTracking().Include(x => x.CreatedByUser)
                    .FirstOrDefaultAsync(x => x.FlowId == flow.Id && x.VersionNo == versionNo, ct)
                ?? throw new NotFoundException("Flow version", versionNo);
        return new FlowContentDto(flow.Id, v.VersionNo, v.Source.ToString(), JsonSerializer.Deserialize<JsonElement>(v.Content),
            JsonSerializer.Deserialize<string[]>(v.LockedIds) ?? [], v.CreatedAt, v.CreatedByUser?.DisplayName ?? "System");
    }

    private async Task<JsonObject> LoadNodeAsync(Guid flowId, int versionNo, CancellationToken ct)
    {
        var content = await db.FlowVersions.AsNoTracking().Where(v => v.FlowId == flowId && v.VersionNo == versionNo)
                          .Select(v => v.Content).FirstOrDefaultAsync(ct)
                      ?? throw new NotFoundException("Flow version", versionNo);
        return (JsonObject)JsonNode.Parse(content)!;
    }
}

