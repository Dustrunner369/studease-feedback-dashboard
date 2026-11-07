using Feedback.Api.Data;
using Feedback.Api.Domain;
using Feedback.Api.Intake;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Portal;

/// <summary>The public, no-sign-in portal API under /api.</summary>
public static class PortalEndpoints
{
    public static void MapPortalApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireRateLimiting(PortalRateLimit.Policy);
        api.MapUpvotes();

        api.MapPost("/requests", async (SubmitRequestBody body, IntakePipeline intake, CancellationToken ct) =>
        {
            var (valid, errors) = SubmissionValidator.Validate(body.Title, body.Description, body.Category, body.Email);
            if (valid is null) return Results.ValidationProblem(errors);

            var saved = await intake.SubmitAsync(valid, ct);
            return Results.Created($"/api/requests/{saved.Id}", FeatureRequestView.From(saved));
        });

        api.MapGet("/requests", async (string? category, string? sort, HttpRequest http, FeedbackDbContext db, CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(category) && !Categories.IsValid(category))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["category"] = ["Unknown Category."] });
            if (!string.IsNullOrEmpty(sort) && sort is not ("new" or "top"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sort"] = ["Unknown sort."] });

            var rows = db.FeatureRequests.AsNoTracking();
            if (!string.IsNullOrEmpty(category))
                rows = rows.Where(r => (r.Problem != null ? r.Problem.Category : r.Category) == category);

            // Most upvoted is the default; ties (and Newest) go latest first.
            var ordered = sort == "new"
                ? rows.OrderByDescending(r => r.CreatedAt)
                : rows.OrderByDescending(r => db.Upvotes.Count(u => u.FeatureRequestId == r.Id)).ThenByDescending(r => r.CreatedAt);
            var board = await ToViews(ordered.ThenByDescending(r => r.Id), db, BrowserToken.From(http)).ToListAsync(ct);
            return Results.Ok(board);
        });

        api.MapGet("/requests/{id:long}", async (long id, HttpRequest http, FeedbackDbContext db, CancellationToken ct) =>
        {
            var request = await ToViews(db.FeatureRequests.AsNoTracking().Where(r => r.Id == id), db, BrowserToken.From(http))
                .SingleOrDefaultAsync(ct);
            return request is null ? Results.NotFound() : Results.Ok(request);
        });
    }

    // A matched request is listed under its Problem's Category; an unmatched one under the Requester's pick.
    // Keep this the last step of a query: EF can't filter or sort on the projected record.
    // upvoted is true only for the asking browser's own token (false with no token).
    private static IQueryable<FeatureRequestView> ToViews(IQueryable<FeatureRequest> requests, FeedbackDbContext db, string? token) =>
        requests.Select(r => new FeatureRequestView(
            r.Id, r.Title, r.Description, r.Problem != null ? r.Problem.Category : r.Category, r.CreatedAt,
            db.Upvotes.Count(u => u.FeatureRequestId == r.Id),
            token != null && db.Upvotes.Any(u => u.FeatureRequestId == r.Id && u.BrowserToken == token)));
}

public sealed record SubmitRequestBody(string? Title, string? Description, string? Category, string? Email);

/// <summary>A Feature Request as Requesters see it. Never includes the email.</summary>
public sealed record FeatureRequestView(
    long Id, string Title, string Description, string Category, DateTimeOffset CreatedAt, int Upvotes, bool Upvoted)
{
    /// <summary>A just-submitted request: no Upvotes yet.</summary>
    public static FeatureRequestView From(FeatureRequest r) => new(r.Id, r.Title, r.Description, r.Category, r.CreatedAt, 0, false);
}
