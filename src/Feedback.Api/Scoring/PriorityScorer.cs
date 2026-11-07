using Feedback.Api.Ai;
using Feedback.Api.Data;
using Feedback.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Feedback.Api.Scoring;

/// <summary>
/// Keeps a Problem's Recommended Priority current. Two entry points:
/// <see cref="RescoreAsync"/> (one AI call: after a new link, a move, a merge, or Retry) and
/// <see cref="RefreshReachAsync"/> (no AI call: after an Upvote or un-upvote).
/// Closed Problems (Shipped, Won't do) are frozen: both do nothing. Neither ever throws for an AI failure;
/// a failed scoring sets <c>scoring_failed</c> and keeps the last good ratings.
/// </summary>
public sealed class PriorityScorer(FeedbackDbContext db, IAiGateway ai, AiCallLog log, IOptions<PriorityCriteria> options)
{
    private PriorityCriteria Criteria => options.Value;

    /// <summary>Asks the AI to rate the Problem again, then recomputes Reach, score and level.</summary>
    public async Task RescoreAsync(long problemId, CancellationToken ct)
    {
        var problem = await OpenProblemAsync(problemId, ct);
        if (problem is null) return;

        var requests = await db.FeatureRequests.AsNoTracking().Where(r => r.ProblemId == problemId)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new ScoreRequest(r.Title, r.Description))
            .ToListAsync(ct);
        var c = Criteria;
        var input = new ScoreInput(
            new ScoreProblem(problem.Title, problem.Description, problem.Category), requests,
            c.Goals, c.CoreTasks, c.Levels.StrategicFit, c.Levels.Pain, c.Levels.RetentionRisk);

        var result = await log.RunLoggedAsync(AiSteps.Score, new AiCallFor(ProblemId: problemId),
            call => ai.ScoreAsync(input, call), ct, check: Check);

        if (result.Value is { } r)
        {
            var scoring = new PriorityScoring
            {
                ProblemId = problemId,
                StrategicFit = r.StrategicFit,
                StrategicFitReason = r.StrategicFitReason.Trim(),
                StrategicFitGoal = r.Goal,
                Pain = r.Pain,
                PainReason = r.PainReason.Trim(),
                RetentionRisk = r.RetentionRisk,
                RetentionRiskReason = r.RetentionRiskReason.Trim(),
                AiCallId = result.CallId,
            };
            db.PriorityScorings.Add(scoring);
            await db.SaveChangesAsync(ct);
            problem.CurrentScoring = scoring;
            problem.FirstScoringId ??= scoring.Id;
            problem.ScoringFailed = false;
        }
        else
        {
            problem.ScoringFailed = true;
        }
        // Counted after the AI call, so Upvotes that arrived meanwhile are not lost.
        await ApplyAsync(problem, ct);
    }

    /// <summary>Recomputes Reach (Feature Requests × 3 + Upvotes), and the score and level from the current ratings.</summary>
    public async Task RefreshReachAsync(long problemId, CancellationToken ct)
    {
        var problem = await OpenProblemAsync(problemId, ct);
        if (problem is null) return;
        await ApplyAsync(problem, ct);
    }

    private Task<Problem?> OpenProblemAsync(long problemId, CancellationToken ct) =>
        db.Problems.Include(p => p.CurrentScoring)
            .SingleOrDefaultAsync(p => p.Id == problemId
                && p.Status != ProblemStatuses.Shipped && p.Status != ProblemStatuses.WontDo, ct);

    private async Task ApplyAsync(Problem problem, CancellationToken ct)
    {
        var requests = await db.FeatureRequests.CountAsync(r => r.ProblemId == problem.Id, ct);
        var upvotes = await db.Upvotes.CountAsync(u => db.FeatureRequests
            .Any(r => r.ProblemId == problem.Id && r.Id == u.FeatureRequestId), ct);
        problem.ReachPoints = PriorityCriteria.ReachPoints(requests, upvotes);
        problem.ReachRating = Criteria.ReachRating(problem.ReachPoints.Value);
        if (problem.CurrentScoring is { } s)
        {
            problem.RecommendedScore = Criteria.Score(s.StrategicFit, problem.ReachRating.Value, s.Pain, s.RetentionRisk);
            problem.RecommendedLevel = Criteria.Level(problem.RecommendedScore.Value);
        }
        await db.SaveChangesAsync(ct);
    }

    // The strict form guarantees the shape and the 1–5 range; this checks what JSON schema can't.
    private string? Check(ScoreRatings r)
    {
        int[] ratings = [r.StrategicFit, r.Pain, r.RetentionRisk];
        if (ratings.Any(x => x is < 1 or > 5)) return "A rating is outside 1–5.";
        if (new[] { r.StrategicFitReason, r.PainReason, r.RetentionRiskReason }.Any(string.IsNullOrWhiteSpace))
            return "A reason is empty.";
        if (r.Goal is null) return r.StrategicFit == 1 ? null : "Strategic fit names no goal.";
        return Criteria.Goals.Contains(r.Goal) ? null : $"Unknown goal \"{r.Goal}\".";
    }
}
