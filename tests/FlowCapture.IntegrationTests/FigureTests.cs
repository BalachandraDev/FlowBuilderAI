using System.Net;
using System.Net.Http.Json;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>Pictures (screenshots, photos of forms) attached to a request: stored, captioned, linked to a step, ordered, removed, and never read by the AI.</summary>
[Collection("api")]
public class FigureTests
{
    private readonly TestApp _app;

    public FigureTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
    }

    // a 1x1 PNG, a JPEG header and a WebP header: only the first bytes matter to the server
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0];
    private static readonly byte[] Webp = [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P', (byte)'V', (byte)'P', (byte)'8', (byte)' '];

    private static MultipartFormDataContent Form(byte[] bytes, string fileName = "screen.png", string? caption = null, string? stepId = null, string claimedType = "image/png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(claimedType);
        form.Add(file, "File", fileName);
        if (caption is not null) form.Add(new StringContent(caption), "Caption");
        if (stepId is not null) form.Add(new StringContent(stepId), "StepId");
        return form;
    }

    private static async Task<HttpResponseMessage> Attach(HttpClient c, Guid flowId, MultipartFormDataContent form) => await c.PostAsync($"/api/v1/flows/{flowId}/figures", form);

    [Fact]
    public async Task A_picture_is_stored_listed_captioned_linked_to_a_step_reordered_and_removed()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();

        var first = await (await Attach(w.Exec, flow.Id, Form(Png, "advice.png", "  The advice   screen  ", "S1"))).ReadAsync<FigureDto>();
        first.Caption.Should().Be("The advice screen", "spaces are tidied");
        first.StepId.Should().Be("S1");
        first.ContentType.Should().Be("image/png");
        first.UploadedByName.Should().NotBeEmpty();
        var second = await (await Attach(w.Exec, flow.Id, Form(Jpeg, "bill.jpg", "The bill", null, "application/octet-stream"))).ReadAsync<FigureDto>();
        second.ContentType.Should().Be("image/jpeg", "the picture is recognised by its first bytes, not by what the sender says it is");
        var third = await (await Attach(w.Exec, flow.Id, Form(Webp, "form.webp"))).ReadAsync<FigureDto>();
        third.Caption.Should().BeEmpty();

        // everybody on the project sees them, in the order they were attached; the bytes come back unchanged
        var list = await w.Reviewer.GetAsync<List<FigureDto>>($"/api/v1/flows/{flow.Id}/figures");
        list.Select(f => f.Id).Should().Equal(first.Id, second.Id, third.Id);
        (await w.Reviewer.GetByteArrayAsync($"/api/v1/figures/{first.Id}/image")).Should().Equal(Png);

        var edited = await w.Exec.PutAsync<FigureDto>($"/api/v1/figures/{second.Id}", new UpdateFigureRequest("The final bill", "S3"));
        edited.Caption.Should().Be("The final bill");
        edited.StepId.Should().Be("S3");

        var moved = await w.Exec.PostAsync<List<FigureDto>>($"/api/v1/figures/{third.Id}/move?direction=-1");
        moved.Select(f => f.Id).Should().Equal(first.Id, third.Id, second.Id);

        (await w.Exec.DeleteAsync($"/api/v1/figures/{first.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await w.Exec.GetAsync<List<FigureDto>>($"/api/v1/flows/{flow.Id}/figures")).Select(f => f.Id).Should().Equal(third.Id, second.Id);
        (await w.Exec.GetAsync($"/api/v1/figures/{first.Id}/image")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _app.Ai.Calls.Should().BeEmpty("the AI never reads the pictures, so they cost no tokens");
    }

    [Fact]
    public async Task Only_pictures_are_accepted_and_only_a_reasonable_number_of_them()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();

        var page = await Attach(w.Exec, flow.Id, Form("<html><script>alert(1)</script></html>"u8.ToArray(), "page.png", null, null, "image/png"));
        page.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a page called a picture is not a picture");
        (await page.ProblemAsync()).Should().Contain("figure_type");
        (await Attach(w.Exec, flow.Id, Form([], "empty.png"))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "an empty file is not a picture");
        (await Attach(w.Exec, flow.Id, Form(new byte[9 * 1024 * 1024].Select((_, i) => i < 3 ? (byte)0xFF : (byte)0).ToArray(), "big.jpg"))).StatusCode.Should().BeOneOf(HttpStatusCode.UnprocessableEntity, HttpStatusCode.RequestEntityTooLarge);

        for (var i = 0; i < 20; i++) (await Attach(w.Exec, flow.Id, Form(Png, $"{i}.png"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var over = await Attach(w.Exec, flow.Id, Form(Png, "21.png"));
        over.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await over.ProblemAsync()).Should().Contain("figure_limit");
    }

    [Fact]
    public async Task Only_people_who_may_capture_for_the_module_can_change_the_pictures_and_outsiders_cannot_even_look()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        var made = await (await Attach(w.Exec, flow.Id, Form(Png, "a.png", "A"))).ReadAsync<FigureDto>();

        (await Attach(w.Reviewer, flow.Id, Form(Png))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Reviewer.PutAsJsonAsync($"/api/v1/figures/{made.Id}", new UpdateFigureRequest("x", null), TestJson.Options)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Reviewer.DeleteAsync($"/api/v1/figures/{made.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Reviewer.GetAsync($"/api/v1/flows/{flow.Id}/figures")).StatusCode.Should().Be(HttpStatusCode.OK, "a reviewer may look");

        (await w.Outsider.GetAsync($"/api/v1/flows/{flow.Id}/figures")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Outsider.GetAsync($"/api/v1/figures/{made.Id}/image")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await w.Outsider.DeleteAsync($"/api/v1/figures/{made.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [RequiresBrowserFact]
    public async Task The_pictures_are_printed_in_the_document_and_the_document_is_still_made_without_the_AI()
    {
        var w = await World.CreateAsync(_app);
        var flow = await w.FlowWithInputAsync();
        (await w.AnalyseAsync(flow.Id)).Status.Should().Be("Succeeded");
        await Attach(w.Exec, flow.Id, Form(Png, "advice.png", "The receive menu", "S1"));
        await Attach(w.Exec, flow.Id, Form(Jpeg, "list.jpg", "The printed list"));
        var aiCalls = _app.Ai.Calls.Count;

        (await w.Exec.PostAsync($"/api/v1/flows/{flow.Id}/guide", new { })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _app.WhenJobsDone();

        var docs = await w.Exec.GetAsync<List<DocumentDto>>($"/api/v1/flows/{flow.Id}/documents");
        var doc = docs.Should().ContainSingle(d => d.Scope == "Guide").Subject;
        var pdf = await w.Exec.GetByteArrayAsync($"/api/v1/documents/{doc.Id}/versions/{doc.CurrentVersionNo}/download?format=pdf");
        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        _app.Ai.Calls.Count.Should().Be(aiCalls, "making the document, pictures included, uses no AI tokens");
    }
}