using FlowCapture.Application.Documents;
using FlowCapture.Shared.Dtos;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace FlowCapture.Api.Controllers;

/// <summary>Pictures (screenshots, photos of forms) attached to a request. They are printed in its implementation document.</summary>
public sealed class FiguresController(ISender mediator, FigureService figures) : ApiController(mediator)
{
    [HttpGet("flows/{id:guid}/figures")]
    public async Task<ActionResult<IReadOnlyList<FigureDto>>> List(Guid id, CancellationToken ct) => Ok(await figures.ListAsync(id, ct));

    [RequestSizeLimit(12 * 1024 * 1024), HttpPost("flows/{id:guid}/figures")]
    public async Task<ActionResult<FigureDto>> Add(Guid id, [FromForm] FigureForm form, CancellationToken ct)
    {
        if (form.File is null || form.File.Length == 0) return BadRequest(new ProblemDetails { Title = "Choose a picture to attach.", Status = 400 });
        await using var stream = form.File.OpenReadStream();
        var made = await figures.AddAsync(id, stream, form.File.FileName, form.Caption, form.StepId, ct);
        return Created($"/api/v1/figures/{made.Id}/image", made);
    }

    [HttpPut("figures/{id:guid}")]
    public async Task<ActionResult<FigureDto>> Update(Guid id, UpdateFigureRequest r, CancellationToken ct) => Ok(await figures.UpdateAsync(id, r.Caption, r.StepId, ct));

    [HttpPost("figures/{id:guid}/move")]
    public async Task<ActionResult<IReadOnlyList<FigureDto>>> Move(Guid id, [FromQuery] int direction, CancellationToken ct) => Ok(await figures.MoveAsync(id, direction, ct));

    [HttpDelete("figures/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await figures.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("figures/{id:guid}/image")]
    public async Task<IActionResult> Image(Guid id, CancellationToken ct)
    {
        var (stream, type) = await figures.OpenAsync(id, ct);
        return File(stream, type);
    }
}

public sealed class FigureForm
{
    public IFormFile? File { get; set; }
    public string? Caption { get; set; }
    public string? StepId { get; set; }
}