using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Application.Reviews;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.ReviewFeatures;

public sealed record ListClarificationsQuery(Guid FlowId, string? Status) : IRequest<IReadOnlyList<ClarificationDto>>;
public sealed record UpdateClarificationCommand(Guid Id, UpdateClarificationRequest Body, string? IfMatch) : IRequest<ClarificationDto>;
public sealed record ListCommentsQuery(Guid FlowId, string? Section) : IRequest<IReadOnlyList<CommentDto>>;
public sealed record CreateCommentResult(CommentDto Comment, bool Duplicate);
public sealed record CreateCommentCommand(Guid FlowId, CreateCommentRequest Body) : IRequest<CreateCommentResult>;
public sealed record ResolveCommentCommand(Guid Id, bool Resolved) : IRequest<CommentDto>;

public sealed class UpdateClarificationValidator : AbstractValidator<UpdateClarificationCommand>
{
    public UpdateClarificationValidator() =>
        RuleFor(x => x.Body).Must(b => b.Answer is not null || b.Status is not null || b.IsBlocking is not null)
            .WithMessage("Provide an answer, a status or isBlocking.");
}

public sealed class CreateCommentValidator : AbstractValidator<CreateCommentCommand>
{
    public CreateCommentValidator()
    {
        RuleFor(x => x.Body.Id).NotEmpty();
        RuleFor(x => x.Body.Text).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Body.Section).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Body.ItemId).MaximumLength(20);
    }
}

public sealed class ReviewHandlers(IAppDbContext db, IProjectAccess access, ReviewService reviews) :
    IRequestHandler<ListClarificationsQuery, IReadOnlyList<ClarificationDto>>,
    IRequestHandler<UpdateClarificationCommand, ClarificationDto>,
    IRequestHandler<ListCommentsQuery, IReadOnlyList<CommentDto>>,
    IRequestHandler<CreateCommentCommand, CreateCommentResult>,
    IRequestHandler<ResolveCommentCommand, CommentDto>
{
    public async Task<IReadOnlyList<ClarificationDto>> Handle(ListClarificationsQuery r, CancellationToken ct)
    {
        await EnsureFlowMemberAsync(r.FlowId, ct);
        var q = db.ClarificationItems.AsNoTracking().Where(c => c.FlowId == r.FlowId);
        if (!string.IsNullOrWhiteSpace(r.Status))
        {
            if (!Enum.TryParse<ClarificationStatus>(r.Status, true, out var s)) throw new DomainException("Unknown status.", "invalid_status");
            q = q.Where(c => c.Status == s);
        }
        var rows = await q.OrderBy(c => c.QuestionKey).ToListAsync(ct);
        return rows.OrderBy(c => c.QuestionKey.StartsWith('X') ? 1 : 0).ThenBy(c => Mapping.NumberOf(c.QuestionKey)).Select(c => c.ToDto()).ToList();
    }

    public Task<ClarificationDto> Handle(UpdateClarificationCommand r, CancellationToken ct) =>
        reviews.UpdateClarificationAsync(r.Id, r.Body, r.IfMatch, ct);

    public async Task<IReadOnlyList<CommentDto>> Handle(ListCommentsQuery r, CancellationToken ct)
    {
        await EnsureFlowMemberAsync(r.FlowId, ct);
        var q = db.Comments.AsNoTracking().Where(c => c.FlowId == r.FlowId);
        if (!string.IsNullOrWhiteSpace(r.Section)) q = q.Where(c => c.Section == r.Section);
        return await q.OrderBy(c => c.CreatedAt).Select(c => new CommentDto(c.Id, c.FlowId, c.Section, c.ItemId, c.ParentCommentId, c.Text,
            c.IsResolved, c.Author!.DisplayName, c.CreatedAt)).ToListAsync(ct);
    }

    public async Task<CreateCommentResult> Handle(CreateCommentCommand r, CancellationToken ct)
    {
        var (comment, duplicate) = await reviews.CreateCommentAsync(r.FlowId, r.Body, ct);
        return new CreateCommentResult(comment, duplicate);
    }

    public Task<CommentDto> Handle(ResolveCommentCommand r, CancellationToken ct) => reviews.ResolveCommentAsync(r.Id, r.Resolved, ct);

    private async Task EnsureFlowMemberAsync(Guid flowId, CancellationToken ct)
    {
        var projectId = await db.Flows.AsNoTracking().Where(f => f.Id == flowId).Select(f => (Guid?)f.ProjectDepartment!.ProjectId).FirstOrDefaultAsync(ct)
                        ?? throw new NotFoundException("Flow", flowId);
        await access.EnsureMemberAsync(projectId, ct);
    }
}

