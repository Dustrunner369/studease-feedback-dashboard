using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Ai;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class ConsistencyCheckTests(FeedbackDatabase database) : AdminTest(database)
{
    private const string FindSpots = "Help students find new study spots";

    // One Feature Request each, so Reach is 2 (7.5 points) throughout.
    private static readonly ScoreRatings Medium46 = Ratings(fit: 4, pain: 3, retention: 2); // 22.5 + 7.5 + 12.5 + 3.75
    private static readonly ScoreRatings High70 = Ratings(fit: 4, pain: 5, retention: 5);   // 22.5 + 7.5 + 25 + 15

    [Fact]
    public async Task Metrics_show_no_consistency_check_until_one_runs()
    {
        var metrics = await MetricsAsync();

        Assert.Null(metrics.Consistency);
        Assert.Equal(0.90, metrics.Targets.PriorityConsistency);
    }

    [Fact]
    public async Task A_stable_run_re_scores_every_open_problem_three_times_and_logs_each_call()
    {
        Ai.Score = _ => Medium46;
        var (openings, _) = await NewProblemAsync("Filter by opening hours");
        var (noise, _) = await NewProblemAsync("Noise levels");
        var (_, shippedRequest) = await NewProblemAsync("Dark mode");
        await SetProblemStatusAsync(shippedRequest, "Shipped");
        Ai.ScoreCalls.Clear();

        var run = (await RunAsync()).Consistency!;

        Assert.Equal(2, run.Checked);
        Assert.Equal(1.0, run.SameLevelShare);
        Assert.Equal(0.0, run.AverageSpread);
        Assert.Equal(0, run.CouldntCheck);
        Assert.Empty(run.Changed);
        Assert.True(run.RanAt > DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(6, Ai.ScoreCalls.Count);
        Assert.DoesNotContain(Ai.ScoreCalls, c => c.Problem.Title == "Dark mode");
        foreach (var id in new[] { openings, noise })
        {
            var calls = (await Client.GetFromJsonAsync<AiCallJson[]>($"/api/admin/ai-calls?problemId={id}"))!;
            Assert.Equal(4, calls.Count(c => c.Step == "Score" && c.Status == "OK")); // intake + 3 re-scores
        }
        Assert.Equal(run, (await MetricsAsync()).Consistency);
    }

    [Fact]
    public async Task A_problem_that_changes_level_is_listed_and_its_saved_priority_is_untouched()
    {
        Ai.Score = _ => Medium46;
        var (openings, _) = await NewProblemAsync("Filter by opening hours");
        await NewProblemAsync("Noise levels");
        var before = await DrawerAsync(openings);

        var answers = new ConcurrentDictionary<string, int>();
        Ai.Score = input =>
        {
            var n = answers.AddOrUpdate(input.Problem.Title, 1, (_, k) => k + 1);
            return input.Problem.Title == "Filter by opening hours" && n == 2 ? High70 : Medium46;
        };

        var run = (await RunAsync()).Consistency!;

        Assert.Equal(2, run.Checked);
        Assert.Equal(0.5, run.SameLevelShare);
        Assert.Equal(12.0, run.AverageSpread); // (70 − 46 + 0) / 2
        var changed = Assert.Single(run.Changed);
        Assert.Equal((openings, "Filter by opening hours"), (changed.ProblemId, changed.Title));
        Assert.Equal(["Medium", "High", "Medium"], changed.Levels);
        Assert.Equal([46, 70, 46], changed.Scores);

        Assert.Equal(before, await DrawerAsync(openings));
    }

    [Fact]
    public async Task A_failed_re_score_is_left_out_of_the_headline_and_counted_as_couldnt_check()
    {
        Ai.Score = _ => Medium46;
        var (openings, _) = await NewProblemAsync("Filter by opening hours");
        await NewProblemAsync("Noise levels");
        var before = await DrawerAsync(openings);

        var answers = new ConcurrentDictionary<string, int>();
        Ai.Score = input =>
        {
            var n = answers.AddOrUpdate(input.Problem.Title, 1, (_, k) => k + 1);
            if (input.Problem.Title == "Filter by opening hours" && n == 2)
                throw new AiCallException(AiFailureKind.Timeout, "Timed out");
            return Medium46;
        };

        var run = (await RunAsync()).Consistency!;

        Assert.Equal(1, run.Checked);
        Assert.Equal(1, run.CouldntCheck);
        Assert.Equal(1.0, run.SameLevelShare);
        Assert.Equal(0.0, run.AverageSpread);
        Assert.Empty(run.Changed);

        Assert.Equal(before, await DrawerAsync(openings)); // not marked as failed scoring either
        var calls = (await Client.GetFromJsonAsync<AiCallJson[]>($"/api/admin/ai-calls?problemId={openings}&status=Failed"))!;
        Assert.Equal("Score", Assert.Single(calls).Step);
    }

    [Fact]
    public async Task Only_the_last_run_is_kept()
    {
        Ai.Score = _ => Medium46;
        await NewProblemAsync("Filter by opening hours");
        var answers = new ConcurrentDictionary<string, int>();
        Ai.Score = input => answers.AddOrUpdate(input.Problem.Title, 1, (_, k) => k + 1) == 2 ? High70 : Medium46;
        Assert.Single((await RunAsync()).Consistency!.Changed);

        Ai.Score = _ => Medium46;
        var second = (await RunAsync()).Consistency!;

        Assert.Empty(second.Changed);
        Assert.Equal(1.0, second.SameLevelShare);
        Assert.Equal(second, (await MetricsAsync()).Consistency);
    }

    [Fact]
    public async Task With_no_open_problems_the_run_has_no_headline()
    {
        var run = (await RunAsync()).Consistency!;

        Assert.Equal((0, 0), (run.Checked, run.CouldntCheck));
        Assert.Null(run.SameLevelShare);
        Assert.Null(run.AverageSpread);
    }

    [Fact]
    public async Task The_check_needs_sign_in()
    {
        await Client.PostAsync("/api/admin/logout", null);

        var response = await Client.PostAsync("/api/admin/metrics/consistency-check", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<ConsistencyMetricsJson> RunAsync()
    {
        var response = await Client.PostAsync("/api/admin/metrics/consistency-check", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConsistencyMetricsJson>())!;
    }

    private async Task<ConsistencyMetricsJson> MetricsAsync() =>
        (await Client.GetFromJsonAsync<ConsistencyMetricsJson>("/api/admin/metrics"))!;

    private async Task<string> DrawerAsync(long id) => await Client.GetStringAsync($"/api/admin/problems/{id}");

    private static ScoreRatings Ratings(int fit, int pain, int retention) => new(
        fit, $"fit reason {fit}", FindSpots, pain, $"pain reason {pain}", retention, $"retention reason {retention}");
}

public sealed record ConsistencyMetricsJson(ConsistencyTargetsJson Targets, ConsistencyJson? Consistency);

public sealed record ConsistencyTargetsJson(double PriorityConsistency);

public sealed record ConsistencyJson(
    DateTimeOffset RanAt, int Checked, double? SameLevelShare, double? AverageSpread, int CouldntCheck,
    ChangedLevelJson[] Changed)
{
    // Records compare arrays by reference; compare the parts that matter.
    public bool Equals(ConsistencyJson? other) => other is not null
        && RanAt == other.RanAt && Checked == other.Checked && SameLevelShare == other.SameLevelShare
        && AverageSpread == other.AverageSpread && CouldntCheck == other.CouldntCheck
        && Changed.SequenceEqual(other.Changed);

    public override int GetHashCode() => HashCode.Combine(RanAt, Checked);
}

public sealed record ChangedLevelJson(long ProblemId, string Title, string[] Levels, int[] Scores)
{
    public bool Equals(ChangedLevelJson? other) => other is not null
        && ProblemId == other.ProblemId && Title == other.Title
        && Levels.SequenceEqual(other.Levels) && Scores.SequenceEqual(other.Scores);

    public override int GetHashCode() => HashCode.Combine(ProblemId, Title);
}
