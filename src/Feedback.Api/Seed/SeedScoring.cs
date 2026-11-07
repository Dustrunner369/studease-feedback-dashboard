namespace Feedback.Api.Seed;

/// <summary>
/// Scores the AI's first answers against the seed file's answer key, as defined in "Pick the success metrics" (#12).
/// <list type="bullet">
/// <item><b>Duplicate catch rate:</b> of the requests written for a Problem an earlier request (in submission order)
/// was also written for, the share the AI linked to an already existing Problem instead of creating one.</item>
/// <item><b>Grouping accuracy:</b> each AI Problem stands for whichever intended Problem most of its requests were
/// written for (ties: the earliest request's); a request is right when its AI Problem stands for its own intended
/// Problem. Spam has no intended Problem, so it is right only in an AI Problem that stands for none.</item>
/// </list>
/// A failed match (no Problem) is wrong on both.
/// </summary>
public static class SeedScoring
{
    /// <param name="answers">Per request in submission order: its intended Problem key (null = spam) and the AI's Problem (null = failed).</param>
    public static SeedScore Score(IReadOnlyList<(string? IntendedKey, long? ProblemId)> answers)
    {
        var seenKeys = new HashSet<string>();
        var seenProblems = new HashSet<long>();
        int duplicates = 0, caught = 0;
        foreach (var (key, problemId) in answers)
        {
            if (key is not null && seenKeys.Contains(key))
            {
                duplicates++;
                if (problemId is { } id && seenProblems.Contains(id)) caught++;
            }
            if (key is not null) seenKeys.Add(key);
            if (problemId is { } p) seenProblems.Add(p);
        }

        var standsFor = answers.Select((a, i) => (a.IntendedKey, a.ProblemId, Index: i))
            .Where(a => a.ProblemId is not null)
            .GroupBy(a => a.ProblemId!.Value)
            .ToDictionary(g => g.Key, g => g
                .GroupBy(a => a.IntendedKey)
                .OrderByDescending(k => k.Count()).ThenBy(k => k.Min(a => a.Index))
                .First().Key);
        var right = answers.Count(a => a.ProblemId is { } id && standsFor[id] == a.IntendedKey);

        return new SeedScore(
            duplicates == 0 ? null : (double)caught / duplicates,
            answers.Count == 0 ? null : (double)right / answers.Count,
            AiProblemCount: standsFor.Count,
            IntendedProblemCount: answers.Where(a => a.IntendedKey is not null).Select(a => a.IntendedKey).Distinct().Count(),
            FailedMatches: answers.Count(a => a.ProblemId is null));
    }
}

public sealed record SeedScore(
    double? DuplicateCatchRate, double? GroupingAccuracy, int AiProblemCount, int IntendedProblemCount, int FailedMatches);
