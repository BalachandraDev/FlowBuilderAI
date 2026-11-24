using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Reviews;

/// <summary>Clarification answers and comments. Shared by the REST endpoints and the offline sync push.</summary>
public sealed class ReviewService(IAppDbContext db, IProjectAccess access, ICurrentUser user, IClock clock)
{
    public async Task<ClarificationDto> UpdateClarificationAsync(Guid id, UpdateClarificationRequest req, string? ifMatch, CancellationToken ct)
    {
        var item = await db.ClarificationItems.Include(c => c.Flow!).ThenInclude(f => f.ProjectDepartment)
                       .FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Clarification", id);
        var pd = item.Flow!.ProjectDepartment!;
        var role = await access.GetRoleAsync(pd.ProjectId, ct);

        if (req.IsBlocking is not null)
        {
            if (role != ProjectRole.Lead) throw new ForbiddenException("Only the Lead can mark a question as blocking.");
        }
        if (req.Answer is not null || req.Status is not null)
            await access.EnsureCanCaptureAsync(pd.ProjectId, pd.Id, ct);

        if (ifMatch is not null && ifMatch != item.ConcurrencyStamp)
            throw new ConflictException("This question was changed on the server.", item.ToDto());
        if (ifMatch is not null) db.Entry(item).Property(c => c.ConcurrencyStamp).OriginalValue = ifMatch;

        var now = clock.UtcNow;
        if (req.Answer is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Answer)) throw new DomainException("The answer cannot be empty.", "empty_answer");
            if (req.Answer.Length > 4000) throw new DomainException("The answer is too long.", "text_too_long");
            item.Answer = req.Answer.Trim();
            item.Status = ClarificationStatus.Answered;
            item.AnsweredBy = user.RequireUserId();
            item.AnsweredAt = now;
            item.DismissReason = null;
        }
        else if (req.Status is not null)
        {
            if (!Enum.TryParse<ClarificationStatus>(req.Status, true, out var status))
                throw new DomainException("Status must be Open, Answered or Dismissed.", "invalid_status");
            switch (status)
            {
                case ClarificationStatus.Dismissed:
                    if (string.IsNullOrWhiteSpace(req.DismissReason))
                        throw new DomainException("Give a reason for dismissing the question.", "reason_required");
                    item.Status = status;
                    item.DismissReason = req.DismissReason.Trim();
                    break;
                case ClarificationStatus.Open:
                    item.Status = status;
                    item.Answer = null;
                    item.DismissReason = null;
                    break;
                default:
                    throw new DomainException("Use the answer field to answer a question.", "invalid_status");
            }
        }
        if (req.IsBlocking is { } blocking) item.IsBlocking = blocking;

        item.Flow!.LastActivityAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var current = await db.ClarificationItems.AsNoTracking().FirstAsync(c => c.Id == id, ct);
            throw new ConflictException("This question was changed on the server.", current.ToDto());
        }
        return item.ToDto();
    }

    public async Task<(CommentDto Comment, bool Duplicate)> CreateCommentAsync(Guid flowId, CreateCommentRequest req, CancellationToken ct)
    {
        var flow = await db.Flows.Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == flowId, ct)
                   ?? throw new NotFoundException("Flow", flowId);
        await access.EnsureMemberAsync(flow.ProjectDepartment!.ProjectId, ct);
        var uid = user.RequireUserId();

        var existing = await db.Comments.AsNoTracking().Include(c => c.Author).FirstOrDefaultAsync(c => c.Id == req.Id, ct);
        if (existing is not null)
        {
            if (existing.AuthorId != uid || existing.FlowId != flowId) throw new ConflictException("A comment with this id already exists.");
            return (existing.ToDto(existing.Author?.DisplayName ?? ""), true);
        }

        if (string.IsNullOrWhiteSpace(req.Text) || req.Text.Length > 2000) throw new DomainException("A comment must be 1-2000 characters.", "invalid_comment");
        if (string.IsNullOrWhiteSpace(req.Section) || req.Section.Length > 40) throw new DomainException("Section is required.", "invalid_section");
        if (req.ParentCommentId is { } parent && !await db.Comments.AnyAsync(c => c.Id == parent && c.FlowId == flowId, ct))
            throw new DomainException("The comment you are replying to does not exist.", "invalid_parent");

        var comment = new Comment
        {
            Id = req.Id, FlowId = flowId, Section = req.Section, ItemId = req.ItemId, ParentCommentId = req.ParentCommentId,
            Text = req.Text.Trim(), AuthorId = uid,
        };
        db.Comments.Add(comment);
        flow.LastActivityAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        var name = await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.DisplayName).FirstAsync(ct);
        return (comment.ToDto(name), false);
    }

    public async Task<CommentDto> ResolveCommentAsync(Guid commentId, bool resolved, CancellationToken ct)
    {
        var comment = await db.Comments.Include(c => c.Author).Include(c => c.Flow!).ThenInclude(f => f.ProjectDepartment)
                          .FirstOrDefaultAsync(c => c.Id == commentId, ct) ?? throw new NotFoundException("Comment", commentId);
        var projectId = comment.Flow!.ProjectDepartment!.ProjectId;
        var role = await access.GetRoleAsync(projectId, ct);
        if (role is not (ProjectRole.Lead or ProjectRole.Reviewer))
            throw new ForbiddenException("Only a Lead or Reviewer can resolve comments.");
        comment.IsResolved = resolved;
        comment.ResolvedBy = resolved ? user.RequireUserId() : null;
        await db.SaveChangesAsync(ct);
        return comment.ToDto(comment.Author?.DisplayName ?? "");
    }
}
