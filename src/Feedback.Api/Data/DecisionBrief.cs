using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Feedback.Api.Data;

/// <summary>
/// A Problem's latest Decision Brief (one row per Problem; Regenerate replaces it).
/// Content is the stored brief (Who's asking + checked claims) as JSON; CoveredFeatureRequestIds are the
/// Problem's Feature Requests when it was written, so it reads as Out of date once they differ.
/// </summary>
public sealed class DecisionBrief
{
    public long Id { get; set; }
    public long ProblemId { get; set; }
    public required string Content { get; set; }
    public long[] CoveredFeatureRequestIds { get; set; } = [];
    public long AiCallId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static void Configure(EntityTypeBuilder<DecisionBrief> e)
    {
        e.ToTable("decision_briefs");
        e.Property(b => b.Content).HasColumnType("jsonb");
        e.Property(b => b.CreatedAt).HasDefaultValueSql("now()");
        // The brief goes with its Problem when it is merged away or emptied.
        e.HasOne<Problem>().WithMany().HasForeignKey(b => b.ProblemId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(b => b.ProblemId).IsUnique();
    }
}
