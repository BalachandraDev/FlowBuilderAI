using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Documents;
using A = DocumentFormat.OpenXml.Drawing;

namespace FlowCapture.Infrastructure.Documents;

/// <summary>
/// Fills the Word template: cover details go into the content controls, and the document replaces the "Body" control.
/// Layout comes from the template's styles (Heading1, TableText, RgdTable ...), so the look is the template's, not the code's.
/// </summary>
public sealed class OpenXmlRgdWriter : IRgdDocxWriter
{
    private const int UsableWidthDxa = 9360;                         // 6.5 in
    private const int MaxImageWidthPx = 624, MaxImageHeightPx = 640;
    private static readonly string HeaderFill = "DCE6F2";

    public async Task<byte[]> WriteAsync(RgdDocument document, Stream template, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        await template.CopyToAsync(stream, ct);
        stream.Position = 0;

        using (var doc = WordprocessingDocument.Open(stream, true))
        {
            var main = doc.MainDocumentPart ?? throw new InvalidOperationException("The template has no document part.");
            var body = main.Document?.Body ?? throw new InvalidOperationException("The template has no body.");

            FillCover(body, document.Cover);

            var marker = body.Descendants<SdtBlock>().FirstOrDefault(s => TagOf(s) == "Body")
                         ?? throw new InvalidOperationException("The template has no \"Body\" content control.");
            var imageId = 100;
            foreach (var element in Render(main, document.Blocks, () => imageId++))
            {
                body.InsertBefore(element, marker);
            }
            marker.Remove();

            doc.PackageProperties.Title = document.Title;
            doc.PackageProperties.Subject = $"{document.Cover.DocNumber}";
            doc.PackageProperties.Creator = "bbflowbuild";
            doc.PackageProperties.Created = DateTime.UtcNow;
            main.Document!.Save();
        }
        return stream.ToArray();
    }

    public IReadOnlyList<string> ValidateTemplate(Stream template)
    {
        var problems = new List<string>();
        try
        {
            using var copy = new MemoryStream();
            template.CopyTo(copy);
            copy.Position = 0;
            using var doc = WordprocessingDocument.Open(copy, false);
            var main = doc.MainDocumentPart;
            if (main?.Document?.Body is not { } body) return ["The file is not a Word document with content."];

            var tags = body.Descendants<SdtElement>().Select(TagOf).Where(t => t is not null).ToHashSet();
            foreach (var required in WordXml.RequiredTags.Where(t => !tags.Contains(t)))
                problems.Add($"The template has no content control tagged \"{required}\".");

            var styles = main.StyleDefinitionsPart?.Styles?.Elements<Style>().Select(s => s.StyleId?.Value).ToHashSet() ?? [];
            foreach (var required in WordXml.RequiredStyles.Where(s => !styles.Contains(s)))
                problems.Add($"The template has no style with the id \"{required}\".");
        }
        catch (Exception e) when (e is OpenXmlPackageException or InvalidDataException or IOException or FileFormatException)
        {
            problems.Add("The file could not be opened as a Word document (.docx).");
        }
        return problems;
    }

    // ------------------------------------------------------------------ cover

    private static void FillCover(Body body, RgdCover cover)
    {
        var values = new Dictionary<string, string>
        {
            ["DocNumber"] = cover.DocNumber, ["Module"] = cover.Module, ["AssignedBy"] = cover.AssignedBy, ["CreatedDate"] = cover.CreatedDate,
        };
        foreach (var sdt in body.Descendants<SdtRun>().ToList())
        {
            if (TagOf(sdt) is not { } tag || !values.TryGetValue(tag, out var value)) continue;
            var content = sdt.GetFirstChild<SdtContentRun>() ?? sdt.AppendChild(new SdtContentRun());
            var text = content.Descendants<Text>().FirstOrDefault();
            if (text is null) content.AppendChild(new Run(text = new Text()));
            text.Text = value;
            text.Space = SpaceProcessingModeValues.Preserve;
            foreach (var extra in content.Descendants<Text>().Skip(1).ToList()) extra.Remove();
        }
    }

    private static string? TagOf(SdtElement sdt) => sdt.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value;

    // ------------------------------------------------------------------ blocks

    private static IEnumerable<OpenXmlElement> Render(MainDocumentPart main, IReadOnlyList<Block> blocks, Func<int> nextImageId)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    yield return Para(h.Text, h.Level switch { 0 => "DocTitle", 1 => "Heading1", 2 => "Heading2", _ => "Heading3" });
                    break;
                case ParagraphBlock p:
                    yield return Para(p.Text, null, p.Bold, p.Italic);
                    break;
                case BulletsBlock b:
                    foreach (var item in b.Items) yield return Para(item, "ListBullet");
                    break;
                case TableBlock t:
                    yield return Table(t);
                    yield return Para("", null, spacingAfter: "80");           // keeps two tables from merging into one
                    break;
                case ImageBlock i:
                    yield return Image(main, i, nextImageId());
                    yield return Para(i.Caption, "Caption");
                    break;
                case PageBreakBlock:
                    yield return new Paragraph(new Run(new Break { Type = BreakValues.Page }));
                    break;
            }
        }
    }

    private static Paragraph Para(string text, string? style, bool bold = false, bool italic = false, string? spacingAfter = null)
    {
        var p = new Paragraph();
        var props = new ParagraphProperties();
        if (style is not null) props.Append(new ParagraphStyleId { Val = style });
        if (spacingAfter is not null) props.Append(new SpacingBetweenLines { After = spacingAfter });
        if (props.HasChildren) p.Append(props);
        if (text.Length == 0) return p;

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var run = new Run();
            if (bold || italic)
            {
                var rp = new RunProperties();
                if (bold) rp.Append(new Bold());
                if (italic) rp.Append(new Italic());
                run.Append(rp);
            }
            if (i > 0) run.Append(new Break());
            run.Append(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            p.Append(run);
        }
        return p;
    }

    private static Table Table(TableBlock block)
    {
        var table = new Table();
        table.Append(new TableProperties(
            new TableStyle { Val = "RgdTable" },
            new TableWidth { Width = UsableWidthDxa.ToString(), Type = TableWidthUnitValues.Dxa },
            new TableLayout { Type = TableLayoutValues.Fixed }));

        var total = block.Widths.Sum();
        var widths = block.Widths.Select(w => (int)Math.Round(UsableWidthDxa * (double)w / total)).ToList();
        var grid = new TableGrid();
        foreach (var w in widths) grid.Append(new GridColumn { Width = w.ToString() });
        table.Append(grid);

        var labelTable = block.Header.Count == 0;
        if (!labelTable)
        {
            var header = new TableRow(new TableRowProperties(new TableHeader(), new CantSplit()));
            for (var c = 0; c < block.Header.Count; c++) header.Append(Cell(block.Header[c], widths[c], bold: true, fill: HeaderFill));
            table.Append(header);
        }
        foreach (var row in block.Rows)
        {
            var tr = new TableRow(new TableRowProperties(new CantSplit()));
            for (var c = 0; c < row.Count; c++)
                tr.Append(Cell(row[c], widths[Math.Min(c, widths.Count - 1)], bold: labelTable && c == 0, fill: labelTable && c == 0 ? HeaderFill : null));
            table.Append(tr);
        }
        return table;
    }

    private static TableCell Cell(string text, int width, bool bold, string? fill)
    {
        var props = new TableCellProperties(new TableCellWidth { Width = width.ToString(), Type = TableWidthUnitValues.Dxa });
        if (fill is not null) props.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = fill });
        var cell = new TableCell(props);
        foreach (var line in text.Split('\n')) cell.Append(Para(line, "TableText", bold));
        return cell;
    }

    private static Paragraph Image(MainDocumentPart main, ImageBlock image, int id)
    {
        var part = main.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream(image.Png)) part.FeedData(ms);
        var relId = main.GetIdOfPart(part);

        var scale = Math.Min(1.0, Math.Min(MaxImageWidthPx / (double)image.WidthPx, MaxImageHeightPx / (double)image.HeightPx));
        var cx = (long)(image.WidthPx * scale * WordXml.EmuPerPixel);
        var cy = (long)(image.HeightPx * scale * WordXml.EmuPerPixel);

        var drawing = new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = cx, Cy = cy },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = (uint)id, Name = $"Diagram {id}", Description = image.Caption },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties(
                    new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties { Id = (uint)id, Name = $"diagram{id}.png" },
                    new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureDrawingProperties()),
                new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                new DocumentFormat.OpenXml.Drawing.Pictures.ShapeProperties(
                    new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });

        return new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }, new KeepNext()), new Run(drawing));
    }
}
