using System.Net;
using System.Net.Http.Json;
using System.Text;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>One big PDF of a whole project and a smaller PDF for each module, made from the analysed requests (real Chrome prints them).</summary>
[Collection("api")]
public class CombinedGuideTests
{
    private readonly TestApp _app;

    public CombinedGuideTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
    }

    private async Task<(HttpClient Client, Guid Id)> NewUserAsync(string name)
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var email = $"{name}.{Guid.NewGuid().ToString("N")[..8]}@test.local";
        var made = await admin.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, "Test " + name, TestApp.Password, [AppRoles.Lead]));
        await admin.NewClientAsync(null, made.Id);
        return (await _app.LoginAsync(email), made.Id);
    }

    private async Task AnalyseAsync(HttpClient c, Guid flowId)
    {
        foreach (var text in new[] { "When the case sheet comes the user opens the receive menu and picks the ward. A super user gets his own ward pre-selected.", "This is a change to existing behaviour. Priority is High. All MRD users use this menu frequently." })
            await c.PostAsJsonAsync($"/api/v1/flows/{flowId}/entries", new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", text, null, null, null), TestJson.Options);
        await c.PostAsJsonAsync($"/api/v1/flows/{flowId}/analyses", new StartAnalysisRequest("Initial", null), TestJson.Options);
        await _app.WhenJobsDone();
    }

    private async Task<JobDto> RunAsync(HttpClient c, string url)
    {
        var started = await c.PostAsync(url, new { });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.ProblemAsync());
        var job = await started.ReadAsync<JobDto>();
        await _app.WhenJobsDone();
        return await c.GetAsync<JobDto>($"/api/v1/jobs/{job.JobId}");
    }

    [RequiresBrowserFact]
    public async Task A_whole_project_PDF_and_a_PDF_for_each_module_are_made_from_the_analysed_requests_with_no_AI()
    {
        var (user, _) = await NewUserAsync("combiner");
        var (stranger, _) = await NewUserAsync("combiner-stranger");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Combined " + Guid.NewGuid().ToString("N")[..5], null));
        var pharmacy = await user.AddModuleAsync(quick.ProjectId, "Pharmacy");
        var empty = await user.AddModuleAsync(quick.ProjectId, "Billing");
        var returns = await user.PostAsync<FlowDto>($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(pharmacy.Id, "Returns"));
        await user.PostAsync<FlowDto>($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(pharmacy.Id, "Not analysed one"));
        await user.PostAsync<FlowDto>($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(empty.Id, "Nothing here"));

        // nothing analysed yet: a clear reason, no job
        var none = await user.PostAsync($"/api/v1/projects/{quick.ProjectId}/guide", new { });
        none.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await none.ProblemAsync()).Should().Contain("not_analysed");

        await AnalyseAsync(user, quick.FlowId);
        await AnalyseAsync(user, returns.Id);
        var aiCalls = _app.Ai.Calls.Count;

        // somebody on another client cannot
        (await stranger.PostAsync($"/api/v1/projects/{quick.ProjectId}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await stranger.PostAsync($"/api/v1/projects/{quick.ProjectId}/modules/{pharmacy.Id}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // the whole project
        var whole = await RunAsync(user, $"/api/v1/projects/{quick.ProjectId}/guide");
        whole.Kind.Should().Be("GuideProjectDocument");
        whole.State.Should().Be("Succeeded", whole.Message);
        var library = await user.GetAsync<List<LibraryDocumentDto>>("/api/v1/library");
        var project = library.Should().ContainSingle(d => d.ProjectId == quick.ProjectId && d.Kind == "ProjectGuide").Subject;
        project.DocNumber.Should().EndWith("-FULL");
        project.HasDiagram.Should().BeFalse();
        var pdf = await user.GetAsync($"/api/v1/documents/{project.Id}/versions/{project.VersionNo}/download?format=pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK, await pdf.ProblemAsync());
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        bytes.Length.Should().BeGreaterThan(8000);

        // one module
        var module = await RunAsync(user, $"/api/v1/projects/{quick.ProjectId}/modules/{pharmacy.Id}/guide");
        module.Kind.Should().Be("GuideModuleDocument");
        module.State.Should().Be("Succeeded", module.Message);
        var item = (await user.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Should().ContainSingle(d => d.Kind == "ModuleGuide" && d.ProjectId == quick.ProjectId).Subject;
        item.ModuleId.Should().Be(pharmacy.Id);
        item.DocNumber.Should().EndWith("-MOD");
        (await user.GetAsync($"/api/v1/documents/{item.Id}/versions/1/download?format=pdf")).StatusCode.Should().Be(HttpStatusCode.OK);

        // a module with nothing analysed
        var emptyRun = await user.PostAsync($"/api/v1/projects/{quick.ProjectId}/modules/{empty.Id}/guide", new { });
        emptyRun.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // making it again adds a version of the same document, and the other people on another client still see nothing of it
        var again = await RunAsync(user, $"/api/v1/projects/{quick.ProjectId}/guide");
        again.State.Should().Be("Succeeded");
        var second = (await user.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Single(d => d.Kind == "ProjectGuide" && d.ProjectId == quick.ProjectId);
        second.Id.Should().Be(project.Id);
        second.VersionNo.Should().Be(2);
        (await stranger.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Should().NotContain(d => d.ProjectId == quick.ProjectId);
        (await stranger.GetAsync($"/api/v1/documents/{project.Id}/versions/1/download?format=pdf")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // no AI was used for any of this
        _app.Ai.Calls.Count.Should().Be(aiCalls);
    }

    [Fact]
    public async Task The_PDFs_need_a_request_that_was_analysed_and_a_module_that_belongs_to_the_project()
    {
        var (user, _) = await NewUserAsync("combiner2");
        var (other, _) = await NewUserAsync("combiner3");
        var quick = await user.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Plain " + Guid.NewGuid().ToString("N")[..5], null));
        var elsewhere = await other.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Else " + Guid.NewGuid().ToString("N")[..5], null));

        (await user.PostAsync($"/api/v1/projects/{quick.ProjectId}/modules/{quick.DepartmentId}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        // a module of another project is not found, whoever asks
        (await user.PostAsync($"/api/v1/projects/{quick.ProjectId}/modules/{elsewhere.DepartmentId}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await user.PostAsync($"/api/v1/projects/{Guid.NewGuid()}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
