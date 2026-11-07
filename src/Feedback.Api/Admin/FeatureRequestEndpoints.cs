using Feedback.Api.Data;
using Feedback.Api.Intake;
using Feedback.Api.Triage;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Admin;

/// <summary>Moving Feature Requests between Problems, and the "N Feature Requests need review" list.</summary>
public static class FeatureRequestEndpoints
{
    public static void MapFeatureRequests(this RouteGroupBuilder admin)
    {
        // Move ticked requests to an existing or a new Problem (also Match by hand for a request that needs review).
        admin.MapPost("/feature-requests/move", async (MoveRequest body, Regrouping regrouping, CancellationToken ct) =>
        {
            var result = await regrouping.MoveAsync(body, ct);
            return result.Errors is { } errors ? Results.ValidationProblem(errors) : Results.Ok(result.Value);
        });

        // Requests with no Problem (matching or embedding failed, or still queued), newest first,
        // each with its latest failed AI call.
        admin.MapGet("/feature-requests/needs-review", async (FeedbackDbContext db, CancellationToken ct) =>
            Results.Ok(await db.FeatureRequests.AsNoTracking().Where(r => r.ProblemId == null)
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                .Select(r => new NeedsReviewItem(
                    r.Id, r.Title, r.Description, r.Email, r.Category, r.CreatedAt,
                    db.Upvotes.Count(u => u.FeatureRequestId == r.Id),
                    db.AiCalls.Where(c => c.FeatureRequestId == r.Id && c.Status == AiCallStatus.Failed)
                        .OrderByDescending(c => c.Id)
                        .Select(c => new NeedsReviewFailure(c.Step, c.FailureKind, c.ErrorMessage, c.CreatedAt))
                        .FirstOrDefault()))
                .ToListAsync(ct)));

        // Retry AI: runs embed → shortlist → match (→ score) now, in this request. ProblemId is null if it failed again.
        admin.MapPost("/feature-requests/{id:long}/retry-match", async (long id, FeedbackDbContext db, MatchingStep matching, CancellationToken ct) =>
        {
            var problemId = await db.FeatureRequests.Where(r => r.Id == id).Select(r => new { r.ProblemId }).SingleOrDefaultAsync(ct);
            if (problemId is null) return Results.NotFound();
            if (problemId.ProblemId is not null) return Results.Conflict();
            await matching.RunAsync(id, ct);
            var matched = await db.FeatureRequests.AsNoTracking().Where(r => r.Id == id).Select(r => r.ProblemId).SingleAsync(ct);
            return Results.Ok(new RetryMatchResult(id, matched));
        });
    }
}

/// <summary>A Feature Request waiting for a Problem. Failure is its latest failed AI call (null if none yet, e.g. still queued).</summary>
public sealed record NeedsReviewItem(
    long Id, string Title, string Description, string Email, string Category, DateTimeOffset CreatedAt, int Upvotes,
    NeedsReviewFailure? Failure);

public sealed record NeedsReviewFailure(string Step, string? FailureKind, string? ErrorMessage, DateTimeOffset CreatedAt);

public sealed record RetryMatchResult(long FeatureRequestId, long? ProblemId);
