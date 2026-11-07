using Feedback.Api.Data;
using Feedback.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Feedback.Api.Intake;

/// <summary>A Problem's best-matching Feature Request for an embedding, and that request's cosine distance.</summary>
public sealed record ClosestRequest(
    long ProblemId, string ProblemTitle, string ProblemDescription, string ProblemCategory,
    long FeatureRequestId, string Title, string Description, double Distance);

/// <summary>
/// The nearest-Problems search shared by the matching shortlist and suggest-as-you-type:
/// the best-matching Feature Request per Problem, closest first. Only matched requests count
/// (Problems have no embedding), and Shipped Problems are never candidates; Won't do ones are.
/// </summary>
public static class ClosestProblems
{
    public static Task<List<ClosestRequest>> FindAsync(
        FeedbackDbContext db, Vector embedding, int limit, CancellationToken ct, long? excludeRequestId = null)
    {
        var exclude = excludeRequestId ?? 0; // identity ids start at 1
        return db.Database.SqlQuery<ClosestRequest>($"""
            SELECT best.problem_id, p.title AS problem_title, p.description AS problem_description,
                   p.category AS problem_category,
                   best.id AS feature_request_id, best.title, best.description, best.distance
            FROM (
                SELECT DISTINCT ON (fr.problem_id) fr.problem_id, fr.id, fr.title, fr.description,
                       fr.embedding <=> {embedding} AS distance
                FROM feature_requests fr
                WHERE fr.problem_id IS NOT NULL AND fr.embedding IS NOT NULL AND fr.id <> {exclude}
                ORDER BY fr.problem_id, distance, fr.id
            ) best
            JOIN problems p ON p.id = best.problem_id
            WHERE p.status <> {ProblemStatuses.Shipped}
            ORDER BY best.distance, best.problem_id
            LIMIT {limit}
            """).ToListAsync(ct);
    }
}
