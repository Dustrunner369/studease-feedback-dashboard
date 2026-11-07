using System.Security.Cryptography;
using Feedback.Api.Data;
using Feedback.Api.Intake;
using Feedback.Api.Scoring;
using Feedback.Api.Triage;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Seed;

/// <summary>
/// <c>seed load</c>: sends the seed file through the real intake pipeline one request at a time (as if each were
/// submitted on the portal on its date), adds its Upvotes, scores the AI's first answers against the answer key into
/// <c>seed_metrics</c>, then applies the product-team actions through <see cref="ProblemDecisions"/>.
/// Never touches GitHub or Decision Briefs. Carries on past failed AI calls.
/// </summary>
public sealed class SeedLoader(IServiceScopeFactory scopes, IntakeQueue queue)
{
    public async Task<int> LoadAsync(SeedFile file, TextWriter output, CancellationToken ct)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FeedbackDbContext>();
            var nonEmpty = (await FeedbackTables.CountAsync(db, ct)).Where(c => c.Rows > 0).ToList();
            if (nonEmpty.Count > 0)
            {
                output.WriteLine($"The feedback schema already has data ({string.Join(", ", nonEmpty.Select(c => $"{c.Table}: {c.Rows}"))}).");
                output.WriteLine("Nothing was loaded. Run `seed remove` first.");
                return 1;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var answers = new List<(string? IntendedKey, long? ProblemId)>();
        var requestIds = new List<long>();
        output.WriteLine($"Submitting {file.Requests.Count} Feature Requests through the intake pipeline...");
        foreach (var (seed, i) in file.Requests.Select((r, i) => (r, i)))
        {
            var submittedAt = now - TimeSpan.FromDays(seed.DaysAgo);
            var (id, problemId) = await SubmitAsync(seed, submittedAt, now, ct);
            requestIds.Add(id);
            answers.Add((seed.Problem, problemId));
            if (problemId is null) output.WriteLine($"  [{i + 1}] match failed: \"{seed.Title}\" (needs review)");
            if ((i + 1) % 25 == 0) output.WriteLine($"  {i + 1}/{file.Requests.Count}");
        }

        // Every link is still the AI's first answer: score it now, before any product-team action.
        var score = SeedScoring.Score(answers);
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FeedbackDbContext>();
            db.SeedMetrics.Add(new SeedMetrics
            {
                ComputedAt = DateTimeOffset.UtcNow,
                DuplicateCatchRate = score.DuplicateCatchRate,
                GroupingAccuracy = score.GroupingAccuracy,
                AiProblemCount = score.AiProblemCount,
                IntendedProblemCount = score.IntendedProblemCount,
                FailedMatches = score.FailedMatches,
            });
            await db.SaveChangesAsync(ct);
        }

        var applied = new List<string>();
        foreach (var action in file.Actions ?? [])
            applied.Add(await ApplyAsync(action, requestIds[IndexOf(file, action.Request)], ct));

        var matched = file.Requests.Count - score.FailedMatches;
        output.WriteLine();
        output.WriteLine($"{matched} matched, {score.FailedMatches} failed.");
        output.WriteLine($"AI made {score.AiProblemCount} Problems where the data has {score.IntendedProblemCount}.");
        output.WriteLine($"Duplicate catch rate {Percent(score.DuplicateCatchRate)} (target ≥90%), grouping accuracy {Percent(score.GroupingAccuracy)} (target ≥85%).");
        if (applied.Count > 0)
        {
            output.WriteLine("Product-team actions:");
            foreach (var line in applied) output.WriteLine($"  {line}");
        }
        return 0;
    }

    // One portal-style submit, then wait for its embed → match → score to finish before the next (order matters).
    private async Task<(long Id, long? ProblemId)> SubmitAsync(SeedRequest seed, DateTimeOffset submittedAt, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var (valid, _) = SubmissionValidator.Validate(seed.Title, seed.Description, seed.Category, seed.Email);
        var request = await services.GetRequiredService<IntakePipeline>().SubmitAsync(valid!, ct, submittedAt);
        await queue.WhenIdleAsync().WaitAsync(ct);

        var db = services.GetRequiredService<FeedbackDbContext>();
        var problemId = await db.FeatureRequests.Where(r => r.Id == request.Id).Select(r => r.ProblemId).SingleAsync(ct);
        if (seed.Upvotes > 0)
        {
            // Inserted directly (as many browsers would have), at random times since the request was submitted.
            var span = (now - submittedAt).Ticks;
            for (var n = 0; n < seed.Upvotes; n++)
                db.Upvotes.Add(new Upvote
                {
                    FeatureRequestId = request.Id,
                    BrowserToken = RandomToken(),
                    CreatedAt = submittedAt + TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * span)),
                });
            await db.SaveChangesAsync(ct);
            if (problemId is { } p) await services.GetRequiredService<PriorityScorer>().RefreshReachAsync(p, ct);
        }
        return (request.Id, problemId);
    }

    private async Task<string> ApplyAsync(SeedAction action, long requestId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FeedbackDbContext>();
        var problem = await db.FeatureRequests.Where(r => r.Id == requestId)
            .Select(r => r.Problem == null ? null : new { r.Problem.Id, r.Problem.Title, r.Problem.RecommendedLevel })
            .SingleAsync(ct);
        var what = action.Status ?? $"Priority {action.Priority}";
        if (problem is null) return $"skipped {what}: \"{action.Request}\" is unmatched (needs review).";

        var decisions = scope.ServiceProvider.GetRequiredService<ProblemDecisions>();
        DecisionResult result;
        if (action.Status is { } status)
            result = await decisions.SetStatusAsync(problem.Id, status, action.Reason, ct);
        else
        {
            if (problem.RecommendedLevel is not { } recommended)
                return $"skipped {what} on \"{problem.Title}\": it has no Recommended Priority.";
            var level = action.Priority == SeedAction.Override ? SeedAction.OverrideOf(recommended) : recommended;
            what = action.Priority == SeedAction.Override
                ? $"Priority {level} (override of Recommended {recommended})" : $"Priority {level}";
            result = await decisions.ApplyAsync(problem.Id,
                new ProblemChanges { SetsPriority = true, Priority = level, PriorityReason = action.Reason }, ct);
        }
        return result.Errors is { } errors
            ? $"couldn't set {what} on \"{problem.Title}\": {string.Join(" ", errors.Values.SelectMany(e => e))}"
            : $"{what}: \"{problem.Title}\"";
    }

    private static int IndexOf(SeedFile file, string title)
    {
        for (var i = 0; i < file.Requests.Count; i++)
            if (file.Requests[i].Title == title) return i;
        throw new InvalidOperationException($"No seed request is titled \"{title}\".");
    }

    private static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Percent(double? rate) => rate is { } r ? $"{r:P0}" : "n/a";
}
