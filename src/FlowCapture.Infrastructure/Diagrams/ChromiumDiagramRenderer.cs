using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowCapture.Infrastructure.Diagrams;

public sealed class DiagramOptions
{
    public const string Section = "Diagrams";
    /// <summary>Path to Chrome, Chromium or Edge. Empty = look in the usual places.</summary>
    public string? BrowserPath { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>Pixel density of the PNG. 2 gives a sharp picture in Word and PDF.</summary>
    public int Scale { get; set; } = 2;
    /// <summary>Chrome refuses to run as root in a container without this.</summary>
    public bool NoSandbox { get; set; }
}

/// <summary>
/// Draws Mermaid with a headless browser (the same job mermaid-cli does, without needing Node). The same bundled Mermaid library as
/// the phone is used, so server diagrams look like the ones in the app. Step one draws the SVG; step two takes a sharp PNG of it.
/// </summary>
public sealed partial class ChromiumDiagramRenderer(IOptions<DiagramOptions> options, ILogger<ChromiumDiagramRenderer> log) : IDiagramRenderer
{
    private static readonly SemaphoreSlim Limit = new(2, 2);
    private const int Margin = 16;

    public async Task<DiagramRender> RenderAsync(string mermaid, bool withPng, CancellationToken ct)
    {
        var browser = Resolve(options.Value.BrowserPath)
                      ?? throw new DependencyUnavailableException("No browser (Chrome, Chromium or Edge) is installed on the server, so diagrams cannot be drawn. Install one or set Diagrams:BrowserPath.");
        var script = Path.Combine(AppContext.BaseDirectory, "assets", "mermaid.min.js");
        if (!File.Exists(script)) throw new DependencyUnavailableException("The bundled diagram library (assets/mermaid.min.js) is missing from the server.");

        var dir = Directory.CreateTempSubdirectory("flowcapture-diagram-");
        await Limit.WaitAsync(ct);
        try
        {
            // 1. draw the SVG
            var drawPage = Path.Combine(dir.FullName, "draw.html");
            await File.WriteAllTextAsync(drawPage, DrawHtml(new Uri(script).AbsoluteUri, mermaid), ct);
            var dom = await RunAsync(browser, [.. Common(dir), "--virtual-time-budget=15000", "--dump-dom", new Uri(drawPage).AbsoluteUri], captureOutput: true, ct);
            var svg = ExtractSvg(dom, out var width, out var height);

            if (!withPng) return new DiagramRender(svg, null, width, height);

            // 2. take a PNG of it at the exact size
            return new DiagramRender(svg, await ShotAsync(browser, dir, svg, width, height, ct), width, height);
        }
        finally
        {
            Limit.Release();
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    public async Task<byte[]> RasterizeSvgAsync(string svg, int width, int height, CancellationToken ct)
    {
        var browser = Resolve(options.Value.BrowserPath)
                      ?? throw new DependencyUnavailableException("No browser (Chrome, Chromium or Edge) is installed on the server, so the picture cannot be made. Install one or set Diagrams:BrowserPath.");
        var dir = Directory.CreateTempSubdirectory("flowcapture-diagram-");
        await Limit.WaitAsync(ct);
        try { return await ShotAsync(browser, dir, svg, Math.Max(1, width), Math.Max(1, height), ct); }
        finally
        {
            Limit.Release();
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    private async Task<byte[]> ShotAsync(string browser, DirectoryInfo dir, string svg, int width, int height, CancellationToken ct)
    {
        var png = Path.Combine(dir.FullName, "diagram.png");
        var shotPage = Path.Combine(dir.FullName, "shot.html");
        await File.WriteAllTextAsync(shotPage, ShotHtml(svg, width, height), new UTF8Encoding(false), ct);
        await RunAsync(browser, [.. Common(dir), "--virtual-time-budget=3000", $"--window-size={width + 2 * Margin},{height + 2 * Margin}",
            $"--force-device-scale-factor={Math.Clamp(options.Value.Scale, 1, 4)}", "--default-background-color=FFFFFFFF", $"--screenshot={png}", new Uri(shotPage).AbsoluteUri], false, ct);
        if (!File.Exists(png)) throw new DependencyUnavailableException("The browser did not produce the diagram image.");
        return await File.ReadAllBytesAsync(png, ct);
    }

    private IEnumerable<string> Common(DirectoryInfo dir)
    {
        yield return "--headless=new";
        yield return "--disable-gpu";
        yield return "--hide-scrollbars";
        yield return "--no-first-run";
        yield return "--disable-dev-shm-usage";            // containers give /dev/shm only 64 MB, which can crash Chrome
        yield return "--allow-file-access-from-files";
        yield return $"--user-data-dir={Path.Combine(dir.FullName, "profile")}";
        if (options.Value.NoSandbox) yield return "--no-sandbox";
    }

    private async Task<string> RunAsync(string browser, IEnumerable<string> args, bool captureOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(browser) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new DependencyUnavailableException("The browser could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new DependencyUnavailableException("The browser took too long to draw the diagram.");
        }
        var output = await stdout;
        log.LogDebug("browser exit {Code}: {Err}", process.ExitCode, (await stderr).Length > 400 ? (await stderr)[..400] : await stderr);
        return captureOutput ? output : "";
    }

    // ------------------------------------------------------------------ pages

    private static string DrawHtml(string scriptUri, string mermaid) => $$"""
        <!doctype html><html><head><meta charset="utf-8"></head><body><div id="out"></div>
        <script src="{{scriptUri}}"></script>
        <script>
        (async function () {
          var out = document.getElementById('out');
          try {
            mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'default', fontFamily: 'Arial, Helvetica, sans-serif',
                                 flowchart: { useMaxWidth: false, htmlLabels: false, curve: 'basis' } });
            var source = {{JsonSerializer.Serialize(mermaid)}};
            await mermaid.parse(source);
            var r = await mermaid.render('flow', source);
            out.innerHTML = r.svg;
          } catch (e) { out.textContent = 'MERMAID_ERROR: ' + (e && e.message ? e.message : e); }
        })();
        </script></body></html>
        """;

    private static string ShotHtml(string svg, int width, int height) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>
        html,body{margin:0;background:#fff} #box{padding:{{Margin}}px} #box svg{width:{{width}}px;height:{{height}}px;display:block}
        </style></head><body><div id="box">{{svg}}</div></body></html>
        """;

    // ------------------------------------------------------------------ reading the result

    internal static string ExtractSvg(string dom, out int width, out int height)
    {
        // only the drawing area counts: the page's own script also contains the words "MERMAID_ERROR" and "<svg"
        var area = DrawingArea().Match(dom);
        var inner = area.Success ? area.Groups[1].Value : "";
        var error = ErrorText().Match(inner);
        if (error.Success) throw new DomainException("The diagram could not be drawn: " + System.Net.WebUtility.HtmlDecode(error.Groups[1].Value).Trim(), "diagram_invalid");

        var start = inner.IndexOf("<svg", StringComparison.Ordinal);
        var end = inner.LastIndexOf("</svg>", StringComparison.Ordinal);
        if (start < 0 || end < start) throw new DomainException("The diagram could not be drawn (the browser returned no picture).", "diagram_invalid");
        var svg = inner[start..(end + "</svg>".Length)];

        var view = ViewBox().Match(svg);
        if (!view.Success) throw new DomainException("The drawn diagram has no size.", "diagram_invalid");
        width = Math.Max(1, (int)Math.Ceiling(double.Parse(view.Groups[3].Value, CultureInfo.InvariantCulture)));
        height = Math.Max(1, (int)Math.Ceiling(double.Parse(view.Groups[4].Value, CultureInfo.InvariantCulture)));

        // make it a standalone SVG file: namespace and an explicit size instead of "100%"
        var open = RootTag().Match(svg);
        var tag = open.Value;
        tag = Regex.Replace(tag, @"\s(width|height)=""[^""]*""", "");
        tag = Regex.Replace(tag, @"\sstyle=""[^""]*""", "");
        if (!tag.Contains("xmlns=", StringComparison.Ordinal)) tag = tag.Replace("<svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"", StringComparison.Ordinal);
        tag = tag.Replace("<svg", $"<svg width=\"{width}\" height=\"{height}\"", StringComparison.Ordinal);
        svg = tag + svg[open.Length..];

        XDocument.Parse(svg);                                     // throws if the browser's HTML serialisation is not valid XML
        return svg;
    }

    [GeneratedRegex(@"<div id=""out"">(.*?)</div>\s*<script", RegexOptions.Singleline)] private static partial Regex DrawingArea();
    [GeneratedRegex(@"MERMAID_ERROR:\s*([^<]*)")] private static partial Regex ErrorText();
    [GeneratedRegex(@"viewBox=""\s*(-?[\d.]+)[ ,]+(-?[\d.]+)[ ,]+([\d.]+)[ ,]+([\d.]+)\s*""")] private static partial Regex ViewBox();
    [GeneratedRegex(@"^<svg[^>]*>")] private static partial Regex RootTag();

    // ------------------------------------------------------------------ finding a browser

    public static string? Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var env = Environment.GetEnvironmentVariable("FLOWCAPTURE_BROWSER");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        string[] candidates =
        [
            @"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/snap/bin/chromium",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
