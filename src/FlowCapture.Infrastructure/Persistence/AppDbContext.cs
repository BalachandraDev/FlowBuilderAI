using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FlowCapture.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientUser> ClientUsers => Set<ClientUser>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<ProjectDepartment> ProjectDepartments => Set<ProjectDepartment>();
    public DbSet<DepartmentAssignment> DepartmentAssignments => Set<DepartmentAssignment>();
    public DbSet<Flow> Flows => Set<Flow>();
    public DbSet<CaptureEntry> CaptureEntries => Set<CaptureEntry>();
    public DbSet<AudioFile> AudioFiles => Set<AudioFile>();
    public DbSet<Transcript> Transcripts => Set<Transcript>();
    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();
    public DbSet<AnalysisRun> AnalysisRuns => Set<AnalysisRun>();
    public DbSet<FlowVersion> FlowVersions => Set<FlowVersion>();
    public DbSet<Requirement> Requirements => Set<Requirement>();
    public DbSet<RequirementCategory> RequirementCategories => Set<RequirementCategory>();
    public DbSet<ClarificationItem> ClarificationItems => Set<ClarificationItem>();
    public DbSet<CrossFlowLink> CrossFlowLinks => Set<CrossFlowLink>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<DiagramAsset> DiagramAssets => Set<DiagramAsset>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();
    public DbSet<GeneratedDocument> GeneratedDocuments => Set<GeneratedDocument>();
    public DbSet<FlowFigure> FlowFigures => Set<FlowFigure>();
    public DbSet<AiAttempt> AiAttempts => Set<AiAttempt>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();
    public DbSet<DocumentJobRun> DocumentJobRuns => Set<DocumentJobRun>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<AiUsage> AiUsages => Set<AiUsage>();
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Every timestamp is UTC. SQL Server hands back Unspecified, so mark it on the way out.
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ---- keys
        b.Entity<UserRole>().HasKey(x => new { x.UserId, x.RoleId });
        b.Entity<ProjectMember>().HasKey(x => new { x.ProjectId, x.UserId });
        b.Entity<ClientUser>().HasKey(x => new { x.ClientId, x.UserId });
        b.Entity<DepartmentAssignment>().HasKey(x => new { x.ProjectDepartmentId, x.UserId });
        b.Entity<DocumentSequence>().HasKey(x => new { x.ProjectId, x.ModuleCode });
        b.Entity<RequirementCategory>().HasKey(x => x.Code);
        b.Entity<SystemSetting>().HasKey(x => x.Key);
        b.Entity<Language>().HasKey(x => x.Code);
        b.Entity<IdempotencyRecord>().HasKey(x => new { x.Key, x.UserId });
        b.Entity<AuditLog>().HasKey(x => x.Id);

        // ---- lengths and uniqueness
        b.Entity<User>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.PreferredLanguage).HasMaxLength(10);
        });
        b.Entity<Role>(e => { e.Property(x => x.Name).HasMaxLength(40); e.HasIndex(x => x.Name).IsUnique(); });
        b.Entity<RefreshToken>(e => { e.Property(x => x.TokenHash).HasMaxLength(128); e.HasIndex(x => x.TokenHash); e.Property(x => x.DeviceInfo).HasMaxLength(200); });
        b.Entity<DeviceToken>(e => { e.Property(x => x.Platform).HasMaxLength(10); e.Property(x => x.PushToken).HasMaxLength(512); e.HasIndex(x => new { x.UserId, x.PushToken }).IsUnique(); });

        b.Entity<Project>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(30); e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.HospitalName).HasMaxLength(200);
            e.Property(x => x.Location).HasMaxLength(200);
            e.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadUserId);
            e.HasOne(x => x.Client).WithMany(c => c.Projects).HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ClientId);
        });
        b.Entity<Client>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Location).HasMaxLength(200);
        });
        b.Entity<ClientUser>(e =>
        {
            e.HasOne(x => x.Client).WithMany(c => c.Users).HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });
        b.Entity<Department>(e => { e.Property(x => x.Code).HasMaxLength(20); e.HasIndex(x => x.Code).IsUnique(); e.Property(x => x.Name).HasMaxLength(120); });
        b.Entity<ProjectDepartment>(e =>
        {
            e.Property(x => x.ModuleCode).HasMaxLength(20);
            e.Property(x => x.CustomName).HasMaxLength(120);
            e.HasIndex(x => new { x.ProjectId, x.ModuleCode }).IsUnique();
            e.Ignore(x => x.DisplayName);
        });
        // a deleted project is hidden from every query (its rows are kept)
        b.Entity<Project>().HasQueryFilter(p => p.DeletedAt == null);
        b.Entity<Flow>().HasQueryFilter(f => f.DeletedAt == null);
        b.Entity<Flow>(e =>
        {
            e.Property(x => x.MenuName).HasMaxLength(200);
            e.HasIndex(x => new { x.ProjectDepartmentId, x.MenuName }).IsUnique();
            e.Property(x => x.Kind).HasMaxLength(4).HasDefaultValue(RequestKind.NR);
            e.HasIndex(x => new { x.ProjectDepartmentId, x.Kind, x.RequestNo }).IsUnique();
            e.HasIndex(x => x.LastActivityAt);
        });

        b.Entity<AiAttempt>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.Outcome).HasMaxLength(24);
            e.HasIndex(x => new { x.Provider, x.Model, x.At });
        });

        b.Entity<FlowFigure>(e =>
        {
            e.Property(x => x.Caption).HasMaxLength(300);
            e.Property(x => x.StepId).HasMaxLength(40);
            e.Property(x => x.BlobPath).HasMaxLength(300);
            e.Property(x => x.ContentType).HasMaxLength(60);
            e.Property(x => x.OriginalName).HasMaxLength(200);
            e.HasIndex(x => new { x.FlowId, x.SortOrder });
            e.HasOne(x => x.Flow).WithMany().HasForeignKey(x => x.FlowId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CaptureEntry>(e =>
        {
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.IntervieweeName).HasMaxLength(120);
            e.Property(x => x.IntervieweeRole).HasMaxLength(120);
            e.Property(x => x.ImageBlobPath).HasMaxLength(400);
            e.Property(x => x.ImageContentType).HasMaxLength(60);
            e.HasOne(x => x.CapturedByUser).WithMany().HasForeignKey(x => x.CapturedBy);
            e.HasIndex(x => new { x.FlowId, x.RefNo }).IsUnique();
            e.HasIndex(x => new { x.FlowId, x.CapturedAt });
            e.HasIndex(x => x.UpdatedAt);
            e.HasOne(x => x.Audio).WithOne(a => a.Entry).HasForeignKey<AudioFile>(a => a.EntryId);
            e.HasOne(x => x.Transcript).WithOne(t => t.Entry).HasForeignKey<Transcript>(t => t.EntryId);
        });
        b.Entity<AudioFile>(e =>
        {
            e.HasIndex(x => x.EntryId).IsUnique();
            e.Property(x => x.BlobPath).HasMaxLength(400);
            e.Property(x => x.ContentType).HasMaxLength(60);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.ReceivedChunks).HasMaxLength(8000);
        });
        b.Entity<Transcript>(e =>
        {
            e.HasIndex(x => x.EntryId).IsUnique();
            e.HasIndex(x => x.UpdatedAt);
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.Language).HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(500);
            e.Ignore(x => x.EffectiveText);
        });

        b.Entity<PromptTemplate>(e => { e.Property(x => x.Key).HasMaxLength(80); e.Property(x => x.Sha256).HasMaxLength(64); e.HasIndex(x => new { x.Key, x.Version }).IsUnique(); });
        b.Entity<AnalysisRun>(e =>
        {
            e.Property(x => x.InputHash).HasMaxLength(64);
            e.Property(x => x.SystemPromptSha256).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(60);
            e.Property(x => x.ErrorCode).HasMaxLength(40);
            e.Property(x => x.ErrorDetail).HasMaxLength(4000);
            e.Property(x => x.EstimatedCost).HasPrecision(12, 4);
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.HasIndex(x => x.InputHash);
            e.HasIndex(x => x.FlowId);
        });
        b.Entity<FlowVersion>(e =>
        {
            e.HasIndex(x => new { x.FlowId, x.VersionNo }).IsUnique();
            e.Property(x => x.ChangeSummary).HasMaxLength(1000);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedBy);
        });
        b.Entity<Requirement>(e =>
        {
            e.Property(x => x.ReqId).HasMaxLength(10);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.CategoryCode).HasMaxLength(20);
            e.Property(x => x.Priority).HasMaxLength(20);
            e.HasIndex(x => new { x.FlowId, x.IsCurrent });
        });
        b.Entity<RequirementCategory>(e => { e.Property(x => x.Code).HasMaxLength(10); e.Property(x => x.Name).HasMaxLength(60); e.Property(x => x.Definition).HasMaxLength(400); });
        b.Entity<ClarificationItem>(e =>
        {
            e.Property(x => x.QuestionKey).HasMaxLength(10);
            e.Property(x => x.Section).HasMaxLength(40);
            e.Property(x => x.Question).HasMaxLength(1000);
            e.Property(x => x.RelatedGapText).HasMaxLength(1000);
            e.Property(x => x.Answer).HasMaxLength(4000);
            e.Property(x => x.DismissReason).HasMaxLength(400);
            e.HasIndex(x => new { x.FlowId, x.QuestionKey }).IsUnique();
            e.HasIndex(x => new { x.FlowId, x.Status });
            e.HasIndex(x => x.UpdatedAt);
        });
        b.Entity<CrossFlowLink>(e =>
        {
            e.Property(x => x.FromItemId).HasMaxLength(10);
            e.Property(x => x.ToItemId).HasMaxLength(10);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Basis).HasMaxLength(40);
            e.HasIndex(x => new { x.ProjectId, x.Status });
        });
        b.Entity<Comment>(e =>
        {
            e.Property(x => x.Section).HasMaxLength(40);
            e.Property(x => x.ItemId).HasMaxLength(20);
            e.Property(x => x.Text).HasMaxLength(2000);
            e.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId);
            e.HasIndex(x => new { x.FlowId, x.CreatedAt });
            e.HasIndex(x => x.UpdatedAt);
        });

        b.Entity<DiagramAsset>(e => { e.Property(x => x.SvgPath).HasMaxLength(400); e.Property(x => x.PngPath).HasMaxLength(400); e.Property(x => x.MermaidPath).HasMaxLength(400); e.Property(x => x.Sha256).HasMaxLength(64); });
        // two writers taking the same number: the second save fails and retries with the next one
        b.Entity<DocumentSequence>(e => { e.Property(x => x.ModuleCode).HasMaxLength(20); e.Property(x => x.LastNumber).IsConcurrencyToken(); });
        b.Entity<DocumentJobRun>(e =>
        {
            e.Property(x => x.AssignedBy).HasMaxLength(120);
            e.Property(x => x.ErrorCode).HasMaxLength(40);
            e.Property(x => x.ErrorDetail).HasMaxLength(2000);
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.HasIndex(x => new { x.FlowId, x.Status });
        });
        b.Entity<GeneratedDocument>(e =>
        {
            e.Property(x => x.DocNumber).HasMaxLength(40);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.AssignedBy).HasMaxLength(120);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasIndex(x => new { x.ProjectId, x.DocNumber }).IsUnique();
        });
        b.Entity<DocumentVersion>(e =>
        {
            e.HasIndex(x => new { x.GeneratedDocumentId, x.VersionNo }).IsUnique();
            e.Property(x => x.DocxPath).HasMaxLength(400);
            e.Property(x => x.PdfPath).HasMaxLength(400);
            e.Property(x => x.TemplateVersion).HasMaxLength(40);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.ApprovalNote).HasMaxLength(1000);
        });
        b.Entity<DocumentTemplate>(e => { e.Property(x => x.Name).HasMaxLength(80); e.Property(x => x.Version).HasMaxLength(40); e.Property(x => x.BlobPath).HasMaxLength(400); });

        b.Entity<AuditLog>(e =>
        {
            e.Property(x => x.Action).HasMaxLength(20);
            e.Property(x => x.EntityType).HasMaxLength(60);
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.CorrelationId).HasMaxLength(60);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.HasIndex(x => new { x.EntityType, x.EntityId, x.At });
            e.HasIndex(x => new { x.ProjectId, x.At });
        });
        b.Entity<SystemSetting>(e => e.Property(x => x.Key).HasMaxLength(80));
        b.Entity<AiUsage>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.Model).HasMaxLength(80);
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.UserId, x.At });
            e.HasIndex(x => new { x.ProjectId, x.At });
        });
        b.Entity<Language>(e => { e.Property(x => x.Code).HasMaxLength(10); e.Property(x => x.Name).HasMaxLength(60); e.Property(x => x.SttLocale).HasMaxLength(20); });
        b.Entity<IdempotencyRecord>(e => e.Property(x => x.Path).HasMaxLength(300));

        // ---- relationships that cascade (children owned by the parent); everything else restricts
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
        b.Entity<ProjectMember>().HasOne(x => x.Project).WithMany(p => p.Members).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectMember>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DepartmentAssignment>().HasOne(x => x.ProjectDepartment).WithMany(p => p.Assignments).HasForeignKey(x => x.ProjectDepartmentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DepartmentAssignment>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<UserRole>().HasOne(x => x.User).WithMany(u => u.Roles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserRole>().HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);

        // ---- enums are stored as text, concurrency stamp on every auditable entity
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (!type.IsEnum) continue;
                var converter = (ValueConverter)Activator.CreateInstance(typeof(EnumToStringConverter<>).MakeGenericType(type))!;
                property.SetValueConverter(converter);
                property.SetMaxLength(32);
            }
            if (typeof(AuditableEntity).IsAssignableFrom(entity.ClrType))
            {
                var stamp = entity.FindProperty(nameof(AuditableEntity.ConcurrencyStamp))!;
                stamp.IsConcurrencyToken = true;
                stamp.SetMaxLength(36);
            }
        }
    }
}

internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

internal sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)) : v,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
