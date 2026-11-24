using System.Text.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Content;
using FlowCapture.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Application.Documents;

/// <summary>
/// One PDF for a whole project, or for one module: a cover, a contents page and every analysed request as a chapter, written from the same analysed
/// workflows as the request's own document. No AI is used, so it costs no free tokens. A request that has not been analysed is listed on the contents
/// page and left out. The person may choose which sections to print and which requests to include (the choice is kept with the job). Every run adds
/// a new version of the same document, and the older versions stay downloadable. Runs as the same kind of background job as the single document
/// (<see cref="GuideGenerationService"/> hands it over).
/// </summary>
public sealed class CombinedGuideService(
    IAppDbContext db, IClock clock, IBlobStore blobs, IHtmlPdfRenderer pdf, IAiStatusService aiStatus, FigureService figures, ILogger<CombinedGuideService> log)
{
    /// <summary>What the PDF is made from, read once for both the outline and the PDF.</summary>
    private sealed record Loaded(Project Project, ProjectDepartment? Module, List<CombinedModule> Modules, List<object> Sources, int Analysed);

    public async Task<(Guid DocumentId, int VersionNo)> GenerateAsync(DocumentJobRun job, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var sections = ChosenSections(job);
        var only = ChosenRequests(job);
        var projectRow = await db.Projects.AsNoTracking().Include(p => p.Lead).FirstOrDefaultAsync(p => p.Id == job.ProjectId, ct) ?? throw new NotFoundException("Project", job.ProjectId);
        var preparedBy = await db.Users.AsNoTracking().Where(u => u.Id == job.RequestedBy).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? projectRow.Lead?.DisplayName ?? "";
        var demo = (await aiStatus.GetAsync(ct)).Provider == "demo";

        var loaded = await LoadAsync(job.ProjectId, job.Scope == DocumentScope.GuideModule ? job.ProjectDepartmentId : null, preparedBy, demo, only, sections, ct, GuideSections.ReadTitles(job.TitlesJson));
        var project = loaded.Project;
        var module = loaded.Module;

        var doc = await EnsureDocumentAsync(job, project, module, ct);
        var versionNo = (await db.DocumentVersions.Where(v => v.GeneratedDocumentId == doc.Id).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0) + 1;
        var title = module is null ? $"{project.HospitalName}: whole project" : $"{module.DisplayName}: all requests";
        var html = GuideHtmlBuilder.BuildCombined(new CombinedGuideInput(doc.DocNumber, title, project.HospitalName, project.Client?.Name, module?.DisplayName, preparedBy, clock.UtcNow, loaded.Modules, demo, GuideGenerationService.LoadLogo()));
        var bytes = await pdf.RenderAsync(html, ct);
        log.LogInformation("Combined guide job {JobId} ({Scope}): {Requests} requests, {Sections} sections, {Ms} ms", job.Id, job.Scope, loaded.Analysed, sections?.Count.ToString() ?? "all", started.ElapsedMilliseconds);

        var path = $"documents/{project.Id:N}/{doc.DocNumber}/v{versionNo}/{doc.DocNumber}-v{versionNo}.pdf";
        using (var ms = new MemoryStream(bytes)) await blobs.SaveAsync(path, ms, "application/pdf", ct);
        db.DocumentVersions.Add(new DocumentVersion
        {
            GeneratedDocumentId = doc.Id, VersionNo = versionNo, SourceFlowVersions = JsonSerializer.Serialize(loaded.Sources), DocxPath = "", PdfPath = path,
            TemplateVersion = "combined implementation document", Sha256 = Hashing.Sha256Hex(html), ApprovalStatus = ApprovalStatus.Pending,
            GeneratedBy = job.RequestedBy, GeneratedAt = clock.UtcNow,
        });
        var tracked = await db.GeneratedDocuments.FirstAsync(d => d.Id == doc.Id, ct);
        tracked.CurrentVersionNo = versionNo;
        tracked.Title = title.Length > 300 ? title[..300] : title;
        await db.SaveChangesAsync(ct);
        return (doc.Id, versionNo);
    }

    /// <summary>
    /// The headings the PDF can have, with how many requests each is in, and the analysed requests that can be put in it. Nothing is made, so the person
    /// can choose before anything is written.
    /// </summary>
    public async Task<GuideOutlineDto> OutlineAsync(Guid projectId, Guid? moduleId, CancellationToken ct)
    {
        var loaded = await LoadAsync(projectId, moduleId, "", false, null, null, ct);
        var inputs = loaded.Modules.SelectMany(m => m.Requests.Where(r => r.Input is not null).Select(r => (Module: m.Name, Request: r))).ToList();
        var outlines = inputs.Select(x => GuideHtmlBuilder.Outline(x.Request.Input!, standalone: false, slim: true)).ToList();

        var sections = new List<GuideSectionDto>();
        foreach (var key in GuideSections.For(ownDocument: false))
        {
            var k = outlines.Count(o => o.Any(s => s.Key == key));
            if (k == 0) continue;
            var detail = k == outlines.Count ? (k == 1 ? "in this request" : $"in all {k} requests") : $"in {k} of {outlines.Count} requests";
            // the section as printed in each request that has it
            var parts = new List<GuidePartDto>();
            for (var i = 0; i < inputs.Count; i++)
                if (outlines[i].FirstOrDefault(s => s.Key == key) is { } section)
                    parts.Add(new GuidePartDto(Guid.ParseExact(inputs[i].Request.Anchor[2..], "N"), section.Html));
            sections.Add(new GuideSectionDto(key, GuideSections.TitleOf(key), detail, parts));
        }        var requests = inputs.Select(x => new GuideRequestDto(Guid.ParseExact(x.Request.Anchor[2..], "N"), x.Request.Code, x.Request.Title, x.Module)).ToList();
        return new GuideOutlineDto(sections, requests, GuideHtmlBuilder.PreviewStyle());
    }

    private async Task<Loaded> LoadAsync(Guid projectId, Guid? moduleId, string preparedBy, bool demo, IReadOnlySet<Guid>? only, IReadOnlySet<string>? sections, CancellationToken ct, IReadOnlyDictionary<string, string>? titles = null)
    {
        var project = await db.Projects.AsNoTracking().Include(p => p.Lead).Include(p => p.Client).FirstOrDefaultAsync(p => p.Id == projectId, ct)
                      ?? throw new NotFoundException("Project", projectId);
        var module = moduleId is null
            ? null
            : await db.ProjectDepartments.AsNoTracking().Include(d => d.Department).FirstOrDefaultAsync(d => d.Id == moduleId && d.ProjectId == project.Id, ct)
              ?? throw new NotFoundException("Module", moduleId.Value);

        var query = db.Flows.AsNoTracking().Include(f => f.ProjectDepartment!).ThenInclude(d => d.Department).Where(f => f.ProjectDepartment!.ProjectId == project.Id);
        if (module is not null) query = query.Where(f => f.ProjectDepartmentId == module.Id);
        var flows = await query.ToListAsync(ct);
        if (only is not null) flows = flows.Where(f => only.Contains(f.Id)).ToList();            // the requests the person chose
        var analysed = flows.Where(f => f.CurrentVersionNo > 0).ToList();
        if (analysed.Count == 0)
            throw new DomainException(only is not null ? "None of the chosen requests has been analysed, so there is nothing to put in the document."
                : module is null ? "No request in this project has been analysed yet, so there is nothing to put in the document." : "No request in this module has been analysed yet, so there is nothing to put in the document.", "not_analysed");

        var ids = analysed.Select(f => f.Id).ToList();
        var openByFlow = (await db.ClarificationItems.AsNoTracking().Where(c => ids.Contains(c.FlowId) && c.Status == ClarificationStatus.Open).OrderBy(c => c.QuestionKey)
            .Select(c => new { c.FlowId, Item = new GuideOpenQuestion(c.QuestionKey, c.Question, c.Status.ToString()) }).ToListAsync(ct))
            .GroupBy(x => x.FlowId).ToDictionary(g => g.Key, g => (IReadOnlyList<GuideOpenQuestion>)g.Select(x => x.Item).ToList());
        var versions = await db.FlowVersions.AsNoTracking().Where(v => ids.Contains(v.FlowId)).ToListAsync(ct);

        var modules = new List<CombinedModule>();
        var sources = new List<object>();
        foreach (var group in flows.GroupBy(f => f.ProjectDepartmentId).OrderBy(g => g.First().ProjectDepartment!.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var name = group.First().ProjectDepartment!.DisplayName;
            var requests = new List<CombinedRequest>();
            foreach (var f in group.OrderBy(f => f.Kind).ThenBy(f => f.RequestNo).ThenBy(f => f.MenuName, StringComparer.OrdinalIgnoreCase))
            {
                var code = f.RequestNo > 0 ? $"{f.Kind}-{f.RequestNo:000}" : "Request";
                GuideInput? input = null;
                if (f.CurrentVersionNo > 0)
                {
                    var version = versions.First(v => v.FlowId == f.Id && v.VersionNo == f.CurrentVersionNo);
                    var content = FlowJson.Deserialize(version.Content);
                    input = new GuideInput("", project.HospitalName, f.MenuName, name, preparedBy, clock.UtcNow, version.VersionNo, content, GuidePlanBuilder.FromFlow(content),
                        null, openByFlow.GetValueOrDefault(f.Id) ?? [], demo, null, null, false, FlowDiagramBuilder.Build(content))
                    { RequestCode = f.RequestNo > 0 ? code : null, Figures = await figures.ForDocumentAsync(f.Id, ct), Sections = sections, SectionTitles = titles };
                    sources.Add(new { flowId = f.Id, versionNo = version.VersionNo });
                }
                requests.Add(new CombinedRequest(Anchor(f.Id), code, f.MenuName, input));
            }
            modules.Add(new CombinedModule(name, requests));
        }
        return new Loaded(project, module, modules, sources, analysed.Count);
    }

    /// <summary>The name a request's chapter has inside the PDF, and the id the outline gives it so the choice can be sent back.</summary>
    private static string Anchor(Guid flowId) => "r-" + flowId.ToString("N");

    private static IReadOnlySet<string>? ChosenSections(DocumentJobRun job) =>
        string.IsNullOrWhiteSpace(job.SectionsJson) ? null : GuideSections.Parse(JsonSerializer.Deserialize<string[]>(job.SectionsJson));

    private static IReadOnlySet<Guid>? ChosenRequests(DocumentJobRun job)
    {
        var ids = string.IsNullOrWhiteSpace(job.FlowIdsJson) ? [] : JsonSerializer.Deserialize<Guid[]>(job.FlowIdsJson) ?? [];
        return ids.Length == 0 ? null : ids.ToHashSet();
    }

    /// <summary>
    /// The document of a project is IMPL-{project code}-FULL and of a module IMPL-{module code}-MOD, so each is one document that gains versions,
    /// and neither collides with the numbers of the requests (IMPL-WARD-NR-003).
    /// </summary>
    private async Task<GeneratedDocument> EnsureDocumentAsync(DocumentJobRun job, Project project, ProjectDepartment? module, CancellationToken ct)
    {
        var scope = job.Scope;
        var doc = await db.GeneratedDocuments.FirstOrDefaultAsync(d => d.ProjectId == project.Id && d.Scope == scope && d.ProjectDepartmentId == (module == null ? null : module.Id), ct);
        if (doc is not null) return doc;
        var number = module is null ? $"IMPL-{project.Code}-FULL" : $"IMPL-{module.ModuleCode}-MOD";
        doc = new GeneratedDocument
        {
            ProjectId = project.Id, Scope = scope, ProjectDepartmentId = module?.Id, DocNumber = number.Length > 40 ? number[..40] : number,
            Title = module is null ? $"{project.HospitalName}: whole project" : $"{module.DisplayName}: all requests",
        };
        db.GeneratedDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        return doc;
    }
}
