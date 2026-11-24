using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FlowCapture.Domain;
using FlowCapture.Infrastructure.Files;
using FluentAssertions;
using Xunit;

namespace FlowCapture.UnitTests;

public class FileTextExtractorTests
{
    private readonly FileTextExtractor _x = new();

    private static MemoryStream Workbook()
    {
        var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook, true))
        {
            var book = doc.AddWorkbookPart();
            book.Workbook = new Workbook();
            var styles = book.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new Stylesheet(
                new Fonts(new Font()), new Fills(new Fill(), new Fill()), new Borders(new Border()),
                new CellStyleFormats(new CellFormat()),
                new CellFormats(new CellFormat(), new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true }));
            var sheets = book.Workbook.AppendChild(new Sheets());

            WorksheetPart AddSheet(uint id, string name, params Row[] rows)
            {
                var part = book.AddNewPart<WorksheetPart>();
                part.Worksheet = new Worksheet(new SheetData(rows));
                sheets.Append(new Sheet { Id = book.GetIdOfPart(part), SheetId = id, Name = name });
                return part;
            }
            Cell Text(string reference, string value) => new() { CellReference = reference, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(value)) };
            Cell Number(string reference, string value, uint style = 0) => new() { CellReference = reference, CellValue = new CellValue(value), StyleIndex = style };

            AddSheet(1, "Wards",
                new Row(Text("A1", "Ward"), Text("B1", "Beds"), Text("C1", "Opened")) { RowIndex = 1 },
                new Row(Text("A2", "ICU"), Number("B2", "12.5"), Number("C2", "45323", 1)) { RowIndex = 2 },         // 45323 = 01-02-2024
                new Row { RowIndex = 3 },                                                                           // an empty row is left out
                new Row(Text("A4", "Pharmacy"), Text("C4", "by request")) { RowIndex = 4 });                        // an empty cell in the middle stays a gap
            AddSheet(2, "Empty sheet");
            AddSheet(3, "Notes", new Row(Text("A1", "Discharge needs a summary")) { RowIndex = 1 });
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>A small, valid PDF with the given text on its page (or none).</summary>
    private static MemoryStream Pdf(string? text)
    {
        var content = text is null ? "" : $"BT /F1 18 Tf 20 100 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 400 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            $"<</Length {content.Length}>>\nstream\n{content}\nendstream",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<</Size {objects.Length + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF");
        return new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    [Fact]
    public void An_Excel_workbook_becomes_text_sheet_by_sheet_with_dates_gaps_and_no_empty_rows()
    {
        var text = _x.Extract(Workbook(), "Wards.xlsx");
        text.Should().Contain("Sheet: Wards").And.Contain("Sheet: Notes");
        text.Should().NotContain("Empty sheet");
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        lines.Should().Contain("Ward | Beds | Opened");
        lines.Should().Contain("ICU | 12.5 | 01-02-2024", "a number stays a number and a date shows as a date");
        lines.Should().Contain("Pharmacy |  | by request", "an empty cell between two full ones stays a gap");
        lines.Should().Contain("Discharge needs a summary");
        lines.Count(l => l.Length == 0).Should().BeLessThan(4);
    }

    [Fact]
    public void An_old_xls_file_is_refused_with_the_way_out_and_other_types_are_refused_too()
    {
        var old = () => _x.Extract(new MemoryStream([1, 2, 3]), "old.xls");
        old.Should().Throw<DomainException>().Which.Message.Should().Contain("Save As").And.Contain(".xlsx");
        var other = () => _x.Extract(new MemoryStream([1, 2, 3]), "notes.docx");
        other.Should().Throw<DomainException>().Which.Code.Should().Be("file_type");
    }

    [Fact]
    public void A_broken_workbook_or_PDF_says_it_could_not_be_opened_instead_of_crashing()
    {
        var bad = () => _x.Extract(new MemoryStream(Encoding.ASCII.GetBytes("this is not a workbook")), "broken.xlsx");
        bad.Should().Throw<DomainException>().Which.Code.Should().Be("file_unreadable");
        var badPdf = () => _x.Extract(new MemoryStream(Encoding.ASCII.GetBytes("this is not a pdf at all")), "broken.pdf");
        badPdf.Should().Throw<DomainException>().Which.Code.Should().Be("file_unreadable");
    }

    [Fact]
    public void A_PDF_with_text_is_read_and_a_scanned_PDF_with_none_says_to_attach_photos_instead()
    {
        _x.Extract(Pdf("Bed allocation happens at admission"), "flow.pdf").Should().Contain("Bed allocation happens at admission");
        var scan = () => _x.Extract(Pdf(null), "scan.pdf");
        scan.Should().Throw<DomainException>().Which.Should().Match<DomainException>(e => e.Code == "pdf_no_text" && e.Message.Contains("photos"));
    }
}
