using Feedback.Api.Data;
using Feedback.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Admin;

/// <summary>Settings → Archive: Shipped and Won't do Problems, with the Recommended Priority they had when closed.</summary>
public static class ArchiveEndpoints
{
    public static void MapArchive(this RouteGroupBuilder admin)
    {
        admin.MapGet("/archive", async (FeedbackDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Problems.AsNoTracking()
                .Where(p => p.Status == ProblemStatuses.Shipped || p.Status == ProblemStatuses.WontDo)
                .OrderByDescending(p => p.ClosedAt).ThenByDescending(p => p.Id)
                .Select(p => new
                {
                    p.Id, p.Title, p.Category, p.Status, p.Priority, p.RecommendedLevel, p.RecommendedScore, p.ClosedAt,
                    FeatureRequests = p.FeatureRequests.Count,
                    Upvotes = db.Upvotes.Count(u => p.FeatureRequests.Select(r => r.Id).Contains(u.FeatureRequestId)),
                    // The reason given with the latest status change (Won't do always has one).
                    Reason = db.DecisionLog.Where(d => d.ProblemId == p.Id && d.Kind == DecisionKinds.Status)
                        .OrderByDescending(d => d.Id).Select(d => d.Reason).FirstOrDefault(),
                })
                .ToListAsync(ct);
            return Results.Ok(rows.Select(p => new ArchiveRow(
                p.Id, p.Title, p.Category, p.Status, p.Priority,
                p is { RecommendedLevel: { } level, RecommendedScore: { } score } ? new FrozenRecommended(level, score) : null,
                p.FeatureRequests, p.Upvotes, p.Reason, p.ClosedAt ?? DateTimeOffset.MinValue)));
        });
    }
}

/// <summary>One archived Problem. Recommended is frozen at closing (null if it was never scored).</summary>
public sealed record ArchiveRow(
    long Id, string Title, string Category, string Status, string? Priority, FrozenRecommended? Recommended,
    int FeatureRequests, int Upvotes, string? Reason, DateTimeOffset ClosedAt);

public sealed record FrozenRecommended(string Level, int Score);
