using System.Globalization;
using FlowCapture.Application.Abstractions;
using FlowCapture.Application.Services;
using FlowCapture.Domain;
using FlowCapture.Shared.Dtos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FlowCapture.Application.Features.AiUsageFeatures;

public sealed record GetAiUsageSummaryQuery(string? Month) : IRequest<AiUsageSummaryDto>;

public sealed class GetAiUsageSummaryValidator : AbstractValidator<GetAiUsageSummaryQuery>
{
    public GetAiUsageSummaryValidator() =>
        RuleFor(x => x.Month).Must(m => m is null || DateTime.TryParseExact(m, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .WithMessage("Month must look like 2026-11.");
}

/// <summary>
/// The AI usage page: tokens used in a month, by what they were for (analysis, documents, voice, photos), by day, by person and by
/// project. An administrator sees everyone's; anybody else sees only their own.
/// </summary>
public sealed class AiUsageHandlers(IAppDbContext db, ICurrentUser user, IClock clock, ISettingsService settings, IAiQuota quota, IModelQuota modelQuota) : IRequestHandler<GetAiUsageSummaryQuery, AiUsageSummaryDto>
{
    public async Task<AiUsageSummaryDto> Handle(GetAiUsageSummaryQuery r, CancellationToken ct)
    {
        var uid = user.RequireUserId();
        var all = user.IsInRole(Shared.AppRoles.Admin);
        var now = clock.UtcNow;
        var first = r.Month is null ? new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            : DateTime.ParseExact(r.Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var next = first.AddMonths(1);
        var previous = first.AddMonths(-1);

        IQueryable<AiUsage> scope = db.AiUsages.AsNoTracking();
        if (!all) scope = scope.Where(x => x.UserId == uid);

        var rows = await scope.Where(x => x.At >= first && x.At < next).OrderByDescending(x => x.At).ToListAsync(ct);
        var lastMonth = await scope.Where(x => x.At >= previous && x.At < first).GroupBy(_ => 1)
            .Select(g => new { Calls = g.Count(), In = g.Sum(x => (long)x.InputTokens), Out = g.Sum(x => (long)x.OutputTokens) }).FirstOrDefaultAsync(ct);

        static AiUsageTotalsDto Totals(IEnumerable<AiUsage> xs)
        {
            var list = xs.ToList();
            long i = list.Sum(x => (long)x.InputTokens), o = list.Sum(x => (long)x.OutputTokens);
            return new AiUsageTotalsDto(list.Count, i, o, i + o);
        }

        var today = now.Date;
        var userIds = rows.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().ToList();
        var projectIds = rows.Where(x => x.ProjectId.HasValue).Select(x => x.ProjectId!.Value).Distinct().ToList();
        var userNames = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var projectNames = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.HospitalName, ct);

        var byKind = rows.GroupBy(x => x.Kind).OrderByDescending(g => g.Sum(x => (long)x.InputTokens + x.OutputTokens))
            .Select(g => new AiUsageKindDto(g.Key.ToString(), g.Count(), g.Sum(x => (long)x.InputTokens), g.Sum(x => (long)x.OutputTokens), g.Sum(x => (long)x.InputTokens + x.OutputTokens))).ToList();

        // every day of the month up to today (or the whole month in the past), so the chart has no gaps
        var lastDay = first.Year == now.Year && first.Month == now.Month ? today : next.AddDays(-1);
        var byDay = Enumerable.Range(0, (lastDay - first).Days + 1).Select(i => first.AddDays(i)).Select(d =>
        {
            var day = rows.Where(x => x.At.Date == d).ToList();
            return new AiUsageDayDto(d.ToString("yyyy-MM-dd"), day.Count, day.Sum(x => (long)x.InputTokens + x.OutputTokens));
        }).ToList();

        var byUser = !all ? [] : rows.GroupBy(x => x.UserId).OrderByDescending(g => g.Sum(x => (long)x.InputTokens + x.OutputTokens)).Take(20)
            .Select(g => new AiUsageWhoDto(g.Key, g.Key is { } id && userNames.TryGetValue(id, out var n) ? n : "System", g.Count(), g.Sum(x => (long)x.InputTokens + x.OutputTokens))).ToList();
        var byProject = rows.Where(x => x.ProjectId.HasValue).GroupBy(x => x.ProjectId).OrderByDescending(g => g.Sum(x => (long)x.InputTokens + x.OutputTokens)).Take(20)
            .Select(g => new AiUsageWhoDto(g.Key, g.Key is { } id && projectNames.TryGetValue(id, out var n) ? n : "Removed project", g.Count(), g.Sum(x => (long)x.InputTokens + x.OutputTokens))).ToList();

        var recent = rows.Take(25).Select(x => new AiUsageCallDto(x.At, x.Kind.ToString(),
            x.UserId is { } id && userNames.TryGetValue(id, out var n) ? n : "System",
            x.ProjectId is { } pid && projectNames.TryGetValue(pid, out var pn) ? pn : "", x.Model, x.InputTokens, x.OutputTokens, x.DurationMs, x.Success)).ToList();

        // every AI service and model that was used, with its own totals (the people and projects above are the same tokens seen another way)
        var byModel = rows.GroupBy(x => (x.Provider, x.Model)).OrderByDescending(g => g.Sum(x => (long)x.InputTokens + x.OutputTokens))
            .Select(g => new AiModelUsageDto(g.Key.Provider, g.Key.Model, g.Count(), g.Sum(x => (long)x.InputTokens), g.Sum(x => (long)x.OutputTokens), g.Sum(x => (long)x.InputTokens + x.OutputTokens))).ToList();
        // the free allowances are an administrator's concern: today's use of each free AI against the limit that was set
        var quotas = !all ? [] : (await quota.GetAllAsync(ct)).Select(q => new AiQuotaDto(q.Provider, q.UsedRequests, q.UsedTokens, q.LimitRequests, q.LimitTokens, q.ResetsAtUtc, q.Reached, q.Reached ? q.Message : null)).ToList();

        return new AiUsageSummaryDto(first.ToString("yyyy-MM"), all, (await settings.GetProviderAsync(ct)).Provider, Totals(rows), Totals(rows.Where(x => x.At >= today)),
            new AiUsageTotalsDto(lastMonth?.Calls ?? 0, lastMonth?.In ?? 0, lastMonth?.Out ?? 0, (lastMonth?.In ?? 0) + (lastMonth?.Out ?? 0)),
            byKind, byDay, byUser, byProject, recent, byModel, quotas, !all ? [] : (await modelQuota.GetKnownAsync(ct)).Select(m => m.ToDto()).ToList());
    }
}
