using System.Text.Json;

namespace FlowCapture.Shared.Dtos;

// ---- common ----
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
public record JobDto(Guid JobId, string Kind, string State, int? Percent, string? Message, Guid? ResultId, DateTime CreatedAt);

/// <summary>What to put in an implementation PDF. Nothing sent means everything. <paramref name="Sections"/> are the keys from the outline; <paramref name="FlowIds"/> (project and module PDFs) the requests to include; <paramref name="Titles"/> the headings the person renamed (section key to new heading).</summary>
public sealed record GuideChoiceRequest(IReadOnlyList<string>? Sections = null, IReadOnlyList<Guid>? FlowIds = null, IReadOnlyDictionary<string, string>? Titles = null);
/// <summary>
/// One heading the PDF would have, what is under it in a few words, and the section itself as it is printed (one <see cref="GuidePartDto"/> for a request's own
/// document, one for each request in a project or module PDF) so it can be seen before choosing.
/// </summary>
public sealed record GuideSectionDto(string Key, string Title, string Detail, IReadOnlyList<GuidePartDto> Parts);
/// <summary>The page markup of one section of one request. <paramref name="FlowId"/> is empty for a request's own document. The number of the section is a <c>\u0001</c> to be replaced.</summary>
public sealed record GuidePartDto(Guid FlowId, string Html);
/// <summary>An analysed request that can be put in a project or module PDF.</summary>
public sealed record GuideRequestDto(Guid FlowId, string Code, string Title, string Module);
/// <summary>What a PDF would contain, shown before it is made so the person can choose: the headings with their pages, the requests, and the document's own look (<paramref name="Style"/>, <paramref name="Cover"/>).</summary>
public sealed record GuideOutlineDto(IReadOnlyList<GuideSectionDto> Sections, IReadOnlyList<GuideRequestDto> Requests, string Style = "", string Cover = "");
// ---- auth ----
public record LoginRequest(string Email, string Password, string? DeviceId = null);
public record TokenResponse(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, UserProfileDto User);
public record RefreshRequest(string RefreshToken);
public record LogoutRequest(string RefreshToken);
public record UserProfileDto(Guid Id, string Email, string DisplayName, string[] Roles, IReadOnlyList<ProjectMembershipDto> Projects);
public record ProjectMembershipDto(Guid ProjectId, string ProjectCode, string ProjectRole);
public record RegisterDeviceRequest(string Platform, string PushToken);

// ---- users / master data / admin ----
public record UserDto(Guid Id, string Email, string DisplayName, bool IsActive, string[] Roles, string? PreferredLanguage);
public record CreateUserRequest(string Email, string DisplayName, string Password, string[] Roles, string? PreferredLanguage = null);
public record UpdateUserRequest(string DisplayName, bool IsActive, string[] Roles, string? PreferredLanguage, string? Email = null);
public record ResetPasswordResponse(string TemporaryPassword);
public record UserRefDto(Guid Id, string DisplayName);

public record DepartmentDto(Guid Id, string Code, string Name, bool IsActive);
public record UpsertDepartmentRequest(string Code, string Name, bool IsActive = true);
public record CategoryDto(string Code, string Name, string Definition, bool IsActive, int SortOrder);
public record LanguageDto(string Code, string Name, string SttLocale, bool IsActive);
public record SettingDto(string Key, string Value);
public record PromptTemplateDto(Guid Id, string Key, int Version, bool IsActive, string Sha256, string? Notes, string? Content);
public record CreatePromptRequest(string Key, string Content, string? Notes, bool Activate = false);

// ---- projects ----
public record CreateProjectRequest(string HospitalName, string Code, string Location, DateOnly StartDate, Guid LeadUserId, string Status = "Active");
public record UpdateProjectRequest(string HospitalName, string Location, DateOnly StartDate, Guid LeadUserId, string Status, long? MonthlyAiTokenLimit);
public record ProjectDto(Guid Id, string Code, string HospitalName, string Location, DateOnly StartDate, Guid LeadUserId, string LeadName, string Status, long? MonthlyAiTokenLimit, string RowVersion,
    Guid? ClientId = null, string? ClientName = null);

/// <summary>A client with the people assigned to it. Administrators see every client; a user sees only the active clients they are assigned to.</summary>
public record ClientDto(Guid Id, string Name, string Location, bool IsActive, int ProjectCount, Guid[] UserIds);
public record CreateClientRequest(string Name, string? Location);
public record UpdateClientRequest(string Name, string? Location, bool IsActive);
public record SetClientUsersRequest(Guid[] UserIds);
/// <summary>A person changing their own password. The current one is asked for again, so a left-open screen cannot be used to lock them out.</summary>
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

// ---- the database the app uses, and moving it to another SQL Server (administrator)
/// <summary>The details of a SQL Server. The password is used for the connection only and is never sent back.</summary>
public record DbDetailsRequest(string Server, string Database, string User, string Password, bool WindowsLogin = false);
public record DbInfoDto(string Server, string Database, string Provider, bool UsingSavedServer, string? Problem);
public record DbTestDto(bool Ok, string Message, bool DatabaseExists, string[] ExistingTables, string[] MissingTables);
public record DbTableProgressDto(string Table, long SourceRows, long Inserted, long AlreadyThere, long Failed, string[] Errors);
public record DbMoveDto(string State, string Step, DateTime? StartedAt, DateTime? FinishedAt, DbTableProgressDto[] Tables, string[] Notes, bool CanUse, string? Target);
public record DbUseDto(bool Saved, bool RestartNeeded, bool Restarting, string Message);
public record ProjectMemberDto(Guid UserId, string DisplayName, string ProjectRole);
public record SetMembersRequest(IReadOnlyList<MemberAssignment> Members);
public record MemberAssignment(Guid UserId, string ProjectRole);
public record ProjectDepartmentDto(Guid Id, string ModuleCode, string Name, bool IsCustom, IReadOnlyList<UserRefDto> Assignees, int FlowCount, int EntryCount, int ConfirmedCount);
/// <summary>Adds a module to a project. Only <paramref name="DepartmentId"/> (a module from the administrator's list) is accepted; a typed name is refused. The other two are kept only so older callers still compile and are ignored.</summary>
public record AddProjectDepartmentRequest(Guid? DepartmentId, string? CustomName = null, string? ModuleCode = null);
public record SetAssignmentsRequest(Guid[] UserIds);

// ---- flows ----
/// <param name="Kind">NR (a New Requirement, the default), CR (a Change Request) or BUG (a bug to fix).</param>
public record CreateFlowRequest(Guid ProjectDepartmentId, string MenuName, string? Kind = null);
/// <summary>The new name (heading) of a request.</summary>
public record RenameFlowRequest(string MenuName);
public record FlowDto(Guid Id, Guid ProjectId, Guid ProjectDepartmentId, string ModuleCode, string DepartmentName, string MenuName,
    string Status, int CurrentVersionNo, int? ConfirmedVersionNo, int EntryCount, int OpenQuestionCount, DateTime LastActivityAt, string RowVersion,
    string Kind = "NR", int RequestNo = 0)
{
    /// <summary>The name of the request inside its module, for example NR-003 or CR-012.</summary>
    public string RequestCode => $"{Kind}-{RequestNo:000}";
}
/// <summary>A picture attached to a request. <c>StepId</c> is the step it is printed with; empty means it is printed with the attachments.</summary>
public record FigureDto(Guid Id, Guid FlowId, string Caption, string? StepId, int SortOrder, string ContentType, long SizeBytes, string OriginalName, string UploadedByName, DateTime CreatedAt);
public record UpdateFigureRequest(string Caption, string? StepId);
public record FlowContentDto(Guid FlowId, int VersionNo, string Source, JsonElement Content, string[] LockedIds, DateTime CreatedAt, string CreatedByName);
public record UpdateFlowContentRequest(int BaseVersionNo, JsonElement Content, string[]? LockedIds, string? ChangeNote);
public record ConfirmFlowRequest(int VersionNo);

// ---- capture ----
public record CreateEntryRequest(Guid Id, string Kind, DateTime CapturedAt, string Language, string? RawText, string? Notes, string? IntervieweeName, string? IntervieweeRole);
public record UpdateEntryRequest(string Language, string? RawText, string? Notes, string? IntervieweeName, string? IntervieweeRole);
public record EntryDto(Guid Id, Guid FlowId, int RefNo, string Kind, DateTime CapturedAt, Guid CapturedBy, string CapturedByName, string Language,
    string? RawText, string? Notes, string? IntervieweeName, string? IntervieweeRole, string AudioState, int? AudioDurationSec,
    string TranscriptState, bool IsDeleted, string RowVersion);
public record InitAudioUploadRequest(Guid EntryId, long SizeBytes, string Sha256, string ContentType, int DurationSec, int ChunkSizeBytes);
public record AudioUploadDto(Guid UploadId, int ChunkSizeBytes, int TotalChunks, int[] ReceivedChunks, string State);
public record TranscriptDto(Guid EntryId, string Status, string Language, string? RawText, string? MaskedText, string? UserEditedText,
    double? AvgConfidence, int MaskedCount, string? Error);
public record SaveTranscriptRequest(string Text);
public record RetranscribeRequest(string? Language);

// ---- sync ----
public record SyncPushRequest(string DeviceId, IReadOnlyList<SyncOpDto> Ops);
public record SyncOpDto(Guid OpId, string Type, Guid EntityId, string? BaseRowVersion, JsonElement Payload);
public record SyncPushResponse(IReadOnlyList<SyncOpResultDto> Results);
public record SyncOpResultDto(Guid OpId, string Result, string? NewRowVersion, JsonElement? ServerCopy, string? Reason);
public record SyncPullResponse(string NextCursor, bool HasMore, IReadOnlyList<EntryDto> Entries, IReadOnlyList<TranscriptDto> Transcripts,
    IReadOnlyList<ClarificationDto> Clarifications, IReadOnlyList<FlowDto> Flows, IReadOnlyList<CommentDto> Comments);
public record AnswerClarificationOp(string? Answer, string? Status, string? DismissReason);
public record SyncCreateEntryPayload(Guid FlowId, string Kind, DateTime CapturedAt, string Language, string? RawText, string? Notes, string? IntervieweeName, string? IntervieweeRole);
public record SyncCreateCommentPayload(Guid FlowId, string Section, string? ItemId, Guid? ParentCommentId, string Text);

// ---- analysis ----
public record StartAnalysisRequest(string Mode, Guid[]? EntryIds, bool Force = false);
public record StartDepartmentAnalysisRequest(Guid ProjectDepartmentId);
public record StartProjectAnalysisRequest(Guid[]? FlowIds);
public record SkippedFlowDto(Guid FlowId, string MenuName, string Reason);
public record DepartmentAnalysisResultDto(IReadOnlyList<AnalysisRunDto> Runs, IReadOnlyList<SkippedFlowDto> Skipped);
public record AnalysisRunDto(Guid Id, string Scope, string Mode, string Status, Guid? FlowId, string Model, int? InputTokens, int? OutputTokens,
    int? DurationMs, int Attempts, string? ErrorCode, string? ErrorMessage, int? ResultVersionNo, string[] Warnings, JsonElement? Result,
    DateTime? StartedAt, DateTime? FinishedAt, DateTime CreatedAt);
public record CrossFlowLinkDto(Guid Id, Guid FromFlowId, string FromItemId, Guid ToFlowId, string ToItemId, string Description, string[] DataPassed, string Basis, string Status);
public record DecideLinkRequest(string Status);

// ---- versions ----
public record FlowVersionSummaryDto(int VersionNo, string Source, string AuthorName, DateTime CreatedAt, string? ChangeSummary, bool IsConfirmed);
public record FlowDiffDto(int FromVersion, int ToVersion, IReadOnlyList<DiffItemDto> Items);
public record DiffItemDto(string Section, string ItemId, string Change, JsonElement? Before, JsonElement? After);

// ---- clarifications and comments ----
public record ClarificationDto(Guid Id, Guid FlowId, string QuestionKey, string Section, string Question, string[]? Options, string Status,
    bool IsBlocking, string? Answer, string RowVersion);
public record UpdateClarificationRequest(string? Answer, string? Status, string? DismissReason, bool? IsBlocking);
public record CommentDto(Guid Id, Guid FlowId, string Section, string? ItemId, Guid? ParentCommentId, string Text, bool IsResolved, string AuthorName, DateTime CreatedAt);
public record CreateCommentRequest(Guid Id, string Section, string? ItemId, Guid? ParentCommentId, string Text);
public record ResolveCommentRequest(bool Resolved);

// ---- documents and diagrams
public record GenerateDocumentRequest(bool IncludeDiagrams = true, string? AssignedBy = null);
public record GenerateProjectDocumentRequest(Guid[]? FlowIds = null, string? AssignedBy = null);
public record DocumentDto(Guid Id, string DocNumber, string Title, string Scope, Guid? FlowId, int CurrentVersionNo, IReadOnlyList<DocumentVersionDto> Versions);
public record DocumentVersionDto(int VersionNo, DateTime GeneratedAt, string GeneratedByName, string ApprovalStatus, string TemplateVersion,
                                 int[] SourceFlowVersionNos, string? ApprovedByName = null, string? ApprovalNote = null,
                                 DateTime? FinalizedAt = null, string? FinalizedByName = null, string? FinalNote = null);
/// <summary>The administrator verifies a final document (<paramref name="Approved"/>) or returns it to its maker with the reason in <paramref name="Note"/>.</summary>
public record ApproveDocumentRequest(bool Approved, string? Note);
/// <summary>A person marks a version of a document as final, to give to the team, with an optional note for the administrator.</summary>
public record MarkFinalRequest(string? Note = null);
public record DiagramInfoDto(string Kind, int? VersionNo, int Width, int Height, string Sha256);

// ---- the simple user journey: projects, the shared library of documents, the admin dashboard ----
public record QuickProjectRequest(string Name, string? Description, Guid? ClientId = null);
public record QuickProjectDto(Guid ProjectId, Guid DepartmentId, Guid FlowId, string Name, string Code);
/// <summary>A generated document as shown in the library. Kind: "Guide" (a request's implementation document), "ModuleGuide" (one PDF of every request in a module), "ProjectGuide" (one PDF of the whole project) or "Requirements".</summary>
public record LibraryDocumentDto(Guid Id, string DocNumber, string Title, string Kind, Guid ProjectId, string ProjectName, Guid? FlowId,
    int VersionNo, DateTime CreatedAt, string CreatedByName, string[] Formats, bool HasDiagram, Guid? ModuleId = null,
    string Stage = "Working", DateTime? FinalizedAt = null, string? FinalizedByName = null, string? ReviewNote = null, string? VerifiedByName = null, DateTime? VerifiedAt = null,
    Guid? ClientId = null, string? ClientName = null, string? FinalNote = null);
// ---- AI usage: how many tokens were used, by what, by whom, for which project ----
public record AiUsageTotalsDto(int Calls, long InputTokens, long OutputTokens, long TotalTokens);
public record AiUsageKindDto(string Kind, int Calls, long InputTokens, long OutputTokens, long TotalTokens);
public record AiUsageDayDto(string Day, int Calls, long TotalTokens);
public record AiUsageWhoDto(Guid? Id, string Name, int Calls, long TotalTokens);
public record AiUsageCallDto(DateTime At, string Kind, string Who, string Project, string Model, long InputTokens, long OutputTokens, int DurationMs, bool Success);
/// <summary>Everyone's usage for an administrator (AllUsers), the caller's own for anybody else.</summary>
public record AiUsageSummaryDto(string Month, bool AllUsers, string Provider, AiUsageTotalsDto Totals, AiUsageTotalsDto Today, AiUsageTotalsDto LastMonth,
    IReadOnlyList<AiUsageKindDto> ByKind, IReadOnlyList<AiUsageDayDto> ByDay, IReadOnlyList<AiUsageWhoDto> ByUser, IReadOnlyList<AiUsageWhoDto> ByProject,
    IReadOnlyList<AiUsageCallDto> Recent, IReadOnlyList<AiModelUsageDto>? ByModel = null, IReadOnlyList<AiQuotaDto>? Quotas = null, IReadOnlyList<AiModelQuotaDto>? ModelQuotas = null);

public record AdminOverviewDto(int Users, int ActiveUsers, int Projects, int Flows, int Notes, int Documents, string AiProvider, bool AiReady, int AwaitingVerification = 0, int Clients = 0);

/// <summary>
/// One line of "who handles what" for the administrator: a user on a client, with their work for it. A client nobody is assigned to has a line without a user,
/// and a user assigned to no client a line without a client, so nothing is hidden. Counts of final documents are by the person who marked them final.
/// </summary>
public record HandlingRowDto(Guid? ClientId, string? ClientName, bool ClientActive, Guid? UserId, string? UserName, string? UserEmail, bool UserActive,
    int Projects, int ProjectsStarted, int Modules, int Requests, int AwaitingVerification, int Verified, int Returned, DateTime? LastFinalAt);
/// <summary>One free allowance as the AI service last reported it (what is left, out of how much).</summary>
public record AiLimitDto(string Provider, string Model, string What, int? Limit, int? Remaining, string? Renews, DateTime At);
public record AiAdminDto(string Provider, string[] Providers, bool HasKey, string? KeyHint, string GeminiModel, bool KeyFromDashboard, bool AiWording = false, bool AllowMedia = true,
    bool GroqHasKey = false, string? GroqKeyHint = null, string VoiceProvider = "Gemini", string GroqSttModel = "", IReadOnlyList<AiLimitDto>? Limits = null, IReadOnlyList<AiQuotaDto>? Quotas = null, IReadOnlyList<AiModelQuotaDto>? ModelQuotas = null);
/// <summary>GeminiApiKey: null leaves the saved key as it is, an empty text removes it.</summary>
/// <param name="Limits">Daily limits of the free accounts, keyed "Gemini.Requests", "Gemini.Tokens", "Groq.Requests", "Groq.Tokens". 0 removes the limit.</param>
public record SaveAiAdminRequest(string? Provider, string? GeminiModel, string? GeminiApiKey, bool? AiWording = null, bool? AllowMedia = null, string? GroqApiKey = null, string? VoiceProvider = null,
    IReadOnlyDictionary<string, long>? Limits = null, IReadOnlyDictionary<string, long>? ModelLimits = null);
/// <summary>What one AI used today against the limit the administrator set (null = no limit set), and when its day ends.</summary>
public record AiQuotaDto(string Provider, long UsedRequests, long UsedTokens, long? LimitRequests, long? LimitTokens, DateTime ResetsAt, bool Reached, string? Message);
/// <summary>One Gemini model against its own free limits: requests today and in the last minute, tokens in the last minute.</summary>
public record AiModelQuotaDto(string Model, string Name, long UsedToday, long? PerDay, long UsedLastMinute, long? PerMinute, long TokensLastMinute, long? TokensPerMinute, bool Reached, DateTime ResetsAt, bool IsPreset);
/// <summary>What the free Gemini has left today across the models the app uses. RequestsLeft is null when a model is not limited by the app.</summary>
public record AiCapacityDto(bool Reached, long? RequestsLeft, DateTime ResetsAt, string? Message);
public record AiModelUsageDto(string Provider, string Model, int Calls, long InputTokens, long OutputTokens, long TotalTokens);
public record AiModelDto(string Id, string Name);
public record AiTestResultDto(bool Ok, string Message, string? Model, IReadOnlyList<AiModelDto> Models, IReadOnlyList<AiProbeDto>? Probes = null);
/// <summary>One model's answer to the key test: did it answer just now, how fast, and what to say about it.</summary>
public record AiProbeDto(string Model, bool Ok, int Milliseconds, string Message);
public record DocumentTemplateDto(Guid Id, string Name, string Version, bool IsActive, DateTime UploadedAt);

// ---- dashboard / usage / audit ----
public record DashboardDto(Guid ProjectId, int DepartmentsTotal, int FlowsTotal, int Captured, int Analysed, int Confirmed, int Documented,
    int OpenClarifications, int BlockingClarifications, DateTime? LastActivityAt, IReadOnlyList<DepartmentProgressDto> Departments, AiUsageDto AiUsage);
public record DepartmentProgressDto(Guid ProjectDepartmentId, string ModuleCode, string Name, int Entries, int Flows, int Analysed, int Confirmed, int Documented, int OpenQuestions);
public record PendingItemDto(string Kind, Guid? FlowId, string Title, string Detail, DateTime Since);
public record AiUsageDto(string Month, long InputTokens, long OutputTokens, decimal EstimatedCost, long? Limit, int Runs);
public record AuditLogDto(long Id, DateTime At, Guid? UserId, string? UserName, string Action, string EntityType, string EntityId, Guid? ProjectId,
    string? OldValues, string? NewValues, string? CorrelationId);
