using FlowCapture.Api.Infrastructure;
using FlowCapture.Application.Features.AnalysisFeatures;
using FlowCapture.Application.Features.FlowFeatures;
using FlowCapture.Application.Features.Projects;
using FlowCapture.Application.Features.ReportingFeatures;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowCapture.Api.Controllers;

public sealed class ProjectsController(ISender mediator) : ApiController(mediator)
{
    [HttpGet("projects")]
    public async Task<ActionResult<IReadOnlyList<ProjectDto>>> List(CancellationToken ct) => Ok(await Mediator.Send(new ListProjectsQuery(), ct));

    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Lead}"), Idempotent, HttpPost("projects")]
    public async Task<ActionResult<ProjectDto>> Create(CreateProjectRequest r, CancellationToken ct)
    {
        var p = await Mediator.Send(new CreateProjectCommand(r), ct);
        return Created($"/api/v1/projects/{p.Id}", p);
    }

    [HttpGet("projects/{id:guid}")]
    public async Task<ActionResult<ProjectDto>> Get(Guid id, CancellationToken ct) => await Mediator.Send(new GetProjectQuery(id), ct);

    /// <summary>Deletes the project (hides it everywhere; nothing is erased). The project's lead or an administrator.</summary>
    [HttpDelete("projects/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await Mediator.Send(new DeleteProjectCommand(id), ct);
        return NoContent();
    }

    [HttpPut("projects/{id:guid}")]
    public async Task<ActionResult<ProjectDto>> Update(Guid id, UpdateProjectRequest r, CancellationToken ct) =>
        await Mediator.Send(new UpdateProjectCommand(id, r, IfMatch), ct);

    [HttpGet("projects/{id:guid}/members")]
    public async Task<ActionResult<IReadOnlyList<ProjectMemberDto>>> Members(Guid id, CancellationToken ct) => Ok(await Mediator.Send(new GetMembersQuery(id), ct));

    [HttpPut("projects/{id:guid}/members")]
    public async Task<ActionResult<IReadOnlyList<ProjectMemberDto>>> SetMembers(Guid id, SetMembersRequest r, CancellationToken ct) =>
        Ok(await Mediator.Send(new SetMembersCommand(id, r), ct));

    [HttpGet("projects/{id:guid}/departments")]
    public async Task<ActionResult<IReadOnlyList<ProjectDepartmentDto>>> Departments(Guid id, CancellationToken ct) =>
        Ok(await Mediator.Send(new ListProjectDepartmentsQuery(id), ct));

    [HttpPost("projects/{id:guid}/departments")]
    public async Task<ActionResult<ProjectDepartmentDto>> AddDepartment(Guid id, AddProjectDepartmentRequest r, CancellationToken ct) =>
        Created($"/api/v1/projects/{id}/departments", await Mediator.Send(new AddProjectDepartmentCommand(id, r), ct));

    [HttpPut("projects/{id:guid}/departments/{pdId:guid}/assignments")]
    public async Task<ActionResult<ProjectDepartmentDto>> SetAssignments(Guid id, Guid pdId, SetAssignmentsRequest r, CancellationToken ct) =>
        await Mediator.Send(new SetAssignmentsCommand(id, pdId, r), ct);

    [HttpGet("projects/{id:guid}/dashboard")]
    public async Task<ActionResult<DashboardDto>> Dashboard(Guid id, CancellationToken ct) => await Mediator.Send(new GetDashboardQuery(id), ct);

    [HttpGet("projects/{id:guid}/pending")]
    public async Task<ActionResult<IReadOnlyList<PendingItemDto>>> Pending(Guid id, CancellationToken ct) => Ok(await Mediator.Send(new GetPendingQuery(id), ct));

    // ---- flows of a project
    [HttpGet("projects/{id:guid}/flows")]
    public async Task<ActionResult<IReadOnlyList<FlowDto>>> Flows(Guid id, [FromQuery] Guid? departmentId, [FromQuery] string? status, CancellationToken ct) =>
        Ok(await Mediator.Send(new ListFlowsQuery(id, departmentId, status), ct));

    [Idempotent, HttpPost("projects/{id:guid}/flows")]
    public async Task<ActionResult<FlowDto>> CreateFlow(Guid id, CreateFlowRequest r, CancellationToken ct)
    {
        var result = await Mediator.Send(new CreateFlowCommand(id, r), ct);
        return result.Created ? Created($"/api/v1/flows/{result.Flow.Id}", result.Flow) : Ok(result.Flow);
    }

    // ---- project-level analysis
    [HttpPost("projects/{id:guid}/flows:analyse-department")]
    public async Task<IActionResult> AnalyseDepartment(Guid id, StartDepartmentAnalysisRequest r, CancellationToken ct)
    {
        var result = await Mediator.Send(new StartDepartmentAnalysisCommand(id, r.ProjectDepartmentId), ct);
        return Accepted(result);
    }

    [Idempotent, HttpPost("projects/{id:guid}/analyses")]
    public async Task<IActionResult> AnalyseProject(Guid id, StartProjectAnalysisRequest? r, CancellationToken ct)
    {
        var run = await Mediator.Send(new StartWholeAnalysisCommand(id, r?.FlowIds), ct);
        return AcceptedJob(run.Id, run);
    }

    [HttpGet("projects/{id:guid}/cross-links")]
    public async Task<ActionResult<IReadOnlyList<CrossFlowLinkDto>>> Links(Guid id, [FromQuery] string? status, CancellationToken ct) =>
        Ok(await Mediator.Send(new ListLinksQuery(id, status), ct));
}

public sealed class CrossLinksController(ISender mediator) : ApiController(mediator)
{
    [HttpPut("cross-links/{id:guid}")]
    public async Task<ActionResult<CrossFlowLinkDto>> Decide(Guid id, DecideLinkRequest r, CancellationToken ct) =>
        await Mediator.Send(new DecideLinkCommand(id, r.Status), ct);
}
