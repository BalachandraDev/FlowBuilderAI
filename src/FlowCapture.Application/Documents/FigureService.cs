using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Documents;

/// <summary>One picture ready to print: its place, its caption and the picture itself.</summary>
public sealed record GuideFigure(int No, string Caption, string? StepId, string ContentType, byte[] Bytes);

/// <summary>
/// Pictures attached to a request: screenshots of the screens, photos of forms. They are kept as files, can be listed, captioned, linked to a step,
/// reordered and removed, and are printed in the implementation document. The AI never reads them, so they cost no tokens.
/// </summary>
public sealed class FigureService(IAppDbContext db, IProjectAccess access, IBlobStore blobs, ICurrentUser user, IClock clock)
{
    public const long MaxBytes = 8 * 1024 * 1024;
    public const int MaxPerFlow = 20;
    public const int MaxCaption = 300;

    /// <summary>Only plain pictures a browser and the PDF can show. The file is recognised by its first bytes, not by what the sender calls it.</summary>
    public static string? TypeOf(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A ? "image/png"
        : b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P' ? "image/webp"
        : null;

    private static string Extension(string type) => type switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };

    private async Task<(Flow Flow, ProjectDepartment Dept)> LoadFlowAsync(Guid flowId, bool write, CancellationToken ct)
    {
        var flow = await db.Flows.Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == flowId, ct) ?? throw new NotFoundException("Flow", flowId);
        var pd = flow.ProjectDepartment!;
        if (write) await access.EnsureCanCaptureAsync(pd.ProjectId, pd.Id, ct); else await access.EnsureMemberAsync(pd.ProjectId, ct);
        return (flow, pd);
    }

    public async Task<IReadOnlyList<FigureDto>> ListAsync(Guid flowId, CancellationToken ct)
    {
        await LoadFlowAsync(flowId, write: false, ct);
        var rows = await db.FlowFigures.AsNoTracking().Where(x => x.FlowId == flowId).OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        var ids = rows.Select(r => r.CreatedBy).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return rows.Select(r => ToDto(r, r.CreatedBy is { } id && names.TryGetValue(id, out var n) ? n : "")).ToList();
    }

    public async Task<FigureDto> AddAsync(Guid flowId, Stream file, string? fileName, string? caption, string? stepId, CancellationToken ct)
    {
        var (flow, pd) = await LoadFlowAsync(flowId, write: true, ct);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) throw new DomainException("The picture is empty.", "figure_empty");
        if (buffer.Length > MaxBytes) throw new DomainException($"The picture is too large. The most that can be attached is {MaxBytes / (1024 * 1024)} MB.", "figure_size");
        var bytes = buffer.ToArray();
        var type = TypeOf(bytes) ?? throw new DomainException("Attach a JPEG, PNG or WebP picture.", "figure_type");

        var count = await db.FlowFigures.CountAsync(x => x.FlowId == flowId, ct);
        if (count >= MaxPerFlow) throw new DomainException($"A request can have up to {MaxPerFlow} pictures. Remove one first.", "figure_limit");
        var next = (await db.FlowFigures.Where(x => x.FlowId == flowId).MaxAsync(x => (int?)x.SortOrder, ct) ?? 0) + 1;

        var figure = new FlowFigure
        {
            FlowId = flowId, SortOrder = next, Caption = Clean(caption, MaxCaption), StepId = CleanStep(stepId),
            ContentType = type, SizeBytes = bytes.Length, OriginalName = Clean(fileName, 200),
        };
        figure.BlobPath = $"figures/{pd.ProjectId:N}/{flowId:N}/{figure.Id:N}{Extension(type)}";
        buffer.Position = 0;
        await blobs.SaveAsync(figure.BlobPath, buffer, type, ct);
        db.FlowFigures.Add(figure);
        await db.SaveChangesAsync(ct);
        await db.TouchFlowAsync(flow.Id, clock.UtcNow, ct);
        return ToDto(figure, await NameOfAsync(user.UserId, ct));
    }

    public async Task<FigureDto> UpdateAsync(Guid figureId, string caption, string? stepId, CancellationToken ct)
    {
        var figure = await db.FlowFigures.FirstOrDefaultAsync(x => x.Id == figureId, ct) ?? throw new NotFoundException("Picture", figureId);
        await LoadFlowAsync(figure.FlowId, write: true, ct);
        figure.Caption = Clean(caption, MaxCaption);
        figure.StepId = CleanStep(stepId);
        await db.SaveChangesAsync(ct);
        await db.TouchFlowAsync(figure.FlowId, clock.UtcNow, ct);
        return ToDto(figure, await NameOfAsync(figure.CreatedBy, ct));
    }

    /// <summary>Moves a picture one place earlier or later in the printed order.</summary>
    public async Task<IReadOnlyList<FigureDto>> MoveAsync(Guid figureId, int direction, CancellationToken ct)
    {
        var figure = await db.FlowFigures.AsNoTracking().FirstOrDefaultAsync(x => x.Id == figureId, ct) ?? throw new NotFoundException("Picture", figureId);
        await LoadFlowAsync(figure.FlowId, write: true, ct);
        var all = await db.FlowFigures.Where(x => x.FlowId == figure.FlowId).OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        var i = all.FindIndex(x => x.Id == figureId);
        var j = i + Math.Sign(direction);
        if (i >= 0 && j >= 0 && j < all.Count) (all[i], all[j]) = (all[j], all[i]);
        for (var k = 0; k < all.Count; k++) all[k].SortOrder = k + 1;
        await db.SaveChangesAsync(ct);
        return await ListAsync(figure.FlowId, ct);
    }

    public async Task DeleteAsync(Guid figureId, CancellationToken ct)
    {
        var figure = await db.FlowFigures.FirstOrDefaultAsync(x => x.Id == figureId, ct) ?? throw new NotFoundException("Picture", figureId);
        await LoadFlowAsync(figure.FlowId, write: true, ct);
        db.FlowFigures.Remove(figure);
        await db.SaveChangesAsync(ct);
        try { await blobs.DeleteAsync(figure.BlobPath, ct); } catch (IOException) { /* the file stays behind; nothing refers to it */ }
        await db.TouchFlowAsync(figure.FlowId, clock.UtcNow, ct);
    }

    /// <summary>Opens the stored picture. Any member of the project may look at it.</summary>
    public async Task<(Stream Stream, string ContentType)> OpenAsync(Guid figureId, CancellationToken ct)
    {
        var figure = await db.FlowFigures.AsNoTracking().FirstOrDefaultAsync(x => x.Id == figureId, ct) ?? throw new NotFoundException("Picture", figureId);
        await LoadFlowAsync(figure.FlowId, write: false, ct);
        return (await blobs.OpenReadAsync(figure.BlobPath, ct), figure.ContentType);
    }

    /// <summary>The pictures of a request, with their bytes, in printed order, for the document. The caller has already checked who may make the document.</summary>
    public async Task<IReadOnlyList<GuideFigure>> ForDocumentAsync(Guid flowId, CancellationToken ct)
    {
        var rows = await db.FlowFigures.AsNoTracking().Where(x => x.FlowId == flowId).OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        var list = new List<GuideFigure>();
        foreach (var r in rows)
        {
            try
            {
                await using var s = await blobs.OpenReadAsync(r.BlobPath, ct);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, ct);
                list.Add(new GuideFigure(list.Count + 1, r.Caption, r.StepId, r.ContentType, ms.ToArray()));
            }
            catch (IOException) { /* a missing file is left out of the document rather than stopping it */ }
        }
        return list;
    }

    private async Task<string> NameOfAsync(Guid? userId, CancellationToken ct) =>
        userId is null ? "" : await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "";

    private static string Clean(string? text, int max)
    {
        var t = System.Text.RegularExpressions.Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        return t.Length > max ? t[..max] : t;
    }

    private static string? CleanStep(string? stepId) => string.IsNullOrWhiteSpace(stepId) ? null : Clean(stepId, 40);

    private static FigureDto ToDto(FlowFigure f, string by) => new(f.Id, f.FlowId, f.Caption, f.StepId, f.SortOrder, f.ContentType, f.SizeBytes, f.OriginalName, by, f.CreatedAt);
}