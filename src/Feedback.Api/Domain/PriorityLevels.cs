using Feedback.Api.Scoring;

namespace Feedback.Api.Domain;

/// <summary>The three levels a Priority or a Recommended Priority can take.</summary>
public static class PriorityLevels
{
    public static readonly IReadOnlyList<string> All = [PriorityCriteria.High, PriorityCriteria.Medium, PriorityCriteria.Low];

    public static bool IsValid(string? level) => level is not null && All.Contains(level);

    public static string SqlList => string.Join(", ", All.Select(l => $"'{l}'"));
}

/// <summary>What a decision_log row records.</summary>
public static class DecisionKinds
{
    public const string Status = "status";
    public const string Priority = "priority";
    public const string Merge = "merge";
    public const string Move = "move";
    public const string Rename = "rename";
    public const string Category = "category";

    public static readonly IReadOnlyList<string> All = [Status, Priority, Merge, Move, Rename, Category];

    public static string SqlList => string.Join(", ", All.Select(k => $"'{k}'"));
}
