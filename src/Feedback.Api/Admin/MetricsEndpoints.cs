using Feedback.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Admin;

/// <summary>The Metrics panel: the numbers <c>seed load</c> saved (the consistency check joins them later).</summary>
public static class MetricsEndpoints
{
    /// <summary>Targets set for this project (assumption 17).</summary>
    public static readonly MetricTargets Targets = new(DuplicateCatchRate: 0.90, GroupingAccuracy: 0.85);

    public static void MapMetrics(this RouteGroupBuilder admin)
    {
        admin.MapGet("/metrics", async (FeedbackDbContext db, CancellationToken ct) =>
        {
            var seed = await db.SeedMetrics.AsNoTracking().OrderByDescending(m => m.Id)
                .Select(m => new SeedMetricsView(m.ComputedAt, m.DuplicateCatchRate, m.GroupingAccuracy,
                    m.AiProblemCount, m.IntendedProblemCount, m.FailedMatches))
                .FirstOrDefaultAsync(ct);
            return Results.Ok(new MetricsView(Targets, seed));
        });
    }
}

/// <summary>Rates are 0–1. Seed is null until a <c>seed load</c>.</summary>
public sealed record MetricsView(MetricTargets Targets, SeedMetricsView? Seed);

public sealed record MetricTargets(double DuplicateCatchRate, double GroupingAccuracy);

public sealed record SeedMetricsView(
    DateTimeOffset ComputedAt, double? DuplicateCatchRate, double? GroupingAccuracy,
    int AiProblemCount, int IntendedProblemCount, int FailedMatches);
