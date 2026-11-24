using System.Diagnostics;
using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowCapture.Infrastructure.Diagrams;

/// <summary>Prints an HTML page to PDF with headless Chrome or Edge: no extra software, and the page decides its own A4 layout with CSS.</summary>
public sealed class ChromiumPdfRenderer(IOptions<DiagramOptions> options, ILogger<ChromiumPdfRenderer> log) : IHtmlPdfRenderer
{
    private static readonly SemaphoreSlim Limit = new(2, 2);

    public async Task<byte[]> RenderAsync(string html, CancellationToken ct)
    {
        var browser = ChromiumDiagramRenderer.Resolve(options.Value.BrowserPath)
                      ?? throw new DependencyUnavailableException("No browser (Chrome, Chromium or Edge) is installed on the server, so the PDF cannot be made. Install one or set Diagrams:BrowserPath.");
        var dir = Directory.CreateTempSubdirectory("flowcapture-pdf-");
        await Limit.WaitAsync(ct);
        try
        {
            var page = Path.Combine(dir.FullName, "guide.html");
            var pdf = Path.Combine(dir.FullName, "guide.pdf");
            await File.WriteAllTextAsync(page, html, new System.Text.UTF8Encoding(false), ct);

            var args = new List<string>
            {
                "--headless=new", "--disable-gpu", "--no-first-run", "--disable-dev-shm-usage", "--disable-javascript",
                $"--user-data-dir={Path.Combine(dir.FullName, "profile")}",
                "--no-pdf-header-footer", $"--print-to-pdf={pdf}", new Uri(page).AbsoluteUri,
            };
            if (options.Value.NoSandbox) args.Insert(1, "--no-sandbox");

            var psi = new ProcessStartInfo(browser) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi) ?? throw new DependencyUnavailableException("The browser could not be started.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(options.Value.TimeoutSeconds, 60)));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new DependencyUnavailableException("The browser took too long to make the PDF.");
            }
            _ = await stdout;
            var err = await stderr;
            log.LogDebug("browser exit {Code}: {Err}", process.ExitCode, err.Length > 400 ? err[..400] : err);

            if (!File.Exists(pdf) || new FileInfo(pdf).Length < 200)
                throw new DependencyUnavailableException("The browser did not produce the PDF.");
            return await File.ReadAllBytesAsync(pdf, ct);
        }
        finally
        {
            Limit.Release();
            try { dir.Delete(true); } catch (IOException) { }
        }
    }
}
