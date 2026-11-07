using Feedback.Api.Data;
using Feedback.Api.Scoring;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Admin;

/// <summary>The Metrics panel: the numbers <c>seed load</c> saved, and the last priority consistency check.</summary>
public static class MetricsEndpoints
{
    /// <summary>Targets set for this project (assumption 17).</summary>
    public static readonly MetricTargets Targets = new(DuplicateCatchRate: 0.90, GroupingAccuracy: 0.85, PriorityConsistency: 0.90);

    public static void MapMetrics(this RouteGroupBuilder admin)
    {
        admin.MapGet("/metrics", async (FeedbackDbContext db, CancellationToken ct) =>
            Results.Ok(await MetricsAsync(db, ct)));

        // Synchronous: returns once every re-score is done. Never changes a saved Recommended Priority.
        admin.MapPost("/metrics/consistency-check", async (ConsistencyCheck check, FeedbackDbContext db, CancellationToken ct) =>
        {
            await check.RunAsync(ct);
            return Results.Ok(await MetricsAsync(db, ct));
        });
    }

    private static async Task<MetricsView> MetricsAsync(FeedbackDbContext db, CancellationToken ct)
    {
        var seed = await db.SeedMetrics.AsNoTracking().OrderByDescending(m => m.Id)
            .Select(m => new SeedMetricsView(m.ComputedAt, m.DuplicateCatchRate, m.GroupingAccuracy,
                m.AiProblemCount, m.IntendedProblemCount, m.FailedMatches))
            .FirstOrDefaultAsync(ct);
        return new MetricsView(Targets, seed, await ConsistencyAsync(db, ct));
    }

    private static async Task<ConsistencyView?> ConsistencyAsync(FeedbackDbContext db, CancellationToken ct)
    {
        var run = await db.ConsistencyRuns.AsNoTracking().OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        if (run is null) return null;
        var results = await db.ConsistencyResults.AsNoTracking().Where(r => r.RunId == run.Id)
            .Join(db.Problems, r => r.ProblemId, p => p.Id, (r, p) => new { r.ProblemId, p.Title, r.Levels, r.Scores, r.CouldntCheck })
            .OrderBy(r => r.ProblemId)
            .ToListAsync(ct);

        var checkedOnes = results.Where(r => !r.CouldntCheck).ToList();
        var changed = checkedOnes.Where(r => r.Levels.Distinct().Count() > 1)
            .Select(r => new ChangedLevelView(r.ProblemId, r.Title, r.Levels, r.Scores)).ToList();
        return new ConsistencyView(
            run.RanAt,
            checkedOnes.Count,
            checkedOnes.Count == 0 ? null : (double)(checkedOnes.Count - changed.Count) / checkedOnes.Count,
            checkedOnes.Count == 0 ? null : checkedOnes.Average(r => (double)(r.Scores.Max() - r.Scores.Min())),
            results.Count - checkedOnes.Count,
            changed);
    }
}

/// <summary>Rates are 0–1. Seed is null until a <c>seed load</c>; Consistency is null until a check runs.</summary>
public sealed record MetricsView(MetricTargets Targets, SeedMetricsView? Seed, ConsistencyView? Consistency);

public sealed record MetricTargets(double DuplicateCatchRate, double GroupingAccuracy, double PriorityConsistency);

public sealed record SeedMetricsView(
    DateTimeOffset ComputedAt, double? DuplicateCatchRate, double? GroupingAccuracy,
    int AiProblemCount, int IntendedProblemCount, int FailedMatches);

/// <summary>
/// The last consistency check. <c>Checked</c> = Problems whose 3 re-scores all succeeded (the headline's base);
/// <c>SameLevelShare</c> (0–1) and <c>AverageSpread</c> (mean of max − min of the 0–100 scores) are over those,
/// null when none. <c>CouldntCheck</c> = Problems with a failed re-score.
/// </summary>
public sealed record ConsistencyView(
    DateTimeOffset RanAt, int Checked, double? SameLevelShare, double? AverageSpread, int CouldntCheck,
    IReadOnlyList<ChangedLevelView> Changed);

public sealed record ChangedLevelView(long ProblemId, string Title, string[] Levels, int[] Scores);
