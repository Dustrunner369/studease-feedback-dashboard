namespace Feedback.Api.Ai;

/// <summary>
/// Every AI call the app makes, one operation per AI step. The real adapter is the OpenAI one;
/// tests swap in an in-memory fake.
/// <para>
/// Interface contract: each operation either returns an <see cref="AiReply{T}"/>
/// (the parsed strict-form answer plus the model the API reported and token usage)
/// or throws <see cref="AiCallException"/> with a <see cref="AiFailureKind"/>.
/// Temporary errors are already retried (the OpenAI package's 2 retries) before it throws.
/// Model and reasoning effort come from config (Ai:Steps), never from callers.
/// Requester text goes in as data, never as instructions.
/// </para>
/// Callers wrap each call in AiCallLog.RunAsync, which logs the ai_calls row.
/// Later tickets add: score a Problem, write a brief, pick files, write solutions, draft an update.
/// </summary>
public interface IAiGateway
{
    /// <summary>Embeds text with the Embed step's model (1536 dims).</summary>
    Task<AiReply<float[]>> EmbedAsync(string text, CancellationToken ct);

    /// <summary>Decides whether a Feature Request belongs to one of the shortlisted Problems or needs a new one.</summary>
    Task<AiReply<MatchDecision>> MatchAsync(MatchInput input, CancellationToken ct);
}

/// <summary>A successful AI answer and what it cost.</summary>
public sealed record AiReply<T>(T Value, string Model, int InputTokens, int OutputTokens);

/// <summary>The fixed failure kinds logged in ai_calls.failure_kind.</summary>
public static class AiFailureKind
{
    public const string Timeout = "Timeout";
    public const string ApiError = "API error";
    public const string InvalidOutput = "Invalid output";

    public static readonly IReadOnlyList<string> All = [Timeout, ApiError, InvalidOutput];
}

/// <summary>An AI call that failed after the package's retries, or whose output broke the strict form.</summary>
public sealed class AiCallException(string kind, string message, string? model = null) : Exception(message)
{
    public string Kind { get; } = kind;
    /// <summary>The model the API reported, when a response came back at all.</summary>
    public string? Model { get; } = model;
}

/// <summary>The Feature Request to match, and the up to 5 closest Problems (closest first).</summary>
public sealed record MatchInput(MatchRequest Request, IReadOnlyList<MatchCandidate> Candidates);

public sealed record MatchRequest(string Title, string Description, string Category);

/// <summary>A shortlisted Problem with its best-matching Feature Request and that request's cosine distance.</summary>
public sealed record MatchCandidate(
    long ProblemId, string Title, string Description, string Category,
    string ClosestRequestTitle, string ClosestRequestDescription, double Distance);

/// <summary>
/// The strict matching form: exactly one of <see cref="ExistingProblemId"/> (from the shortlist)
/// or <see cref="NewProblem"/>; <see cref="Reason"/> is always required.
/// </summary>
public sealed record MatchDecision(long? ExistingProblemId, NewProblemDraft? NewProblem, bool IsBundled, string Reason);

public sealed record NewProblemDraft(string Title, string Description, string Category);
