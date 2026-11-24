using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlowCapture.Application.Abstractions;
using FluentAssertions;
using FlowCapture.Shared.Dtos;
using Xunit;

namespace FlowCapture.IntegrationTests;

[Collection("api")]
public class PipelineTests
{
    private readonly TestApp _app;

    public PipelineTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
        _app.Ai.InputTokens = 1200;
        _app.Ai.OutputTokens = 700;
    }

    private static JsonNode Node(FlowContentDto c) => JsonNode.Parse(c.Content.GetRawText())!;

    private static async Task<HttpResponseMessage> EditAsync(HttpClient client, Guid flowId, int baseVersion, Action<JsonObject> edit, string[]? locks = null)
    {
        var current = await client.GetAsync<FlowContentDto>($"/api/v1/flows/{flowId}/content");
        var node = (JsonObject)Node(current);
        edit(node);
        return await client.PutAsJsonAsync($"/api/v1/flows/{flowId}/content",
            new UpdateFlowContentRequest(baseVersion, JsonSerializer.SerializeToElement(node), locks, "test edit"), TestJson.Options);
    }

    // ------------------------------------------------------------------ analysis

    [Fact]
    public async Task A_first_analysis_creates_version_one_with_requirements_questions_and_usage()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();

        var run = await w.AnalyseAsync(flow.Id);

        run.Status.Should().Be("Succeeded");
        run.ResultVersionNo.Should().Be(1);
        run.InputTokens.Should().Be(1500);       // 1200 + 300 cached
        run.OutputTokens.Should().Be(700);
        run.Attempts.Should().Be(1);
        run.Warnings.Should().BeEmpty();

        var updated = await w.Exec.GetAsync<FlowDto>($"/api/v1/flows/{flow.Id}");
        updated.Status.Should().Be("Analysed");
        updated.CurrentVersionNo.Should().Be(1);
        updated.OpenQuestionCount.Should().Be(3);

        var content = await w.Exec.GetAsync<FlowContentDto>($"/api/v1/flows/{flow.Id}/content");
        Node(content)["requirements"]![0]!["reqId"]!.GetValue<string>().Should().Be("REQ001");
        Node(content)["department"]!.GetValue<string>().Should().Be("MRD");

        var questions = await w.Exec.GetAsync<List<ClarificationDto>>($"/api/v1/flows/{flow.Id}/clarifications");
        questions.Select(q => q.QuestionKey).Should().Equal("Q1", "Q2", "Q3");
        questions.Should().OnlyContain(q => q.Status == "Open");
        questions[0].Options.Should().NotBeNullOrEmpty();

        var usage = await w.Lead.GetAsync<AiUsageDto>($"/api/v1/usage/ai?projectId={w.Project.Id}");
        usage.Runs.Should().Be(1);
        usage.InputTokens.Should().Be(1500);
        usage.EstimatedCost.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_AI_only_ever_sees_masked_text_and_never_the_hospital_or_people_names()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.NewFlowAsync();
        await w.AddTextAsync(flow.Id, "The user opens the menu. Patient name is Ravi Kumar, UHID: AB123456, phone 9876543210.");

        var run = await w.AnalyseAsync(flow.Id);

        run.Status.Should().Be("Succeeded");
        var sent = _app.Ai.Calls.Last().UserMessage;
        sent.Should().Contain("[PATIENT_NAME]").And.Contain("[UHID]").And.Contain("[PHONE]");
        sent.Should().NotContain("Ravi").And.NotContain("AB123456").And.NotContain("9876543210");
        sent.Should().NotContain(w.Project.HospitalName);
        _app.Ai.Calls.Last().SystemPrompt.Should().Contain("flowchart TD").And.Contain("CR = Change Request");
    }

    [Fact]
    public async Task Invalid_AI_output_is_retried_once_with_the_errors_and_then_accepted()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        _app.Ai.Script.Enqueue(r =>
        {
            var n = JsonNode.Parse(FakeAi.FlowResult(r.UserMessage))!;
            n["steps"]![0]!["next"] = new JsonArray("S99");
            return n.ToJsonString();
        });

        var run = await w.AnalyseAsync(flow.Id);

        run.Status.Should().Be("Succeeded");
        run.Attempts.Should().Be(2);
        run.InputTokens.Should().Be(3000);       // both calls are counted
        var retry = _app.Ai.Calls.Last().UserMessage;
        retry.Should().Contain("S99").And.Contain("<previous_output>").And.Contain("did not pass validation");
    }

    [Fact]
    public async Task Two_invalid_answers_fail_the_run_without_creating_a_version_and_keep_the_raw_output()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        Func<AiCallRequest, string> bad = r =>
        {
            var n = JsonNode.Parse(FakeAi.FlowResult(r.UserMessage))!;
            n["steps"]![0]!["next"] = new JsonArray("S99");
            return n.ToJsonString();
        };
        _app.Ai.Script.Enqueue(r => bad(r));
        _app.Ai.Script.Enqueue(r => bad(r));

        var run = await w.AnalyseAsync(flow.Id);

        run.Status.Should().Be("Failed");
        run.ErrorCode.Should().Be("validation_failed");
        run.ErrorMessage.Should().Contain("S99");
        run.Attempts.Should().Be(2);
        (await w.Exec.GetAsync<FlowDto>($"/api/v1/flows/{flow.Id}")).CurrentVersionNo.Should().Be(0);
    }

    [Fact]
    public async Task An_input_that_the_AI_cannot_support_with_sources_is_rejected()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        _app.Ai.Script.Enqueue(r =>
        {
            var n = JsonNode.Parse(FakeAi.FlowResult(r.UserMessage))!;
            n["businessRules"]![0]!["sourceEntryIds"] = new JsonArray();     // an invented rule with no source
            return n.ToJsonString();
        });
        _app.Ai.Script.Enqueue(r =>
        {
            var n = JsonNode.Parse(FakeAi.FlowResult(r.UserMessage))!;
            n["businessRules"]![0]!["sourceEntryIds"] = new JsonArray();
            return n.ToJsonString();
        });

        var run = await w.AnalyseAsync(flow.Id);

        run.Status.Should().Be("Failed");
        run.ErrorMessage.Should().Contain("at least one source");
    }

    // ------------------------------------------------------------------ review loop

    [Fact]
    public async Task Analysing_again_with_nothing_new_is_refused_to_save_free_tokens_and_goes_ahead_when_something_is_added_or_forced()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        (await w.AnalyseAsync(flow.Id)).Status.Should().Be("Succeeded");
        var calls = _app.Ai.Calls.Count;

        var refused = await w.Exec.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/analyses", new StartAnalysisRequest("Reanalyse", null), TestJson.Options);
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await refused.ProblemAsync()).Should().Contain("nothing_new").And.Contain("free AI tokens");
        _app.Ai.Calls.Count.Should().Be(calls, "no AI call was made");

        // a new note is something new
        await Task.Delay(30);
        await w.AddTextAsync(flow.Id, "The user can also print the received list at the end of the day.");
        (await w.AnalyseAsync(flow.Id, "Reanalyse")).Status.Should().Be("Succeeded");
        _app.Ai.Calls.Count.Should().BeGreaterThan(calls);

        // and the person can always choose to analyse anyway
        var again = _app.Ai.Calls.Count;
        var forced = await w.Exec.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/analyses", new StartAnalysisRequest("Reanalyse", null, true), TestJson.Options);
        forced.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.Accepted);
        await _app.WhenJobsDone();
        _app.Ai.Calls.Count.Should().BeGreaterThan(again);
    }
    [Fact]
    public async Task Reanalysis_never_loses_locked_user_edits_even_if_the_model_overwrites_them()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        (await w.AnalyseAsync(flow.Id)).Status.Should().Be("Succeeded");

        // The executive edits the purpose and step S2 and locks both.
        var edit = await EditAsync(w.Exec, flow.Id, 1, n =>
        {
            n["purpose"] = "Purpose written by the executive.";
            n["steps"]![1]!["action"] = "System pre-selects the ward mapped to the logged in user.";
        }, ["purpose", "S2"]);
        edit.StatusCode.Should().Be(HttpStatusCode.OK, await edit.ProblemAsync());

        // The "model" ignores the lock: rewrites the purpose, rewrites S2 and deletes it from the flow.
        _app.Ai.Script.Enqueue(r =>
        {
            var previous = JsonNode.Parse(FakeAi.Between(r.UserMessage, "<previous_result>", "</previous_result>")!)!;
            previous["purpose"] = "Purpose rewritten by the AI.";
            var steps = previous["steps"]!.AsArray();
            steps.RemoveAt(1);
            previous["changeLog"] = new JsonArray();
            return previous.ToJsonString();
        });

        var run = await w.AnalyseAsync(flow.Id, "Reanalyse");

        run.Status.Should().Be("Succeeded", run.ErrorMessage);
        run.ResultVersionNo.Should().Be(3);
        run.Warnings.Should().Contain(x => x.Contains("purpose")).And.Contain(x => x.Contains("S2"));
        var request = _app.Ai.Calls.Last().UserMessage;
        request.Should().Contain("<locked_items>").And.Contain("\"S2\"").And.Contain("Purpose written by the executive.");

        var content = Node(await w.Exec.GetAsync<FlowContentDto>($"/api/v1/flows/{flow.Id}/content"));
        content["purpose"]!.GetValue<string>().Should().Be("Purpose written by the executive.");
        content["steps"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).Should().Contain("S2");
        content["steps"]![1]!["action"]!.GetValue<string>().Should().StartWith("System pre-selects the ward mapped");
    }

    [Fact]
    public async Task A_manual_edit_is_a_new_version_and_a_stale_edit_gets_a_conflict_with_the_current_content()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        await w.AnalyseAsync(flow.Id);

        var ok = await EditAsync(w.Exec, flow.Id, 1, n => n["scope"] = "Scope edited by hand.");
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.ProblemAsync());
        var v2 = await ok.ReadAsync<FlowContentDto>();
        v2.VersionNo.Should().Be(2);
        v2.Source.Should().Be("Manual");
        Node(v2)["changeLog"]![0]!["itemId"]!.GetValue<string>().Should().Be("scope");

        var stale = await EditAsync(w.Lead, flow.Id, 1, n => n["scope"] = "Someone else's edit.");
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.ProblemAsync()).Should().Contain("serverCopy").And.Contain("Scope edited by hand.");

        var diff = await w.Exec.GetAsync<FlowDiffDto>($"/api/v1/flows/{flow.Id}/versions/1/diff/2");
        diff.Items.Should().ContainSingle(d => d.Section == "scope" && d.Change == "Modified");

        var versions = await w.Exec.GetAsync<List<FlowVersionSummaryDto>>($"/api/v1/flows/{flow.Id}/versions");
        versions.Select(v => v.VersionNo).Should().Equal(2, 1);
    }

    [Fact]
    public async Task A_manual_edit_that_breaks_the_structure_is_rejected_with_the_reason()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        await w.AnalyseAsync(flow.Id);

        var bad = await EditAsync(w.Exec, flow.Id, 1, n => n["steps"]![0]!["next"] = new JsonArray("S42"));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.ProblemAsync()).Should().Contain("S42");

        var blank = await EditAsync(w.Exec, flow.Id, 1, n => n["purpose"] = "TBC");
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await w.Exec.GetAsync<FlowDto>($"/api/v1/flows/{flow.Id}")).CurrentVersionNo.Should().Be(1);
    }

    [Fact]
    public async Task Rolling_back_creates_a_new_version_with_the_old_content()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        await w.AnalyseAsync(flow.Id);
        var original = Node(await w.Exec.GetAsync<FlowContentDto>($"/api/v1/flows/{flow.Id}/content"))["scope"]!.GetValue<string>();
        (await EditAsync(w.Exec, flow.Id, 1, n => n["scope"] = "Temporary scope.")).EnsureSuccessStatusCode();

        (await w.Exec.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/versions/1/rollback", new { }, TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var rolled = await w.Lead.PostAsync<FlowContentDto>($"/api/v1/flows/{flow.Id}/versions/1/rollback");

        rolled.VersionNo.Should().Be(3);
        rolled.Source.Should().Be("Rollback");
        Node(rolled)["scope"]!.GetValue<string>().Should().Be(original);
    }

    [Fact]
    public async Task Answers_are_sent_back_on_reanalysis_and_an_answered_question_is_not_reopened()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        await w.AnalyseAsync(flow.Id);
        var q1 = (await w.Exec.GetAsync<List<ClarificationDto>>($"/api/v1/flows/{flow.Id}/clarifications")).First(q => q.QuestionKey == "Q1");

        var answered = await w.Exec.PutAsync<ClarificationDto>($"/api/v1/clarifications/{q1.Id}", new UpdateClarificationRequest("The first mapped ward", null, null, null));
        answered.Status.Should().Be("Answered");

        // the model resolves Q1 and drops it, together with its gap
        _app.Ai.Script.Enqueue(r =>
        {
            var n = JsonNode.Parse(FakeAi.Between(r.UserMessage, "<previous_result>", "</previous_result>")!)!;
            n["clarificationQuestions"]!.AsArray().RemoveAt(0);
            n["gaps"]!.AsArray().RemoveAt(0);
            return n.ToJsonString();
        });
        var run = await w.AnalyseAsync(flow.Id, "Reanalyse");

        run.Status.Should().Be("Succeeded", run.ErrorMessage);
        _app.Ai.Calls.Last().UserMessage.Should().Contain("<answer ref=\"A1\" question_id=\"Q1\">The first mapped ward</answer>");
        var after = await w.Exec.GetAsync<List<ClarificationDto>>($"/api/v1/flows/{flow.Id}/clarifications");
        after.First(q => q.QuestionKey == "Q1").Status.Should().Be("Answered");
        after.First(q => q.QuestionKey == "Q2").Status.Should().Be("Open");
    }

    [Fact]
    public async Task A_run_fails_instead_of_overwriting_when_the_flow_was_edited_while_the_AI_was_working()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        await w.AnalyseAsync(flow.Id);
        await Task.Delay(30);
        await w.AddTextAsync(flow.Id, "A further detail so that there is something new to analyse.");

        _app.Ai.Script.Enqueue(r =>
        {
            // somebody saves a manual edit while the model is still thinking
            EditAsync(w.Lead, flow.Id, 1, n => n["scope"] = "Edited during the analysis.").GetAwaiter().GetResult().EnsureSuccessStatusCode();
            return FakeAi.FlowResult(r.UserMessage);
        });
        var run = await w.AnalyseAsync(flow.Id, "Reanalyse");

        run.Status.Should().Be("Failed");
        run.ErrorCode.Should().Be("stale_base");
        var content = Node(await w.Exec.GetAsync<FlowContentDto>($"/api/v1/flows/{flow.Id}/content"));
        content["scope"]!.GetValue<string>().Should().Be("Edited during the analysis.");
    }

    // ------------------------------------------------------------------ lifecycle

    [Fact]
    public async Task The_lifecycle_is_enforced_and_only_the_lead_can_confirm()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        await w.AnalyseAsync(flow.Id);

        // Executives cannot confirm; the lead cannot confirm before review.
        (await w.Exec.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/confirm", new ConfirmFlowRequest(1), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var early = await w.Lead.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/confirm", new ConfirmFlowRequest(1), TestJson.Options);
        early.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await early.ProblemAsync()).Should().Contain("review");

        (await w.Exec.PostAsync<FlowDto>($"/api/v1/flows/{flow.Id}/submit-review")).Status.Should().Be("UnderReview");

        // A blocking question stops confirmation until it is answered.
        var q1 = (await w.Exec.GetAsync<List<ClarificationDto>>($"/api/v1/flows/{flow.Id}/clarifications")).First();
        (await w.Exec.PutAsJsonAsync($"/api/v1/clarifications/{q1.Id}", new UpdateClarificationRequest(null, null, null, true), TestJson.Options))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);                         // only the lead can mark blocking
        await w.Lead.PutAsync<ClarificationDto>($"/api/v1/clarifications/{q1.Id}", new UpdateClarificationRequest(null, null, null, true));
        var blocked = await w.Lead.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/confirm", new ConfirmFlowRequest(1), TestJson.Options);
        blocked.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await blocked.ProblemAsync()).Should().Contain("blocking");

        await w.Exec.PutAsync<ClarificationDto>($"/api/v1/clarifications/{q1.Id}", new UpdateClarificationRequest("Use the first mapped ward", null, null, null));
        var wrongVersion = await w.Lead.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/confirm", new ConfirmFlowRequest(7), TestJson.Options);
        wrongVersion.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var confirmed = await w.Lead.PostAsync<FlowDto>($"/api/v1/flows/{flow.Id}/confirm", new ConfirmFlowRequest(1));
        confirmed.Status.Should().Be("Confirmed");
        confirmed.ConfirmedVersionNo.Should().Be(1);
        (await w.Lead.GetAsync<List<FlowVersionSummaryDto>>($"/api/v1/flows/{flow.Id}/versions")).Single().IsConfirmed.Should().BeTrue();

        // Any edit after confirmation sends the flow back to review and clears the confirmation.
        (await EditAsync(w.Exec, flow.Id, 1, n => n["scope"] = "Changed after confirmation.")).EnsureSuccessStatusCode();
        var back = await w.Exec.GetAsync<FlowDto>($"/api/v1/flows/{flow.Id}");
        back.Status.Should().Be("UnderReview");
        back.ConfirmedVersionNo.Should().BeNull();
    }

    [Fact]
    public async Task A_reviewer_can_comment_but_cannot_change_the_flow_and_the_lead_resolves_comments()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        await w.AnalyseAsync(flow.Id);

        (await EditAsync(w.Reviewer, flow.Id, 1, n => n["scope"] = "x")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Reviewer.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/analyses", new StartAnalysisRequest("Reanalyse", null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var comment = await w.Reviewer.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/comments",
            new CreateCommentRequest(Guid.NewGuid(), "steps", "S2", null, "Should the system also log who changed the ward?"), TestJson.Options);
        var dto = await comment.ReadAsync<CommentDto>();
        dto.AuthorName.Should().StartWith("reviewer");

        (await w.Exec.PutAsJsonAsync($"/api/v1/comments/{dto.Id}/resolve", new ResolveCommentRequest(true), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Lead.PutAsync<CommentDto>($"/api/v1/comments/{dto.Id}/resolve", new ResolveCommentRequest(true))).IsResolved.Should().BeTrue();
        (await w.Exec.GetAsync<List<CommentDto>>($"/api/v1/flows/{flow.Id}/comments?section=steps")).Should().ContainSingle();
    }

    // ------------------------------------------------------------------ cost control

    [Fact]
    public async Task The_monthly_AI_limit_stops_new_runs_with_a_clear_429()
    {
        var w = await World.CreateAsync(_app, tokenLimit: 1000);
        var first = await w.FlowWithInputAsync("First flow");
        (await w.AnalyseAsync(first.Id)).Status.Should().Be("Succeeded");     // uses 2,200 tokens, over the limit

        var second = await w.FlowWithInputAsync("Second flow");
        var res = await w.Exec.PostAsJsonAsync($"/api/v1/flows/{second.Id}/analyses", new StartAnalysisRequest("Initial", null), TestJson.Options);

        res.StatusCode.Should().Be((HttpStatusCode)429);
        (await res.ProblemAsync()).Should().Contain("monthly AI limit").And.Contain("ai_budget_exceeded");
    }

    // ------------------------------------------------------------------ whole project

    [Fact]
    public async Task Whole_project_analysis_proposes_hand_offs_and_only_the_lead_can_accept_them()
    {
        var w = await World.CreateAsync(_app);
        var mrd = await w.FlowWithInputAsync("IP Case Sheet Receive", w.Mrd);
        var billing = await w.FlowWithInputAsync("Final Bill", w.Billing);
        (await w.AnalyseAsync(mrd.Id)).Status.Should().Be("Succeeded");
        (await w.AnalyseAsync(billing.Id)).Status.Should().Be("Succeeded");

        var started = await w.Exec.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/analyses", new StartProjectAnalysisRequest(null), TestJson.Options);
        started.StatusCode.Should().Be(HttpStatusCode.Forbidden);                // lead only

        var res = await w.Lead.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/analyses", new StartProjectAnalysisRequest(null), TestJson.Options);
        res.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var run = await res.ReadAsync<AnalysisRunDto>();
        await _app.WhenJobsDone();
        run = await w.Lead.GetAsync<AnalysisRunDto>($"/api/v1/analysis-runs/{run.Id}");
        run.Status.Should().Be("Succeeded", run.ErrorMessage);
        run.Scope.Should().Be("Whole");
        run.Result.Should().NotBeNull();

        var links = await w.Lead.GetAsync<List<CrossFlowLinkDto>>($"/api/v1/projects/{w.Project.Id}/cross-links?status=Proposed");
        var link = links.Should().ContainSingle().Subject;
        link.FromItemId.Should().Be("S5");
        link.ToItemId.Should().Be("S1");

        (await w.Exec.PutAsJsonAsync($"/api/v1/cross-links/{link.Id}", new DecideLinkRequest("Accepted"), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Lead.PutAsync<CrossFlowLinkDto>($"/api/v1/cross-links/{link.Id}", new DecideLinkRequest("Accepted"))).Status.Should().Be("Accepted");

        // the conflict's suggested question became an open cross-flow question on the first flow (Billing sorts before MRD)
        var questions = await w.Lead.GetAsync<List<ClarificationDto>>($"/api/v1/flows/{billing.Id}/clarifications");
        questions.Should().Contain(q => q.QuestionKey == "X1" && q.Question.Contains("ward pre-selection"));

        // a second run with the same input does not create a duplicate link
        await w.Lead.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/analyses", new StartProjectAnalysisRequest(null), TestJson.Options);
        await _app.WhenJobsDone();
        (await w.Lead.GetAsync<List<CrossFlowLinkDto>>($"/api/v1/projects/{w.Project.Id}/cross-links")).Should().HaveCount(1);
    }

    [Fact]
    public async Task Whole_project_analysis_needs_at_least_two_analysed_flows()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        await w.AnalyseAsync(flow.Id);

        var res = await w.Lead.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/analyses", new StartProjectAnalysisRequest(null), TestJson.Options);
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.ProblemAsync()).Should().Contain("at least two");
    }

    // ------------------------------------------------------------------ dashboard

    [Fact]
    public async Task The_dashboard_and_pending_list_reflect_the_state_of_the_project()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();

        var before = await w.Lead.GetAsync<DashboardDto>($"/api/v1/projects/{w.Project.Id}/dashboard");
        before.DepartmentsTotal.Should().Be(2);
        before.Captured.Should().Be(1);
        before.Analysed.Should().Be(0);
        (await w.Lead.GetAsync<List<PendingItemDto>>($"/api/v1/projects/{w.Project.Id}/pending")).Should().Contain(p => p.Kind == "UnanalysedEntries");

        await w.AnalyseAsync(flow.Id);
        var after = await w.Lead.GetAsync<DashboardDto>($"/api/v1/projects/{w.Project.Id}/dashboard");
        after.Analysed.Should().Be(1);
        after.Confirmed.Should().Be(0);
        after.OpenClarifications.Should().Be(3);
        after.AiUsage.Runs.Should().Be(1);
        after.Departments.Single(d => d.ModuleCode == "MRD").Flows.Should().Be(1);
        var pending = await w.Lead.GetAsync<List<PendingItemDto>>($"/api/v1/projects/{w.Project.Id}/pending");
        pending.Should().NotContain(p => p.Kind == "UnanalysedEntries").And.Contain(p => p.Kind == "OpenQuestions");

        await w.AddTextAsync(flow.Id, "One more detail from the next visit.");
        (await w.Lead.GetAsync<List<PendingItemDto>>($"/api/v1/projects/{w.Project.Id}/pending")).Should().Contain(p => p.Kind == "UnanalysedEntries");

        (await w.Exec.GetAsync($"/api/v1/projects/{w.Project.Id}/pending")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

