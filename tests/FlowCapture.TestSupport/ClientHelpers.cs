using FlowCapture.Shared.Dtos;

namespace FlowCapture.TestSupport;

public static class ClientHelpers
{
    /// <summary>Makes a client (as the administrator) and assigns the given users to it.</summary>
    public static async Task<ClientDto> NewClientAsync(this HttpClient admin, string? name = null, params Guid[] userIds)
    {
        var client = await admin.PostAsync<ClientDto>("/api/v1/clients", new CreateClientRequest(name ?? "Client " + Guid.NewGuid().ToString("N")[..8], "Chennai"));
        return userIds.Length == 0 ? client : await admin.PutAsync<ClientDto>($"/api/v1/clients/{client.Id}/users", new SetClientUsersRequest(userIds));
    }

    /// <summary>Puts a colleague on the same client as the owner (the owner's only client), so they can open the owner's projects.</summary>
    public static async Task ShareClientAsync(this HttpClient admin, HttpClient owner, HttpClient colleague)
    {
        var client = (await owner.GetAsync<List<ClientDto>>("/api/v1/clients")).Single();
        var ownerId = (await owner.GetAsync<UserProfileDto>("/api/v1/auth/me")).Id;
        var colleagueId = (await colleague.GetAsync<UserProfileDto>("/api/v1/auth/me")).Id;
        await admin.PutAsync<ClientDto>($"/api/v1/clients/{client.Id}/users", new SetClientUsersRequest([ownerId, colleagueId]));
    }
}
