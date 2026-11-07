using Feedback.Api.Domain;

namespace Feedback.Api.Data;

/// <summary>Shared, EF-translatable Problem filters.</summary>
public static class ProblemQueries
{
    /// <summary>Open Problems: not Shipped and not Won't do (see <see cref="ProblemStatuses.IsClosed"/>).</summary>
    public static IQueryable<Problem> Open(this IQueryable<Problem> problems) =>
        problems.Where(p => p.Status != ProblemStatuses.Shipped && p.Status != ProblemStatuses.WontDo);

    /// <summary>Closed Problems: Shipped or Won't do (the Archive).</summary>
    public static IQueryable<Problem> Closed(this IQueryable<Problem> problems) =>
        problems.Where(p => p.Status == ProblemStatuses.Shipped || p.Status == ProblemStatuses.WontDo);
}
