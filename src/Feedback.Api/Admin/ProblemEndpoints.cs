using Feedback.Api.Data;
using Feedback.Api.Domain;
using Microsoft.EntityFrameworkCore;

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
                    p.Id, p.Title, p.Category, p.Status, p.CreatedAt,
                    FeatureRequests = p.FeatureRequests.Count,
                    Upvotes = db.Upvotes.Count(u => p.FeatureRequests.Select(r => r.Id).Contains(u.FeatureRequestId)),
                })
                .ToListAsync(ct);

            // The table is ordered by Recommended score; until scoring lands, by Reach points (Feature Requests × 3 + Upvotes).
            var table = rows
                .OrderByDescending(p => p.FeatureRequests * 3 + p.Upvotes).ThenByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
                .Select(p => new ProblemRow(p.Id, p.Title, p.Category, p.Status, p.FeatureRequests, p.Upvotes, null))
                .ToList();
            var needsReview = await db.FeatureRequests.CountAsync(r => r.ProblemId == null, ct);
            return Results.Ok(new ProblemsPage(needsReview, table));
        });

        admin.MapGet("/problems/{id:long}", async (long id, FeedbackDbContext db, CancellationToken ct) =>
        {
            var problem = await db.Problems.AsNoTracking().Where(p => p.Id == id)
                .Select(p => new { p.Id, p.Title, p.Description, p.Category, p.Status })
                .SingleOrDefaultAsync(ct);
            if (problem is null) return Results.NotFound();

            var requests = await db.FeatureRequests.AsNoTracking().Where(r => r.ProblemId == id)
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                .Select(r => new DrawerRequest(
                    r.Id, r.Title, r.Description, r.Email, r.Category, r.CreatedAt,
                    db.Upvotes.Count(u => u.FeatureRequestId == r.Id), r.MatchReason, r.IsBundled))
                .ToListAsync(ct);
            return Results.Ok(new ProblemDrawer(
                problem.Id, problem.Title, problem.Description, problem.Category, problem.Status, null, requests));
        });
    }
}

public sealed record ProblemsPage(int NeedsReview, IReadOnlyList<ProblemRow> Problems);

/// <summary>One row of the Problems table. Priority is the product team's; null = "Not set".</summary>
public sealed record ProblemRow(
    long Id, string Title, string Category, string Status, int FeatureRequests, int Upvotes, string? Priority);

/// <summary>The Problem drawer. Admin-only, so Feature Requests include the Requester email.</summary>
public sealed record ProblemDrawer(
    long Id, string Title, string Description, string Category, string Status, string? Priority,
    IReadOnlyList<DrawerRequest> FeatureRequests);

/// <summary>A Feature Request in the drawer. Category is the Requester's own pick; MatchReason is "✦ Why it's here".</summary>
public sealed record DrawerRequest(
    long Id, string Title, string Description, string Email, string Category, DateTimeOffset CreatedAt,
    int Upvotes, string? MatchReason, bool IsBundled);
