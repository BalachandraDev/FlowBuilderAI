using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Capture;
using FlowCapture.Application.Common;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.CaptureFeatures;

public sealed record ListEntriesQuery(Guid FlowId) : IRequest<IReadOnlyList<EntryDto>>;
public sealed record GetEntryQuery(Guid Id) : IRequest<EntryDto>;
public sealed record CreateEntryCommand(Guid FlowId, CreateEntryRequest Body) : IRequest<EntryWriteResult>;
public sealed record UpdateEntryCommand(Guid Id, UpdateEntryRequest Body, string? IfMatch) : IRequest<EntryDto>;
public sealed record DeleteEntryCommand(Guid Id) : IRequest;

public sealed record InitAudioUploadCommand(InitAudioUploadRequest Body) : IRequest<AudioUploadDto>;
public sealed record GetAudioUploadQuery(Guid UploadId) : IRequest<AudioUploadDto>;
public sealed record PutAudioChunkCommand(Guid UploadId, int ChunkNo, Stream Body) : IRequest<AudioUploadDto>;
public sealed record CompleteAudioUploadCommand(Guid UploadId) : IRequest<AudioUploadDto>;
public sealed record StreamAudioQuery(Guid EntryId) : IRequest<AudioStreamResult>;
public sealed record AudioStreamResult(Stream Stream, string ContentType);

public sealed record GetTranscriptQuery(Guid EntryId) : IRequest<TranscriptDto>;
public sealed record SaveTranscriptCommand(Guid EntryId, string Text) : IRequest<TranscriptDto>;
public sealed record RetranscribeCommand(Guid EntryId, string? Language) : IRequest<TranscriptDto>;

public sealed class CreateEntryValidator : AbstractValidator<CreateEntryCommand>
{
    public CreateEntryValidator()
    {
        RuleFor(x => x.Body.Id).NotEmpty();
        RuleFor(x => x.Body.Kind).NotEmpty();
        RuleFor(x => x.Body.IntervieweeName).MaximumLength(120);
        RuleFor(x => x.Body.IntervieweeRole).MaximumLength(120);
    }
}

public sealed class UpdateEntryValidator : AbstractValidator<UpdateEntryCommand>
{
    public UpdateEntryValidator()
    {
        RuleFor(x => x.Body.IntervieweeName).MaximumLength(120);
        RuleFor(x => x.Body.IntervieweeRole).MaximumLength(120);
        RuleFor(x => x.Body.Language).MaximumLength(10);
    }
}

public sealed class InitAudioUploadValidator : AbstractValidator<InitAudioUploadCommand>
{
    public InitAudioUploadValidator()
    {
        RuleFor(x => x.Body.EntryId).NotEmpty();
        RuleFor(x => x.Body.ContentType).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Body.Sha256).NotEmpty();
        RuleFor(x => x.Body.DurationSec).GreaterThanOrEqualTo(0);
    }
}

public sealed class SaveTranscriptValidator : AbstractValidator<SaveTranscriptCommand>
{
    public SaveTranscriptValidator() => RuleFor(x => x.Text).NotEmpty();
}

public sealed class CaptureHandlers(
    IAppDbContext db, IProjectAccess access, EntryService entries, AudioService audio, TranscriptionService transcription,
    IJobScheduler jobs, IClock clock, Services.ISettingsService settings) :
    IRequestHandler<ListEntriesQuery, IReadOnlyList<EntryDto>>,
    IRequestHandler<GetEntryQuery, EntryDto>,
    IRequestHandler<CreateEntryCommand, EntryWriteResult>,
    IRequestHandler<UpdateEntryCommand, EntryDto>,
    IRequestHandler<DeleteEntryCommand>,
    IRequestHandler<InitAudioUploadCommand, AudioUploadDto>,
    IRequestHandler<GetAudioUploadQuery, AudioUploadDto>,
    IRequestHandler<PutAudioChunkCommand, AudioUploadDto>,
    IRequestHandler<CompleteAudioUploadCommand, AudioUploadDto>,
    IRequestHandler<StreamAudioQuery, AudioStreamResult>,
    IRequestHandler<GetTranscriptQuery, TranscriptDto>,
    IRequestHandler<SaveTranscriptCommand, TranscriptDto>,
    IRequestHandler<RetranscribeCommand, TranscriptDto>
{
    public async Task<IReadOnlyList<EntryDto>> Handle(ListEntriesQuery r, CancellationToken ct)
    {
        var projectId = await db.Flows.AsNoTracking().Where(f => f.Id == r.FlowId).Select(f => (Guid?)f.ProjectDepartment!.ProjectId).FirstOrDefaultAsync(ct)
                        ?? throw new NotFoundException("Flow", r.FlowId);
        await access.EnsureMemberAsync(projectId, ct);
        return await db.CaptureEntries.AsNoTracking().Where(e => e.FlowId == r.FlowId && !e.IsDeleted)
            .OrderBy(e => e.RefNo).Select(Mapping.EntryProjection).ToListAsync(ct);
    }

    public async Task<EntryDto> Handle(GetEntryQuery r, CancellationToken ct)
    {
        await entries.LoadForReadAsync(r.Id, ct);
        return await entries.GetDtoAsync(r.Id, ct);
    }

    public Task<EntryWriteResult> Handle(CreateEntryCommand r, CancellationToken ct) => entries.CreateAsync(r.FlowId, r.Body, ct);
    public Task<EntryDto> Handle(UpdateEntryCommand r, CancellationToken ct) => entries.UpdateAsync(r.Id, r.Body, r.IfMatch, ct);
    public Task Handle(DeleteEntryCommand r, CancellationToken ct) => entries.DeleteAsync(r.Id, ct);

    public Task<AudioUploadDto> Handle(InitAudioUploadCommand r, CancellationToken ct) => audio.InitAsync(r.Body, ct);
    public Task<AudioUploadDto> Handle(GetAudioUploadQuery r, CancellationToken ct) => audio.GetAsync(r.UploadId, ct);
    public Task<AudioUploadDto> Handle(PutAudioChunkCommand r, CancellationToken ct) => audio.PutChunkAsync(r.UploadId, r.ChunkNo, r.Body, ct);
    public Task<AudioUploadDto> Handle(CompleteAudioUploadCommand r, CancellationToken ct) => audio.CompleteAsync(r.UploadId, ct);

    public async Task<AudioStreamResult> Handle(StreamAudioQuery r, CancellationToken ct)
    {
        var (stream, contentType) = await audio.OpenAsync(r.EntryId, ct);
        return new AudioStreamResult(stream, contentType);
    }

    public async Task<TranscriptDto> Handle(GetTranscriptQuery r, CancellationToken ct)
    {
        var entry = await entries.LoadForReadAsync(r.EntryId, ct);
        return entry.Transcript?.ToDto() ?? throw new NotFoundException("Transcript for entry", r.EntryId);
    }

    public async Task<TranscriptDto> Handle(SaveTranscriptCommand r, CancellationToken ct)
    {
        var entry = await entries.LoadForWriteAsync(r.EntryId, ct);
        return await transcription.SaveEditedAsync(entry, r.Text, ct);
    }

    public async Task<TranscriptDto> Handle(RetranscribeCommand r, CancellationToken ct)
    {
        var entry = await entries.LoadForWriteAsync(r.EntryId, ct);
        var photo = entry.Kind == EntryKind.Image && !string.IsNullOrEmpty(entry.ImageBlobPath);
        if (!photo && entry.Audio is not { UploadState: UploadState.Complete })
            throw new DomainException("There is no uploaded recording to transcribe.", "no_audio");
        if (entry.Transcript is { Status: TranscriptStatus.Processing })
            throw new DomainException("This is already being turned into text.", "already_processing");
        if (!await settings.GetFlagAsync(Services.SettingsService.FlagAllowMedia, true, ct))
            throw new DomainException("Photos and voice recordings are switched off by the administrator. Type the note instead.", "media_blocked");

        if (!string.IsNullOrWhiteSpace(r.Language)) entry.Language = r.Language.Trim();
        var t = entry.Transcript ?? new Transcript { EntryId = entry.Id };
        if (entry.Transcript is null) db.Transcripts.Add(t);
        t.Status = TranscriptStatus.Queued;
        t.Error = null;
        t.RawText = null;
        t.MaskedText = null;
        t.UserEditedText = null;
        t.Language = entry.Language;
        await db.SaveChangesAsync(ct);
        await db.TouchFlowAsync(entry.FlowId, clock.UtcNow, ct);
        if (photo) jobs.EnqueueImageRead(entry.Id); else jobs.EnqueueTranscription(entry.Id);
        return t.ToDto();
    }
}
