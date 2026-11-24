using System.Linq.Expressions;
using System.Text.Json;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;

namespace FlowCapture.Application.Common;

/// <summary>Entity to DTO mapping. Expressions are used where EF must translate them into SQL.</summary>
public static class Mapping
{
    public static readonly Expression<Func<Flow, FlowDto>> FlowProjection = f => new FlowDto(
        f.Id,
        f.ProjectDepartment!.ProjectId,
        f.ProjectDepartmentId,
        f.ProjectDepartment.ModuleCode,
        f.ProjectDepartment.CustomName ?? f.ProjectDepartment.Department!.Name,
        f.MenuName,
        f.Status.ToString(),
        f.CurrentVersionNo,
        f.ConfirmedVersionNo,
        f.Entries.Count(e => !e.IsDeleted),
        f.Clarifications.Count(c => c.Status == ClarificationStatus.Open),
        f.LastActivityAt,
        f.ConcurrencyStamp,
        f.Kind.ToString(),
        f.RequestNo);

    public static readonly Expression<Func<CaptureEntry, EntryDto>> EntryProjection = e => new EntryDto(
        e.Id, e.FlowId, e.RefNo, e.Kind.ToString(), e.CapturedAt, e.CapturedBy,
        e.CapturedByUser!.DisplayName, e.Language, e.RawText, e.Notes, e.IntervieweeName, e.IntervieweeRole,
        e.Audio == null ? "None" : e.Audio.UploadState.ToString(),
        e.Audio == null ? (int?)null : e.Audio.DurationSec,
        e.Transcript == null ? "None" : e.Transcript.Status.ToString(),
        e.IsDeleted, e.ConcurrencyStamp);

    public static readonly Expression<Func<ClarificationItem, ClarificationDto>> ClarificationProjection = c => new ClarificationDto(
        c.Id, c.FlowId, c.QuestionKey, c.Section, c.Question, null, c.Status.ToString(), c.IsBlocking, c.Answer, c.ConcurrencyStamp);

    public static ClarificationDto ToDto(this ClarificationItem c) => new(
        c.Id, c.FlowId, c.QuestionKey, c.Section, c.Question,
        string.IsNullOrEmpty(c.OptionsJson) ? null : JsonSerializer.Deserialize<string[]>(c.OptionsJson),
        c.Status.ToString(), c.IsBlocking, c.Answer, c.ConcurrencyStamp);

    public static CommentDto ToDto(this Comment c, string authorName) => new(
        c.Id, c.FlowId, c.Section, c.ItemId, c.ParentCommentId, c.Text, c.IsResolved, authorName, c.CreatedAt);

    public static TranscriptDto ToDto(this Transcript t) => new(
        t.EntryId, t.Status.ToString(), t.Language, t.RawText, t.MaskedText, t.UserEditedText, t.AvgConfidence, t.MaskedCount, t.Error);

    public static ProjectDto ToDto(this Project p, string leadName, string? clientName = null) => new(
        p.Id, p.Code, p.HospitalName, p.Location, p.StartDate, p.LeadUserId, leadName, p.Status.ToString(),
        p.MonthlyAiTokenLimit, p.ConcurrencyStamp, p.ClientId, clientName ?? p.Client?.Name);

    public static AnalysisRunDto ToDto(this AnalysisRun r) => new(
        r.Id, r.Scope.ToString(), r.Mode.ToString(), r.Status.ToString(), r.FlowId, r.Model,
        r.Status is AnalysisStatus.Succeeded or AnalysisStatus.Failed ? r.InputTokens : null,
        r.Status is AnalysisStatus.Succeeded or AnalysisStatus.Failed ? r.OutputTokens : null,
        r.Status is AnalysisStatus.Succeeded or AnalysisStatus.Failed ? r.DurationMs : null,
        r.Attempts, r.ErrorCode, r.ErrorDetail is { Length: > 600 } d ? d[..600] : r.ErrorDetail, r.ResultVersionNo,
        JsonSerializer.Deserialize<string[]>(r.Warnings) ?? [],
        r.Scope == AnalysisScope.Whole && r.ResultJson is not null ? JsonSerializer.Deserialize<JsonElement>(r.ResultJson) : null,
        r.StartedAt, r.FinishedAt, r.CreatedAt);

    public static CrossFlowLinkDto ToDto(this CrossFlowLink l) => new(
        l.Id, l.FromFlowId, l.FromItemId, l.ToFlowId, l.ToItemId, l.Description,
        JsonSerializer.Deserialize<string[]>(l.DataPassedJson) ?? [], l.Basis, l.Status.ToString());

    public static string DisplayName(this ProjectDepartment pd) => pd.CustomName ?? pd.Department?.Name ?? pd.ModuleCode;

    /// <summary>E# reference of an entry, as used in AI input and results.</summary>
    public static string Ref(this CaptureEntry e) => $"E{e.RefNo}";
    public static int NumberOf(string key) =>
        int.TryParse(new string(key.Where(char.IsDigit).ToArray()), out var n) ? n : 0;
}
