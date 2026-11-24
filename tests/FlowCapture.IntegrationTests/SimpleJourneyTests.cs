using System.Net;
using System.Net.Http.Json;
using System.Text;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>The two-level journey: an admin makes users; a user makes a project, adds notes (typed, photographed), analyses, and gets the diagram and the implementation guide.</summary>
[Collection("api")]
public class SimpleJourneyTests
{
    private readonly TestApp _app;

    public SimpleJourneyTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
        _app.Images.Fail = false;
    }

    private async Task<(HttpClient Client, string Email)> NewUserAsync(string name = "user", string role = AppRoles.Lead)
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var email = $"{name}.{Guid.NewGuid().ToString("N")[..8]}@test.local";
        var made = await admin.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, "Test " + name, TestApp.Password, [role]));
        await admin.NewClientAsync(null, made.Id);        // every test user has a client of their own
        return (await _app.LoginAsync(email), email);
    }

    private static async Task<EntryDto> AddNoteAsync(HttpClient c, Guid flowId, string text)
    {
        var res = await c.PostAsJsonAsync($"/api/v1/flows/{flowId}/entries", new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", text, null, null, null), TestJson.Options);
        return await res.ReadAsync<EntryDto>();
    }

    private async Task<AnalysisRunDto> AnalyseAsync(HttpClient c, Guid flowId)
    {
        var run = await (await c.PostAsJsonAsync($"/api/v1/flows/{flowId}/analyses", new StartAnalysisRequest("Initial", null), TestJson.Options)).ReadAsync<AnalysisRunDto>();
        await _app.WhenJobsDone();
        return await c.GetAsync<AnalysisRunDto>($"/api/v1/analysis-runs/{run.Id}");
    }

    private static MultipartFormDataContent Photo(string type = "image/jpeg", int size = 2048)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[size]);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        form.Add(file, "File", "notes.jpg");
        return form;
    }

    // ------------------------------------------------------------------ users and projects

    [Fact]
    public async Task A_user_makes_a_project_in_one_step_and_becomes_its_lead_with_a_workflow_ready_for_notes()
    {
        var (user, _) = await NewUserAsync();

        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Kauvery Discharge", "Chennai"));

        quick.Code.Should().MatchRegex("^[A-Z0-9]{2,4}-\\d{4}$");
        var projects = await user.GetAsync<List<ProjectDto>>("/api/v1/projects");
        projects.Should().ContainSingle(p => p.Id == quick.ProjectId && p.HospitalName == "Kauvery Discharge");
        var flow = await user.GetAsync<FlowDto>($"/api/v1/flows/{quick.FlowId}");
        flow.MenuName.Should().Be("Kauvery Discharge");
        flow.Status.Should().Be("Draft");
        (await AddNoteAsync(user, quick.FlowId, "The nurse opens the discharge menu.")).Kind.Should().Be("Text");   // the user may capture in their own project
    }

    [Fact]
    public async Task A_user_does_not_see_another_users_projects_and_cannot_write_in_them()
    {
        var (a, _) = await NewUserAsync("a");
        var (b, _) = await NewUserAsync("b");
        var mine = await a.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Private " + Guid.NewGuid().ToString("N")[..6], null));

        (await b.GetAsync<List<ProjectDto>>("/api/v1/projects")).Should().NotContain(p => p.Id == mine.ProjectId);
        var res = await b.PostAsJsonAsync($"/api/v1/flows/{mine.FlowId}/entries", new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", "x", null, null, null), TestJson.Options);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Only_admins_and_users_can_start_projects_and_a_name_is_required()
    {
        var (reviewer, _) = await NewUserAsync("rev", AppRoles.Reviewer);
        (await reviewer.PostAsJsonAsync("/api/v1/projects/quick", new QuickProjectRequest("Nope", null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var (user, _) = await NewUserAsync();
        (await user.PostAsJsonAsync("/api/v1/projects/quick", new QuickProjectRequest("  ", null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ photos of notes

    [Fact]
    public async Task A_photo_of_notes_is_stored_read_into_masked_text_and_used_by_the_analysis()
    {
        var (user, _) = await NewUserAsync();
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Photo project", null));

        var res = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/images", Photo());
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.ProblemAsync());
        var entry = await res.ReadAsync<EntryDto>();
        entry.Kind.Should().Be("Image");
        await _app.WhenJobsDone();

        var transcript = await user.GetAsync<TranscriptDto>($"/api/v1/entries/{entry.Id}/transcript");
        transcript.Status.Should().Be("Ready");
        transcript.MaskedText.Should().Contain("MRD user checks the ward list").And.NotContain("AB123456");
        (await user.GetAsync<EntryDto>($"/api/v1/entries/{entry.Id}")).TranscriptState.Should().Be("Ready");
        var picture = await user.GetAsync($"/api/v1/entries/{entry.Id}/image");
        picture.StatusCode.Should().Be(HttpStatusCode.OK);
        picture.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");

        await AddNoteAsync(user, quick.FlowId, "Priority is High. This is a change to existing behaviour.");
        (await AnalyseAsync(user, quick.FlowId)).Status.Should().Be("Succeeded");
        _app.Ai.Calls.Last().UserMessage.Should().Contain("photo_of_notes_text").And.Contain("MRD user checks the ward list").And.NotContain("AB123456");
    }

    [Fact]
    public async Task A_photo_that_cannot_be_read_says_why_and_blocks_the_analysis_until_it_is_removed()
    {
        var (user, _) = await NewUserAsync();
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Blurry project", null));
        _app.Images.Fail = true;

        var entry = await (await user.PostAsync($"/api/v1/flows/{quick.FlowId}/images", Photo())).ReadAsync<EntryDto>();
        await _app.WhenJobsDone();

        var transcript = await user.GetAsync<TranscriptDto>($"/api/v1/entries/{entry.Id}/transcript");
        transcript.Status.Should().Be("Failed");
        transcript.Error.Should().Contain("No readable text");

        await AddNoteAsync(user, quick.FlowId, "A typed note.");
        var blocked = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/analyses", new StartAnalysisRequest("Initial", null));
        blocked.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await blocked.ProblemAsync()).Should().Contain("transcript_pending").And.Contain("not turned into text yet");

        _app.Images.Fail = false;
        (await user.PostAsync<TranscriptDto>($"/api/v1/entries/{entry.Id}/transcribe", new { })).Status.Should().Be("Queued");   // "try again" on the photo
        await _app.WhenJobsDone();
        (await user.GetAsync<TranscriptDto>($"/api/v1/entries/{entry.Id}/transcript")).Status.Should().Be("Ready");
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("text/plain")]
    public async Task Only_photos_are_accepted_as_photos(string type)
    {
        var (user, _) = await NewUserAsync();
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Wrong file", null));
        var res = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/images", Photo(type));
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.ProblemAsync()).Should().Contain("JPEG, PNG or WebP");
    }

    [Fact]
    public async Task Someone_who_may_not_capture_in_the_project_cannot_add_a_photo()
    {
        var (owner, _) = await NewUserAsync("owner");
        var (other, _) = await NewUserAsync("other");
        var quick = await owner.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Mine", null));
        (await other.PostAsync($"/api/v1/flows/{quick.FlowId}/images", Photo())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Notes_added_at_the_same_moment_all_arrive_and_get_their_own_numbers()
    {
        var (user, _) = await NewUserAsync();
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Busy project", null));

        // a typed note, photos and more typed notes, all sent together (as when someone adds notes quickly)
        var sends = Enumerable.Range(0, 4).Select(i => user.PostAsJsonAsync($"/api/v1/flows/{quick.FlowId}/entries",
                new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", $"Typed note {i}", null, null, null), TestJson.Options))
            .Concat(Enumerable.Range(0, 3).Select(_ => user.PostAsync($"/api/v1/flows/{quick.FlowId}/images", Photo())))
            .ToList();
        var results = await Task.WhenAll(sends);

        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK, "no note is refused because another arrived at the same time");
        await _app.WhenJobsDone();
        var notes = await user.GetAsync<List<EntryDto>>($"/api/v1/flows/{quick.FlowId}/entries");
        notes.Should().HaveCount(7);
        notes.Select(n => n.RefNo).Should().OnlyHaveUniqueItems();
        (await user.GetAsync<FlowDto>($"/api/v1/flows/{quick.FlowId}")).EntryCount.Should().Be(7);
    }

    [Fact]
    public async Task The_same_photo_sent_twice_with_the_same_id_makes_one_note()
    {
        var (user, _) = await NewUserAsync();
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Retry", null));
        var id = Guid.NewGuid();
        MultipartFormDataContent WithId() { var f = Photo(); f.Add(new StringContent(id.ToString()), "EntryId"); return f; }

        (await user.PostAsync($"/api/v1/flows/{quick.FlowId}/images", WithId())).StatusCode.Should().Be(HttpStatusCode.Created);
        await _app.WhenJobsDone();
        (await user.PostAsync($"/api/v1/flows/{quick.FlowId}/images", WithId())).StatusCode.Should().Be(HttpStatusCode.Created);

        (await user.GetAsync<List<EntryDto>>($"/api/v1/flows/{quick.FlowId}/entries")).Should().ContainSingle();
        _app.Images.Calls.Should().BeGreaterThan(0);
    }

    // ------------------------------------------------------------------ AI usage

    [Fact]
    public async Task AI_usage_counts_the_tokens_of_analysis_and_photos_and_each_user_sees_only_their_own()
    {
        var (a, aEmail) = await NewUserAsync("usage-a");
        var (b, _) = await NewUserAsync("usage-b");
        var quick = await a.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Usage project", null));
        await a.PostAsync($"/api/v1/flows/{quick.FlowId}/images", Photo());
        await _app.WhenJobsDone();
        await AddNoteAsync(a, quick.FlowId, "Priority is High. This is a change to existing behaviour.");
        (await AnalyseAsync(a, quick.FlowId)).Status.Should().Be("Succeeded");

        var mine = await a.GetAsync<AiUsageSummaryDto>("/api/v1/ai-usage");

        mine.AllUsers.Should().BeFalse();
        mine.Month.Should().Be(DateTime.UtcNow.ToString("yyyy-MM"));
        mine.Totals.Calls.Should().Be(2);
        mine.Totals.InputTokens.Should().Be(1500 + 400);                         // the analysis (with its cached part) and the photo
        mine.Totals.OutputTokens.Should().Be(700 + 60);
        mine.Totals.TotalTokens.Should().Be(1500 + 700 + 400 + 60);
        mine.Today.TotalTokens.Should().Be(mine.Totals.TotalTokens);
        mine.ByKind.Single(k => k.Kind == "Analysis").TotalTokens.Should().Be(2200);
        mine.ByKind.Single(k => k.Kind == "Photo").TotalTokens.Should().Be(460);
        mine.ByProject.Should().ContainSingle(p => p.Id == quick.ProjectId && p.Name == "Usage project" && p.TotalTokens == 2660);
        mine.ByUser.Should().BeEmpty("a user does not see the others");
        mine.ByDay.Last().Day.Should().Be(DateTime.UtcNow.ToString("yyyy-MM-dd"));
        mine.ByDay.Last().TotalTokens.Should().Be(2660);
        mine.ByDay.Select(d => d.Day).Should().OnlyHaveUniqueItems().And.BeInAscendingOrder();
        mine.Recent.Should().HaveCount(2).And.OnlyContain(c => c.Success && c.Project == "Usage project");
        mine.Recent.Select(c => c.Kind).Should().BeEquivalentTo("Analysis", "Photo");

        var other = await b.GetAsync<AiUsageSummaryDto>("/api/v1/ai-usage");
        other.Totals.TotalTokens.Should().Be(0);
        other.Recent.Should().BeEmpty();

        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var all = await admin.GetAsync<AiUsageSummaryDto>("/api/v1/ai-usage");
        all.AllUsers.Should().BeTrue();
        all.Totals.TotalTokens.Should().BeGreaterThanOrEqualTo(2660);
        all.ByUser.Should().Contain(u => u.Name == "Test usage-a" && u.TotalTokens == 2660);
        all.Recent.Should().Contain(c => c.Who == "Test usage-a" && c.Kind == "Analysis");
        _ = aEmail;
    }

    [Fact]
    public async Task AI_usage_can_be_looked_up_for_another_month_and_refuses_a_bad_one()
    {
        var (user, _) = await NewUserAsync("usage-month");
        var empty = await user.GetAsync<AiUsageSummaryDto>("/api/v1/ai-usage?month=2020-01");
        empty.Month.Should().Be("2020-01");
        empty.Totals.TotalTokens.Should().Be(0);
        empty.ByDay.Should().HaveCount(31);
        (await user.GetAsync("/api/v1/ai-usage?month=January")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _app.Anonymous().GetAsync("/api/v1/ai-usage")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------ the admin dashboard

    [Fact]
    public async Task The_admin_saves_the_free_AI_key_which_is_kept_secret_and_can_be_removed()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (user, _) = await NewUserAsync();
        (await user.GetAsync("/api/v1/admin/ai")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.GetAsync("/api/v1/admin/overview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var saved = await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest("Gemini", "gemini-test-model", "AIza-secret-key-1234"));

        saved.HasKey.Should().BeTrue();
        saved.KeyHint.Should().Be("...1234");
        saved.GeminiModel.Should().Be("gemini-test-model");
        saved.KeyFromDashboard.Should().BeTrue();
        (await admin.GetStringAsync("/api/v1/admin/ai")).Should().NotContain("secret-key");
        (await admin.GetStringAsync("/api/v1/settings")).Should().NotContain("secret-key").And.NotContain("Secret.");

        var untouched = await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null));   // null = leave the key as it is
        untouched.KeyHint.Should().Be("...1234");

        var removed = await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, "", ""));
        removed.HasKey.Should().BeFalse();
        removed.KeyHint.Should().BeNull();
        (await admin.PostAsync<AiTestResultDto>("/api/v1/admin/ai/test")).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task An_admin_cannot_stop_or_demote_themselves_but_can_do_it_to_someone_else()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var me = await admin.GetAsync<UserProfileDto>("/api/v1/auth/me");
        var self = (await admin.GetAsync<List<UserDto>>("/api/v1/users")).Single(u => u.Id == me.Id);

        var stop = await admin.PutAsJsonAsync($"/api/v1/users/{self.Id}", new UpdateUserRequest(self.DisplayName, false, self.Roles, null), TestJson.Options);
        stop.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await stop.ProblemAsync()).Should().Contain("self_lockout");
        var demote = await admin.PutAsJsonAsync($"/api/v1/users/{self.Id}", new UpdateUserRequest(self.DisplayName, true, [AppRoles.Lead], null), TestJson.Options);
        demote.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var (_, email) = await NewUserAsync("other");
        var other = (await admin.GetAsync<List<UserDto>>("/api/v1/users")).Single(u => u.Email == email);
        (await admin.PutAsync<UserDto>($"/api/v1/users/{other.Id}", new UpdateUserRequest(other.DisplayName, false, other.Roles, null))).IsActive.Should().BeFalse();
        (await _app.Anonymous().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, TestApp.Password), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_admin_can_change_the_name_and_sign_in_email_of_a_user_or_an_admin()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (_, userEmail) = await NewUserAsync("rename");
        var (_, adminEmail) = await NewUserAsync("rename-admin", AppRoles.Admin);
        var (_, takenEmail) = await NewUserAsync("taken");
        var list = await admin.GetAsync<List<UserDto>>("/api/v1/users");

        foreach (var (email, newName) in new[] { (userEmail, "Renamed User"), (adminEmail, "Renamed Admin") })
        {
            var u = list.Single(x => x.Email == email);
            var newEmail = $"new.{Guid.NewGuid().ToString("N")[..8]}@Test.Local";
            var saved = await admin.PutAsync<UserDto>($"/api/v1/users/{u.Id}", new UpdateUserRequest(newName, true, u.Roles, null, newEmail));
            saved.DisplayName.Should().Be(newName);
            saved.Email.Should().Be(newEmail.ToLowerInvariant());
            saved.Roles.Should().BeEquivalentTo(u.Roles);
            (await _app.Anonymous().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, TestApp.Password), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await _app.Anonymous().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(newEmail, TestApp.Password), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // an email that someone else already uses is refused, and leaving the email out changes nothing
        var one = list.Single(x => x.Email == takenEmail);
        var clash = await admin.PutAsJsonAsync($"/api/v1/users/{one.Id}", new UpdateUserRequest(one.DisplayName, true, one.Roles, null, TestApp.AdminEmail), TestJson.Options);
        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var bad = await admin.PutAsJsonAsync($"/api/v1/users/{one.Id}", new UpdateUserRequest(one.DisplayName, true, one.Roles, null, "not-an-email"), TestJson.Options);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var same = await admin.PutAsync<UserDto>($"/api/v1/users/{one.Id}", new UpdateUserRequest("Only the name", true, one.Roles, null));
        same.Email.Should().Be(takenEmail);
        same.DisplayName.Should().Be("Only the name");
    }

    [Fact]
    public async Task An_unknown_AI_provider_is_refused()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var res = await admin.PutAsJsonAsync("/api/v1/admin/ai", new SaveAiAdminRequest("Skynet", null, null), TestJson.Options);
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.ProblemAsync()).Should().Contain("must be one of");
    }

    [Fact]
    public async Task The_admin_overview_counts_users_projects_and_documents_and_reports_the_AI_state()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        await NewUserAsync();
        var overview = await admin.GetAsync<AdminOverviewDto>("/api/v1/admin/overview");
        overview.Users.Should().BeGreaterThan(1);
        overview.ActiveUsers.Should().BeLessThanOrEqualTo(overview.Users);
        overview.AiProvider.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_system_info_tells_any_user_whether_the_AI_is_ready()
    {
        var (user, _) = await NewUserAsync();
        var info = await user.GetAsync<System.Text.Json.JsonElement>("/api/v1/system/info");
        info.GetProperty("ai").GetString().Should().NotBeNullOrEmpty();
    }

    // ------------------------------------------------------------------ diagram, guide and the shared library

    [RequiresBrowserFact]
    public async Task A_user_gets_the_workflow_diagram_and_an_implementation_guide_pdf_and_everybody_can_find_it_in_the_library()
    {
        var (user, _) = await NewUserAsync("author");
        var (colleague, _) = await NewUserAsync("colleague");
        var (stranger, _) = await NewUserAsync("stranger");
        await (await _app.LoginAsync(TestApp.AdminEmail)).ShareClientAsync(user, colleague);        // the same client, so the documents are shared
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Guide project " + Guid.NewGuid().ToString("N")[..5], null));
        await AddNoteAsync(user, quick.FlowId, "When the case sheet comes the user opens the receive menu and picks the ward. A super user gets his own ward pre-selected.");
        await AddNoteAsync(user, quick.FlowId, "This is a change to existing behaviour. Priority is High. All MRD users use this menu frequently.");

        // no guide before the analysis
        (await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        (await AnalyseAsync(user, quick.FlowId)).Status.Should().Be("Succeeded");
        var diagram = await user.GetAsync($"/api/v1/flows/{quick.FlowId}/diagram?format=png");
        diagram.StatusCode.Should().Be(HttpStatusCode.OK, await diagram.ProblemAsync());
        (await diagram.Content.ReadAsByteArrayAsync()).Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47);

        var started = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.ProblemAsync());
        var job = await started.ReadAsync<JobDto>();
        job.Kind.Should().Be("GuideDocument");
        await _app.WhenJobsDone();
        var done = await user.GetAsync<JobDto>($"/api/v1/jobs/{job.JobId}");
        done.State.Should().Be("Succeeded", done.Message);
        // the document is written from the analysed workflow: no second AI call, so no extra tokens and no waiting for the AI
        _app.Ai.Calls.Should().NotContain(c => c.ToolName == "submit_implementation_plan");
        var used = await user.GetAsync<AiUsageSummaryDto>("/api/v1/ai-usage");
        used.ByKind.Should().NotContain(k => k.Kind == "Guide");

        // a colleague on the same client sees it in the library and can open it; somebody on another client cannot
        (await stranger.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Should().NotContain(d => d.ProjectId == quick.ProjectId);
        var library = await colleague.GetAsync<List<LibraryDocumentDto>>("/api/v1/library");
        var item = library.Should().ContainSingle(d => d.ProjectId == quick.ProjectId).Subject;
        item.Kind.Should().Be("Guide");
        item.Formats.Should().Equal("pdf");
        item.HasDiagram.Should().BeTrue();
        item.DocNumber.Should().StartWith("IMPL-");

        var pdf = await colleague.GetAsync($"/api/v1/documents/{item.Id}/versions/{item.VersionNo}/download?format=pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK, await pdf.ProblemAsync());
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        bytes.Length.Should().BeGreaterThan(5000);

        (await colleague.GetAsync($"/api/v1/documents/{item.Id}/versions/{item.VersionNo}/download?format=docx")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await colleague.GetAsync($"/api/v1/library/{item.Id}/diagram?format=png")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await stranger.GetAsync($"/api/v1/documents/{item.Id}/versions/{item.VersionNo}/download?format=pdf")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await stranger.GetAsync($"/api/v1/library/{item.Id}/diagram?format=png")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // generating again adds a version to the same document
        await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { });
        await _app.WhenJobsDone();
        var again = (await colleague.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Single(d => d.ProjectId == quick.ProjectId);
        again.Id.Should().Be(item.Id);
        again.VersionNo.Should().Be(2);
    }

    [RequiresBrowserFact]
    public async Task A_document_made_without_a_prior_picture_still_gives_the_diagram_as_svg_and_draws_the_png_when_first_asked_for()
    {
        var (user, _) = await NewUserAsync("lazy-png");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Lazy picture " + Guid.NewGuid().ToString("N")[..5], null));
        await AddNoteAsync(user, quick.FlowId, "When the case sheet comes the user opens the receive menu and picks the ward. Priority is High. This is a change to existing behaviour.");
        (await AnalyseAsync(user, quick.FlowId)).Status.Should().Be("Succeeded");

        // the document is the first thing that draws the diagram: it needs only the SVG
        (await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _app.WhenJobsDone();
        var item = (await user.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Single(d => d.ProjectId == quick.ProjectId);

        var svg = await user.GetAsync($"/api/v1/library/{item.Id}/diagram?format=svg");
        svg.StatusCode.Should().Be(HttpStatusCode.OK, await svg.ProblemAsync());
        (await svg.Content.ReadAsStringAsync()).Should().StartWith("<svg");
        var png = await user.GetAsync($"/api/v1/library/{item.Id}/diagram?format=png");     // drawn now, once, because somebody asked for it
        png.StatusCode.Should().Be(HttpStatusCode.OK, await png.ProblemAsync());
        (await png.Content.ReadAsByteArrayAsync()).Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47);
    }

    [RequiresBrowserFact]
    public async Task When_the_admin_switches_AI_wording_on_the_document_asks_the_AI_once_more_and_counts_it()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (user, _) = await NewUserAsync("wording");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Wording " + Guid.NewGuid().ToString("N")[..5], null));
        await AddNoteAsync(user, quick.FlowId, "When the case sheet comes the user opens the receive menu and picks the ward. Priority is High. This is a change to existing behaviour.");
        (await AnalyseAsync(user, quick.FlowId)).Status.Should().Be("Succeeded");
        try
        {
            (await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, AiWording: true))).AiWording.Should().BeTrue();
            await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { });
            await _app.WhenJobsDone();
            _app.Ai.Calls.Last().ToolName.Should().Be("submit_implementation_plan");
            (await user.GetAsync<AiUsageSummaryDto>("/api/v1/ai-usage")).ByKind.Should().Contain(k => k.Kind == "Guide" && k.TotalTokens == 2200);
        }
        finally { await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, AiWording: false)); }
    }

    // ------------------------------------------------------------------ prompts and lists in the notes

    [Fact]
    public async Task The_newest_prompt_version_in_the_prompt_folder_is_the_active_one_and_the_older_one_is_kept_but_inactive()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowCapture.Application.Abstractions.IAppDbContext>();
        var rows = await db.PromptTemplates.AsNoTracking().ToListAsync();

        foreach (var key in new[] { "common-rules", "part-analysis.system", "reanalysis.system", "whole-analysis.system", "schema.flow" })
        {
            var versions = rows.Where(r => r.Key == key).ToList();
            versions.Select(v => v.Version).Should().Contain([1, 2], $"{key} has both versions stored");
            versions.Where(v => v.IsActive).Should().ContainSingle($"{key} has exactly one active version").Which.Version.Should().Be(2);
        }
        rows.Single(r => r.Key == "part-analysis.system" && r.IsActive).Content.Should().Contain("{{include:common-rules.v2}}");
    }

    [Fact]
    public async Task A_list_in_the_notes_that_does_not_come_back_as_steps_of_its_own_is_sent_back_once_and_then_accepted_with_a_warning()
    {
        var (user, _) = await NewUserAsync("lists");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Masters " + Guid.NewGuid().ToString("N")[..5], null));
        await AddNoteAsync(user, quick.FlowId,
            "Treatment Method: how the waste is destroyed.\nWaste Category: bag colours.\nException Type: the kind of problem.\nReason: why a record was changed.\nWeight Unit: kilogram or gram.\nCBWTF: the outside company that collects the waste.");

        var run = await AnalyseAsync(user, quick.FlowId);

        run.Status.Should().Be("Succeeded");
        _app.Ai.Calls.Should().HaveCount(2, "asked again once, never more");
        var second = _app.Ai.Calls.Last().UserMessage;
        second.Should().Contain("list of 6 items").And.Contain("EACH must be its own step").And.NotContain("<previous_output>", "a missing step is told in words: sending the whole first answer back would only cost tokens");
        _app.Ai.Calls.First().Careful.Should().BeFalse("the first attempt thinks lightly");
        _app.Ai.Calls.Last().Careful.Should().BeTrue("the corrective attempt may think harder");
        run.Warnings.Should().Contain(w => w.Contains("notes list several items"));
    }

    [Fact]
    public async Task Requests_are_new_requirements_or_change_requests_numbered_inside_their_module()
    {
        var (user, _) = await NewUserAsync("folders");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Folders " + Guid.NewGuid().ToString("N")[..5], null));
        var first = await user.GetAsync<FlowDto>($"/api/v1/flows/{quick.FlowId}");
        first.Kind.Should().Be("NR");
        first.RequestCode.Should().Be("NR-001", "the request made with the project is the first new requirement");

        var ward = await user.AddModuleAsync(quick.ProjectId, "Ward");
        ward.ModuleCode.Should().Be("WARD");
        FlowDto Parse(HttpResponseMessage r) => r.ReadAsync<FlowDto>().GetAwaiter().GetResult();
        async Task<FlowDto> Make(string name, string? kind, Guid? dept = null) =>
            Parse(await user.PostAsJsonAsync($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(dept ?? ward.Id, name, kind), TestJson.Options));

        (await Make("Bed allocation", null)).RequestCode.Should().Be("NR-001", "a request is a new requirement unless it says otherwise, and each module counts from 1");
        (await Make("Bed transfer", "nr")).RequestCode.Should().Be("NR-002");
        (await Make("Discharge print", "CR")).RequestCode.Should().Be("CR-001", "new requirements and change requests are counted apart");
        (await Make("Diet chart", "CR")).RequestCode.Should().Be("CR-002");
        (await Make("Bed allocation", "CR")).RequestCode.Should().Be("NR-001", "the same name in the same module is the same request: nothing new is numbered");
        (await Make("Ward round", "NR", quick.DepartmentId)).RequestCode.Should().Be("NR-002", "another module counts on its own: General already has NR-001");

        var listed = await user.GetAsync<List<FlowDto>>($"/api/v1/projects/{quick.ProjectId}/flows");
        listed.Where(f => f.ProjectDepartmentId == ward.Id).Select(f => f.RequestCode).Should().BeEquivalentTo(["NR-001", "NR-002", "CR-001", "CR-002"]);

        var bad = await user.PostAsJsonAsync($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(ward.Id, "Something", "ZZ"), TestJson.Options);
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await bad.ProblemAsync()).Should().Contain("invalid_kind");
    }

    [RequiresBrowserFact]
    public async Task The_document_of_a_request_is_named_after_its_module_its_kind_and_its_number()
    {
        var (user, _) = await NewUserAsync("docname");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Names " + Guid.NewGuid().ToString("N")[..5], null));
        var ward = await user.AddModuleAsync(quick.ProjectId, "Ward");
        var cr = await (await user.PostAsJsonAsync($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(ward.Id, "Discharge print", "CR"), TestJson.Options)).ReadAsync<FlowDto>();
        await AddNoteAsync(user, cr.Id, "When the case sheet comes the user opens the discharge menu and picks the ward. Priority is High. All nurses use this menu frequently.");
        (await AnalyseAsync(user, cr.Id)).Status.Should().Be("Succeeded");

        (await user.PostAsync($"/api/v1/flows/{cr.Id}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _app.WhenJobsDone();

        var docs = await user.GetAsync<List<DocumentDto>>($"/api/v1/flows/{cr.Id}/documents");
        docs.Should().ContainSingle(d => d.DocNumber == "IMPL-WARD-CR-001", "module, kind and number name the document");
    }
    [Fact]
    public async Task Ordinary_notes_without_a_list_are_analysed_with_one_call()
    {
        var (user, _) = await NewUserAsync("nolist");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Plain " + Guid.NewGuid().ToString("N")[..5], null));
        await AddNoteAsync(user, quick.FlowId, "When the case sheet comes the user opens the receive menu and picks the ward. Priority is High.");
        (await AnalyseAsync(user, quick.FlowId)).Status.Should().Be("Succeeded");
        _app.Ai.Calls.Should().HaveCount(1);
    }
    // ------------------------------------------------------------------ privacy, accounts and limits

    [Fact]
    public async Task Photos_and_recordings_are_refused_while_the_admin_has_switched_them_off_but_typed_notes_still_work()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (user, _) = await NewUserAsync("privacy");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Privacy project", null));
        var photo = await (await user.PostAsync($"/api/v1/flows/{quick.FlowId}/images", Photo())).ReadAsync<EntryDto>();
        await _app.WhenJobsDone();
        try
        {
            (await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, AllowMedia: false))).AllowMedia.Should().BeFalse();
            (await user.GetAsync<System.Text.Json.JsonElement>("/api/v1/system/info")).GetProperty("allowMedia").GetBoolean().Should().BeFalse();

            var refused = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/images", Photo());
            refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await refused.ProblemAsync()).Should().Contain("media_blocked").And.Contain("switched off");
            var voice = await user.PostAsJsonAsync($"/api/v1/flows/{quick.FlowId}/entries", new CreateEntryRequest(Guid.NewGuid(), "Voice", DateTime.UtcNow, "en-IN", null, null, null, null), TestJson.Options);
            voice.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await user.PostAsync($"/api/v1/entries/{photo.Id}/transcribe", new { })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);   // "try again" on an old photo sends it too
            (await AddNoteAsync(user, quick.FlowId, "A typed note is still fine.")).Kind.Should().Be("Text");
        }
        finally { await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, AllowMedia: true)); }

        (await user.PostAsync($"/api/v1/flows/{quick.FlowId}/images", Photo())).StatusCode.Should().Be(HttpStatusCode.Created);   // and back on again
    }

    [Fact]
    public async Task Only_the_admin_can_list_the_accounts()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (user, _) = await NewUserAsync("nosy");
        (await user.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.GetAsync<List<UserDto>>("/api/v1/users")).Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_saved_AI_key_never_reaches_the_audit_log_not_even_encrypted()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, "AIza-audit-secret-5678"));
        await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, "AIza-audit-secret-9999"));
        try
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlowCapture.Application.Abstractions.IAppDbContext>();
            var stored = await db.SystemSettings.AsNoTracking().Where(s => s.Key == FlowCapture.Application.Services.SettingsService.GeminiKeySetting).Select(s => s.Value).SingleAsync();
            stored.Should().NotBeNullOrEmpty();

            var rows = await db.AuditLogs.AsNoTracking().Where(a => a.EntityType == "SystemSetting").ToListAsync();
            rows.Should().NotBeEmpty();
            foreach (var row in rows) (row.NewValues + row.OldValues).Should().NotContain(stored).And.NotContain("AIza");
            rows.Any(r => (r.NewValues ?? "").Contains("redacted")).Should().BeTrue("the change is still recorded, with the value hidden");
        }
        finally { await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, "", "")); }
    }

    [Fact]
    public async Task One_person_pressing_the_AI_buttons_in_a_loop_is_stopped_and_does_not_use_up_the_shared_quota()
    {
        var (user, _) = await NewUserAsync("loop");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Loop project", null));
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 30; i++) statuses.Add((await user.PostAsync($"/api/v1/flows/{quick.FlowId}/analyses", new StartAnalysisRequest("Initial", null))).StatusCode);

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
        statuses.Take(10).Should().NotContain(HttpStatusCode.TooManyRequests, "a normal person is never limited");
        var limited = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/analyses", new StartAnalysisRequest("Initial", null));
        (await limited.ProblemAsync()).Should().Contain("Wait a minute");
        // somebody else is not affected
        var (other, _) = await NewUserAsync("calm");
        var otherQuick = await other.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Calm project", null));
        (await other.PostAsync($"/api/v1/flows/{otherQuick.FlowId}/analyses", new StartAnalysisRequest("Initial", null))).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Only_someone_who_can_capture_in_the_project_can_start_its_guide()
    {
        var (owner, _) = await NewUserAsync("owner");
        var (other, _) = await NewUserAsync("other");
        var quick = await owner.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Mine too", null));
        (await other.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

internal static class RawHttp
{
    public static Task<HttpResponseMessage> PostAsync(this HttpClient c, string url, object body) => c.PostAsJsonAsync(url, body, TestJson.Options);
}
