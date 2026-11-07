using System.Text.Json;
using Feedback.Api.Data;

namespace Feedback.Api.Triage;

/// <summary>
/// The append-only record of what the product team decided. <see cref="Add"/> only stages the row,
/// so it is saved in the same SaveChanges (and transaction) as the change it records.
/// </summary>
public static class DecisionLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="kind">One of <see cref="Domain.DecisionKinds"/>.</param>
    /// <param name="details">Any object; stored as camelCase JSON (e.g. <c>new { from, to }</c>).</param>
    public static void Add(FeedbackDbContext db, long? problemId, string kind, string? reason, object details) =>
        db.DecisionLog.Add(new DecisionLogEntry
        {
            ProblemId = problemId,
            Kind = kind,
            Reason = reason,
            Details = JsonSerializer.Serialize(details, Json),
        });
}
