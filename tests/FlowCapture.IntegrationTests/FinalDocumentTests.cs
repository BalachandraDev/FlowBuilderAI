using System.Net;
using System.Net.Http.Json;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>
/// The office's flow: the administrator makes users and clients and assigns them; users make documents and mark one as final to give to the team;
/// the administrator sees only those final documents, verifies them or returns them, and can see who handles which client.
/// </summary>
[Collection("api")]
public class FinalDocumentTests
{
    private readonly TestApp _app;

    public FinalDocumentTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
    }

    private async Task<(HttpClient Client, Guid Id, string Name)> NewUserAsync(string name, bool withClient = true)
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var email = $"{name}.{Guid.NewGuid().ToString("N")[..8]}@test.local";
        var made = await admin.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, "Test " + name, TestApp.Password, [AppRoles.Lead]));
        if (withClient) await admin.NewClientAsync(null, made.Id);
        return (await _app.LoginAsync(email), made.Id, "Test " + name);
    }

    private async Task AnalyseAsync(HttpClient c, Guid flowId)
    {
        foreach (var text in new[] { "When the case sheet comes the user opens the receive menu and picks the ward. A super user gets his own ward pre-selected.", "This is a change to existing behaviour. Priority is High. All MRD users use this menu frequently." })
            await c.PostAsJsonAsync($"/api/v1/flows/{flowId}/entries", new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", text, null, null, null), TestJson.Options);
        await c.PostAsJsonAsync($"/api/v1/flows/{flowId}/analyses", new StartAnalysisRequest("Initial", null), TestJson.Options);
        await _app.WhenJobsDone();
    }

    private async Task<LibraryDocumentDto> MakeDocumentAsync(HttpClient user, Guid flowId)
    {
        var started = await user.PostAsync($"/api/v1/flows/{flowId}/guide", new { });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.ProblemAsync());
        await _app.WhenJobsDone();
        return (await user.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Single(d => d.FlowId == flowId);
    }

    private static Task<LibraryDocumentDto?> MineAsync(HttpClient c, Guid docId) =>
        c.GetAsync<List<LibraryDocumentDto>>("/api/v1/library").ContinueWith(t => t.Result.SingleOrDefault(d => d.Id == docId));

    [RequiresBrowserFact]
    public async Task A_document_is_a_working_copy_until_its_maker_marks_it_final_and_only_then_can_the_administrator_see_and_verify_it()
    {
        var (user, userId, userName) = await NewUserAsync("maker");
        var (stranger, _, _) = await NewUserAsync("maker-stranger");
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Final " + Guid.NewGuid().ToString("N")[..5], null));
        await AnalyseAsync(user, quick.FlowId);
        var made = await MakeDocumentAsync(user, quick.FlowId);

        // a working copy: the user sees it as that, the administrator does not see it at all
        made.Stage.Should().Be("Working");
        (await admin.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Should().NotContain(d => d.Id == made.Id);
        foreach (var url in new[] { $"/api/v1/documents/{made.Id}", $"/api/v1/documents/{made.Id}/versions/1/download?format=pdf", $"/api/v1/library/{made.Id}/diagram" })
            (await admin.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
        (await admin.GetAsync<List<DocumentDto>>($"/api/v1/flows/{quick.FlowId}/documents")).Should().BeEmpty("the list of a request's documents holds only the final ones for the administrator");
        (await admin.PostAsync($"/api/v1/documents/{made.Id}/versions/1/final", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);     // the administrator does not mark documents final
        (await admin.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);                 // nor make them
        (await admin.PostAsync($"/api/v1/projects/{quick.ProjectId}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await stranger.PostAsync($"/api/v1/documents/{made.Id}/versions/1/final", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);   // somebody on another client cannot

        // the maker marks it final, with a note
        var overview = await admin.GetAsync<AdminOverviewDto>("/api/v1/admin/overview");
        var marked = await user.PostAsync<DocumentDto>($"/api/v1/documents/{made.Id}/versions/1/final", new MarkFinalRequest("Ready to give to the team"));
        marked.Versions.Single().Should().Match<DocumentVersionDto>(v => v.FinalizedAt != null && v.FinalizedByName == userName && v.FinalNote == "Ready to give to the team" && v.ApprovalStatus == "Pending");
        (await user.PostAsync<DocumentDto>($"/api/v1/documents/{made.Id}/versions/1/final", new MarkFinalRequest("again"))).Versions.Single().FinalNote.Should().Be("Ready to give to the team", "marking twice changes nothing");
        (await admin.GetAsync<AdminOverviewDto>("/api/v1/admin/overview")).AwaitingVerification.Should().Be(overview.AwaitingVerification + 1);

        // now the administrator sees it, with who made it final and for which client, and can open it
        var seen = (await admin.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Single(d => d.Id == made.Id);
        seen.Should().Match<LibraryDocumentDto>(d => d.Stage == "Final" && d.FinalizedByName == userName && d.ClientName != null && d.ClientId != null && d.FinalNote == "Ready to give to the team");
        (await admin.GetAsync($"/api/v1/documents/{made.Id}/versions/1/download?format=pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync<DocumentDto>($"/api/v1/documents/{made.Id}")).Versions.Should().ContainSingle();
        (await stranger.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Should().NotContain(d => d.Id == made.Id, "the documents of a client are for the people on that client");

        // the maker can take it back while nothing has been decided, and then the administrator does not see it again
        (await user.DeleteAsync($"/api/v1/documents/{made.Id}/versions/1/final")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await MineAsync(user, made.Id))!.Stage.Should().Be("Working");
        (await admin.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Should().NotContain(d => d.Id == made.Id);

        // marked again: the administrator returns it, with a reason that is required
        await user.PostAsync<DocumentDto>($"/api/v1/documents/{made.Id}/versions/1/final", new MarkFinalRequest(null));
        (await admin.PostAsync($"/api/v1/documents/{made.Id}/versions/1/approve", new ApproveDocumentRequest(false, null))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await admin.PostAsync<DocumentDto>($"/api/v1/documents/{made.Id}/versions/1/approve", new ApproveDocumentRequest(false, "The scope of the second module is missing."));
        var returned = (await MineAsync(user, made.Id))!;
        returned.Should().Match<LibraryDocumentDto>(d => d.Stage == "Returned" && d.ReviewNote == "The scope of the second module is missing.");
        (await user.DeleteAsync($"/api/v1/documents/{made.Id}/versions/1/final")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a decided version cannot be taken back");
        (await user.PostAsync($"/api/v1/documents/{made.Id}/versions/1/final", new { })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a returned version needs a new version");

        // the maker updates the document (version 2); only the latest version can be marked final, and the administrator verifies it
        var again = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { });
        again.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _app.WhenJobsDone();
        (await MineAsync(user, made.Id))!.Should().Match<LibraryDocumentDto>(d => d.VersionNo == 2 && d.Stage == "Working");
        var old = await user.PostAsync($"/api/v1/documents/{made.Id}/versions/1/final", new { });
        old.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await old.ProblemAsync()).Should().Contain("not_latest");
        await user.PostAsync<DocumentDto>($"/api/v1/documents/{made.Id}/versions/2/final", new MarkFinalRequest(null));
        await admin.PostAsync<DocumentDto>($"/api/v1/documents/{made.Id}/versions/2/approve", new ApproveDocumentRequest(true, null));
        var verified = (await MineAsync(user, made.Id))!;
        verified.Should().Match<LibraryDocumentDto>(d => d.Stage == "Verified" && d.VerifiedByName != null && d.VersionNo == 2);
        (await MineAsync(admin, made.Id))!.Stage.Should().Be("Verified");

        // who handles what: the user is on a client, and what they handled shows beside them
        var rows = await admin.GetAsync<List<HandlingRowDto>>("/api/v1/admin/handling");
        rows.Should().Contain(r => r.UserId == userId && r.ClientId == verified.ClientId && r.Verified >= 1 && r.Projects >= 1 && r.ProjectsStarted >= 1 && r.LastFinalAt != null);
        (await user.GetAsync("/api/v1/admin/handling")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_administrator_sees_who_handles_which_client_including_clients_without_people_and_people_without_clients()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (_, assignedId, assignedName) = await NewUserAsync("handler");
        var (_, idleId, _) = await NewUserAsync("idle", withClient: false);
        var empty = await admin.PostAsync<ClientDto>("/api/v1/clients", new CreateClientRequest("Nobody Hospital " + Guid.NewGuid().ToString("N")[..5], null));

        var rows = await admin.GetAsync<List<HandlingRowDto>>("/api/v1/admin/handling");

        var mine = rows.Single(r => r.UserId == assignedId);
        mine.Should().Match<HandlingRowDto>(r => r.ClientId != null && r.ClientName != null && r.UserName == assignedName && r.UserEmail != null && r.UserActive);
        rows.Should().Contain(r => r.ClientId == empty.Id && r.UserId == null, "a client nobody handles is listed too");
        rows.Should().Contain(r => r.UserId == idleId && r.ClientId == null, "so is a user who is on no client");
        rows.Should().NotContain(r => r.UserEmail != null && r.UserEmail.StartsWith("admin@"), "the administrator does not handle clients");
    }

    [Fact]
    public async Task The_final_endpoints_need_a_real_version_of_a_real_document()
    {
        var (user, _, _) = await NewUserAsync("nodoc");
        (await user.PostAsync($"/api/v1/documents/{Guid.NewGuid()}/versions/1/final", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await user.DeleteAsync($"/api/v1/documents/{Guid.NewGuid()}/versions/1/final")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await user.PostAsync($"/api/v1/documents/{Guid.NewGuid()}/versions/1/approve", new ApproveDocumentRequest(true, null))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
