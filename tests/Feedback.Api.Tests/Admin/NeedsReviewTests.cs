using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Ai;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class NeedsReviewTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task A_request_whose_matching_failed_is_listed_with_why()
    {
        Ai.Match = _ => throw new AiCallException(AiFailureKind.Timeout, "Timed out after 60 s");
        var request = await SubmitAsync("Dark mode please", "Profile & account", "ana@example.com");
        await App.WhenIntakeIdleAsync();
        await UpvoteAsync(request.Id, 2);

        Assert.Equal(1, (await ProblemsAsync()).NeedsReview);
        var item = Assert.Single(await NeedsReviewAsync());
        Assert.Equal((request.Id, "Dark mode please", "ana@example.com", "Profile & account", 2),
            (item.Id, item.Title, item.Email, item.Category, item.Upvotes));
        Assert.Equal(("Match", "Timeout", "Timed out after 60 s"),
            (item.Failure!.Step, item.Failure.FailureKind, item.Failure.ErrorMessage));
    }

    [Fact]
    public async Task Retry_ai_matches_the_request_and_scores_its_problem()
    {
        var (problemId, _) = await NewProblemAsync("Dark mode");
        Ai.Embed = _ => throw new AiCallException(AiFailureKind.ApiError, "500 from OpenAI");
        var request = await SubmitAsync("Night theme", "Other");
        await App.WhenIntakeIdleAsync();
        Assert.Equal("Embed", Assert.Single(await NeedsReviewAsync()).Failure!.Step);

        Ai.Embed = _ => FakeAiGateway.Axis(0);
        Ai.Match = _ => FakeAiGateway.Existing(problemId);
        var scoreCalls = Ai.ScoreCalls.Count;
        var response = await Client.PostAsync($"/api/admin/feature-requests/{request.Id}/retry-match", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((request.Id, problemId), Unpack((await response.Content.ReadFromJsonAsync<RetryMatchJson>())!));
        Assert.Empty(await NeedsReviewAsync());
        Assert.Equal(0, (await ProblemsAsync()).NeedsReview);
        var drawer = (await Client.GetFromJsonAsync<MergedDrawerJson>($"/api/admin/problems/{problemId}"))!;
        Assert.Contains(drawer.FeatureRequests, r => r.Id == request.Id && r.MatchReason == "Same need as the shortlisted Problem.");
        Assert.Equal(scoreCalls + 1, Ai.ScoreCalls.Count);
    }

    [Fact]
    public async Task Retry_ai_that_fails_again_leaves_it_in_the_list_with_a_new_failure()
    {
        Ai.Match = _ => throw new AiCallException(AiFailureKind.Timeout, "Timed out after 60 s");
        var request = await SubmitAsync("Night theme", "Other");
        await App.WhenIntakeIdleAsync();

        Ai.Match = _ => throw new AiCallException(AiFailureKind.InvalidOutput, "Bad JSON");
        var response = await Client.PostAsync($"/api/admin/feature-requests/{request.Id}/retry-match", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<RetryMatchJson>())!.ProblemId);
        Assert.Equal("Bad JSON", Assert.Single(await NeedsReviewAsync()).Failure!.ErrorMessage);
    }

    [Fact]
    public async Task Match_by_hand_moves_the_request_to_a_problem_and_scores_it()
    {
        var (problemId, _) = await NewProblemAsync("Dark mode");
        Ai.Match = _ => throw new AiCallException(AiFailureKind.Timeout, "Timed out after 60 s");
        var request = await SubmitAsync("Night theme", "Other");
        await App.WhenIntakeIdleAsync();
        var scoreCalls = Ai.ScoreCalls.Count;

        var response = await Client.PostAsJsonAsync("/api/admin/feature-requests/move",
            new { featureRequestIds = new[] { request.Id }, toProblemId = problemId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await NeedsReviewAsync());
        var drawer = (await Client.GetFromJsonAsync<MergedDrawerJson>($"/api/admin/problems/{problemId}"))!;
        Assert.Equal(2, drawer.FeatureRequests.Length);
        var move = Assert.Single(drawer.Decisions);
        Assert.Equal("move", move.Kind);
        Assert.Empty(move.Details.GetProperty("from").EnumerateArray());
        Assert.Equal(scoreCalls + 1, Ai.ScoreCalls.Count);
    }

    [Fact]
    public async Task Retrying_a_matched_or_unknown_request_is_refused()
    {
        var (_, matched) = await NewProblemAsync("Dark mode");

        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/admin/feature-requests/{matched}/retry-match", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync("/api/admin/feature-requests/999/retry-match", null)).StatusCode);
    }

    private static (long, long?) Unpack(RetryMatchJson r) => (r.FeatureRequestId, r.ProblemId);

    private async Task<NeedsReviewJson[]> NeedsReviewAsync() =>
        (await Client.GetFromJsonAsync<NeedsReviewJson[]>("/api/admin/feature-requests/needs-review"))!;
}

public sealed record NeedsReviewJson(
    long Id, string Title, string Description, string Email, string Category, DateTimeOffset CreatedAt, int Upvotes,
    NeedsReviewFailureJson? Failure);

public sealed record NeedsReviewFailureJson(string Step, string? FailureKind, string? ErrorMessage, DateTimeOffset CreatedAt);

public sealed record RetryMatchJson(long FeatureRequestId, long? ProblemId);
