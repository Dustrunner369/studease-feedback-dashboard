using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Ai;
using Feedback.Api.Tests.Infrastructure;
using Feedback.Api.Tests.Portal;

namespace Feedback.Api.Tests.Intake;

public sealed class MatchingTests(FeedbackDatabase database) : ApiTest(database)
{
    [Fact]
    public async Task A_request_matched_to_an_existing_problem_lists_the_others_as_related_requests()
    {
        var first = await SubmitAsync("Filter spots by opening hours", "Finding spots");
        await App.WhenIntakeIdleAsync();
        Ai.Match = input => FakeAiGateway.Existing(input.Candidates[0].ProblemId);

        var second = await SubmitAsync("Show only spots open now", "Other");
        await App.WhenIntakeIdleAsync();

        var page = await GetPageAsync(second.Id);
        var related = Assert.Single(page.Related);
        Assert.Equal(first.Id, related.Id);
        Assert.Equal("Filter spots by opening hours", related.Title);
        Assert.Equal(second.Id, Assert.Single((await GetPageAsync(first.Id)).Related).Id);
    }

    [Fact]
    public async Task A_matched_request_is_listed_under_its_problems_category_not_the_requesters_pick()
    {
        await SubmitAsync("Filter spots by opening hours", "Finding spots");
        await App.WhenIntakeIdleAsync();
        Ai.Match = input => FakeAiGateway.Existing(input.Candidates[0].ProblemId);

        var second = await SubmitAsync("Show only spots open now", "Other");
        await App.WhenIntakeIdleAsync();

        Assert.Contains(second.Id, (await BoardAsync("Finding spots")).Select(r => r.Id));
        Assert.DoesNotContain(second.Id, (await BoardAsync("Other")).Select(r => r.Id));
        Assert.Equal("Finding spots", (await GetPageAsync(second.Id)).Category);
    }

    [Fact]
    public async Task A_new_problem_takes_the_ai_category_and_has_no_related_requests()
    {
        Ai.Match = input => new MatchDecision(null,
            new NewProblemDraft("Share study spots with friends", "Students want to send spots to friends.", "Social"),
            false, "No shortlisted Problem is about sharing.");

        var request = await SubmitAsync("Send a spot to my mate", "Other");
        await App.WhenIntakeIdleAsync();

        Assert.Equal([request.Id], (await BoardAsync("Social")).Select(r => r.Id));
        Assert.Empty(await BoardAsync("Other"));
        Assert.Empty((await GetPageAsync(request.Id)).Related);
    }

    [Fact]
    public async Task The_shortlist_is_the_five_closest_problems_by_their_best_matching_request()
    {
        // The new request sits on axis 0; each other request leans towards it by a different amount.
        var vectors = new Dictionary<string, float[]>
        {
            ["F far"] = FakeAiGateway.Axis(16, 0, 0.3f),
            ["A"] = FakeAiGateway.Axis(11, 0, 4f),
            ["B"] = FakeAiGateway.Axis(12, 0, 2f),
            ["C"] = FakeAiGateway.Axis(13, 0, 1.5f),
            ["D"] = FakeAiGateway.Axis(14, 0, 1f),
            ["F close"] = FakeAiGateway.Axis(0, 30, 0.1f),
            ["E"] = FakeAiGateway.Axis(15, 0, 0.7f),
            ["New request"] = FakeAiGateway.Axis(0),
        };
        Ai.Embed = text => vectors[text[..text.IndexOf('\n')]];

        await SubmitAndWaitAsync("F far");
        await SubmitAndWaitAsync("A");
        var problemF = Assert.Single(Ai.MatchCalls.Last().Candidates).ProblemId;
        foreach (var title in new[] { "B", "C", "D" }) await SubmitAndWaitAsync(title);
        Ai.Match = _ => FakeAiGateway.Existing(problemF);
        await SubmitAndWaitAsync("F close");
        Ai.Match = FakeAiGateway.NewProblemFromRequest;
        await SubmitAndWaitAsync("E");

        await SubmitAndWaitAsync("New request");

        var shortlist = Ai.MatchCalls.Last().Candidates;
        Assert.Equal(["F close", "A", "B", "C", "D"], shortlist.Select(c => c.ClosestRequestTitle));
        Assert.Equal(problemF, shortlist[0].ProblemId);
        Assert.Equal("Need: F far", shortlist[0].Title);
        Assert.True(shortlist[0].Distance < shortlist[1].Distance);
    }

    [Fact]
    public async Task Invalid_ai_output_leaves_the_request_unmatched_and_the_requester_sees_nothing_different()
    {
        Ai.Match = _ => new MatchDecision(987654, null, false, "Points at a Problem that was never shortlisted.");

        var response = await Client.PostAsJsonAsync("/api/requests", Body("Dark mode please", "Other"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var request = (await response.Content.ReadFromJsonAsync<FeatureRequestJson>())!;
        await App.WhenIntakeIdleAsync();

        var page = await GetPageAsync(request.Id);
        Assert.Empty(page.Related);
        Assert.Equal("Other", page.Category);
        Assert.Equal([request.Id], (await BoardAsync("Other")).Select(r => r.Id));
        Assert.Single(Ai.MatchCalls); // not retried
    }

    [Fact]
    public async Task An_ai_api_failure_leaves_the_request_unmatched_and_the_next_request_still_matches()
    {
        Ai.Match = _ => throw new AiCallException(AiFailureKind.ApiError, "500 from OpenAI");
        var failed = await SubmitAsync("Dark mode please", "Other");
        await App.WhenIntakeIdleAsync();
        Ai.Match = FakeAiGateway.NewProblemFromRequest;

        var next = await SubmitAsync("Night theme", "Other");
        await App.WhenIntakeIdleAsync();

        Assert.Empty((await GetPageAsync(failed.Id)).Related);
        // The failed request has no Problem, so it never appears on the shortlist.
        Assert.Empty(Ai.MatchCalls.Last().Candidates);
        Assert.Equal(next.Id, (await GetPageAsync(next.Id)).Id);
    }

    [Fact]
    public async Task An_embedding_failure_skips_matching()
    {
        Ai.Embed = _ => throw new AiCallException(AiFailureKind.Timeout, "Timed out");

        var request = await SubmitAsync("Dark mode please", "Other");
        await App.WhenIntakeIdleAsync();

        Assert.Empty(Ai.MatchCalls);
        Assert.Equal([request.Id], (await BoardAsync("Other")).Select(r => r.Id));
    }

    [Fact]
    public async Task Related_requests_never_include_emails()
    {
        await SubmitAsync("Filter spots by opening hours", "Finding spots");
        await App.WhenIntakeIdleAsync();
        Ai.Match = input => FakeAiGateway.Existing(input.Candidates[0].ProblemId);
        var second = await SubmitAsync("Show only spots open now", "Other");
        await App.WhenIntakeIdleAsync();

        var json = await Client.GetStringAsync($"/api/requests/{second.Id}");

        Assert.Contains("Filter spots by opening hours", json);
        Assert.DoesNotContain("example.com", json);
    }

    private static object Body(string title, string category) => new
    {
        title,
        description = $"Description of {title}",
        category,
        email = "sam@example.com",
    };

    private async Task<FeatureRequestJson> SubmitAsync(string title, string category)
    {
        var response = await Client.PostAsJsonAsync("/api/requests", Body(title, category));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeatureRequestJson>())!;
    }

    private async Task SubmitAndWaitAsync(string title)
    {
        await SubmitAsync(title, "Other");
        await App.WhenIntakeIdleAsync();
    }

    private async Task<FeatureRequestPageJson> GetPageAsync(long id) =>
        (await Client.GetFromJsonAsync<FeatureRequestPageJson>($"/api/requests/{id}"))!;

    private async Task<FeatureRequestJson[]> BoardAsync(string category) =>
        (await Client.GetFromJsonAsync<FeatureRequestJson[]>(
            "/api/requests?sort=new&category=" + Uri.EscapeDataString(category)))!;
}

/// <summary>A Feature Request's own page: the request plus its Related requests.</summary>
public sealed record FeatureRequestPageJson(
    long Id, string Title, string Description, string Category, DateTimeOffset CreatedAt, FeatureRequestJson[] Related);
