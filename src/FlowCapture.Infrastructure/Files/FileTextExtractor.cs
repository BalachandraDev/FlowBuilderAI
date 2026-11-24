using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using UglyToad.PdfPig;

namespace FlowCapture.Infrastructure.Files;

/// <summary>
/// Turns an Excel workbook or a PDF into plain text for a note. Excel: every sheet, one line per row with the cells separated by " | "
/// (numbers as typed, dates as dates, empty rows left out). PDF: the text of every page. A scanned PDF has no text to read, and says so.
/// </summary>
public sealed class FileTextExtractor : IFileTextExtractor
{
    private const int MaxSheets = 25, MaxRows = 3000, MaxColumns = 60, MaxPages = 150;

    public string Extract(Stream file, string fileName)
    {
        var name = Path.GetFileName(fileName ?? "").ToLowerInvariant();
        if (name.EndsWith(".xlsx") || name.EndsWith(".xlsm")) return Workbook(file);
        if (name.EndsWith(".xls")) throw new DomainException("Old Excel files (.xls) cannot be read. In Excel choose Save As, Excel Workbook (.xlsx), then attach that file.", "xls_not_supported");
        if (name.EndsWith(".pdf")) return Pdf(file);
        throw new DomainException("Only Excel workbooks (.xlsx) and PDF files can be read this way.", "file_type");
    }

    // ------------------------------------------------------------------ Excel

    private static string Workbook(Stream file)
    {
        try
        {
            using var doc = SpreadsheetDocument.Open(file, false);
            var book = doc.WorkbookPart ?? throw new DomainException("This workbook is empty.", "file_empty");
            var strings = book.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];
            var dateStyles = DateStyles(book);
            var sb = new StringBuilder();
            var sheets = book.Workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
            foreach (var sheet in sheets.Take(MaxSheets))
            {
                if (book.GetPartById(sheet.Id!.Value!) is not WorksheetPart part) continue;
                var rows = new List<string>();
                foreach (var row in part.Worksheet.Descendants<Row>())
                {
                    var cells = new SortedDictionary<int, string>();
                    foreach (var cell in row.Elements<Cell>())
                    {
                        var col = ColumnIndex(cell.CellReference?.Value);
                        if (col < 0 || col >= MaxColumns) continue;
                        var text = CellText(cell, strings, dateStyles);
                        if (text.Length > 0) cells[col] = text;
                    }
                    if (cells.Count == 0) continue;
                    // the cells keep their columns: an empty cell between two full ones stays visible as a gap
                    var last = cells.Keys.Max();
                    rows.Add(string.Join(" | ", Enumerable.Range(0, last + 1).Select(i => cells.GetValueOrDefault(i, ""))).TrimEnd());
                    if (rows.Count >= MaxRows) break;
                }
                if (rows.Count == 0) continue;
                sb.Append("Sheet: ").AppendLine(sheet.Name?.Value ?? "Sheet");
                foreach (var r in rows) sb.AppendLine(r);
                if (rows.Count >= MaxRows) sb.AppendLine($"(only the first {MaxRows} rows of this sheet are shown)");
                sb.AppendLine();
            }
            if (sheets.Count > MaxSheets) sb.AppendLine($"(only the first {MaxSheets} sheets are shown)");
            var text2 = sb.ToString().Trim();
            if (text2.Length == 0) throw new DomainException("This workbook has no text or numbers in it.", "file_empty");
            return text2;
        }
        catch (DomainException) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or FileFormatException or System.Xml.XmlException or OpenXmlPackageException or InvalidOperationException or ArgumentException)
        {
            throw new DomainException("This Excel file could not be opened. Check that it is an .xlsx workbook and is not password protected.", "file_unreadable");
        }
    }

    private static string CellText(Cell cell, List<string> strings, HashSet<uint> dateStyles)
    {
        var raw = cell.CellValue?.InnerText ?? cell.InnerText;
        if (string.IsNullOrEmpty(raw)) return "";
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString) return int.TryParse(raw, out var i) && i >= 0 && i < strings.Count ? strings[i].Trim() : "";
        if (type == CellValues.Boolean) return raw == "1" ? "Yes" : "No";
        if (type == CellValues.InlineString || type == CellValues.String || type == CellValues.Error) return raw.Trim();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            if (cell.StyleIndex?.Value is { } style && dateStyles.Contains(style) && number > 0 && number < 2958466)
                return DateTime.FromOADate(number).ToString(number % 1 == 0 ? "dd-MM-yyyy" : "dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);
            return number.ToString("0.##########", CultureInfo.InvariantCulture);
        }
        return raw.Trim();
    }

    /// <summary>The cell styles whose number format is a date or a time, so a date shows as a date and not as 45231.</summary>
    private static HashSet<uint> DateStyles(WorkbookPart book)
    {
        var result = new HashSet<uint>();
        var styles = book.WorkbookStylesPart?.Stylesheet;
        if (styles?.CellFormats is null) return result;
        var custom = styles.NumberingFormats?.Elements<NumberingFormat>().ToDictionary(n => n.NumberFormatId?.Value ?? 0, n => n.FormatCode?.Value ?? "") ?? [];
        uint index = 0;
        foreach (var f in styles.CellFormats.Elements<CellFormat>())
        {
            var id = f.NumberFormatId?.Value ?? 0;
            var builtIn = id is >= 14 and <= 22 or >= 45 and <= 47;
            var code = custom.GetValueOrDefault(id, "");
            var customDate = code.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]", ""), "[dmyhs]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (builtIn || customDate) result.Add(index);
            index++;
        }
        return result;
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return -1;
        var n = 0;
        foreach (var c in reference)
        {
            if (!char.IsLetter(c)) break;
            n = n * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return n - 1;
    }

    // ------------------------------------------------------------------ PDF

    private static string Pdf(Stream file)
    {
        try
        {
            using var pdf = PdfDocument.Open(file);
            var sb = new StringBuilder();
            var pages = 0;
            foreach (var page in pdf.GetPages())
            {
                if (++pages > MaxPages) { sb.AppendLine($"(only the first {MaxPages} pages are shown)"); break; }
                var text = page.Text.Trim();
                if (text.Length == 0) continue;
                if (pdf.NumberOfPages > 1) sb.Append("Page ").Append(page.Number).AppendLine();
                sb.AppendLine(text).AppendLine();
            }
            var all = sb.ToString().Trim();
            if (all.Length < 8)
                throw new DomainException("This PDF has no text in it (it looks like a scan). Take photos of the pages and attach those pictures instead.", "pdf_no_text");
            return all;
        }
        catch (DomainException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new DomainException("This PDF could not be opened. It may be damaged or password protected.", "file_unreadable");
        }
    }
}
