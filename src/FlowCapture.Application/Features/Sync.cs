using System.Text.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Capture;
using FlowCapture.Application.Common;
using FlowCapture.Application.Reviews;
using FlowCapture.Domain;
using FlowCapture.Shared.Content;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.SyncFeatures;

public sealed record SyncPushCommand(SyncPushRequest Body) : IRequest<SyncPushResponse>;
public sealed record SyncPullQuery(string? Since, Guid? ProjectId, int? PageSize) : IRequest<SyncPullResponse>;

public sealed class SyncPushValidator : AbstractValidator<SyncPushCommand>
{
    public SyncPushValidator()
    {
        RuleFor(x => x.Body.DeviceId).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Body.Ops).NotEmpty().Must(o => o.Count <= 100).WithMessage("Send at most 100 operations per request.");
    }
}

/// <summary>
/// Applies a batch of offline operations in order. Each operation is idempotent (client generated ids), and a failure of one
/// never blocks the others: every operation gets its own result.
/// </summary>
public sealed class SyncPushHandler(IAppDbContext db, EntryService entries, ReviewService reviews) : IRequestHandler<SyncPushCommand, SyncPushResponse>
{
    public async Task<SyncPushResponse> Handle(SyncPushCommand r, CancellationToken ct)
    {
        var results = new List<SyncOpResultDto>();
        foreach (var op in r.Body.Ops)
        {
            try { results.Add(await ApplyAsync(op, ct)); }
            catch (ConflictException e)
            {
                db.ChangeTracker.Clear();
                results.Add(new SyncOpResultDto(op.OpId, "Conflict", null, e.ServerCopy is null ? null : JsonSerializer.SerializeToElement(e.ServerCopy, FlowJson.Options), e.Message));
            }
            catch (Exception e) when (e is ForbiddenException or DomainException or NotFoundException or ValidationException or JsonException)
            {
                db.ChangeTracker.Clear();
                var reason = e is ValidationException v ? string.Join("; ", v.Errors.Select(x => x.ErrorMessage)) : e.Message;
                results.Add(new SyncOpResultDto(op.OpId, "Rejected", null, null, reason));
            }
        }
        return new SyncPushResponse(results);
    }

    private async Task<SyncOpResultDto> ApplyAsync(SyncOpDto op, CancellationToken ct)
    {
        switch (op.Type.ToLowerInvariant())
        {
            case "createentry":
            {
                var p = Read<SyncCreateEntryPayload>(op);
                var res = await entries.CreateAsync(p.FlowId, new CreateEntryRequest(op.EntityId, p.Kind, p.CapturedAt, p.Language,
                    p.RawText, p.Notes, p.IntervieweeName, p.IntervieweeRole), ct);
                return new SyncOpResultDto(op.OpId, res.Duplicate ? "Duplicate" : "Applied", res.Entry.RowVersion, null, null);
            }
            case "updateentry":
            {
                var p = Read<UpdateEntryRequest>(op);
                var current = await entries.GetDtoAsync(op.EntityId, ct);
                // The same update arriving twice (the first response was lost) must not look like a conflict.
                if (current.RawText == p.RawText && current.Notes == p.Notes && current.IntervieweeName == p.IntervieweeName
                    && current.IntervieweeRole == p.IntervieweeRole && (string.IsNullOrWhiteSpace(p.Language) || current.Language == p.Language.Trim()))
                    return new SyncOpResultDto(op.OpId, "Duplicate", current.RowVersion, null, null);
                var updated = await entries.UpdateAsync(op.EntityId, p, op.BaseRowVersion, ct);
                return new SyncOpResultDto(op.OpId, "Applied", updated.RowVersion, null, null);
            }
            case "deleteentry":
            {
                try { await entries.DeleteAsync(op.EntityId, ct); }
                catch (NotFoundException) { return new SyncOpResultDto(op.OpId, "Duplicate", null, null, null); }
                return new SyncOpResultDto(op.OpId, "Applied", null, null, null);
            }
            case "createcomment":
            {
                var p = Read<SyncCreateCommentPayload>(op);
                var res = await reviews.CreateCommentAsync(p.FlowId, new CreateCommentRequest(op.EntityId, p.Section, p.ItemId, p.ParentCommentId, p.Text), ct);
                return new SyncOpResultDto(op.OpId, res.Duplicate ? "Duplicate" : "Applied", null, null, null);
            }
            case "answerclarification":
            {
                var p = Read<AnswerClarificationOp>(op);
                var existing = await db.ClarificationItems.AsNoTracking().FirstOrDefaultAsync(c => c.Id == op.EntityId, ct)
                               ?? throw new NotFoundException("Clarification", op.EntityId);
                if (p.Answer is not null && existing.Status == ClarificationStatus.Answered && existing.Answer == p.Answer.Trim())
                    return new SyncOpResultDto(op.OpId, "Duplicate", existing.ConcurrencyStamp, null, null);
                var dto = await reviews.UpdateClarificationAsync(op.EntityId, new UpdateClarificationRequest(p.Answer, p.Status, p.DismissReason, null), op.BaseRowVersion, ct);
                return new SyncOpResultDto(op.OpId, "Applied", dto.RowVersion, null, null);
            }
            default:
                throw new DomainException($"Unknown operation type '{op.Type}'.", "unknown_op");
        }
    }

    private static T Read<T>(SyncOpDto op) =>
        op.Payload.ValueKind == JsonValueKind.Undefined || op.Payload.ValueKind == JsonValueKind.Null
            ? throw new DomainException("The operation has no payload.", "missing_payload")
            : op.Payload.Deserialize<T>(FlowJson.Options) ?? throw new DomainException("The operation payload is empty.", "missing_payload");
}

/// <summary>
/// Returns everything that changed since the cursor (an opaque UTC tick count). When a page is full the cursor stops just before the
/// last row, so rows sharing a timestamp are re-sent rather than lost. Clients upsert by id, so repeats are harmless.
/// </summary>
public sealed class SyncPullHandler(IAppDbContext db, IProjectAccess access, IClock clock) : IRequestHandler<SyncPullQuery, SyncPullResponse>
{
    public async Task<SyncPullResponse> Handle(SyncPullQuery r, CancellationToken ct)
    {
        var since = ParseCursor(r.Since);
        var page = Math.Clamp(r.PageSize ?? 200, 10, 500);

        IReadOnlyList<Guid> projectIds;
        if (r.ProjectId is { } pid)
        {
            await access.EnsureMemberAsync(pid, ct);
            projectIds = [pid];
        }
        else projectIds = await access.MemberProjectIdsAsync(ct);

        DateTime? stop = null;                         // earliest "truncated" position across tables
        void Truncated(DateTime lastStamp)
        {
            var c = lastStamp.AddTicks(-1);
            if (stop is null || c < stop) stop = c;
        }

        // entries (an entry is also "changed" when its audio or transcript row changed)
        var entryRows = await db.CaptureEntries.AsNoTracking().Include(e => e.CapturedByUser).Include(e => e.Audio).Include(e => e.Transcript)
            .Where(e => projectIds.Contains(e.Flow!.ProjectDepartment!.ProjectId)
                        && (e.UpdatedAt > since || (e.Transcript != null && e.Transcript.UpdatedAt > since) || (e.Audio != null && e.Audio.UpdatedAt > since)))
            .ToListAsync(ct);
        var entryStamp = (CaptureEntry e) => new[] { e.UpdatedAt, e.Transcript?.UpdatedAt ?? DateTime.MinValue, e.Audio?.UpdatedAt ?? DateTime.MinValue }.Max();
        var orderedEntries = entryRows.OrderBy(entryStamp).ToList();
        if (orderedEntries.Count > page) { orderedEntries = orderedEntries.Take(page).ToList(); Truncated(entryStamp(orderedEntries[^1])); }
        var toEntryDto = Mapping.EntryProjection.Compile();

        var transcripts = await db.Transcripts.AsNoTracking()
            .Where(t => t.UpdatedAt > since && projectIds.Contains(t.Entry!.Flow!.ProjectDepartment!.ProjectId))
            .OrderBy(t => t.UpdatedAt).Take(page + 1).ToListAsync(ct);
        if (transcripts.Count > page) { transcripts = transcripts.Take(page).ToList(); Truncated(transcripts[^1].UpdatedAt); }

        var clar = await db.ClarificationItems.AsNoTracking()
            .Where(c => c.UpdatedAt > since && projectIds.Contains(c.Flow!.ProjectDepartment!.ProjectId))
            .OrderBy(c => c.UpdatedAt).Take(page + 1).ToListAsync(ct);
        if (clar.Count > page) { clar = clar.Take(page).ToList(); Truncated(clar[^1].UpdatedAt); }

        var flowRows = await db.Flows.AsNoTracking()
            .Where(f => f.UpdatedAt > since && projectIds.Contains(f.ProjectDepartment!.ProjectId))
            .OrderBy(f => f.UpdatedAt).Take(page + 1).Select(f => new { f.UpdatedAt, f.Id }).ToListAsync(ct);
        if (flowRows.Count > page) { flowRows = flowRows.Take(page).ToList(); Truncated(flowRows[^1].UpdatedAt); }
        var flowIds = flowRows.Select(f => f.Id).ToList();
        var flows = await db.Flows.AsNoTracking().Where(f => flowIds.Contains(f.Id)).Select(Mapping.FlowProjection).ToListAsync(ct);

        var comments = await db.Comments.AsNoTracking().Include(c => c.Author)
            .Where(c => c.UpdatedAt > since && projectIds.Contains(c.Flow!.ProjectDepartment!.ProjectId))
            .OrderBy(c => c.UpdatedAt).Take(page + 1).ToListAsync(ct);
        if (comments.Count > page) { comments = comments.Take(page).ToList(); Truncated(comments[^1].UpdatedAt); }

        var hasMore = stop is not null;
        var next = hasMore ? stop!.Value : clock.UtcNow.AddSeconds(-5);   // small overlap covers commits that landed late
        if (next < since) next = since;
        return new SyncPullResponse(
            next.Ticks.ToString(), hasMore,
            orderedEntries.Select(toEntryDto).ToList(),
            transcripts.Select(t => t.ToDto()).ToList(),
            clar.Select(c => c.ToDto()).ToList(),
            flows,
            comments.Select(c => c.ToDto(c.Author?.DisplayName ?? "")).ToList());
    }

    private static DateTime ParseCursor(string? cursor) =>
        string.IsNullOrWhiteSpace(cursor) ? DateTime.MinValue
        : long.TryParse(cursor, out var ticks) && ticks is >= 0 and <= 3155378975999999999L ? new DateTime(ticks, DateTimeKind.Utc)
        : throw new DomainException("The sync cursor is not valid. Start again without a cursor.", "invalid_cursor");
}
