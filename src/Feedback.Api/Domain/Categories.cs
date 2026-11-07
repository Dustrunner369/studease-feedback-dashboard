namespace Feedback.Api.Domain;

/// <summary>The seven fixed areas of the Studease app (see GLOSSARY.md).</summary>
public static class Categories
{
    public static readonly IReadOnlyList<string> All =
    [
        "Finding spots",
        "Adding & editing spots",
        "Spot details",
        "Visits",
        "Profile & account",
        "Social",
        "Other",
    ];

    public static bool IsValid(string? category) => category is not null && All.Contains(category);

    /// <summary>SQL list for check constraints, e.g. <c>'Finding spots', 'Social'</c>.</summary>
    public static string SqlList => string.Join(", ", All.Select(c => $"'{c.Replace("'", "''")}'"));
}
