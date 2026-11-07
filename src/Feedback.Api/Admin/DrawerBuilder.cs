using System.Text.Json;
using Feedback.Api.Briefs;
using Feedback.Api.Data;
using Feedback.Api.Scoring;
using Feedback.Api.Solutions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Feedback.Api.Admin;

/// <summary>Builds the Problem drawer (scoped): every endpoint that answers with a drawer uses this.</summary>
public sealed class DrawerBuilder(FeedbackDbContext db, IOptions<PriorityCriteria> options)
{
    /// <summary>The Problem drawer, or null if there is no such Problem.</summary>
    public async Task<ProblemDrawer?> BuildAsync(long id, CancellationToken ct)
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
        var decisions = (await db.DecisionLog.AsNoTracking().Where(d => d.ProblemId == id)
                .OrderByDescending(d => d.Id).ToListAsync(ct))
            .Select(d => new DecisionView(d.Kind, d.Reason, JsonDocument.Parse(d.Details).RootElement, d.CreatedAt))
            .ToList();
        return new ProblemDrawer(
            problem.Id, problem.Title, problem.Description, problem.Category, problem.Status, problem.Priority,
            problem.PriorityOverrideReason, Recommended(problem, options.Value), problem.ScoringFailed, requests, decisions,
            problem.ClosedAt, GitHubIssueView.Of(problem),
            SolutionsView.Of(await db.SolutionSuggestions.AsNoTracking().SingleOrDefaultAsync(s => s.ProblemId == id, ct)),
            await DecisionBriefs.ViewAsync(db, id, requests.Select(r => r.Id).ToList(), ct));
    }

    /// <summary>200 with the drawer, or 404 if the Problem is gone.</summary>
    public async Task<IResult> OkAsync(long id, CancellationToken ct) =>
        await BuildAsync(id, ct) is { } drawer ? Results.Ok(drawer) : Results.NotFound();

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
