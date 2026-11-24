using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using FlowCapture.Infrastructure.Documents;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Xunit;

namespace FlowCapture.IntegrationTests;

[Collection("api")]
public class DocumentTests
{
    private readonly TestApp _app;

    public DocumentTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<FlowDto> ConfirmedFlowAsync(World w, string menu = "IP Case Sheet Receive", ProjectDepartmentDto? dept = null)
    {
        var flow = await w.FlowWithInputAsync(menu, dept);
        (await w.AnalyseAsync(flow.Id)).Status.Should().Be("Succeeded");
        await w.Exec.PostAsync<FlowDto>($"/api/v1/flows/{flow.Id}/submit-review");
        return await w.Lead.PostAsync<FlowDto>($"/api/v1/flows/{flow.Id}/confirm", new ConfirmFlowRequest(1));
    }

    private async Task<JobDto> GenerateAsync(World w, Guid flowId, HttpClient? as_ = null)
    {
        var res = await (as_ ?? w.Lead).PostAsJsonAsync($"/api/v1/flows/{flowId}/documents", new GenerateDocumentRequest(), TestJson.Options);
        res.StatusCode.Should().Be(HttpStatusCode.Accepted, await res.ProblemAsync());
        res.Headers.Location!.ToString().Should().Contain("/api/v1/jobs/");
        var job = await res.ReadAsync<JobDto>();
        await _app.WhenJobsDone();
        return await w.Lead.GetAsync<JobDto>($"/api/v1/jobs/{job.JobId}");
    }

    private static async Task<byte[]> BytesAsync(HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await res.Content.ReadAsByteArrayAsync();
    }

    private static string DocxText(byte[] docx)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        return doc.MainDocumentPart!.Document!.Body!.InnerText;
    }

    // ------------------------------------------------------------------ the whole pipeline

    [RequiresDocumentToolsFact]
    public async Task A_confirmed_flow_becomes_a_numbered_word_and_pdf_document_with_its_diagram()
    {
        var w = await World.CreateAsync(_app);
        var flow = await ConfirmedFlowAsync(w);

        var job = await GenerateAsync(w, flow.Id);

        job.State.Should().Be("Succeeded", job.Message);
        (await w.Lead.GetAsync<FlowDto>($"/api/v1/flows/{flow.Id}")).Status.Should().Be("DocumentsGenerated");
        var docs = await w.Exec.GetAsync<List<DocumentDto>>($"/api/v1/flows/{flow.Id}/documents");   // any member can see it
        var doc = docs.Should().ContainSingle().Subject;
        doc.DocNumber.Should().Be("MRD-001");
        doc.Scope.Should().Be("Flow");
        doc.Versions.Should().ContainSingle().Which.Should().Match<DocumentVersionDto>(v => v.VersionNo == 1 && v.ApprovalStatus == "Pending" && v.SourceFlowVersionNos.SequenceEqual(new[] { 1 }));

        var res = await w.Exec.GetAsync($"/api/v1/documents/{doc.Id}/versions/1/download?format=docx");
        res.Content.Headers.ContentDisposition!.FileName.Should().Be("MRD-001-v1.docx");
        var docx = await res.Content.ReadAsByteArrayAsync();
        var text = DocxText(docx);
        text.Should().Contain("MRD-001").And.Contain("Default Department/Ward Selection in IP Case Sheet Receive Menu");
        text.Should().Contain("Functional Requirement Document").And.Contain("1. Purpose").And.Contain("Req. ID").And.Contain("REQ001")
            .And.Contain("Pre-conditions").And.Contain("12. Acceptance criteria and test scenarios").And.Contain("14. Sign-off");
        text.Should().NotContain("MRD-000");
        using (var open = WordprocessingDocument.Open(new MemoryStream(docx), false))
            open.MainDocumentPart!.ImageParts.Count().Should().BeGreaterThanOrEqualTo(2);              // logo + the flow diagram

        var pdf = await BytesAsync(w.Exec, $"/api/v1/documents/{doc.Id}/versions/1/download?format=pdf");
        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        pdf.Length.Should().BeGreaterThan(20_000);                                                       // the diagram makes it sizeable

        // the diagram files were stored and can be fetched
        var png = await BytesAsync(w.Exec, $"/api/v1/flows/{flow.Id}/diagram?format=png");
        png.Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47);
        XDocument.Parse(System.Text.Encoding.UTF8.GetString(await BytesAsync(w.Exec, $"/api/v1/flows/{flow.Id}/diagram?format=svg"))).Root!.Name.LocalName.Should().Be("svg");

        // a second flow in the same module continues the numbering; another module starts its own
        var second = await ConfirmedFlowAsync(w, "IP Case Sheet Send");
        var billing = await ConfirmedFlowAsync(w, "Final Bill", w.Billing);
        (await GenerateAsync(w, second.Id)).State.Should().Be("Succeeded");
        (await GenerateAsync(w, billing.Id)).State.Should().Be("Succeeded");
        var all = await w.Lead.GetAsync<List<DocumentDto>>($"/api/v1/projects/{w.Project.Id}/documents");
        all.Select(d => d.DocNumber).Should().BeEquivalentTo(["MRD-001", "MRD-002", "BILLING-001"]);
    }

    [RequiresDocumentToolsFact]
    public async Task Generating_again_adds_a_version_with_the_same_number_and_older_versions_stay_downloadable()
    {
        var w = await World.CreateAsync(_app);
        var flow = await ConfirmedFlowAsync(w);
        (await GenerateAsync(w, flow.Id)).State.Should().Be("Succeeded");
        var doc = (await w.Lead.GetAsync<List<DocumentDto>>($"/api/v1/flows/{flow.Id}/documents")).Single();
        var v1 = await BytesAsync(w.Lead, $"/api/v1/documents/{doc.Id}/versions/1/download");

        // an edit voids the confirmation; documents are refused until the lead confirms the new version
        var content = await w.Exec.GetAsync<FlowContentDto>($"/api/v1/flows/{flow.Id}/content");
        var node = System.Text.Json.Nodes.JsonNode.Parse(content.Content.GetRawText())!;
        node["purpose"] = "A completely different purpose written after the first document.";
        (await w.Exec.PutAsJsonAsync($"/api/v1/flows/{flow.Id}/content", new UpdateFlowContentRequest(1, System.Text.Json.JsonSerializer.SerializeToElement(node), null, "edit"), TestJson.Options)).EnsureSuccessStatusCode();
        (await w.Lead.GetAsync<FlowDto>($"/api/v1/flows/{flow.Id}")).Status.Should().Be("UnderReview");
        var refused = await w.Lead.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/documents", new GenerateDocumentRequest(), TestJson.Options);
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await refused.ProblemAsync()).Should().Contain("not_confirmed");
        (await BytesAsync(w.Lead, $"/api/v1/documents/{doc.Id}/versions/1/download")).Should().Equal(v1);   // the old one is untouched

        await w.Lead.PostAsync<FlowDto>($"/api/v1/flows/{flow.Id}/confirm", new ConfirmFlowRequest(2));
        (await GenerateAsync(w, flow.Id)).State.Should().Be("Succeeded");

        var after = (await w.Lead.GetAsync<List<DocumentDto>>($"/api/v1/flows/{flow.Id}/documents")).Single();
        after.DocNumber.Should().Be("MRD-001");
        after.CurrentVersionNo.Should().Be(2);
        after.Versions.Select(v => v.VersionNo).Should().Equal(2, 1);                                   // newest first
        after.Versions[0].SourceFlowVersionNos.Should().Equal(2);
        DocxText(await BytesAsync(w.Lead, $"/api/v1/documents/{doc.Id}/versions/2/download")).Should().Contain("A completely different purpose");
        DocxText(await BytesAsync(w.Lead, $"/api/v1/documents/{doc.Id}/versions/1/download")).Should().NotContain("A completely different purpose");
    }

    [Fact]
    public async Task Documents_need_a_confirmed_flow_and_only_the_lead_can_ask_for_them()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        await w.AnalyseAsync(flow.Id);

        var early = await w.Lead.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/documents", new GenerateDocumentRequest(), TestJson.Options);
        early.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await early.ProblemAsync()).Should().Contain("not_confirmed");

        await w.Exec.PostAsync<FlowDto>($"/api/v1/flows/{flow.Id}/submit-review");
        await w.Lead.PostAsync<FlowDto>($"/api/v1/flows/{flow.Id}/confirm", new ConfirmFlowRequest(1));
        (await w.Exec.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/documents", new GenerateDocumentRequest(), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Reviewer.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/documents", new GenerateDocumentRequest(), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Outsider.GetAsync($"/api/v1/flows/{flow.Id}/documents")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Lead.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/documents", new GenerateProjectDocumentRequest([Guid.NewGuid()]), TestJson.Options))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await w.Exec.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/documents", new GenerateProjectDocumentRequest(), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var none = await World.CreateAsync(_app);                                                       // a project with nothing confirmed
        (await none.Lead.PostAsJsonAsync($"/api/v1/projects/{none.Project.Id}/documents", new GenerateProjectDocumentRequest(), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [RequiresDocumentToolsFact]
    public async Task The_administrator_verifies_or_returns_a_final_version_once_and_every_download_is_audited()
    {
        var w = await World.CreateAsync(_app);
        var flow = await ConfirmedFlowAsync(w);
        (await GenerateAsync(w, flow.Id)).State.Should().Be("Succeeded");
        var doc = (await w.Lead.GetAsync<List<DocumentDto>>($"/api/v1/flows/{flow.Id}/documents")).Single();

        // a working copy is not for the administrator: it cannot be opened, listed or decided on
        (await w.Admin.PostAsJsonAsync($"/api/v1/documents/{doc.Id}/versions/1/approve", new ApproveDocumentRequest(true, null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Admin.GetAsync($"/api/v1/documents/{doc.Id}/versions/1/download?format=pdf")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Admin.GetAsync($"/api/v1/documents/{doc.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // the lead marks it final; only the administrator decides on it
        (await w.Lead.PostAsync<DocumentDto>($"/api/v1/documents/{doc.Id}/versions/1/final", new MarkFinalRequest("Ready for the team"))).Versions.Single().FinalizedAt.Should().NotBeNull();
        (await w.Exec.PostAsJsonAsync($"/api/v1/documents/{doc.Id}/versions/1/approve", new ApproveDocumentRequest(true, null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Reviewer.PostAsJsonAsync($"/api/v1/documents/{doc.Id}/versions/1/approve", new ApproveDocumentRequest(true, null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Lead.PostAsJsonAsync($"/api/v1/documents/{doc.Id}/versions/1/approve", new ApproveDocumentRequest(true, null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Admin.PostAsJsonAsync($"/api/v1/documents/{doc.Id}/versions/1/approve", new ApproveDocumentRequest(false, null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.BadRequest);  // a rejection needs a reason

        var approved = await w.Admin.PostAsync<DocumentDto>($"/api/v1/documents/{doc.Id}/versions/1/approve", new ApproveDocumentRequest(true, "Checked against the sample RGD."));
        approved.Versions.Single().Should().Match<DocumentVersionDto>(v => v.ApprovalStatus == "Approved" && v.ApprovedByName != null && v.ApprovalNote == "Checked against the sample RGD.");
        (await w.Admin.PostAsJsonAsync($"/api/v1/documents/{doc.Id}/versions/1/approve", new ApproveDocumentRequest(false, "Changed my mind"), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await BytesAsync(w.Admin, $"/api/v1/documents/{doc.Id}/versions/1/download?format=pdf");
        var audit = await w.Admin.GetAsync<PagedResult<AuditLogDto>>("/api/v1/audit?entityType=DocumentVersion");
        audit.Items.Should().Contain(a => a.Action == "Download" && a.UserName != null && a.NewValues!.Contains("MRD-001"));
        (await w.Lead.GetAsync($"/api/v1/documents/{doc.Id}/versions/9/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await w.Lead.GetAsync($"/api/v1/documents/{doc.Id}/versions/1/download?format=xlsx")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
    [RequiresDocumentToolsFact]
    public async Task The_project_document_has_a_chapter_per_confirmed_flow_the_accepted_hand_offs_and_a_combined_diagram()
    {
        var w = await World.CreateAsync(_app);
        var mrd = await ConfirmedFlowAsync(w, "IP Case Sheet Receive", w.Mrd);
        var billing = await ConfirmedFlowAsync(w, "Final Bill", w.Billing);

        // whole-project analysis proposes a hand-off (billing sorts first, so it is F1); the lead accepts it
        var started = await w.Lead.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/analyses", new StartProjectAnalysisRequest(null), TestJson.Options);
        started.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _app.WhenJobsDone();
        var link = (await w.Lead.GetAsync<List<CrossFlowLinkDto>>($"/api/v1/projects/{w.Project.Id}/cross-links")).Single();
        await w.Lead.PutAsync<CrossFlowLinkDto>($"/api/v1/cross-links/{link.Id}", new DecideLinkRequest("Accepted"));

        // the combined diagram can be drawn on its own
        (await w.Exec.GetAsync($"/api/v1/projects/{w.Project.Id}/diagram/combined")).StatusCode.Should().Be(HttpStatusCode.NotFound);   // not drawn yet
        (await w.Exec.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/diagram/combined", new { }, TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var info = await w.Lead.PostAsync<DiagramInfoDto>($"/api/v1/projects/{w.Project.Id}/diagram/combined");
        info.Kind.Should().Be("Combined");
        var svg = System.Text.Encoding.UTF8.GetString(await BytesAsync(w.Exec, $"/api/v1/projects/{w.Project.Id}/diagram/combined?format=svg"));
        svg.Should().Contain("Billing").And.Contain("IP Case Sheet Receive").And.Contain("Case sheet");
        (await BytesAsync(w.Exec, $"/api/v1/projects/{w.Project.Id}/diagram/combined?format=mmd")).Should().NotBeEmpty();

        var res = await w.Lead.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/documents", new GenerateProjectDocumentRequest(null, "Hospital Lead"), TestJson.Options);
        res.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var job = await res.ReadAsync<JobDto>();
        await _app.WhenJobsDone();
        (await w.Lead.GetAsync<JobDto>($"/api/v1/jobs/{job.JobId}")).State.Should().Be("Succeeded");

        var doc = (await w.Lead.GetAsync<List<DocumentDto>>($"/api/v1/projects/{w.Project.Id}/documents")).Single();
        doc.Scope.Should().Be("Project");
        doc.DocNumber.Should().Be($"PRJ-{w.Project.Code}-C01");
        doc.Versions.Single().SourceFlowVersionNos.Should().Equal(1, 1);
        var text = DocxText(await BytesAsync(w.Lead, $"/api/v1/documents/{doc.Id}/versions/1/download"));
        text.Should().Contain("Consolidated Requirement Gathering Document").And.Contain("Chapter 1.").And.Contain("Chapter 2.")
            .And.Contain("Hand-offs between departments").And.Contain("Case sheet").And.Contain("Hospital Lead").And.Contain("All modules");
        (await w.Lead.GetAsync<FlowDto>($"/api/v1/flows/{mrd.Id}")).Status.Should().Be("Confirmed");        // a project document does not change flow status
        (await w.Lead.GetAsync<FlowDto>($"/api/v1/flows/{billing.Id}")).Status.Should().Be("Confirmed");

        // a second run adds a version of the same document
        await w.Lead.PostAsJsonAsync($"/api/v1/projects/{w.Project.Id}/documents", new GenerateProjectDocumentRequest(), TestJson.Options);
        await _app.WhenJobsDone();
        (await w.Lead.GetAsync<List<DocumentDto>>($"/api/v1/projects/{w.Project.Id}/documents")).Single().CurrentVersionNo.Should().Be(2);
    }

    // ------------------------------------------------------------------ diagrams

    [Fact]
    public async Task The_mermaid_source_is_available_in_any_status_and_a_flow_without_analysis_has_no_diagram()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        var empty = await w.Exec.GetAsync($"/api/v1/flows/{flow.Id}/diagram?format=mmd");
        empty.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await empty.ProblemAsync()).Should().Contain("not_analysed");

        await w.AnalyseAsync(flow.Id);
        var res = await w.Reviewer.GetAsync($"/api/v1/flows/{flow.Id}/diagram?format=mmd");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        (await res.Content.ReadAsStringAsync()).Should().StartWith("flowchart TD").And.Contain("D1{");
        (await w.Outsider.GetAsync($"/api/v1/flows/{flow.Id}/diagram?format=mmd")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Exec.GetAsync($"/api/v1/flows/{flow.Id}/diagram?format=gif")).StatusCode.Should().BeOneOf(HttpStatusCode.UnprocessableEntity);
    }

    [RequiresBrowserFact]
    public async Task A_diagram_is_drawn_on_demand_cached_and_redrawn_when_asked()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        await w.AnalyseAsync(flow.Id);

        var first = await BytesAsync(w.Exec, $"/api/v1/flows/{flow.Id}/diagram?format=png&versionNo=1");
        first.Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47);
        var again = await BytesAsync(w.Exec, $"/api/v1/flows/{flow.Id}/diagram?format=png");
        again.Should().Equal(first);                                                                     // cached: the very same file

        (await w.Reviewer.PostAsJsonAsync($"/api/v1/flows/{flow.Id}/diagram/regenerate", new { }, TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var info = await w.Exec.PostAsync<DiagramInfoDto>($"/api/v1/flows/{flow.Id}/diagram/regenerate");
        info.Should().Match<DiagramInfoDto>(i => i.Kind == "Flow" && i.VersionNo == 1 && i.Width > 100 && i.Height > 100 && i.Sha256.Length == 64);
    }

    // ------------------------------------------------------------------ templates

    [Fact]
    public async Task An_administrator_uploads_checks_and_activates_word_templates()
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var before = await admin.GetAsync<List<DocumentTemplateDto>>("/api/v1/admin/document-templates");
        before.Should().ContainSingle(t => t.IsActive);                                                  // seeded on the first start

        // a Word file with no placeholders is refused with the reasons
        var bad = await UploadAsync(admin, new byte[] { 1, 2, 3 }, "Broken");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.ProblemAsync()).Should().Contain("could not be opened");

        var good = await UploadAsync(admin, RgdTemplateFactory.CreateDefault(), "Custom branding", "2.0");
        good.StatusCode.Should().Be(HttpStatusCode.Created, await good.ProblemAsync());
        var created = await good.ReadAsync<DocumentTemplateDto>();
        created.Should().Match<DocumentTemplateDto>(t => !t.IsActive && t.Version == "2.0" && t.Name == "Custom branding");

        var activated = await admin.PostAsync<DocumentTemplateDto>($"/api/v1/admin/document-templates/{created.Id}/activate");
        activated.IsActive.Should().BeTrue();
        var after = await admin.GetAsync<List<DocumentTemplateDto>>("/api/v1/admin/document-templates");
        after.Where(t => t.IsActive).Should().ContainSingle().Which.Id.Should().Be(created.Id);

        var w = await World.CreateAsync(_app);
        (await w.Lead.GetAsync("/api/v1/admin/document-templates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // put the seeded one back so other tests are unaffected
        await admin.PostAsync<DocumentTemplateDto>($"/api/v1/admin/document-templates/{before.Single(t => t.IsActive).Id}/activate");
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] file, string name, string? version = null)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        form.Add(content, "file", "template.docx");
        form.Add(new StringContent(name), "name");
        if (version is not null) form.Add(new StringContent(version), "version");
        return await client.PostAsync("/api/v1/admin/document-templates", form);
    }
}
