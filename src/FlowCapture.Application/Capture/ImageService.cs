using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Capture;

/// <summary>Photos of handwritten or printed notes. The photo is stored, then read into text in a background job.</summary>
public sealed class ImageService(IAppDbContext db, EntryService entries, IBlobStore blobs, IJobScheduler jobs, IClock clock)
{
    public const long MaxBytes = 8 * 1024 * 1024;
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg", ["image/jpg"] = ".jpg", ["image/png"] = ".png", ["image/webp"] = ".webp", ["image/heic"] = ".heic", ["image/heif"] = ".heif",
    };

    public async Task<EntryDto> AddAsync(Guid flowId, Guid entryId, Stream image, string contentType, string language, string? notes, CancellationToken ct)
    {
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (!Types.TryGetValue(type, out var extension))
            throw new DomainException("Upload a JPEG, PNG or WebP photo.", "image_type");

        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) throw new DomainException("The photo is empty.", "image_empty");
        if (buffer.Length > MaxBytes) throw new DomainException($"The photo is too large. The most that can be uploaded is {MaxBytes / (1024 * 1024)} MB.", "image_size");

        // The entry is created first, with the same rules as any other note: who may add to this flow, ref numbers, retries with the same id.
        var created = await entries.CreateAsync(flowId, new CreateEntryRequest(entryId, nameof(EntryKind.Image), clock.UtcNow, language, null, notes, null, null), ct);
        if (created.Duplicate) return created.Entry;

        var entry = await db.CaptureEntries.Include(e => e.Flow).Include(e => e.Transcript).FirstAsync(e => e.Id == entryId, ct);
        var path = $"images/{entry.Id:N}{extension}";
        buffer.Position = 0;
        await blobs.SaveAsync(path, buffer, type, ct);
        entry.ImageBlobPath = path;
        entry.ImageContentType = type == "image/jpg" ? "image/jpeg" : type;
        db.Transcripts.Add(new Transcript { EntryId = entry.Id, Status = TranscriptStatus.Queued, Language = entry.Language });
        await db.SaveChangesAsync(ct);
        await db.TouchFlowAsync(entry.FlowId, clock.UtcNow, ct);

        jobs.EnqueueImageRead(entry.Id);
        return await entries.GetDtoAsync(entry.Id, ct);
    }

    /// <summary>Opens the stored photo. Any member of the project may look at it.</summary>
    public async Task<(Stream Stream, string ContentType)> OpenAsync(Guid entryId, CancellationToken ct)
    {
        var entry = await entries.LoadForReadAsync(entryId, ct);
        if (entry.Kind != EntryKind.Image || string.IsNullOrEmpty(entry.ImageBlobPath)) throw new NotFoundException("Photo for entry", entryId);
        return (await blobs.OpenReadAsync(entry.ImageBlobPath, ct), entry.ImageContentType ?? "image/jpeg");
    }
}
