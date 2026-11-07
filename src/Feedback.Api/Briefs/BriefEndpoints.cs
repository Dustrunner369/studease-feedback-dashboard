using Feedback.Api.Admin;
using Feedback.Api.Data;
using Feedback.Api.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Feedback.Api.Briefs;

/// <summary>Generate brief / Regenerate / Try again on a Problem.</summary>
public static class BriefEndpoints
{
    public static void MapBriefs(this RouteGroupBuilder admin)
    {
        // Writes the brief now (the drawer waits for it) and returns the refreshed drawer.
        // A failed call keeps any previous brief and answers 502 so the drawer offers Try again.
        admin.MapPost("/problems/{id:long}/brief", async (
            long id, DecisionBriefs briefs, FeedbackDbContext db, IOptions<PriorityCriteria> criteria, CancellationToken ct) =>
        {
            if (!await db.Problems.AnyAsync(p => p.Id == id, ct)) return Results.NotFound();
            if (!await briefs.GenerateAsync(id, ct))
                return Results.Problem("The brief couldn't be written. Try again.", statusCode: StatusCodes.Status502BadGateway);
            return Results.Ok(await ProblemEndpoints.DrawerAsync(id, db, criteria.Value, ct));
        });
    }
}
