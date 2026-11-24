using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.HandlingFeatures;

/// <summary>Who handles which client, with the work and the final documents of each: for the administrator.</summary>
public sealed record GetHandlingQuery : IRequest<IReadOnlyList<HandlingRowDto>>;

public sealed class HandlingHandlers(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetHandlingQuery, IReadOnlyList<HandlingRowDto>>
{
    public async Task<IReadOnlyList<HandlingRowDto>> Handle(GetHandlingQuery r, CancellationToken ct)
    {
        if (!user.IsInRole(AppRoles.Admin)) throw new ForbiddenException("Only an administrator can see who handles which client.");

        var clients = await db.Clients.AsNoTracking().Include(c => c.Users).OrderBy(c => c.Name).ToListAsync(ct);
        // administrators do not handle clients: the people on a client are the users
        var people = (await db.Users.AsNoTracking().Include(u => u.Roles).ThenInclude(x => x.Role).Where(u => u.DeletedAt == null).OrderBy(u => u.DisplayName).ToListAsync(ct))
            .Where(u => !u.Roles.Any(x => x.Role!.Name == AppRoles.Admin)).ToDictionary(u => u.Id);

        var projects = await db.Projects.AsNoTracking().Select(p => new { p.Id, p.ClientId, p.LeadUserId }).ToListAsync(ct);          // a deleted project is not listed
        var projectIds = projects.Select(p => p.Id).ToHashSet();
        var moduleCount = (await db.ProjectDepartments.AsNoTracking().Select(d => d.ProjectId).ToListAsync(ct)).Where(projectIds.Contains).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var requestCount = (await db.Flows.AsNoTracking().Select(f => f.ProjectDepartment!.ProjectId).ToListAsync(ct)).Where(projectIds.Contains).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

        // the latest final version of each document: where it stands, who marked it, and for which client
        var finals = await (from v in db.DocumentVersions.AsNoTracking()
                            join d in db.GeneratedDocuments.AsNoTracking() on v.GeneratedDocumentId equals d.Id
                            where v.FinalizedAt != null
                            select new { v.GeneratedDocumentId, d.ProjectId, v.VersionNo, v.FinalizedAt, v.FinalizedBy, v.ApprovalStatus }).ToListAsync(ct);
        var latestFinal = finals.Where(f => projectIds.Contains(f.ProjectId)).GroupBy(f => f.GeneratedDocumentId).Select(g => g.OrderByDescending(x => x.VersionNo).First()).ToList();
        var clientOf = projects.ToDictionary(p => p.Id, p => p.ClientId);

        var rows = new List<HandlingRowDto>();
        foreach (var c in clients)
        {
            var clientProjects = projects.Where(p => p.ClientId == c.Id).ToList();
            var modules = clientProjects.Sum(p => moduleCount.GetValueOrDefault(p.Id));
            var requests = clientProjects.Sum(p => requestCount.GetValueOrDefault(p.Id));
            var assigned = c.Users.Where(u => people.ContainsKey(u.UserId)).Select(u => people[u.UserId]).OrderBy(u => u.DisplayName).ToList();
            if (assigned.Count == 0)
            {
                rows.Add(new HandlingRowDto(c.Id, c.Name, c.IsActive, null, null, null, false, clientProjects.Count, 0, modules, requests, 0, 0, 0, null));
                continue;
            }
            foreach (var u in assigned)
            {
                var mine = latestFinal.Where(f => clientOf.GetValueOrDefault(f.ProjectId) == c.Id && f.FinalizedBy == u.Id).ToList();
                rows.Add(new HandlingRowDto(c.Id, c.Name, c.IsActive, u.Id, u.DisplayName, u.Email, u.IsActive, clientProjects.Count, clientProjects.Count(p => p.LeadUserId == u.Id), modules, requests,
                    mine.Count(f => f.ApprovalStatus == ApprovalStatus.Pending), mine.Count(f => f.ApprovalStatus == ApprovalStatus.Approved), mine.Count(f => f.ApprovalStatus == ApprovalStatus.Rejected),
                    mine.Count == 0 ? null : mine.Max(f => f.FinalizedAt)));
            }
        }

        // users who are on no client yet
        var onAClient = clients.SelectMany(c => c.Users).Select(u => u.UserId).ToHashSet();
        foreach (var u in people.Values.Where(u => !onAClient.Contains(u.Id)))
            rows.Add(new HandlingRowDto(null, null, true, u.Id, u.DisplayName, u.Email, u.IsActive, 0, 0, 0, 0, 0, 0, 0, null));
        return rows;
    }
}
