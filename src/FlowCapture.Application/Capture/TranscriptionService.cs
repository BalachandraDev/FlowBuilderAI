using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowCapture.Application.Capture;

/// <summary>Transcribes an uploaded recording and masks PHI. Runs in a background job.</summary>
public sealed class TranscriptionService(
    IAppDbContext db, IBlobStore blobs, ISpeechToTextService stt, IImageTextService imageText, IPhiMasker masker, IPushSender push,
    Services.AiUsageRecorder usage, IClock clock, ILogger<TranscriptionService> log)
{
    private async Task<Guid?> ProjectOfAsync(Flow flow, CancellationToken ct) =>
        await db.ProjectDepartments.AsNoTracking().Where(p => p.Id == flow.ProjectDepartmentId).Select(p => (Guid?)p.ProjectId).FirstOrDefaultAsync(ct);

    /// <summary>Reads the text of a photo of notes. Same states, masking and notification as a transcription.</summary>
    public async Task ProcessImageAsync(Guid entryId, CancellationToken ct)
    {
        var entry = await db.CaptureEntries.Include(e => e.Transcript).Include(e => e.Flow)
            .FirstOrDefaultAsync(e => e.Id == entryId && !e.IsDeleted && e.Kind == EntryKind.Image, ct);
        if (entry is null || string.IsNullOrEmpty(entry.ImageBlobPath)) return;

        var transcript = entry.Transcript;
        if (transcript is null) db.Transcripts.Add(transcript = new Transcript { EntryId = entry.Id });
        if (transcript.Status == TranscriptStatus.Ready && transcript.Error is null && !string.IsNullOrEmpty(transcript.RawText)) return;

        transcript.Status = TranscriptStatus.Processing;
        transcript.Error = null;
        await db.SaveChangesAsync(ct);

        try
        {
            await using var stream = await blobs.OpenReadAsync(entry.ImageBlobPath, ct);
            var result = await imageText.ReadAsync(stream, entry.ImageContentType ?? "image/jpeg", ct);
            var masked = await masker.MaskAsync(result.Text, ct);

            transcript.Provider = result.Provider;
            transcript.Language = entry.Language;
            transcript.RawText = result.Text;
            transcript.MaskedText = masked.Text;
            transcript.MaskedCount = masked.Count;
            transcript.UserEditedText = null;
            transcript.Status = TranscriptStatus.Ready;
            await db.SaveChangesAsync(ct);
            await db.TouchFlowAsync(entry.FlowId, clock.UtcNow, ct);
            if (result.InputTokens + result.OutputTokens > 0)
                await usage.RecordAsync(AiUsageKind.Photo, entry.CapturedBy, await ProjectOfAsync(entry.Flow, ct), entry.FlowId, null, result.Model, result.InputTokens, result.OutputTokens, 0, true, ct);
        }
        catch (Exception e) when (e is SttException or AiServiceException)
        {
            var retryable = e is SttException { Retryable: true } or AiServiceException { Retryable: true };
            log.LogWarning(e, "Reading the photo of entry {EntryId} failed (retryable: {Retryable})", entryId, retryable);
            transcript.Status = TranscriptStatus.Failed;
            transcript.Error = e.Message.Length > 500 ? e.Message[..500] : e.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            if (retryable) throw;
        }
        finally
        {
            await NotifyAsync(entry.CapturedBy, entryId, transcript.Status);
        }
    }

    public async Task ProcessAsync(Guid entryId, CancellationToken ct)
    {
        var entry = await db.CaptureEntries.Include(e => e.Audio).Include(e => e.Transcript).Include(e => e.Flow)
            .FirstOrDefaultAsync(e => e.Id == entryId && !e.IsDeleted, ct);
        if (entry?.Audio is not { UploadState: UploadState.Complete } audio) return;

        var transcript = entry.Transcript;
        if (transcript is null) db.Transcripts.Add(transcript = new Transcript { EntryId = entry.Id });
        if (transcript.Status == TranscriptStatus.Ready && transcript.Error is null && !string.IsNullOrEmpty(transcript.RawText)) return;   // already done: job retried after success

        transcript.Status = TranscriptStatus.Processing;
        transcript.Error = null;
        await db.SaveChangesAsync(ct);

        var languages = await db.Languages.AsNoTracking().Where(l => l.IsActive).Select(l => l.SttLocale).ToListAsync(ct);
        var locales = new[] { entry.Language }.Concat(languages).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        try
        {
            await using var stream = await blobs.OpenReadAsync(audio.BlobPath, ct);
            var result = await stt.TranscribeAsync(stream, audio.ContentType, Path.GetFileName(audio.BlobPath), locales, ct);
            var masked = await masker.MaskAsync(result.Text, ct);

            transcript.Provider = result.Provider;
            transcript.Language = result.Locale;
            transcript.RawText = result.Text;
            transcript.MaskedText = masked.Text;
            transcript.MaskedCount = masked.Count;
            transcript.UserEditedText = null;
            transcript.AvgConfidence = result.AvgConfidence;
            transcript.SegmentsJson = result.SegmentsJson;
            transcript.Status = TranscriptStatus.Ready;
            await db.SaveChangesAsync(ct);
            await db.TouchFlowAsync(entry.FlowId, clock.UtcNow, ct);
            if (result.InputTokens + result.OutputTokens > 0)
                await usage.RecordAsync(AiUsageKind.Voice, entry.CapturedBy, await ProjectOfAsync(entry.Flow, ct), entry.FlowId, ProviderLabel(result.Provider), result.Model, result.InputTokens, result.OutputTokens, 0, true, ct);
        }
        catch (SttException e)
        {
            log.LogWarning(e, "Transcription of entry {EntryId} failed (retryable: {Retryable})", entryId, e.Retryable);
            transcript.Status = TranscriptStatus.Failed;
            transcript.Error = e.Message.Length > 500 ? e.Message[..500] : e.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            if (e.Retryable) throw;           // let the job runner retry with back-off
        }
        finally
        {
            await NotifyAsync(entry.CapturedBy, entryId, transcript.Status);
        }
    }

    /// <summary>The name shown on the AI usage page for the service that did the work (null keeps the one chosen in the settings).</summary>
    private static string? ProviderLabel(string? provider) => provider?.ToLowerInvariant() switch { "groq" => "Groq", "gemini" => "Gemini", _ => null };

    public async Task<TranscriptDto> SaveEditedAsync(CaptureEntry entry, string text, CancellationToken ct)
    {
        if (entry.Transcript is not { Status: TranscriptStatus.Ready } transcript)
            throw new DomainException("There is no transcript to edit yet.", "no_transcript");
        if (string.IsNullOrWhiteSpace(text)) throw new DomainException("The transcript cannot be empty.", "empty_transcript");
        if (text.Length > 200_000) throw new DomainException("The transcript is too long.", "text_too_long");

        // User edits are re-masked so a name typed while correcting the text never reaches the AI.
        var masked = await masker.MaskAsync(text, ct);
        transcript.UserEditedText = masked.Text;
        transcript.MaskedCount = Math.Max(transcript.MaskedCount, masked.Count);
        await db.SaveChangesAsync(ct);
        await db.TouchFlowAsync(entry.FlowId, clock.UtcNow, ct);
        return transcript.ToDto();
    }

    private async Task NotifyAsync(Guid userId, Guid entryId, TranscriptStatus status)
    {
        try
        {
            await push.SendToUserAsync(userId,
                status == TranscriptStatus.Ready ? "Transcript ready" : "Transcription failed",
                status == TranscriptStatus.Ready ? "Review and correct the transcript before analysing." : "A recording could not be transcribed.",
                new Dictionary<string, string> { ["type"] = "transcript", ["entryId"] = entryId.ToString() }, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Push notification for entry {EntryId} failed", entryId);
        }
    }
}
