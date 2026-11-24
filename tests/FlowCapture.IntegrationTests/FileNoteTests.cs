using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FlowCapture.Shared;
using FlowCapture.Shared.Dtos;
using FlowCapture.TestSupport;
using FluentAssertions;
using Xunit;

namespace FlowCapture.IntegrationTests;

/// <summary>An Excel workbook or a PDF attached as a note: the server reads the text out of it, with no AI.</summary>
[Collection("api")]
public class FileNoteTests
{
    private readonly TestApp _app;

    public FileNoteTests(TestApp app)
    {
        _app = app;
        _app.Ai.Reset();
    }

    private async Task<(HttpClient Client, QuickProjectDto Project)> NewProjectAsync(string name)
    {
        var admin = await _app.LoginAsync(TestApp.AdminEmail);
        var email = $"{name}.{Guid.NewGuid().ToString("N")[..8]}@test.local";
        var made = await admin.PostAsync<UserDto>("/api/v1/users", new CreateUserRequest(email, "Test " + name, TestApp.Password, [AppRoles.Lead]));
        await admin.NewClientAsync(null, made.Id);
        var client = await _app.LoginAsync(email);
        return (client, await client.PostAsync<QuickProjectDto>("/api/v1/projects/quick", new QuickProjectRequest(name + " " + Guid.NewGuid().ToString("N")[..5], null)));
    }

    private static byte[] Pdf(string text)
    {
        var content = $"BT /F1 18 Tf 20 100 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<</Type/Catalog/Pages 2 0 R>>", "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 400 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            $"<</Length {content.Length}>>\nstream\n{content}\nendstream", "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<</Size {objects.Length + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient c, Guid flowId, string fileName, byte[] bytes, string type)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "File", fileName);
        form.Add(new StringContent("en-IN"), "Language");
        return c.PostAsync($"/api/v1/flows/{flowId}/files", form);
    }

    [Fact]
    public async Task A_PDF_with_text_becomes_a_typed_note_that_names_the_file_and_uses_no_AI()
    {
        var (user, project) = await NewProjectAsync("filer");
        var res = await SendAsync(user, project.FlowId, "Ward flow.pdf", Pdf("Beds are allocated at admission"), "application/pdf");
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.ProblemAsync());
        var entry = await res.ReadAsync<EntryDto>();
        entry.Kind.Should().Be("Text");
        entry.RawText.Should().Contain("Beds are allocated at admission");
        entry.Notes.Should().Be("From the file Ward flow.pdf");

        var notes = await user.GetAsync<List<EntryDto>>($"/api/v1/flows/{project.FlowId}/entries");
        notes.Should().Contain(e => e.Id == entry.Id);
        _app.Ai.Calls.Should().BeEmpty("the text is read on the server, not by the AI");
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_says_why_and_other_people_cannot_add_to_the_request()
    {
        var (user, project) = await NewProjectAsync("filer2");
        var (outsider, _) = await NewProjectAsync("filer3");

        var broken = await SendAsync(user, project.FlowId, "broken.xlsx", Encoding.ASCII.GetBytes("not a workbook"), "application/octet-stream");
        broken.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await broken.ProblemAsync()).Should().Contain("could not be opened");

        var old = await SendAsync(user, project.FlowId, "old.xls", [1, 2, 3], "application/vnd.ms-excel");
        old.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await old.ProblemAsync()).Should().Contain(".xlsx");

        var scan = await SendAsync(user, project.FlowId, "scan.pdf", Pdf(""), "application/pdf");
        scan.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await scan.ProblemAsync()).Should().Contain("scan");

        var word = await SendAsync(user, project.FlowId, "notes.docx", [1, 2, 3], "application/octet-stream");
        word.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        (await SendAsync(outsider, project.FlowId, "Ward flow.pdf", Pdf("Not yours to add to"), "application/pdf")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.GetAsync<List<EntryDto>>($"/api/v1/flows/{project.FlowId}/entries")).Should().BeEmpty("nothing half-added");
    }
}
