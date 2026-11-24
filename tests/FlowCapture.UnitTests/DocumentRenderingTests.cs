using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FlowCapture.Application.Documents;
using FlowCapture.Domain;
using FlowCapture.Infrastructure.Diagrams;
using FlowCapture.Infrastructure.Documents;
using FlowCapture.Shared.Content;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowCapture.UnitTests;

/// <summary>Runs only where a browser is installed; skipped (not failed) elsewhere.</summary>
public sealed class RequiresBrowserFactAttribute : FactAttribute
{
    public RequiresBrowserFactAttribute()
    {
        if (ChromiumDiagramRenderer.Resolve(null) is null) Skip = "No Chrome, Chromium or Edge installed.";
    }
}

public sealed class RequiresLibreOfficeFactAttribute : FactAttribute
{
    public RequiresLibreOfficeFactAttribute()
    {
        if (LibreOfficePdfConverter.Resolve(null) is null) Skip = "LibreOffice is not installed.";
    }
}

public class TemplateAndWordTests
{
    private static readonly OpenXmlRgdWriter Writer = new();

    /// <summary>Not a check: set FLOWCAPTURE_WRITE_TEMPLATE to a file path to export the built-in template so it can be opened in Word and re-branded.</summary>
    [Fact]
    public void The_built_in_template_can_be_exported_for_branding()
    {
        var target = Environment.GetEnvironmentVariable("FLOWCAPTURE_WRITE_TEMPLATE");
        var bytes = RgdTemplateFactory.CreateDefault();
        Writer.ValidateTemplate(new MemoryStream(bytes)).Should().BeEmpty();
        if (!string.IsNullOrWhiteSpace(target)) File.WriteAllBytes(target, bytes);
    }

    private static RgdDocument SampleDocument(ImageBlock? image = null)
    {
        var content = FlowJson.Deserialize(TestData.Example().ToJsonString());
        var input = new FlowDocumentInput(content, "MRD-003", "MRD", "Palavesaraj P", new DateTime(2025, 3, 18, 0, 0, 0, DateTimeKind.Utc), "Test Hospital",
            "Asha Lead", 3, new DateTime(2025, 3, 17, 0, 0, 0, DateTimeKind.Utc), [new OpenIssue("Q1", "businessRules", "Which ward is pre-selected?")], image);
        return RgdDocumentBuilder.Build(input);
    }

    private static string AllText(WordprocessingDocument doc) =>
        string.Join("\n", doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Select(p => string.Concat(p.Descendants<Text>().Select(t => t.Text))));

    [Fact]
    public void The_default_template_is_a_valid_word_file_with_all_the_placeholders_and_styles_the_writer_needs()
    {
        var template = RgdTemplateFactory.CreateDefault();
        using var stream = new MemoryStream(template);

        Writer.ValidateTemplate(stream).Should().BeEmpty();

        using var doc = WordprocessingDocument.Open(new MemoryStream(template), false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        AllText(doc).Should().Contain("AOSTA INDIA PRIVATE LIMITED").And.Contain("BACKBONE").And.Contain("Requirement Gathering Document");
        doc.MainDocumentPart.HeaderParts.Single().Header!.InnerText.Should().Contain("Requirement Gathering Document");
        doc.MainDocumentPart.FooterParts.Single().Footer!.InnerXml.Should().Contain("PAGE");
        body.Descendants<SectionProperties>().Should().HaveCount(2);                        // the cover is its own section
        doc.MainDocumentPart.ImageParts.Should().NotBeEmpty();
    }

    [Fact]
    public void A_file_that_is_not_a_usable_template_is_refused_with_reasons()
    {
        Writer.ValidateTemplate(new MemoryStream([1, 2, 3, 4])).Should().ContainSingle().Which.Should().Contain("could not be opened");

        // a real Word file that lacks the placeholders and styles
        using var bare = new MemoryStream();
        using (var d = WordprocessingDocument.Create(bare, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            d.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("Hello")))));
        }
        bare.Position = 0;
        var problems = Writer.ValidateTemplate(bare);
        problems.Should().Contain(p => p.Contains("\"Body\"")).And.Contain(p => p.Contains("\"DocNumber\"")).And.Contain(p => p.Contains("\"Heading1\""));
    }

    [Fact]
    public async Task The_writer_fills_the_cover_and_replaces_the_body_marker_with_the_document()
    {
        var model = SampleDocument();
        var bytes = await Writer.WriteAsync(model, new MemoryStream(RgdTemplateFactory.CreateDefault()), CancellationToken.None);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var text = AllText(doc);
        text.Should().Contain("MRD-003").And.Contain("Palavesaraj P").And.Contain("18-03-2025");
        text.Should().NotContain("MRD-000").And.NotContain("The generated document is placed here.");
        doc.MainDocumentPart!.Document!.Body!.Descendants<SdtBlock>().Should().BeEmpty();            // the marker is gone
        doc.PackageProperties.Title.Should().Be("Default Department/Ward Selection in IP Case Sheet Receive Menu");

        // headings appear in the order of the sample RGD
        var headings = doc.MainDocumentPart.Document.Body.Descendants<Paragraph>()
            .Where(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value is "Heading1" or "DocTitle")
            .Select(p => p.InnerText).ToList();
        headings.Should().ContainInOrder("Functional Requirement Document", "1. Purpose", "2. Scope", "3. Functional Requirement");
    }

    [Fact]
    public async Task Tables_have_repeating_header_rows_and_no_empty_cells_and_bullets_use_the_bullet_style()
    {
        var bytes = await Writer.WriteAsync(SampleDocument(), new MemoryStream(RgdTemplateFactory.CreateDefault()), CancellationToken.None);
        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var body = doc.MainDocumentPart!.Document!.Body!;

        var tables = body.Descendants<Table>().ToList();
        tables.Should().HaveCountGreaterThan(8);
        tables.Where(t => t.Elements<TableRow>().First().TableRowProperties?.GetFirstChild<TableHeader>() is not null).Should().HaveCountGreaterThan(5);
        foreach (var cell in tables.SelectMany(t => t.Descendants<TableCell>()).Where(c => c.Ancestors<SdtElement>().Count() == 0))
            cell.InnerText.Should().NotBeNullOrWhiteSpace();
        body.Descendants<Paragraph>().Count(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "ListBullet").Should().BeGreaterThan(0);
        // the requirements table is the first table after the cover table
        var req = tables[1];
        req.Descendants<TableRow>().First().InnerText.Should().Contain("Req. ID").And.Contain("Requirements Description").And.Contain("Category");
        req.InnerText.Should().Contain("REQ001").And.Contain("CR");
    }

    [Fact]
    public async Task A_picture_is_embedded_and_scaled_to_fit_the_page()
    {
        // 1x1 transparent PNG, declared as a very large diagram
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var model = SampleDocument(new ImageBlock(png, 1800, 900, "Figure 1: Main flow, version 3"));

        var bytes = await Writer.WriteAsync(model, new MemoryStream(RgdTemplateFactory.CreateDefault()), CancellationToken.None);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        doc.MainDocumentPart!.ImageParts.Count().Should().BeGreaterThanOrEqualTo(2);              // the cover logo and the diagram
        var extent = doc.MainDocumentPart.Document!.Body!.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().Last();
        (extent.Cx!.Value / 9525.0).Should().BeLessThanOrEqualTo(624.5);                           // 6.5 inches
        (extent.Cy!.Value / 9525.0).Should().BeLessThanOrEqualTo(640.5);
        ((double)extent.Cx.Value / extent.Cy.Value).Should().BeApproximately(2.0, 0.01);          // same shape as the original
        AllText(doc).Should().Contain("Figure 1: Main flow, version 3");
    }

    [Fact]
    public async Task Special_characters_and_line_breaks_in_text_survive_into_the_document()
    {
        var content = FlowJson.Deserialize(TestData.Example().ToJsonString());
        content.Purpose = "Show <ward> & \"mapped\" bay; line one\nline two. தமிழ்";
        var model = RgdDocumentBuilder.Build(new FlowDocumentInput(content, "MRD-001", "MRD", "A & B", DateTime.UtcNow, "H", "P", 1, null, [], null));

        var bytes = await Writer.WriteAsync(model, new MemoryStream(RgdTemplateFactory.CreateDefault()), CancellationToken.None);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var text = AllText(doc);
        text.Should().Contain("Show <ward> & \"mapped\" bay; line one").And.Contain("line two. தமிழ்").And.Contain("A & B");
    }

    [Fact]
    public void The_diagram_library_the_server_draws_documents_with_is_shipped_next_to_the_app()
    {
        // The server draws the diagrams in documents with this bundled Mermaid (the web app draws its on-screen diagrams with the npm package).
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "mermaid.min.js");
        File.Exists(path).Should().BeTrue();
        new FileInfo(path).Length.Should().BeGreaterThan(1_000_000);
        File.ReadAllText(path).Should().Contain("mermaid");
    }
}

public class DiagramRenderingTests
{
    private static ChromiumDiagramRenderer Renderer() =>
        new(Options.Create(new DiagramOptions()), NullLogger<ChromiumDiagramRenderer>.Instance);

    [RequiresBrowserFact]
    public async Task A_flow_is_drawn_as_a_valid_standalone_svg_and_a_sharp_png()
    {
        var result = await Renderer().RenderAsync((string)TestData.Example()["mermaid"]!, true, CancellationToken.None);

        var svg = XDocument.Parse(result.Svg);                                             // well-formed XML
        svg.Root!.Name.LocalName.Should().Be("svg");
        svg.Root.Attribute("xmlns")!.Value.Should().Be("http://www.w3.org/2000/svg");
        svg.Root.Attribute("width")!.Value.Should().Be(result.Width.ToString());
        result.Svg.Should().Contain("Is the user a super user?").And.Contain("Opens IP Case Sheet Receive in Department menu");
        result.Svg.Should().NotContain("foreignObject");                                   // plain SVG text, so other tools can show it

        result.Png!.Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47);
        // PNG width in the header is Width + margins, at 2x density
        var pngWidth = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(result.Png!.AsSpan(16));
        pngWidth.Should().BeGreaterThan(result.Width);
        result.Width.Should().BeGreaterThan(100);
        result.Height.Should().BeGreaterThan(100);
    }

    [RequiresBrowserFact]
    public async Task Invalid_mermaid_is_reported_as_a_clear_error_and_not_as_a_blank_picture()
    {
        var act = () => Renderer().RenderAsync("flowchart TD\n  A --> ", true, CancellationToken.None);
        var e = (await act.Should().ThrowAsync<DomainException>()).Which;
        e.Code.Should().Be("diagram_invalid");
        e.Message.Should().Contain("could not be drawn");
        e.Message.Should().NotContain("e.message");                       // the real reason from the parser, not the page's own source
    }

    [RequiresBrowserFact]
    public async Task The_combined_project_diagram_with_departments_and_hand_offs_can_be_drawn()
    {
        var a = FlowJson.Deserialize(TestData.Example().ToJsonString());
        var b = FlowJson.Deserialize(TestData.Example().ToJsonString());
        var mermaid = CombinedMermaidBuilder.Build([new(1, "MRD", "MRD", "Receive", a), new(2, "BILLING", "Billing", "Final bill", b)], [new(1, "S5", 2, "S1", "Case sheet")]);

        var result = await Renderer().RenderAsync(mermaid, true, CancellationToken.None);

        XDocument.Parse(result.Svg);
        result.Svg.Should().Contain("Billing").And.Contain("Final bill").And.Contain("Case sheet");
        result.Png!.Length.Should().BeGreaterThan(5000);
    }

    [Fact]
    public async Task Without_a_browser_the_error_says_what_to_install()
    {
        var renderer = new ChromiumDiagramRenderer(Options.Create(new DiagramOptions { BrowserPath = @"C:\does\not\exist.exe" }), NullLogger<ChromiumDiagramRenderer>.Instance);
        if (ChromiumDiagramRenderer.Resolve(null) is not null) return;                    // a browser exists elsewhere on this machine
        await renderer.Invoking(r => r.RenderAsync("flowchart TD\n A-->B", true, CancellationToken.None)).Should().ThrowAsync<DependencyUnavailableException>().WithMessage("*Install*");
    }
}

public class PdfConversionTests
{
    [RequiresLibreOfficeFact]
    public async Task The_word_document_converts_to_a_pdf_with_a_cover_and_several_pages()
    {
        var content = FlowJson.Deserialize(TestData.Example().ToJsonString());
        var model = RgdDocumentBuilder.Build(new FlowDocumentInput(content, "MRD-003", "MRD", "Palavesaraj P", DateTime.UtcNow, "Test Hospital", "Asha", 3, DateTime.UtcNow,
            [new OpenIssue("Q1", "businessRules", "Which ward is pre-selected?")], null));
        var docx = await new OpenXmlRgdWriter().WriteAsync(model, new MemoryStream(RgdTemplateFactory.CreateDefault()), CancellationToken.None);

        var pdf = await new LibreOfficePdfConverter(Options.Create(new DocumentOptions()), NullLogger<LibreOfficePdfConverter>.Instance).ConvertAsync(docx, CancellationToken.None);

        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        var pages = Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page[^s]").Count;
        pages.Should().BeGreaterThanOrEqualTo(3);                                          // cover + at least two content pages
        pdf.Length.Should().BeGreaterThan(10_000);
    }
}
