namespace Feedback.Api.Domain;

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

    public static string SqlList => Sql.List(All);
}
