using Feedback.Api.Ai;
using Feedback.Api.Data;
using Feedback.Api.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Feedback.Api.Portal;

/// <summary>
/// Config section <c>Suggest</c>. Similarity is 1 − cosine distance of the embeddings.
/// The thresholds are placeholders carried over from the word-overlap prototype; tune them on real embeddings over the seed.
/// </summary>
public sealed class SuggestOptions
{
    public const string Section = "Suggest";
    public double ShowThreshold { get; set; } = 0.30;
    public double StrongThreshold { get; set; } = 0.60;
    public int MaxResults { get; set; } = 4;
    /// <summary>Title + description must have this many non-space characters before anything is embedded.</summary>
    public int MinChars { get; set; } = 4;
}

public static class SuggestEndpoints
{
    /// <summary>
    /// POST /api/suggest: similar existing Feature Requests for what the Requester is typing, one per Problem.
    /// Calls are not logged in ai_calls. Any embedding failure just means no suggestions.
    /// </summary>
    public static void MapSuggest(this RouteGroupBuilder api)
    {
        api.MapPost("/suggest", async (SuggestBody body, HttpRequest http, FeedbackDbContext db, IAiGateway ai,
            IOptions<SuggestOptions> options, ILogger<SuggestOptions> logger, CancellationToken ct) =>
        {
            var o = options.Value;
            var title = Clip(body.Title, SubmissionValidator.TitleMax);
            var description = Clip(body.Description, SubmissionValidator.DescriptionMax);
            if ((title + description).Count(c => !char.IsWhiteSpace(c)) < o.MinChars)
                return Results.Ok(Array.Empty<SuggestionView>());

            float[] embedding;
            try
            {
                embedding = (await ai.EmbedAsync(MatchingStep.EmbeddingText(title, description), ct)).Value;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Suggest embedding failed: {Message}", e.Message);
                return Results.Ok(Array.Empty<SuggestionView>());
            }
            if (embedding.Length != 1536) return Results.Ok(Array.Empty<SuggestionView>());

            var closest = (await ClosestProblems.FindAsync(db, new Vector(embedding), o.MaxResults, ct))
                .Where(r => 1 - r.Distance >= o.ShowThreshold)
                .ToList();
            var ids = closest.Select(r => r.FeatureRequestId).ToList();
            var views = await PortalEndpoints.ToViews(
                    db.FeatureRequests.AsNoTracking().Where(r => ids.Contains(r.Id)), db, BrowserToken.From(http))
                .ToDictionaryAsync(v => v.Id, ct);

            return Results.Ok(closest.Select((r, i) =>
            {
                var similarity = 1 - r.Distance;
                return SuggestionView.From(views[r.FeatureRequestId], similarity, strong: i == 0 && similarity >= o.StrongThreshold);
            }).ToList());
        });
    }

    private static string Clip(string? text, int max)
    {
        text = text?.Trim() ?? "";
        return text.Length > max ? text[..max] : text;
    }
}

public sealed record SuggestBody(string? Title, string? Description);

/// <summary>A suggested Feature Request, how similar it is (0–1), and whether it is the strong match. Never includes the email.</summary>
public sealed record SuggestionView(
    long Id, string Title, string Description, string Category, DateTimeOffset CreatedAt, int Upvotes, bool Upvoted,
    double Similarity, bool Strong)
{
    public static SuggestionView From(FeatureRequestView r, double similarity, bool strong) =>
        new(r.Id, r.Title, r.Description, r.Category, r.CreatedAt, r.Upvotes, r.Upvoted, Math.Round(similarity, 4), strong);
}
