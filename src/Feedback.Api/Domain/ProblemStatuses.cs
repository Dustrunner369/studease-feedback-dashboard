namespace Feedback.Api.Domain;

public static class ProblemStatuses
{
    public const string New = "New";
    public const string UnderReview = "Under review";
    public const string Planned = "Planned";
    public const string InProgress = "In progress";
    public const string Shipped = "Shipped";
    public const string WontDo = "Won't do";

    public static readonly IReadOnlyList<string> All = [New, UnderReview, Planned, InProgress, Shipped, WontDo];

    public static string SqlList => string.Join(", ", All.Select(s => $"'{s.Replace("'", "''")}'"));
}
