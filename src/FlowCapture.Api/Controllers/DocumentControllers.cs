using FlowCapture.Api.Infrastructure;
using FlowCapture.Application.Features.DocumentFeatures;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowCapture.Api.Controllers;

public sealed class DocumentsController(ISender mediator) : ApiController(mediator)
{
    [Idempotent, HttpPost("flows/{id:guid}/documents")]
    public async Task<IActionResult> GenerateForFlow(Guid id, GenerateDocumentRequest? r, CancellationToken ct)
    {
        var job = await Mediator.Send(new GenerateFlowDocumentCommand(id, r ?? new GenerateDocumentRequest()), ct);
        return AcceptedJob(job.JobId, job);
    }

    [Idempotent, HttpPost("projects/{id:guid}/documents")]
    public async Task<IActionResult> GenerateForProject(Guid id, GenerateProjectDocumentRequest? r, CancellationToken ct)
    {
        var job = await Mediator.Send(new GenerateProjectDocumentCommand(id, r ?? new GenerateProjectDocumentRequest()), ct);
        return AcceptedJob(job.JobId, job);
    }

    [HttpGet("projects/{id:guid}/documents")]
    public async Task<ActionResult<IReadOnlyList<DocumentDto>>> ForProject(Guid id, CancellationToken ct) => Ok(await Mediator.Send(new ListProjectDocumentsQuery(id), ct));

    [HttpGet("flows/{id:guid}/documents")]
    public async Task<ActionResult<IReadOnlyList<DocumentDto>>> ForFlow(Guid id, CancellationToken ct) => Ok(await Mediator.Send(new ListFlowDocumentsQuery(id), ct));

    [HttpGet("documents/{id:guid}")]
    public async Task<ActionResult<DocumentDto>> Get(Guid id, CancellationToken ct) => await Mediator.Send(new GetDocumentQuery(id), ct);

    [HttpGet("documents/{id:guid}/versions/{n:int}/download")]
    public async Task<IActionResult> Download(Guid id, int n, [FromQuery] string format = "docx", CancellationToken ct = default)
    {
        var file = await Mediator.Send(new DownloadDocumentQuery(id, n, format), ct);
        return File(file.Stream, file.ContentType, file.FileName);
    }

    /// <summary>The administrator verifies a final document or returns it to its maker with the reason.</summary>
    [HttpPost("documents/{id:guid}/versions/{n:int}/approve")]
    public async Task<ActionResult<DocumentDto>> Approve(Guid id, int n, ApproveDocumentRequest r, CancellationToken ct) =>
        await Mediator.Send(new ApproveDocumentCommand(id, n, r), ct);

    /// <summary>A user marks the latest version as final, to give to the team (the administrator then verifies it).</summary>
    [HttpPost("documents/{id:guid}/versions/{n:int}/final")]
    public async Task<ActionResult<DocumentDto>> MarkFinal(Guid id, int n, MarkFinalRequest? r, CancellationToken ct) =>
        await Mediator.Send(new MarkFinalCommand(id, n, r?.Note), ct);

    /// <summary>Takes the final mark back while the administrator has not decided.</summary>
    [HttpDelete("documents/{id:guid}/versions/{n:int}/final")]
    public async Task<ActionResult<DocumentDto>> WithdrawFinal(Guid id, int n, CancellationToken ct) =>
        await Mediator.Send(new WithdrawFinalCommand(id, n), ct);
}

public sealed class DiagramsController(ISender mediator) : ApiController(mediator)
{
    [HttpGet("flows/{id:guid}/diagram")]
    public async Task<IActionResult> Flow(Guid id, [FromQuery] string format = "svg", [FromQuery] int? versionNo = null, CancellationToken ct = default)
    {
        var file = await Mediator.Send(new GetFlowDiagramQuery(id, format, versionNo), ct);
        return File(file.Stream, file.ContentType, file.FileName);
    }

    [HttpPost("flows/{id:guid}/diagram/regenerate")]
    public async Task<ActionResult<DiagramInfoDto>> Regenerate(Guid id, CancellationToken ct) => await Mediator.Send(new RegenerateFlowDiagramCommand(id), ct);

    [HttpGet("projects/{id:guid}/diagram/combined")]
    public async Task<IActionResult> Combined(Guid id, [FromQuery] string format = "svg", CancellationToken ct = default)
    {
        var file = await Mediator.Send(new GetCombinedDiagramQuery(id, format), ct);
        return File(file.Stream, file.ContentType, file.FileName);
    }

    [HttpPost("projects/{id:guid}/diagram/combined")]
    public async Task<ActionResult<DiagramInfoDto>> GenerateCombined(Guid id, CancellationToken ct) => await Mediator.Send(new GenerateCombinedDiagramCommand(id), ct);
}

[Authorize(Roles = AppRoles.Admin)]
public sealed class DocumentTemplatesController(ISender mediator) : ApiController(mediator)
{
    [HttpGet("admin/document-templates")]
    public async Task<ActionResult<IReadOnlyList<DocumentTemplateDto>>> List(CancellationToken ct) => Ok(await Mediator.Send(new ListTemplatesQuery(), ct));

    [RequestSizeLimit(11 * 1024 * 1024), HttpPost("admin/document-templates")]
    public async Task<ActionResult<DocumentTemplateDto>> Upload(IFormFile file, [FromForm] string name, [FromForm] string? version, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var created = await Mediator.Send(new UploadTemplateCommand(name, version, stream, file.Length), ct);
        return Created($"/api/v1/admin/document-templates/{created.Id}", created);
    }

    [HttpPost("admin/document-templates/{id:guid}/activate")]
    public async Task<ActionResult<DocumentTemplateDto>> Activate(Guid id, CancellationToken ct) => await Mediator.Send(new ActivateTemplateCommand(id), ct);
}
