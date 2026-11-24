using System.Net;
using System.Net.Http.Json;
using FlowCapture.Domain;
using FlowCapture.Infrastructure.Maintenance;
using FlowCapture.Application.Abstractions;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>What keeps the system healthy without anybody watching: housekeeping, response headers and the audit trail.</summary>
[Collection("api")]
public class OperationsTests
{
    private readonly TestApp _app;

    public OperationsTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
    }

    private async Task<T> WithDbAsync<T>(Func<IAppDbContext, Task<T>> work)
    {
        using var scope = _app.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<IAppDbContext>());
    }

    // ------------------------------------------------------------------ housekeeping

    [Fact]
    public async Task Maintenance_frees_interrupted_jobs_and_leaves_running_ones_alone()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        var run = await w.AnalyseAsync(flow.Id);                                   // a normal finished run, must stay as it is
        run.Status.Should().Be("Succeeded");

        var (staleDoc, freshDoc) = await WithDbAsync(async db =>
        {
            DocumentJobRun Job() => new()
            {
                ProjectId = w.Project.Id, Scope = DocumentScope.Flow, FlowId = flow.Id, Status = DocJobStatus.Running,
                RequestedBy = Guid.NewGuid(), StartedAt = DateTime.UtcNow,
            };
            var stale = Job();
            var fresh = Job();
            db.DocumentJobRuns.AddRange(stale, fresh);
            await db.SaveChangesAsync();
            var longAgo = DateTime.UtcNow.AddHours(-3);
            await db.DocumentJobRuns.Where(j => j.Id == stale.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.UpdatedAt, longAgo));
            return (stale.Id, fresh.Id);
        });

        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var report = await admin.PostAsync<MaintenanceReport>("/api/v1/admin/maintenance/run");
        report.StuckDocuments.Should().Be(1);

        // the lead sees the stale job as failed with an instruction, the fresh one is untouched, the finished analysis is unchanged
        var failed = await w.Lead.GetAsync<JobDto>($"/api/v1/jobs/{staleDoc}");
        failed.State.Should().Be("Failed");
        failed.Message.Should().Contain("interrupted").And.Contain("Start it again");
        (await w.Lead.GetAsync<JobDto>($"/api/v1/jobs/{freshDoc}")).State.Should().Be("Running");
        (await w.Lead.GetAsync<AnalysisRunDto>($"/api/v1/analysis-runs/{run.Id}")).Status.Should().Be("Succeeded");

        // running it again does nothing more
        (await admin.PostAsync<MaintenanceReport>("/api/v1/admin/maintenance/run")).StuckDocuments.Should().Be(0);
    }

    [Fact]
    public async Task Maintenance_fails_interrupted_analyses_and_transcriptions_so_they_can_be_started_again()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        var entryId = await WithDbAsync(db => db.CaptureEntries.Where(e => e.FlowId == flow.Id).Select(e => e.Id).FirstAsync());

        (Guid analysisId, Guid transcriptId) = await WithDbAsync(async db =>
        {
            var analysis = new AnalysisRun
            {
                ProjectId = w.Project.Id, FlowId = flow.Id, Scope = AnalysisScope.Part, Mode = AnalysisMode.Initial,
                Status = AnalysisStatus.Running, RequestedBy = w.ExecId, StartedAt = DateTime.UtcNow,
            };
            var transcript = new Transcript { EntryId = entryId, Language = "en-IN", Status = TranscriptStatus.Processing };
            db.AnalysisRuns.Add(analysis);
            db.Transcripts.Add(transcript);
            await db.SaveChangesAsync();
            var longAgo = DateTime.UtcNow.AddHours(-3);
            await db.AnalysisRuns.Where(r => r.Id == analysis.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, longAgo));
            await db.Transcripts.Where(t => t.Id == transcript.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, longAgo));
            return (analysis.Id, transcript.Id);
        });

        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var report = await admin.PostAsync<MaintenanceReport>("/api/v1/admin/maintenance/run");
        report.StuckAnalyses.Should().Be(1);
        report.StuckTranscripts.Should().Be(1);

        var failed = await w.Lead.GetAsync<AnalysisRunDto>($"/api/v1/analysis-runs/{analysisId}");
        failed.Status.Should().Be("Failed");
        failed.ErrorMessage.Should().Contain("interrupted");
        (await WithDbAsync(db => db.Transcripts.AsNoTracking().Where(t => t.Id == transcriptId).Select(t => t.Status).FirstAsync())).Should().Be(TranscriptStatus.Failed);

        // the flow is free again: a new analysis is accepted
        (await w.AnalyseAsync(flow.Id)).Status.Should().Be("Succeeded");
    }

    [Fact]
    public async Task Maintenance_removes_expired_sign_in_tokens_and_old_stored_responses_but_keeps_current_ones()
    {
        var userId = Guid.NewGuid();
        var (oldToken, freshToken, oldKey, freshKey) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await WithDbAsync(async db =>
        {
            var user = await db.Users.FirstAsync();
            db.RefreshTokens.AddRange(
                new RefreshToken { Id = oldToken, UserId = user.Id, TokenHash = "old-" + oldToken, CreatedAt = DateTime.UtcNow.AddDays(-60), ExpiresAt = DateTime.UtcNow.AddDays(-30) },
                new RefreshToken { Id = freshToken, UserId = user.Id, TokenHash = "fresh-" + freshToken, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(30) });
            db.IdempotencyRecords.AddRange(
                new IdempotencyRecord { Key = oldKey, UserId = userId, Path = "POST /x", StatusCode = 200, CreatedAt = DateTime.UtcNow.AddDays(-20) },
                new IdempotencyRecord { Key = freshKey, UserId = userId, Path = "POST /x", StatusCode = 200, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        var report = await (await _app.LoginAsync(TestApp.AdminEmail)).PostAsync<MaintenanceReport>("/api/v1/admin/maintenance/run");
        report.RefreshTokensRemoved.Should().BeGreaterThanOrEqualTo(1);
        report.IdempotencyRecordsRemoved.Should().BeGreaterThanOrEqualTo(1);

        (await WithDbAsync(db => db.RefreshTokens.AnyAsync(t => t.Id == oldToken))).Should().BeFalse();
        (await WithDbAsync(db => db.RefreshTokens.AnyAsync(t => t.Id == freshToken))).Should().BeTrue();
        (await WithDbAsync(db => db.IdempotencyRecords.AnyAsync(r => r.Key == oldKey))).Should().BeFalse();
        (await WithDbAsync(db => db.IdempotencyRecords.AnyAsync(r => r.Key == freshKey))).Should().BeTrue();
    }

    [Fact]
    public async Task Only_an_administrator_can_run_maintenance()
    {
        var w = await World.CreateAsync(_app);
        (await w.Lead.PostAsJsonAsync("/api/v1/admin/maintenance/run", new { }, TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _app.Anonymous().PostAsJsonAsync("/api/v1/admin/maintenance/run", new { }, TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------ response headers

    [Fact]
    public async Task Responses_carry_security_headers_and_are_never_cached()
    {
        var w = await World.CreateAsync(_app);
        foreach (var res in new[] { await w.Lead.GetAsync("/api/v1/projects"), await _app.Anonymous().GetAsync("/api/v1/projects"), await _app.Anonymous().GetAsync("/health/live") })
        {
            res.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
            res.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
            res.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
            res.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("frame-ancestors 'none'");
            res.Headers.CacheControl!.NoStore.Should().BeTrue();
            res.Headers.Contains("X-Correlation-Id").Should().BeTrue();
        }
    }

    [Fact]
    public async Task Opening_the_api_address_in_a_browser_gives_a_friendly_answer_not_a_401()
    {
        var res = await _app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/");
        res.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Redirect);        // the redirect to /swagger in development, a status note elsewhere
    }

    // ------------------------------------------------------------------ audit trail

    [RequiresDocumentToolsFact]
    public async Task Everything_that_happens_to_a_document_shows_in_the_project_audit_trail()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        await w.AnalyseAsync(flow.Id);
        await w.Exec.PostAsync<FlowDto>($"/api/v1/flows/{flow.Id}/submit-review");
        await w.Lead.PostAsync<FlowDto>($"/api/v1/flows/{flow.Id}/confirm", new ConfirmFlowRequest(1));
        (await w.Lead.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/documents", new GenerateDocumentRequest(), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _app.WhenJobsDone();
        var doc = (await w.Lead.GetAsync<List<DocumentDto>>($"/api/v1/flows/{flow.Id}/documents")).Single();
        await w.Lead.PostAsync<DocumentDto>($"/api/v1/documents/{doc.Id}/versions/1/final", new MarkFinalRequest(null));            // the lead marks it final
        await w.Admin.PostAsync<DocumentDto>($"/api/v1/documents/{doc.Id}/versions/1/approve", new ApproveDocumentRequest(true, "ok"));  // the administrator verifies it
        (await w.Admin.GetAsync($"/api/v1/documents/{doc.Id}/versions/1/download?format=docx")).EnsureSuccessStatusCode();

        var items = new List<AuditLogDto>();
        for (var page = 1; ; page++)
        {
            var p = await w.Lead.GetAsync<PagedResult<AuditLogDto>>($"/api/v1/audit?projectId={w.Project.Id}&pageSize=100&page={page}");
            items.AddRange(p.Items);
            if (items.Count >= p.Total || p.Items.Count == 0) break;
        }
        var trail = new PagedResult<AuditLogDto>(items, 1, items.Count, items.Count);
        var seen = trail.Items.Select(a => (a.EntityType, a.Action)).ToHashSet();
        seen.Should().Contain(("GeneratedDocument", "Create"));
        seen.Should().Contain(("DocumentVersion", "Create"));
        seen.Should().Contain(("DocumentVersion", "Update"));                       // the approval
        seen.Should().Contain(("DocumentVersion", "Download"));
        seen.Should().Contain(("DocumentJobRun", "Create"));
        seen.Should().Contain(("FlowVersion", "Create"));
        seen.Should().Contain(("Flow", "Update"));                                  // the confirmation

        var approval = trail.Items.First(a => a.EntityType == "DocumentVersion" && a.Action == "Update" && a.NewValues!.Contains("Approved"));
        approval.UserName.Should().NotBeNull();
        approval.NewValues.Should().Contain("Approved").And.NotContain("ok");        // the note is free text: never copied into the trail
        trail.Items.Should().OnlyContain(a => a.At > DateTime.UtcNow.AddHours(-1) && a.CorrelationId != null || a.Action == "Create" || a.Action == "Update" || a.Action == "Download");

        // an executive cannot read the trail; another project's lead sees none of it
        (await w.Exec.GetAsync($"/api/v1/audit?projectId={w.Project.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var other = await World.CreateAsync(_app);
        (await other.Lead.GetAsync($"/api/v1/audit?projectId={w.Project.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
