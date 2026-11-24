using System.Net;
using System.Net.Http.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Services;
using FlowCapture.Domain;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>Deleting a project, and the daily free-allowance limits of each AI.</summary>
[Collection("api")]
public class QuotaAndDeleteTests
{
    private readonly TestApp _app;

    public QuotaAndDeleteTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
    }

    private async Task<(HttpClient Client, string Email)> NewUserAsync(string name = "user")
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var email = $"{name}.{Guid.NewGuid().ToString("N")[..8]}@test.local";
        var made = await admin.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, "Test " + name, TestApp.Password, [AppRoles.Lead]));
        await admin.NewClientAsync(null, made.Id);        // every test user has a client of their own
        return (await _app.LoginAsync(email), email);
    }

    private async Task<QuickProjectDto> NewProjectAsync(HttpClient c, string name) =>
        await c.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest(name + " " + Guid.NewGuid().ToString("N")[..5], null));

    // ------------------------------------------------------------------ deleting a project

    [Fact]
    public async Task A_deleted_project_disappears_everywhere_and_cannot_be_opened_but_nothing_is_erased()
    {
        var (lead, _) = await NewUserAsync("deleter");
        var (colleague, _) = await NewUserAsync("colleague");
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var keep = await NewProjectAsync(lead, "Keep");
        var gone = await NewProjectAsync(lead, "Gone");
        var overview = (await admin.GetAsync<AdminOverviewDto>("/api/v1/admin/overview")).Projects;

        (await colleague.DeleteAsync($"/api/v1/projects/{gone.ProjectId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "only the project's own lead may delete it");

        (await lead.DeleteAsync($"/api/v1/projects/{gone.ProjectId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await lead.GetAsync<List<ProjectDto>>("/api/v1/projects")).Select(p => p.Id).Should().Contain(keep.ProjectId).And.NotContain(gone.ProjectId);
        (await admin.GetAsync<List<ProjectDto>>("/api/v1/projects")).Select(p => p.Id).Should().NotContain(gone.ProjectId);
        (await lead.GetAsync($"/api/v1/projects/{gone.ProjectId}/flows")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "nobody can open a deleted project, not even its lead");
        (await lead.GetAsync($"/api/v1/flows/{gone.FlowId}")).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        (await admin.GetAsync($"/api/v1/flows/{gone.FlowId}/entries")).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        (await admin.GetAsync<AdminOverviewDto>("/api/v1/admin/overview")).Projects.Should().Be(overview - 1);

        // the rows are still there, marked as deleted, so the project can be brought back
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var row = await db.Projects.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == gone.ProjectId);
        row.DeletedAt.Should().NotBeNull();
        (await db.Flows.AsNoTracking().AnyAsync(f => f.Id == gone.FlowId)).Should().BeTrue();

        // deleting it twice, or something that never existed, is "not found"
        (await lead.DeleteAsync($"/api/v1/projects/{gone.ProjectId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_administrator_can_delete_anybodys_project_and_the_AI_use_it_caused_stays_on_the_record()
    {
        var (lead, _) = await NewUserAsync("owner");
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var quick = await NewProjectAsync(lead, "Spent");
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.AiUsages.Add(new AiUsage { At = DateTime.UtcNow, ProjectId = quick.ProjectId, FlowId = quick.FlowId, Kind = AiUsageKind.Analysis, Provider = "Gemini", Model = "m-test-deleted", InputTokens = 900_000_000, OutputTokens = 100_000_000 });         // far more than any other test, so it is in the top of every list
            await db.SaveChangesAsync();
        }
        (await admin.DeleteAsync($"/api/v1/projects/{quick.ProjectId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var usage = await admin.GetAsync<AiUsageSummaryDto>("/api/v1/ai-usage");
        usage.ByModel!.Should().Contain(m => m.Model == "m-test-deleted" && m.TotalTokens == 1_000_000_000, "the tokens were really spent, so they stay in the totals");
        usage.ByProject.Should().Contain(p => p.Name == "Removed project" && p.TotalTokens >= 1_000_000_000);
        await RemoveUsageAsync("m-test-deleted");
    }

    // ------------------------------------------------------------------ deleting a request (NR or CR)

    [Fact]
    public async Task A_deleted_request_disappears_but_is_kept_its_name_is_freed_and_its_number_is_never_used_again()
    {
        var (lead, _) = await NewUserAsync("reqdel");
        var (outsider, _) = await NewUserAsync("reqout");
        var quick = await NewProjectAsync(lead, "Requests");
        var ward = await lead.AddModuleAsync(quick.ProjectId, "Ward");
        async Task<FlowDto> Make(string name, string kind) =>
            await (await lead.PostAsJsonAsync($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(ward.Id, name, kind), TestJson.Options)).ReadAsync<FlowDto>();

        var a = await Make("Bed allocation", "NR");
        var b = await Make("Bed transfer", "NR");
        var cr = await Make("Discharge print", "CR");
        (a.RequestCode, b.RequestCode, cr.RequestCode).Should().Be(("NR-001", "NR-002", "CR-001"));
        await lead.PostAsJsonAsync($"/api/v1/flows/{b.Id}/entries", new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", "The nurse moves the patient to another bed.", null, null, null), TestJson.Options);

        (await outsider.DeleteAsync($"/api/v1/flows/{b.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "somebody who is not on the project cannot delete its requests");
        (await lead.DeleteAsync($"/api/v1/flows/{b.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var listed = await lead.GetAsync<List<FlowDto>>($"/api/v1/projects/{quick.ProjectId}/flows");
        listed.Select(f => f.RequestCode).Should().Contain(["NR-001", "CR-001"]).And.NotContain("NR-002");
        (await lead.GetAsync($"/api/v1/flows/{b.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await lead.GetAsync($"/api/v1/flows/{b.Id}/entries")).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        (await lead.DeleteAsync($"/api/v1/flows/{b.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound, "it is already gone");
        var modules = await lead.GetAsync<List<ProjectDepartmentDto>>($"/api/v1/projects/{quick.ProjectId}/departments");
        modules.Single(m => m.Id == ward.Id).FlowCount.Should().Be(2, "the module counts only the requests that are left");

        // the same name can be used again, and gets a new number: NR-002 is never given out twice
        var again = await Make("Bed transfer", "NR");
        again.RequestCode.Should().Be("NR-003");
        again.Id.Should().NotBe(b.Id);

        // nothing was erased
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var row = await db.Flows.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == b.Id);
        row.DeletedAt.Should().NotBeNull();
        row.RequestNo.Should().Be(2);
        row.MenuName.Should().StartWith("Bed transfer").And.Contain("[deleted");
        (await db.CaptureEntries.AsNoTracking().CountAsync(e => e.FlowId == b.Id)).Should().Be(1, "its notes are still stored");
    }

    [Fact]
    public async Task Deleting_the_last_request_of_a_project_leaves_the_project_and_a_new_request_can_be_made()
    {
        var (lead, _) = await NewUserAsync("lastreq");
        var quick = await NewProjectAsync(lead, "Last");
        (await lead.DeleteAsync($"/api/v1/flows/{quick.FlowId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await lead.GetAsync<List<FlowDto>>($"/api/v1/projects/{quick.ProjectId}/flows")).Should().BeEmpty();
        (await lead.GetAsync<List<ProjectDto>>("/api/v1/projects")).Select(p => p.Id).Should().Contain(quick.ProjectId);
        var made = await (await lead.PostAsJsonAsync($"/api/v1/projects/{quick.ProjectId}/flows", new CreateFlowRequest(quick.DepartmentId, "Second try", "NR"), TestJson.Options)).ReadAsync<FlowDto>();
        made.RequestCode.Should().Be("NR-002");
    }

    [RequiresBrowserFact]
    public async Task The_document_of_a_deleted_request_leaves_the_library()
    {
        var (lead, _) = await NewUserAsync("docdel");
        var (colleague, _) = await NewUserAsync("reader");
        await (await _app.LoginAsync(TestApp.AdminEmail)).ShareClientAsync(lead, colleague);        // the same client, so the documents are shared
        var quick = await NewProjectAsync(lead, "Library");
        await lead.PostAsJsonAsync($"/api/v1/flows/{quick.FlowId}/entries", new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", "When the case sheet comes the user opens the receive menu and picks the ward. Priority is High.", null, null, null), TestJson.Options);
        await lead.PostAsJsonAsync($"/api/v1/flows/{quick.FlowId}/analyses", new StartAnalysisRequest("Initial", null), TestJson.Options);
        await _app.WhenJobsDone();
        (await lead.PostAsync($"/api/v1/flows/{quick.FlowId}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _app.WhenJobsDone();
        (await colleague.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Should().ContainSingle(d => d.ProjectId == quick.ProjectId);

        (await lead.DeleteAsync($"/api/v1/flows/{quick.FlowId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await colleague.GetAsync<List<LibraryDocumentDto>>("/api/v1/library")).Should().NotContain(d => d.ProjectId == quick.ProjectId);
    }
    // ------------------------------------------------------------------ the free allowance

    private async Task<AiAdminDto> SetLimitsAsync(HttpClient admin, Dictionary<string, long> limits) =>
        await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, Limits: limits));

    private async Task RemoveUsageAsync(string model)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.AiUsages.RemoveRange(await db.AiUsages.Where(u => u.Model == model).ToListAsync());
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_admin_sets_a_daily_limit_for_an_AI_sees_what_it_used_today_and_is_told_when_it_is_reached()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        const string model = "whisper-quota-test";
        try
        {
            var before = await SetLimitsAsync(admin, new() { ["Groq.Requests"] = 3, ["Groq.Tokens"] = 1000 });
            var groq = before.Quotas!.Single(q => q.Provider == "Groq");
            groq.LimitRequests.Should().Be(3);
            groq.LimitTokens.Should().Be(1000);
            groq.Reached.Should().BeFalse();
            before.Quotas!.Single(q => q.Provider == "Gemini").LimitRequests.Should().BeNull("no limit was set for Gemini");

            using (var scope = _app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
                for (var i = 0; i < 2; i++) db.AiUsages.Add(new AiUsage { At = DateTime.UtcNow, Kind = AiUsageKind.Voice, Provider = "Groq", Model = model, InputTokens = 0, OutputTokens = 200 });
                await db.SaveChangesAsync();
            }
            var two = (await admin.GetAsync<AiAdminDto>("/api/v1/admin/ai")).Quotas!.Single(q => q.Provider == "Groq");
            two.UsedRequests.Should().BeGreaterThanOrEqualTo(2);
            two.UsedTokens.Should().BeGreaterThanOrEqualTo(400);
            two.Reached.Should().BeFalse("2 of 3 requests, 400 of 1000 tokens");

            using (var scope = _app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
                db.AiUsages.Add(new AiUsage { At = DateTime.UtcNow, Kind = AiUsageKind.Voice, Provider = "Groq", Model = model, OutputTokens = 700 });
                await db.SaveChangesAsync();
            }
            var reached = (await admin.GetAsync<AiAdminDto>("/api/v1/admin/ai")).Quotas!.Single(q => q.Provider == "Groq");
            reached.Reached.Should().BeTrue();
            reached.Message.Should().Contain("Groq").And.Contain("daily free limit").And.Contain("renews");

            // the usage page shows the same, with each model's own totals, to the admin only
            var usage = await admin.GetAsync<AiUsageSummaryDto>("/api/v1/ai-usage");
            usage.Quotas!.Should().Contain(q => q.Provider == "Groq" && q.Reached);
            usage.ByModel!.Should().Contain(m => m.Model == model && m.Provider == "Groq" && m.Calls == 3 && m.OutputTokens == 1100);
            var (user, _) = await NewUserAsync("plain");
            (await user.GetAsync<AiUsageSummaryDto>("/api/v1/ai-usage")).Quotas.Should().BeEmpty("the allowances are an administrator's concern");

            // a limit can be removed again with 0
            (await SetLimitsAsync(admin, new() { ["Groq.Requests"] = 0, ["Groq.Tokens"] = 0 })).Quotas!.Single(q => q.Provider == "Groq").Reached.Should().BeFalse();
        }
        finally
        {
            await SetLimitsAsync(admin, new() { ["Groq.Requests"] = 0, ["Groq.Tokens"] = 0 });
            await RemoveUsageAsync(model);
        }
    }

    [Fact]
    public async Task Only_known_limits_with_sensible_numbers_are_accepted()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var bad = await admin.PutAsJsonAsync("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, Limits: new Dictionary<string, long> { ["Gemini.Requests"] = -5 }), TestJson.Options);
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await bad.ProblemAsync()).Should().Contain("invalid_limit");
        var unknown = await admin.PutAsJsonAsync("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, Limits: new Dictionary<string, long> { ["Claude.Requests"] = 5 }), TestJson.Options);
        unknown.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var (user, _) = await NewUserAsync("notadmin");
        (await user.PutAsJsonAsync("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, Limits: new Dictionary<string, long> { ["Gemini.Requests"] = 5 }), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_analysis_is_refused_at_once_when_Gemini_has_used_its_daily_limit_and_goes_ahead_again_when_the_limit_is_lifted()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (user, _) = await NewUserAsync("limited");
        var quick = await NewProjectAsync(user, "Limited");
        await user.PostAsJsonAsync($"/api/v1/flows/{quick.FlowId}/entries", new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", "The nurse opens the discharge menu and picks the ward.", null, null, null), TestJson.Options);
        var original = (await admin.GetAsync<AiAdminDto>("/api/v1/admin/ai")).Provider;
        const string model = "gemini-quota-test";
        try
        {
            await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest("Gemini", null, null));
            await SetLimitsAsync(admin, new() { ["Gemini.Requests"] = 1 });
            using (var scope = _app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
                db.AiUsages.Add(new AiUsage { At = DateTime.UtcNow, Kind = AiUsageKind.Analysis, Provider = "Gemini", Model = model, InputTokens = 10, OutputTokens = 10 });
                await db.SaveChangesAsync();
            }
            var calls = _app.Ai.Calls.Count;
            var refused = await user.PostAsJsonAsync($"/api/v1/flows/{quick.FlowId}/analyses", new StartAnalysisRequest("Initial", null), TestJson.Options);
            refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var body = await refused.ProblemAsync();
            body.Should().Contain("ai_quota_reached").And.Contain("Gemini").And.Contain("daily free limit");
            _app.Ai.Calls.Count.Should().Be(calls, "nothing was sent to the AI");

            await SetLimitsAsync(admin, new() { ["Gemini.Requests"] = 0 });
            (await user.PostAsJsonAsync($"/api/v1/flows/{quick.FlowId}/analyses", new StartAnalysisRequest("Initial", null), TestJson.Options)).IsSuccessStatusCode.Should().BeTrue();
            await _app.WhenJobsDone();
        }
        finally
        {
            await SetLimitsAsync(admin, new() { ["Gemini.Requests"] = 0, ["Gemini.Tokens"] = 0 });
            await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(original, null, null));
            await RemoveUsageAsync(model);
        }
    }

    // ------------------------------------------------------------------ the limits of each Gemini model

    private async Task AddAttemptsAsync(string model, int count, string outcome = "ok", int tokens = 100)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        for (var i = 0; i < count; i++) db.AiAttempts.Add(new AiAttempt { At = DateTime.UtcNow, Provider = "Gemini", Model = model, Outcome = outcome, InputTokens = tokens, OutputTokens = 0 });
        await db.SaveChangesAsync();
    }

    private async Task RemoveAttemptsAsync(params string[] models)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.AiAttempts.RemoveRange(await db.AiAttempts.Where(a => models.Contains(a.Model)).ToListAsync());
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_admin_sees_each_model_against_its_own_limits_and_can_change_them()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        const string mine = "gemini-test-limits-9";
        try
        {
            await AddAttemptsAsync("gemini-3.8-flash", 3);
            await AddAttemptsAsync(mine, 2);
            var shown = (await admin.GetAsync<AiAdminDto>("/api/v1/admin/ai")).ModelQuotas!;
            var flash = shown.Single(m => m.Model == "gemini-3.8-flash");
            (flash.PerDay, flash.PerMinute, flash.TokensPerMinute).Should().Be((20L, 5L, 250_000L), "the limits Google AI Studio showed are preset");
            flash.UsedToday.Should().BeGreaterThanOrEqualTo(3);
            flash.IsPreset.Should().BeTrue();
            flash.Name.Should().Be("Gemini 3.8 Flash");
            shown.Single(m => m.Model == "gemini-3.5-flash-lite").PerDay.Should().Be(500);
            shown.Single(m => m.Model == mine).PerDay.Should().BeNull("a model nobody has set a limit for is not limited by the app");

            // limits can be set per model, and 0 removes a limit (even a preset one)
            var saved = await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, ModelLimits: new Dictionary<string, long> { [mine + ".PerDay"] = 2, [mine + ".PerMinute"] = 3 }));
            var changed = saved.ModelQuotas!.Single(m => m.Model == mine);
            (changed.PerDay, changed.PerMinute, changed.TokensPerMinute, changed.Reached).Should().Be((2L, 3L, (long?)null, true), "2 requests used of 2 today");
            var removed = await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, ModelLimits: new Dictionary<string, long> { ["gemini-3.8-flash.PerMinute"] = 0 }));
            var flash2 = removed.ModelQuotas!.Single(m => m.Model == "gemini-3.8-flash");
            flash2.PerMinute.Should().BeNull("0 removes the limit");
            flash2.PerDay.Should().Be(20, "the others keep their values");

            var usage = await admin.GetAsync<AiUsageSummaryDto>("/api/v1/ai-usage");
            usage.ModelQuotas!.Should().Contain(m => m.Model == "gemini-3.8-flash");

            var bad = await admin.PutAsJsonAsync("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, ModelLimits: new Dictionary<string, long> { ["gemini-3.8-flash.Nonsense"] = 5 }), TestJson.Options);
            bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await admin.PutAsJsonAsync("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, ModelLimits: new Dictionary<string, long> { ["gemini-3.8-flash.PerDay"] = -1 }), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }
        finally
        {
            await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(null, null, null, ModelLimits: new Dictionary<string, long> { [mine + ".PerDay"] = 0, [mine + ".PerMinute"] = 0, ["gemini-3.8-flash.PerMinute"] = 5 }));
            await RemoveAttemptsAsync("gemini-3.8-flash", mine);
        }
    }

    [Fact]
    public async Task When_every_Gemini_model_has_used_its_requests_everybody_is_told_and_an_analysis_is_refused_at_once()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var (user, _) = await NewUserAsync("spent");
        var quick = await NewProjectAsync(user, "Spent models");
        await user.PostAsJsonAsync($"/api/v1/flows/{quick.FlowId}/entries", new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", "The nurse opens the discharge menu.", null, null, null), TestJson.Options);
        var original = (await admin.GetAsync<AiAdminDto>("/api/v1/admin/ai")).Provider;
        var presets = new[] { ("gemini-3.8-flash", 20), ("gemini-3.6-flash", 20), ("gemini-3.5-flash-lite", 500) };
        try
        {
            await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest("Gemini", null, null));
            // two of three models used up: some capacity is left, and the number is shown
            await AddAttemptsAsync("gemini-3.8-flash", 20); await AddAttemptsAsync("gemini-3.6-flash", 20);
            using (var scope = _app.Services.CreateScope())
            {
                var cap = await scope.ServiceProvider.GetRequiredService<IModelQuota>().GetCapacityAsync();
                cap.Reached.Should().BeFalse();
                cap.RequestsLeft.Should().BeInRange(1, 500, "only the Lite model has room");
            }
            // all three used up
            await AddAttemptsAsync("gemini-3.5-flash-lite", 500);
            using (var scope = _app.Services.CreateScope())
            {
                var cap = await scope.ServiceProvider.GetRequiredService<IModelQuota>().GetCapacityAsync();
                cap.Reached.Should().BeTrue();
                cap.RequestsLeft.Should().Be(0);
                cap.Message.Should().Contain("Gemini 3.8 Flash: 20 of 20 requests today").And.Contain("Gemini 3.5 Flash Lite: 500 of 500").And.Contain("renews in");
            }
            var calls = _app.Ai.Calls.Count;
            var refused = await user.PostAsJsonAsync($"/api/v1/flows/{quick.FlowId}/analyses", new StartAnalysisRequest("Initial", null), TestJson.Options);
            refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await refused.ProblemAsync()).Should().Contain("ai_quota_reached").And.Contain("Gemini 3.8 Flash");
            _app.Ai.Calls.Count.Should().Be(calls);
        }
        finally
        {
            await admin.PutAsync<AiAdminDto>("/api/v1/admin/ai", new SaveAiAdminRequest(original, null, null));
            await RemoveAttemptsAsync(presets.Select(p => p.Item1).ToArray());
        }
    }
}