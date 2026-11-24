using FlowCapture.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Common;

public static class FlowActivity
{
    /// <summary>
    /// Records "something happened on this workflow just now". Written straight to the database, not through the tracked flow, so several
    /// notes added at the same moment (a typed note, a photo and a recording, or two people) never collide on the flow's edit stamp.
    /// </summary>
    public static Task TouchFlowAsync(this IAppDbContext db, Guid flowId, DateTime now, CancellationToken ct) =>
        db.Flows.Where(f => f.Id == flowId).ExecuteUpdateAsync(s => s.SetProperty(f => f.LastActivityAt, now).SetProperty(f => f.UpdatedAt, now), ct);
}
