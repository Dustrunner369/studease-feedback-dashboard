using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Feedback.Api.Data;

/// <summary>Where an Outbox message is: Drafting → Ready (or Draft failed → Retry / Edit → Ready) → Sent.</summary>
public static class OutboxStatus
{
    public const string Drafting = "Drafting";
    public const string DraftFailed = "Draft failed";
    public const string Ready = "Ready";
    public const string Sent = "Sent";

    public static readonly IReadOnlyList<string> All = [Drafting, DraftFailed, Ready, Sent];

    public static string SqlList => string.Join(", ", All.Select(s => $"'{s}'"));
}

/// <summary>
/// One update to one Requester (one email) about a shipped Problem, covering all their Feature Requests in it.
/// Drafted by AI, approved by the product team; "Sent" is as far as it goes (nothing is really delivered).
/// Discard deletes the row.
/// </summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }
    /// <summary>Null once the Problem has been deleted (merged away); the message keeps its text.</summary>
    public long? ProblemId { get; set; }
    public long[] FeatureRequestIds { get; set; } = [];
    public required string ToEmail { get; set; }
    /// <summary>Empty until drafted (or written by hand).</summary>
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public required string Status { get; set; }
    /// <summary>The latest draft call (OK or Failed).</summary>
    public long? AiCallId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    public static void Configure(EntityTypeBuilder<OutboxMessage> e)
    {
        e.ToTable("outbox_messages", t =>
            t.HasCheckConstraint("ck_outbox_messages_status", $"status IN ({OutboxStatus.SqlList})"));
        e.Property(m => m.Status).HasMaxLength(16);
        e.Property(m => m.ToEmail).HasMaxLength(254);
        e.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
        e.HasOne<Problem>().WithMany().HasForeignKey(m => m.ProblemId).OnDelete(DeleteBehavior.SetNull);
        e.HasIndex(m => m.ProblemId);
    }
}
