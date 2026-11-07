using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Feedback.Api.Data;

/// <summary>
/// The one row <c>seed load</c> writes: how well the AI's first answers grouped the seed backlog,
/// scored against the seed file's answer key (which never enters the database).
/// </summary>
public sealed class SeedMetrics
{
    public long Id { get; set; }
    public DateTimeOffset ComputedAt { get; set; }
    /// <summary>0–1; null when no seed request had an earlier one written for the same Problem.</summary>
    public double? DuplicateCatchRate { get; set; }
    /// <summary>0–1; null for an empty load.</summary>
    public double? GroupingAccuracy { get; set; }
    public int AiProblemCount { get; set; }
    public int IntendedProblemCount { get; set; }
    public int FailedMatches { get; set; }

    internal static void Configure(EntityTypeBuilder<SeedMetrics> e)
    {
        e.ToTable("seed_metrics");
        e.Property(m => m.ComputedAt).HasDefaultValueSql("now()");
    }
}
