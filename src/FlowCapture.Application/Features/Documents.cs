using System.Text.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Documents;
using FlowCapture.Domain;
using FlowCapture.Shared.Content;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.DocumentFeatures;

public sealed record GenerateFlowDocumentCommand(Guid FlowId, GenerateDocumentRequest Body) : IRequest<JobDto>;
public sealed record GenerateProjectDocumentCommand(Guid ProjectId, GenerateProjectDocumentRequest Body) : IRequest<JobDto>;
public sealed record ListProjectDocumentsQuery(Guid ProjectId) : IRequest<IReadOnlyList<DocumentDto>>;
public sealed record ListFlowDocumentsQuery(Guid FlowId) : IRequest<IReadOnlyList<DocumentDto>>;
public sealed record GetDocumentQuery(Guid Id) : IRequest<DocumentDto>;
public sealed record DownloadDocumentQuery(Guid Id, int VersionNo, string Format) : IRequest<FileResultDto>;
public sealed record ApproveDocumentCommand(Guid Id, int VersionNo, ApproveDocumentRequest Body) : IRequest<DocumentDto>;
/// <summary>A user marks the latest version of a document as final, to give to the team; the administrator then verifies it.</summary>
public sealed record MarkFinalCommand(Guid Id, int VersionNo, string? Note) : IRequest<DocumentDto>;
/// <summary>Takes a final mark back while the administrator has not decided yet.</summary>
public sealed record WithdrawFinalCommand(Guid Id, int VersionNo) : IRequest<DocumentDto>;

public sealed record GetFlowDiagramQuery(Guid FlowId, string Format, int? VersionNo) : IRequest<FileResultDto>;
public sealed record RegenerateFlowDiagramCommand(Guid FlowId) : IRequest<DiagramInfoDto>;
public sealed record GetCombinedDiagramQuery(Guid ProjectId, string Format) : IRequest<FileResultDto>;
public sealed record GenerateCombinedDiagramCommand(Guid ProjectId) : IRequest<DiagramInfoDto>;

public sealed record ListTemplatesQuery : IRequest<IReadOnlyList<DocumentTemplateDto>>;
public sealed record UploadTemplateCommand(string Name, string? Version, Stream Content, long Length) : IRequest<DocumentTemplateDto>;
public sealed record ActivateTemplateCommand(Guid Id) : IRequest<DocumentTemplateDto>;

/// <summary>A file to send back. The caller owns the stream.</summary>
public sealed record FileResultDto(Stream Stream, string ContentType, string FileName);

public sealed class ApproveDocumentValidator : AbstractValidator<ApproveDocumentCommand>
{
    public ApproveDocumentValidator()
    {
        RuleFor(x => x.Body.Note).MaximumLength(1000);
        RuleFor(x => x.Body).Must(b => b.Approved || !string.IsNullOrWhiteSpace(b.Note)).WithMessage("Say why the document is rejected.");
    }
}

public sealed class UploadTemplateValidator : AbstractValidator<UploadTemplateCommand>
{
    public UploadTemplateValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Version).MaximumLength(40);
        RuleFor(x => x.Length).GreaterThan(0).LessThanOrEqualTo(10 * 1024 * 1024).WithMessage("The template must be a .docx file of at most 10 MB.");
    }
}

public sealed class DocumentHandlers(
    IAppDbContext db, IProjectAccess access, ICurrentUser user, IClock clock, IBlobStore blobs, IJobScheduler jobs,
    DiagramService diagrams, IRgdDocxWriter writer, IPushSender push) :
    IRequestHandler<MarkFinalCommand, DocumentDto>,
    IRequestHandler<WithdrawFinalCommand, DocumentDto>,
    IRequestHandler<GenerateFlowDocumentCommand, JobDto>,
    IRequestHandler<GenerateProjectDocumentCommand, JobDto>,
    IRequestHandler<ListProjectDocumentsQuery, IReadOnlyList<DocumentDto>>,
    IRequestHandler<ListFlowDocumentsQuery, IReadOnlyList<DocumentDto>>,
    IRequestHandler<GetDocumentQuery, DocumentDto>,
    IRequestHandler<DownloadDocumentQuery, FileResultDto>,
    IRequestHandler<ApproveDocumentCommand, DocumentDto>,
    IRequestHandler<GetFlowDiagramQuery, FileResultDto>,
    IRequestHandler<RegenerateFlowDiagramCommand, DiagramInfoDto>,
    IRequestHandler<GetCombinedDiagramQuery, FileResultDto>,
    IRequestHandler<GenerateCombinedDiagramCommand, DiagramInfoDto>,
    IRequestHandler<ListTemplatesQuery, IReadOnlyList<DocumentTemplateDto>>,
    IRequestHandler<UploadTemplateCommand, DocumentTemplateDto>,
    IRequestHandler<ActivateTemplateCommand, DocumentTemplateDto>
{
    // ------------------------------------------------------------------ generating

    public async Task<JobDto> Handle(GenerateFlowDocumentCommand r, CancellationToken ct)
    {
        var flow = await db.Flows.AsNoTracking().Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == r.FlowId, ct) ?? throw new NotFoundException("Flow", r.FlowId);
        var projectId = flow.ProjectDepartment!.ProjectId;
        RefuseAdmin();
        await access.EnsureLeadAsync(projectId, ct);
        if (!FlowStatusMachine.CanGenerateDocuments(flow.Status))
            throw new DomainException($"Documents can only be generated for a confirmed flow. This flow is {flow.Status}.", "not_confirmed");

        var running = await db.DocumentJobRuns.AsNoTracking().FirstOrDefaultAsync(j => j.FlowId == flow.Id && j.Scope == DocumentScope.Flow
            && (j.Status == DocJobStatus.Queued || j.Status == DocJobStatus.Running), ct);
        if (running is not null) return ToDto(running);

        return await QueueAsync(new DocumentJobRun
        {
            ProjectId = projectId, Scope = DocumentScope.Flow, FlowId = flow.Id, AssignedBy = Clean(r.Body.AssignedBy),
            IncludeDiagrams = r.Body.IncludeDiagrams, RequestedBy = user.RequireUserId(),
        }, ct);
    }

    public async Task<JobDto> Handle(GenerateProjectDocumentCommand r, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == r.ProjectId, ct)) throw new NotFoundException("Project", r.ProjectId);
        RefuseAdmin();
        await access.EnsureLeadAsync(r.ProjectId, ct);

        var confirmed = await db.Flows.AsNoTracking().Where(f => f.ProjectDepartment!.ProjectId == r.ProjectId
            && (f.Status == FlowStatus.Confirmed || f.Status == FlowStatus.DocumentsGenerated)).Select(f => f.Id).ToListAsync(ct);
        if (confirmed.Count == 0) throw new DomainException("No flow in this project is confirmed yet.", "nothing_to_document");
        if (r.Body.FlowIds is { Length: > 0 } chosen && chosen.Except(confirmed).Any())
            throw new DomainException("Some of the chosen flows are not confirmed.", "not_confirmed");

        var running = await db.DocumentJobRuns.AsNoTracking().FirstOrDefaultAsync(j => j.ProjectId == r.ProjectId && j.Scope == DocumentScope.Project
            && (j.Status == DocJobStatus.Queued || j.Status == DocJobStatus.Running), ct);
        if (running is not null) return ToDto(running);

        return await QueueAsync(new DocumentJobRun
        {
            ProjectId = r.ProjectId, Scope = DocumentScope.Project, FlowIdsJson = JsonSerializer.Serialize(r.Body.FlowIds ?? []),
            AssignedBy = Clean(r.Body.AssignedBy), IncludeDiagrams = true, RequestedBy = user.RequireUserId(),
        }, ct);
    }

    private async Task<JobDto> QueueAsync(DocumentJobRun job, CancellationToken ct)
    {
        db.DocumentJobRuns.Add(job);
        await db.SaveChangesAsync(ct);
        jobs.EnqueueDocument(job.Id);
        return ToDto(job);
    }

    public static JobDto ToDto(DocumentJobRun j) => new(j.Id, j.Scope switch { DocumentScope.Flow => "FlowDocument", DocumentScope.Guide => "GuideDocument", DocumentScope.GuideModule => "GuideModuleDocument", DocumentScope.GuideProject => "GuideProjectDocument", _ => "ProjectDocument" },
        j.Status.ToString(), j.Status == DocJobStatus.Succeeded ? 100 : null, j.ErrorDetail, j.ResultDocumentId, j.CreatedAt);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, 120)];

    private bool IsAdmin => user.IsInRole(FlowCapture.Shared.AppRoles.Admin);

    /// <summary>The administrator verifies the final documents; the users make them.</summary>
    private void RefuseAdmin()
    {
        if (IsAdmin) throw new ForbiddenException("The administrator does not make documents. Users make them and mark them final; you verify the final ones.");
    }

    /// <summary>Where a version of a document stands: a working copy, final (waiting for the administrator), verified, or returned to its maker.</summary>
    public static string StageOf(DocumentVersion v) =>
        v.FinalizedAt is null ? "Working" : v.ApprovalStatus switch { ApprovalStatus.Approved => "Verified", ApprovalStatus.Rejected => "Returned", _ => "Final" };

    // ------------------------------------------------------------------ reading

    public async Task<IReadOnlyList<DocumentDto>> Handle(ListProjectDocumentsQuery r, CancellationToken ct)
    {
        await access.EnsureMemberAsync(r.ProjectId, ct);
        return await LoadAsync(db.GeneratedDocuments.Where(d => d.ProjectId == r.ProjectId), ct);
    }

    public async Task<IReadOnlyList<DocumentDto>> Handle(ListFlowDocumentsQuery r, CancellationToken ct)
    {
        var projectId = await ProjectOfFlowAsync(r.FlowId, ct);
        await access.EnsureMemberAsync(projectId, ct);
        return await LoadAsync(db.GeneratedDocuments.Where(d => d.FlowId == r.FlowId), ct);
    }

    public async Task<DocumentDto> Handle(GetDocumentQuery r, CancellationToken ct)
    {
        var doc = await RequireAsync(r.Id, ct);
        await access.EnsureMemberAsync(doc.ProjectId, ct);      // the documents of a client are for the people assigned to that client (and administrators)
        var found = await LoadAsync(db.GeneratedDocuments.Where(d => d.Id == r.Id), ct);
        return found.SingleOrDefault() ?? throw new ForbiddenException("The administrator can only open documents that were marked final.");
    }

    /// <summary>
    /// The documents with their versions. The administrator sees only the versions that were marked final (and only the documents that have one): working
    /// copies are the users' own.
    /// </summary>
    private async Task<IReadOnlyList<DocumentDto>> LoadAsync(IQueryable<GeneratedDocument> query, CancellationToken ct)
    {
        var admin = IsAdmin;
        var docs = await query.AsNoTracking().Where(d => d.CurrentVersionNo > 0).OrderBy(d => d.DocNumber).ToListAsync(ct);
        var ids = docs.Select(d => d.Id).ToList();
        var shown = db.DocumentVersions.AsNoTracking().Where(v => ids.Contains(v.GeneratedDocumentId));
        if (admin) shown = shown.Where(v => v.FinalizedAt != null);
        var versions = await shown.OrderByDescending(v => v.VersionNo).ToListAsync(ct);
        var userIds = versions.SelectMany(v => new[] { (Guid?)v.GeneratedBy, v.ApprovedBy, v.FinalizedBy }).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        if (admin) docs = docs.Where(d => versions.Any(v => v.GeneratedDocumentId == d.Id)).ToList();

        return docs.Select(d => new DocumentDto(d.Id, d.DocNumber, d.Title, d.Scope.ToString(), d.FlowId, admin ? versions.Where(v => v.GeneratedDocumentId == d.Id).Max(v => v.VersionNo) : d.CurrentVersionNo,
            versions.Where(v => v.GeneratedDocumentId == d.Id).Select(v => new DocumentVersionDto(v.VersionNo, v.GeneratedAt, names.GetValueOrDefault(v.GeneratedBy, "System"),
                v.ApprovalStatus.ToString(), v.TemplateVersion,
                (JsonSerializer.Deserialize<JsonElement[]>(v.SourceFlowVersions) ?? []).Select(e => e.GetProperty("versionNo").GetInt32()).ToArray(),
                v.ApprovedBy is { } a ? names.GetValueOrDefault(a) : null, v.ApprovalNote,
                v.FinalizedAt, v.FinalizedBy is { } f ? names.GetValueOrDefault(f) : null, v.FinalNote)).ToList())).ToList();
    }

    public async Task<FileResultDto> Handle(DownloadDocumentQuery r, CancellationToken ct)
    {
        var doc = await RequireAsync(r.Id, ct);
        await access.EnsureMemberAsync(doc.ProjectId, ct);      // see above: only people who can open the project
        var version = await db.DocumentVersions.AsNoTracking().FirstOrDefaultAsync(v => v.GeneratedDocumentId == doc.Id && v.VersionNo == r.VersionNo, ct)
                      ?? throw new NotFoundException("Document version", r.VersionNo);
        if (IsAdmin && version.FinalizedAt is null) throw new ForbiddenException("The administrator can only open documents that were marked final.");

        var pdf = r.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase);
        if (!pdf && !r.Format.Equals("docx", StringComparison.OrdinalIgnoreCase)) throw new DomainException("Format must be docx or pdf.", "invalid_format");
        if (!pdf && string.IsNullOrEmpty(version.DocxPath)) throw new DomainException("This document is only available as a PDF.", "pdf_only");

        // downloads are part of the audit trail
        db.AuditLogs.Add(new AuditLog
        {
            At = clock.UtcNow, UserId = user.UserId, Action = "Download", EntityType = nameof(DocumentVersion), EntityId = version.Id.ToString(),
            ProjectId = doc.ProjectId, NewValues = JsonSerializer.Serialize(new { doc.DocNumber, version.VersionNo, format = r.Format.ToLowerInvariant() }),
            CorrelationId = user.CorrelationId, IpAddress = user.IpAddress,
        });
        await db.SaveChangesAsync(ct);

        var stream = await blobs.OpenReadAsync(pdf ? version.PdfPath : version.DocxPath, ct);
        return new FileResultDto(stream,
            pdf ? "application/pdf" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            $"{doc.DocNumber}-v{version.VersionNo}.{(pdf ? "pdf" : "docx")}");
    }

    /// <summary>The administrator verifies a final document, or returns it to its maker with the reason. Only a version that was marked final can be decided.</summary>
    public async Task<DocumentDto> Handle(ApproveDocumentCommand r, CancellationToken ct)
    {
        var doc = await RequireAsync(r.Id, ct);
        if (!IsAdmin) throw new ForbiddenException("Only the administrator verifies the final documents.");

        var version = await db.DocumentVersions.FirstOrDefaultAsync(v => v.GeneratedDocumentId == doc.Id && v.VersionNo == r.VersionNo, ct)
                      ?? throw new NotFoundException("Document version", r.VersionNo);
        // tracked on purpose: the audit trail finds the project of this version through its document, and never queries while saving
        await db.GeneratedDocuments.FirstAsync(d => d.Id == doc.Id, ct);
        if (version.FinalizedAt is null) throw new ForbiddenException("The administrator can only open documents that were marked final.");
        if (version.ApprovalStatus != ApprovalStatus.Pending)
            throw new DomainException($"This version was already {(version.ApprovalStatus == ApprovalStatus.Approved ? "verified" : "returned")}. Its maker has to make and mark a new version as final.", "already_decided");

        version.ApprovalStatus = r.Body.Approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        version.ApprovedBy = user.RequireUserId();
        version.ApprovedAt = clock.UtcNow;
        version.ApprovalNote = r.Body.Note?.Trim();
        await db.SaveChangesAsync(ct);

        if (version.FinalizedBy is { } maker)
            await TellAsync(maker, r.Body.Approved ? "Document verified" : "Document returned",
                r.Body.Approved ? $"{doc.DocNumber} was verified by the administrator." : $"{doc.DocNumber} was returned: {r.Body.Note?.Trim()}", doc.Id, ct);
        return (await LoadAsync(db.GeneratedDocuments.Where(d => d.Id == doc.Id), ct)).Single();
    }

    // ------------------------------------------------------------------ final: the user hands a version to the team, the administrator verifies it

    /// <summary>Who may mark a document final: the people who can work on what it is made from (never the administrator).</summary>
    private async Task EnsureCanFinalizeAsync(GeneratedDocument doc, CancellationToken ct)
    {
        RefuseAdmin();
        switch (doc.Scope)
        {
            case DocumentScope.GuideProject:
                if (await access.GetRoleAsync(doc.ProjectId, ct) is not (ProjectRole.Lead or ProjectRole.Collaborator))
                    throw new ForbiddenException("Only the project's lead and the colleagues on its client can mark the PDF of the whole project as final.");
                break;
            case DocumentScope.GuideModule:
                await access.EnsureCanCaptureAsync(doc.ProjectId, doc.ProjectDepartmentId ?? Guid.Empty, ct);
                break;
            case DocumentScope.Guide:
                var department = await db.Flows.AsNoTracking().Where(f => f.Id == doc.FlowId).Select(f => (Guid?)f.ProjectDepartmentId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Flow", doc.FlowId ?? Guid.Empty);
                await access.EnsureCanCaptureAsync(doc.ProjectId, department, ct);
                break;
            default:
                await access.EnsureLeadAsync(doc.ProjectId, ct);
                break;
        }
    }

    public async Task<DocumentDto> Handle(MarkFinalCommand r, CancellationToken ct)
    {
        var doc = await RequireAsync(r.Id, ct);
        await EnsureCanFinalizeAsync(doc, ct);
        var version = await db.DocumentVersions.FirstOrDefaultAsync(v => v.GeneratedDocumentId == doc.Id && v.VersionNo == r.VersionNo, ct) ?? throw new NotFoundException("Document version", r.VersionNo);
        await db.GeneratedDocuments.FirstAsync(d => d.Id == doc.Id, ct);                       // tracked for the audit trail, as in Approve
        if (r.VersionNo != doc.CurrentVersionNo)
            throw new DomainException("Only the latest version can be marked final. Open the latest version of this document.", "not_latest");
        if (version.FinalizedAt is not null)
        {
            if (version.ApprovalStatus == ApprovalStatus.Pending) return (await LoadAsync(db.GeneratedDocuments.Where(d => d.Id == doc.Id), ct)).Single();      // already waiting
            throw new DomainException(version.ApprovalStatus == ApprovalStatus.Approved ? "This version was already verified."
                : "This version was returned by the administrator. Update the document, then mark the new version as final.", "already_decided");
        }

        // one final version waits at a time: an older one that was still waiting is replaced by this one
        foreach (var older in await db.DocumentVersions.Where(v => v.GeneratedDocumentId == doc.Id && v.FinalizedAt != null && v.ApprovalStatus == ApprovalStatus.Pending && v.Id != version.Id).ToListAsync(ct))
        { older.FinalizedAt = null; older.FinalizedBy = null; older.FinalNote = null; }

        version.FinalizedAt = clock.UtcNow;
        version.FinalizedBy = user.RequireUserId();
        version.FinalNote = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim()[..Math.Min(r.Note.Trim().Length, 1000)];
        await db.SaveChangesAsync(ct);
        return (await LoadAsync(db.GeneratedDocuments.Where(d => d.Id == doc.Id), ct)).Single();
    }

    public async Task<DocumentDto> Handle(WithdrawFinalCommand r, CancellationToken ct)
    {
        var doc = await RequireAsync(r.Id, ct);
        await EnsureCanFinalizeAsync(doc, ct);
        var version = await db.DocumentVersions.FirstOrDefaultAsync(v => v.GeneratedDocumentId == doc.Id && v.VersionNo == r.VersionNo, ct) ?? throw new NotFoundException("Document version", r.VersionNo);
        await db.GeneratedDocuments.FirstAsync(d => d.Id == doc.Id, ct);
        if (version.FinalizedAt is null) return (await LoadAsync(db.GeneratedDocuments.Where(d => d.Id == doc.Id), ct)).Single();           // nothing to take back
        if (version.ApprovalStatus != ApprovalStatus.Pending)
            throw new DomainException("The administrator has already decided on this version, so it cannot be taken back.", "already_decided");

        version.FinalizedAt = null; version.FinalizedBy = null; version.FinalNote = null;
        await db.SaveChangesAsync(ct);
        return (await LoadAsync(db.GeneratedDocuments.Where(d => d.Id == doc.Id), ct)).Single();
    }

    /// <summary>A short message to a person's devices (nothing happens if they have none, and a failure never stops the work).</summary>
    private async Task TellAsync(Guid userId, string title, string body, Guid documentId, CancellationToken ct)
    {
        try { await push.SendToUserAsync(userId, title, body, new Dictionary<string, string> { ["type"] = "document", ["documentId"] = documentId.ToString() }, ct); }
        catch (Exception) { /* a message that does not arrive must not undo the decision */ }
    }

    private async Task<GeneratedDocument> RequireAsync(Guid id, CancellationToken ct) =>
        await db.GeneratedDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Document", id);

    private async Task<Guid> ProjectOfFlowAsync(Guid flowId, CancellationToken ct) =>
        await db.Flows.AsNoTracking().Where(f => f.Id == flowId).Select(f => (Guid?)f.ProjectDepartment!.ProjectId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Flow", flowId);

    // ------------------------------------------------------------------ diagrams

    public async Task<FileResultDto> Handle(GetFlowDiagramQuery r, CancellationToken ct)
    {
        var flow = await db.Flows.AsNoTracking().Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == r.FlowId, ct) ?? throw new NotFoundException("Flow", r.FlowId);
        var projectId = flow.ProjectDepartment!.ProjectId;
        await access.EnsureMemberAsync(projectId, ct);
        var versionNo = r.VersionNo ?? flow.CurrentVersionNo;
        if (versionNo <= 0) throw new DomainException("This flow has not been analysed yet, so there is no diagram.", "not_analysed");

        var version = await db.FlowVersions.AsNoTracking().FirstOrDefaultAsync(v => v.FlowId == flow.Id && v.VersionNo == versionNo, ct) ?? throw new NotFoundException("Flow version", versionNo);
        var mermaid = FlowJson.Deserialize(version.Content).Mermaid;
        var name = $"{flow.MenuName}-v{versionNo}";

        if (r.Format.Equals("mmd", StringComparison.OrdinalIgnoreCase))
            return new FileResultDto(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(mermaid)), "text/plain; charset=utf-8", $"{Safe(name)}.mmd");

        var asset = await diagrams.EnsureFlowDiagramAsync(projectId, flow.Id, versionNo, FlowJson.Deserialize(version.Content), force: false, ct, needPng: r.Format.Equals("png", StringComparison.OrdinalIgnoreCase));
        var (stream, type, ext) = await diagrams.OpenAsync(asset, r.Format, ct);
        return new FileResultDto(stream, type, $"{Safe(name)}.{ext}");
    }

    public async Task<DiagramInfoDto> Handle(RegenerateFlowDiagramCommand r, CancellationToken ct)
    {
        var flow = await db.Flows.AsNoTracking().Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == r.FlowId, ct) ?? throw new NotFoundException("Flow", r.FlowId);
        var projectId = flow.ProjectDepartment!.ProjectId;
        await access.EnsureCanCaptureAsync(projectId, flow.ProjectDepartmentId, ct);
        if (flow.CurrentVersionNo <= 0) throw new DomainException("This flow has not been analysed yet, so there is no diagram.", "not_analysed");

        var version = await db.FlowVersions.AsNoTracking().FirstAsync(v => v.FlowId == flow.Id && v.VersionNo == flow.CurrentVersionNo, ct);
        var asset = await diagrams.EnsureFlowDiagramAsync(projectId, flow.Id, flow.CurrentVersionNo, FlowJson.Deserialize(version.Content), force: true, ct, needPng: false);
        return new DiagramInfoDto("Flow", asset.FlowVersionNo, asset.Width, asset.Height, asset.Sha256 ?? "");
    }

    public async Task<FileResultDto> Handle(GetCombinedDiagramQuery r, CancellationToken ct)
    {
        await access.EnsureMemberAsync(r.ProjectId, ct);
        var asset = await diagrams.FindCombinedAsync(r.ProjectId, ct) ?? throw new NotFoundException("Combined diagram", r.ProjectId);
        var (stream, type, ext) = await diagrams.OpenAsync(asset, r.Format, ct);
        return new FileResultDto(stream, type, $"combined-flow.{ext}");
    }

    public async Task<DiagramInfoDto> Handle(GenerateCombinedDiagramCommand r, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == r.ProjectId, ct)) throw new NotFoundException("Project", r.ProjectId);
        await access.EnsureLeadAsync(r.ProjectId, ct);
        var source = await diagrams.BuildCombinedSourceAsync(r.ProjectId, ct)
                     ?? throw new DomainException("No flow in this project is confirmed yet, so there is nothing to combine.", "nothing_to_document");
        var asset = await diagrams.EnsureCombinedDiagramAsync(r.ProjectId, source, force: false, ct);
        return new DiagramInfoDto("Combined", null, asset.Width, asset.Height, asset.Sha256 ?? "");
    }

    private static string Safe(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

    // ------------------------------------------------------------------ templates

    public async Task<IReadOnlyList<DocumentTemplateDto>> Handle(ListTemplatesQuery r, CancellationToken ct) =>
        await db.DocumentTemplates.AsNoTracking().OrderByDescending(t => t.CreatedAt)
            .Select(t => new DocumentTemplateDto(t.Id, t.Name, t.Version, t.IsActive, t.CreatedAt)).ToListAsync(ct);

    public async Task<DocumentTemplateDto> Handle(UploadTemplateCommand r, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await r.Content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var problems = writer.ValidateTemplate(buffer);
        if (problems.Count > 0)
            throw new ValidationException(problems.Select(p => new FluentValidation.Results.ValidationFailure("file", p)));

        var template = new DocumentTemplate
        {
            Name = r.Name.Trim(), Version = string.IsNullOrWhiteSpace(r.Version) ? clock.UtcNow.ToString("yyyyMMdd-HHmm") : r.Version.Trim(), IsActive = false,
        };
        template.BlobPath = $"templates/{template.Id:N}.docx";
        buffer.Position = 0;
        await blobs.SaveAsync(template.BlobPath, buffer, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ct);
        db.DocumentTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return new DocumentTemplateDto(template.Id, template.Name, template.Version, template.IsActive, template.CreatedAt);
    }

    public async Task<DocumentTemplateDto> Handle(ActivateTemplateCommand r, CancellationToken ct)
    {
        var template = await db.DocumentTemplates.FirstOrDefaultAsync(t => t.Id == r.Id, ct) ?? throw new NotFoundException("Template", r.Id);
        foreach (var other in await db.DocumentTemplates.Where(t => t.IsActive && t.Id != template.Id).ToListAsync(ct)) other.IsActive = false;
        template.IsActive = true;
        await db.SaveChangesAsync(ct);
        return new DocumentTemplateDto(template.Id, template.Name, template.Version, true, template.CreatedAt);
    }
}
