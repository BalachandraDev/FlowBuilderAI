using FlowCapture.Infrastructure.Diagrams;
using FlowCapture.Infrastructure.Documents;
using Xunit;

namespace FlowCapture.TestSupport;

/// <summary>Runs only where a browser (diagrams) is installed; skipped, not failed, elsewhere.</summary>
public sealed class RequiresBrowserFactAttribute : FactAttribute
{
    public RequiresBrowserFactAttribute()
    {
        if (ChromiumDiagramRenderer.Resolve(null) is null) Skip = "No Chrome, Chromium or Edge installed.";
    }
}

/// <summary>Runs only after the web app has been built into the API's wwwroot (npm run build in the web folder); skipped, not failed, before that.</summary>
public sealed class RequiresWebAppFactAttribute : FactAttribute
{
    public static string? IndexPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "FlowCapture.Api", "wwwroot", "index.html");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public RequiresWebAppFactAttribute()
    {
        if (IndexPath() is null) Skip = "The web app is not built. Run npm run build in the web folder.";
    }
}

/// <summary>Runs only where both a browser (diagrams) and LibreOffice (PDF) are installed; skipped, not failed, elsewhere.</summary>
public sealed class RequiresDocumentToolsFactAttribute : FactAttribute
{
    public RequiresDocumentToolsFactAttribute()
    {
        if (ChromiumDiagramRenderer.Resolve(null) is null) Skip = "No Chrome, Chromium or Edge installed.";
        else if (LibreOfficePdfConverter.Resolve(null) is null) Skip = "LibreOffice is not installed.";
    }
}
