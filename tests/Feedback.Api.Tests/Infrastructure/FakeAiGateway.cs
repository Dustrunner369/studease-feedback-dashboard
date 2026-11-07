using System.Collections.Concurrent;
using Feedback.Api.Ai;

namespace Feedback.Api.Tests.Infrastructure;

/// <summary>
/// In-memory stand-in for the OpenAI gateway. By default it embeds text as a bag of words
/// (so similar wording is close) and matches every request to a new Problem titled after it,
/// in the Requester's Category, and scores every Problem with the same ratings.
/// Tests replace <see cref="Embed"/> / <see cref="Match"/> / <see cref="Score"/> to script
/// answers; throw <see cref="AiCallException"/> from them to simulate failures.
/// </summary>
public sealed class FakeAiGateway : IAiGateway
{
    public const string ChatModel = "gpt-6-luna-fake";
    public const string EmbeddingModel = "text-embedding-3-small-fake";

    public Func<string, float[]> Embed { get; set; } = BagOfWords;
    public Func<MatchInput, MatchDecision> Match { get; set; } = NewProblemFromRequest;

    /// <summary>Every MatchAsync input, in call order.</summary>
    public ConcurrentQueue<MatchInput> MatchCalls { get; } = new();

    public Task<AiReply<float[]>> EmbedAsync(string text, CancellationToken ct) =>
        Task.FromResult(new AiReply<float[]>(Embed(text), EmbeddingModel, 12, 0));

    public Task<AiReply<MatchDecision>> MatchAsync(MatchInput input, CancellationToken ct)
    {
        MatchCalls.Enqueue(input);
        return Task.FromResult(new AiReply<MatchDecision>(Match(input), ChatModel, 900, 80));
    }

    /// <summary>Default: Strategic fit 3 (first goal), Pain 3, Retention risk 2.</summary>
    public Func<ScoreInput, ScoreRatings> Score { get; set; } = _ => new(
        3, "Supports finding spots in a small way.", "Help students find new study spots",
        3, "Noticeable friction.", 2, "Mild frustration.");

    /// <summary>Every ScoreAsync input, in call order.</summary>
    public ConcurrentQueue<ScoreInput> ScoreCalls { get; } = new();

    public Task<AiReply<ScoreRatings>> ScoreAsync(ScoreInput input, CancellationToken ct)
    {
        ScoreCalls.Enqueue(input);
        return Task.FromResult(new AiReply<ScoreRatings>(Score(input), ChatModel, 1200, 150));
    }

    /// <summary>Default: subject "It's here: &lt;Problem title&gt;", body listing the request titles.</summary>
    public Func<DraftUpdateInput, UpdateDraft> DraftUpdate { get; set; } = DefaultDraft;

    /// <summary>Every DraftUpdateAsync input, in call order.</summary>
    public ConcurrentQueue<DraftUpdateInput> DraftUpdateCalls { get; } = new();

    public Task<AiReply<UpdateDraft>> DraftUpdateAsync(DraftUpdateInput input, CancellationToken ct)
    {
        DraftUpdateCalls.Enqueue(input);
        return Task.FromResult(new AiReply<UpdateDraft>(DraftUpdate(input), ChatModel, 400, 120));
    }

    public static UpdateDraft DefaultDraft(DraftUpdateInput input) => new(
        $"It's here: {input.Problem.Title}",
        $"Thanks for asking for {string.Join(" and ", input.FeatureRequests.Select(r => r.Title))}. It's in Studease now.\n\nThe Studease team");

    public static MatchDecision NewProblemFromRequest(MatchInput input) => new(
        null,
        new NewProblemDraft($"Need: {input.Request.Title}", input.Request.Description, input.Request.Category),
        false,
        "Nothing on the shortlist is the same need.");

    public static MatchDecision Existing(long problemId) => new(problemId, null, false, "Same need as the shortlisted Problem.");

    /// <summary>A unit vector along one axis, mixed with a little of another: <c>Axis(0)</c> and <c>Axis(0, 1, 0.5f)</c> are close.</summary>
    public static float[] Axis(int main, int other = 1, float otherWeight = 0f)
    {
        var v = new float[1536];
        v[main] = 1f;
        if (otherWeight != 0f) v[other] += otherWeight;
        return Normalise(v);
    }

    private static float[] BagOfWords(string text)
    {
        var v = new float[1536];
        foreach (var word in text.ToLowerInvariant().Split((char[])[' ', '\n', '\t', '.', ',', '!', '?'], StringSplitOptions.RemoveEmptyEntries))
            v[(int)((uint)StringComparer.Ordinal.GetHashCode(word) % 1536)] += 1f;
        v[1535] += 0.01f; // never the zero vector
        return Normalise(v);
    }

    private static float[] Normalise(float[] v)
    {
        var length = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / length).ToArray();
    }
}
