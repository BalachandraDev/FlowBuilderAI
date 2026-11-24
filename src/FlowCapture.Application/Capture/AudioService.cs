using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlowCapture.Application.Capture;

/// <summary>Resumable chunked audio upload. The upload id is the AudioFile id.</summary>
public sealed class AudioService(
    IAppDbContext db, EntryService entries, IBlobStore blobs, IJobScheduler jobs, IClock clock,
    Services.ISettingsService settings, IOptions<CaptureOptions> options)
{
    private const int MinChunkBytes = 16 * 1024;

    public async Task<AudioUploadDto> InitAsync(InitAudioUploadRequest req, CancellationToken ct)
    {
        var entry = await entries.LoadForWriteAsync(req.EntryId, ct);
        var cfg = options.Value;
        if (entry.Kind != EntryKind.Voice) throw new DomainException("Only voice entries can have audio.", "not_voice");
        if (req.SizeBytes <= 0 || req.SizeBytes > cfg.MaxAudioBytes)
            throw new DomainException($"Audio size must be between 1 byte and {cfg.MaxAudioBytes / (1024 * 1024)} MB.", "audio_size");
        var maxMinutes = await settings.GetAudioMaxMinutesAsync(ct);
        if (req.DurationSec > maxMinutes * 60 + 5)
            throw new DomainException($"Recordings can be at most {maxMinutes} minutes.", "audio_too_long");
        if (req.ChunkSizeBytes < MinChunkBytes || req.ChunkSizeBytes > cfg.MaxChunkBytes)
            throw new DomainException($"Chunk size must be between {MinChunkBytes} and {cfg.MaxChunkBytes} bytes.", "chunk_size");
        if (req.Sha256.Length != 64 || !req.Sha256.All(Uri.IsHexDigit))
            throw new DomainException("Sha256 must be 64 hex characters.", "sha256");
        if (!(req.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || req.ContentType == "application/octet-stream"))
            throw new DomainException("Content type must be an audio type.", "content_type");

        var sha = req.Sha256.ToLowerInvariant();
        var audio = entry.Audio;
        if (audio is not null)
        {
            if (audio.UploadState == UploadState.Complete)
            {
                if (audio.Sha256 == sha) return ToDto(audio);
                throw new ConflictException("This entry already has a different recording.");
            }
            if (audio.Sha256 == sha && audio.SizeBytes == req.SizeBytes && audio.ChunkSizeBytes == req.ChunkSizeBytes)
                return ToDto(audio);                       // resume
            await DeleteChunksAsync(audio, ct);            // different file: start again
        }
        else
        {
            audio = new AudioFile { EntryId = entry.Id };
            db.AudioFiles.Add(audio);
        }

        audio.ContentType = req.ContentType;
        audio.DurationSec = req.DurationSec;
        audio.SizeBytes = req.SizeBytes;
        audio.Sha256 = sha;
        audio.ChunkSizeBytes = req.ChunkSizeBytes;
        audio.TotalChunks = (int)Math.Ceiling(req.SizeBytes / (double)req.ChunkSizeBytes);
        audio.UploadState = UploadState.Initiated;
        audio.ReceivedChunks = "";
        audio.BlobPath = "";
        await db.SaveChangesAsync(ct);
        return ToDto(audio);
    }

    public async Task<AudioUploadDto> GetAsync(Guid uploadId, CancellationToken ct)
    {
        var audio = await LoadAsync(uploadId, ct);
        return ToDto(audio);
    }

    public async Task<AudioUploadDto> PutChunkAsync(Guid uploadId, int chunkNo, Stream body, CancellationToken ct)
    {
        var audio = await LoadAsync(uploadId, ct);
        if (audio.UploadState == UploadState.Complete) return ToDto(audio);
        if (chunkNo < 0 || chunkNo >= audio.TotalChunks)
            throw new DomainException($"Chunk number must be between 0 and {audio.TotalChunks - 1}.", "chunk_number");

        var expected = chunkNo == audio.TotalChunks - 1
            ? audio.SizeBytes - (long)(audio.TotalChunks - 1) * audio.ChunkSizeBytes
            : audio.ChunkSizeBytes;

        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, ct);
        if (buffer.Length != expected)
            throw new DomainException($"Chunk {chunkNo} must be exactly {expected} bytes but was {buffer.Length}.", "chunk_length");

        buffer.Position = 0;
        await blobs.SaveAsync(ChunkPath(audio, chunkNo), buffer, "application/octet-stream", ct);

        var received = Received(audio);
        if (received.Add(chunkNo))
            audio.ReceivedChunks = string.Join(',', received.OrderBy(x => x));
        audio.UploadState = UploadState.Uploading;
        await db.SaveChangesAsync(ct);
        return ToDto(audio);
    }

    public async Task<AudioUploadDto> CompleteAsync(Guid uploadId, CancellationToken ct)
    {
        var audio = await LoadAsync(uploadId, ct);
        if (audio.UploadState == UploadState.Complete) return ToDto(audio);

        var received = Received(audio);
        var missing = Enumerable.Range(0, audio.TotalChunks).Where(i => !received.Contains(i)).ToList();
        if (missing.Count > 0)
            throw new DomainException($"{missing.Count} chunk(s) are missing: {string.Join(", ", missing.Take(10))}.", "chunks_missing");

        var entry = await db.CaptureEntries.Include(e => e.Flow).Include(e => e.Transcript).FirstAsync(e => e.Id == audio.EntryId, ct);
        var finalPath = $"audio/{audio.EntryId:N}/{audio.Id:N}{ExtensionFor(audio.ContentType)}";
        var temp = Path.GetTempFileName();
        try
        {
            string actualSha;
            long total = 0;
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
            {
                for (var i = 0; i < audio.TotalChunks; i++)
                {
                    await using var chunk = await blobs.OpenReadAsync(ChunkPath(audio, i), ct);
                    var buf = new byte[81920];
                    int n;
                    while ((n = await chunk.ReadAsync(buf, ct)) > 0)
                    {
                        hash.AppendData(buf, 0, n);
                        await file.WriteAsync(buf.AsMemory(0, n), ct);
                        total += n;
                    }
                }
                actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (total != audio.SizeBytes || actualSha != audio.Sha256)
                {
                    audio.UploadState = UploadState.Failed;
                    await db.SaveChangesAsync(ct);
                    throw new DomainException("The uploaded audio is corrupt (size or checksum mismatch). Upload it again.", "checksum_mismatch");
                }
                file.Position = 0;
                await blobs.SaveAsync(finalPath, file, audio.ContentType, ct);
            }
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }

        await DeleteChunksAsync(audio, ct);
        audio.BlobPath = finalPath;
        audio.UploadState = UploadState.Complete;

        var transcript = entry.Transcript;
        if (transcript is null)
            db.Transcripts.Add(transcript = new Transcript { EntryId = entry.Id });
        transcript.Status = TranscriptStatus.Queued;
        transcript.Language = entry.Language;
        transcript.Error = null;
        await db.SaveChangesAsync(ct);
        await db.TouchFlowAsync(entry.FlowId, clock.UtcNow, ct);

        jobs.EnqueueTranscription(entry.Id);
        return ToDto(audio);
    }

    /// <summary>Opens the stored recording for playback. Any project member may listen.</summary>
    public async Task<(Stream Stream, string ContentType)> OpenAsync(Guid entryId, CancellationToken ct)
    {
        var entry = await entries.LoadForReadAsync(entryId, ct);
        var audio = entry.Audio;
        if (audio is null || audio.UploadState != UploadState.Complete)
            throw new NotFoundException("Audio for entry", entryId);
        return (await blobs.OpenReadAsync(audio.BlobPath, ct), audio.ContentType);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<AudioFile> LoadAsync(Guid uploadId, CancellationToken ct)
    {
        var audio = await db.AudioFiles.FirstOrDefaultAsync(a => a.Id == uploadId, ct) ?? throw new NotFoundException("Upload", uploadId);
        await entries.LoadForWriteAsync(audio.EntryId, ct);   // same rule as creating the entry
        return audio;
    }

    private async Task DeleteChunksAsync(AudioFile audio, CancellationToken ct)
    {
        foreach (var n in Received(audio))
            await blobs.DeleteAsync(ChunkPath(audio, n), ct);
    }

    private static string ChunkPath(AudioFile a, int n) => $"uploads/{a.Id:N}/{n:D6}";

    private static HashSet<int> Received(AudioFile a) =>
        a.ReceivedChunks.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();

    private static AudioUploadDto ToDto(AudioFile a) =>
        new(a.Id, a.ChunkSizeBytes, a.TotalChunks, Received(a).OrderBy(x => x).ToArray(), a.UploadState.ToString());

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" or "audio/aac" => ".m4a",
        "audio/ogg" or "audio/opus" => ".ogg",
        "audio/webm" => ".webm",
        "audio/flac" => ".flac",
        _ => ".bin",
    };
}
