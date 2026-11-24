using System.Net.Http.Json;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;

namespace FlowCapture.TestSupport;

/// <summary>A project with a lead, an assigned executive, a reviewer and an outsider, created through the real API.</summary>
public sealed class World
{
    public required TestApp App { get; init; }
    public required HttpClient Admin { get; init; }
    public required HttpClient Lead { get; init; }
    public required HttpClient Exec { get; init; }
    public required HttpClient Reviewer { get; init; }
    public required HttpClient Outsider { get; init; }
    public required ProjectDto Project { get; init; }
    public required ProjectDepartmentDto Mrd { get; init; }
    public required ProjectDepartmentDto Billing { get; init; }
    public required Guid ExecId { get; init; }
    public required string ExecEmail { get; init; }
    public required string LeadEmail { get; init; }
    public required string ReviewerEmail { get; init; }

    public static async Task<World> CreateAsync(TestApp app, long? tokenLimit = null)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var admin = await app.LoginAsync(TestApp.AdminEmail);

        async Task<(Guid Id, string Email)> NewUser(string role, string name)
        {
            var email = $"{name}.{tag}@test.local";
            var u = await admin.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, $"{name} {tag}", TestApp.Password, [role]));
            return (u.Id, email);
        }

        var lead = await NewUser(AppRoles.Lead, "lead");
        var exec = await NewUser(AppRoles.Executive, "exec");
        var reviewer = await NewUser(AppRoles.Reviewer, "reviewer");
        var outsider = await NewUser(AppRoles.Executive, "outsider");

        var leadClient = await app.LoginAsync(lead.Email);
        var project = await leadClient.PostAsync<ProjectDto>("/api/v1/projects",
            new CreateProjectRequest($"Test Hospital {tag}", $"T{tag}", "Chennai", new DateOnly(2026, 11, 1), lead.Id));

        var master = await leadClient.GetAsync<List<DepartmentDto>>("/api/v1/master/departments");
        var mrd = await leadClient.PostAsync<ProjectDepartmentDto>($"/api/v1/projects/{project.Id}/departments",
            new AddProjectDepartmentRequest(master.First(d => d.Code == "MRD").Id, null, null));
        var billing = await leadClient.PostAsync<ProjectDepartmentDto>($"/api/v1/projects/{project.Id}/departments",
            new AddProjectDepartmentRequest(master.First(d => d.Code == "BILLING").Id, null, null));

        await leadClient.PutAsync<List<ProjectMemberDto>>($"/api/v1/projects/{project.Id}/members", new SetMembersRequest([
            new MemberAssignment(lead.Id, "Lead"), new MemberAssignment(exec.Id, "Executive"), new MemberAssignment(reviewer.Id, "Reviewer")]));
        await leadClient.PutAsync<ProjectDepartmentDto>($"/api/v1/projects/{project.Id}/departments/{mrd.Id}/assignments", new SetAssignmentsRequest([exec.Id]));
        await leadClient.PutAsync<ProjectDepartmentDto>($"/api/v1/projects/{project.Id}/departments/{billing.Id}/assignments", new SetAssignmentsRequest([exec.Id]));

        if (tokenLimit is not null)
        {
            var current = await leadClient.GetAsync<ProjectDto>($"/api/v1/projects/{project.Id}");
            project = await leadClient.PutAsync<ProjectDto>($"/api/v1/projects/{project.Id}",
                new UpdateProjectRequest(current.HospitalName, current.Location, current.StartDate, current.LeadUserId, current.Status, tokenLimit));
        }

        return new World
        {
            App = app, Admin = admin, Lead = leadClient, Exec = await app.LoginAsync(exec.Email), Reviewer = await app.LoginAsync(reviewer.Email),
            Outsider = await app.LoginAsync(outsider.Email), Project = project, Mrd = mrd, Billing = billing, ExecId = exec.Id,
            ExecEmail = exec.Email, LeadEmail = lead.Email, ReviewerEmail = reviewer.Email,
        };
    }

    public Task<FlowDto> NewFlowAsync(string menu = "IP Case Sheet Receive", ProjectDepartmentDto? dept = null) =>
        Exec.PostAsync<FlowDto>($"/api/v1/projects/{Project.Id}/flows", new CreateFlowRequest((dept ?? Mrd).Id, menu));

    public async Task<EntryDto> AddTextAsync(Guid flowId, string text, HttpClient? as_ = null)
    {
        var res = await (as_ ?? Exec).PostAsJsonAsync($"/api/v1/flows/{flowId}/entries",
            new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", text, null, null, "MRD user"), TestJson.Options);
        return await res.ReadAsync<EntryDto>();
    }

    /// <summary>A flow with two text entries (so the example's E1 and E2 references are valid).</summary>
    public async Task<FlowDto> FlowWithInputAsync(string menu = "IP Case Sheet Receive", ProjectDepartmentDto? dept = null)
    {
        var flow = await NewFlowAsync(menu, dept);
        await AddTextAsync(flow.Id, "When the case sheet comes to the department the user opens the receive menu and picks the ward. A super user gets his own ward pre-selected.");
        await AddTextAsync(flow.Id, "This is a change to existing behaviour. Priority is High. All MRD users use this menu frequently.");
        return flow;
    }

    public async Task<AnalysisRunDto> AnalyseAsync(Guid flowId, string mode = "Initial", HttpClient? as_ = null)
    {
        var res = await (as_ ?? Exec).PostAsJsonAsync($"/api/v1/flows/{flowId}/analyses", new StartAnalysisRequest(mode, null), TestJson.Options);
        var run = await res.ReadAsync<AnalysisRunDto>();
        await App.WhenJobsDone();
        return await Lead.GetAsync<AnalysisRunDto>($"/api/v1/analysis-runs/{run.Id}");
    }
}
