using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Content;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Documents;

/// <summary>Draws Mermaid diagrams on the server and keeps the files. Unchanged source is never drawn twice.</summary>
public sealed class DiagramService(IAppDbContext db, IBlobStore blobs, IDiagramRenderer renderer)
{
    /// <summary>
    /// The workflow picture, drawn by the server itself from the steps and decisions (<see cref="FlowDiagramBuilder"/>): plain SVG text, no browser,
    /// a few milliseconds. The PNG, which needs a browser, is made only when somebody asks for it (<paramref name="needPng"/>).
    /// </summary>
    public async Task<DiagramAsset> EnsureFlowDiagramAsync(Guid projectId, Guid flowId, int versionNo, FlowContent content, bool force, CancellationToken ct, bool needPng = true)
    {
        var asset = await db.DiagramAssets.FirstOrDefaultAsync(a => a.Kind == DiagramKind.Flow && a.FlowId == flowId && a.FlowVersionNo == versionNo, ct);
        var folder = $"diagrams/{projectId:N}/{flowId:N}/v{versionNo}";
        var drawing = FlowDiagramBuilder.Build(content);
        var hash = Hashing.Sha256Hex(FlowDiagramBuilder.Version + drawing.Svg);

        var fresh = asset is not null && !force && asset.Sha256 == hash && asset.SvgPath is not null && await blobs.ExistsAsync(asset.SvgPath, ct);
        if (!fresh)
        {
            var isNew = asset is null;
            asset ??= new DiagramAsset { Kind = DiagramKind.Flow, ProjectId = projectId, FlowId = flowId, FlowVersionNo = versionNo };
            asset.MermaidSource = content.Mermaid ?? "";
            asset.Sha256 = hash;
            asset.Width = drawing.Width;
            asset.Height = drawing.Height;
            asset.MermaidPath = $"{folder}/diagram.mmd";
            asset.SvgPath = $"{folder}/diagram.svg";
            asset.PngPath = null;                                   // the old picture is out of date
            await Save(asset.MermaidPath, System.Text.Encoding.UTF8.GetBytes(asset.MermaidSource), "text/plain", ct);
            await Save(asset.SvgPath, System.Text.Encoding.UTF8.GetBytes(drawing.Svg), "image/svg+xml", ct);
            if (isNew) db.DiagramAssets.Add(asset);
            await db.SaveChangesAsync(ct);
        }
        if (needPng) await EnsurePngAsync(asset!, ct);
        return asset!;
    }

    /// <summary>Makes the PNG of a flow picture from its SVG, once. Needs a browser.</summary>
    private async Task EnsurePngAsync(DiagramAsset asset, CancellationToken ct)
    {
        if (asset.PngPath is not null && await blobs.ExistsAsync(asset.PngPath, ct)) return;
        var svg = await ReadSvgAsync(asset, ct);
        var png = await renderer.RasterizeSvgAsync(svg, asset.Width, asset.Height, ct);
        var tracked = await db.DiagramAssets.FirstAsync(a => a.Id == asset.Id, ct);
        tracked.PngPath = Path.GetDirectoryName(asset.SvgPath!)!.Replace('\\', '/') + "/diagram.png";
        await Save(tracked.PngPath, png, "image/png", ct);
        await db.SaveChangesAsync(ct);
        asset.PngPath = tracked.PngPath;
    }

    /// <summary>The drawn diagram as SVG text (vector: sharp at any size in the PDF).</summary>
    public async Task<string> ReadSvgAsync(DiagramAsset asset, CancellationToken ct)
    {
        await using var stream = await blobs.OpenReadAsync(asset.SvgPath!, ct);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task<DiagramAsset> EnsureCombinedDiagramAsync(Guid projectId, string mermaid, bool force, CancellationToken ct)
    {
        var asset = await db.DiagramAssets.FirstOrDefaultAsync(a => a.Kind == DiagramKind.Combined && a.ProjectId == projectId, ct);
        return await EnsureAsync(asset, () => new DiagramAsset { Kind = DiagramKind.Combined, ProjectId = projectId },
            $"diagrams/{projectId:N}/combined", mermaid, force, true, ct);
    }

    /// <summary>The Mermaid source of the end-to-end diagram: every confirmed flow of the project plus the accepted hand-offs between them. Null when nothing is confirmed.</summary>
    public async Task<string?> BuildCombinedSourceAsync(Guid projectId, CancellationToken ct)
    {
        var flows = await db.Flows.AsNoTracking().Include(f => f.ProjectDepartment!).ThenInclude(p => p.Department)
            .Where(f => f.ProjectDepartment!.ProjectId == projectId && (f.Status == FlowStatus.Confirmed || f.Status == FlowStatus.DocumentsGenerated) && f.ConfirmedVersionNo != null)
            .OrderBy(f => f.ProjectDepartment!.ModuleCode).ThenBy(f => f.MenuName).ToListAsync(ct);
        if (flows.Count == 0) return null;

        var nodes = new List<CombinedMermaidBuilder.FlowNode>();
        var position = new Dictionary<Guid, int>();
        foreach (var f in flows)
        {
            var version = await db.FlowVersions.AsNoTracking().FirstAsync(v => v.FlowId == f.Id && v.VersionNo == f.ConfirmedVersionNo, ct);
            var pd = f.ProjectDepartment!;
            position[f.Id] = nodes.Count + 1;
            nodes.Add(new(nodes.Count + 1, pd.ModuleCode, pd.CustomName ?? pd.Department?.Name ?? pd.ModuleCode, f.MenuName, FlowJson.Deserialize(version.Content)));
        }

        var links = (await db.CrossFlowLinks.AsNoTracking().Where(l => l.ProjectId == projectId && l.Status == LinkStatus.Accepted).ToListAsync(ct))
            .Where(l => position.ContainsKey(l.FromFlowId) && position.ContainsKey(l.ToFlowId))
            .Select(l => new CombinedMermaidBuilder.Link(position[l.FromFlowId], l.FromItemId, position[l.ToFlowId], l.ToItemId,
                string.Join(", ", System.Text.Json.JsonSerializer.Deserialize<string[]>(l.DataPassedJson) ?? []))).ToList();
        return CombinedMermaidBuilder.Build(nodes, links);
    }

    public async Task<DiagramAsset?> FindFlowDiagramAsync(Guid flowId, int versionNo, CancellationToken ct) =>
        await db.DiagramAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Kind == DiagramKind.Flow && a.FlowId == flowId && a.FlowVersionNo == versionNo, ct);

    public async Task<DiagramAsset?> FindCombinedAsync(Guid projectId, CancellationToken ct) =>
        await db.DiagramAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Kind == DiagramKind.Combined && a.ProjectId == projectId, ct);

    public async Task<ImageBlock> ToImageAsync(DiagramAsset asset, string caption, CancellationToken ct)
    {
        await using var stream = await blobs.OpenReadAsync(asset.PngPath!, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return new ImageBlock(ms.ToArray(), asset.Width, asset.Height, caption);
    }

    /// <summary>Opens one of the stored files. Format is svg, png or mmd.</summary>
    public async Task<(Stream Stream, string ContentType, string Extension)> OpenAsync(DiagramAsset asset, string format, CancellationToken ct)
    {
        // a diagram made for a document has no PNG yet: draw it now, once, the first time somebody asks for the picture
        if (format.Equals("png", StringComparison.OrdinalIgnoreCase) && (asset.PngPath is null || !await blobs.ExistsAsync(asset.PngPath, ct)) && asset.SvgPath is not null)
        {
            if (asset.Kind == DiagramKind.Flow) await EnsurePngAsync(asset, ct);
            else if (!string.IsNullOrEmpty(asset.MermaidSource))
            {
                var tracked = await db.DiagramAssets.FirstOrDefaultAsync(a => a.Id == asset.Id, ct);
                if (tracked is not null) asset = await EnsureAsync(tracked, () => tracked, Path.GetDirectoryName(tracked.SvgPath!)!.Replace('\\', '/'), tracked.MermaidSource, true, true, ct);
            }
        }
        var (path, type, ext) = format.ToLowerInvariant() switch
        {
            "svg" => (asset.SvgPath, "image/svg+xml", "svg"),
            "png" => (asset.PngPath, "image/png", "png"),
            "mmd" or "mermaid" => (asset.MermaidPath, "text/plain; charset=utf-8", "mmd"),
            _ => throw new DomainException("Format must be svg, png or mmd.", "invalid_format"),
        };
        if (path is null) throw new NotFoundException("Diagram file", format);
        return (await blobs.OpenReadAsync(path, ct), type, ext);
    }

    private async Task<DiagramAsset> EnsureAsync(DiagramAsset? existing, Func<DiagramAsset> create, string folder, string mermaid, bool force, bool needPng, CancellationToken ct)
    {
        var hash = Hashing.Sha256Hex(mermaid);
        if (existing is not null && !force && existing.Sha256 == hash && existing.SvgPath is not null && await blobs.ExistsAsync(existing.SvgPath, ct)
            && (!needPng || (existing.PngPath is not null && await blobs.ExistsAsync(existing.PngPath, ct))))
            return existing;

        var drawn = await renderer.RenderAsync(mermaid, needPng, ct);

        var asset = existing ?? create();
        asset.MermaidSource = mermaid;
        asset.Sha256 = hash;
        asset.Width = drawn.Width;
        asset.Height = drawn.Height;
        asset.MermaidPath = $"{folder}/diagram.mmd";
        asset.SvgPath = $"{folder}/diagram.svg";
        asset.PngPath = drawn.Png is null ? null : $"{folder}/diagram.png";
        await Save(asset.MermaidPath, System.Text.Encoding.UTF8.GetBytes(mermaid), "text/plain", ct);
        await Save(asset.SvgPath, System.Text.Encoding.UTF8.GetBytes(drawn.Svg), "image/svg+xml", ct);
        if (drawn.Png is not null) await Save(asset.PngPath!, drawn.Png, "image/png", ct);

        if (existing is null) db.DiagramAssets.Add(asset);
        await db.SaveChangesAsync(ct);
        return asset;
    }

    private async Task Save(string path, byte[] bytes, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream(bytes);
        await blobs.SaveAsync(path, ms, contentType, ct);
    }
}
