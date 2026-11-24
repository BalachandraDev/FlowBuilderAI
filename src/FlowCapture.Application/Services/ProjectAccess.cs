using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using FlowCapture.Shared;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Services;

/// <summary>Per-project access control. Checked in every project-scoped handler, never only in the UI.</summary>
public sealed class ProjectAccess(IAppDbContext db, ICurrentUser user) : IProjectAccess
{
    public async Task<ProjectRole?> GetRoleAsync(Guid projectId, CancellationToken ct = default)
    {
        // a deleted project cannot be opened by anybody, whatever their role (the filter hides it from this query)
        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct)) return null;
        if (user.IsInRole(AppRoles.Admin)) return ProjectRole.Lead;
        if (user.UserId is not { } uid) return null;
        var member = await db.ProjectMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == uid, ct);
        return member?.ProjectRole;
    }

    public async Task EnsureMemberAsync(Guid projectId, CancellationToken ct = default)
    {
        if (await GetRoleAsync(projectId, ct) is null) throw new ForbiddenException("You are not a member of this project.");
    }

    public async Task EnsureLeadAsync(Guid projectId, CancellationToken ct = default)
    {
        if (await GetRoleAsync(projectId, ct) != ProjectRole.Lead)
            throw new ForbiddenException("Only the Implementation Lead of this project can do this.");
    }

    public async Task EnsureCanCaptureAsync(Guid projectId, Guid projectDepartmentId, CancellationToken ct = default)
    {
        // the administrator manages users and clients and verifies the final documents; the users do the work (notes, analysis, documents)
        if (user.IsInRole(AppRoles.Admin)) throw new ForbiddenException("The administrator does not work on requests. Users make the documents and mark them final; you verify the final ones.");
        var role = await GetRoleAsync(projectId, ct);
        switch (role)
        {
            case ProjectRole.Lead:
            case ProjectRole.Collaborator:      // a colleague on the same client can add notes and make documents in every module
                return;
            case ProjectRole.Executive:
                var uid = user.RequireUserId();
                var assigned = await db.DepartmentAssignments.AsNoTracking()
                    .AnyAsync(a => a.ProjectDepartmentId == projectDepartmentId && a.UserId == uid, ct);
                if (!assigned) throw new ForbiddenException("You are not assigned to this department.");
                return;
            default:
                throw new ForbiddenException("Your role in this project cannot capture or edit flows.");
        }
    }

    public async Task<IReadOnlyList<Guid>> MemberProjectIdsAsync(CancellationToken ct = default)
    {
        if (user.IsInRole(AppRoles.Admin))
            return await db.Projects.AsNoTracking().Select(p => p.Id).ToListAsync(ct);
        var uid = user.RequireUserId();
        return await db.ProjectMembers.AsNoTracking().Where(m => m.UserId == uid).Select(m => m.ProjectId).ToListAsync(ct);
    }
}
