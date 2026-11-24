using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FlowCapture.Shared.Dtos;
using FluentAssertions;
using Xunit;

namespace FlowCapture.IntegrationTests;

[Collection("api")]
public class CaptureTests
{
    private readonly TestApp _app;

    public CaptureTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
        _app.Stt.Fail = false;
        _app.Stt.Text = "The user opens the menu. UHID: AB123456 and phone 9876543210 should not appear.";
    }

    // ------------------------------------------------------------------ entries

    [Fact]
    public async Task Creating_the_same_entry_twice_is_idempotent_and_ref_numbers_are_stable()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.NewFlowAsync();
        var id = Guid.NewGuid();
        var body = new CreateEntryRequest(id, "Text", DateTime.UtcNow, "en-IN", "First note.", null, null, "MRD user");

        var first = await w.Exec.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/entries", body, TestJson.Options);
        var again = await w.Exec.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/entries", body, TestJson.Options);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.ReadAsync<EntryDto>()).RefNo.Should().Be(1);
        (await again.ReadAsync<EntryDto>()).RefNo.Should().Be(1);

        var second = await w.AddTextAsync(flow.Id, "Second note.");
        second.RefNo.Should().Be(2);
        (await w.Exec.GetAsync<List<EntryDto>>($"/api/v1/flows/{flow.Id}/entries")).Select(e => e.RefNo).Should().Equal(1, 2);

        // another user cannot take over an existing id
        var clash = await w.Lead.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/entries", body, TestJson.Options);
        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Entry_edits_use_optimistic_concurrency_and_only_the_owner_or_lead_may_edit()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.NewFlowAsync();
        var entry = await w.AddTextAsync(flow.Id, "Original text.");

        async Task<HttpResponseMessage> Put(HttpClient c, string? ifMatch, string text)
        {
            var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/entries/{entry.Id}")
            {
                Content = JsonContent.Create(new UpdateEntryRequest("en-IN", text, null, null, "MRD user"), options: TestJson.Options),
            };
            if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
            return await c.SendAsync(req);
        }

        var ok = await Put(w.Exec, entry.RowVersion, "Edited text.");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var edited = await ok.ReadAsync<EntryDto>();
        edited.RowVersion.Should().NotBe(entry.RowVersion);

        var stale = await Put(w.Exec, entry.RowVersion, "Edit from an old copy.");
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.ProblemAsync()).Should().Contain("Edited text.");

        (await Put(w.Lead, null, "Lead can edit.")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Put(w.Reviewer, null, "Reviewers cannot.")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Access_is_enforced_per_project_role_and_department_assignment()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.NewFlowAsync();

        (await w.Outsider.GetAsync($"/api/v1/projects/{w.Project.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Outsider.GetAsync($"/api/v1/flows/{flow.Id}/entries")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Reviewer.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/entries",
            new CreateEntryRequest(Guid.NewGuid(), "Text", DateTime.UtcNow, "en-IN", "x", null, null, null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Reviewer.GetAsync($"/api/v1/flows/{flow.Id}/entries")).StatusCode.Should().Be(HttpStatusCode.OK);   // read access

        // an executive who is not assigned to a department cannot create flows in it
        var master = await w.Lead.GetAsync<List<DepartmentDto>>("/api/v1/master/departments");
        var ot = await w.Lead.PostAsync<ProjectDepartmentDto>($"/api/v1/projects/{w.Project.Id}/departments",
            new AddProjectDepartmentRequest(master.First(d => d.Code == "OT").Id, null, null));
        (await w.Exec.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/flows", new CreateFlowRequest(ot.Id, "Theatre booking"), TestJson.Options))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Lead.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/flows", new CreateFlowRequest(ot.Id, "Theatre booking"), TestJson.Options))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // management actions
        (await w.Exec.PutAsJsonAsync($"/api/v1/projects/{w.Project.Id}/members", new SetMembersRequest([]), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Exec.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("X", "XX1", "", new DateOnly(2026, 1, 1), w.ExecId), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Lead.GetAsync("/api/v1/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Lead.GetAsync("/api/v1/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _app.Anonymous().GetAsync("/api/v1/projects")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------ audio and transcripts

    private static (byte[] Data, string Sha) Audio(int size)
    {
        var data = RandomNumberGenerator.GetBytes(size);
        return (data, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
    }

    private async Task<EntryDto> VoiceEntryAsync(World w, Guid flowId)
    {
        var res = await w.Exec.PostAsJsonAsync($"/api/v1/flows/{flowId}/entries",
            new CreateEntryRequest(Guid.NewGuid(), "Voice", DateTime.UtcNow, "ta-IN", null, null, null, "MRD supervisor"), TestJson.Options);
        return await res.ReadAsync<EntryDto>();
    }

    private static async Task<HttpResponseMessage> PutChunk(HttpClient c, Guid uploadId, int n, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await c.PutAsync($"/api/v1/audio-uploads/{uploadId}/chunks/{n}", content);
    }

    [Fact]
    public async Task A_recording_is_uploaded_in_chunks_transcribed_masked_and_analysed_without_identifiers()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.NewFlowAsync();
        var entry = await VoiceEntryAsync(w, flow.Id);
        var (data, sha) = Audio(20_000);

        var init = await w.Exec.PostAsync<AudioUploadDto>("/api/v1/audio-uploads", new InitAudioUploadRequest(entry.Id, data.Length, sha, "audio/wav", 90, 16 * 1024));
        init.TotalChunks.Should().Be(2);
        init.ReceivedChunks.Should().BeEmpty();

        // the upload can be resumed: chunk 1 first, then ask what is missing
        (await PutChunk(w.Exec, init.UploadId, 1, data[(16 * 1024)..])).EnsureSuccessStatusCode();
        var status = await w.Exec.GetAsync<AudioUploadDto>($"/api/v1/audio-uploads/{init.UploadId}");
        status.ReceivedChunks.Should().Equal(1);
        var early = await w.Exec.PostAsJsonAsync($"/api/v1/audio-uploads/{init.UploadId}/complete", new { }, TestJson.Options);
        early.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);          // chunk 0 is still missing

        (await PutChunk(w.Exec, init.UploadId, 0, data[..(16 * 1024)])).EnsureSuccessStatusCode();
        var complete = await w.Exec.PostAsJsonAsync($"/api/v1/audio-uploads/{init.UploadId}/complete", new { }, TestJson.Options);
        complete.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _app.WhenJobsDone();

        var transcript = await w.Exec.GetAsync<TranscriptDto>($"/api/v1/entries/{entry.Id}/transcript");
        transcript.Status.Should().Be("Ready");
        transcript.MaskedText.Should().Contain("[UHID]").And.Contain("[PHONE]").And.NotContain("AB123456").And.NotContain("9876543210");
        transcript.MaskedCount.Should().Be(2);
        transcript.AvgConfidence.Should().BeApproximately(0.93, 0.001);
        _app.Stt.Locales.Last().Should().StartWith("ta-IN").And.Contain("en-IN");

        // the stored audio round-trips and supports range requests for playback
        var played = await w.Exec.GetAsync($"/api/v1/entries/{entry.Id}/audio");
        (await played.Content.ReadAsByteArrayAsync()).Should().Equal(data);
        var range = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/entries/{entry.Id}/audio") { Headers = { Range = new RangeHeaderValue(0, 99) } };
        var part = await w.Exec.SendAsync(range);
        part.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await part.Content.ReadAsByteArrayAsync()).Should().Equal(data[..100]);

        // the user corrects the transcript; a name typed while correcting is masked again
        var edited = await w.Exec.PutAsync<TranscriptDto>($"/api/v1/entries/{entry.Id}/transcript",
            new SaveTranscriptRequest("The user opens the menu. The patient name is Ravi Kumar, call 9123456789."));
        edited.UserEditedText.Should().Contain("[PATIENT_NAME]").And.Contain("[PHONE]").And.NotContain("Ravi");

        // analysis uses the corrected, masked text and never the raw transcript
        var run = await w.AnalyseAsync(flow.Id);
        run.Status.Should().Be("Succeeded", run.ErrorMessage);
        var sent = _app.Ai.Calls.Last().UserMessage;
        sent.Should().Contain("kind=\"voice_transcript\"").And.Contain("language=\"ta-IN\"").And.Contain("[PATIENT_NAME]");
        sent.Should().NotContain("Ravi").And.NotContain("AB123456").And.NotContain("9876543210").And.NotContain("9123456789");
    }

    [Fact]
    public async Task Bad_chunks_missing_chunks_and_a_wrong_checksum_are_all_rejected_with_a_reason()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.NewFlowAsync();
        var entry = await VoiceEntryAsync(w, flow.Id);
        var (data, _) = Audio(20_000);

        var init = await w.Exec.PostAsync<AudioUploadDto>("/api/v1/audio-uploads",
            new InitAudioUploadRequest(entry.Id, data.Length, new string('a', 64), "audio/wav", 60, 16 * 1024));

        var shortChunk = await PutChunk(w.Exec, init.UploadId, 0, data[..1000]);
        shortChunk.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await shortChunk.ProblemAsync()).Should().Contain("chunk_length");
        (await PutChunk(w.Exec, init.UploadId, 5, data[..100])).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        (await PutChunk(w.Exec, init.UploadId, 0, data[..(16 * 1024)])).EnsureSuccessStatusCode();
        var missing = await w.Exec.PostAsJsonAsync($"/api/v1/audio-uploads/{init.UploadId}/complete", new { }, TestJson.Options);
        missing.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await missing.ProblemAsync()).Should().Contain("chunks_missing");

        (await PutChunk(w.Exec, init.UploadId, 1, data[(16 * 1024)..])).EnsureSuccessStatusCode();
        var corrupt = await w.Exec.PostAsJsonAsync($"/api/v1/audio-uploads/{init.UploadId}/complete", new { }, TestJson.Options);
        corrupt.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await corrupt.ProblemAsync()).Should().Contain("checksum_mismatch");

        // limits are checked when the upload starts
        var tooLong = await w.Exec.PostAsJsonAsync("/api/v1/audio-uploads",
            new InitAudioUploadRequest(entry.Id, data.Length, new string('b', 64), "audio/wav", 31 * 60, 16 * 1024), TestJson.Options);
        tooLong.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await tooLong.ProblemAsync()).Should().Contain("30 minutes");
    }

    [Fact]
    public async Task A_failed_transcription_is_visible_and_the_recording_can_be_transcribed_again()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.NewFlowAsync();
        var entry = await VoiceEntryAsync(w, flow.Id);
        var (data, sha) = Audio(5000);
        var init = await w.Exec.PostAsync<AudioUploadDto>("/api/v1/audio-uploads", new InitAudioUploadRequest(entry.Id, data.Length, sha, "audio/mp4", 10, 16 * 1024));
        (await PutChunk(w.Exec, init.UploadId, 0, data)).EnsureSuccessStatusCode();

        _app.Stt.Fail = true;
        await w.Exec.PostAsJsonAsync($"/api/v1/audio-uploads/{init.UploadId}/complete", new { }, TestJson.Options);
        await _app.WhenJobsDone();
        var failed = await w.Exec.GetAsync<TranscriptDto>($"/api/v1/entries/{entry.Id}/transcript");
        failed.Status.Should().Be("Failed");
        failed.Error.Should().Contain("speech service");

        // analysis refuses to run on an untranscribed recording and says why
        var blocked = await w.Exec.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/analyses", new StartAnalysisRequest("Initial", null), TestJson.Options);
        blocked.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await blocked.ProblemAsync()).Should().Contain("transcript_pending");

        _app.Stt.Fail = false;
        var retry = await w.Exec.PostAsJsonAsync($"/api/v1/entries/{entry.Id}/transcribe", new RetranscribeRequest("hi-IN"), TestJson.Options);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _app.WhenJobsDone();
        (await w.Exec.GetAsync<TranscriptDto>($"/api/v1/entries/{entry.Id}/transcript")).Status.Should().Be("Ready");
        _app.Stt.Locales.Last().First().Should().Be("hi-IN");
    }

    // ------------------------------------------------------------------ offline sync

    private static SyncOpDto Op(string type, Guid entityId, object payload, string? baseVersion = null) =>
        new(Guid.NewGuid(), type, entityId, baseVersion, JsonSerializer.SerializeToElement(payload, TestJson.Options));

    [Fact]
    public async Task Sync_push_applies_each_operation_once_reports_conflicts_and_survives_a_lost_response()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.NewFlowAsync();
        var entryId = Guid.NewGuid();
        var create = new SyncCreateEntryPayload(flow.Id, "Text", DateTime.UtcNow, "en-IN", "Typed offline in the ward.", null, null, "MRD user");

        var first = await w.Exec.PostAsync<SyncPushResponse>("/api/v1/sync/push", new SyncPushRequest("device-1", [Op("CreateEntry", entryId, create)]));
        first.Results.Single().Result.Should().Be("Applied");
        var version = first.Results.Single().NewRowVersion!;

        // the response was lost, so the device sends the same operation again
        var retry = await w.Exec.PostAsync<SyncPushResponse>("/api/v1/sync/push", new SyncPushRequest("device-1", [Op("CreateEntry", entryId, create)]));
        retry.Results.Single().Result.Should().Be("Duplicate");
        (await w.Exec.GetAsync<List<EntryDto>>($"/api/v1/flows/{flow.Id}/entries")).Should().ContainSingle();

        var update = new UpdateEntryRequest("en-IN", "Edited offline.", null, null, "MRD user");
        var stale = await w.Exec.PostAsync<SyncPushResponse>("/api/v1/sync/push", new SyncPushRequest("device-1", [Op("UpdateEntry", entryId, update, "not-the-current-version")]));
        stale.Results.Single().Result.Should().Be("Conflict");
        stale.Results.Single().ServerCopy.Should().NotBeNull();
        stale.Results.Single().ServerCopy!.Value.GetProperty("rawText").GetString().Should().Be("Typed offline in the ward.");

        var good = await w.Exec.PostAsync<SyncPushResponse>("/api/v1/sync/push", new SyncPushRequest("device-1", [Op("UpdateEntry", entryId, update, version)]));
        good.Results.Single().Result.Should().Be("Applied");
        var again = await w.Exec.PostAsync<SyncPushResponse>("/api/v1/sync/push", new SyncPushRequest("device-1", [Op("UpdateEntry", entryId, update, version)]));
        again.Results.Single().Result.Should().Be("Duplicate");    // same change arriving twice is not a conflict

        var batch = await w.Exec.PostAsync<SyncPushResponse>("/api/v1/sync/push", new SyncPushRequest("device-1", [
            Op("DeleteEntry", entryId, new { }),
            Op("DeleteEntry", entryId, new { }),
            Op("FlyToTheMoon", entryId, new { }),
            Op("CreateEntry", Guid.NewGuid(), create with { FlowId = Guid.NewGuid() }),
            Op("CreateEntry", Guid.NewGuid(), create with { Kind = "Text", RawText = "" }),
        ]));
        batch.Results.Select(r => r.Result).Should().Equal("Applied", "Duplicate", "Rejected", "Rejected", "Rejected");
        batch.Results[2].Reason.Should().Contain("Unknown operation");

        // a reviewer cannot push entries
        var denied = await w.Reviewer.PostAsync<SyncPushResponse>("/api/v1/sync/push", new SyncPushRequest("d", [Op("CreateEntry", Guid.NewGuid(), create)]));
        denied.Results.Single().Result.Should().Be("Rejected");
    }

    [Fact]
    public async Task Sync_pull_pages_through_changes_without_missing_any_and_only_returns_project_data_the_user_belongs_to()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.NewFlowAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 25; i++) created.Add((await w.AddTextAsync(flow.Id, $"Note {i}")).Id);

        var seen = new HashSet<Guid>();
        string? cursor = null;
        var pages = 0;
        bool more;
        do
        {
            var page = await w.Lead.GetAsync<SyncPullResponse>($"/api/v1/sync/pull?projectId={w.Project.Id}&pageSize=10" + (cursor is null ? "" : $"&since={cursor}"));
            foreach (var e in page.Entries) seen.Add(e.Id);
            page.Flows.Should().OnlyContain(f => f.ProjectId == w.Project.Id);
            cursor = page.NextCursor;
            more = page.HasMore;
            pages++;
        } while (more && pages < 20);

        seen.Should().BeEquivalentTo(created);
        pages.Should().BeGreaterThan(1);

        var future = DateTime.UtcNow.AddHours(1).Ticks;
        var none = await w.Lead.GetAsync<SyncPullResponse>($"/api/v1/sync/pull?projectId={w.Project.Id}&since={future}");
        none.Entries.Should().BeEmpty();
        none.HasMore.Should().BeFalse();

        (await w.Outsider.GetAsync($"/api/v1/sync/pull?projectId={w.Project.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var outsiderAll = await w.Outsider.GetAsync<SyncPullResponse>("/api/v1/sync/pull");
        outsiderAll.Entries.Should().NotContain(e => created.Contains(e.Id));
        (await w.Lead.GetAsync("/api/v1/sync/pull?since=abc")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ------------------------------------------------------------------ idempotency and audit

    [Fact]
    public async Task An_Idempotency_Key_replays_the_first_response_and_cannot_be_reused_for_another_request()
    {
        var w = await World.CreateAsync(_app);
        var key = Guid.NewGuid().ToString();
        HttpRequestMessage Req(string url, string menu)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new CreateFlowRequest(w.Mrd.Id, menu), options: TestJson.Options) };
            r.Headers.Add("Idempotency-Key", key);
            return r;
        }

        var first = await w.Exec.SendAsync(Req($"/api/v1/projects/{w.Project.Id}/flows", "Receive"));
        var second = await w.Exec.SendAsync(Req($"/api/v1/projects/{w.Project.Id}/flows", "Receive"));
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.GetValues("Idempotent-Replay").Should().Contain("true");
        (await second.ReadAsync<FlowDto>()).Id.Should().Be((await first.ReadAsync<FlowDto>()).Id);

        var other = await w.Exec.SendAsync(Req($"/api/v1/projects/{w.Project.Id}/analyses", "x"));
        other.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_audit_trail_records_who_changed_what_without_copying_free_text()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.NewFlowAsync();
        var entry = await w.AddTextAsync(flow.Id, "SECRET-PHRASE-123 patient story");
        await w.Exec.PutAsync<EntryDto>($"/api/v1/entries/{entry.Id}", new UpdateEntryRequest("en-IN", "SECRET-PHRASE-456 edited", null, null, "MRD user"));

        var page = await w.Admin.GetAsync<PagedResult<AuditLogDto>>($"/api/v1/audit?entityType=CaptureEntry&entityId={entry.Id}");
        page.Items.Select(a => a.Action).Should().Contain(["Create", "Update"]);
        page.Items.Should().OnlyContain(a => a.UserName != null && a.UserName.StartsWith("exec"));
        page.Items.Should().OnlyContain(a => a.CorrelationId != null);
        var all = string.Join(" ", page.Items.Select(a => a.OldValues + " " + a.NewValues));
        all.Should().Contain("[redacted").And.NotContain("SECRET-PHRASE");

        var project = await w.Lead.GetAsync<PagedResult<AuditLogDto>>($"/api/v1/audit?projectId={w.Project.Id}&pageSize=100");
        project.Items.Should().Contain(a => a.EntityType == "Project" || a.EntityType == "Flow" || a.EntityType == "ProjectMember");
        (await w.Exec.GetAsync($"/api/v1/audit?projectId={w.Project.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ platform

    [Fact]
    public async Task Login_refresh_rotation_and_token_reuse_detection_work()
    {
        var client = _app.Anonymous();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(TestApp.AdminEmail, "wrong-password"), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(TestApp.AdminEmail, TestApp.Password), TestJson.Options)).ReadAsync<TokenResponse>();
        login.User.Roles.Should().Contain("Admin");

        var rotated = await (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken), TestJson.Options)).ReadAsync<TokenResponse>();
        rotated.RefreshToken.Should().NotBe(login.RefreshToken);

        // presenting the old token again is treated as theft: every session of the user ends
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(rotated.RefreshToken), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_endpoints_work_and_errors_are_problem_json_with_a_trace_id()
    {
        var client = _app.Anonymous();
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var missing = await admin.GetAsync($"/api/v1/flows/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        missing.Headers.Contains("X-Correlation-Id").Should().BeTrue();
        (await missing.ProblemAsync()).Should().Contain("traceId");

        var invalid = await admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("not-an-email", "", "short", ["Nope"]), TestJson.Options);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalid.ProblemAsync()).Should().Contain("errors");
    }

    [Fact]
    public async Task Admin_can_manage_categories_settings_and_prompt_versions()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);

        var prompts = await admin.GetAsync<List<PromptTemplateDto>>("/api/v1/admin/prompts");
        prompts.Select(p => p.Key).Should().Contain(["part-analysis.system", "reanalysis.system", "whole-analysis.system", "schema.flow", "schema.project-analysis", "common-rules"]);
        prompts.Where(p => p.Key == "part-analysis.system").Should().ContainSingle(p => p.IsActive && p.Version == 2, "the newest version in the prompt folder is the active one");
        var shipped = prompts.Where(p => p.Key == "common-rules").Max(p => p.Version);

        var created = await admin.PostAsync<PromptTemplateDto>("/api/v1/admin/prompts", new CreatePromptRequest("common-rules", "# changed rules {{category_definitions}}", "test", true));
        created.Version.Should().Be(shipped + 1);
        created.IsActive.Should().BeTrue();
        (await admin.GetAsync<List<PromptTemplateDto>>("/api/v1/admin/prompts")).Where(p => p.Key == "common-rules" && p.IsActive).Should().ContainSingle().Which.Version.Should().Be(shipped + 1);
        // put the original back so other tests are not affected
        var original = (await admin.GetAsync<List<PromptTemplateDto>>("/api/v1/admin/prompts")).First(p => p.Key == "common-rules" && p.Version == shipped);
        await admin.PostAsync<PromptTemplateDto>($"/api/v1/admin/prompts/{original.Id}/activate");

        var badSchema = await admin.PostAsJsonAsync("/api/v1/admin/prompts", new CreatePromptRequest("schema.flow", "[1,2]", null), TestJson.Options);
        badSchema.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var bad = await admin.PutAsJsonAsync("/api/v1/settings", new List<SettingDto> { new("Ai.MaxTokens", "12") }, TestJson.Options);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var unknown = await admin.PutAsJsonAsync("/api/v1/settings", new List<SettingDto> { new("Nope.Key", "1") }, TestJson.Options);
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var saved = await admin.PutAsync<List<SettingDto>>("/api/v1/settings", new List<SettingDto> { new("Pii.NameDictionary", "Ramesh, Lakshmi") });
        saved.Should().Contain(s => s.Key == "Pii.NameDictionary");

        var cats = await admin.PutAsync<List<CategoryDto>>("/api/v1/master/requirement-categories", new List<CategoryDto>
        {
            new("CR", "Change Request", "a change to existing behaviour", true, 1),
            new("NR", "New Requirement", "a new capability", true, 2),
            new("ER", "Enhancement", "an improvement", true, 3),
            new("DR", "Data Request", "a request for data or a report", true, 4),
        });
        cats.Should().HaveCount(4);
        // restore the standard three
        await admin.PutAsync<List<CategoryDto>>("/api/v1/master/requirement-categories", cats.Where(c => c.Code != "DR").ToList());
    }
}

