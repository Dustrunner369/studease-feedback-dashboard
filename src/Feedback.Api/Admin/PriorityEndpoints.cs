using Feedback.Api.Data;
using Feedback.Api.Domain;
using Feedback.Api.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Feedback.Api.Admin;

/// <summary>Settings → Priority criteria (read-only) and Retry for a failed scoring.</summary>
public static class PriorityEndpoints
{
    public static void MapPriority(this RouteGroupBuilder admin)
    {
        admin.MapGet("/criteria", (IOptions<PriorityCriteria> options) => Results.Ok(CriteriaView.From(options.Value)));

        // Retry for a failed scoring: runs the scoring call again now (the drawer waits for it) and returns the
        // refreshed drawer. Only when the last scoring failed (there is no manual re-score); closed Problems are
        // frozen. Otherwise 409.
        admin.MapPost("/problems/{id:long}/retry-scoring", async (
            long id, FeedbackDbContext db, PriorityScorer scorer, DrawerBuilder drawers, CancellationToken ct) =>
        {
            var problem = await db.Problems.Where(p => p.Id == id)
                .Select(p => new { p.Status, p.ScoringFailed }).SingleOrDefaultAsync(ct);
            if (problem is null) return Results.NotFound();
            if (ProblemStatuses.IsClosed(problem.Status))
                return Results.Problem("A closed Problem's Recommended Priority is frozen.", statusCode: StatusCodes.Status409Conflict);
            if (!problem.ScoringFailed)
                return Results.Problem("Retry is only for a failed scoring.", statusCode: StatusCodes.Status409Conflict);

            await scorer.RescoreAsync(id, ct);
            return await drawers.OkAsync(id, ct);
        });
    }
}

public sealed record CriteriaView(
    IReadOnlyList<string> Goals, string CoreTasks, IReadOnlyList<CriterionView> Criteria,
    IReadOnlyList<ReachBandView> ReachBands, CutOffsView CutOffs)
{
    public static CriteriaView From(PriorityCriteria c) => new(
        c.Goals,
        c.CoreTasks,
        [
            new(Criterion.StrategicFit, c.Weights.StrategicFit, "Scored 1-5 by AI using the table below", c.Levels.StrategicFit),
            new(Criterion.Reach, c.Weights.Reach, "Determined by frequency of requests and number of upvotes", null),
            new(Criterion.Pain, c.Weights.Pain, "Scored 1-5 by AI using the table below", c.Levels.Pain),
            new(Criterion.RetentionRisk, c.Weights.RetentionRisk, "Scored 1-5 by AI using the table below", c.Levels.RetentionRisk),
        ],
        c.ReachBands.OrderBy(b => b.MinPoints).Select(b => new ReachBandView(b.MinPoints, b.Rating)).ToList(),
        new CutOffsView(c.CutOffs.High, c.CutOffs.Medium));
}

/// <summary>Levels: what each rating 1–5 means (index 0 = 1); null for Reach, which code works out.</summary>
public sealed record CriterionView(string Name, int Weight, string RatedBy, IReadOnlyList<string>? Levels);

public sealed record ReachBandView(int MinPoints, int Rating);

public sealed record CutOffsView(int High, int Medium);
