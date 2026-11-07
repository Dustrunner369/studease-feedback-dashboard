namespace Feedback.Api.Domain;

/// <summary>SQL text built from the fixed vocabulary (check constraints).</summary>
public static class Sql
{
    /// <summary>A quoted SQL list, e.g. <c>'New', 'Won''t do'</c>; single quotes are doubled.</summary>
    public static string List(IEnumerable<string> values) =>
        string.Join(", ", values.Select(v => $"'{v.Replace("'", "''")}'"));
}
