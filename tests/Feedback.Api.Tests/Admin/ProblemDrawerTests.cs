using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class ProblemDrawerTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task The_drawer_shows_the_problem_and_why_each_request_is_there()
    {
        var (problemId, first) = await NewProblemAsync("Filter by opening hours", "Finding spots", "sam@example.com");
        var second = await AddToProblemAsync(problemId, "Open-now filter and dark mode", pick: "Spot details",
            email: "kim@example.com", bundled: true, reason: "Main need is filtering by opening hours.");
        await UpvoteAsync(second, 3);

        var drawer = await DrawerAsync(problemId);

        Assert.Equal(problemId, drawer.Id);
        Assert.Equal("Filter by opening hours", drawer.Title);
        Assert.Equal("About Filter by opening hours.", drawer.Description);
        Assert.Equal("Finding spots", drawer.Category);
        Assert.Equal("New", drawer.Status);
        Assert.Null(drawer.Priority);

        Assert.Equal([second, first], drawer.FeatureRequests.Select(r => r.Id)); // newest first
        var bundled = drawer.FeatureRequests[0];
        Assert.Equal("Open-now filter and dark mode", bundled.Title);
        Assert.Equal("Description of Open-now filter and dark mode", bundled.Description);
        Assert.Equal("kim@example.com", bundled.Email);
        Assert.Equal("Spot details", bundled.Category);
        Assert.Equal(3, bundled.Upvotes);
        Assert.Equal("Main need is filtering by opening hours.", bundled.MatchReason);
        Assert.True(bundled.IsBundled);
        Assert.True(bundled.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));

        var original = drawer.FeatureRequests[1];
        Assert.Equal("sam@example.com", original.Email);
        Assert.Equal("Other", original.Category);
        Assert.Equal(0, original.Upvotes);
        Assert.Equal("Nothing on the shortlist is about Filter by opening hours.", original.MatchReason);
        Assert.False(original.IsBundled);
    }

    [Fact]
    public async Task An_unknown_problem_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/admin/problems/999")).StatusCode);
    }

    private async Task<ProblemDrawerJson> DrawerAsync(long id) =>
        (await Client.GetFromJsonAsync<ProblemDrawerJson>($"/api/admin/problems/{id}"))!;
}

public sealed record ProblemDrawerJson(
    long Id, string Title, string Description, string Category, string Status, string? Priority,
    DrawerRequestJson[] FeatureRequests);

public sealed record DrawerRequestJson(
    long Id, string Title, string Description, string Email, string Category, DateTimeOffset CreatedAt,
    int Upvotes, string? MatchReason, bool IsBundled);
