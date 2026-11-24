using FlowCapture.Application.Features.Admin;
using FlowCapture.Application.Features.Auth;
using FlowCapture.Application.Features.Clients;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FlowCapture.Api.Controllers;

[ApiController, Authorize, Route("api/v1")]
public abstract class ApiController(ISender mediator) : ControllerBase
{
    protected ISender Mediator { get; } = mediator;

    /// <summary>Value of the If-Match header without quotes, or null.</summary>
    protected string? IfMatch => Request.Headers.IfMatch.FirstOrDefault()?.Trim('"');

    protected AcceptedResult AcceptedJob(Guid jobId, object body) => Accepted($"/api/v1/jobs/{jobId}", body);
}

public sealed class AuthController(ISender mediator) : ApiController(mediator)
{
    [AllowAnonymous, EnableRateLimiting("auth"), HttpPost("auth/login")]
    public async Task<ActionResult<TokenResponse>> Login(LoginRequest r, CancellationToken ct) =>
        await Mediator.Send(new LoginCommand(r.Email, r.Password, r.DeviceId), ct);

    [AllowAnonymous, EnableRateLimiting("auth"), HttpPost("auth/refresh")]
    public async Task<ActionResult<TokenResponse>> Refresh(RefreshRequest r, CancellationToken ct) =>
        await Mediator.Send(new RefreshCommand(r.RefreshToken), ct);

    [HttpPost("auth/logout")]
    public async Task<IActionResult> Logout(LogoutRequest r, CancellationToken ct)
    {
        await Mediator.Send(new LogoutCommand(r.RefreshToken), ct);
        return NoContent();
    }

    /// <summary>Everybody, administrator or user, changes their own password here. Rate limited like sign-in, because it checks the current password.</summary>
    [EnableRateLimiting("auth"), HttpPost("auth/change-password")]
    public async Task<ActionResult<TokenResponse>> ChangePassword(ChangePasswordRequest r, [FromQuery] string? deviceId, CancellationToken ct) =>
        await Mediator.Send(new ChangePasswordCommand(r, deviceId), ct);

    [HttpGet("auth/me")]
    public async Task<ActionResult<UserProfileDto>> Me(CancellationToken ct) => await Mediator.Send(new MeQuery(), ct);

    [HttpPut("devices")]
    public async Task<IActionResult> RegisterDevice(RegisterDeviceRequest r, CancellationToken ct)
    {
        await Mediator.Send(new RegisterDeviceCommand(r.Platform, r.PushToken), ct);
        return NoContent();
    }
}

public sealed class UsersController(ISender mediator) : ApiController(mediator)
{
    // only the administrator manages accounts: a user has no need to list everybody's email and role
    [Authorize(Roles = AppRoles.Admin), HttpGet("users")]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> List(CancellationToken ct) => Ok(await Mediator.Send(new ListUsersQuery(), ct));

    [Authorize(Roles = AppRoles.Admin), HttpPost("users")]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest r, CancellationToken ct)
    {
        var user = await Mediator.Send(new CreateUserCommand(r), ct);
        return Created($"/api/v1/users/{user.Id}", user);
    }

    [Authorize(Roles = AppRoles.Admin), HttpPut("users/{id:guid}")]
    public async Task<ActionResult<UserDto>> Update(Guid id, UpdateUserRequest r, CancellationToken ct) =>
        await Mediator.Send(new UpdateUserCommand(id, r), ct);

    /// <summary>Only an administrator deletes a user. Nothing is erased (see DeleteUserCommand).</summary>
    [Authorize(Roles = AppRoles.Admin), HttpDelete("users/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await Mediator.Send(new DeleteUserCommand(id), ct);
        return NoContent();
    }

    [Authorize(Roles = AppRoles.Admin), HttpPost("users/{id:guid}/reset-password")]
    public async Task<ActionResult<ResetPasswordResponse>> ResetPassword(Guid id, CancellationToken ct) =>
        await Mediator.Send(new ResetPasswordCommand(id), ct);
}

/// <summary>Clients (customers). Everybody signed in can list the clients they may use; only the administrator makes and changes them and assigns people.</summary>
public sealed class ClientsController(ISender mediator) : ApiController(mediator)
{
    [HttpGet("clients")]
    public async Task<ActionResult<IReadOnlyList<ClientDto>>> List(CancellationToken ct) => Ok(await Mediator.Send(new ListClientsQuery(), ct));

    [Authorize(Roles = AppRoles.Admin), HttpPost("clients")]
    public async Task<ActionResult<ClientDto>> Create(CreateClientRequest r, CancellationToken ct)
    {
        var client = await Mediator.Send(new CreateClientCommand(r), ct);
        return Created($"/api/v1/clients/{client.Id}", client);
    }

    [Authorize(Roles = AppRoles.Admin), HttpPut("clients/{id:guid}")]
    public async Task<ActionResult<ClientDto>> Update(Guid id, UpdateClientRequest r, CancellationToken ct) =>
        await Mediator.Send(new UpdateClientCommand(id, r), ct);

    [Authorize(Roles = AppRoles.Admin), HttpPut("clients/{id:guid}/users")]
    public async Task<ActionResult<ClientDto>> SetUsers(Guid id, SetClientUsersRequest r, CancellationToken ct) =>
        await Mediator.Send(new SetClientUsersCommand(id, r), ct);
}

public sealed class MasterDataController(ISender mediator) : ApiController(mediator)
{
    [HttpGet("master/departments")]
    public async Task<ActionResult<IReadOnlyList<DepartmentDto>>> Departments(CancellationToken ct) => Ok(await Mediator.Send(new ListDepartmentsQuery(), ct));

    [Authorize(Roles = AppRoles.Admin), HttpPost("master/departments")]
    public async Task<ActionResult<DepartmentDto>> CreateDepartment(UpsertDepartmentRequest r, CancellationToken ct) =>
        Created("/api/v1/master/departments", await Mediator.Send(new UpsertDepartmentCommand(null, r), ct));

    [Authorize(Roles = AppRoles.Admin), HttpPut("master/departments/{id:guid}")]
    public async Task<ActionResult<DepartmentDto>> UpdateDepartment(Guid id, UpsertDepartmentRequest r, CancellationToken ct) =>
        await Mediator.Send(new UpsertDepartmentCommand(id, r), ct);

    [HttpGet("master/requirement-categories")]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> Categories(CancellationToken ct) => Ok(await Mediator.Send(new ListCategoriesQuery(), ct));

    [Authorize(Roles = AppRoles.Admin), HttpPut("master/requirement-categories")]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> PutCategories(List<CategoryDto> items, CancellationToken ct) =>
        Ok(await Mediator.Send(new PutCategoriesCommand(items), ct));

    [HttpGet("master/languages")]
    public async Task<ActionResult<IReadOnlyList<LanguageDto>>> Languages(CancellationToken ct) => Ok(await Mediator.Send(new ListLanguagesQuery(), ct));

    [Authorize(Roles = AppRoles.Admin), HttpPut("master/languages")]
    public async Task<ActionResult<IReadOnlyList<LanguageDto>>> PutLanguages(List<LanguageDto> items, CancellationToken ct) =>
        Ok(await Mediator.Send(new PutLanguagesCommand(items), ct));
}

[Authorize(Roles = AppRoles.Admin)]
public sealed class AdminController(ISender mediator) : ApiController(mediator)
{
    [HttpGet("settings")]
    public async Task<ActionResult<IReadOnlyList<SettingDto>>> GetSettings(CancellationToken ct) => Ok(await Mediator.Send(new GetSettingsQuery(), ct));

    [HttpPut("settings")]
    public async Task<ActionResult<IReadOnlyList<SettingDto>>> PutSettings(List<SettingDto> items, CancellationToken ct) =>
        Ok(await Mediator.Send(new PutSettingsCommand(items), ct));

    [HttpGet("admin/prompts")]
    public async Task<ActionResult<IReadOnlyList<PromptTemplateDto>>> Prompts([FromQuery] bool includeContent, CancellationToken ct) =>
        Ok(await Mediator.Send(new ListPromptsQuery(includeContent), ct));

    [HttpPost("admin/prompts")]
    public async Task<ActionResult<PromptTemplateDto>> CreatePrompt(CreatePromptRequest r, CancellationToken ct) =>
        Created("/api/v1/admin/prompts", await Mediator.Send(new CreatePromptCommand(r), ct));

    /// <summary>Runs the housekeeping now: frees interrupted jobs and removes expired tokens and stored responses.</summary>
    [HttpPost("admin/maintenance/run")]
    public async Task<ActionResult<FlowCapture.Infrastructure.Maintenance.MaintenanceReport>> RunMaintenance([FromServices] FlowCapture.Infrastructure.Maintenance.MaintenanceService service, CancellationToken ct) =>
        Ok(await service.RunAsync(ct));

    [HttpPost("admin/prompts/{id:guid}/activate")]
    public async Task<ActionResult<PromptTemplateDto>> Activate(Guid id, CancellationToken ct) =>
        await Mediator.Send(new ActivatePromptCommand(id), ct);
}
