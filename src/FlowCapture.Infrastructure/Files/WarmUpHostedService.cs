using System.Diagnostics;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FlowCapture.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Infrastructure.Files;

/// <summary>
/// Does the slow "first time" work right after the server starts, in the background, so the first person to save something does not pay for it.
/// Measured: the first Excel read took about 1.0 s and the first PDF read about 1.4 s (loading and compiling the readers), then 1-30 ms every time after.
/// The first database queries of each kind are the same: compiling them takes a moment once. Nothing is written anywhere, and a failure here is only logged.
/// </summary>
public sealed class WarmUpHostedService(IServiceScopeFactory scopes, ILogger<WarmUpHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();                                       // never hold up the start of the server
        var total = Stopwatch.StartNew();
        string? failed = null;
        try
        {
            var files = Stopwatch.StartNew();
            WarmFileReaders();
            log.LogInformation("Warm-up: Excel and PDF readers ready in {Ms} ms", files.ElapsedMilliseconds);
        }
        catch (Exception e) { failed = "the file readers"; log.LogWarning(e, "Warm-up of the file readers did not finish"); }

        try
        {
            ct.ThrowIfCancellationRequested();
            var queries = Stopwatch.StartNew();
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            // the queries saving a note or a request runs first: nothing matches (Id is never empty), so nothing is read or changed
            var none = Guid.Empty;
            await db.Flows.AsNoTracking().Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == none, ct);
            await db.CaptureEntries.AsNoTracking().Where(e => e.FlowId == none).MaxAsync(e => (int?)e.RefNo, ct);
            await db.Flows.IgnoreQueryFilters().AsNoTracking().Where(f => f.ProjectDepartmentId == none).MaxAsync(f => (int?)f.RequestNo, ct);
            await db.ProjectDepartments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == none, ct);
            log.LogInformation("Warm-up: first database queries ready in {Ms} ms", queries.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { failed ??= "the database queries"; log.LogWarning(e, "Warm-up of the database queries did not finish"); }

        log.LogInformation("Warm-up finished in {Ms} ms{Note}", total.ElapsedMilliseconds, failed is null ? "" : $" (not everything: {failed})");
    }

    /// <summary>Reads a tiny workbook and a tiny PDF, so the readers are loaded and compiled. Returns what they read (a test checks the samples really are readable).</summary>
    internal static (string Excel, string Pdf) WarmFileReaders()
    {
        var reader = new FileTextExtractor();
        return (reader.Extract(SmallWorkbook(), "warm.xlsx"), reader.Extract(SmallPdf(), "warm.pdf"));
    }

    private static MemoryStream SmallWorkbook()
    {
        var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook, true))
        {
            var book = doc.AddWorkbookPart();
            book.Workbook = new Workbook();
            var sheets = book.Workbook.AppendChild(new Sheets());
            var part = book.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(new SheetData(new Row(
                new Cell { CellReference = "A1", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("Ward")) },
                new Cell { CellReference = "B1", CellValue = new CellValue("12") }) { RowIndex = 1 }));
            sheets.Append(new Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = "Warm" });
        }
        ms.Position = 0;
        return ms;
    }

    private static MemoryStream SmallPdf()
    {
        const string content = "BT /F1 18 Tf 20 100 Td (Warm up text) Tj ET";
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
        return new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString()));
    }
}
