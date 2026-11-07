using System.Net.Http.Json;
using Feedback.Api.Ai;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class RecommendedPriorityTests(FeedbackDatabase database) : AdminTest(database)
{
    private const string FindSpots = "Help students find new study spots";

    [Fact]
    public async Task A_new_problem_is_scored_against_the_criteria_and_the_drawer_explains_the_score()
    {
        Ai.Score = _ => Ratings(fit: 4, pain: 3, retention: 2);

        var (problemId, _) = await NewProblemAsync("Filter by opening hours");

        var recommended = (await DrawerAsync(problemId)).Recommended!;
        // 30 × 3/4 + 30 × 1/4 (1 Feature Request = 3 Reach points → 2) + 25 × 2/4 + 15 × 1/4 = 46.25
        Assert.Equal(46, recommended.Score);
        Assert.Equal("Medium", recommended.Level);
        Assert.Equal(
            [
                new CriterionScoreJson("Strategic fit", 30, 4, 22.5m, "fit reason 4", FindSpots),
                new CriterionScoreJson("Reach", 30, 2, 7.5m, "3 Reach points (Feature Requests × 3 + Upvotes).", null),
                new CriterionScoreJson("Pain", 25, 3, 12.5m, "pain reason 3", null),
                new CriterionScoreJson("Retention risk", 15, 2, 3.75m, "retention reason 2", null),
            ],
            recommended.Breakdown);
    }

    [Theory]
    // Reach is 2 (one Feature Request, 3 points) → 7.5 in every row.
    [InlineData(4, 5, 5, 70, "High")]   // 22.5 + 7.5 + 25 + 15
    [InlineData(4, 5, 4, 66, "Medium")] // 22.5 + 7.5 + 25 + 11.25 = 66.25
    [InlineData(2, 5, 1, 40, "Medium")] // 7.5 + 7.5 + 25 + 0
    [InlineData(1, 5, 1, 33, "Low")]    // 0 + 7.5 + 25 + 0 = 32.5 → 33
    [InlineData(1, 1, 1, 8, "Low")]     // 7.5
    public async Task The_score_and_level_follow_the_weights_and_cut_offs(int fit, int pain, int retention, int score, string level)
    {
        Ai.Score = _ => Ratings(fit, pain, retention, goal: fit == 1 ? null : FindSpots);

        var (problemId, _) = await NewProblemAsync("Filter by opening hours");

        var recommended = (await DrawerAsync(problemId)).Recommended!;
        Assert.Equal((score, level), (recommended.Score, recommended.Level));
    }

    [Fact]
    public async Task Upvotes_and_un_upvotes_refresh_reach_without_an_ai_call()
    {
        Ai.Score = _ => Ratings(fit: 3, pain: 3, retention: 3); // 15 + 12.5 + 7.5 = 35, plus Reach
        var (problemId, requestId) = await NewProblemAsync("Filter by opening hours");
        Assert.Equal(43, (await DrawerAsync(problemId)).Recommended!.Score); // Reach 2 (3 points): +7.5 → 42.5

        await UpvoteAsync(requestId, 6); // 9 points: still Reach 2
        Assert.Equal(43, (await DrawerAsync(problemId)).Recommended!.Score);

        await UpvoteAsync(requestId, 7); // 10 points: Reach 3
        var reached = (await DrawerAsync(problemId)).Recommended!;
        Assert.Equal((50, "Medium"), (reached.Score, reached.Level));
        Assert.Equal(new CriterionScoreJson("Reach", 30, 3, 15m, "10 Reach points (Feature Requests × 3 + Upvotes).", null),
            reached.Breakdown[1]);

        await UnUpvoteAsync(requestId, 6);
        Assert.Equal(43, (await DrawerAsync(problemId)).Recommended!.Score);

        Assert.Single(Ai.ScoreCalls);
    }

    [Theory]
    [InlineData(2, 14, 4)] // 20 points
    [InlineData(1, 36, 4)] // 39 points
    [InlineData(1, 37, 5)] // 40 points
    public async Task Reach_points_fall_into_fixed_bands(int requests, int upvotes, int reach)
    {
        var (problemId, requestId) = await NewProblemAsync("Filter by opening hours");
        for (var i = 1; i < requests; i++) await AddToProblemAsync(problemId, $"Me too {i}");
        await UpvoteAsync(requestId, upvotes);

        Assert.Equal(reach, (await DrawerAsync(problemId)).Recommended!.Breakdown.Single(c => c.Criterion == "Reach").Rating);
    }

    [Fact]
    public async Task A_new_link_rescores_the_problem_with_its_requests()
    {
        Ai.Score = _ => Ratings(fit: 2, pain: 2, retention: 1);
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        Ai.Score = _ => Ratings(fit: 4, pain: 4, retention: 4);

        await AddToProblemAsync(problemId, "Show spots open now");

        var recommended = (await DrawerAsync(problemId)).Recommended!;
        // 22.5 + Reach 2 (6 points) 7.5 + 18.75 + 11.25 = 60
        Assert.Equal((60, "Medium"), (recommended.Score, recommended.Level));
        Assert.Equal("pain reason 4", recommended.Breakdown.Single(c => c.Criterion == "Pain").Reason);

        var input = Ai.ScoreCalls.Last();
        Assert.Equal("Filter by opening hours", input.Problem.Title);
        Assert.Equal(["Request for Filter by opening hours", "Show spots open now"], input.FeatureRequests.Select(r => r.Title));
        Assert.Equal(3, input.Goals.Count);
        Assert.Equal(2, Ai.ScoreCalls.Count);
    }

    [Fact]
    public async Task Scoring_is_logged_as_its_own_ai_call_for_the_problem()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");

        var calls = (await Client.GetFromJsonAsync<AiCallJson[]>($"/api/admin/ai-calls?problemId={problemId}"))!;

        var score = calls.First();
        Assert.Equal(("Score", "OK", problemId), (score.Step, score.Status, score.ProblemId));
        Assert.Equal(0.000195m, score.CostUsd); // 1200 × $0.10/M + 150 × $0.50/M
    }

    [Fact]
    public async Task A_failed_scoring_is_shown_and_retry_scores_again()
    {
        Ai.Score = _ => throw new AiCallException(AiFailureKind.ApiError, "500 from OpenAI");
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");

        var failed = await DrawerAsync(problemId);
        Assert.True(failed.ScoringFailed);
        Assert.Null(failed.Recommended);

        Ai.Score = _ => Ratings(fit: 4, pain: 3, retention: 2);
        var retry = await Client.PostAsync($"/api/admin/problems/{problemId}/retry-scoring", null);

        retry.EnsureSuccessStatusCode();
        var retried = (await retry.Content.ReadFromJsonAsync<ScoredDrawerJson>())!;
        Assert.False(retried.ScoringFailed);
        Assert.Equal((46, "Medium"), (retried.Recommended!.Score, retried.Recommended.Level));
        var reopened = await DrawerAsync(problemId);
        Assert.False(reopened.ScoringFailed);
        Assert.Equal(46, reopened.Recommended!.Score);
        Assert.Equal(["OK", "Failed"],
            (await Client.GetFromJsonAsync<AiCallJson[]>($"/api/admin/ai-calls?problemId={problemId}"))!
                .Where(c => c.Step == "Score").Select(c => c.Status));
    }

    [Fact]
    public async Task Output_breaking_the_rules_fails_the_scoring_and_keeps_the_last_score()
    {
        Ai.Score = _ => Ratings(fit: 4, pain: 3, retention: 2);
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        Ai.Score = _ => Ratings(fit: 4, pain: 3, retention: 2, goal: "World domination");

        await AddToProblemAsync(problemId, "Show spots open now");

        var drawer = await DrawerAsync(problemId);
        Assert.True(drawer.ScoringFailed);
        Assert.Equal(46, drawer.Recommended!.Score); // the last good ratings; 6 Reach points is still Reach 2
        var call = (await Client.GetFromJsonAsync<AiCallJson[]>($"/api/admin/ai-calls?problemId={problemId}&status=Failed"))!.Single();
        Assert.Equal(("Score", "Invalid output"), (call.Step, call.FailureKind));
    }

    [Fact]
    public async Task Retry_on_an_unknown_problem_is_not_found()
    {
        var response = await Client.PostAsync("/api/admin/problems/999/retry-scoring", null);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retry_is_only_for_a_failed_scoring()
    {
        Ai.Score = _ => Ratings(fit: 4, pain: 3, retention: 2);
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");

        var retry = await Client.PostAsync($"/api/admin/problems/{problemId}/retry-scoring", null);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Single(Ai.ScoreCalls);
    }

    [Fact]
    public async Task A_closed_problem_keeps_its_recommended_priority()
    {
        var (problemId, requestId) = await NewProblemAsync("Filter by opening hours");
        var before = (await DrawerAsync(problemId)).Recommended!;
        await SetProblemStatusAsync(requestId, "Won't do");

        await UpvoteAsync(requestId, 40);
        var retry = await Client.PostAsync($"/api/admin/problems/{problemId}/retry-scoring", null);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal(before.Score, (await DrawerAsync(problemId)).Recommended!.Score);
        Assert.Single(Ai.ScoreCalls);
    }

    private async Task UnUpvoteAsync(long requestId, int times)
    {
        for (var i = 0; i < times; i++)
        {
            var remove = new HttpRequestMessage(HttpMethod.Delete, $"/api/requests/{requestId}/upvote");
            remove.Headers.Add("X-Browser-Token", $"browser-token-{requestId:D4}-{i:D6}");
            (await Client.SendAsync(remove)).EnsureSuccessStatusCode();
        }
    }

    private static ScoreRatings Ratings(int fit, int pain, int retention, string? goal = FindSpots) => new(
        fit, $"fit reason {fit}", goal, pain, $"pain reason {pain}", retention, $"retention reason {retention}");

    private async Task<ScoredDrawerJson> DrawerAsync(long id) =>
        (await Client.GetFromJsonAsync<ScoredDrawerJson>($"/api/admin/problems/{id}"))!;
}

public sealed record ScoredDrawerJson(long Id, RecommendedJson? Recommended, bool ScoringFailed);

public sealed record RecommendedJson(string Level, int Score, CriterionScoreJson[] Breakdown);

public sealed record CriterionScoreJson(string Criterion, int Weight, int Rating, decimal Points, string Reason, string? Goal);
