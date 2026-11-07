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

        // Runs the scoring call again now (the drawer waits for it) and returns the refreshed drawer.
        // Closed Problems are frozen: 409.
        admin.MapPost("/problems/{id:long}/retry-scoring", async (
            long id, FeedbackDbContext db, PriorityScorer scorer, IOptions<PriorityCriteria> options, CancellationToken ct) =>
        {
            var status = await db.Problems.Where(p => p.Id == id).Select(p => p.Status).SingleOrDefaultAsync(ct);
            if (status is null) return Results.NotFound();
            if (status is ProblemStatuses.Shipped or ProblemStatuses.WontDo)
                return Results.Problem("A closed Problem's Recommended Priority is frozen.", statusCode: StatusCodes.Status409Conflict);

            await scorer.RescoreAsync(id, ct);
            return Results.Ok(await ProblemEndpoints.DrawerAsync(id, db, options.Value, ct));
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
            new(Criterion.StrategicFit, c.Weights.StrategicFit, "AI, 1–5 + reason, naming the goal it serves best", c.Levels.StrategicFit),
            new(Criterion.Reach, c.Weights.Reach, "Code: Feature Requests × 3 + Upvotes, in fixed bands", null),
            new(Criterion.Pain, c.Weights.Pain, "AI, 1–5 + reason", c.Levels.Pain),
            new(Criterion.RetentionRisk, c.Weights.RetentionRisk, "AI, 1–5 + reason", c.Levels.RetentionRisk),
        ],
        c.ReachBands.OrderBy(b => b.MinPoints).Select(b => new ReachBandView(b.MinPoints, b.Rating)).ToList(),
        new CutOffsView(c.CutOffs.High, c.CutOffs.Medium));
}

/// <summary>Levels: what each rating 1–5 means (index 0 = 1); null for Reach, which code works out.</summary>
public sealed record CriterionView(string Name, int Weight, string RatedBy, IReadOnlyList<string>? Levels);

public sealed record ReachBandView(int MinPoints, int Rating);

public sealed record CutOffsView(int High, int Medium);
