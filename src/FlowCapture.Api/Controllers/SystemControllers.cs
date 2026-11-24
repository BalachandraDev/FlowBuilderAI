using FlowCapture.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace FlowCapture.Api.Controllers;

public sealed record SystemInfoDto(string Ai, string? Notice, bool AllowMedia = true, FlowCapture.Shared.Dtos.AiCapacityDto? Capacity = null);

/// <summary>What the apps need to know about how the server is set up, for any signed-in user.</summary>
public sealed class SystemController(IAiStatusService status, FlowCapture.Application.Services.ISettingsService settings, FlowCapture.Application.Services.IModelQuota quota) : ControllerBase
{
    /// <summary>"gemini" or "anthropic" (real analysis), "no-key" (analysis will fail until a key is set) or "demo" (a fixed example, not a real analysis).</summary>
    [HttpGet("api/v1/system/info")]
    public async Task<ActionResult<SystemInfoDto>> Info(CancellationToken ct)
    {
        var s = await status.GetAsync(ct);
        // how much of the free Gemini allowance is left today: everybody can see it, so nobody is surprised by a refusal
        var capacity = s.Provider == "gemini" ? FlowCapture.Application.Services.ModelQuotaMapping.ToDto(await quota.GetCapacityAsync(ct)) : null;
        return new SystemInfoDto(s.Provider, s.Notice, await settings.GetFlagAsync(FlowCapture.Application.Services.SettingsService.FlagAllowMedia, true, ct), capacity);
    }
}
