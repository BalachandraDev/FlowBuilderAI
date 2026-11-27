using System.Text.Json;
using System.Text.Json.Nodes;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Application.Services;
using FlowCapture.Domain;
using FlowCapture.Shared.Content;
using FlowCapture.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Application.Documents;

/// <summary>
/// Makes the implementation document (PDF) for one analysed flow: a cover like the company's requirement documents, the flow explained
/// stage by stage with its diagram, where it touches the system, the rules and requirements, and what is still open. It explains the
/// flow and its touch points; it is not a development plan. Runs in a background job. Every run adds a new version of the same
/// document, and the older versions stay downloadable. Every AI call is written to the AI usage page.
/// </summary>
public sealed class GuideGenerationService(
    IAppDbContext db, IClock clock, IBlobStore blobs, DiagramService diagrams, DocumentNumbering numbering, ISettingsService settings,
    IFlowAnalysisService ai, IHtmlPdfRenderer pdf, IAiStatusService aiStatus, AiUsageRecorder usage, IPushSender push, FigureService figures, CombinedGuideService combined,
    ILogger<GuideGenerationService> log)
{
    public const string ToolName = "submit_implementation_plan";

    public async Task ExecuteAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.DocumentJobRuns.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status != DocJobStatus.Queued) return;
        job.Status = DocJobStatus.Running;
        job.StartedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        var requestedBy = job.RequestedBy;
        string? code = null, detail = null;
        (Guid DocumentId, int VersionNo)? result = null;
        try { result = await GenerateAsync(job, ct); }
        catch (AiServiceException e) { code = e.Code; detail = e.Message; }
        catch (DependencyUnavailableException e) { code = "dependency_unavailable"; detail = e.Message; }
        catch (DomainException e) { code = e.Code; detail = e.Message; }
        catch (Exception e)
        {
            log.LogError(e, "Guide job {JobId} failed", jobId);
            code = "unexpected_error";
            detail = "The document could not be generated. The error has been logged; try again.";
        }

        db.ChangeTracker.Clear();
        job = await db.DocumentJobRuns.FirstAsync(j => j.Id == jobId, CancellationToken.None);
        job.FinishedAt = clock.UtcNow;
        if (result is { } r) { job.Status = DocJobStatus.Succeeded; job.ResultDocumentId = r.DocumentId; job.ResultVersionNo = r.VersionNo; }
        else { job.Status = DocJobStatus.Failed; job.ErrorCode = code; job.ErrorDetail = detail; }
        await db.SaveChangesAsync(CancellationToken.None);

        try
        {
            await push.SendToUserAsync(requestedBy, result is null ? "Document failed" : "Document ready",
                result is null ? "The implementation document could not be generated." : "The implementation document is ready to download.",
                new Dictionary<string, string> { ["type"] = "document", ["jobId"] = jobId.ToString() }, CancellationToken.None);
        }
        catch (Exception e) { log.LogWarning(e, "Push notification for guide job {JobId} failed", jobId); }
    }

    private async Task<(Guid, int)> GenerateAsync(DocumentJobRun job, CancellationToken ct)
    {
        // the PDF of a whole project or of one module is made by its own service, from the same analysed workflows
        if (job.Scope is DocumentScope.GuideModule or DocumentScope.GuideProject) return await combined.GenerateAsync(job, ct);

        var flow = await db.Flows.Include(f => f.ProjectDepartment!).ThenInclude(p => p.Project!).ThenInclude(p => p.Lead)
            .Include(f => f.ProjectDepartment!).ThenInclude(p => p.Department)
            .FirstOrDefaultAsync(f => f.Id == job.FlowId, ct) ?? throw new NotFoundException("Flow", job.FlowId ?? Guid.Empty);
        if (flow.CurrentVersionNo <= 0) throw new DomainException("Analyse the notes first. There is nothing to write a document from yet.", "not_analysed");

        var version = await db.FlowVersions.AsNoTracking().FirstAsync(v => v.FlowId == flow.Id && v.VersionNo == flow.CurrentVersionNo, ct);
        var content = FlowJson.Deserialize(version.Content);
        var project = flow.ProjectDepartment!.Project!;
        var preparedBy = await db.Users.AsNoTracking().Where(u => u.Id == job.RequestedBy).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? project.Lead?.DisplayName ?? "";

        var started = System.Diagnostics.Stopwatch.StartNew();

        // 1. the diagram: drawn by the server from the steps and decisions as plain SVG (milliseconds, no browser) and kept for the library
        await diagrams.EnsureFlowDiagramAsync(project.Id, flow.Id, version.VersionNo, content, force: false, ct, needPng: false);
        var drawing = FlowDiagramBuilder.Build(content);
        string? svg = null;
        byte[]? png = null;
        var drawn = started.ElapsedMilliseconds;

        // 2. the explanation of the flow and its touch points: built from the workflow in milliseconds. Only when the administrator
        //    has switched AI wording on is the AI asked to rewrite it (slower, and it uses tokens).
        var plan = GuidePlanBuilder.FromFlow(content);
        var aiWording = await settings.GetFlagAsync(SettingsService.FlagAiWording, false, ct) && (await aiStatus.GetAsync(ct)).Provider != "demo";
        if (aiWording)
        {
            try { plan = await AskForPlanAsync(job, project.HospitalName, flow.ProjectDepartment.DisplayName, content, ct); }
            catch (AiServiceException e) { log.LogWarning("AI wording skipped for guide job {JobId}: {Code}", job.Id, e.Code); }   // the plain version is still a good document
        }

        // 3. the page and the PDF
        var open = await db.ClarificationItems.AsNoTracking().Where(c => c.FlowId == flow.Id && c.Status == ClarificationStatus.Open).OrderBy(c => c.QuestionKey)
            .Select(c => new GuideOpenQuestion(c.QuestionKey, c.Question, c.Status.ToString())).ToListAsync(ct);
        var doc = await EnsureDocumentAsync(flow, project, ct);
        var demo = (await aiStatus.GetAsync(ct)).Provider == "demo";
        var versionNo = (await db.DocumentVersions.Where(v => v.GeneratedDocumentId == doc.Id).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0) + 1;
        var history = await HistoryAsync(doc.Id, versionNo, version.VersionNo, preparedBy, ct);
        var html = GuideHtmlBuilder.Build(new GuideInput(doc.DocNumber, project.HospitalName, flow.MenuName, flow.ProjectDepartment.DisplayName, preparedBy, clock.UtcNow,
            version.VersionNo, content, plan, png, open, demo, LoadLogo(), svg, AiWritten: aiWording, Diagram: drawing, History: history)
        { RequestCode = flow.RequestNo > 0 ? $"{flow.Kind}-{flow.RequestNo:000}" : null, Figures = await figures.ForDocumentAsync(flow.Id, ct),
            Sections = string.IsNullOrWhiteSpace(job.SectionsJson) ? null : GuideSections.Parse(JsonSerializer.Deserialize<string[]>(job.SectionsJson)),      // only what the person chose
            SectionTitles = GuideSections.ReadTitles(job.TitlesJson),                                                                                         // under the headings they chose to call them
            SectionTexts = GuideSections.TextsOf(GuideSections.ReadTexts(job.TextsJson), null) });                                                            // with the text they wrote instead of the generated text
        var bytes = await pdf.RenderAsync(html, ct);
        log.LogInformation("Guide job {JobId}: diagram {DiagramMs} ms, whole document {TotalMs} ms, AI wording {AiWording}", job.Id, drawn, started.ElapsedMilliseconds, aiWording);

        var path = $"documents/{project.Id:N}/{doc.DocNumber}/v{versionNo}/{doc.DocNumber}-v{versionNo}.pdf";
        using (var ms = new MemoryStream(bytes)) await blobs.SaveAsync(path, ms, "application/pdf", ct);
        db.DocumentVersions.Add(new DocumentVersion
        {
            GeneratedDocumentId = doc.Id, VersionNo = versionNo, SourceFlowVersions = JsonSerializer.Serialize(new[] { new { flowId = flow.Id, versionNo = version.VersionNo } }),
            DocxPath = "", PdfPath = path, TemplateVersion = "implementation document", Sha256 = Hashing.Sha256Hex(html),
            ApprovalStatus = ApprovalStatus.Pending, GeneratedBy = job.RequestedBy, GeneratedAt = clock.UtcNow,
        });
        var tracked = await db.GeneratedDocuments.FirstAsync(d => d.Id == doc.Id, ct);
        tracked.CurrentVersionNo = versionNo;
        tracked.Title = flow.MenuName.Length > 300 ? flow.MenuName[..300] : flow.MenuName;
        await db.SaveChangesAsync(ct);
        await db.TouchFlowAsync(flow.Id, clock.UtcNow, ct);        // not through the tracked flow: notes being added at the same moment must not collide with it
        return (doc.Id, versionNo);
    }

    /// <summary>
    /// The headings this request's document would have, and what is under each, so the person can choose before anything is written. Quick, uses no AI,
    /// and makes nothing (the plain wording is used; the sections are the same when the administrator has switched AI wording on).
    /// </summary>
    public async Task<GuideOutlineDto> OutlineAsync(Guid flowId, Guid requestedBy, CancellationToken ct)
    {
        var flow = await db.Flows.AsNoTracking().Include(f => f.ProjectDepartment!).ThenInclude(p => p.Project!).Include(f => f.ProjectDepartment!).ThenInclude(p => p.Department)
            .FirstOrDefaultAsync(f => f.Id == flowId, ct) ?? throw new NotFoundException("Flow", flowId);
        if (flow.CurrentVersionNo <= 0) throw new DomainException("Analyse the notes first. There is nothing to write a document from yet.", "not_analysed");
        var version = await db.FlowVersions.AsNoTracking().FirstAsync(v => v.FlowId == flow.Id && v.VersionNo == flow.CurrentVersionNo, ct);
        var content = FlowJson.Deserialize(version.Content);
        var project = flow.ProjectDepartment!.Project!;
        var preparedBy = await db.Users.AsNoTracking().Where(u => u.Id == requestedBy).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? project.Lead?.DisplayName ?? "";
        var open = await db.ClarificationItems.AsNoTracking().Where(c => c.FlowId == flow.Id && c.Status == ClarificationStatus.Open).OrderBy(c => c.QuestionKey)
            .Select(c => new GuideOpenQuestion(c.QuestionKey, c.Question, c.Status.ToString())).ToListAsync(ct);

        // the document number and version this PDF would get, so the cover and the document control show what will really be printed
        var doc = await db.GeneratedDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Scope == DocumentScope.Guide && d.FlowId == flow.Id, ct);
        var module = flow.ProjectDepartment.ModuleCode;
        var number = doc?.DocNumber ?? (flow.RequestNo > 0
            ? $"IMPL-{(string.IsNullOrWhiteSpace(module) || module.Equals("GEN", StringComparison.OrdinalIgnoreCase) ? project.Code : module)}-{flow.Kind}-{flow.RequestNo:000}"
            : "(numbered when the PDF is made)");
        var versionNo = doc is null ? 1 : (await db.DocumentVersions.Where(v => v.GeneratedDocumentId == doc.Id).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0) + 1;
        var history = doc is null ? null : await HistoryAsync(doc.Id, versionNo, version.VersionNo, preparedBy, ct);

        var input = new GuideInput(number, project.HospitalName, flow.MenuName, flow.ProjectDepartment.DisplayName, preparedBy, clock.UtcNow, version.VersionNo, content,
            GuidePlanBuilder.FromFlow(content), null, open, false, null, null, false, FlowDiagramBuilder.Build(content), history)
        { RequestCode = flow.RequestNo > 0 ? $"{flow.Kind}-{flow.RequestNo:000}" : null, Figures = await figures.ForDocumentAsync(flow.Id, ct) };
        var sections = GuideHtmlBuilder.Outline(input, standalone: true).Select(s => new GuideSectionDto(s.Key, s.Title, s.Detail, [new GuidePartDto(Guid.Empty, s.Html)])).ToList();
        return new GuideOutlineDto(sections, [], GuideHtmlBuilder.PreviewStyle(), GuideHtmlBuilder.PreviewCover(input));
    }
    /// <summary>The earlier versions of this document and the one being made now, for the document control table.</summary>
    private async Task<List<GuideVersionRow>> HistoryAsync(Guid documentId, int newVersionNo, int flowVersionNo, string preparedBy, CancellationToken ct)
    {
        var rows = new List<GuideVersionRow>();
        var earlier = await db.DocumentVersions.AsNoTracking().Where(v => v.GeneratedDocumentId == documentId).OrderBy(v => v.VersionNo)
            .Select(v => new { v.VersionNo, v.GeneratedAt, v.GeneratedBy, v.SourceFlowVersions }).ToListAsync(ct);
        foreach (var v in earlier)
        {
            var by = v.GeneratedBy is { } id ? await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
            var source = 0;
            try { using var doc = JsonDocument.Parse(v.SourceFlowVersions); source = doc.RootElement[0].GetProperty("versionNo").GetInt32(); } catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
            rows.Add(new GuideVersionRow(v.VersionNo, v.GeneratedAt, by ?? "", source));
        }
        rows.Add(new GuideVersionRow(newVersionNo, clock.UtcNow, preparedBy, flowVersionNo));
        return rows;
    }

    /// <summary>The company logo for the cover, if templates/logo.png has been supplied. Without it the cover is text only.</summary>
    internal static byte[]? LoadLogo()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "templates", "logo.png");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException) { return null; }
    }

    private async Task<JsonObject> AskForPlanAsync(DocumentJobRun job, string project, string department, FlowContent content, CancellationToken ct)
    {
        var cfg = await settings.GetAiAsync(ct);
        var node = (JsonObject)JsonNode.Parse(FlowJson.Serialize(content))!;
        foreach (var strip in new[] { "mermaid", "changeLog", "confidence", "schemaVersion", "clarificationQuestions" }) node.Remove(strip);
        var message = $"<implementation_request>\n<project>{System.Security.SecurityElement.Escape(project)}</project>\n<department>{System.Security.SecurityElement.Escape(department)}</department>\n<workflow>{node.ToJsonString()}</workflow>\n</implementation_request>";

        for (var attempt = 1; ; attempt++)
        {
            var result = await ai.CallAsync(new AiCallRequest(cfg.Model, SystemPrompt, message, ToolName,
                "Submit the explanation of the workflow.", (JsonObject)JsonNode.Parse(PlanSchema)!, cfg.MaxTokens, cfg.Effort, cfg.DisableThinking), ct);

            JsonObject? plan = null;
            try { plan = JsonNode.Parse(result.ToolInputJson) as JsonObject; } catch (JsonException) { }
            var usable = plan is not null && GuideHtmlBuilder.Str(plan, "summary").Length > 0;
            await usage.RecordAsync(AiUsageKind.Guide, job.RequestedBy, job.ProjectId, job.FlowId, null, result.Model,
                result.InputTokens + result.CacheCreationTokens + result.CacheReadTokens, result.OutputTokens, result.DurationMs, usable, CancellationToken.None);
            if (usable) return plan!;
            if (attempt >= 2) throw new AiServiceException("ai_no_tool_call", "The AI did not return a usable explanation. Try again.", retryable: true);
        }
    }

    private async Task<GeneratedDocument> EnsureDocumentAsync(Flow flow, Project project, CancellationToken ct)
    {
        var doc = await db.GeneratedDocuments.Include(d => d.Versions).FirstOrDefaultAsync(d => d.Scope == DocumentScope.Guide && d.FlowId == flow.Id, ct);
        if (doc is not null) return doc;
        // a numbered request names its own document: IMPL-WARD-NR-003 (the module, the kind, the number). The default "General" module has no
        // name worth printing, so the project code stands in for it. Requests made before numbering keep the old running number.
        var module = flow.ProjectDepartment?.ModuleCode;
        string number;
        if (flow.RequestNo > 0) number = $"IMPL-{(string.IsNullOrWhiteSpace(module) || module.Equals("GEN", StringComparison.OrdinalIgnoreCase) ? project.Code : module)}-{flow.Kind}-{flow.RequestNo:000}";
        else number = $"IMPL-{project.Code}-{await numbering.NextAsync(project.Id, "IMPL", ct):000}";
        doc = new GeneratedDocument
        {
            ProjectId = project.Id, Scope = DocumentScope.Guide, FlowId = flow.Id, Title = flow.MenuName,
            DocNumber = number,
        };
        db.GeneratedDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    // ------------------------------------------------------------------ what the AI is asked

    private const string SystemPrompt = """
        You are a senior business analyst for BACKBONE, a hospital management system made by AOSTA India. You are given one workflow that
        an implementation consultant captured from a hospital department (as JSON). Write the Implementation Document that explains it to the
        development team: how the flow works, who is involved, and every place where it touches the system. It is NOT a development plan.

        Explain the flow and its touch points. Do not write: development phases or tasks, effort estimates, database tables or fields,
        API designs, code, test cases, or technology choices. The developers will decide those. What they need from you is a clear
        understanding of the business flow.

        Rules:
        - Use only what the workflow says. Never invent hospital names, people, numbers or rules.
        - Never write "To be confirmed" or "Not stated" as an answer. If a detail is unknown, leave that item out, and put the missing
          information in openQuestions as a question to ask the department.
        - Plain English that a developer and a hospital manager can both follow. Short sentences. Keep the names of menus, screens and
          roles exactly as the workflow uses them.
        - summary: 2 to 4 sentences. What this flow is for, who uses it, and why it matters.
        - journey: the flow told as 3 to 9 stages in order. Group related steps into one stage. Each stage says who does it, where in
          the system (the menu or screen), what happens, and the result. Put each decision inside the stage where it is made, as branches
          ("condition" and "result").
        - touchpoints: every screen, menu, master, report or print the flow uses or changes. For each: what happens there, what information
          is used, and what is produced. type is one of Screen, Menu, Master, Report, Print, Module, External system.
        - existingAreasAffected: parts of BACKBONE that already exist and are changed or depended on, and how.
        - handoffs: information passed to another department, module or system, and when.
        - assumptions: what you assumed in order to explain the flow.
        """;

    // Plain JSON Schema with no $ref, so every provider accepts it.
    private const string PlanSchema = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "Two to four sentences: what the flow is for, who uses it and why it matters." },
            "scopeIn": { "type": "array", "items": { "type": "string" }, "description": "What this flow covers." },
            "scopeOut": { "type": "array", "items": { "type": "string" }, "description": "What it clearly does not cover, only when stated or obvious." },
            "roles": { "type": "array", "items": { "type": "object", "properties": {
              "role": { "type": "string" }, "responsibility": { "type": "string" }, "usesAreas": { "type": "array", "items": { "type": "string" } } },
              "required": ["role", "responsibility", "usesAreas"] } },
            "journey": { "type": "array", "items": { "type": "object", "properties": {
              "title": { "type": "string" }, "who": { "type": "string" }, "where": { "type": "string" }, "description": { "type": "string" }, "outcome": { "type": "string" },
              "branches": { "type": "array", "items": { "type": "object", "properties": { "condition": { "type": "string" }, "result": { "type": "string" } }, "required": ["condition", "result"] } } },
              "required": ["title", "who", "where", "description", "outcome", "branches"] } },
            "touchpoints": { "type": "array", "items": { "type": "object", "properties": {
              "name": { "type": "string" }, "type": { "type": "string" }, "whatHappens": { "type": "string" },
              "dataIn": { "type": "array", "items": { "type": "string" } }, "dataOut": { "type": "array", "items": { "type": "string" } } },
              "required": ["name", "type", "whatHappens", "dataIn", "dataOut"] } },
            "existingAreasAffected": { "type": "array", "items": { "type": "object", "properties": { "area": { "type": "string" }, "effect": { "type": "string" } }, "required": ["area", "effect"] } },
            "handoffs": { "type": "array", "items": { "type": "object", "properties": { "from": { "type": "string" }, "to": { "type": "string" }, "whatPasses": { "type": "string" }, "when": { "type": "string" } }, "required": ["from", "to", "whatPasses", "when"] } },
            "assumptions": { "type": "array", "items": { "type": "string" } },
            "openQuestions": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["summary", "scopeIn", "scopeOut", "roles", "journey", "touchpoints", "existingAreasAffected", "handoffs", "assumptions", "openQuestions"]
        }
        """;
}
