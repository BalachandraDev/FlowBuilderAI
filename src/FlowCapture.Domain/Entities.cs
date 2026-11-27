namespace FlowCapture.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

/// <summary>Audit columns and an optimistic-concurrency stamp (provider neutral, maintained by an EF interceptor).</summary>
public abstract class AuditableEntity : Entity
{
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");
}

// ------------------------------------------------------------------ identity

public class User : AuditableEntity
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string? PreferredLanguage { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>Set when an administrator deleted the user. A deleted user cannot sign in and is hidden from the lists, but nothing is erased: their name stays on the notes and documents they made.</summary>
    public DateTime? DeletedAt { get; set; }
    public List<UserRole> Roles { get; set; } = [];
}

public class Role : Entity
{
    public string Name { get; set; } = "";
}

public class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public User? User { get; set; }
    public Role? Role { get; set; }
}

public class ProjectMember
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public ProjectRole ProjectRole { get; set; }
    public Project? Project { get; set; }
    public User? User { get; set; }
}

public class RefreshToken : Entity
{
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
    public string? DeviceInfo { get; set; }
}

public class DeviceToken : Entity
{
    public Guid UserId { get; set; }
    public string Platform { get; set; } = "";
    public string PushToken { get; set; } = "";
    public DateTime LastSeenAt { get; set; }
}

// ------------------------------------------------------------------ project structure

/// <summary>A customer (a hospital or hospital group). An administrator makes clients and assigns users to them; a user picks one of theirs when starting a project.</summary>
public class Client : AuditableEntity
{
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    /// <summary>A stopped client cannot be chosen for new projects. Its existing projects stay.</summary>
    public bool IsActive { get; set; } = true;
    public List<ClientUser> Users { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
}

public class ClientUser
{
    public Guid ClientId { get; set; }
    public Guid UserId { get; set; }
    public Client? Client { get; set; }
    public User? User { get; set; }
}

public class Project : AuditableEntity
{
    public Guid? ClientId { get; set; }
    public Client? Client { get; set; }
    public string Code { get; set; } = "";
    public string HospitalName { get; set; } = "";
    public string Location { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public Guid LeadUserId { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    public long? MonthlyAiTokenLimit { get; set; }
    /// <summary>Set when the project was deleted. A deleted project is hidden everywhere and nobody can open it, but nothing is erased: its data and the AI usage it caused stay, so it can be brought back.</summary>
    public DateTime? DeletedAt { get; set; }
    public User? Lead { get; set; }
    public List<ProjectMember> Members { get; set; } = [];
    public List<ProjectDepartment> Departments { get; set; } = [];
}

public class Department : AuditableEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class ProjectDepartment : AuditableEntity
{
    public Guid ProjectId { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? CustomName { get; set; }
    public string ModuleCode { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public Project? Project { get; set; }
    public Department? Department { get; set; }
    public List<DepartmentAssignment> Assignments { get; set; } = [];
    public List<Flow> Flows { get; set; } = [];

    public string DisplayName => CustomName ?? Department?.Name ?? ModuleCode;
}

public class DepartmentAssignment
{
    public Guid ProjectDepartmentId { get; set; }
    public Guid UserId { get; set; }
    public ProjectDepartment? ProjectDepartment { get; set; }
    public User? User { get; set; }
}

/// <summary>One department menu or sub-process inside a project. The unit of analysis, review, confirmation and the RGD.</summary>
public class Flow : AuditableEntity
{
    public Guid ProjectDepartmentId { get; set; }
    public string MenuName { get; set; } = "General";
    /// <summary>New Requirement or Change Request. Together with <see cref="RequestNo"/> it names the request inside its module: NR-001, CR-004.</summary>
    public RequestKind Kind { get; set; } = RequestKind.NR;
    /// <summary>Counted per module and kind, starting at 1. Never reused.</summary>
    public int RequestNo { get; set; }
    /// <summary>Set when the request was deleted: it is hidden everywhere, but its rows (notes, versions, documents) stay. Its number is never reused.</summary>
    public DateTime? DeletedAt { get; set; }
    public FlowStatus Status { get; set; } = FlowStatus.Draft;
    public int CurrentVersionNo { get; set; }
    public int? ConfirmedVersionNo { get; set; }
    public Guid? ConfirmedBy { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public ProjectDepartment? ProjectDepartment { get; set; }
    public List<CaptureEntry> Entries { get; set; } = [];
    public List<FlowVersion> Versions { get; set; } = [];
    public List<ClarificationItem> Clarifications { get; set; } = [];
}

// ------------------------------------------------------------------ capture

public class CaptureEntry : AuditableEntity
{
    public Guid FlowId { get; set; }
    /// <summary>Stable per-flow number (E1, E2...) cited by AI results. Never reused.</summary>
    public int RefNo { get; set; }
    public Guid CapturedBy { get; set; }
    public DateTime CapturedAt { get; set; }
    public EntryKind Kind { get; set; }
    public string? IntervieweeName { get; set; }
    public string? IntervieweeRole { get; set; }
    public string Language { get; set; } = "en-IN";
    public string? RawText { get; set; }
    public string? Notes { get; set; }
    public bool IsDeleted { get; set; }
    /// <summary>For Image entries: where the photo is stored, and its type (image/jpeg, image/png, image/webp).</summary>
    public string? ImageBlobPath { get; set; }
    public string? ImageContentType { get; set; }
    public Flow? Flow { get; set; }
    public User? CapturedByUser { get; set; }
    public AudioFile? Audio { get; set; }
    public Transcript? Transcript { get; set; }
}

public class AudioFile : AuditableEntity
{
    public Guid EntryId { get; set; }
    public string BlobPath { get; set; } = "";
    public string ContentType { get; set; } = "";
    public int DurationSec { get; set; }
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public int ChunkSizeBytes { get; set; }
    public int TotalChunks { get; set; }
    public UploadState UploadState { get; set; } = UploadState.Initiated;
    /// <summary>Comma separated chunk numbers received so far.</summary>
    public string ReceivedChunks { get; set; } = "";
    public CaptureEntry? Entry { get; set; }
}

public class Transcript : AuditableEntity
{
    public Guid EntryId { get; set; }
    public string? Provider { get; set; }
    public string Language { get; set; } = "";
    public string? RawText { get; set; }
    /// <summary>PHI-masked text. This (or UserEditedText) is the only text that reaches the AI.</summary>
    public string? MaskedText { get; set; }
    /// <summary>User-corrected text. Stored already masked.</summary>
    public string? UserEditedText { get; set; }
    public TranscriptStatus Status { get; set; } = TranscriptStatus.Queued;
    public string? SegmentsJson { get; set; }
    public double? AvgConfidence { get; set; }
    public int MaskedCount { get; set; }
    public string? Error { get; set; }
    public CaptureEntry? Entry { get; set; }

    public string? EffectiveText => string.IsNullOrWhiteSpace(UserEditedText) ? MaskedText : UserEditedText;
}

// ------------------------------------------------------------------ analysis and versions

public class PromptTemplate : AuditableEntity
{
    public string Key { get; set; } = "";
    public int Version { get; set; }
    public string Content { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
}

public class AnalysisRun : AuditableEntity
{
    public AnalysisScope Scope { get; set; }
    public AnalysisMode Mode { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? FlowId { get; set; }
    public AnalysisStatus Status { get; set; } = AnalysisStatus.Queued;
    public string InputHash { get; set; } = "";
    public string PromptTemplateIds { get; set; } = "[]";
    public string SystemPromptSha256 { get; set; } = "";
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public int DurationMs { get; set; }
    public int Attempts { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
    public string? RawOutput { get; set; }
    public string Warnings { get; set; } = "[]";
    /// <summary>Request parameters as JSON, e.g. {"entryIds":[...]} or {"flowIds":[...]}.</summary>
    public string ParametersJson { get; set; } = "{}";
    public Guid RequestedBy { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public Guid? ResultFlowVersionId { get; set; }
    public int? ResultVersionNo { get; set; }
    public string? ResultJson { get; set; }
}

/// <summary>Immutable snapshot of a flow's structured content (flow.v1). Only IsConfirmed may change after insert.</summary>
public class FlowVersion : Entity
{
    public Guid FlowId { get; set; }
    public int VersionNo { get; set; }
    public VersionSource Source { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public int? ParentVersionNo { get; set; }
    public string Content { get; set; } = "{}";
    public string LockedIds { get; set; } = "[]";
    public string? ChangeSummary { get; set; }
    public bool IsConfirmed { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Flow? Flow { get; set; }
    public User? CreatedByUser { get; set; }
}

/// <summary>A picture attached to a request (a screenshot, a photo of a form). It is printed in the implementation document with its caption.</summary>
public class FlowFigure : AuditableEntity
{
    public Guid FlowId { get; set; }
    /// <summary>The order the pictures are printed in.</summary>
    public int SortOrder { get; set; }
    public string Caption { get; set; } = "";
    /// <summary>The step (S1, S2...) the picture belongs to: it is printed with that step. Empty: it is printed with the attachments.</summary>
    public string? StepId { get; set; }
    public string BlobPath { get; set; } = "";
    public string ContentType { get; set; } = "image/jpeg";
    public long SizeBytes { get; set; }
    public string OriginalName { get; set; } = "";
    public Flow? Flow { get; set; }
}

public class Requirement : Entity
{
    public Guid FlowId { get; set; }
    public Guid FlowVersionId { get; set; }
    public string ReqId { get; set; } = "";
    public string Description { get; set; } = "";
    public string CategoryCode { get; set; } = "";
    public string Priority { get; set; } = "";
    public bool IsCurrent { get; set; }
}

public class RequirementCategory
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Definition { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public class ClarificationItem : AuditableEntity
{
    public Guid FlowId { get; set; }
    public string QuestionKey { get; set; } = "";
    public int FirstSeenVersionNo { get; set; }
    public string Section { get; set; } = "general";
    public string Question { get; set; } = "";
    public string? RelatedGapText { get; set; }
    public string? OptionsJson { get; set; }
    public ClarificationStatus Status { get; set; } = ClarificationStatus.Open;
    public bool IsBlocking { get; set; }
    public string? Answer { get; set; }
    public string? DismissReason { get; set; }
    public Guid? AnsweredBy { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public Flow? Flow { get; set; }
}

public class CrossFlowLink : AuditableEntity
{
    public Guid ProjectId { get; set; }
    public Guid FromFlowId { get; set; }
    public string FromItemId { get; set; } = "";
    public Guid ToFlowId { get; set; }
    public string ToItemId { get; set; } = "";
    public string Description { get; set; } = "";
    public string DataPassedJson { get; set; } = "[]";
    public string Basis { get; set; } = "";
    public LinkStatus Status { get; set; } = LinkStatus.Proposed;
    public Guid AnalysisRunId { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
}

public class Comment : AuditableEntity
{
    public Guid FlowId { get; set; }
    public string Section { get; set; } = "general";
    public string? ItemId { get; set; }
    public Guid? ParentCommentId { get; set; }
    public string Text { get; set; } = "";
    public bool IsResolved { get; set; }
    public Guid? ResolvedBy { get; set; }
    public Guid AuthorId { get; set; }
    public User? Author { get; set; }
    public Flow? Flow { get; set; }
}

// ------------------------------------------------------------------ outputs (used by Phase 4, schema created now)

public class DiagramAsset : AuditableEntity
{
    public Guid? FlowId { get; set; }
    public Guid ProjectId { get; set; }
    public int? FlowVersionNo { get; set; }
    public DiagramKind Kind { get; set; }
    public string MermaidSource { get; set; } = "";
    public string? SvgPath { get; set; }
    public string? PngPath { get; set; }
    public string? MermaidPath { get; set; }
    /// <summary>Hash of the Mermaid source the files were drawn from. Unchanged source means the files are reused.</summary>
    public string? Sha256 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public class DocumentSequence
{
    public Guid ProjectId { get; set; }
    public string ModuleCode { get; set; } = "";
    public int LastNumber { get; set; }
}

public class GeneratedDocument : AuditableEntity
{
    public Guid ProjectId { get; set; }
    public DocumentScope Scope { get; set; }
    public Guid? FlowId { get; set; }
    /// <summary>For a module's PDF: the module (a project department).</summary>
    public Guid? ProjectDepartmentId { get; set; }
    public string DocNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public int CurrentVersionNo { get; set; }
    public string? AssignedBy { get; set; }
    public string Status { get; set; } = "Active";
    public List<DocumentVersion> Versions { get; set; } = [];
}

public class DocumentVersion : Entity
{
    public Guid GeneratedDocumentId { get; set; }
    public int VersionNo { get; set; }
    public string SourceFlowVersions { get; set; } = "[]";
    public string DocxPath { get; set; } = "";
    public string PdfPath { get; set; } = "";
    public string TemplateVersion { get; set; } = "";
    public string DiagramAssetIds { get; set; } = "[]";
    public string Sha256 { get; set; } = "";
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;
    public Guid? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovalNote { get; set; }
    /// <summary>When the person who made this version marked it as final, to give to the team. Null: still a working copy. Only a final version is seen by the administrator, who then verifies it (<see cref="ApprovalStatus"/>).</summary>
    public DateTime? FinalizedAt { get; set; }
    public Guid? FinalizedBy { get; set; }
    public string? FinalNote { get; set; }
    public Guid GeneratedBy { get; set; }
    public DateTime GeneratedAt { get; set; }
}

/// <summary>One request to generate a document, run in the background. Shown to the user as a job.</summary>
public class DocumentJobRun : AuditableEntity
{
    public Guid ProjectId { get; set; }
    public DocumentScope Scope { get; set; }
    public Guid? FlowId { get; set; }
    /// <summary>For a module's PDF: the module (a project department).</summary>
    public Guid? ProjectDepartmentId { get; set; }
    /// <summary>For project documents: the chosen flows (JSON array), empty means all confirmed flows.</summary>
    public string FlowIdsJson { get; set; } = "[]";
    /// <summary>For an implementation document: the keys of the sections the person chose to print (JSON array). Null means every section.</summary>
    public string? SectionsJson { get; set; }
    /// <summary>The headings the person renamed for this PDF (JSON object: section key to new heading). Null means every heading keeps its own name.</summary>
    public string? TitlesJson { get; set; }
    /// <summary>The text the person wrote under a heading instead of the generated text (JSON object: section key, or "requestId/sectionKey", to text). Null means all text is as generated.</summary>
    public string? TextsJson { get; set; }
    public string? AssignedBy { get; set; }
    public bool IncludeDiagrams { get; set; } = true;
    public DocJobStatus Status { get; set; } = DocJobStatus.Queued;
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? ResultDocumentId { get; set; }
    public int? ResultVersionNo { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public class DocumentTemplate : AuditableEntity
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string BlobPath { get; set; } = "";
    public bool IsActive { get; set; }
}

// ------------------------------------------------------------------ system

public class AuditLog
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public Guid? ProjectId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? CorrelationId { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>One call to the AI and the tokens it used, for the AI usage page. Written after every call that reports tokens.</summary>
public class AiUsage : Entity
{
    public DateTime At { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? FlowId { get; set; }
    public AiUsageKind Kind { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int DurationMs { get; set; }
    public bool Success { get; set; } = true;
}

/// <summary>
/// One request the app sent to a free AI, whatever came back. Google counts requests per model per minute and per day, and counts the ones that failed
/// too, so this is what the app counts against each model's limits (the usage table only holds the answers that worked).
/// </summary>
public class AiAttempt : Entity
{
    public DateTime At { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>ok, busy, limited, daily_limit (Google said the day's allowance is used up), no_answer or error.</summary>
    public string Outcome { get; set; } = "ok";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}

public class SystemSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public Guid? UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Language
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string SttLocale { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

/// <summary>Stored responses for Idempotency-Key replays on creating POSTs.</summary>
public class IdempotencyRecord
{
    public Guid Key { get; set; }
    public Guid UserId { get; set; }
    public string Path { get; set; } = "";
    public int StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
}
