using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.Clients;

public sealed record ListClientsQuery : IRequest<IReadOnlyList<ClientDto>>;
public sealed record CreateClientCommand(CreateClientRequest Body) : IRequest<ClientDto>;
public sealed record UpdateClientCommand(Guid Id, UpdateClientRequest Body) : IRequest<ClientDto>;
public sealed record SetClientUsersCommand(Guid Id, SetClientUsersRequest Body) : IRequest<ClientDto>;

public sealed class CreateClientValidator : AbstractValidator<CreateClientCommand>
{
    public CreateClientValidator()
    {
        RuleFor(x => x.Body.Name).NotEmpty().WithMessage("Give the client a name.").MaximumLength(200);
        RuleFor(x => x.Body.Location).MaximumLength(200);
    }
}

public sealed class UpdateClientValidator : AbstractValidator<UpdateClientCommand>
{
    public UpdateClientValidator()
    {
        RuleFor(x => x.Body.Name).NotEmpty().WithMessage("Give the client a name.").MaximumLength(200);
        RuleFor(x => x.Body.Location).MaximumLength(200);
    }
}

/// <summary>
/// Keeps project access in step with the people assigned to a client. Everyone assigned to a client can open every project of that client
/// (as a Collaborator: they add notes and make documents, but only a project's lead or an administrator can change or delete it).
/// The access is stored as ordinary project memberships, so the sign-in token and every existing check keep working.
/// </summary>
public static class ClientAccess
{
    /// <summary>Makes every user assigned to the project's client a member of the project, unless they already are.</summary>
    public static async Task AddClientUsersToProjectAsync(IAppDbContext db, Project project, CancellationToken ct)
    {
        if (project.ClientId is not { } clientId) return;
        var assigned = await db.ClientUsers.AsNoTracking().Where(c => c.ClientId == clientId).Select(c => c.UserId).ToListAsync(ct);
        foreach (var uid in assigned.Where(uid => project.Members.All(m => m.UserId != uid)))
            project.Members.Add(new ProjectMember { UserId = uid, ProjectRole = ProjectRole.Collaborator });
    }

    /// <summary>Gives the given users access to every project of the client.</summary>
    public static async Task GrantAsync(IAppDbContext db, Guid clientId, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return;
        var projects = await db.Projects.Include(p => p.Members).Where(p => p.ClientId == clientId).ToListAsync(ct);
        foreach (var p in projects)
            foreach (var uid in userIds.Where(uid => p.Members.All(m => m.UserId != uid)))
                p.Members.Add(new ProjectMember { ProjectId = p.Id, UserId = uid, ProjectRole = ProjectRole.Collaborator });
    }

    /// <summary>Takes the given users' client-given access away. A project they lead (or were added to by hand with another role) is not touched.</summary>
    public static async Task RevokeAsync(IAppDbContext db, Guid clientId, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return;
        var rows = await db.ProjectMembers.Where(m => userIds.Contains(m.UserId) && m.ProjectRole == ProjectRole.Collaborator && m.Project!.ClientId == clientId).ToListAsync(ct);
        db.ProjectMembers.RemoveRange(rows);
    }
}

public sealed class ClientHandlers(IAppDbContext db, ICurrentUser user) :
    IRequestHandler<ListClientsQuery, IReadOnlyList<ClientDto>>,
    IRequestHandler<CreateClientCommand, ClientDto>,
    IRequestHandler<UpdateClientCommand, ClientDto>,
    IRequestHandler<SetClientUsersCommand, ClientDto>
{
    public async Task<IReadOnlyList<ClientDto>> Handle(ListClientsQuery r, CancellationToken ct)
    {
        var admin = user.IsInRole(AppRoles.Admin);
        var uid = user.RequireUserId();
        var q = db.Clients.AsNoTracking().Include(c => c.Users).AsQueryable();
        if (!admin) q = q.Where(c => c.IsActive && c.Users.Any(u => u.UserId == uid));
        var clients = await q.OrderBy(c => c.Name).ToListAsync(ct);
        var counts = await db.Projects.AsNoTracking().Where(p => p.ClientId != null).GroupBy(p => p.ClientId!.Value)
            .Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return clients.Select(c => ToDto(c, counts.GetValueOrDefault(c.Id), admin)).ToList();
    }

    public async Task<ClientDto> Handle(CreateClientCommand r, CancellationToken ct)
    {
        var name = r.Body.Name.Trim();
        await EnsureNameFreeAsync(name, null, ct);
        var client = new Client { Name = name, Location = r.Body.Location?.Trim() ?? "" };
        db.Clients.Add(client);
        await db.SaveChangesAsync(ct);
        return ToDto(client, 0, true);
    }

    public async Task<ClientDto> Handle(UpdateClientCommand r, CancellationToken ct)
    {
        var client = await db.Clients.Include(c => c.Users).FirstOrDefaultAsync(c => c.Id == r.Id, ct) ?? throw new NotFoundException("Client", r.Id);
        var name = r.Body.Name.Trim();
        await EnsureNameFreeAsync(name, client.Id, ct);
        client.Name = name;
        client.Location = r.Body.Location?.Trim() ?? "";
        client.IsActive = r.Body.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(client, await db.Projects.CountAsync(p => p.ClientId == client.Id, ct), true);
    }

    public async Task<ClientDto> Handle(SetClientUsersCommand r, CancellationToken ct)
    {
        var client = await db.Clients.Include(c => c.Users).FirstOrDefaultAsync(c => c.Id == r.Id, ct) ?? throw new NotFoundException("Client", r.Id);
        var wanted = r.Body.UserIds.Distinct().ToHashSet();
        var found = await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        if (found.Count != wanted.Count) throw new DomainException("Every assigned person must be an existing, active user.", "invalid_member");

        var current = client.Users.Select(u => u.UserId).ToHashSet();
        var added = wanted.Except(current).ToList();
        var removed = current.Except(wanted).ToList();
        client.Users.RemoveAll(u => removed.Contains(u.UserId));
        foreach (var uid in added) client.Users.Add(new ClientUser { ClientId = client.Id, UserId = uid });
        await ClientAccess.GrantAsync(db, client.Id, added, ct);
        await ClientAccess.RevokeAsync(db, client.Id, removed, ct);
        await db.SaveChangesAsync(ct);
        return ToDto(client, await db.Projects.CountAsync(p => p.ClientId == client.Id, ct), true);
    }

    private async Task EnsureNameFreeAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var lower = name.ToLowerInvariant();
        if (await db.Clients.AnyAsync(c => c.Name.ToLower() == lower && c.Id != exceptId, ct))
            throw new ConflictException($"There is already a client called \"{name}\".");
    }

    private static ClientDto ToDto(Client c, int projects, bool withUsers) =>
        new(c.Id, c.Name, c.Location, c.IsActive, projects, withUsers ? c.Users.Select(u => u.UserId).ToArray() : []);
}
