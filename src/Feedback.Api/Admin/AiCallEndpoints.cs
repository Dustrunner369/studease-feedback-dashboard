using Feedback.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Admin;

/// <summary>Settings → AI call log, also the per-Problem view in the drawer.</summary>
public static class AiCallEndpoints
{
    /// <summary>The log shows the latest calls only; a demo backlog stays well under this.</summary>
    public const int Limit = 500;

    public static void MapAiCalls(this RouteGroupBuilder admin)
    {
        admin.MapGet("/ai-calls", async (string? status, long? problemId, FeedbackDbContext db, CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(status) && status is not (AiCallStatus.Ok or AiCallStatus.Failed))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Unknown status."] });

            var calls = db.AiCalls.AsNoTracking();
            if (!string.IsNullOrEmpty(status)) calls = calls.Where(c => c.Status == status);
            // A Problem's calls: those made for it, and those made for its Feature Requests (embed, match).
            if (problemId is { } id)
                calls = calls.Where(c => c.ProblemId == id ||
                    db.FeatureRequests.Any(r => r.ProblemId == id && r.Id == c.FeatureRequestId));

            var log = await calls.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Take(Limit)
                .Select(c => new AiCallView(
                    c.Id, c.Step, c.Status, c.FailureKind, c.ErrorMessage, c.Note, c.ProblemId, c.FeatureRequestId,
                    c.Model, c.InputTokens, c.OutputTokens, c.CostUsd, c.LatencyMs, c.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(log);
        });
    }
}

public sealed record AiCallView(
    long Id, string Step, string Status, string? FailureKind, string? ErrorMessage, string? Note,
    long? ProblemId, long? FeatureRequestId, string? Model, int? InputTokens, int? OutputTokens,
    decimal? CostUsd, int LatencyMs, DateTimeOffset CreatedAt);
