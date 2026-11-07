using System.Net.Http.Json;
using Feedback.Api.Ai;
using Feedback.Api.Tests.Infrastructure;
using Feedback.Api.Tests.Portal;

namespace Feedback.Api.Tests.Admin;

/// <summary>Base for /admin tests: signed in, with helpers that build Problems through the real intake.</summary>
public abstract class AdminTest(FeedbackDatabase database) : ApiTest(database)
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SignInAsync();
    }

    /// <summary>Submits a request that becomes a new Problem with this title and Category.</summary>
    protected async Task<(long ProblemId, long RequestId)> NewProblemAsync(
        string title, string category = "Finding spots", string email = "sam@example.com")
    {
        Ai.Match = input => new MatchDecision(null,
            new NewProblemDraft(title, $"About {title}.", category), false, $"Nothing on the shortlist is about {title}.");
        var request = await SubmitAsync($"Request for {title}", "Other", email);
        await App.WhenIntakeIdleAsync();
        Ai.Match = FakeAiGateway.NewProblemFromRequest;
        var problemId = (await ProblemsAsync()).Problems.Single(p => p.Title == title).Id;
        return (problemId, request.Id);
    }

    /// <summary>Submits a request the AI links to an existing Problem.</summary>
    protected async Task<long> AddToProblemAsync(long problemId, string title, string pick = "Other",
        string email = "kim@example.com", bool bundled = false, string reason = "Same need.")
    {
        Ai.Match = _ => new MatchDecision(problemId, null, bundled, reason);
        var request = await SubmitAsync(title, pick, email);
        await App.WhenIntakeIdleAsync();
        Ai.Match = FakeAiGateway.NewProblemFromRequest;
        return request.Id;
    }

    protected async Task<FeatureRequestJson> SubmitAsync(string title, string category, string email = "sam@example.com")
    {
        var response = await Client.PostAsJsonAsync("/api/requests",
            new { title, description = $"Description of {title}", category, email });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeatureRequestJson>())!;
    }

    protected async Task UpvoteAsync(long requestId, int times)
    {
        for (var i = 0; i < times; i++)
        {
            var upvote = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{requestId}/upvote");
            upvote.Headers.Add("X-Browser-Token", $"browser-token-{requestId:D4}-{i:D6}");
            (await Client.SendAsync(upvote)).EnsureSuccessStatusCode();
        }
    }

    protected async Task<ProblemsPageJson> ProblemsAsync(string? category = null) =>
        (await Client.GetFromJsonAsync<ProblemsPageJson>(
            "/api/admin/problems" + (category is null ? "" : "?category=" + Uri.EscapeDataString(category))))!;
}

public sealed record ProblemsPageJson(int NeedsReview, ProblemRowJson[] Problems);

public sealed record ProblemRowJson(
    long Id, string Title, string Category, string Status, int FeatureRequests, int Upvotes, string? Priority);
