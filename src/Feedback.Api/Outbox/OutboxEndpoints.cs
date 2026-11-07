using Feedback.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Outbox;

/// <summary>
/// /admin Outbox: Drafts (Drafting, Draft failed, Ready) and Sent. Approve marks a Ready message Sent
/// (the demo delivers nothing); Edit rewrites any unsent message and makes it Ready; Discard deletes it.
/// </summary>
public static class OutboxEndpoints
{
    public const int SubjectMax = 200;
    public const int BodyMax = 5000;

    public static void MapOutbox(this RouteGroupBuilder admin)
    {
        admin.MapGet("/outbox", async (string? tab, FeedbackDbContext db, CancellationToken ct) =>
        {
            tab = string.IsNullOrEmpty(tab) ? "drafts" : tab;
            if (tab is not ("drafts" or "sent"))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["tab"] = ["Tab is drafts or sent."] });
            var messages = db.OutboxMessages.AsNoTracking();
            messages = tab == "sent"
                ? messages.Where(m => m.Status == OutboxStatus.Sent).OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id)
                : messages.Where(m => m.Status != OutboxStatus.Sent).OrderByDescending(m => m.CreatedAt).ThenBy(m => m.Id);
            return Results.Ok(await ViewsAsync(db, await messages.ToListAsync(ct), ct));
        });

        admin.MapPatch("/outbox/{id:long}", async (long id, OutboxEdit body, FeedbackDbContext db, CancellationToken ct) =>
        {
            var message = await db.OutboxMessages.SingleOrDefaultAsync(m => m.Id == id, ct);
            if (message is null) return Results.NotFound();
            if (message.Status == OutboxStatus.Sent) return Results.Conflict();
            var subject = body.Subject?.Trim() ?? "";
            var text = body.Body?.Trim() ?? "";
            var errors = new Dictionary<string, string[]>();
            if (subject.Length == 0) errors["subject"] = ["Give the update a subject."];
            else if (subject.Length > SubjectMax || subject.Contains('\n')) errors["subject"] = [$"Keep the subject to one line of {SubjectMax} characters."];
            if (text.Length == 0) errors["body"] = ["Write the update."];
            else if (text.Length > BodyMax) errors["body"] = [$"Keep it to {BodyMax} characters."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            (message.Subject, message.Body, message.Status) = (subject, text, OutboxStatus.Ready);
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ViewAsync(db, message, ct));
        });

        admin.MapDelete("/outbox/{id:long}", async (long id, FeedbackDbContext db, CancellationToken ct) =>
        {
            var message = await db.OutboxMessages.SingleOrDefaultAsync(m => m.Id == id, ct);
            if (message is null) return Results.NotFound();
            if (message.Status == OutboxStatus.Sent) return Results.Conflict();
            db.OutboxMessages.Remove(message);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapPost("/outbox/{id:long}/send", async (long id, FeedbackDbContext db, CancellationToken ct) =>
        {
            var message = await db.OutboxMessages.SingleOrDefaultAsync(m => m.Id == id, ct);
            if (message is null) return Results.NotFound();
            if (message.Status != OutboxStatus.Ready) return Results.Conflict();
            // Demo: Sent is the end of the line; no email is delivered.
            (message.Status, message.SentAt) = (OutboxStatus.Sent, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ViewAsync(db, message, ct));
        });

        admin.MapPost("/outbox/{id:long}/retry", async (long id, FeedbackDbContext db, OutboxDrafter drafter, CancellationToken ct) =>
        {
            var message = await db.OutboxMessages.SingleOrDefaultAsync(m => m.Id == id, ct);
            if (message is null) return Results.NotFound();
            // Drafting too: a restart mid-draft leaves it there for good.
            if (message.Status is not (OutboxStatus.DraftFailed or OutboxStatus.Drafting)) return Results.Conflict();
            await drafter.DraftAsync(message, ct);
            return Results.Ok(await ViewAsync(db, message, ct));
        });
    }

    private static async Task<OutboxMessageView> ViewAsync(FeedbackDbContext db, OutboxMessage message, CancellationToken ct) =>
        (await ViewsAsync(db, [message], ct)).Single();

    private static async Task<List<OutboxMessageView>> ViewsAsync(FeedbackDbContext db, List<OutboxMessage> messages, CancellationToken ct)
    {
        var problemIds = messages.Select(m => m.ProblemId).OfType<long>().Distinct().ToList();
        var titles = await db.Problems.AsNoTracking().Where(p => problemIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Title, ct);
        var requestIds = messages.SelectMany(m => m.FeatureRequestIds).Distinct().ToList();
        var requests = await db.FeatureRequests.AsNoTracking().Where(r => requestIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Title, ct);
        return messages.Select(m => new OutboxMessageView(
            m.Id, m.ProblemId, m.ProblemId is { } p ? titles.GetValueOrDefault(p) : null,
            m.FeatureRequestIds.Where(requests.ContainsKey).Select(r => new OutboxRequestView(r, requests[r])).ToList(),
            m.ToEmail, m.Subject, m.Body, m.Status, m.CreatedAt, m.SentAt)).ToList();
    }
}

public sealed record OutboxEdit(string? Subject, string? Body);

/// <summary>One Outbox message. ProblemTitle is null once the Problem is gone.</summary>
public sealed record OutboxMessageView(
    long Id, long? ProblemId, string? ProblemTitle, IReadOnlyList<OutboxRequestView> FeatureRequests, string ToEmail,
    string Subject, string Body, string Status, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);

public sealed record OutboxRequestView(long Id, string Title);
