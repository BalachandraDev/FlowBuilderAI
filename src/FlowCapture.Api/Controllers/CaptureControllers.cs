using FlowCapture.Application.Features.CaptureFeatures;
using FlowCapture.Application.Features.ReportingFeatures;
using FlowCapture.Application.Features.SyncFeatures;
using FlowCapture.Shared.Dtos;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace FlowCapture.Api.Controllers;

public sealed class EntriesController(ISender mediator) : ApiController(mediator)
{
    [HttpGet("entries/{id:guid}")]
    public async Task<ActionResult<EntryDto>> Get(Guid id, CancellationToken ct) => await Mediator.Send(new GetEntryQuery(id), ct);

    [HttpPut("entries/{id:guid}")]
    public async Task<ActionResult<EntryDto>> Update(Guid id, UpdateEntryRequest r, CancellationToken ct) =>
        await Mediator.Send(new UpdateEntryCommand(id, r, IfMatch), ct);

    [HttpDelete("entries/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await Mediator.Send(new DeleteEntryCommand(id), ct);
        return NoContent();
    }

    [HttpGet("entries/{id:guid}/audio")]
    public async Task<IActionResult> Audio(Guid id, CancellationToken ct)
    {
        var audio = await Mediator.Send(new StreamAudioQuery(id), ct);
        return File(audio.Stream, audio.ContentType, enableRangeProcessing: true);
    }

    [HttpGet("entries/{id:guid}/transcript")]
    public async Task<ActionResult<TranscriptDto>> Transcript(Guid id, CancellationToken ct) => await Mediator.Send(new GetTranscriptQuery(id), ct);

    [HttpPut("entries/{id:guid}/transcript")]
    public async Task<ActionResult<TranscriptDto>> SaveTranscript(Guid id, SaveTranscriptRequest r, CancellationToken ct) =>
        await Mediator.Send(new SaveTranscriptCommand(id, r.Text), ct);

    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("ai"), HttpPost("entries/{id:guid}/transcribe")]
    public async Task<IActionResult> Transcribe(Guid id, RetranscribeRequest? r, CancellationToken ct)
    {
        var t = await Mediator.Send(new RetranscribeCommand(id, r?.Language), ct);
        return AcceptedJob(id, t);
    }
}

public sealed class AudioUploadsController(ISender mediator) : ApiController(mediator)
{
    [HttpPost("audio-uploads")]
    public async Task<ActionResult<AudioUploadDto>> Init(InitAudioUploadRequest r, CancellationToken ct) =>
        Ok(await Mediator.Send(new InitAudioUploadCommand(r), ct));

    [HttpGet("audio-uploads/{id:guid}")]
    public async Task<ActionResult<AudioUploadDto>> Status(Guid id, CancellationToken ct) => await Mediator.Send(new GetAudioUploadQuery(id), ct);

    [RequestSizeLimit(16 * 1024 * 1024), Consumes("application/octet-stream"), HttpPut("audio-uploads/{id:guid}/chunks/{n:int}")]
    public async Task<ActionResult<AudioUploadDto>> Chunk(Guid id, int n, CancellationToken ct) =>
        await Mediator.Send(new PutAudioChunkCommand(id, n, Request.Body), ct);

    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("ai"), HttpPost("audio-uploads/{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct)
    {
        var upload = await Mediator.Send(new CompleteAudioUploadCommand(id), ct);
        return Accepted(upload);
    }
}

public sealed class SyncController(ISender mediator) : ApiController(mediator)
{
    [HttpPost("sync/push")]
    public async Task<ActionResult<SyncPushResponse>> Push(SyncPushRequest r, CancellationToken ct) => await Mediator.Send(new SyncPushCommand(r), ct);

    [HttpGet("sync/pull")]
    public async Task<ActionResult<SyncPullResponse>> Pull([FromQuery] string? since, [FromQuery] Guid? projectId, [FromQuery] int? pageSize, CancellationToken ct) =>
        await Mediator.Send(new SyncPullQuery(since, projectId, pageSize), ct);
}

public sealed class ReportingController(ISender mediator) : ApiController(mediator)
{
    [HttpGet("jobs/{id:guid}")]
    public async Task<ActionResult<JobDto>> Job(Guid id, CancellationToken ct) => await Mediator.Send(new GetJobQuery(id), ct);

    [HttpGet("usage/ai")]
    public async Task<ActionResult<AiUsageDto>> Usage([FromQuery] Guid? projectId, [FromQuery] string? month, CancellationToken ct) =>
        await Mediator.Send(new GetUsageQuery(projectId, month), ct);

    [HttpGet("audit")]
    public async Task<ActionResult<PagedResult<AuditLogDto>>> Audit(
        [FromQuery] string? entityType, [FromQuery] string? entityId, [FromQuery] Guid? projectId, [FromQuery] DateTime? from,
        [FromQuery] DateTime? to, [FromQuery] Guid? userId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        await Mediator.Send(new GetAuditQuery(entityType, entityId, projectId, from, to, userId, page, pageSize), ct);
}
