using System.Text.Json.Nodes;
using Feedback.Api.Data;
using Feedback.Api.Domain;
using Feedback.Api.Intake;
using Feedback.Api.Scoring;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Triage;

/// <summary>
/// The product team's decisions on one Problem: rename, description, Category, status and Priority.
/// Every change writes a decision_log row in the same save. Status changes all go through here,
/// so closing (Shipped / Won't do), reopening and the Shipped hooks happen in one place:
/// call <see cref="SetStatusAsync"/> from anywhere else that changes a status (e.g. GitHub's "Mark Shipped?").
/// </summary>
public sealed class ProblemDecisions(FeedbackDbContext db, PriorityScorer scorer, IEnumerable<IShippedHook> shippedHooks)
{
    public const int ReasonMax = 300;

    /// <summary>Sets the status (with a reason, required for Won't do).</summary>
    public Task<DecisionResult> SetStatusAsync(long problemId, string status, string? reason, CancellationToken ct) =>
        ApplyAsync(problemId, new ProblemChanges { Status = status, Reason = reason }, ct);

    public async Task<DecisionResult> ApplyAsync(long problemId, ProblemChanges changes, CancellationToken ct)
    {
        var problem = await db.Problems.SingleOrDefaultAsync(p => p.Id == problemId, ct);
        if (problem is null) return DecisionResult.NotFound;

        var errors = new Dictionary<string, string[]>();
        var title = changes.Title?.Trim();
        var description = changes.Description?.Trim();
        var reason = Trimmed(changes.Reason);
        var priorityReason = Trimmed(changes.PriorityReason);

        if (changes.Title is not null && string.IsNullOrEmpty(title)) errors["title"] = ["Give the Problem a title."];
        else if (title?.Length > SubmissionValidator.TitleMax) errors["title"] = [$"Keep the title to {SubmissionValidator.TitleMax} characters."];
        if (changes.Description is not null && string.IsNullOrEmpty(description)) errors["description"] = ["Describe the need."];
        else if (description?.Length > SubmissionValidator.DescriptionMax)
            errors["description"] = [$"Keep it to {SubmissionValidator.DescriptionMax} characters."];
        if (changes.Category is not null && !Categories.IsValid(changes.Category)) errors["category"] = ["Unknown Category."];
        if (changes.Status is not null && !ProblemStatuses.IsValid(changes.Status)) errors["status"] = ["Unknown status."];
        if (changes.SetsPriority && changes.Priority is not null && !PriorityLevels.IsValid(changes.Priority))
            errors["priority"] = ["Priority is High, Medium or Low."];

        var settingStatus = changes.Status is not null && changes.Status != problem.Status;
        var settingPriority = changes.SetsPriority && changes.Priority != problem.Priority;
        var overrides = settingPriority && changes.Priority is not null
            && problem.RecommendedLevel is not null && changes.Priority != problem.RecommendedLevel;

        if (ReasonError(reason) is { } bad) errors["reason"] = [bad];
        else if (reason is null && settingStatus && changes.Status == ProblemStatuses.WontDo)
            errors["reason"] = ["Say why it's Won't do."];
        if (ReasonError(priorityReason) is { } badPriority) errors["priorityReason"] = [badPriority];
        else if (priorityReason is null && overrides)
            errors["priorityReason"] = [$"Say why the Priority differs from the Recommended Priority ({problem.RecommendedLevel})."];

        if (errors.Count > 0) return DecisionResult.Invalid(errors);

        if (title is not null && title != problem.Title)
        {
            DecisionLog.Add(db, problem.Id, DecisionKinds.Rename, null, new { field = "title", from = problem.Title, to = title });
            problem.Title = title;
        }
        if (description is not null && description != problem.Description)
        {
            DecisionLog.Add(db, problem.Id, DecisionKinds.Rename, null, new { field = "description", from = problem.Description, to = description });
            problem.Description = description;
        }
        if (changes.Category is not null && changes.Category != problem.Category)
        {
            DecisionLog.Add(db, problem.Id, DecisionKinds.Category, null, new { from = problem.Category, to = changes.Category });
            problem.Category = changes.Category;
        }
        if (settingPriority)
        {
            DecisionLog.Add(db, problem.Id, DecisionKinds.Priority, overrides ? priorityReason : null,
                new { from = problem.Priority, to = changes.Priority, recommended = problem.RecommendedLevel });
            problem.Priority = changes.Priority;
            problem.PriorityOverrideReason = overrides ? priorityReason : null;
        }

        var wasClosed = ProblemStatuses.IsClosed(problem.Status);
        if (settingStatus)
        {
            var status = changes.Status!;
            DecisionLog.Add(db, problem.Id, DecisionKinds.Status, reason, new { from = problem.Status, to = status });
            problem.Status = status;
            // Closing freezes the Recommended Priority: PriorityScorer leaves closed Problems alone.
            problem.ClosedAt = ProblemStatuses.IsClosed(status) ? (wasClosed ? problem.ClosedAt : DateTimeOffset.UtcNow) : null;
        }
        await db.SaveChangesAsync(ct);

        if (settingStatus)
        {
            // Reopened: scored again from scratch (one AI call), so the frozen ratings and the Reach it missed
            // while closed both catch up. A failed call sets scoring_failed (Retry) and keeps the last ratings.
            if (wasClosed && !ProblemStatuses.IsClosed(problem.Status)) await scorer.RescoreAsync(problem.Id, ct);
            if (problem.Status == ProblemStatuses.Shipped)
                foreach (var hook in shippedHooks) await hook.OnShippedAsync(problem.Id, ct);
        }
        return DecisionResult.Ok;
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? ReasonError(string? reason) =>
        reason is not null && (reason.Contains('\n') || reason.Contains('\r')) ? "Keep the reason to one line."
        : reason?.Length > ReasonMax ? $"Keep the reason to {ReasonMax} characters."
        : null;
}

/// <summary>
/// What a PATCH asks to change; null leaves a field alone. Priority is set only when <see cref="SetsPriority"/>
/// (so <c>"priority": null</c> clears it to Not set). Reason goes with the status change (required for Won't do);
/// PriorityReason with the Priority (required when it differs from the Recommended level), so one PATCH can do both.
/// </summary>
public sealed class ProblemChanges
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? Status { get; init; }
    public bool SetsPriority { get; init; }
    public string? Priority { get; init; }
    public string? Reason { get; init; }
    public string? PriorityReason { get; init; }

    /// <summary>Reads a PATCH body; null if it isn't a JSON object of strings.</summary>
    public static ProblemChanges? From(JsonObject? body)
    {
        if (body is null) return null;
        string? Text(string name) => body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        bool Bad(string name) => body[name] is { } node && !(node is JsonValue v && v.TryGetValue<string>(out _));
        if (new[] { "title", "description", "category", "status", "priority", "reason", "priorityReason" }.Any(Bad)) return null;
        return new ProblemChanges
        {
            Title = Text("title"),
            Description = Text("description"),
            Category = Text("category"),
            Status = Text("status"),
            SetsPriority = body.ContainsKey("priority"),
            Priority = Text("priority"),
            Reason = Text("reason"),
            PriorityReason = Text("priorityReason"),
        };
    }
}

public sealed record DecisionResult(bool Found, Dictionary<string, string[]>? Errors)
{
    public static readonly DecisionResult Ok = new(true, null);
    public static readonly DecisionResult NotFound = new(false, null);
    public static DecisionResult Invalid(Dictionary<string, string[]> errors) => new(true, errors);
}
