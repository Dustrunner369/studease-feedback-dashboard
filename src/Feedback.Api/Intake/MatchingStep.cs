using Feedback.Api.Ai;
using Feedback.Api.Data;
using Feedback.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Feedback.Api.Intake;

/// <summary>
/// Background part of intake for one Feature Request: embed → shortlist → match.
/// Any failure is logged in ai_calls and leaves the request unmatched (needs review);
/// the Requester never sees it. Scoring hooks in after a successful link (later ticket).
/// </summary>
public sealed class MatchingStep(FeedbackDbContext db, IAiGateway ai, AiCallLog log)
{
    public const int ShortlistSize = 5;

    public async Task RunAsync(long featureRequestId, CancellationToken ct)
    {
        var request = await db.FeatureRequests.SingleOrDefaultAsync(r => r.Id == featureRequestId, ct);
        if (request is null || request.ProblemId is not null) return;
        var @for = new AiCallFor(FeatureRequestId: request.Id);

        if (request.Embedding is null)
        {
            var vector = await log.RunAsync(AiSteps.Embed, @for, c => ai.EmbedAsync(EmbeddingText(request), c), ct,
                check: v => v.Length == 1536 ? null : $"Expected 1536 dimensions, got {v.Length}.");
            if (vector is null) return;
            request.Embedding = new Vector(vector);
            await db.SaveChangesAsync(ct);
        }

        var shortlist = await ShortlistAsync(request, ct);
        var input = new MatchInput(new MatchRequest(request.Title, request.Description, request.Category), shortlist);
        var decision = await log.RunAsync(AiSteps.Match, @for, c => ai.MatchAsync(input, c), ct,
            check: d => Check(d, shortlist));
        if (decision is null) return;

        if (decision.ExistingProblemId is { } problemId)
        {
            request.ProblemId = problemId;
        }
        else
        {
            var draft = decision.NewProblem!;
            request.Problem = new Problem
            {
                Title = draft.Title.Trim(),
                Description = draft.Description.Trim(),
                Category = draft.Category,
                Status = ProblemStatuses.New,
            };
        }
        request.MatchReason = decision.Reason.Trim();
        request.IsBundled = decision.IsBundled;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The text embedded for a Feature Request (suggest-as-you-type embeds the typed text the same way).</summary>
    public static string EmbeddingText(FeatureRequest r) => EmbeddingText(r.Title, r.Description);

    public static string EmbeddingText(string title, string description) => $"{title}\n\n{description}";

    // The 5 closest Problems by their best-matching request. Shipped Problems are never candidates.
    private async Task<IReadOnlyList<MatchCandidate>> ShortlistAsync(FeatureRequest request, CancellationToken ct)
    {
        var rows = await ClosestProblems.FindAsync(db, request.Embedding!, ShortlistSize, ct, excludeRequestId: request.Id);
        return rows.Select(r => new MatchCandidate(
            r.ProblemId, r.ProblemTitle, r.ProblemDescription, r.ProblemCategory, r.Title, r.Description, r.Distance)).ToList();
    }

    // The strict form guarantees the shape; this checks what JSON schema can't.
    private static string? Check(MatchDecision d, IReadOnlyList<MatchCandidate> shortlist)
    {
        if (string.IsNullOrWhiteSpace(d.Reason)) return "The reason is empty.";
        return (d.ExistingProblemId, d.NewProblem) switch
        {
            (not null, not null) => "Gave both an existing Problem and a new one.",
            (null, null) => "Gave neither an existing Problem nor a new one.",
            ({ } id, null) when shortlist.All(c => c.ProblemId != id) => $"Problem {id} was not on the shortlist.",
            (null, { } p) when string.IsNullOrWhiteSpace(p.Title) || string.IsNullOrWhiteSpace(p.Description) =>
                "The new Problem has no title or description.",
            (null, { } p) when !Categories.IsValid(p.Category) => $"Unknown Category \"{p.Category}\".",
            _ => null,
        };
    }
}
