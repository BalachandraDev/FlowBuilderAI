using System.Text.Json.Nodes;
using FlowCapture.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FlowCapture.Application.Abstractions;

public interface IClock { DateTime UtcNow { get; } }

public interface ICurrentUser
{
    Guid? UserId { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
    string? CorrelationId { get; }
    string? IpAddress { get; }
}

public static class CurrentUserExtensions
{
    public static Guid RequireUserId(this ICurrentUser user) =>
        user.UserId ?? throw new ForbiddenException("Authentication is required.");
}

public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<ProjectMember> ProjectMembers { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<DeviceToken> DeviceTokens { get; }
    DbSet<Project> Projects { get; }
    DbSet<Client> Clients { get; }
    DbSet<ClientUser> ClientUsers { get; }
    DbSet<Department> Departments { get; }
    DbSet<ProjectDepartment> ProjectDepartments { get; }
    DbSet<DepartmentAssignment> DepartmentAssignments { get; }
    DbSet<Flow> Flows { get; }
    DbSet<CaptureEntry> CaptureEntries { get; }
    DbSet<AudioFile> AudioFiles { get; }
    DbSet<Transcript> Transcripts { get; }
    DbSet<PromptTemplate> PromptTemplates { get; }
    DbSet<AnalysisRun> AnalysisRuns { get; }
    DbSet<FlowVersion> FlowVersions { get; }
    DbSet<Requirement> Requirements { get; }
    DbSet<RequirementCategory> RequirementCategories { get; }
    DbSet<ClarificationItem> ClarificationItems { get; }
    DbSet<CrossFlowLink> CrossFlowLinks { get; }
    DbSet<Comment> Comments { get; }
    DbSet<DiagramAsset> DiagramAssets { get; }
    DbSet<DocumentSequence> DocumentSequences { get; }
    DbSet<GeneratedDocument> GeneratedDocuments { get; }
    DbSet<FlowFigure> FlowFigures { get; }
    DbSet<AiAttempt> AiAttempts { get; }
    DbSet<DocumentVersion> DocumentVersions { get; }
    DbSet<DocumentTemplate> DocumentTemplates { get; }
    DbSet<DocumentJobRun> DocumentJobRuns { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<SystemSetting> SystemSettings { get; }
    DbSet<AiUsage> AiUsages { get; }
    DbSet<Language> Languages { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }

    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }
    EntityEntry<T> Entry<T>(T entity) where T : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IBlobStore
{
    Task SaveAsync(string path, Stream content, string? contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
    Task<bool> ExistsAsync(string path, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}

/// <summary>The tokens are what the AI reported for this call (0 when the service does not say). They feed the AI usage page.</summary>
public sealed record SttResult(string Text, string Locale, double? AvgConfidence, string? SegmentsJson, string Provider,
    int InputTokens = 0, int OutputTokens = 0, string? Model = null);

public interface ISpeechToTextService
{
    Task<SttResult> TranscribeAsync(Stream audio, string contentType, string fileName,
        IReadOnlyList<string> candidateLocales, CancellationToken ct);
}

public sealed class SttException(string message, bool retryable = false) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}

public sealed record ImageTextResult(string Text, string Provider, int InputTokens = 0, int OutputTokens = 0, string? Model = null);

/// <summary>Reads the text of a photo of handwritten or printed notes.</summary>
public interface IImageTextService
{
    Task<ImageTextResult> ReadAsync(Stream image, string contentType, CancellationToken ct);
}

/// <summary>
/// Reads the text out of a file an administrator or consultant attaches as a note: an Excel workbook (.xlsx) or a PDF with text in it.
/// It runs on the server and uses no AI. Throws a <see cref="DomainException"/> with a reason a person can act on when it cannot.
/// </summary>
public interface IFileTextExtractor
{
    string Extract(Stream file, string fileName);
}

/// <summary>Encrypts secrets (such as the AI key) that an administrator saves in the app, so they are not readable in the database.</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    /// <summary>The original text, or null when the value cannot be decrypted (for example the signing key was changed).</summary>
    string? Unprotect(string protectedValue);
}

/// <summary>What the apps and the health check need to know about the AI set-up.</summary>
public sealed record AiStatus(string Provider, bool Ready, string? Notice);

public interface IAiStatusService
{
    Task<AiStatus> GetAsync(CancellationToken ct = default);
}

public sealed record MaskResult(string Text, int Count);

public interface IPhiMasker
{
    Task<MaskResult> MaskAsync(string text, CancellationToken ct = default);
}

public sealed record AiCallRequest(
    string Model,
    string SystemPrompt,
    string UserMessage,
    string ToolName,
    string ToolDescription,
    JsonObject InputSchema,
    int MaxTokens,
    string? Effort,
    bool DisableThinking,
    /// <summary>A second, corrective attempt: the AI is allowed to think harder. The first attempt thinks lightly (thinking is billed as output).</summary>
    bool Careful = false);

public sealed record AiCallResult(
    string ToolInputJson,
    int InputTokens,
    int CacheCreationTokens,
    int CacheReadTokens,
    int OutputTokens,
    string StopReason,
    string Model,
    int DurationMs);

/// <summary>Failure of the AI call. Code is stored on the run (e.g. ai_refusal, ai_rate_limited, ai_no_tool_call).</summary>
public sealed class AiServiceException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public interface IFlowAnalysisService
{
    Task<AiCallResult> CallAsync(AiCallRequest request, CancellationToken ct);
}

/// <summary>A drawn diagram: SVG for documents and the web, PNG for Word, and its logical size in CSS pixels.</summary>
public sealed record DiagramRender(string Svg, byte[]? Png, int Width, int Height);

public interface IDiagramRenderer
{
    /// <summary>Draws the diagram. The SVG needs one browser start; the PNG needs a second, so it is made only when asked for.</summary>
    Task<DiagramRender> RenderAsync(string mermaid, bool withPng, CancellationToken ct);

    /// <summary>A sharp PNG of a finished SVG (the workflow picture the server drew itself), for Word and for downloading.</summary>
    Task<byte[]> RasterizeSvgAsync(string svg, int width, int height, CancellationToken ct);
}

/// <summary>Fills the branded Word template with a document. The template decides logo, fonts, colours, header and footer.</summary>
public interface IRgdDocxWriter
{
    Task<byte[]> WriteAsync(Documents.RgdDocument document, Stream template, CancellationToken ct);
    /// <summary>Why a template cannot be used (missing placeholders or styles). Empty when it is fine.</summary>
    IReadOnlyList<string> ValidateTemplate(Stream template);
}

/// <summary>Turns a finished HTML page (A4 print layout) into a PDF. Uses the same browser that draws the diagrams.</summary>
public interface IHtmlPdfRenderer
{
    Task<byte[]> RenderAsync(string html, CancellationToken ct);
}

public interface IPdfConverter
{
    Task<byte[]> ConvertAsync(byte[] docx, CancellationToken ct);
}

public interface IPushSender
{
    Task SendToUserAsync(Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data, CancellationToken ct);
}

public interface IJobScheduler
{
    void EnqueueTranscription(Guid entryId);
    void EnqueueImageRead(Guid entryId);
    void EnqueueGuide(Guid jobId);
    void EnqueueAnalysis(Guid runId);
    void EnqueueDocument(Guid jobId);
}

public interface IPasswordService
{
    string Hash(string password);
    bool Verify(string hash, string password);
}

public sealed record AccessToken(string Token, DateTime ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user, IReadOnlyCollection<string> roles);
    /// <returns>(raw token to give the client, hash to store)</returns>
    (string Raw, string Hash) NewRefreshToken();
    string HashRefreshToken(string raw);
    TimeSpan RefreshTokenLifetime { get; }
}

public interface IProjectAccess
{
    /// <summary>Effective project role of the current user, or null when not a member. Admins are treated as Lead.</summary>
    Task<ProjectRole?> GetRoleAsync(Guid projectId, CancellationToken ct = default);
    Task EnsureMemberAsync(Guid projectId, CancellationToken ct = default);
    Task EnsureLeadAsync(Guid projectId, CancellationToken ct = default);
    /// <summary>Lead, Admin, or an Executive assigned to the project department.</summary>
    Task EnsureCanCaptureAsync(Guid projectId, Guid projectDepartmentId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> MemberProjectIdsAsync(CancellationToken ct = default);
}
