using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Feedback.Api.Data;

/// <summary>The last priority consistency check (only one is kept; a new run replaces it).</summary>
public sealed class ConsistencyRun
{
    public long Id { get; set; }
    public DateTimeOffset RanAt { get; set; }

    internal static void Configure(EntityTypeBuilder<ConsistencyRun> e)
    {
        e.ToTable("consistency_runs");
        e.Property(r => r.RanAt).HasDefaultValueSql("now()");
    }
}

/// <summary>
/// One open Problem's 3 re-scores in a run. <c>CouldntCheck</c>: a re-score failed, so the Problem is left out
/// of the headline; Levels/Scores then hold only the re-scores that succeeded.
/// </summary>
public sealed class ConsistencyResult
{
    public long RunId { get; set; }
    public long ProblemId { get; set; }
    public string[] Levels { get; set; } = [];
    public int[] Scores { get; set; } = [];
    public bool CouldntCheck { get; set; }

    internal static void Configure(EntityTypeBuilder<ConsistencyResult> e)
    {
        e.ToTable("consistency_results");
        e.HasKey(r => new { r.RunId, r.ProblemId });
        e.HasOne<ConsistencyRun>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Cascade);
        // Merged-away or emptied Problems drop out of the last run.
        e.HasOne<Problem>().WithMany().HasForeignKey(r => r.ProblemId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(r => r.ProblemId);
    }
}
