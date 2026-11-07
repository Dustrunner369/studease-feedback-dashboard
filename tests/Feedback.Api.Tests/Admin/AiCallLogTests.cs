using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Ai;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class AiCallLogTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task The_log_lists_every_ai_call_newest_first_with_its_model_tokens_cost_and_request()
    {
        var request = await SubmitAsync("Dark mode please", "Other");
        await App.WhenIntakeIdleAsync();

        var calls = await CallsAsync();

        Assert.Equal(["Match", "Embed"], calls.Select(c => c.Step));
        var match = calls[0];
        Assert.Equal("OK", match.Status);
        Assert.Null(match.FailureKind);
        Assert.Equal(FakeAiGateway.ChatModel, match.Model);
        Assert.Equal(900, match.InputTokens);
        Assert.Equal(80, match.OutputTokens);
        Assert.Equal(0.00013m, match.CostUsd); // gpt-6-luna: 900 × $0.10/M + 80 × $0.50/M
        Assert.True(match.LatencyMs >= 0);
        Assert.Equal(request.Id, match.FeatureRequestId);
        Assert.True(match.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task The_log_can_be_filtered_to_failed_calls()
    {
        await SubmitAsync("Dark mode please", "Other");
        await App.WhenIntakeIdleAsync();
        Ai.Match = _ => throw new AiCallException(AiFailureKind.Timeout, "Timed out after 60 s");
        var failed = await SubmitAsync("Night theme", "Other");
        await App.WhenIntakeIdleAsync();

        var calls = await CallsAsync("?status=Failed");

        var call = Assert.Single(calls);
        Assert.Equal("Match", call.Step);
        Assert.Equal("Failed", call.Status);
        Assert.Equal("Timeout", call.FailureKind);
        Assert.Equal("Timed out after 60 s", call.ErrorMessage);
        Assert.Equal(failed.Id, call.FeatureRequestId);
        Assert.Equal(3, (await CallsAsync("?status=OK")).Length);
    }

    [Fact]
    public async Task The_per_problem_view_shows_the_calls_for_that_problems_requests()
    {
        var (problemId, first) = await NewProblemAsync("Filter by opening hours");
        var second = await AddToProblemAsync(problemId, "Show spots open now");
        await NewProblemAsync("Share spots with friends", "Social");

        var calls = await CallsAsync($"?problemId={problemId}");

        Assert.Equal(4, calls.Length);
        Assert.Equal([second, second, first, first], calls.Select(c => c.FeatureRequestId!.Value));
    }

    [Fact]
    public async Task Problem_and_status_filters_combine()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        Ai.Match = _ => throw new AiCallException(AiFailureKind.ApiError, "500 from OpenAI");
        await SubmitAsync("Dark mode please", "Other");
        await App.WhenIntakeIdleAsync();

        Assert.Empty(await CallsAsync($"?status=Failed&problemId={problemId}"));
        Assert.Single(await CallsAsync("?status=Failed"));
    }

    [Fact]
    public async Task An_unknown_status_filter_is_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/admin/ai-calls?status=Broken")).StatusCode);
    }

    private async Task<AiCallJson[]> CallsAsync(string query = "") =>
        (await Client.GetFromJsonAsync<AiCallJson[]>("/api/admin/ai-calls" + query))!;
}

public sealed record AiCallJson(
    long Id, string Step, string Status, string? FailureKind, string? ErrorMessage, string? Note,
    long? ProblemId, long? FeatureRequestId, string? Model, int? InputTokens, int? OutputTokens,
    decimal? CostUsd, int LatencyMs, DateTimeOffset CreatedAt);
