using System.Text.Json;
using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using FlowCapture.Shared.Content;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Flows;

/// <summary>
/// The single place that creates FlowVersion rows. It also applies the lifecycle transition and keeps the
/// Requirement and ClarificationItem projections in step. Callers own the SaveChanges / transaction.
/// </summary>
public sealed class FlowVersionWriter(IAppDbContext db, IClock clock)
{
    public async Task<FlowVersion> AddAsync(
        Flow flow, FlowContent content, VersionSource source, Guid? analysisRunId,
        IEnumerable<string> lockedIds, string? changeSummary, Guid? userId, CancellationToken ct)
    {
        // Manual edits and rollbacks always get a diagram that matches the data. Analysis results are checked by the runner.
        if (source != VersionSource.Analysis)
            content.Mermaid = MermaidBuilder.Build(content);

        var previousStatus = flow.Status;
        flow.Status = source == VersionSource.Analysis
            ? FlowStatusMachine.OnAnalysisSucceeded(previousStatus)
            : FlowStatusMachine.OnContentChanged(previousStatus);
        if (previousStatus is FlowStatus.Confirmed or FlowStatus.DocumentsGenerated)
            flow.ConfirmedVersionNo = null;

        var now = clock.UtcNow;
        var version = new FlowVersion
        {
            FlowId = flow.Id,
            VersionNo = flow.CurrentVersionNo + 1,
            Source = source,
            AnalysisRunId = analysisRunId,
            ParentVersionNo = flow.CurrentVersionNo == 0 ? null : flow.CurrentVersionNo,
            Content = FlowJson.Serialize(content),
            LockedIds = JsonSerializer.Serialize(lockedIds.Distinct(StringComparer.Ordinal).ToArray()),
            ChangeSummary = changeSummary is { Length: > 1000 } s ? s[..1000] : changeSummary,
            CreatedAt = now,
            CreatedBy = userId,
        };
        db.FlowVersions.Add(version);
        flow.CurrentVersionNo = version.VersionNo;
        flow.LastActivityAt = now;

        foreach (var old in await db.Requirements.Where(r => r.FlowId == flow.Id && r.IsCurrent).ToListAsync(ct))
            old.IsCurrent = false;
        foreach (var r in content.Requirements)
            db.Requirements.Add(new Requirement
            {
                FlowId = flow.Id, FlowVersionId = version.Id, ReqId = r.ReqId, Description = r.Description,
                CategoryCode = r.Category, Priority = r.Priority, IsCurrent = true,
            });

        await SyncClarificationsAsync(flow.Id, content, version.VersionNo, ct);
        return version;
    }

    private async Task SyncClarificationsAsync(Guid flowId, FlowContent content, int versionNo, CancellationToken ct)
    {
        var existing = await db.ClarificationItems.Where(c => c.FlowId == flowId).ToListAsync(ct);
        var gaps = content.Gaps.ToDictionary(g => g.Id, g => g.Description, StringComparer.Ordinal);

        foreach (var q in content.ClarificationQuestions)
        {
            var gapText = q.RelatedGapId is not null && gaps.TryGetValue(q.RelatedGapId, out var g) ? g : null;
            var options = q.Options is { Count: > 0 } ? JsonSerializer.Serialize(q.Options) : null;
            var row = existing.FirstOrDefault(c => c.QuestionKey == q.Id);
            if (row is null)
            {
                db.ClarificationItems.Add(new ClarificationItem
                {
                    FlowId = flowId, QuestionKey = q.Id, FirstSeenVersionNo = versionNo, Section = q.Section,
                    Question = q.Question, RelatedGapText = gapText, OptionsJson = options, Status = ClarificationStatus.Open,
                });
            }
            else if (row.Status == ClarificationStatus.Open)
            {
                row.Section = q.Section;
                row.Question = q.Question;
                row.RelatedGapText = gapText;
                row.OptionsJson = options;
            }
        }

        var present = content.ClarificationQuestions.Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in existing.Where(c => c.Status == ClarificationStatus.Open
                                                && !present.Contains(c.QuestionKey)
                                                && !c.QuestionKey.StartsWith('X')))
        {
            row.Status = ClarificationStatus.Dismissed;
            row.DismissReason = $"No longer raised in version {versionNo}.";
        }
    }
}
