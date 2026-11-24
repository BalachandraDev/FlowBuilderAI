using FlowCapture.Infrastructure.Persistence;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowCapture.Api.Controllers;

/// <summary>Which SQL Server the app uses, and moving it to another one. Administrator only. The password is never returned.</summary>
[Authorize(Roles = AppRoles.Admin)]
public sealed class DatabaseController(ISender mediator, DatabaseMoveService mover) : ApiController(mediator)
{
    [HttpGet("admin/database")]
    public ActionResult<DbInfoDto> Info() => mover.Info();

    /// <summary>Checks the server and says which tables exist and which would be made. Changes nothing.</summary>
    [HttpPost("admin/database/test")]
    public async Task<ActionResult<DbTestDto>> Test(DbDetailsRequest r, CancellationToken ct) => await mover.TestAsync(r, ct);

    /// <summary>Makes the missing tables on the new server and copies the data across, in the background. Nothing on the current server is changed.</summary>
    [HttpPost("admin/database/move")]
    public ActionResult<DbMoveDto> Move(DbDetailsRequest r) => Accepted(mover.Start(r));

    [HttpGet("admin/database/move")]
    public ActionResult<DbMoveDto> Progress() => mover.Progress();

    /// <summary>Saves the server that was last tested or moved to; the app uses it from its next start.</summary>
    [HttpPost("admin/database/use")]
    public ActionResult<DbUseDto> Use() => mover.Use();

    /// <summary>Forgets the saved server; the app goes back to the database it was installed with at its next start.</summary>
    [HttpDelete("admin/database/use")]
    public ActionResult<DbUseDto> Forget() => mover.Forget();
}
