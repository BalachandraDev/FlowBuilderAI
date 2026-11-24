using FlowCapture.Api.Infrastructure;
using FlowCapture.Application.Features.AnalysisFeatures;
using FlowCapture.Application.Features.CaptureFeatures;
using FlowCapture.Application.Features.FlowFeatures;
using FlowCapture.Application.Features.ReviewFeatures;
using FlowCapture.Shared.Dtos;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace FlowCapture.Api.Controllers;

public sealed class FlowsController(ISender mediator) : ApiController(mediator)
{
    [HttpGet("flows/{id:guid}")]
    public async Task<ActionResult<FlowDto>> Get(Guid id, CancellationToken ct) => await Mediator.Send(new GetFlowQuery(id), ct);

    /// <summary>Changes the heading of a request. Its number stays. Anybody who may capture for its module.</summary>
    [HttpPut("flows/{id:guid}")]
    public async Task<ActionResult<FlowDto>> Rename(Guid id, RenameFlowRequest r, CancellationToken ct) => await Mediator.Send(new RenameFlowCommand(id, r), ct);

    /// <summary>Deletes a request (NR, CR or BUG): hidden everywhere, nothing erased. Anybody who may capture for its module.</summary>
    [HttpDelete("flows/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await Mediator.Send(new DeleteFlowCommand(id), ct);
        return NoContent();
    }

    [HttpGet("flows/{id:guid}/content")]
    public async Task<ActionResult<FlowContentDto>> Content(Guid id, CancellationToken ct) => await Mediator.Send(new GetFlowContentQuery(id), ct);

    [HttpPut("flows/{id:guid}/content")]
    public async Task<ActionResult<FlowContentDto>> UpdateContent(Guid id, UpdateFlowContentRequest r, CancellationToken ct) =>
        await Mediator.Send(new UpdateFlowContentCommand(id, r), ct);

    [HttpPost("flows/{id:guid}/submit-review")]
    public async Task<ActionResult<FlowDto>> SubmitReview(Guid id, CancellationToken ct) => await Mediator.Send(new SubmitReviewCommand(id), ct);

    [HttpPost("flows/{id:guid}/confirm")]
    public async Task<ActionResult<FlowDto>> Confirm(Guid id, ConfirmFlowRequest r, CancellationToken ct) => await Mediator.Send(new ConfirmFlowCommand(id, r.VersionNo), ct);

    [HttpPost("flows/{id:guid}/reopen")]
    public async Task<ActionResult<FlowDto>> Reopen(Guid id, CancellationToken ct) => await Mediator.Send(new ReopenFlowCommand(id), ct);

    // ---- versions
    [HttpGet("flows/{id:guid}/versions")]
    public async Task<ActionResult<IReadOnlyList<FlowVersionSummaryDto>>> Versions(Guid id, CancellationToken ct) => Ok(await Mediator.Send(new ListVersionsQuery(id), ct));

    [HttpGet("flows/{id:guid}/versions/{n:int}")]
    public async Task<ActionResult<FlowContentDto>> Version(Guid id, int n, CancellationToken ct) => await Mediator.Send(new GetVersionQuery(id, n), ct);

    [HttpGet("flows/{id:guid}/versions/{a:int}/diff/{b:int}")]
    public async Task<ActionResult<FlowDiffDto>> Diff(Guid id, int a, int b, CancellationToken ct) => await Mediator.Send(new DiffVersionsQuery(id, a, b), ct);

    [HttpPost("flows/{id:guid}/versions/{n:int}/rollback")]
    public async Task<ActionResult<FlowContentDto>> Rollback(Guid id, int n, CancellationToken ct) => await Mediator.Send(new RollbackCommand(id, n), ct);

    // ---- analysis
    [Idempotent, Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("ai"), HttpPost("flows/{id:guid}/analyses")]
    public async Task<IActionResult> Analyse(Guid id, StartAnalysisRequest r, CancellationToken ct)
    {
        var run = await Mediator.Send(new StartPartAnalysisCommand(id, r), ct);
        return AcceptedJob(run.Id, run);
    }

    [HttpGet("flows/{id:guid}/analyses")]
    public async Task<ActionResult<IReadOnlyList<AnalysisRunDto>>> Runs(Guid id, CancellationToken ct) => Ok(await Mediator.Send(new ListRunsQuery(id), ct));

    // ---- entries
    [HttpGet("flows/{id:guid}/entries")]
    public async Task<ActionResult<IReadOnlyList<EntryDto>>> Entries(Guid id, CancellationToken ct) => Ok(await Mediator.Send(new ListEntriesQuery(id), ct));

    [HttpPost("flows/{id:guid}/entries")]
    public async Task<ActionResult<EntryDto>> CreateEntry(Guid id, CreateEntryRequest r, CancellationToken ct)
    {
        var result = await Mediator.Send(new CreateEntryCommand(id, r), ct);
        return result.Duplicate ? Ok(result.Entry) : Created($"/api/v1/entries/{result.Entry.Id}", result.Entry);
    }

    // ---- clarifications and comments
    [HttpGet("flows/{id:guid}/clarifications")]
    public async Task<ActionResult<IReadOnlyList<ClarificationDto>>> Clarifications(Guid id, [FromQuery] string? status, CancellationToken ct) =>
        Ok(await Mediator.Send(new ListClarificationsQuery(id, status), ct));

    [HttpGet("flows/{id:guid}/comments")]
    public async Task<ActionResult<IReadOnlyList<CommentDto>>> Comments(Guid id, [FromQuery] string? section, CancellationToken ct) =>
        Ok(await Mediator.Send(new ListCommentsQuery(id, section), ct));

    [HttpPost("flows/{id:guid}/comments")]
    public async Task<ActionResult<CommentDto>> CreateComment(Guid id, CreateCommentRequest r, CancellationToken ct)
    {
        var result = await Mediator.Send(new CreateCommentCommand(id, r), ct);
        return result.Duplicate ? Ok(result.Comment) : Created($"/api/v1/flows/{id}/comments", result.Comment);
    }
}

public sealed class ReviewItemsController(ISender mediator) : ApiController(mediator)
{
    [HttpPut("clarifications/{id:guid}")]
    public async Task<ActionResult<ClarificationDto>> Update(Guid id, UpdateClarificationRequest r, CancellationToken ct) =>
        await Mediator.Send(new UpdateClarificationCommand(id, r, IfMatch), ct);

    [HttpPut("comments/{id:guid}/resolve")]
    public async Task<ActionResult<CommentDto>> Resolve(Guid id, ResolveCommentRequest r, CancellationToken ct) =>
        await Mediator.Send(new ResolveCommentCommand(id, r.Resolved), ct);
}

public sealed class AnalysisRunsController(ISender mediator) : ApiController(mediator)
{
    [HttpGet("analysis-runs/{id:guid}")]
    public async Task<ActionResult<AnalysisRunDto>> Get(Guid id, CancellationToken ct) => await Mediator.Send(new GetRunQuery(id), ct);

    [HttpPost("analysis-runs/{id:guid}/cancel")]
    public async Task<ActionResult<AnalysisRunDto>> Cancel(Guid id, CancellationToken ct) => await Mediator.Send(new CancelRunCommand(id), ct);
}
