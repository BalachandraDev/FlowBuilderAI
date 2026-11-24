using System.Text.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Application.Documents;

/// <summary>
/// Produces the requirement document (Word and PDF) for one confirmed flow, or the consolidated document for a project.
/// Runs in a background job. A flow's document keeps its number for life; every generation adds a new version, and older
/// versions stay downloadable.
/// </summary>
public sealed class DocumentGenerationService(
    IAppDbContext db, IClock clock, IBlobStore blobs, DiagramService diagrams, DocumentNumbering numbering,
    IRgdDocxWriter writer, IPdfConverter pdf, IPushSender push, ILogger<DocumentGenerationService> log)
{
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
        try
        {
            result = job.Scope == DocumentScope.Flow ? await GenerateFlowAsync(job, ct) : await GenerateProjectAsync(job, ct);
        }
        catch (DependencyUnavailableException e) { code = "dependency_unavailable"; detail = e.Message; }
        catch (DomainException e) { code = e.Code; detail = e.Message; }
        catch (Exception e)
        {
            log.LogError(e, "Document job {JobId} failed", jobId);
            code = "unexpected_error";
            detail = "The document could not be generated. The error has been logged; try again.";
        }

        db.ChangeTracker.Clear();
        job = await db.DocumentJobRuns.FirstAsync(j => j.Id == jobId, CancellationToken.None);
        job.FinishedAt = clock.UtcNow;
        if (result is { } r)
        {
            job.Status = DocJobStatus.Succeeded;
            job.ResultDocumentId = r.DocumentId;
            job.ResultVersionNo = r.VersionNo;
        }
        else
        {
            job.Status = DocJobStatus.Failed;
            job.ErrorCode = code;
            job.ErrorDetail = detail;
        }
        await db.SaveChangesAsync(CancellationToken.None);

        try
        {
            await push.SendToUserAsync(requestedBy, result is null ? "Document failed" : "Document ready",
                result is null ? "The requirement document could not be generated." : "The requirement document is ready to download.",
                new Dictionary<string, string> { ["type"] = "document", ["jobId"] = jobId.ToString() }, CancellationToken.None);
        }
        catch (Exception e) { log.LogWarning(e, "Push notification for document job {JobId} failed", jobId); }
    }

    // ------------------------------------------------------------------ one flow

    private async Task<(Guid, int)> GenerateFlowAsync(DocumentJobRun job, CancellationToken ct)
    {
        var flow = await db.Flows.Include(f => f.ProjectDepartment!).ThenInclude(p => p.Project!).ThenInclude(p => p.Lead)
            .FirstOrDefaultAsync(f => f.Id == job.FlowId, ct) ?? throw new NotFoundException("Flow", job.FlowId ?? Guid.Empty);
        var (input, confirmedVersion) = await LoadFlowInputAsync(flow, job.AssignedBy, job.RequestedBy, ct);

        var project = flow.ProjectDepartment!.Project!;
        var doc = await EnsureFlowDocumentAsync(flow, project, ct);
        input = input with { DocNumber = doc.DocNumber };

        ImageBlock? image = null;
        if (job.IncludeDiagrams)
        {
            var asset = await diagrams.EnsureFlowDiagramAsync(project.Id, flow.Id, confirmedVersion, input.Content, force: false, ct);
            image = await diagrams.ToImageAsync(asset, $"Figure 1: Main flow, version {confirmedVersion}", ct);
            input = input with { Diagram = image };
        }

        var model = RgdDocumentBuilder.Build(input);
        EnsureComplete(model);
        var template = await LoadTemplateAsync(ct);
        var docx = await writer.WriteAsync(model, template.Stream, ct);
        await template.Stream.DisposeAsync();
        var pdfBytes = await pdf.ConvertAsync(docx, ct);

        var version = await SaveVersionAsync(doc, docx, pdfBytes, template.Version,
            [new { flowId = flow.Id, versionNo = confirmedVersion }], job.RequestedBy, model.Title, ct);
        flow.Status = FlowStatusMachine.OnDocumentsGenerated(flow.Status);
        flow.LastActivityAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return (doc.Id, version);
    }

    private async Task<(FlowDocumentInput Input, int ConfirmedVersion)> LoadFlowInputAsync(Flow flow, string? assignedBy, Guid requestedBy, CancellationToken ct)
    {
        if (!FlowStatusMachine.CanGenerateDocuments(flow.Status) || flow.ConfirmedVersionNo is not { } confirmed)
            throw new DomainException($"Documents can only be generated for a confirmed flow. \"{flow.MenuName}\" is {flow.Status}.", "not_confirmed");

        var version = await db.FlowVersions.AsNoTracking().FirstAsync(v => v.FlowId == flow.Id && v.VersionNo == confirmed, ct);
        var content = FlowJson.Deserialize(version.Content);
        var pd = flow.ProjectDepartment!;
        var project = pd.Project!;
        var issues = await db.ClarificationItems.AsNoTracking()
            .Where(c => c.FlowId == flow.Id && c.Status == ClarificationStatus.Open).OrderBy(c => c.QuestionKey).ToListAsync(ct);
        var preparedBy = await db.Users.AsNoTracking().Where(u => u.Id == requestedBy).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? project.Lead?.DisplayName ?? "To be confirmed";

        var input = new FlowDocumentInput(content, "", pd.ModuleCode, assignedBy ?? project.Lead?.DisplayName ?? "To be confirmed", clock.UtcNow, project.HospitalName,
            preparedBy, confirmed, flow.ConfirmedAt, issues.Select(i => new OpenIssue(i.QuestionKey, i.Section, i.Question)).ToList(), null);
        return (input, confirmed);
    }

    private async Task<GeneratedDocument> EnsureFlowDocumentAsync(Flow flow, Project project, CancellationToken ct)
    {
        var doc = await db.GeneratedDocuments.Include(d => d.Versions).FirstOrDefaultAsync(d => d.Scope == DocumentScope.Flow && d.FlowId == flow.Id, ct);
        if (doc is not null) return doc;

        var n = await numbering.NextAsync(project.Id, flow.ProjectDepartment!.ModuleCode, ct);
        doc = new GeneratedDocument
        {
            ProjectId = project.Id, Scope = DocumentScope.Flow, FlowId = flow.Id, Title = flow.MenuName,
            DocNumber = DocumentNumbering.FlowNumber(flow.ProjectDepartment.ModuleCode, n),
        };
        db.GeneratedDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    // ------------------------------------------------------------------ the whole project

    private async Task<(Guid, int)> GenerateProjectAsync(DocumentJobRun job, CancellationToken ct)
    {
        var project = await db.Projects.Include(p => p.Lead).FirstAsync(p => p.Id == job.ProjectId, ct);
        var wanted = JsonSerializer.Deserialize<Guid[]>(job.FlowIdsJson) ?? [];
        var flows = await db.Flows.Include(f => f.ProjectDepartment!).ThenInclude(p => p.Department)
            .Where(f => f.ProjectDepartment!.ProjectId == project.Id && (f.Status == FlowStatus.Confirmed || f.Status == FlowStatus.DocumentsGenerated))
            .OrderBy(f => f.ProjectDepartment!.ModuleCode).ThenBy(f => f.MenuName).ToListAsync(ct);
        if (wanted.Length > 0)
        {
            var notConfirmed = wanted.Except(flows.Select(f => f.Id)).ToList();
            if (notConfirmed.Count > 0)
                throw new DomainException($"{notConfirmed.Count} of the chosen flow(s) are not confirmed, so they cannot be part of the project document.", "not_confirmed");
            flows = flows.Where(f => wanted.Contains(f.Id)).ToList();
        }
        if (flows.Count == 0) throw new DomainException("No flow in this project is confirmed yet.", "nothing_to_document");

        var preparedBy = await db.Users.AsNoTracking().Where(u => u.Id == job.RequestedBy).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "To be confirmed";
        var assignedBy = job.AssignedBy ?? project.Lead?.DisplayName ?? "To be confirmed";
        var now = clock.UtcNow;

        var chapters = new List<ChapterInput>();
        var nodes = new List<CombinedMermaidBuilder.FlowNode>();
        var sources = new List<object>();
        var index = 0;
        foreach (var flow in flows)
        {
            index++;
            var (input, confirmed) = await LoadFlowInputAsync(flow, assignedBy, job.RequestedBy, ct);
            var flowDoc = await EnsureFlowDocumentAsync(flow, project, ct);
            input = input with { DocNumber = flowDoc.DocNumber, Created = now };
            if (job.IncludeDiagrams)
            {
                var asset = await diagrams.EnsureFlowDiagramAsync(project.Id, flow.Id, confirmed, input.Content, false, ct);
                input = input with { Diagram = await diagrams.ToImageAsync(asset, $"Figure {index}: {flow.MenuName}, version {confirmed}", ct) };
            }
            var dept = flow.ProjectDepartment!.CustomName ?? flow.ProjectDepartment.Department?.Name ?? flow.ProjectDepartment.ModuleCode;
            chapters.Add(new ChapterInput(input, dept, flow.MenuName));
            nodes.Add(new CombinedMermaidBuilder.FlowNode(index, flow.ProjectDepartment.ModuleCode, dept, flow.MenuName, input.Content));
            sources.Add(new { flowId = flow.Id, versionNo = confirmed });
        }

        var ids = flows.Select(f => f.Id).ToHashSet();
        var accepted = (await db.CrossFlowLinks.AsNoTracking().Where(l => l.ProjectId == project.Id && l.Status == LinkStatus.Accepted).ToListAsync(ct))
            .Where(l => ids.Contains(l.FromFlowId) && ids.Contains(l.ToFlowId)).ToList();
        var position = flows.Select((f, i) => (f.Id, No: i + 1)).ToDictionary(x => x.Id, x => x.No);
        var links = accepted.Select(l => new CombinedMermaidBuilder.Link(position[l.FromFlowId], l.FromItemId, position[l.ToFlowId], l.ToItemId,
            string.Join(", ", JsonSerializer.Deserialize<string[]>(l.DataPassedJson) ?? []))).ToList();

        ImageBlock? combined = null;
        if (job.IncludeDiagrams)
        {
            var asset = await diagrams.EnsureCombinedDiagramAsync(project.Id, CombinedMermaidBuilder.Build(nodes, links), false, ct);
            combined = await diagrams.ToImageAsync(asset, "Figure: end-to-end flow across departments", ct);
        }

        string StepLabel(int flowNo, string id)
        {
            var steps = chapters[flowNo - 1].Flow.Content.Steps;
            var i = steps.FindIndex(s => s.Id == id);
            return i >= 0 ? $"Step {i + 1}" : id;
        }
        var rows = accepted.Select(l => new HandoffRow(
            chapters[position[l.FromFlowId] - 1].MenuName, StepLabel(position[l.FromFlowId], l.FromItemId),
            chapters[position[l.ToFlowId] - 1].MenuName, StepLabel(position[l.ToFlowId], l.ToItemId), l.Description,
            string.Join(", ", JsonSerializer.Deserialize<string[]>(l.DataPassedJson) ?? []) is { Length: > 0 } d ? d : "Not applicable")).ToList();

        var doc = await EnsureProjectDocumentAsync(project, ct);
        var model = ProjectDocumentBuilder.Build(new ProjectDocumentInput(doc.DocNumber, project.HospitalName, project.Code, project.Location, assignedBy, now,
            preparedBy, chapters, combined, rows));
        EnsureComplete(model);

        var template = await LoadTemplateAsync(ct);
        var docx = await writer.WriteAsync(model, template.Stream, ct);
        await template.Stream.DisposeAsync();
        var pdfBytes = await pdf.ConvertAsync(docx, ct);
        var version = await SaveVersionAsync(doc, docx, pdfBytes, template.Version, sources, job.RequestedBy, model.Title, ct);
        return (doc.Id, version);
    }

    private async Task<GeneratedDocument> EnsureProjectDocumentAsync(Project project, CancellationToken ct)
    {
        var doc = await db.GeneratedDocuments.Include(d => d.Versions).FirstOrDefaultAsync(d => d.Scope == DocumentScope.Project && d.ProjectId == project.Id, ct);
        if (doc is not null) return doc;
        var n = await numbering.NextAsync(project.Id, "PRJ-C", ct);
        doc = new GeneratedDocument
        {
            ProjectId = project.Id, Scope = DocumentScope.Project, Title = $"{project.HospitalName}: consolidated requirements",
            DocNumber = DocumentNumbering.ProjectNumber(project.Code, n),
        };
        db.GeneratedDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    // ------------------------------------------------------------------ shared

    private void EnsureComplete(RgdDocument model)
    {
        var blanks = model.FindBlankText();
        if (blanks.Count > 0)
            throw new DomainException("The document would contain empty fields: " + string.Join(", ", blanks.Take(5)), "document_incomplete");
    }

    private async Task<(Stream Stream, string Version)> LoadTemplateAsync(CancellationToken ct)
    {
        var template = await db.DocumentTemplates.AsNoTracking().Where(t => t.IsActive).OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync(ct)
                       ?? throw new DependencyUnavailableException("No Word template is active. An administrator must upload one.");
        var source = await blobs.OpenReadAsync(template.BlobPath, ct);
        var copy = new MemoryStream();
        await source.CopyToAsync(copy, ct);
        await source.DisposeAsync();
        copy.Position = 0;
        return (copy, $"{template.Name} {template.Version}");
    }

    private async Task<int> SaveVersionAsync(GeneratedDocument doc, byte[] docx, byte[] pdfBytes, string templateVersion, IReadOnlyList<object> sources, Guid by, string title, CancellationToken ct)
    {
        var versionNo = (await db.DocumentVersions.Where(v => v.GeneratedDocumentId == doc.Id).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0) + 1;
        var folder = $"documents/{doc.ProjectId:N}/{doc.DocNumber}/v{versionNo}";
        var docxPath = $"{folder}/{doc.DocNumber}-v{versionNo}.docx";
        var pdfPath = $"{folder}/{doc.DocNumber}-v{versionNo}.pdf";
        await Save(docxPath, docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ct);
        await Save(pdfPath, pdfBytes, "application/pdf", ct);

        db.DocumentVersions.Add(new DocumentVersion
        {
            GeneratedDocumentId = doc.Id, VersionNo = versionNo, SourceFlowVersions = JsonSerializer.Serialize(sources),
            DocxPath = docxPath, PdfPath = pdfPath, TemplateVersion = templateVersion.Length > 40 ? templateVersion[..40] : templateVersion,
            Sha256 = Hashing.Sha256Hex(docx), ApprovalStatus = ApprovalStatus.Pending, GeneratedBy = by, GeneratedAt = clock.UtcNow,
        });
        var tracked = await db.GeneratedDocuments.FirstAsync(d => d.Id == doc.Id, ct);
        tracked.CurrentVersionNo = versionNo;
        tracked.Title = title.Length > 300 ? title[..300] : title;
        await db.SaveChangesAsync(ct);
        return versionNo;
    }

    private async Task Save(string path, byte[] bytes, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream(bytes);
        await blobs.SaveAsync(path, ms, contentType, ct);
    }
}
