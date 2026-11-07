using Feedback.Api.Briefs;
using Feedback.Api.Data;
using Feedback.Api.Solutions;
using Feedback.Api.Domain;
using Feedback.Api.Triage;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            var problems = db.Problems.AsNoTracking().Open();
            if (!string.IsNullOrEmpty(category)) problems = problems.Where(p => p.Category == category);

            var rows = await problems
                .Select(p => new
                {
                    p.Id, p.Title, p.Category, p.Status, p.CreatedAt, p.RecommendedScore, p.Priority,
                    FeatureRequests = p.FeatureRequests.Count,
                    Upvotes = db.Upvotes.Count(u => p.FeatureRequests.Select(r => r.Id).Contains(u.FeatureRequestId)),
                })
                .ToListAsync(ct);

            // Ordered by Recommended score (not shown in the table); not-yet-scored Problems last;
            // ties by Reach points (Feature Requests × 3 + Upvotes), then newest.
            var table = rows
                .OrderByDescending(p => p.RecommendedScore.HasValue).ThenByDescending(p => p.RecommendedScore)
                .ThenByDescending(p => p.FeatureRequests * 3 + p.Upvotes).ThenByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
                .Select(p => new ProblemRow(p.Id, p.Title, p.Category, p.Status, p.FeatureRequests, p.Upvotes, p.Priority))
                .ToList();
            var needsReview = await db.FeatureRequests.CountAsync(r => r.ProblemId == null, ct);
            return Results.Ok(new ProblemsPage(needsReview, table));
        });

        admin.MapGet("/problems/{id:long}", (long id, DrawerBuilder drawers, CancellationToken ct) => drawers.OkAsync(id, ct));

        // Merge two or more open Problems into one; returns the kept Problem's drawer.
        admin.MapPost("/problems/merge", async (
            MergeRequest body, Regrouping regrouping, DrawerBuilder drawers, CancellationToken ct) =>
        {
            var result = await regrouping.MergeAsync(body, ct);
            if (result.Errors is { } errors) return Results.ValidationProblem(errors);
            return await drawers.OkAsync(result.Value, ct);
        });

        // Rename, description, Category, status and Priority; returns the refreshed drawer.
        admin.MapPatch("/problems/{id:long}", async (
            long id, JsonObject? body, ProblemDecisions decisions, DrawerBuilder drawers, CancellationToken ct) =>
        {
            if (ProblemChanges.From(body) is not { } changes)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["Send a JSON object of strings."] });
            var result = await decisions.ApplyAsync(id, changes, ct);
            if (!result.Found) return Results.NotFound();
            if (result.Errors is { } errors) return Results.ValidationProblem(errors);
            return await drawers.OkAsync(id, ct);
        });
    }
}

/// <summary>✦ Recommended Priority: level, 0–100 score, and how it was worked out.</summary>
public sealed record RecommendedView(string Level, int Score, IReadOnlyList<CriterionScore> Breakdown);

/// <summary>One criterion's rating (1–5), its share of the score, and why. Goal is Strategic fit's named goal.</summary>
public sealed record CriterionScore(string Criterion, int Weight, int Rating, decimal Points, string Reason, string? Goal);

public sealed record ProblemsPage(int NeedsReview, IReadOnlyList<ProblemRow> Problems);

/// <summary>One row of the Problems table. Priority is the product team's; null = "Not set".</summary>
public sealed record ProblemRow(
    long Id, string Title, string Category, string Status, int FeatureRequests, int Upvotes, string? Priority);

/// <summary>
/// The Problem drawer. Admin-only, so Feature Requests include the Requester email.
/// Recommended is null until a scoring succeeds; ScoringFailed means the last scoring call failed (offer Retry).
/// Brief is the latest Decision Brief (null until Generate brief).
/// </summary>
public sealed record ProblemDrawer(
    long Id, string Title, string Description, string Category, string Status, string? Priority,
    string? PriorityOverrideReason, RecommendedView? Recommended, bool ScoringFailed,
    IReadOnlyList<DrawerRequest> FeatureRequests, IReadOnlyList<DecisionView> Decisions, DateTimeOffset? ClosedAt,
    GitHubIssueView? GitHubIssue, SolutionsView? Solutions, DecisionBriefView? Brief);

/// <summary>
/// The Problem's GitHub issue (null until Create GitHub issue). State is as of CheckedAt (the last refresh);
/// MarkShipped is "Issue closed. Mark Shipped?": the issue is closed but the Problem isn't.
/// </summary>
public sealed record GitHubIssueView(int Number, string Url, string State, DateTimeOffset? CheckedAt, bool MarkShipped)
{
    public static GitHubIssueView? Of(Problem p) => p is { GitHubIssueNumber: { } number, GitHubIssueUrl: { } url }
        ? new(number, url, p.GitHubIssueState ?? GitHub.IssueStates.Open, p.GitHubCheckedAt,
            p.GitHubIssueState == GitHub.IssueStates.Closed && !ProblemStatuses.IsClosed(p.Status))
        : null;
}

/// <summary>A decision_log row, newest first in the drawer. Details is the stored JSON object.</summary>
public sealed record DecisionView(string Kind, string? Reason, JsonElement Details, DateTimeOffset CreatedAt);

/// <summary>A Feature Request in the drawer. Category is the Requester's own pick; MatchReason is "✦ Why it's here".</summary>
public sealed record DrawerRequest(
    long Id, string Title, string Description, string Email, string Category, DateTimeOffset CreatedAt,
    int Upvotes, string? MatchReason, bool IsBundled);
