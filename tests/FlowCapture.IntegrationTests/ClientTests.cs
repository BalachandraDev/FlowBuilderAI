using System.Net;
using System.Net.Http.Json;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>Clients: the administrator makes them and assigns users; users pick one of theirs when they start a project.</summary>
[Collection("api")]
public class ClientTests
{
    private readonly TestApp _app;

    public ClientTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
    }

    private async Task<(HttpClient Client, Guid Id, string Email)> NewUserAsync(string name)
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var email = $"{name}.{Guid.NewGuid().ToString("N")[..8]}@test.local";
        var made = await admin.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, "Test " + name, TestApp.Password, [AppRoles.Lead]));
        return (await _app.LoginAsync(email), made.Id, email);
    }

    private static Task<QuickProjectDto> StartAsync(HttpClient c, string name, Guid? clientId = null) =>
        c.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest(name + " " + Guid.NewGuid().ToString("N")[..5], null, clientId));

    private static async Task<HttpResponseMessage> NoteAsync(HttpClient c, Guid flowId) =>
        await c.PostAsJsonAsync($"/api/v1/flows/{flowId}/entries", new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", "The nurse opens the ward list and picks the patient.", null, null, null), TestJson.Options);

    [Fact]
    public async Task Only_the_admin_makes_clients_and_names_are_unique()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (user, _, _) = await NewUserAsync("noclient");
        var name = "Apollo " + Guid.NewGuid().ToString("N")[..6];

        var made = await admin.PostAsync<ClientDto>("/api/v1/clients", new CreateClientRequest(name, "Chennai"));
        made.IsActive.Should().BeTrue();
        (await user.PostAsJsonAsync("/api/v1/clients", new CreateClientRequest("Mine", null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PutAsJsonAsync($"/api/v1/clients/{made.Id}", new UpdateClientRequest("Hacked", null, true), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PutAsJsonAsync($"/api/v1/clients/{made.Id}/users", new SetClientUsersRequest([]), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await admin.PostAsJsonAsync("/api/v1/clients", new CreateClientRequest(name.ToUpperInvariant(), null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync("/api/v1/clients", new CreateClientRequest("  ", null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var renamed = await admin.PutAsync<ClientDto>($"/api/v1/clients/{made.Id}", new UpdateClientRequest(name + " Hospitals", "Madurai", true));
        renamed.Name.Should().Be(name + " Hospitals");
        renamed.Location.Should().Be("Madurai");
    }

    [Fact]
    public async Task A_user_sees_only_the_active_clients_they_are_assigned_to()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (user, uid, _) = await NewUserAsync("picker");
        var mine = await admin.NewClientAsync(null, uid);
        var notMine = await admin.NewClientAsync();

        var list = await user.GetAsync<List<ClientDto>>("/api/v1/clients");
        list.Select(c => c.Id).Should().Contain(mine.Id).And.NotContain(notMine.Id);
        list.Should().OnlyContain(c => c.UserIds.Length == 0, "a user does not see who else is assigned");
        (await admin.GetAsync<List<ClientDto>>("/api/v1/clients")).Select(c => c.Id).Should().Contain([mine.Id, notMine.Id]);

        await admin.PutAsync<ClientDto>($"/api/v1/clients/{mine.Id}", new UpdateClientRequest(mine.Name, mine.Location, false));
        (await user.GetAsync<List<ClientDto>>("/api/v1/clients")).Select(c => c.Id).Should().NotContain(mine.Id, "a stopped client cannot be chosen");
        (await StartAsyncRaw(user, mine.Id)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    private static Task<HttpResponseMessage> StartAsyncRaw(HttpClient c, Guid? clientId) =>
        c.PostAsJsonAsync("/api/v1/projects/quick", new QuickProjectRequest("Raw " + Guid.NewGuid().ToString("N")[..5], null, clientId), TestJson.Options);

    [Fact]
    public async Task A_project_needs_one_of_the_users_own_clients()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (none, _, _) = await NewUserAsync("none");
        var noClient = await StartAsyncRaw(none, null);
        noClient.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await noClient.ProblemAsync()).Should().Contain("no_client");

        var (user, uid, _) = await NewUserAsync("two");
        var a = await admin.NewClientAsync(null, uid);
        var b = await admin.NewClientAsync(null, uid);
        var stranger = await admin.NewClientAsync();

        var ask = await StartAsyncRaw(user, null);
        ask.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ask.ProblemAsync()).Should().Contain("client_required", "with several clients the user must choose");
        var refused = await StartAsyncRaw(user, stranger.Id);
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await refused.ProblemAsync()).Should().Contain("client_not_allowed");

        var made = await StartAsync(user, "Chosen", b.Id);
        (await user.GetAsync<List<ProjectDto>>("/api/v1/projects")).Single(p => p.Id == made.ProjectId).Should().Match<ProjectDto>(p => p.ClientId == b.Id && p.ClientName == b.Name);

        // with exactly one client it is used without asking
        var (single, sid, _) = await NewUserAsync("one");
        var only = await admin.NewClientAsync(null, sid);
        var auto = await StartAsync(single, "Auto");
        (await single.GetAsync<List<ProjectDto>>("/api/v1/projects")).Single(p => p.Id == auto.ProjectId).ClientId.Should().Be(only.Id);
        a.Id.Should().NotBe(b.Id);
    }

    [Fact]
    public async Task Everybody_on_a_client_can_work_in_its_projects_but_only_the_lead_can_delete_them()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (owner, ownerId, _) = await NewUserAsync("owner");
        var (mate, mateId, _) = await NewUserAsync("mate");
        var (outsider, outsiderId, _) = await NewUserAsync("outsider");
        var client = await admin.NewClientAsync(null, ownerId, mateId);
        await admin.NewClientAsync(null, outsiderId);

        var first = await StartAsync(owner, "Shared", client.Id);
        (await mate.GetAsync<List<ProjectDto>>("/api/v1/projects")).Select(p => p.Id).Should().Contain(first.ProjectId, "a colleague on the client sees the project from the start");
        (await outsider.GetAsync<List<ProjectDto>>("/api/v1/projects")).Select(p => p.Id).Should().NotContain(first.ProjectId);
        (await outsider.GetAsync($"/api/v1/projects/{first.ProjectId}/flows")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await NoteAsync(mate, first.FlowId)).StatusCode.Should().Be(HttpStatusCode.Created, "a colleague can add notes");
        (await NoteAsync(outsider, first.FlowId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await mate.DeleteAsync($"/api/v1/projects/{first.ProjectId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "only the lead or an administrator deletes a project");
        var token = await mate.GetAsync<UserProfileDto>("/api/v1/auth/me");
        token.Projects.Single(p => p.ProjectId == first.ProjectId).ProjectRole.Should().Be("Collaborator");

        // someone assigned later gets the projects that already exist; someone removed loses them
        var (late, lateId, _) = await NewUserAsync("late");
        await admin.PutAsync<ClientDto>($"/api/v1/clients/{client.Id}/users", new SetClientUsersRequest([ownerId, mateId, lateId]));
        (await late.GetAsync<List<ProjectDto>>("/api/v1/projects")).Select(p => p.Id).Should().Contain(first.ProjectId);
        var second = await StartAsync(owner, "Second", client.Id);
        (await late.GetAsync<List<ProjectDto>>("/api/v1/projects")).Select(p => p.Id).Should().Contain(second.ProjectId);

        var after = await admin.PutAsync<ClientDto>($"/api/v1/clients/{client.Id}/users", new SetClientUsersRequest([ownerId, lateId]));
        after.UserIds.Should().BeEquivalentTo([ownerId, lateId]);
        (await mate.GetAsync<List<ProjectDto>>("/api/v1/projects")).Select(p => p.Id).Should().NotContain([first.ProjectId, second.ProjectId]);
        (await owner.GetAsync<List<ProjectDto>>("/api/v1/projects")).Select(p => p.Id).Should().Contain([first.ProjectId, second.ProjectId], "the lead keeps their own projects");

        // a person who does not exist or is stopped cannot be assigned
        (await admin.PutAsJsonAsync($"/api/v1/clients/{client.Id}/users", new SetClientUsersRequest([Guid.NewGuid()]), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

}
