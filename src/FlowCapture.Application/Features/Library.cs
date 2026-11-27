using System.Text.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Capture;
using FlowCapture.Application.Documents;
using FlowCapture.Application.Features.DocumentFeatures;
using FlowCapture.Application.Services;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.LibraryFeatures;

public sealed record CreateQuickProjectCommand(QuickProjectRequest Body) : IRequest<QuickProjectDto>;
public sealed record ListLibraryQuery : IRequest<IReadOnlyList<LibraryDocumentDto>>;
public sealed record GetLibraryDiagramQuery(Guid DocumentId, string Format) : IRequest<FileResultDto>;
/// <summary>The document of one request. <paramref name="Choice"/> is what to put in it: null prints everything.</summary>
public sealed record GenerateGuideCommand(Guid FlowId, GuideChoiceRequest? Choice = null) : IRequest<JobDto>;
/// <summary>One big PDF of every analysed request of the project, grouped by module (or of the requests chosen).</summary>
public sealed record GenerateProjectGuideCommand(Guid ProjectId, GuideChoiceRequest? Choice = null) : IRequest<JobDto>;
/// <summary>A smaller PDF of every analysed request of one module (or of the requests chosen).</summary>
public sealed record GenerateModuleGuideCommand(Guid ProjectId, Guid ModuleId, GuideChoiceRequest? Choice = null) : IRequest<JobDto>;
/// <summary>What a request's document would contain, to choose from before it is made.</summary>
public sealed record GuideOutlineQuery(Guid FlowId) : IRequest<GuideOutlineDto>;
public sealed record ProjectGuideOutlineQuery(Guid ProjectId, Guid? ModuleId) : IRequest<GuideOutlineDto>;
public sealed record AddImageCommand(Guid FlowId, Guid EntryId, Stream Content, string ContentType, string Language, string? Notes) : IRequest<EntryDto>;
public sealed record AddFileNoteCommand(Guid FlowId, Guid EntryId, Stream Content, string FileName, string Language) : IRequest<EntryDto>;
public sealed record StreamImageQuery(Guid EntryId) : IRequest<ImageStreamResult>;
public sealed record ImageStreamResult(Stream Stream, string ContentType);
public sealed record GetAdminOverviewQuery : IRequest<AdminOverviewDto>;

public sealed class CreateQuickProjectValidator : AbstractValidator<CreateQuickProjectCommand>
{
    public CreateQuickProjectValidator()
    {
        RuleFor(x => x.Body.Name).NotEmpty().WithMessage("Give the project a name.").MaximumLength(120);
        RuleFor(x => x.Body.Description).MaximumLength(500);
    }
}

public sealed class LibraryHandlers(
    IAppDbContext db, IProjectAccess access, ICurrentUser user, IClock clock, IJobScheduler jobs, ImageService images,
    DiagramService diagrams, IAiStatusService aiStatus, FileNoteService fileNotes, GuideGenerationService guides, CombinedGuideService combined) :
    IRequestHandler<GuideOutlineQuery, GuideOutlineDto>,
    IRequestHandler<ProjectGuideOutlineQuery, GuideOutlineDto>,
    IRequestHandler<AddFileNoteCommand, EntryDto>,
    IRequestHandler<GenerateProjectGuideCommand, JobDto>,
    IRequestHandler<GenerateModuleGuideCommand, JobDto>,
    IRequestHandler<CreateQuickProjectCommand, QuickProjectDto>,
    IRequestHandler<ListLibraryQuery, IReadOnlyList<LibraryDocumentDto>>,
    IRequestHandler<GetLibraryDiagramQuery, FileResultDto>,
    IRequestHandler<GenerateGuideCommand, JobDto>,
    IRequestHandler<AddImageCommand, EntryDto>,
    IRequestHandler<StreamImageQuery, ImageStreamResult>,
    IRequestHandler<GetAdminOverviewQuery, AdminOverviewDto>
{
    // ------------------------------------------------------------------ a new project in one step

    /// <summary>
    /// A project with one workflow, ready for notes: the person who makes it becomes its lead, and a single "General" department and a workflow
    /// named like the project are created, so the user never has to know about departments and menus. More workflows can be added later.
    /// </summary>
    public async Task<QuickProjectDto> Handle(CreateQuickProjectCommand r, CancellationToken ct)
    {
        var uid = user.RequireUserId();
        var name = r.Body.Name.Trim();
        var code = await NewCodeAsync(name, ct);
        var client = await ChooseClientAsync(uid, r.Body.ClientId, ct);

        var project = new Project { Code = code, HospitalName = name, Location = r.Body.Description?.Trim() ?? "", StartDate = DateOnly.FromDateTime(clock.UtcNow), LeadUserId = uid, ClientId = client.Id };
        project.Members.Add(new ProjectMember { UserId = uid, ProjectRole = ProjectRole.Lead });
        await Clients.ClientAccess.AddClientUsersToProjectAsync(db, project, ct);       // the colleagues on this client can open it too
        var department = new ProjectDepartment { Project = project, CustomName = "General", ModuleCode = "GEN" };
        var flow = new Flow { ProjectDepartment = department, MenuName = name.Length > 200 ? name[..200] : name, Kind = RequestKind.NR, RequestNo = 1, LastActivityAt = clock.UtcNow };
        db.Projects.Add(project);
        db.ProjectDepartments.Add(department);
        db.Flows.Add(flow);
        await db.SaveChangesAsync(ct);
        return new QuickProjectDto(project.Id, department.Id, flow.Id, name, code);
    }

    /// <summary>
    /// The client a new project belongs to. A user must pick one of the active clients they are assigned to (when they have only one, it is used);
    /// an administrator may pick any active client.
    /// </summary>
    private async Task<Client> ChooseClientAsync(Guid uid, Guid? wanted, CancellationToken ct)
    {
        var admin = user.IsInRole(Shared.AppRoles.Admin);
        var usable = db.Clients.Where(c => c.IsActive && (admin || c.Users.Any(u => u.UserId == uid)));
        if (wanted is { } id)
            return await usable.FirstOrDefaultAsync(c => c.Id == id, ct)
                   ?? throw new DomainException("You are not assigned to this client, or it has been stopped. Ask an administrator.", "client_not_allowed");
        var options = await usable.OrderBy(c => c.Name).Take(2).ToListAsync(ct);
        if (options.Count == 1) return options[0];
        throw new DomainException(options.Count == 0
            ? "You are not assigned to any client yet. Ask an administrator to assign you to one, then start the project."
            : "Choose the client this project is for.", options.Count == 0 ? "no_client" : "client_required");
    }

    private async Task<string> NewCodeAsync(string name, CancellationToken ct)
    {
        var letters = new string(name.Where(char.IsLetterOrDigit).Take(4).ToArray()).ToUpperInvariant();
        if (letters.Length < 2) letters = "PRJ";
        for (var i = 0; i < 20; i++)
        {
            var code = $"{letters}-{Random.Shared.Next(1000, 9999)}";
            if (!await db.Projects.AnyAsync(p => p.Code == code, ct)) return code;
        }
        return $"{letters}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
    }

    // ------------------------------------------------------------------ the library everybody can browse

    public async Task<IReadOnlyList<LibraryDocumentDto>> Handle(ListLibraryQuery r, CancellationToken ct)
    {
        user.RequireUserId();
        // only the documents of projects the person can open: their own and those of the clients they are assigned to. The administrator sees only the
        // documents that users marked final (the version that was marked), never the working copies.
        var admin = user.IsInRole(Shared.AppRoles.Admin);
        var mine = await access.MemberProjectIdsAsync(ct);
        var docs = await db.GeneratedDocuments.AsNoTracking().Where(d => d.CurrentVersionNo > 0 && mine.Contains(d.ProjectId)).ToListAsync(ct);
        var ids = docs.Select(d => d.Id).ToList();
        var versionQuery = db.DocumentVersions.AsNoTracking().Where(v => ids.Contains(v.GeneratedDocumentId));
        if (admin) versionQuery = versionQuery.Where(v => v.FinalizedAt != null);
        var versions = await versionQuery.ToListAsync(ct);
        if (admin) docs = docs.Where(d => versions.Any(v => v.GeneratedDocumentId == d.Id)).ToList();
        var projectIds = docs.Select(d => d.ProjectId).Distinct().ToList();
        var projectRows = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).Select(p => new { p.Id, p.HospitalName, p.ClientId, ClientName = p.Client != null ? p.Client.Name : null }).ToListAsync(ct);
        var projects = projectRows.ToDictionary(p => p.Id);
        docs = docs.Where(d => projects.ContainsKey(d.ProjectId)).ToList();             // the documents of a deleted project are not listed
        var flowIds = docs.Where(d => d.FlowId.HasValue).Select(d => d.FlowId!.Value).Distinct().ToList();
        var liveFlows = (await db.Flows.AsNoTracking().Where(f => flowIds.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct)).ToHashSet();
        docs = docs.Where(d => d.FlowId is null || liveFlows.Contains(d.FlowId.Value)).ToList();     // nor those of a deleted request
        var userIds = versions.SelectMany(v => new[] { (Guid?)v.GeneratedBy, v.FinalizedBy, v.ApprovedBy }).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? NameOf(Guid? id) => id is { } g ? names.GetValueOrDefault(g) : null;

        return docs.Select(d =>
        {
            var latest = versions.Where(v => v.GeneratedDocumentId == d.Id).OrderByDescending(v => v.VersionNo).First();      // for the administrator: the latest FINAL version
            var kind = d.Scope switch { DocumentScope.Guide => "Guide", DocumentScope.GuideModule => "ModuleGuide", DocumentScope.GuideProject => "ProjectGuide", _ => "Requirements" };
            var project = projects[d.ProjectId];
            var stage = DocumentHandlers.StageOf(latest);
            return new LibraryDocumentDto(d.Id, d.DocNumber, d.Title, kind, d.ProjectId,
                project.HospitalName, d.FlowId, latest.VersionNo, latest.GeneratedAt, names.GetValueOrDefault(latest.GeneratedBy, "System"),
                string.IsNullOrEmpty(latest.DocxPath) ? ["pdf"] : ["pdf", "docx"], d.Scope is DocumentScope.Guide or DocumentScope.Flow, d.ProjectDepartmentId,
                stage, latest.FinalizedAt, NameOf(latest.FinalizedBy), latest.ApprovalNote, NameOf(latest.ApprovedBy), latest.ApprovedAt, project.ClientId, project.ClientName, latest.FinalNote);
        }).OrderByDescending(d => d.FinalizedAt ?? d.CreatedAt).ToList();
    }
    public async Task<FileResultDto> Handle(GetLibraryDiagramQuery r, CancellationToken ct)
    {
        user.RequireUserId();
        var doc = await db.GeneratedDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == r.DocumentId, ct) ?? throw new NotFoundException("Document", r.DocumentId);
        await access.EnsureMemberAsync(doc.ProjectId, ct);
        var versionsOf = db.DocumentVersions.AsNoTracking().Where(v => v.GeneratedDocumentId == doc.Id);
        if (user.IsInRole(Shared.AppRoles.Admin)) versionsOf = versionsOf.Where(v => v.FinalizedAt != null);            // the administrator sees only final documents
        var latest = await versionsOf.OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(ct) ?? throw new ForbiddenException("The administrator can only open documents that were marked final.");

        DiagramAsset? asset;
        if (doc.Scope == DocumentScope.Project) asset = await diagrams.FindCombinedAsync(doc.ProjectId, ct);
        else
        {
            var source = (JsonSerializer.Deserialize<JsonElement[]>(latest.SourceFlowVersions) ?? []).FirstOrDefault();
            asset = source.ValueKind == JsonValueKind.Object ? await diagrams.FindFlowDiagramAsync(doc.FlowId ?? Guid.Empty, source.GetProperty("versionNo").GetInt32(), ct) : null;
        }
        if (asset is null) throw new NotFoundException("Diagram for document", r.DocumentId);
        var (stream, type, ext) = await diagrams.OpenAsync(asset, r.Format, ct);
        return new FileResultDto(stream, type, $"{doc.DocNumber}-diagram.{ext}");
    }

    // ------------------------------------------------------------------ the implementation guide

    public async Task<JobDto> Handle(GenerateGuideCommand r, CancellationToken ct)
    {
        var flow = await db.Flows.AsNoTracking().Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == r.FlowId, ct) ?? throw new NotFoundException("Flow", r.FlowId);
        var pd = flow.ProjectDepartment!;
        await access.EnsureCanCaptureAsync(pd.ProjectId, pd.Id, ct);
        if (flow.CurrentVersionNo <= 0) throw new DomainException("Analyse the notes first. There is nothing to write a guide from yet.", "not_analysed");

        var running = await db.DocumentJobRuns.AsNoTracking().FirstOrDefaultAsync(j => j.FlowId == flow.Id && j.Scope == DocumentScope.Guide
            && (j.Status == DocJobStatus.Queued || j.Status == DocJobStatus.Running), ct);
        if (running is not null) return DocumentHandlers.ToDto(running);

        var sections = GuideSections.Parse(r.Choice?.Sections);                      // refuses an empty choice or an unknown section
        var titles = RenamedHeadings(r.Choice, sections);                            // refuses an unknown section or a heading that is too long
        var texts = WrittenTexts(r.Choice, sections, null);                          // refuses an unknown or not editable section, or text that is too long
        var job = new DocumentJobRun { ProjectId = pd.ProjectId, Scope = DocumentScope.Guide, FlowId = flow.Id, IncludeDiagrams = true, RequestedBy = user.RequireUserId(),
            SectionsJson = sections is null ? null : JsonSerializer.Serialize(sections.OrderBy(s => s)), TitlesJson = titles, TextsJson = texts };
        db.DocumentJobRuns.Add(job);
        await db.SaveChangesAsync(ct);
        jobs.EnqueueGuide(job.Id);
        return DocumentHandlers.ToDto(job);
    }

    /// <summary>The headings a request's document would have, so the person can choose which to print.</summary>
    public async Task<GuideOutlineDto> Handle(GuideOutlineQuery r, CancellationToken ct)
    {
        var flow = await db.Flows.AsNoTracking().Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == r.FlowId, ct) ?? throw new NotFoundException("Flow", r.FlowId);
        await access.EnsureCanCaptureAsync(flow.ProjectDepartment!.ProjectId, flow.ProjectDepartmentId, ct);
        return await guides.OutlineAsync(r.FlowId, user.RequireUserId(), ct);
    }

    /// <summary>The headings (and requests) a project's or a module's PDF would have, so the person can choose.</summary>
    public async Task<GuideOutlineDto> Handle(ProjectGuideOutlineQuery r, CancellationToken ct)
    {
        if (r.ModuleId is { } m)
        {
            if (!await db.ProjectDepartments.AnyAsync(d => d.Id == m && d.ProjectId == r.ProjectId, ct)) throw new NotFoundException("Module", m);
            await access.EnsureCanCaptureAsync(r.ProjectId, m, ct);
        }
        else
        {
            if (!await db.Projects.AnyAsync(p => p.Id == r.ProjectId, ct)) throw new NotFoundException("Project", r.ProjectId);
            if (user.IsInRole(Shared.AppRoles.Admin)) throw new ForbiddenException("The administrator does not make documents. Users make them and mark them final; you verify the final ones.");
            if (await access.GetRoleAsync(r.ProjectId, ct) is not (ProjectRole.Lead or ProjectRole.Collaborator))
                throw new ForbiddenException("Only the project's lead and the colleagues on its client can make the PDF of the whole project.");
        }
        return await combined.OutlineAsync(r.ProjectId, r.ModuleId, ct);
    }

    // ------------------------------------------------------------------ one big PDF for the project, and one for each module

    /// <summary>The whole project in one PDF. For the project's lead, the colleagues on its client and administrators (not for someone who may only work in one module).</summary>
    public async Task<JobDto> Handle(GenerateProjectGuideCommand r, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == r.ProjectId, ct)) throw new NotFoundException("Project", r.ProjectId);
        if (user.IsInRole(Shared.AppRoles.Admin)) throw new ForbiddenException("The administrator does not make documents. Users make them and mark them final; you verify the final ones.");
        var role = await access.GetRoleAsync(r.ProjectId, ct);
        if (role is not (ProjectRole.Lead or ProjectRole.Collaborator))
            throw new ForbiddenException("Only the project's lead and the colleagues on its client can make the PDF of the whole project.");
        return await QueueCombinedAsync(r.ProjectId, null, DocumentScope.GuideProject, r.Choice, ct);
    }

    /// <summary>The requests of one module in one PDF. For anyone who may work in that module.</summary>
    public async Task<JobDto> Handle(GenerateModuleGuideCommand r, CancellationToken ct)
    {
        if (!await db.ProjectDepartments.AnyAsync(d => d.Id == r.ModuleId && d.ProjectId == r.ProjectId, ct)) throw new NotFoundException("Module", r.ModuleId);
        await access.EnsureCanCaptureAsync(r.ProjectId, r.ModuleId, ct);
        return await QueueCombinedAsync(r.ProjectId, r.ModuleId, DocumentScope.GuideModule, r.Choice, ct);
    }

    private async Task<JobDto> QueueCombinedAsync(Guid projectId, Guid? moduleId, DocumentScope scope, GuideChoiceRequest? choice, CancellationToken ct)
    {
        var sections = GuideSections.Parse(choice?.Sections);
        var titles = RenamedHeadings(choice, sections);
        var wanted = choice?.FlowIds?.Distinct().ToList() ?? [];
        var texts = WrittenTexts(choice, sections, wanted);
        var analysed = db.Flows.AsNoTracking().Where(f => f.ProjectDepartment!.ProjectId == projectId && f.CurrentVersionNo > 0);
        if (moduleId is { } m) analysed = analysed.Where(f => f.ProjectDepartmentId == m);
        if (wanted.Count > 0) analysed = analysed.Where(f => wanted.Contains(f.Id));
        if (!await analysed.AnyAsync(ct))
            throw new DomainException(wanted.Count > 0 ? "None of the chosen requests has been analysed yet. Choose a request that has been analysed."
                : moduleId is null ? "No request in this project has been analysed yet. Analyse at least one request first." : "No request in this module has been analysed yet. Analyse at least one request first.", "not_analysed");

        var running = await db.DocumentJobRuns.AsNoTracking().FirstOrDefaultAsync(j => j.ProjectId == projectId && j.Scope == scope && j.ProjectDepartmentId == moduleId
            && (j.Status == DocJobStatus.Queued || j.Status == DocJobStatus.Running), ct);
        if (running is not null) return DocumentHandlers.ToDto(running);

        var job = new DocumentJobRun { ProjectId = projectId, Scope = scope, ProjectDepartmentId = moduleId, IncludeDiagrams = true, RequestedBy = user.RequireUserId(),
            SectionsJson = sections is null ? null : JsonSerializer.Serialize(sections.OrderBy(s => s)), FlowIdsJson = JsonSerializer.Serialize(wanted), TitlesJson = titles, TextsJson = texts };
        db.DocumentJobRuns.Add(job);
        await db.SaveChangesAsync(ct);
        jobs.EnqueueGuide(job.Id);
        return DocumentHandlers.ToDto(job);
    }

    /// <summary>The headings the person renamed, checked and stored as JSON (null when none). A heading of a section that was not chosen is dropped: it is never printed.</summary>
    private static string? RenamedHeadings(GuideChoiceRequest? choice, IReadOnlySet<string>? sections)
    {
        var titles = GuideSections.ParseTitles(choice?.Titles);
        if (titles is not null && sections is not null) titles = titles.Where(t => sections.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value);
        return titles is null || titles.Count == 0 ? null : JsonSerializer.Serialize(titles);
    }

    /// <summary>
    /// The text the person wrote under headings, checked and stored as JSON (null when none). Text of a section that was not chosen, or of a request that was not chosen
    /// (<paramref name="requests"/>, when some were), is dropped: it is never printed. A request's own document keeps plain section keys; a bigger one needs the request in front.
    /// </summary>
    private static string? WrittenTexts(GuideChoiceRequest? choice, IReadOnlySet<string>? sections, IReadOnlyCollection<Guid>? requests)
    {
        var texts = GuideSections.ParseTexts(choice?.Texts);
        if (texts is null) return null;
        var kept = texts.Where(t =>
        {
            var slash = t.Key.IndexOf('/');
            var key = slash < 0 ? t.Key : t.Key[(slash + 1)..];
            if (sections is not null && !sections.Contains(key)) return false;
            if (requests is null) return slash < 0;                                           // a request's own document has no requests in its choice
            return slash >= 0 && (requests.Count == 0 || requests.Contains(Guid.Parse(t.Key[..slash])));
        }).ToDictionary(t => t.Key, t => t.Value);
        return kept.Count == 0 ? null : JsonSerializer.Serialize(kept);
    }

    // ------------------------------------------------------------------ photos of notes

    public Task<EntryDto> Handle(AddFileNoteCommand r, CancellationToken ct) => fileNotes.AddAsync(r.FlowId, r.EntryId, r.Content, r.FileName, r.Language, ct);

    public Task<EntryDto> Handle(AddImageCommand r, CancellationToken ct) => images.AddAsync(r.FlowId, r.EntryId, r.Content, r.ContentType, r.Language, r.Notes, ct);

    public async Task<ImageStreamResult> Handle(StreamImageQuery r, CancellationToken ct)
    {
        var (stream, type) = await images.OpenAsync(r.EntryId, ct);
        return new ImageStreamResult(stream, type);
    }

    // ------------------------------------------------------------------ admin dashboard numbers

    public async Task<AdminOverviewDto> Handle(GetAdminOverviewQuery r, CancellationToken ct)
    {
        if (!user.IsInRole(Shared.AppRoles.Admin)) throw new ForbiddenException("Only an administrator can see this.");
        var status = await aiStatus.GetAsync(ct);
        return new AdminOverviewDto(
            await db.Users.CountAsync(u => u.DeletedAt == null, ct), await db.Users.CountAsync(u => u.IsActive && u.DeletedAt == null, ct), await db.Projects.CountAsync(ct), await db.Flows.CountAsync(f => db.Projects.Any(p => p.Id == f.ProjectDepartment!.ProjectId), ct),
            await db.CaptureEntries.CountAsync(e => !e.IsDeleted && db.Projects.Any(p => p.Id == e.Flow!.ProjectDepartment!.ProjectId), ct), await db.GeneratedDocuments.CountAsync(d => d.CurrentVersionNo > 0 && db.Projects.Any(p => p.Id == d.ProjectId), ct), status.Provider, status.Ready,
            await db.DocumentVersions.CountAsync(v => v.FinalizedAt != null && v.ApprovalStatus == ApprovalStatus.Pending && db.GeneratedDocuments.Any(d => d.Id == v.GeneratedDocumentId && db.Projects.Any(p => p.Id == d.ProjectId)), ct),
            await db.Clients.CountAsync(ct));
    }
}
