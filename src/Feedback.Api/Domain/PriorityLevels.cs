namespace Feedback.Api.Domain;

/// <summary>The three levels a Priority or a Recommended Priority can take.</summary>
public static class PriorityLevels
{
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";

    public static readonly IReadOnlyList<string> All = [High, Medium, Low];

    public static bool IsValid(string? level) => level is not null && All.Contains(level);

    public static string SqlList => Sql.List(All);
}
