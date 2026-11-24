using FlowCapture.Api.Infrastructure;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Features.LibraryFeatures;
using FlowCapture.Application.Services;
using FlowCapture.Domain;
using FlowCapture.Infrastructure.Ai;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowCapture.Api.Controllers;

/// <summary>The simple journey: make a project, add notes (typed, spoken or as photos), analyse, and get the diagram and the implementation guide.</summary>
public sealed class LibraryController(ISender mediator) : ApiController(mediator)
{
    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Lead}"), Idempotent, HttpPost("projects/quick")]
    public async Task<ActionResult<QuickProjectDto>> QuickProject(QuickProjectRequest r, CancellationToken ct) =>
        Created("/api/v1/projects", await Mediator.Send(new CreateQuickProjectCommand(r), ct));

    /// <summary>Every generated document, for every signed-in user.</summary>
    [HttpGet("library")]
    public async Task<ActionResult<IReadOnlyList<LibraryDocumentDto>>> List(CancellationToken ct) => Ok(await Mediator.Send(new ListLibraryQuery(), ct));

    [HttpGet("library/{id:guid}/diagram")]
    public async Task<IActionResult> Diagram(Guid id, [FromQuery] string format = "png", CancellationToken ct = default)
    {
        var file = await Mediator.Send(new GetLibraryDiagramQuery(id, format), ct);
        return File(file.Stream, file.ContentType, file.FileName);
    }

    [Idempotent, Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("ai"), HttpPost("flows/{id:guid}/guide")]
    public async Task<IActionResult> Guide(Guid id, [FromBody] GuideChoiceRequest? choice, CancellationToken ct)
    {
        var job = await Mediator.Send(new GenerateGuideCommand(id, choice), ct);
        return AcceptedJob(job.JobId, job);
    }

    /// <summary>The headings a request's PDF would have and what is under each, so the person can choose before it is made.</summary>
    [HttpGet("flows/{id:guid}/guide-outline")]
    public async Task<ActionResult<GuideOutlineDto>> GuideOutline(Guid id, CancellationToken ct) => Ok(await Mediator.Send(new GuideOutlineQuery(id), ct));

    /// <summary>The same for the PDF of the whole project: the headings, and the analysed requests that can go in it.</summary>
    [HttpGet("projects/{projectId:guid}/guide-outline")]
    public async Task<ActionResult<GuideOutlineDto>> ProjectGuideOutline(Guid projectId, CancellationToken ct) => Ok(await Mediator.Send(new ProjectGuideOutlineQuery(projectId, null), ct));

    /// <summary>The same for the PDF of one module.</summary>
    [HttpGet("projects/{projectId:guid}/modules/{moduleId:guid}/guide-outline")]
    public async Task<ActionResult<GuideOutlineDto>> ModuleGuideOutline(Guid projectId, Guid moduleId, CancellationToken ct) => Ok(await Mediator.Send(new ProjectGuideOutlineQuery(projectId, moduleId), ct));

    /// <summary>One big PDF of the whole project (every analysed request, grouped by module). Uses no AI.</summary>
    [Idempotent, HttpPost("projects/{projectId:guid}/guide")]
    public async Task<IActionResult> ProjectGuide(Guid projectId, [FromBody] GuideChoiceRequest? choice, CancellationToken ct)
    {
        var job = await Mediator.Send(new GenerateProjectGuideCommand(projectId, choice), ct);
        return AcceptedJob(job.JobId, job);
    }

    /// <summary>A smaller PDF of the analysed requests of one module. Uses no AI.</summary>
    [Idempotent, HttpPost("projects/{projectId:guid}/modules/{moduleId:guid}/guide")]
    public async Task<IActionResult> ModuleGuide(Guid projectId, Guid moduleId, [FromBody] GuideChoiceRequest? choice, CancellationToken ct)
    {
        var job = await Mediator.Send(new GenerateModuleGuideCommand(projectId, moduleId, choice), ct);
        return AcceptedJob(job.JobId, job);
    }

    [RequestSizeLimit(12 * 1024 * 1024), Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("ai"), HttpPost("flows/{id:guid}/images")]
    public async Task<ActionResult<EntryDto>> AddImage(Guid id, [FromForm] ImageForm form, CancellationToken ct)
    {
        if (form.File is null || form.File.Length == 0) return BadRequest(new ProblemDetails { Title = "Choose a photo to upload.", Status = 400 });
        await using var stream = form.File.OpenReadStream();
        var entry = await Mediator.Send(new AddImageCommand(id, form.EntryId ?? Guid.NewGuid(), stream, form.File.ContentType, string.IsNullOrWhiteSpace(form.Language) ? "en-IN" : form.Language.Trim(), form.Notes), ct);
        return Created($"/api/v1/entries/{entry.Id}", entry);
    }

    /// <summary>An Excel workbook (.xlsx) or a PDF with text, attached as a note. Read on the server with no AI, so it is not rate limited like photos.</summary>
    [RequestSizeLimit(14 * 1024 * 1024), HttpPost("flows/{id:guid}/files")]
    public async Task<ActionResult<EntryDto>> AddFile(Guid id, [FromForm] ImageForm form, CancellationToken ct)
    {
        if (form.File is null || form.File.Length == 0) return BadRequest(new ProblemDetails { Title = "Choose a file to attach.", Status = 400 });
        await using var stream = form.File.OpenReadStream();
        var entry = await Mediator.Send(new AddFileNoteCommand(id, form.EntryId ?? Guid.NewGuid(), stream, form.File.FileName, string.IsNullOrWhiteSpace(form.Language) ? "en-IN" : form.Language.Trim()), ct);
        return Created($"/api/v1/entries/{entry.Id}", entry);
    }

    /// <summary>Tokens used by the AI in a month. An administrator sees everyone's, anybody else their own.</summary>
    [HttpGet("ai-usage")]
    public async Task<ActionResult<AiUsageSummaryDto>> AiUsage([FromQuery] string? month, CancellationToken ct) =>
        await Mediator.Send(new FlowCapture.Application.Features.AiUsageFeatures.GetAiUsageSummaryQuery(month), ct);

    [HttpGet("entries/{id:guid}/image")]
    public async Task<IActionResult> Image(Guid id, CancellationToken ct)
    {
        var image = await Mediator.Send(new StreamImageQuery(id), ct);
        return File(image.Stream, image.ContentType);
    }
}

public sealed class ImageForm
{
    public IFormFile? File { get; set; }
    public Guid? EntryId { get; set; }
    public string? Language { get; set; }
    public string? Notes { get; set; }
}

/// <summary>What an administrator sees and changes in the Admin dashboard: the numbers, and the free AI account.</summary>
[Authorize(Roles = AppRoles.Admin)]
public sealed class AdminDashboardController(ISender mediator, ISettingsService settings, GeminiClient gemini, GroqClient groq, IAiQuota quota, IModelQuota modelQuota, IAiStatusService aiStatus,
    FlowCapture.Application.Services.AiUsageRecorder usage, FlowCapture.Application.Abstractions.ICurrentUser me) : ApiController(mediator)
{
    [HttpGet("admin/overview")]
    public async Task<ActionResult<AdminOverviewDto>> Overview(CancellationToken ct) => await Mediator.Send(new GetAdminOverviewQuery(), ct);

    /// <summary>Who handles which client, with the work and the final documents of each.</summary>
    [HttpGet("admin/handling")]
    public async Task<ActionResult<IReadOnlyList<HandlingRowDto>>> Handling(CancellationToken ct) => Ok(await Mediator.Send(new FlowCapture.Application.Features.HandlingFeatures.GetHandlingQuery(), ct));

    [HttpGet("admin/ai")]
    public async Task<ActionResult<AiAdminDto>> GetAi(CancellationToken ct) => await Load(ct);

    [HttpPut("admin/ai")]
    public async Task<ActionResult<AiAdminDto>> SaveAi(SaveAiAdminRequest r, CancellationToken ct)
    {
        if (r.Provider is not null) await settings.SetAsync("Ai.Provider", r.Provider, ct);
        if (r.GeminiModel is not null) await settings.SetAsync("Ai.GeminiModel", r.GeminiModel.Trim(), ct);
        if (r.GeminiApiKey is not null) await settings.SetSecretAsync(SettingsService.GeminiKeySetting, r.GeminiApiKey, ct);
        if (r.GroqApiKey is not null) await settings.SetSecretAsync(SettingsService.GroqKeySetting, r.GroqApiKey, ct);
        if (r.VoiceProvider is not null) await settings.SetAsync("Ai.VoiceProvider", r.VoiceProvider.Trim(), ct);
        foreach (var (name, value) in r.Limits ?? new Dictionary<string, long>())
        {
            var key = "Ai.Limit." + name;
            if (!AiQuotaService.AllLimitKeys.Contains(key)) throw new DomainException($"Unknown limit '{name}'.", "unknown_setting");
            if (value < 0) throw new DomainException("A limit cannot be negative. Use 0 for no limit.", "invalid_limit");
            await settings.SetAsync(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        }
        foreach (var (name, value) in r.ModelLimits ?? new Dictionary<string, long>())
        {
            var split = name.LastIndexOf('.');
            var key = split > 0 ? ModelQuotaService.KeyFor(name[..split], name[(split + 1)..]) : "";
            if (!ModelQuotaService.IsModelLimitKey(key)) throw new DomainException($"Unknown model limit '{name}'.", "unknown_setting");
            if (value < 0) throw new DomainException("A limit cannot be negative. Use 0 for no limit.", "invalid_limit");
            await settings.SetAsync(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        }
        if (r.AiWording is { } wording) await settings.SetAsync(SettingsService.FlagAiWording, wording ? "true" : "false", ct);
        if (r.AllowMedia is { } media) await settings.SetAsync(SettingsService.FlagAllowMedia, media ? "true" : "false", ct);
        return await Load(ct);
    }

    /// <summary>Checks the saved Gemini key: lists the models it can use and asks one for a short answer. Never returns the key.</summary>
    [HttpPost("admin/ai/test")]
    public async Task<ActionResult<AiTestResultDto>> Test(CancellationToken ct)
    {
        var s = await settings.GetProviderAsync(ct);
        if (string.IsNullOrWhiteSpace(s.GeminiApiKey)) return new AiTestResultDto(false, "No Gemini key has been saved yet.", null, []);

        IReadOnlyList<AiModelDto> models = [];
        try
        {
            models = (await gemini.ListModelsAsync(s.GeminiApiKey, ct)).Select(m => new AiModelDto(m.Id, m.DisplayName)).ToList();
            // every model the app would use is asked at the same moment, so the admin sees which ones work right now and how fast
            var probes = await gemini.ProbeAsync(await gemini.CandidateModelsAsync(ct), ct);
            foreach (var p in probes.Where(p => p.Ok))
                await usage.RecordAsync(FlowCapture.Domain.AiUsageKind.Test, me.UserId, null, null, "Gemini", p.Model, p.InputTokens, p.OutputTokens, p.Milliseconds, true, ct);
            var shown = probes.Select(p => new AiProbeDto(p.Model, p.Ok, p.Milliseconds, p.Message)).ToList();
            var working = probes.Where(p => p.Ok).OrderBy(p => p.Milliseconds).ToList();
            return working.Count == 0
                ? new AiTestResultDto(false, "The key is accepted, but no model answered just now: Google's free service is busy or limited. Wait a few minutes and test again.", null, models, shown)
                : new AiTestResultDto(true, $"It works. {working.Count} of {probes.Count} model{(probes.Count == 1 ? "" : "s")} answered; the fastest was {working[0].Model} ({working[0].Milliseconds / 1000.0:0.0} s)."
                    + (working.Count < probes.Count ? " Analyses use the working ones and skip the others while they are busy." : ""), working[0].Model, models, shown);
        }
        catch (AiServiceException e)
        {
            return new AiTestResultDto(false, e.Message, null, models);
        }
    }

    /// <summary>Checks the saved Groq key by asking which models it can use. This spends none of the free allowance. Never returns the key.</summary>
    [HttpPost("admin/ai/test-groq")]
    public async Task<ActionResult<AiTestResultDto>> TestGroq(CancellationToken ct)
    {
        try
        {
            var n = await groq.CountModelsAsync(ct);
            var g = await settings.GetGroqAsync(ct);
            return new AiTestResultDto(true, $"The Groq key works ({n} models available). Voice notes are turned into text with {g.SttModel}.", g.SttModel, []);
        }
        catch (GroqUnavailableException e)
        {
            return new AiTestResultDto(false, e.Message, null, []);
        }
    }

    private async Task<AiAdminDto> Load(CancellationToken ct)
    {
        var g = await settings.GetGroqAsync(ct);
        var groqHint = g.ApiKey is null ? null : "..." + g.ApiKey[^Math.Min(4, g.ApiKey.Length)..];
        var limits = GroqClient.Limits.Values.OrderBy(l => l.Model).SelectMany(l => new[]
        {
            l.RequestsLimit is null ? null : new AiLimitDto("Groq", l.Model, "requests per day", l.RequestsLimit, l.RequestsRemaining, l.RequestsReset, l.At),
            l.TokensLimit is null ? null : new AiLimitDto("Groq", l.Model, "tokens per minute", l.TokensLimit, l.TokensRemaining, l.TokensReset, l.At),
        }).Where(x => x is not null).Select(x => x!).ToList();
        var s = await settings.GetProviderAsync(ct);
        var hint = string.IsNullOrWhiteSpace(s.GeminiApiKey) ? null : "..." + s.GeminiApiKey[^Math.Min(4, s.GeminiApiKey.Length)..];
        _ = await aiStatus.GetAsync(ct);
        return new AiAdminDto(s.Provider, SettingsService.Providers, hint is not null, hint, s.GeminiModel, s.KeyFromDashboard,
            await settings.GetFlagAsync(SettingsService.FlagAiWording, false, ct), await settings.GetFlagAsync(SettingsService.FlagAllowMedia, true, ct),
            g.ApiKey is not null, groqHint, g.UseForVoice ? "Groq" : "Gemini", g.SttModel, limits,
            (await quota.GetAllAsync(ct)).Select(q => new AiQuotaDto(q.Provider, q.UsedRequests, q.UsedTokens, q.LimitRequests, q.LimitTokens, q.ResetsAtUtc, q.Reached, q.Reached ? q.Message : null)).ToList(),
            (await modelQuota.GetKnownAsync(ct)).Select(m => m.ToDto()).ToList());
    }
}
