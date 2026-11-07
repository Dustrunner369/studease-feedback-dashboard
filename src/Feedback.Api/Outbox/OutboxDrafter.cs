using Feedback.Api.Ai;
using Feedback.Api.Data;
using Feedback.Api.Triage;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Outbox;

/// <summary>
/// When a Problem ships, queues one Outbox message per distinct Requester email (case-insensitive),
/// listing that Requester's Feature Requests in it, and has the AI draft each one.
/// A Requester already in the Outbox for this Problem (it shipped before and was reopened) gets no second one.
/// AI failures leave the message as Draft failed; nothing here throws for them.
/// </summary>
public sealed class OutboxDrafter(FeedbackDbContext db, IAiGateway ai, AiCallLog log) : IShippedHook
{
    public async Task OnShippedAsync(long problemId, CancellationToken ct)
    {
        var requests = await db.FeatureRequests.AsNoTracking()
            .Where(r => r.ProblemId == problemId).OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Email }).ToListAsync(ct);
        var already = (await db.OutboxMessages.AsNoTracking().Where(m => m.ProblemId == problemId)
            .Select(m => m.ToEmail).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var messages = requests
            .GroupBy(r => r.Email.Trim().ToLowerInvariant())
            .Where(g => !already.Contains(g.Key))
            .Select(g => new OutboxMessage
            {
                ProblemId = problemId,
                ToEmail = g.Key,
                FeatureRequestIds = g.Select(r => r.Id).ToArray(),
                Status = OutboxStatus.Drafting,
            })
            .ToList();
        if (messages.Count == 0) return;
        db.OutboxMessages.AddRange(messages);
        await db.SaveChangesAsync(ct);

        foreach (var message in messages) await DraftAsync(message, ct);
    }

    /// <summary>Drafts (or re-drafts) one tracked message: Ready with the AI's subject and body, or Draft failed.</summary>
    public async Task DraftAsync(OutboxMessage message, CancellationToken ct)
    {
        var problem = message.ProblemId is { } id
            ? await db.Problems.AsNoTracking().Where(p => p.Id == id)
                .Select(p => new ScoreProblem(p.Title, p.Description, p.Category)).SingleOrDefaultAsync(ct)
            : null;
        var requests = await db.FeatureRequests.AsNoTracking()
            .Where(r => message.FeatureRequestIds.Contains(r.Id)).OrderBy(r => r.Id)
            .Select(r => new ScoreRequest(r.Title, r.Description)).ToListAsync(ct);

        UpdateDraft? draft = null;
        if (problem is not null && requests.Count > 0)
        {
            var result = await log.RunLoggedAsync(AiSteps.DraftUpdate, new AiCallFor(message.ProblemId),
                c => ai.DraftUpdateAsync(new DraftUpdateInput(problem, requests), c), ct,
                check: d => string.IsNullOrWhiteSpace(d.Subject) || string.IsNullOrWhiteSpace(d.Body) ? "Empty subject or body." : null);
            message.AiCallId = result.CallId;
            draft = result.Value;
        }

        if (draft is null) message.Status = OutboxStatus.DraftFailed;
        else
        {
            message.Subject = draft.Subject.Trim();
            message.Body = draft.Body.Trim();
            message.Status = OutboxStatus.Ready;
        }
        await db.SaveChangesAsync(ct);
    }
}
