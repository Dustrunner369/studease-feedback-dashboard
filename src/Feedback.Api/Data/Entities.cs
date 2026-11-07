using Pgvector;

namespace Feedback.Api.Data;

/// <summary>The underlying need one or more Feature Requests point to. Later slices add scoring, Priority and GitHub columns.</summary>
public sealed class Problem
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Category { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public List<FeatureRequest> FeatureRequests { get; } = [];
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
