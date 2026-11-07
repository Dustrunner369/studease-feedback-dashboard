using Feedback.Api.Data;
using Feedback.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Scoring;

/// <summary>
/// The priority consistency check: re-scores every open Problem <see cref="Runs"/> times against the current
/// Priority Criteria via <see cref="PriorityScorer.TryScoreAsync"/> (each call logged, nothing about the Problem
/// saved) and replaces the last run with this one.
/// </summary>
public sealed class ConsistencyCheck(FeedbackDbContext db, IServiceScopeFactory scopes)
{
    public const int Runs = 3;

    // Problems run side by side (each in its own scope, since a DbContext is single-threaded) so a seeded
    // backlog's ~45 calls finish in one request; each Problem's 3 re-scores run in order.
    private const int Parallelism = 4;

    public async Task RunAsync(CancellationToken ct)
    {
        var problemIds = await OpenProblems().OrderBy(p => p.Id).Select(p => p.Id).ToListAsync(ct);

        var results = new List<ConsistencyResult>();
        await Parallel.ForEachAsync(problemIds,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (problemId, token) =>
            {
                var result = await CheckAsync(problemId, token);
                lock (results) results.Add(result);
            });

        // A Problem merged away or closed while the check ran drops out.
        var stillOpen = (await OpenProblems().Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        results.RemoveAll(r => !stillOpen.Contains(r.ProblemId));

        await db.ConsistencyRuns.ExecuteDeleteAsync(ct); // results go with it (cascade)
        var run = new ConsistencyRun();
        db.ConsistencyRuns.Add(run);
        await db.SaveChangesAsync(ct);
        foreach (var r in results) r.RunId = run.Id;
        db.ConsistencyResults.AddRange(results);
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<Problem> OpenProblems() => db.Problems.AsNoTracking()
        .Where(p => p.Status != ProblemStatuses.Shipped && p.Status != ProblemStatuses.WontDo);

    private async Task<ConsistencyResult> CheckAsync(long problemId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var scorer = scope.ServiceProvider.GetRequiredService<PriorityScorer>();
        var trials = new List<ScoreTrial>();
        var failed = false;
        for (var i = 1; i <= Runs; i++)
        {
            var trial = await scorer.TryScoreAsync(problemId, $"Consistency check, re-score {i} of {Runs}.", ct);
            if (trial is null) failed = true;
            else trials.Add(trial);
        }
        return new ConsistencyResult
        {
            ProblemId = problemId,
            Levels = trials.Select(t => t.Level).ToArray(),
            Scores = trials.Select(t => t.Score).ToArray(),
            CouldntCheck = failed,
        };
    }
}
