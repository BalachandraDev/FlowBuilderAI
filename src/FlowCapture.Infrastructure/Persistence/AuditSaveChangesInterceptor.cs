using System.Text.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlowCapture.Infrastructure.Persistence;

/// <summary>
/// On every SaveChanges: fills CreatedAt/UpdatedAt/By and rotates the concurrency stamp, then writes AuditLog rows
/// (who, what, old and new values, correlation id). Free text (transcripts, entries, prompts, answers) is never copied into the audit log.
/// </summary>
public sealed class AuditSaveChangesInterceptor(IClock clock, ICurrentUser user) : SaveChangesInterceptor
{
    private static readonly HashSet<Type> NotAudited =
    [
        typeof(AuditLog), typeof(AnalysisRun), typeof(RefreshToken), typeof(DeviceToken), typeof(IdempotencyRecord),
        typeof(Requirement), typeof(DocumentSequence),
    ];

    private static readonly HashSet<string> Redacted = new(StringComparer.Ordinal)
    {
        "PasswordHash", "TokenHash", "PushToken", "Content", "RawText", "MaskedText", "UserEditedText", "Notes", "Answer",
        "Text", "SegmentsJson", "RawOutput", "ResultJson", "MermaidSource", "IntervieweeName", "OptionsJson", "Question",
        "ApprovalNote", "ChangeSummary", "DismissReason", "RelatedGapText",
    };

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
        { nameof(AuditableEntity.UpdatedAt), nameof(AuditableEntity.UpdatedBy), nameof(AuditableEntity.ConcurrencyStamp) };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null) return;
        context.ChangeTracker.DetectChanges();
        var now = clock.UtcNow;
        var userId = user.UserId;

        var changed = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();

        foreach (var entry in changed.Where(e => e.Entity is AuditableEntity))
        {
            var entity = (AuditableEntity)entry.Entity;
            if (entry.State == EntityState.Added)
            {
                entity.CreatedAt = now;
                entity.CreatedBy = userId;
                entity.UpdatedAt = now;
                entity.UpdatedBy = userId;
                entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            }
            else if (entry.State == EntityState.Modified)
            {
                entity.UpdatedAt = now;
                entity.UpdatedBy = userId;
                entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");   // original value stays in the WHERE clause
            }
        }

        var audits = new List<AuditLog>();
        foreach (var entry in changed.Where(e => !NotAudited.Contains(e.Entity.GetType())))
        {
            var audit = Describe(entry, now, userId);
            if (audit is not null) audits.Add(audit);
        }
        if (audits.Count > 0) context.Set<AuditLog>().AddRange(audits);
    }

    private AuditLog? Describe(EntityEntry entry, DateTime now, Guid? userId)
    {
        var action = entry.State switch { EntityState.Added => "Create", EntityState.Modified => "Update", _ => "Delete" };
        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();

        // a stored secret (the AI key) is encrypted, but its ciphertext still has no place in a log anyone can export
        var secretRow = entry.Entity is SystemSetting { Key: var settingKey } && settingKey.StartsWith("Secret.", StringComparison.Ordinal);

        foreach (var p in entry.Properties)
        {
            var name = p.Metadata.Name;
            if (p.Metadata.IsPrimaryKey()) continue;
            var hide = secretRow && name == nameof(SystemSetting.Value);
            switch (entry.State)
            {
                case EntityState.Added:
                    if (!Noise.Contains(name) && name is not ("CreatedAt" or "CreatedBy")) newValues[name] = Value(name, p.CurrentValue, hide);
                    break;
                case EntityState.Deleted:
                    if (!Noise.Contains(name)) oldValues[name] = Value(name, p.OriginalValue, hide);
                    break;
                case EntityState.Modified when p.IsModified && !Noise.Contains(name):
                    oldValues[name] = Value(name, p.OriginalValue, hide);
                    newValues[name] = Value(name, p.CurrentValue, hide);
                    break;
            }
        }
        if (entry.State == EntityState.Modified && newValues.Count == 0) return null;   // only the stamp moved

        var key = string.Join("|", entry.Properties.Where(p => p.Metadata.IsPrimaryKey()).Select(p => p.CurrentValue?.ToString() ?? p.OriginalValue?.ToString()));
        return new AuditLog
        {
            At = now, UserId = userId, Action = action, EntityType = entry.Metadata.ClrType.Name, EntityId = key,
            ProjectId = ProjectIdOf(entry.Entity, entry.Context),
            OldValues = oldValues.Count == 0 ? null : JsonSerializer.Serialize(oldValues),
            NewValues = newValues.Count == 0 ? null : JsonSerializer.Serialize(newValues),
            CorrelationId = user.CorrelationId, IpAddress = user.IpAddress,
        };
    }

    private static object? Value(string name, object? value, bool hide = false)
    {
        if (value is null) return null;
        if (hide || Redacted.Contains(name)) return value is string s ? $"[redacted {s.Length} chars]" : "[redacted]";
        return value is Enum ? value.ToString() : value;
    }

    /// <summary>Best effort: uses navigations and tracked entities that are already loaded, never queries.</summary>
    private static Guid? ProjectIdOf(object entity, DbContext context) => entity switch
    {
        Project p => p.Id,
        ProjectMember m => m.ProjectId,
        ProjectDepartment d => d.ProjectId,
        CrossFlowLink l => l.ProjectId,
        GeneratedDocument g => g.ProjectId,
        DocumentJobRun j => j.ProjectId,
        DocumentVersion v => TrackedDocument(context, v.GeneratedDocumentId)?.ProjectId,
        Flow f => f.ProjectDepartment?.ProjectId,
        FlowVersion v => ProjectIdOfFlow(context, v.FlowId),
        DiagramAsset a => a.ProjectId,
        CaptureEntry e => e.Flow?.ProjectDepartment?.ProjectId ?? ProjectIdOfFlow(context, e.FlowId),
        ClarificationItem c => c.Flow?.ProjectDepartment?.ProjectId ?? ProjectIdOfFlow(context, c.FlowId),
        Comment c => c.Flow?.ProjectDepartment?.ProjectId ?? ProjectIdOfFlow(context, c.FlowId),
        _ => null,
    };

    private static GeneratedDocument? TrackedDocument(DbContext context, Guid id) =>
        context.ChangeTracker.Entries<GeneratedDocument>().Select(e => e.Entity).FirstOrDefault(d => d.Id == id);

    private static Guid? ProjectIdOfFlow(DbContext context, Guid flowId)
    {
        var flow = context.ChangeTracker.Entries<Flow>().Select(e => e.Entity).FirstOrDefault(f => f.Id == flowId);
        if (flow?.ProjectDepartment is { } pd) return pd.ProjectId;
        if (flow is null) return null;
        return context.ChangeTracker.Entries<ProjectDepartment>().Select(e => e.Entity).FirstOrDefault(d => d.Id == flow.ProjectDepartmentId)?.ProjectId;
    }
}
