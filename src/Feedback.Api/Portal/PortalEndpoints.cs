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

        api.MapPost("/requests", async (SubmitRequestBody body, IntakePipeline intake, CancellationToken ct) =>
        {
            var (valid, errors) = SubmissionValidator.Validate(body.Title, body.Description, body.Category, body.Email);
            if (valid is null) return Results.ValidationProblem(errors);

            var saved = await intake.SubmitAsync(valid, ct);
            return Results.Created($"/api/requests/{saved.Id}", FeatureRequestView.From(saved));
        });

        api.MapGet("/requests", async (string? category, string? sort, FeedbackDbContext db, CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(category) && !Categories.IsValid(category))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["category"] = ["Unknown Category."] });
            // Only Newest exists so far; Most upvoted ("top") arrives with Upvotes.
            if (!string.IsNullOrEmpty(sort) && sort != "new")
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sort"] = ["Unknown sort."] });

            var rows = db.FeatureRequests.AsNoTracking();
            if (!string.IsNullOrEmpty(category))
                rows = rows.Where(r => (r.Problem != null ? r.Problem.Category : r.Category) == category);

            var board = await ToViews(rows.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)).ToListAsync(ct);
            return Results.Ok(board);
        });

        api.MapGet("/requests/{id:long}", async (long id, FeedbackDbContext db, CancellationToken ct) =>
        {
            var request = await ToViews(db.FeatureRequests.AsNoTracking().Where(r => r.Id == id)).SingleOrDefaultAsync(ct);
            return request is null ? Results.NotFound() : Results.Ok(request);
        });
    }

    // A matched request is listed under its Problem's Category; an unmatched one under the Requester's pick.
    // Keep this the last step of a query: EF can't filter or sort on the projected record.
    private static IQueryable<FeatureRequestView> ToViews(IQueryable<FeatureRequest> requests) =>
        requests.Select(r => new FeatureRequestView(
            r.Id, r.Title, r.Description, r.Problem != null ? r.Problem.Category : r.Category, r.CreatedAt));
}

public sealed record SubmitRequestBody(string? Title, string? Description, string? Category, string? Email);

/// <summary>A Feature Request as Requesters see it. Never includes the email.</summary>
public sealed record FeatureRequestView(long Id, string Title, string Description, string Category, DateTimeOffset CreatedAt)
{
    public static FeatureRequestView From(FeatureRequest r) => new(r.Id, r.Title, r.Description, r.Category, r.CreatedAt);
}
