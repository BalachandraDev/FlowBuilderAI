using System.Diagnostics;
using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowCapture.Infrastructure.Documents;

public sealed class DocumentOptions
{
    public const string Section = "Documents";
    /// <summary>Path to soffice(.exe). Empty = look in the usual places.</summary>
    public string? LibreOfficePath { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Converts the Word file to PDF with LibreOffice running headless, so the PDF carries exactly the template's branding.
/// LibreOffice allows one conversion per profile at a time, so conversions are queued here.
/// </summary>
public sealed class LibreOfficePdfConverter(IOptions<DocumentOptions> options, ILogger<LibreOfficePdfConverter> log) : IPdfConverter
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public async Task<byte[]> ConvertAsync(byte[] docx, CancellationToken ct)
    {
        var soffice = Resolve(options.Value.LibreOfficePath)
                      ?? throw new DependencyUnavailableException("LibreOffice is not installed on the server, so PDF files cannot be made. Install it or set Documents:LibreOfficePath.");

        var dir = Directory.CreateTempSubdirectory("flowcapture-pdf-");
        try
        {
            var input = Path.Combine(dir.FullName, "document.docx");
            await File.WriteAllBytesAsync(input, docx, ct);
            var profile = new Uri(Path.Combine(dir.FullName, "profile")).AbsoluteUri;

            await OneAtATime.WaitAsync(ct);
            try
            {
                var psi = new ProcessStartInfo(soffice)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                };
                foreach (var arg in new[] { $"-env:UserInstallation={profile}", "--headless", "--norestore", "--nolockcheck", "--convert-to", "pdf:writer_pdf_Export", "--outdir", dir.FullName, input })
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi) ?? throw new DependencyUnavailableException("LibreOffice could not be started.");
                var stdout = process.StandardOutput.ReadToEndAsync(ct);
                var stderr = process.StandardError.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    throw new DependencyUnavailableException("LibreOffice took too long to make the PDF.");
                }
                log.LogDebug("LibreOffice: {Out} {Err}", await stdout, await stderr);
            }
            finally { OneAtATime.Release(); }

            var pdf = Path.Combine(dir.FullName, "document.pdf");
            if (!File.Exists(pdf)) throw new DependencyUnavailableException("LibreOffice did not produce a PDF.");
            return await File.ReadAllBytesAsync(pdf, ct);
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }

    public static string? Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string[] candidates =
        [
            @"C:\Program Files\LibreOffice\program\soffice.exe", @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
            "/usr/bin/soffice", "/usr/bin/libreoffice", "/usr/local/bin/soffice", "/opt/libreoffice/program/soffice", "/Applications/LibreOffice.app/Contents/MacOS/soffice",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
