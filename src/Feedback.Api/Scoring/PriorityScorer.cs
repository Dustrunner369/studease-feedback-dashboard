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

        var result = await AskAsync(problem, ct);

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

    /// <summary>
    /// The consistency check's re-score: the same logged Score call and arithmetic as <see cref="RescoreAsync"/>,
    /// but nothing about the Problem is saved (not its ratings, its Recommended Priority or <c>scoring_failed</c>).
    /// Null for a failed call or a closed/missing Problem.
    /// </summary>
    public async Task<ScoreTrial?> TryScoreAsync(long problemId, string note, CancellationToken ct)
    {
        var problem = await db.Problems.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == problemId
                && p.Status != ProblemStatuses.Shipped && p.Status != ProblemStatuses.WontDo, ct);
        if (problem is null) return null;
        if ((await AskAsync(problem, ct, note)).Value is not { } r) return null;
        var (requests, upvotes) = await CountsAsync(problemId, ct);
        var score = Criteria.Score(r.StrategicFit, Criteria.ReachRating(PriorityCriteria.ReachPoints(requests, upvotes)), r.Pain, r.RetentionRisk);
        return new ScoreTrial(score, Criteria.Level(score));
    }

    private async Task<AiCallResult<ScoreRatings>> AskAsync(Problem problem, CancellationToken ct, string? note = null)
    {
        var requests = await db.FeatureRequests.AsNoTracking().Where(r => r.ProblemId == problem.Id)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new ScoreRequest(r.Title, r.Description))
            .ToListAsync(ct);
        var c = Criteria;
        var input = new ScoreInput(
            new ScoreProblem(problem.Title, problem.Description, problem.Category), requests,
            c.Goals, c.CoreTasks, c.Levels.StrategicFit, c.Levels.Pain, c.Levels.RetentionRisk);
        return await log.RunLoggedAsync(AiSteps.Score, new AiCallFor(ProblemId: problem.Id),
            call => ai.ScoreAsync(input, call), ct, check: Check, note: note);
    }

    private async Task<(int Requests, int Upvotes)> CountsAsync(long problemId, CancellationToken ct) => (
        await db.FeatureRequests.CountAsync(r => r.ProblemId == problemId, ct),
        await db.Upvotes.CountAsync(u => db.FeatureRequests
            .Any(r => r.ProblemId == problemId && r.Id == u.FeatureRequestId), ct));

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
        var (requests, upvotes) = await CountsAsync(problem.Id, ct);
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

/// <summary>One re-score's 0–100 score and level, not saved.</summary>
public sealed record ScoreTrial(int Score, string Level);
