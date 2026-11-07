using Pgvector;

namespace Feedback.Api.Data;

/// <summary>The underlying need one or more Feature Requests point to. Later slices add Priority and GitHub columns.</summary>
public sealed class Problem
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Category { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    // Recommended Priority (kept current by PriorityScoring; frozen once the Problem is closed).
    /// <summary>Feature Requests × 3 + Upvotes when Reach was last worked out (explains reach_rating).</summary>
    public int? ReachPoints { get; set; }
    public int? ReachRating { get; set; }
    public int? RecommendedScore { get; set; }
    public string? RecommendedLevel { get; set; }
    public long? FirstScoringId { get; set; }
    public long? CurrentScoringId { get; set; }
    public PriorityScoring? CurrentScoring { get; set; }
    /// <summary>The last scoring call failed; the drawer offers Retry.</summary>
    public bool ScoringFailed { get; set; }

    public List<FeatureRequest> FeatureRequests { get; } = [];
}

/// <summary>One AI scoring of a Problem: the three AI-rated criteria. Reach is code's, kept on the Problem.</summary>
public sealed class PriorityScoring
{
    public long Id { get; set; }
    public long ProblemId { get; set; }
    public int StrategicFit { get; set; }
    public required string StrategicFitReason { get; set; }
    public string? StrategicFitGoal { get; set; }
    public int Pain { get; set; }
    public required string PainReason { get; set; }
    public int RetentionRisk { get; set; }
    public required string RetentionRiskReason { get; set; }
    public long AiCallId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>What one Requester submitted. Never edited after submit; ProblemId null means "needs review".</summary>
public sealed class FeatureRequest
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Email { get; set; }
    /// <summary>The Requester's own pick; never changed.</summary>
    public required string Category { get; set; }
    public long? ProblemId { get; set; }
    public Problem? Problem { get; set; }
    public Vector? Embedding { get; set; }
    public string? MatchReason { get; set; }
    public bool IsBundled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One browser's support for a Feature Request. The browser token is random and carries no identity.</summary>
public sealed class Upvote
{
    public long FeatureRequestId { get; set; }
    public required string BrowserToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
