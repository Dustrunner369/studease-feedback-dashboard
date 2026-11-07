using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Feedback.Api.Data;

/// <summary>
/// The latest Suggest Solutions result for a Problem (one row per Problem; a new run replaces it).
/// Goes with its Problem when it is merged away or emptied (FK ON DELETE CASCADE).
/// </summary>
public sealed class SolutionSuggestion
{
    public long ProblemId { get; set; }
    /// <summary>The Solution Ideas as JSON (a list of <c>SolutionIdea</c>), already checked and capped.</summary>
    public required string Ideas { get; set; }
    /// <summary>The repo files the picker chose and the writer read, in pick order.</summary>
    public string[] Files { get; set; } = [];
    public DateTimeOffset GeneratedAt { get; set; }
    public long PickerAiCallId { get; set; }
    public long WriteAiCallId { get; set; }

    public static void Configure(EntityTypeBuilder<SolutionSuggestion> e)
    {
        e.ToTable("solution_suggestions");
        e.HasKey(s => s.ProblemId);
        e.Property(s => s.ProblemId).ValueGeneratedNever();
        e.Property(s => s.Ideas).HasColumnType("jsonb");
        e.Property(s => s.GeneratedAt).HasDefaultValueSql("now()");
        e.HasOne<Problem>().WithOne().HasForeignKey<SolutionSuggestion>(s => s.ProblemId).OnDelete(DeleteBehavior.Cascade);
    }
}
