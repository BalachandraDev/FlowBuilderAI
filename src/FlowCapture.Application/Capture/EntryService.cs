using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Capture;

public sealed record EntryWriteResult(EntryDto Entry, bool Duplicate);

/// <summary>Create, edit and delete capture entries. Shared by the REST endpoints and the offline sync push.</summary>
public sealed class EntryService(IAppDbContext db, IProjectAccess access, ICurrentUser user, IClock clock, Services.ISettingsService settings)
{
    private const int MaxTextLength = 200_000;

    public async Task<EntryWriteResult> CreateAsync(Guid flowId, CreateEntryRequest req, CancellationToken ct)
    {
        var flow = await db.Flows.Include(f => f.ProjectDepartment).FirstOrDefaultAsync(f => f.Id == flowId, ct)
                   ?? throw new NotFoundException("Flow", flowId);
        var pd = flow.ProjectDepartment!;
        await access.EnsureCanCaptureAsync(pd.ProjectId, pd.Id, ct);
        var uid = user.RequireUserId();

        var existing = await db.CaptureEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == req.Id, ct);
        if (existing is not null)
        {
            if (existing.CapturedBy != uid || existing.FlowId != flowId)
                throw new ConflictException("An entry with this id already exists.");
            return new EntryWriteResult(await GetDtoAsync(existing.Id, ct), true);
        }

        if (!Enum.TryParse<EntryKind>(req.Kind, true, out var kind))
            throw new DomainException("Kind must be Text or Voice.", "invalid_kind");
        if (kind == EntryKind.Text && string.IsNullOrWhiteSpace(req.RawText))
            throw new DomainException("A text entry needs some text.", "empty_entry");
        // A photo or a recording goes to the AI as it is: patient names or numbers in it cannot be masked like typed text. The administrator can switch that off.
        if (kind != EntryKind.Text && !await settings.GetFlagAsync(Services.SettingsService.FlagAllowMedia, true, ct))
            throw new DomainException("Photos and voice recordings are switched off by the administrator. Type the note instead.", "media_blocked");
        if ((req.RawText?.Length ?? 0) > MaxTextLength || (req.Notes?.Length ?? 0) > MaxTextLength)
            throw new DomainException("The text is too long.", "text_too_long");
        if (string.IsNullOrWhiteSpace(req.Language) || req.Language.Length > 10)
            throw new DomainException("Language is required (for example en-IN).", "invalid_language");

        for (var attempt = 1; ; attempt++)
        {
            var next = (await db.CaptureEntries.Where(e => e.FlowId == flowId).MaxAsync(e => (int?)e.RefNo, ct) ?? 0) + 1;
            var entry = new CaptureEntry
            {
                Id = req.Id, FlowId = flowId, RefNo = next, CapturedBy = uid,
                CapturedAt = req.CapturedAt == default ? clock.UtcNow : req.CapturedAt.ToUniversalTime(),
                Kind = kind, Language = req.Language.Trim(), RawText = req.RawText, Notes = req.Notes,
                IntervieweeName = req.IntervieweeName, IntervieweeRole = req.IntervieweeRole,
            };
            db.CaptureEntries.Add(entry);
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                // Another entry took this ref number at the same moment. Try the next number.
                db.Entry(entry).State = EntityState.Detached;
                await Task.Delay(Random.Shared.Next(10, 60), ct);
            }
        }
        await db.TouchFlowAsync(flowId, clock.UtcNow, ct);
        return new EntryWriteResult(await GetDtoAsync(req.Id, ct), false);
    }

    public async Task<EntryDto> UpdateAsync(Guid entryId, UpdateEntryRequest req, string? ifMatch, CancellationToken ct)
    {
        var entry = await LoadForWriteAsync(entryId, ct);
        if (ifMatch is not null && ifMatch != entry.ConcurrencyStamp)
            throw new ConflictException("This entry was changed on the server.", await GetDtoAsync(entryId, ct));

        if (entry.Kind == EntryKind.Text && string.IsNullOrWhiteSpace(req.RawText))
            throw new DomainException("A text entry needs some text.", "empty_entry");
        if ((req.RawText?.Length ?? 0) > MaxTextLength || (req.Notes?.Length ?? 0) > MaxTextLength)
            throw new DomainException("The text is too long.", "text_too_long");

        if (ifMatch is not null) db.Entry(entry).Property(e => e.ConcurrencyStamp).OriginalValue = ifMatch;
        entry.Language = string.IsNullOrWhiteSpace(req.Language) ? entry.Language : req.Language.Trim();
        entry.RawText = req.RawText;
        entry.Notes = req.Notes;
        entry.IntervieweeName = req.IntervieweeName;
        entry.IntervieweeRole = req.IntervieweeRole;

        try { await db.SaveChangesAsync(ct); await db.TouchFlowAsync(entry.FlowId, clock.UtcNow, ct); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw new ConflictException("This entry was changed on the server.", await GetDtoAsync(entryId, ct));
        }
        return await GetDtoAsync(entryId, ct);
    }

    public async Task DeleteAsync(Guid entryId, CancellationToken ct)
    {
        var entry = await LoadForWriteAsync(entryId, ct);
        var flow = entry.Flow!;
        if (flow.Status >= FlowStatus.Confirmed)
        {
            var remaining = await db.CaptureEntries.CountAsync(e => e.FlowId == flow.Id && !e.IsDeleted && e.Id != entryId, ct);
            if (remaining == 0)
                throw new DomainException("This is the only input of a confirmed flow and cannot be deleted. Re-open the flow first.", "last_entry");
        }
        entry.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        await db.TouchFlowAsync(flow.Id, clock.UtcNow, ct);
    }

    public async Task<EntryDto> GetDtoAsync(Guid entryId, CancellationToken ct) =>
        await db.CaptureEntries.AsNoTracking().Where(e => e.Id == entryId).Select(Mapping.EntryProjection).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Entry", entryId);

    /// <summary>Loads an entry with its flow, checking that the caller may change it (own entry, or Lead/Admin).</summary>
    public async Task<CaptureEntry> LoadForWriteAsync(Guid entryId, CancellationToken ct)
    {
        var entry = await db.CaptureEntries
            .Include(e => e.Flow!).ThenInclude(f => f.ProjectDepartment)
            .Include(e => e.Audio).Include(e => e.Transcript)
            .FirstOrDefaultAsync(e => e.Id == entryId && !e.IsDeleted, ct) ?? throw new NotFoundException("Entry", entryId);
        var pd = entry.Flow!.ProjectDepartment!;
        var role = await access.GetRoleAsync(pd.ProjectId, ct);
        if (role == ProjectRole.Lead) return entry;
        await access.EnsureCanCaptureAsync(pd.ProjectId, pd.Id, ct);
        if (entry.CapturedBy != user.RequireUserId())
            throw new ForbiddenException("You can only change your own entries.");
        return entry;
    }

    /// <summary>Loads an entry for reading (any project member).</summary>
    public async Task<CaptureEntry> LoadForReadAsync(Guid entryId, CancellationToken ct)
    {
        var entry = await db.CaptureEntries
            .Include(e => e.Flow!).ThenInclude(f => f.ProjectDepartment)
            .Include(e => e.Audio).Include(e => e.Transcript)
            .FirstOrDefaultAsync(e => e.Id == entryId && !e.IsDeleted, ct) ?? throw new NotFoundException("Entry", entryId);
        await access.EnsureMemberAsync(entry.Flow!.ProjectDepartment!.ProjectId, ct);
        return entry;
    }
}
