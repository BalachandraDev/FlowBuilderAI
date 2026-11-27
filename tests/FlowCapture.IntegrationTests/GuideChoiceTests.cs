using System.Net;
using System.Net.Http.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>Before a PDF is made the person sees its headings and chooses which go in; only those are written.</summary>
[Collection("api")]
public class GuideChoiceTests
{
    private readonly TestApp _app;

    public GuideChoiceTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
    }

    private async Task<HttpClient> NewUserAsync(string name)
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var email = $"{name}.{Guid.NewGuid().ToString("N")[..8]}@test.local";
        var made = await admin.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, "Test " + name, TestApp.Password, [AppRoles.Lead]));
        await admin.NewClientAsync(null, made.Id);
        return await _app.LoginAsync(email);
    }

    private async Task AnalyseAsync(HttpClient c, Guid flowId)
    {
        foreach (var text in new[] { "When the case sheet comes the user opens the receive menu and picks the ward. A super user gets his own ward pre-selected.", "This is a change to existing behaviour. Priority is High. All MRD users use this menu frequently." })
            await c.PostAsJsonAsync($"/api/v1/flows/{flowId}/entries", new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", text, null, null, null), TestJson.Options);
        await c.PostAsJsonAsync($"/api/v1/flows/{flowId}/analyses", new StartAnalysisRequest("Initial", null), TestJson.Options);
        await _app.WhenJobsDone();
    }

    private static string PdfText(byte[] bytes)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        return string.Join("\n", pdf.GetPages().Select(p => p.Text));
    }

    [Fact]
    public async Task The_outline_of_a_request_lists_its_headings_and_needs_an_analysis_and_the_right_to_make_the_document()
    {
        var user = await NewUserAsync("outline");
        var stranger = await NewUserAsync("outline-stranger");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Outline " + Guid.NewGuid().ToString("N")[..5], null));

        var early = await user.GetAsync($"/api/v1/flows/{quick.FlowId}/guide-outline");
        early.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await early.ProblemAsync()).Should().Contain("not_analysed");

        await AnalyseAsync(user, quick.FlowId);
        var outline = await user.GetAsync<GuideOutlineDto>($"/api/v1/flows/{quick.FlowId}/guide-outline");
        outline.Sections.Select(s => s.Key).Should().StartWith("control", "purpose").And.EndWith("signoff");
        outline.Sections.Should().OnlyContain(s => s.Title.Length > 0 && s.Detail.Length > 0);
        outline.Sections.Should().OnlyContain(s => s.Parts.Count == 1 && s.Parts[0].Html.Contains("<h2"), "the page of each heading can be seen before choosing");
        outline.Style.Should().Contain(".paper");
        outline.Cover.Should().Contain("Implementation Document");
        outline.Requests.Should().BeEmpty("a single request has no list of requests");

        (await stranger.GetAsync($"/api/v1/flows/{quick.FlowId}/guide-outline")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_outline_of_the_project_and_of_a_module_lists_the_headings_and_the_analysed_requests_only()
    {
        var user = await NewUserAsync("outline2");
        var stranger = await NewUserAsync("outline2-stranger");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Outline2 " + Guid.NewGuid().ToString("N")[..5], null));
        var pharmacy = await user.AddModuleAsync(quick.ProjectId, "Pharmacy");
        var returns = await user.PostAsync<FlowDto>($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(pharmacy.Id, "Returns"));
        await user.PostAsync<FlowDto>($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(pharmacy.Id, "Not analysed one"));

        (await user.GetAsync($"/api/v1/projects/{quick.ProjectId}/guide-outline")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await AnalyseAsync(user, quick.FlowId);
        await AnalyseAsync(user, returns.Id);

        var whole = await user.GetAsync<GuideOutlineDto>($"/api/v1/projects/{quick.ProjectId}/guide-outline");
        whole.Requests.Select(r => r.FlowId).Should().BeEquivalentTo([quick.FlowId, returns.Id]);
        whole.Requests.Should().OnlyContain(r => r.Title.Length > 0 && r.Module.Length > 0);
        whole.Sections.Select(s => s.Key).Should().Contain("purpose").And.NotContain(["control", "signoff"], "those belong to a request's own document");
        whole.Sections.Should().OnlyContain(s => s.Detail.StartsWith("in "));
        whole.Sections.First(s => s.Key == "purpose").Parts.Select(p => p.FlowId).Should().BeEquivalentTo([quick.FlowId, returns.Id], "a page for each request, so each chapter can be previewed");
        whole.Sections.SelectMany(s => s.Parts).Should().OnlyContain(p => p.Html.Contains("<h2") && !p.Html.Contains("data:image"));
        whole.Style.Should().Contain(".paper");

        var module = await user.GetAsync<GuideOutlineDto>($"/api/v1/projects/{quick.ProjectId}/modules/{pharmacy.Id}/guide-outline");
        module.Requests.Should().ContainSingle().Which.FlowId.Should().Be(returns.Id);

        (await stranger.GetAsync($"/api/v1/projects/{quick.ProjectId}/guide-outline")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await stranger.GetAsync($"/api/v1/projects/{quick.ProjectId}/modules/{pharmacy.Id}/guide-outline")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_choice_with_an_unknown_section_nothing_chosen_or_no_analysed_request_is_refused_and_no_job_is_made()
    {
        var user = await NewUserAsync("refuse");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Refuse " + Guid.NewGuid().ToString("N")[..5], null));
        await AnalyseAsync(user, quick.FlowId);

        var unknown = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { sections = new[] { "purpose", "nonsense" } });
        unknown.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await unknown.ProblemAsync()).Should().Contain("unknown_section");

        var nothing = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { sections = Array.Empty<string>() });
        nothing.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await nothing.ProblemAsync()).Should().Contain("nothing_chosen");

        var projectNothing = await user.PostAsync($"/api/v1/projects/{quick.ProjectId}/guide", new { flowIds = new[] { Guid.NewGuid() } });
        projectNothing.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await projectNothing.ProblemAsync()).Should().Contain("not_analysed");

        // a heading for a section that does not exist, or one that is too long, is refused too
        var badTitle = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { titles = new Dictionary<string, string> { ["nonsense"] = "Mine" } });
        badTitle.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await badTitle.ProblemAsync()).Should().Contain("unknown_section");
        var longTitle = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { titles = new Dictionary<string, string> { ["purpose"] = new string('x', 121) } });
        longTitle.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await longTitle.ProblemAsync()).Should().Contain("heading_too_long");
        // text under a heading: a section that does not exist, one whose text cannot be written (the sign-off), and text that is too long are refused too
        var badText = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { texts = new Dictionary<string, string> { ["nonsense"] = "Mine" } });
        badText.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await badText.ProblemAsync()).Should().Contain("unknown_section");
        var fixedText = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { texts = new Dictionary<string, string> { ["signoff"] = "Mine" } });
        fixedText.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await fixedText.ProblemAsync()).Should().Contain("section_not_editable");
        var longText = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { texts = new Dictionary<string, string> { ["purpose"] = new string('x', 8001) } });
        longText.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await longText.ProblemAsync()).Should().Contain("text_too_long");
        (await _app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope().ServiceProvider.GetRequiredService<IAppDbContext>().DocumentJobRuns.AsNoTracking()
            .CountAsync(j => j.FlowId == quick.FlowId)).Should().Be(0, "a refused request makes no job");
    }

    [RequiresBrowserFact]
    public async Task A_renamed_heading_is_what_the_PDF_says_and_only_for_that_PDF()
    {
        var user = await NewUserAsync("renamer");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Renamer " + Guid.NewGuid().ToString("N")[..5], null));
        await AnalyseAsync(user, quick.FlowId);

        var started = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { sections = new[] { "purpose", "stages" }, titles = new Dictionary<string, string> { ["purpose"] = "Why we are doing this", ["control"] = "Not printed" } });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.ProblemAsync());
        await _app.WhenJobsDone();
        var doc = (await user.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Single(d => d.FlowId == quick.FlowId && d.Kind == "Guide");
        var text = PdfText(await (await user.GetAsync($"/api/v1/documents/{doc.Id}/versions/{doc.VersionNo}/download?format=pdf")).Content.ReadAsByteArrayAsync());
        text.Should().Contain("Why we are doing this").And.Contain("How it works, stage by stage");
        text.Should().NotContain("Not printed", "a heading that is not ticked is never printed");

        // the next PDF without a new wording has the standard headings again
        (await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { sections = new[] { "purpose" } })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _app.WhenJobsDone();
        var again = (await user.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Single(d => d.FlowId == quick.FlowId && d.Kind == "Guide");
        again.VersionNo.Should().BeGreaterThan(doc.VersionNo);
        PdfText(await (await user.GetAsync($"/api/v1/documents/{again.Id}/versions/{again.VersionNo}/download?format=pdf")).Content.ReadAsByteArrayAsync()).Should().NotContain("Why we are doing this");
    }

    [RequiresBrowserFact]
    public async Task Text_written_under_a_heading_is_what_the_PDF_says_instead_of_the_generated_text_and_only_for_that_PDF()
    {
        var user = await NewUserAsync("writer");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Writer " + Guid.NewGuid().ToString("N")[..5], null));
        await AnalyseAsync(user, quick.FlowId);

        var started = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { sections = new[] { "purpose", "scope" }, texts = new Dictionary<string, string> { ["purpose"] = "The consultant wrote this sentence about the purpose.\n- A first bullet by the consultant", ["stages"] = "Not printed" } });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.ProblemAsync());
        await _app.WhenJobsDone();
        var doc = (await user.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Single(d => d.FlowId == quick.FlowId && d.Kind == "Guide");
        var text = PdfText(await (await user.GetAsync($"/api/v1/documents/{doc.Id}/versions/{doc.VersionNo}/download?format=pdf")).Content.ReadAsByteArrayAsync());
        text.Should().Contain("The consultant wrote this sentence about the purpose.").And.Contain("A first bullet by the consultant");
        text.Should().NotContain("Not printed", "the text of a heading that is not ticked is never printed");

        // the next PDF without written text has the generated text again
        (await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { sections = new[] { "purpose" } })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _app.WhenJobsDone();
        var again = (await user.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Single(d => d.FlowId == quick.FlowId && d.Kind == "Guide");
        again.VersionNo.Should().BeGreaterThan(doc.VersionNo);
        PdfText(await (await user.GetAsync($"/api/v1/documents/{again.Id}/versions/{again.VersionNo}/download?format=pdf")).Content.ReadAsByteArrayAsync()).Should().NotContain("The consultant wrote this sentence");
    }

    [RequiresBrowserFact]
    public async Task Only_the_chosen_sections_and_requests_are_in_the_PDFs()
    {
        var user = await NewUserAsync("chooser");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Chooser " + Guid.NewGuid().ToString("N")[..5], null));
        var pharmacy = await user.AddModuleAsync(quick.ProjectId, "Pharmacy");
        var returns = await user.PostAsync<FlowDto>($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(pharmacy.Id, "Drug returns"));
        var visitor = await user.PostAsync<FlowDto>($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(quick.DepartmentId, "Visitor pass entry"));
        await AnalyseAsync(user, quick.FlowId);
        await AnalyseAsync(user, returns.Id);
        await AnalyseAsync(user, visitor.Id);

        // one request, two headings
        var started = await user.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { sections = new[] { "purpose", "stages" } });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.ProblemAsync());
        await _app.WhenJobsDone();
        var doc = (await user.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Single(d => d.FlowId == quick.FlowId && d.Kind == "Guide");
        var text = PdfText(await (await user.GetAsync($"/api/v1/documents/{doc.Id}/versions/{doc.VersionNo}/download?format=pdf")).Content.ReadAsByteArrayAsync());
        text.Should().Contain("Purpose").And.Contain("How it works, stage by stage");
        text.Should().NotContain("Document control").And.NotContain("Approval and sign-off").And.NotContain("Where it touches the system").And.NotContain("Functional requirements");

        // the project PDF with only one of the two requests and one heading
        var project = await user.PostAsync($"/api/v1/projects/{quick.ProjectId}/guide", new { sections = new[] { "purpose" }, flowIds = new[] { returns.Id } });
        project.StatusCode.Should().Be(HttpStatusCode.Accepted, await project.ProblemAsync());
        await _app.WhenJobsDone();
        var whole = (await user.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Single(d => d.ProjectId == quick.ProjectId && d.Kind == "ProjectGuide");
        var wholeText = PdfText(await (await user.GetAsync($"/api/v1/documents/{whole.Id}/versions/{whole.VersionNo}/download?format=pdf")).Content.ReadAsByteArrayAsync());
        wholeText.Should().Contain("Drug returns").And.Contain("Purpose");
        wholeText.Should().NotContain("How it works, stage by stage").And.NotContain("Where it touches the system");
        wholeText.Should().NotContain("Visitor pass entry", "a request that was not chosen is not in the PDF");
    }
}
