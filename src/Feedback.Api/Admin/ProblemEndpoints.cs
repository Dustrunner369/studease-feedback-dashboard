using Feedback.Api.Data;
using Feedback.Api.Domain;
using Feedback.Api.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Feedback.Api.Admin;

/// <summary>The Problems table and the Problem drawer.</summary>
public static class ProblemEndpoints
{
    public static void MapProblems(this RouteGroupBuilder admin)
    {
        admin.MapGet("/problems", async (string? category, FeedbackDbContext db, CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(category) && !Categories.IsValid(category))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["category"] = ["Unknown Category."] });

            // Open Problems only; Shipped and Won't do belong to the Archive.
            var problems = db.Problems.AsNoTracking()
                .Where(p => p.Status != ProblemStatuses.Shipped && p.Status != ProblemStatuses.WontDo);
            if (!string.IsNullOrEmpty(category)) problems = problems.Where(p => p.Category == category);

            var rows = await problems
                .Select(p => new
                {
                    p.Id, p.Title, p.Category, p.Status, p.CreatedAt, p.RecommendedScore,
                    FeatureRequests = p.FeatureRequests.Count,
                    Upvotes = db.Upvotes.Count(u => p.FeatureRequests.Select(r => r.Id).Contains(u.FeatureRequestId)),
                })
                .ToListAsync(ct);

            // Ordered by Recommended score (not shown in the table); not-yet-scored Problems last;
            // ties by Reach points (Feature Requests × 3 + Upvotes), then newest.
            var table = rows
                .OrderByDescending(p => p.RecommendedScore.HasValue).ThenByDescending(p => p.RecommendedScore)
                .ThenByDescending(p => p.FeatureRequests * 3 + p.Upvotes).ThenByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
                .Select(p => new ProblemRow(p.Id, p.Title, p.Category, p.Status, p.FeatureRequests, p.Upvotes, null))
                .ToList();
            var needsReview = await db.FeatureRequests.CountAsync(r => r.ProblemId == null, ct);
            return Results.Ok(new ProblemsPage(needsReview, table));
        });

        admin.MapGet("/problems/{id:long}", async (long id, FeedbackDbContext db, IOptions<PriorityCriteria> criteria, CancellationToken ct) =>
            await DrawerAsync(id, db, criteria.Value, ct) is { } drawer ? Results.Ok(drawer) : Results.NotFound());
    }

    /// <summary>The Problem drawer, or null if there is no such Problem.</summary>
    internal static async Task<ProblemDrawer?> DrawerAsync(long id, FeedbackDbContext db, PriorityCriteria criteria, CancellationToken ct)
    {
        var problem = await db.Problems.AsNoTracking().Include(p => p.CurrentScoring)
            .SingleOrDefaultAsync(p => p.Id == id, ct);
        if (problem is null) return null;

        var requests = await db.FeatureRequests.AsNoTracking().Where(r => r.ProblemId == id)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Select(r => new DrawerRequest(
                r.Id, r.Title, r.Description, r.Email, r.Category, r.CreatedAt,
                db.Upvotes.Count(u => u.FeatureRequestId == r.Id), r.MatchReason, r.IsBundled))
            .ToListAsync(ct);
        return new ProblemDrawer(
            problem.Id, problem.Title, problem.Description, problem.Category, problem.Status, null,
            Recommended(problem, criteria), problem.ScoringFailed, requests);
    }

    // The stored score and level, explained criterion by criterion (null until a scoring has succeeded).
    private static RecommendedView? Recommended(Problem p, PriorityCriteria c)
    {
        if (p.CurrentScoring is not { } s || p.RecommendedScore is not { } score || p.RecommendedLevel is not { } level
            || p.ReachRating is not { } reach) return null;
        var w = c.Weights;
        return new RecommendedView(level, score,
        [
            new(Criterion.StrategicFit, w.StrategicFit, s.StrategicFit, PriorityCriteria.Points(w.StrategicFit, s.StrategicFit),
                s.StrategicFitReason, s.StrategicFitGoal),
            new(Criterion.Reach, w.Reach, reach, PriorityCriteria.Points(w.Reach, reach),
                $"{p.ReachPoints} Reach points (Feature Requests × 3 + Upvotes).", null),
            new(Criterion.Pain, w.Pain, s.Pain, PriorityCriteria.Points(w.Pain, s.Pain), s.PainReason, null),
            new(Criterion.RetentionRisk, w.RetentionRisk, s.RetentionRisk, PriorityCriteria.Points(w.RetentionRisk, s.RetentionRisk),
                s.RetentionRiskReason, null),
        ]);
    }
}

/// <summary>✦ Recommended Priority: level, 0–100 score, and how it was worked out.</summary>
public sealed record RecommendedView(string Level, int Score, IReadOnlyList<CriterionScore> Breakdown);

/// <summary>One criterion's rating (1–5), its share of the score, and why. Goal is Strategic fit's named goal.</summary>
public sealed record CriterionScore(string Criterion, int Weight, int Rating, decimal Points, string Reason, string? Goal);

public sealed record ProblemsPage(int NeedsReview, IReadOnlyList<ProblemRow> Problems);

/// <summary>One row of the Problems table. Priority is the product team's; null = "Not set".</summary>
public sealed record ProblemRow(
    long Id, string Title, string Category, string Status, int FeatureRequests, int Upvotes, string? Priority);

/// <summary>
/// The Problem drawer. Admin-only, so Feature Requests include the Requester email.
/// Recommended is null until a scoring succeeds; ScoringFailed means the last scoring call failed (offer Retry).
/// </summary>
public sealed record ProblemDrawer(
    long Id, string Title, string Description, string Category, string Status, string? Priority,
    RecommendedView? Recommended, bool ScoringFailed,
    IReadOnlyList<DrawerRequest> FeatureRequests);

/// <summary>A Feature Request in the drawer. Category is the Requester's own pick; MatchReason is "✦ Why it's here".</summary>
public sealed record DrawerRequest(
    long Id, string Title, string Description, string Email, string Category, DateTimeOffset CreatedAt,
    int Upvotes, string? MatchReason, bool IsBundled);
