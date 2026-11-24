using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.Projects;

public sealed record ListProjectsQuery : IRequest<IReadOnlyList<ProjectDto>>;
public sealed record CreateProjectCommand(CreateProjectRequest Body) : IRequest<ProjectDto>;
public sealed record GetProjectQuery(Guid Id) : IRequest<ProjectDto>;
public sealed record DeleteProjectCommand(Guid Id) : IRequest;
public sealed record UpdateProjectCommand(Guid Id, UpdateProjectRequest Body, string? IfMatch) : IRequest<ProjectDto>;
public sealed record GetMembersQuery(Guid ProjectId) : IRequest<IReadOnlyList<ProjectMemberDto>>;
public sealed record SetMembersCommand(Guid ProjectId, SetMembersRequest Body) : IRequest<IReadOnlyList<ProjectMemberDto>>;
public sealed record ListProjectDepartmentsQuery(Guid ProjectId) : IRequest<IReadOnlyList<ProjectDepartmentDto>>;
public sealed record AddProjectDepartmentCommand(Guid ProjectId, AddProjectDepartmentRequest Body) : IRequest<ProjectDepartmentDto>;
public sealed record SetAssignmentsCommand(Guid ProjectId, Guid ProjectDepartmentId, SetAssignmentsRequest Body) : IRequest<ProjectDepartmentDto>;

public sealed class CreateProjectValidator : AbstractValidator<CreateProjectCommand>
{
    public CreateProjectValidator()
    {
        RuleFor(x => x.Body.HospitalName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.Code).NotEmpty().Matches("^[A-Za-z0-9_-]{2,30}$").WithMessage("Project code must be 2-30 characters: letters, digits, _ or -.");
        RuleFor(x => x.Body.Location).MaximumLength(200);
        RuleFor(x => x.Body.Status).Must(s => Enum.TryParse<ProjectStatus>(s, true, out _)).WithMessage("Status must be Active, OnHold or Completed.");
    }
}

public sealed class UpdateProjectValidator : AbstractValidator<UpdateProjectCommand>
{
    public UpdateProjectValidator()
    {
        RuleFor(x => x.Body.HospitalName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.Location).MaximumLength(200);
        RuleFor(x => x.Body.Status).Must(s => Enum.TryParse<ProjectStatus>(s, true, out _)).WithMessage("Status must be Active, OnHold or Completed.");
        RuleFor(x => x.Body.MonthlyAiTokenLimit).Must(l => l is null or >= 0).WithMessage("The limit cannot be negative.");
    }
}

public sealed class AddProjectDepartmentValidator : AbstractValidator<AddProjectDepartmentCommand>
{
    public AddProjectDepartmentValidator()
    {
        // modules are created by the administrator (Admin > Modules); a project can only take one of them, never a typed name
        RuleFor(x => x.Body.DepartmentId).NotNull().WithMessage("Choose a module from the list. Modules are created by the administrator.");
    }
}

public sealed class ProjectHandlers(IAppDbContext db, IProjectAccess access, ICurrentUser user, IClock clock) :
    IRequestHandler<ListProjectsQuery, IReadOnlyList<ProjectDto>>,
    IRequestHandler<CreateProjectCommand, ProjectDto>,
    IRequestHandler<GetProjectQuery, ProjectDto>,
    IRequestHandler<UpdateProjectCommand, ProjectDto>,
    IRequestHandler<DeleteProjectCommand>,
    IRequestHandler<GetMembersQuery, IReadOnlyList<ProjectMemberDto>>,
    IRequestHandler<SetMembersCommand, IReadOnlyList<ProjectMemberDto>>,
    IRequestHandler<ListProjectDepartmentsQuery, IReadOnlyList<ProjectDepartmentDto>>,
    IRequestHandler<AddProjectDepartmentCommand, ProjectDepartmentDto>,
    IRequestHandler<SetAssignmentsCommand, ProjectDepartmentDto>
{
    public async Task<IReadOnlyList<ProjectDto>> Handle(ListProjectsQuery r, CancellationToken ct)
    {
        var ids = await access.MemberProjectIdsAsync(ct);
        var rows = await db.Projects.AsNoTracking().Include(p => p.Lead).Include(p => p.Client).Where(p => ids.Contains(p.Id)).OrderBy(p => p.HospitalName).ToListAsync(ct);
        return rows.Select(p => p.ToDto(p.Lead?.DisplayName ?? "")).ToList();
    }

    public async Task<ProjectDto> Handle(CreateProjectCommand r, CancellationToken ct)
    {
        var b = r.Body;
        var code = b.Code.Trim().ToUpperInvariant();
        if (await db.Projects.AnyAsync(p => p.Code == code, ct)) throw new ConflictException($"Project code '{code}' is already used.");
        var lead = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == b.LeadUserId && u.IsActive, ct)
                   ?? throw new DomainException("The Implementation Lead must be an active user.", "invalid_lead");

        var project = new Project
        {
            Code = code, HospitalName = b.HospitalName.Trim(), Location = b.Location?.Trim() ?? "", StartDate = b.StartDate,
            LeadUserId = lead.Id, Status = Enum.Parse<ProjectStatus>(b.Status, true),
        };
        project.Members.Add(new ProjectMember { UserId = lead.Id, ProjectRole = ProjectRole.Lead });
        var uid = user.RequireUserId();
        if (uid != lead.Id && !user.IsInRole(Shared.AppRoles.Admin))
            project.Members.Add(new ProjectMember { UserId = uid, ProjectRole = ProjectRole.Lead });
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return project.ToDto(lead.DisplayName);
    }

    /// <summary>
    /// Deletes a project: it disappears from every list and cannot be opened, but nothing is erased. Its notes, documents and the AI usage it caused stay
    /// in the database (the usage was really spent), so a deleted project can be brought back by clearing DeletedAt. Only the project's lead or an administrator.
    /// </summary>
    public async Task Handle(DeleteProjectCommand r, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == r.Id, ct) ?? throw new NotFoundException("Project", r.Id);
        await access.EnsureLeadAsync(r.Id, ct);
        project.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProjectDto> Handle(GetProjectQuery r, CancellationToken ct)
    {
        var p = await db.Projects.AsNoTracking().Include(x => x.Lead).Include(x => x.Client).FirstOrDefaultAsync(x => x.Id == r.Id, ct) ?? throw new NotFoundException("Project", r.Id);
        await access.EnsureMemberAsync(p.Id, ct);
        return p.ToDto(p.Lead?.DisplayName ?? "");
    }

    public async Task<ProjectDto> Handle(UpdateProjectCommand r, CancellationToken ct)
    {
        var p = await db.Projects.Include(x => x.Members).Include(x => x.Client).FirstOrDefaultAsync(x => x.Id == r.Id, ct) ?? throw new NotFoundException("Project", r.Id);
        await access.EnsureLeadAsync(p.Id, ct);
        if (r.IfMatch is not null && r.IfMatch != p.ConcurrencyStamp) throw new ConflictException("The project was changed by someone else.");

        var b = r.Body;
        if (b.LeadUserId != p.LeadUserId)
        {
            if (!await db.Users.AnyAsync(u => u.Id == b.LeadUserId && u.IsActive, ct))
                throw new DomainException("The Implementation Lead must be an active user.", "invalid_lead");
            var member = p.Members.FirstOrDefault(m => m.UserId == b.LeadUserId);
            if (member is null) p.Members.Add(new ProjectMember { ProjectId = p.Id, UserId = b.LeadUserId, ProjectRole = ProjectRole.Lead });
            else member.ProjectRole = ProjectRole.Lead;
            p.LeadUserId = b.LeadUserId;
        }
        p.HospitalName = b.HospitalName.Trim();
        p.Location = b.Location?.Trim() ?? "";
        p.StartDate = b.StartDate;
        p.Status = Enum.Parse<ProjectStatus>(b.Status, true);
        p.MonthlyAiTokenLimit = b.MonthlyAiTokenLimit;
        await db.SaveChangesAsync(ct);
        var leadName = await db.Users.AsNoTracking().Where(u => u.Id == p.LeadUserId).Select(u => u.DisplayName).FirstAsync(ct);
        return p.ToDto(leadName);
    }

    public async Task<IReadOnlyList<ProjectMemberDto>> Handle(GetMembersQuery r, CancellationToken ct)
    {
        await access.EnsureMemberAsync(r.ProjectId, ct);
        return await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == r.ProjectId).OrderBy(m => m.User!.DisplayName)
            .Select(m => new ProjectMemberDto(m.UserId, m.User!.DisplayName, m.ProjectRole.ToString())).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ProjectMemberDto>> Handle(SetMembersCommand r, CancellationToken ct)
    {
        var project = await db.Projects.Include(p => p.Members).FirstOrDefaultAsync(p => p.Id == r.ProjectId, ct) ?? throw new NotFoundException("Project", r.ProjectId);
        await access.EnsureLeadAsync(r.ProjectId, ct);

        var wanted = new Dictionary<Guid, ProjectRole>();
        foreach (var m in r.Body.Members)
        {
            if (!Enum.TryParse<ProjectRole>(m.ProjectRole, true, out var role))
                throw new DomainException($"Unknown project role '{m.ProjectRole}'.", "invalid_role");
            wanted[m.UserId] = role;
        }
        if (!wanted.TryGetValue(project.LeadUserId, out var leadRole) || leadRole != ProjectRole.Lead)
            throw new DomainException("The project's Implementation Lead must stay a member with the Lead role. Change the lead on the project first.", "lead_required");

        var users = await db.Users.AsNoTracking().Where(u => wanted.Keys.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        if (users.Count != wanted.Count) throw new DomainException("Every member must be an existing, active user.", "invalid_member");

        project.Members.RemoveAll(m => !wanted.ContainsKey(m.UserId));
        foreach (var (userId, role) in wanted)
        {
            var existing = project.Members.FirstOrDefault(m => m.UserId == userId);
            if (existing is null) project.Members.Add(new ProjectMember { ProjectId = project.Id, UserId = userId, ProjectRole = role });
            else existing.ProjectRole = role;
        }

        // Anyone removed from the project loses their department assignments in it.
        var removedAssignments = await db.DepartmentAssignments
            .Where(a => a.ProjectDepartment!.ProjectId == project.Id && !wanted.Keys.Contains(a.UserId)).ToListAsync(ct);
        db.DepartmentAssignments.RemoveRange(removedAssignments);
        await db.SaveChangesAsync(ct);
        return await Handle(new GetMembersQuery(r.ProjectId), ct);
    }

    public async Task<IReadOnlyList<ProjectDepartmentDto>> Handle(ListProjectDepartmentsQuery r, CancellationToken ct)
    {
        await access.EnsureMemberAsync(r.ProjectId, ct);
        return await LoadDepartmentsAsync(r.ProjectId, null, ct);
    }

    public async Task<ProjectDepartmentDto> Handle(AddProjectDepartmentCommand r, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == r.ProjectId, ct)) throw new NotFoundException("Project", r.ProjectId);
        await access.EnsureLeadAsync(r.ProjectId, ct);

        var pd = new ProjectDepartment { ProjectId = r.ProjectId };
        var did = r.Body.DepartmentId!.Value;
        var master = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == did && d.IsActive, ct)
                     ?? throw new DomainException("This module is not in the administrator's list (or it was stopped). Choose one from the list.", "module_not_available");
        pd.DepartmentId = master.Id;
        pd.ModuleCode = master.Code;
        if (await db.ProjectDepartments.AnyAsync(x => x.ProjectId == r.ProjectId && x.ModuleCode == pd.ModuleCode, ct))
            throw new ConflictException($"This project already has a department with module code '{pd.ModuleCode}'.");

        db.ProjectDepartments.Add(pd);
        await db.SaveChangesAsync(ct);
        return (await LoadDepartmentsAsync(r.ProjectId, pd.Id, ct)).Single();
    }

    public async Task<ProjectDepartmentDto> Handle(SetAssignmentsCommand r, CancellationToken ct)
    {
        var pd = await db.ProjectDepartments.Include(x => x.Assignments)
                     .FirstOrDefaultAsync(x => x.Id == r.ProjectDepartmentId && x.ProjectId == r.ProjectId, ct)
                 ?? throw new NotFoundException("Project department", r.ProjectDepartmentId);
        await access.EnsureLeadAsync(r.ProjectId, ct);

        var wanted = r.Body.UserIds.Distinct().ToList();
        var members = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == r.ProjectId && wanted.Contains(m.UserId) && m.ProjectRole == ProjectRole.Executive)
            .Select(m => m.UserId).ToListAsync(ct);
        if (members.Count != wanted.Count)
            throw new DomainException("Only project members with the Executive role can be assigned to a department.", "invalid_assignee");

        pd.Assignments.RemoveAll(a => !wanted.Contains(a.UserId));
        foreach (var uid in wanted.Where(uid => pd.Assignments.All(a => a.UserId != uid)))
            pd.Assignments.Add(new DepartmentAssignment { ProjectDepartmentId = pd.Id, UserId = uid });
        await db.SaveChangesAsync(ct);
        return (await LoadDepartmentsAsync(r.ProjectId, pd.Id, ct)).Single();
    }

    private async Task<IReadOnlyList<ProjectDepartmentDto>> LoadDepartmentsAsync(Guid projectId, Guid? onlyId, CancellationToken ct)
    {
        var rows = await db.ProjectDepartments.AsNoTracking()
            .Where(pd => pd.ProjectId == projectId && pd.IsActive && (onlyId == null || pd.Id == onlyId))
            .OrderBy(pd => pd.ModuleCode)
            .Select(pd => new
            {
                pd.Id, pd.ModuleCode, Name = pd.CustomName ?? pd.Department!.Name, IsCustom = pd.DepartmentId == null,
                Assignees = pd.Assignments.Select(a => new UserRefDto(a.UserId, a.User!.DisplayName)).ToList(),
                FlowCount = pd.Flows.Count,
                EntryCount = pd.Flows.SelectMany(f => f.Entries).Count(e => !e.IsDeleted),
                // Enums are stored as text, so never use < or > on them inside a query.
                ConfirmedCount = pd.Flows.Count(f => f.Status == FlowStatus.Confirmed || f.Status == FlowStatus.DocumentsGenerated),
            }).ToListAsync(ct);
        return rows.Select(x => new ProjectDepartmentDto(x.Id, x.ModuleCode, x.Name, x.IsCustom, x.Assignees, x.FlowCount, x.EntryCount, x.ConfirmedCount)).ToList();
    }
}
