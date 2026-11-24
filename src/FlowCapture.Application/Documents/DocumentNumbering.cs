using FlowCapture.Application.Abstractions;
using FlowCapture.Domain;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Documents;

/// <summary>
/// Hands out document numbers (MRD-001, MRD-002 ...) one per module and project. The counter row carries a concurrency check,
/// so if two jobs take a number at the same moment the second save fails and retries with the next number.
/// </summary>
public sealed class DocumentNumbering(IAppDbContext db)
{
    public async Task<int> NextAsync(Guid projectId, string sequenceKey, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var row = await db.DocumentSequences.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.ModuleCode == sequenceKey, ct);
            if (row is null) db.DocumentSequences.Add(row = new DocumentSequence { ProjectId = projectId, ModuleCode = sequenceKey, LastNumber = 0 });
            row.LastNumber++;
            try
            {
                await db.SaveChangesAsync(ct);
                return row.LastNumber;
            }
            catch (DbUpdateException) when (attempt < 8)
            {
                // someone else took this number (or created the counter) first: forget our copy and try again
                db.Entry(row).State = EntityState.Detached;
                await Task.Delay(Random.Shared.Next(15, 80), ct);
            }
        }
    }

    public static string FlowNumber(string moduleCode, int n) => $"{moduleCode}-{n:000}";
    public static string ProjectNumber(string projectCode, int n) => $"PRJ-{projectCode}-C{n:00}";
}
