using System.Security.Claims;
using FlowCapture.Application.Abstractions;

namespace FlowCapture.Api.Infrastructure;

/// <summary>The signed-in user of the current HTTP request. Outside a request (background jobs) nobody is signed in.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue("sub") ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;
    public string? CorrelationId => accessor.HttpContext?.Items[CorrelationIdMiddleware.ItemKey] as string;
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}
