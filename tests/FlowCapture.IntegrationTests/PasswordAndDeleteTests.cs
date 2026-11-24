using System.Net;
using System.Net.Http.Json;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>Everybody changes their own password; only an administrator deletes a user.</summary>
[Collection("api")]
public class PasswordAndDeleteTests
{
    private readonly TestApp _app;

    public PasswordAndDeleteTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
    }

    private async Task<(Guid Id, string Email)> NewUserAsync(string name, string role = AppRoles.Lead)
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var email = $"{name}.{Guid.NewGuid().ToString("N")[..8]}@test.local";
        var made = await admin.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, "Test " + name, TestApp.Password, [role]));
        return (made.Id, email);
    }

    private Task<HttpResponseMessage> LoginRawAsync(string email, string password) =>
        _app.Anonymous().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password), TestJson.Options);

    // ------------------------------------------------------------------ change password

    [Theory]
    [InlineData(AppRoles.Lead)]
    [InlineData(AppRoles.Admin)]
    public async Task Anybody_changes_their_own_password_and_stays_signed_in_while_other_sign_ins_end(string role)
    {
        var (_, email) = await NewUserAsync("changer", role);
        var first = await (await LoginRawAsync(email, TestApp.Password)).ReadAsync<TokenResponse>();
        var other = await (await LoginRawAsync(email, TestApp.Password)).ReadAsync<TokenResponse>();      // a second phone
        var client = _app.Anonymous();
        client.DefaultRequestHeaders.Authorization = new("Bearer", first.AccessToken);

        // the wrong current password, a weak one, and the same one are each refused with a reason, and nothing changes
        var wrong = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest("not-it-at-all", "New-Passw0rd-123"), TestJson.Options);
        wrong.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await wrong.ProblemAsync()).Should().Contain("wrong_password");
        (await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest(TestApp.Password, "short1"), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest(TestApp.Password, "onlyletterspassword"), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var same = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest(TestApp.Password, TestApp.Password), TestJson.Options);
        same.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await LoginRawAsync(email, TestApp.Password)).StatusCode.Should().Be(HttpStatusCode.OK, "nothing changed");

        var done = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest(TestApp.Password, "New-Passw0rd-123"), TestJson.Options);
        done.StatusCode.Should().Be(HttpStatusCode.OK, await done.ProblemAsync());
        var fresh = await done.ReadAsync<TokenResponse>();

        (await LoginRawAsync(email, TestApp.Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the old password no longer works");
        (await LoginRawAsync(email, "New-Passw0rd-123")).StatusCode.Should().Be(HttpStatusCode.OK);
        // this sign-in carries on with the tokens it was given; the other phone is signed out
        var again = await _app.Anonymous().PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(fresh.RefreshToken), TestJson.Options);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _app.Anonymous().PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(other.RefreshToken), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _app.Anonymous().PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Changing_a_password_needs_a_sign_in()
    {
        (await _app.Anonymous().PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest("a", "New-Passw0rd-123"), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------ delete a user

    [Fact]
    public async Task Only_an_administrator_deletes_a_user_who_then_cannot_sign_in_and_leaves_the_lists_and_clients_and_the_email_is_free()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (goneId, goneEmail) = await NewUserAsync("goner");
        var (mateId, mateEmail) = await NewUserAsync("bystander");
        var gone = await _app.LoginAsync(goneEmail);
        var mate = await _app.LoginAsync(mateEmail);
        var client = await admin.NewClientAsync(null, goneId, mateId);
        var project = await gone.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Left behind " + Guid.NewGuid().ToString("N")[..5], null, client.Id));

        // not for users, whoever they are
        (await mate.DeleteAsync($"/api/v1/users/{goneId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await gone.DeleteAsync($"/api/v1/users/{goneId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "not even oneself");
        // never oneself
        var me = await admin.GetAsync<UserProfileDto>("/api/v1/auth/me");
        var self = await admin.DeleteAsync($"/api/v1/users/{me.Id}");
        self.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await self.ProblemAsync()).Should().Contain("self_delete");

        var token = await (await LoginRawAsync(goneEmail, TestApp.Password)).ReadAsync<TokenResponse>();
        (await admin.DeleteAsync($"/api/v1/users/{goneId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await LoginRawAsync(goneEmail, TestApp.Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _app.Anonymous().PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(token.RefreshToken), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "signed out everywhere");
        (await admin.GetAsync<List<UserDto>>("/api/v1/users")).Select(u => u.Id).Should().NotContain(goneId).And.Contain(mateId);
        (await admin.GetAsync<List<ClientDto>>("/api/v1/clients")).Single(c => c.Id == client.Id).UserIds.Should().BeEquivalentTo([mateId]);

        // nothing was erased: the project they started is still there for the administrator and for the colleague
        (await admin.GetAsync<List<ProjectDto>>("/api/v1/projects")).Select(p => p.Id).Should().Contain(project.ProjectId);
        (await mate.GetAsync<List<ProjectDto>>("/api/v1/projects")).Select(p => p.Id).Should().Contain(project.ProjectId);

        // the email can be used again, and deleting twice is "not found"
        (await admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(goneEmail, "Second Person", TestApp.Password, [AppRoles.Lead]), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.DeleteAsync($"/api/v1/users/{goneId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_deleted_administrator_can_no_longer_sign_in_and_the_overview_does_not_count_deleted_people()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (extraId, extraEmail) = await NewUserAsync("extra-admin", AppRoles.Admin);
        var before = (await admin.GetAsync<AdminOverviewDto>("/api/v1/admin/overview")).Users;
        (await admin.DeleteAsync($"/api/v1/users/{extraId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync<AdminOverviewDto>("/api/v1/admin/overview")).Users.Should().Be(before - 1);
        (await LoginRawAsync(extraEmail, TestApp.Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
