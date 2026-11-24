using System.Net;
using System.Net.Http.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>Bugs as a third kind of request, renaming a request, and picking a module from the master list.</summary>
[Collection("api")]
public class HeadingsAndBugsTests
{
    private readonly TestApp _app;

    public HeadingsAndBugsTests(TestApp app)
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

    private async Task<(QuickProjectDto Project, ProjectDepartmentDto Ward)> NewProjectAsync(HttpClient c)
    {
        var quick = await c.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest("Headings " + Guid.NewGuid().ToString("N")[..5], null));
        var ward = await c.AddModuleAsync(quick.ProjectId, "Ward");
        return (quick, ward);
    }

    private static async Task<FlowDto> MakeAsync(HttpClient c, Guid project, Guid module, string name, string? kind) =>
        await (await c.PostAsJsonAsync($"/api/v1/projects/{project}/flows", new CreateFlowRequest(module, name, kind), TestJson.Options)).ReadAsync<FlowDto>();

    // ------------------------------------------------------------------ bugs

    [Fact]
    public async Task A_bug_is_a_request_of_its_own_kind_numbered_apart_from_new_requirements_and_change_requests()
    {
        var user = await NewUserAsync("bugs");
        var (quick, ward) = await NewProjectAsync(user);

        (await MakeAsync(user, quick.ProjectId, ward.Id, "Bed allocation", "NR")).RequestCode.Should().Be("NR-001");
        var bug = await MakeAsync(user, quick.ProjectId, ward.Id, "Discharge shows the wrong ward", "BUG");
        bug.RequestCode.Should().Be("BUG-001", "bugs are counted on their own inside the module");
        bug.Kind.Should().Be("BUG");
        (await MakeAsync(user, quick.ProjectId, ward.Id, "Print is cut off", "bug")).RequestCode.Should().Be("BUG-002", "the kind is not case sensitive");
        (await MakeAsync(user, quick.ProjectId, ward.Id, "Discharge print", "CR")).RequestCode.Should().Be("CR-001");

        var listed = await user.GetAsync<List<FlowDto>>($"/api/v1/projects/{quick.ProjectId}/flows");
        listed.Where(f => f.ProjectDepartmentId == ward.Id).Select(f => f.RequestCode).Should().BeEquivalentTo(["NR-001", "BUG-001", "BUG-002", "CR-001"]);

        // a bug takes notes like any other request
        var note = await user.PostAsJsonAsync($"/api/v1/flows/{bug.Id}/entries", new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", "On the discharge screen the ward shows the old ward after a transfer.", null, null, null), TestJson.Options);
        note.StatusCode.Should().Be(HttpStatusCode.Created);

        var bad = await user.PostAsJsonAsync($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(ward.Id, "Something", "ZZ"), TestJson.Options);
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await bad.ProblemAsync()).Should().Contain("invalid_kind").And.Contain("BUG");
    }

    // ------------------------------------------------------------------ renaming a request

    [Fact]
    public async Task A_request_can_be_renamed_and_keeps_its_number_and_the_title_of_its_document_follows()
    {
        var user = await NewUserAsync("rename");
        var (quick, ward) = await NewProjectAsync(user);
        var a = await MakeAsync(user, quick.ProjectId, ward.Id, "Bed allocation", "NR");
        var b = await MakeAsync(user, quick.ProjectId, ward.Id, "Bed transfer", "NR");
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.GeneratedDocuments.Add(new GeneratedDocument { ProjectId = quick.ProjectId, Scope = DocumentScope.Guide, FlowId = a.Id, Title = a.MenuName, DocNumber = "IMPL-WARD-NR-001" });
            await db.SaveChangesAsync();
        }

        var renamed = await (await user.PutAsJsonAsync($"/api/v1/flows/{a.Id}", new RenameFlowRequest("  Bed allocation on admission  "), TestJson.Options)).ReadAsync<FlowDto>();
        renamed.MenuName.Should().Be("Bed allocation on admission", "spaces around the heading are dropped");
        renamed.RequestCode.Should().Be("NR-001", "the number does not change with the name");
        (await user.GetAsync<FlowDto>($"/api/v1/flows/{a.Id}")).MenuName.Should().Be("Bed allocation on admission");
        (await user.GetAsync<List<FlowDto>>($"/api/v1/projects/{quick.ProjectId}/flows")).Should().Contain(f => f.Id == a.Id && f.MenuName == "Bed allocation on admission");
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            (await db.GeneratedDocuments.AsNoTracking().SingleAsync(d => d.FlowId == a.Id && d.Scope == DocumentScope.Guide)).Title.Should().Be("Bed allocation on admission");
        }

        // the same heading again changes nothing and is not an error
        (await user.PutAsJsonAsync($"/api/v1/flows/{a.Id}", new RenameFlowRequest("Bed allocation on admission"), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.OK);

        // a heading another request of the module already has (in any letters) is refused and says why
        var clash = await user.PutAsJsonAsync($"/api/v1/flows/{b.Id}", new RenameFlowRequest("BED ALLOCATION ON ADMISSION"), TestJson.Options);
        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await clash.ProblemAsync()).Should().Contain("already has a request called");
        (await user.GetAsync<FlowDto>($"/api/v1/flows/{b.Id}")).MenuName.Should().Be("Bed transfer", "a refused rename changes nothing");

        // an empty heading, or one that is too long, is refused
        (await user.PutAsJsonAsync($"/api/v1/flows/{b.Id}", new RenameFlowRequest("   "), TestJson.Options)).StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
        (await user.PutAsJsonAsync($"/api/v1/flows/{b.Id}", new RenameFlowRequest(new string('x', 201)), TestJson.Options)).StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);

        // the name of a deleted request is free again
        (await user.DeleteAsync($"/api/v1/flows/{b.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.PutAsJsonAsync($"/api/v1/flows/{b.Id}", new RenameFlowRequest("Anything"), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.NotFound, "a deleted request cannot be opened or changed");
    }

    [Fact]
    public async Task Somebody_who_is_not_on_the_project_cannot_rename_its_requests()
    {
        var owner = await NewUserAsync("owner");
        var outsider = await NewUserAsync("outsider");
        var (quick, ward) = await NewProjectAsync(owner);
        var a = await MakeAsync(owner, quick.ProjectId, ward.Id, "Bed allocation", "NR");

        (await outsider.PutAsJsonAsync($"/api/v1/flows/{a.Id}", new RenameFlowRequest("Mine now"), TestJson.Options)).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        (await owner.GetAsync<FlowDto>($"/api/v1/flows/{a.Id}")).MenuName.Should().Be("Bed allocation");
        (await owner.PutAsJsonAsync($"/api/v1/flows/{Guid.NewGuid()}", new RenameFlowRequest("Nothing"), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ picking a module

    [Fact]
    public async Task The_master_list_offers_Ward_and_Nurse_Station_and_a_module_is_added_from_it_by_its_id()
    {
        var user = await NewUserAsync("modules");
        var (quick, _) = await NewProjectAsync(user);

        var master = await user.GetAsync<List<DepartmentDto>>("/api/v1/master/departments");
        master.Select(d => d.Name).Should().Contain(["Pharmacy", "Billing", "Ward", "Nurse Station"], "anybody signed in can read the list that the picker shows");
        var pharmacy = master.Single(d => d.Code == "PHARMACY");

        var added = await user.PostAsync<ProjectDepartmentDto>($"/api/v1/projects/{quick.ProjectId}/departments", new AddProjectDepartmentRequest(pharmacy.Id, null, null));
        added.Name.Should().Be("Pharmacy");
        added.ModuleCode.Should().Be("PHARMACY");
        added.IsCustom.Should().BeFalse("it came from the master list");

        var again = await user.PostAsJsonAsync($"/api/v1/projects/{quick.ProjectId}/departments", new AddProjectDepartmentRequest(pharmacy.Id, null, null), TestJson.Options);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict, "a project has each module once");
    }

    [Fact]
    public async Task Only_the_administrator_creates_modules_and_a_project_can_only_take_one_from_that_list()
    {
        var user = await NewUserAsync("onlyadmin");
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (quick, _) = await NewProjectAsync(user);
        var name = "Theatre " + Guid.NewGuid().ToString("N")[..6];
        var code = "TH" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // a user cannot make a module, not in the master list and not by typing a name into a project
        (await user.PostAsJsonAsync("/api/v1/master/departments", new UpsertDepartmentRequest(code, name), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var typed = await user.PostAsJsonAsync($"/api/v1/projects/{quick.ProjectId}/departments", new AddProjectDepartmentRequest(null, name, null), TestJson.Options);
        typed.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
        (await typed.ProblemAsync()).Should().Contain("Modules are created by the administrator");
        (await user.GetAsync<List<DepartmentDto>>("/api/v1/master/departments")).Should().NotContain(d => d.Name == name);

        // the administrator makes it, and then the user can take it
        var made = await admin.PostAsync<DepartmentDto>("/api/v1/master/departments", new UpsertDepartmentRequest(code, name));
        made.IsActive.Should().BeTrue();
        (await user.GetAsync<List<DepartmentDto>>("/api/v1/master/departments")).Should().Contain(d => d.Id == made.Id && d.Name == name);
        (await user.PostAsync<ProjectDepartmentDto>($"/api/v1/projects/{quick.ProjectId}/departments", new AddProjectDepartmentRequest(made.Id))).Name.Should().Be(name);

        // two modules never share a name (in any letters) or a code
        (await admin.PostAsJsonAsync("/api/v1/master/departments", new UpsertDepartmentRequest("X" + code[..8], name.ToUpperInvariant()), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync("/api/v1/master/departments", new UpsertDepartmentRequest(code, name + " 2"), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // a stopped module can no longer be taken by a project, and a module that does not exist cannot either
        var stopped = await admin.PostAsync<DepartmentDto>("/api/v1/master/departments", new UpsertDepartmentRequest("ST" + code[2..], name + " old"));
        await admin.PutAsync<DepartmentDto>($"/api/v1/master/departments/{stopped.Id}", new UpsertDepartmentRequest(stopped.Code, stopped.Name, false));
        var refused = await user.PostAsJsonAsync($"/api/v1/projects/{quick.ProjectId}/departments", new AddProjectDepartmentRequest(stopped.Id), TestJson.Options);
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await refused.ProblemAsync()).Should().Contain("module_not_available");
        (await user.PostAsJsonAsync($"/api/v1/projects/{quick.ProjectId}/departments", new AddProjectDepartmentRequest(Guid.NewGuid()), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // renaming a module changes what projects show, without touching their requests
        await admin.PutAsync<DepartmentDto>($"/api/v1/master/departments/{made.Id}", new UpsertDepartmentRequest(made.Code, name + " (main)"));
        (await user.GetAsync<List<ProjectDepartmentDto>>($"/api/v1/projects/{quick.ProjectId}/departments")).Should().Contain(m => m.ModuleCode == made.Code && m.Name == name + " (main)");
    }
}
