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
/// </summary>
public interface IAiGateway
{
    /// <summary>Embeds text with the Embed step's model (1536 dims).</summary>
    Task<AiReply<float[]>> EmbedAsync(string text, CancellationToken ct);

    /// <summary>Decides whether a Feature Request belongs to one of the shortlisted Problems or needs a new one.</summary>
    Task<AiReply<MatchDecision>> MatchAsync(MatchInput input, CancellationToken ct);

    /// <summary>Rates a Problem's Strategic fit, Pain and Retention risk (1–5, with reasons) against the Priority Criteria.</summary>
    Task<AiReply<ScoreRatings>> ScoreAsync(ScoreInput input, CancellationToken ct);

    /// <summary>Drafts the update telling one Requester that the Problem behind their Feature Requests has shipped.</summary>
    Task<AiReply<UpdateDraft>> DraftUpdateAsync(DraftUpdateInput input, CancellationToken ct);

    /// <summary>Writes a Problem's Decision Brief as cited claims (code checks the citations and fills Who's asking).</summary>
    Task<AiReply<BriefForm>> WriteBriefAsync(BriefInput input, CancellationToken ct);

    /// <summary>Suggest Solutions, call 1: picks the repo files (about <see cref="PickFilesInput.MaxFiles"/>) most relevant to a Problem.</summary>
    Task<AiReply<FilePicks>> PickFilesAsync(PickFilesInput input, CancellationToken ct);

    /// <summary>Suggest Solutions, call 2: three short Solution Ideas for a Problem, grounded in the chosen files and open issues/PRs. Strict form.</summary>
    Task<AiReply<SolutionIdeas>> WriteSolutionsAsync(WriteSolutionsInput input, CancellationToken ct);
}

/// <summary>What the picker chooses from: the Problem (never Requester emails) and the repo's filtered file paths.</summary>
public sealed record PickFilesInput(ScoreProblem Problem, IReadOnlyList<string> Paths, int MaxFiles);

/// <summary>The strict picker form: the chosen paths (from <see cref="PickFilesInput.Paths"/>) and why.</summary>
public sealed record FilePicks(IReadOnlyList<string> Paths, string Reason);

/// <summary>
/// What solutions are written from: the Problem, the filtered path list, the chosen files' text (capped; Truncated
/// when cut) and every open issue and PR. Never Requester emails.
/// </summary>
public sealed record WriteSolutionsInput(
    ScoreProblem Problem, IReadOnlyList<string> Paths, IReadOnlyList<SourceFile> Files, IReadOnlyList<RepoItem> OpenItems);

public sealed record SourceFile(string Path, string Text, bool Truncated);

/// <summary>The strict Suggest Solutions form: three Solution Ideas, and no recommendation of one.</summary>
public sealed record SolutionIdeas(IReadOnlyList<SolutionIdea> Ideas);

/// <summary>
/// One short way the product team could address a Problem: a title (~8 words), one sentence in product terms,
/// effort (Small / Medium / Large), up to 3 repo paths it would touch, and related open issue/PR numbers.
/// </summary>
public sealed record SolutionIdea(string Title, string Description, string Effort, IReadOnlyList<string> Files, IReadOnlyList<int> Issues);

/// <summary>An open issue or pull request in the connected repo.</summary>
public sealed record RepoItem(int Number, string Title, string Body, bool IsPullRequest);

/// <summary>
/// What a Decision Brief is written from: the Problem, every linked Feature Request and the product goals.
/// Never emails, the Recommended Priority or the repo.
/// </summary>
public sealed record BriefInput(ScoreProblem Problem, IReadOnlyList<BriefRequest> FeatureRequests, IReadOnlyList<string> Goals);

/// <summary>A linked Feature Request as the brief sees it. Category is the Requester's own pick.</summary>
public sealed record BriefRequest(long Id, string Title, string Description, string Category, DateTimeOffset CreatedAt, int Upvotes);

/// <summary>The strict brief form: the four AI-written sections, each a list of claims. Who's asking is code's.</summary>
public sealed record BriefForm(
    IReadOnlyList<BriefClaim> Need, IReadOnlyList<BriefClaim> AskingFor,
    IReadOnlyList<BriefClaim> PainAndImpact, IReadOnlyList<BriefClaim> OpenQuestions);

/// <summary>One claim: its text, the Feature Requests it cites (at least one) and an optional verbatim quote from one of them.</summary>
public sealed record BriefClaim(string Text, IReadOnlyList<long> FeatureRequestIds, string? Quote);

/// <summary>
/// What a Requester update is drafted from: the shipped Problem and this one Requester's
/// Feature Requests (their own words). Never the email address.
/// </summary>
public sealed record DraftUpdateInput(ScoreProblem Problem, IReadOnlyList<ScoreRequest> FeatureRequests);

/// <summary>The strict draft form: a subject line and a plain-text body.</summary>
public sealed record UpdateDraft(string Subject, string Body);

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

/// <summary>
/// What the scoring step rates: the Problem, its Feature Requests' text (never emails), and the
/// criteria it rates against. Reach is not rated by the AI; code works it out.
/// </summary>
public sealed record ScoreInput(
    ScoreProblem Problem, IReadOnlyList<ScoreRequest> FeatureRequests,
    IReadOnlyList<string> Goals, string CoreTasks,
    IReadOnlyList<string> StrategicFitLevels, IReadOnlyList<string> PainLevels, IReadOnlyList<string> RetentionRiskLevels);

public sealed record ScoreProblem(string Title, string Description, string Category);

public sealed record ScoreRequest(string Title, string Description);

/// <summary>
/// The strict scoring form: three 1–5 ratings, each with a reason, and the goal Strategic fit serves best
/// (one of <see cref="ScoreInput.Goals"/>; null only when the rating is 1, "serves none of the goals").
/// </summary>
public sealed record ScoreRatings(
    int StrategicFit, string StrategicFitReason, string? Goal,
    int Pain, string PainReason,
    int RetentionRisk, string RetentionRiskReason);
