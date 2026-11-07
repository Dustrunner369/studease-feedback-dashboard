using Feedback.Api.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Feedback.Api.Data;

public static class AiCallStatus
{
    public const string Ok = "OK";
    public const string Failed = "Failed";
}

/// <summary>One row per AI call (written by <see cref="AiCallLog"/>). No foreign keys, so the log outlives deletes.</summary>
public sealed class AiCall
{
    public long Id { get; set; }
    public required string Step { get; set; }
    public required string Status { get; set; }
    public string? FailureKind { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Note { get; set; }
    public long? ProblemId { get; set; }
    public long? FeatureRequestId { get; set; }
    /// <summary>The model name the API reported (GPT-6 models have no dated snapshots).</summary>
    public string? Model { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? CostUsd { get; set; }
    public int LatencyMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    internal static void Configure(EntityTypeBuilder<AiCall> e)
    {
        e.ToTable("ai_calls", t =>
        {
            t.HasCheckConstraint("ck_ai_calls_status", $"status IN ('{AiCallStatus.Ok}', '{AiCallStatus.Failed}')");
            t.HasCheckConstraint("ck_ai_calls_failure_kind",
                $"failure_kind IS NULL OR failure_kind IN ({Domain.Sql.List(AiFailureKind.All)})");
        });
        e.Property(c => c.CostUsd).HasPrecision(12, 8);
        e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
        e.HasIndex(c => c.CreatedAt);
        e.HasIndex(c => c.ProblemId);
    }
}
