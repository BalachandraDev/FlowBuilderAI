using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Capture;

/// <summary>
/// An Excel workbook or a PDF attached as a note. The server reads the text out of it (no AI, so no free tokens are used) and saves it as a
/// typed note that says which file it came from. It then goes through analysis like every other note.
/// </summary>
public sealed class FileNoteService(IAppDbContext db, IProjectAccess access, EntryService entries, IFileTextExtractor extractor, IClock clock)
{
    public const long MaxBytes = 12 * 1024 * 1024;
    public const int MaxChars = 200_000;

    public async Task<EntryDto> AddAsync(Guid flowId, Guid entryId, Stream file, string fileName, string language, CancellationToken ct)
    {
        // who may add to this request is decided first, before any file is opened
        var flow = await db.Flows.AsNoTracking().Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == flowId, ct) ?? throw new NotFoundException("Flow", flowId);
        await access.EnsureCanCaptureAsync(flow.ProjectDepartment!.ProjectId, flow.ProjectDepartmentId, ct);

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) throw new DomainException("The file is empty.", "file_empty");
        if (buffer.Length > MaxBytes) throw new DomainException($"The file is too large. The most that can be attached is {MaxBytes / (1024 * 1024)} MB.", "file_size");
        buffer.Position = 0;

        var text = extractor.Extract(buffer, fileName);
        if (text.Length > MaxChars)
            throw new DomainException($"This file has too much text for one note ({text.Length / 1000} thousand characters; the most is {MaxChars / 1000} thousand). Split it into parts and attach them together.", "file_too_long");

        var name = Path.GetFileName(fileName);
        var created = await entries.CreateAsync(flowId, new CreateEntryRequest(entryId, nameof(EntryKind.Text), clock.UtcNow, language, text, $"From the file {name}", null, null), ct);
        return created.Entry;
    }
}
