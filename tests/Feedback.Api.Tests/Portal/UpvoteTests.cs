using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Portal;

public sealed class UpvoteTests(FeedbackDatabase database) : ApiTest(database)
{
    private const string Alice = "browser-token-alice-0001";
    private const string Bob = "browser-token-bob-000002";

    [Fact]
    public async Task Upvoting_twice_from_one_browser_counts_once()
    {
        var id = await SubmitAsync("Dark mode please");

        (await UpvoteAsync(id, Alice)).EnsureSuccessStatusCode();
        (await UpvoteAsync(id, Alice)).EnsureSuccessStatusCode();

        var request = await GetAsync(id, Alice);
        Assert.Equal(1, request.Upvotes);
        Assert.True(request.Upvoted);
    }

    [Fact]
    public async Task Taking_an_upvote_back_removes_it()
    {
        var id = await SubmitAsync("Dark mode please");
        await UpvoteAsync(id, Alice);
        await UpvoteAsync(id, Bob);

        var response = await UnUpvoteAsync(id, Alice);

        response.EnsureSuccessStatusCode();
        var request = await GetAsync(id, Alice);
        Assert.Equal(1, request.Upvotes);
        Assert.False(request.Upvoted);
    }

    [Fact]
    public async Task One_browser_cannot_remove_another_browsers_upvote()
    {
        var id = await SubmitAsync("Dark mode please");
        await UpvoteAsync(id, Alice);

        (await UnUpvoteAsync(id, Bob)).EnsureSuccessStatusCode();

        var request = await GetAsync(id, Alice);
        Assert.Equal(1, request.Upvotes);
        Assert.True(request.Upvoted);
    }

    [Fact]
    public async Task Upvote_responds_with_the_new_count_and_state()
    {
        var id = await SubmitAsync("Dark mode please");
        await UpvoteAsync(id, Bob);

        var state = await (await UpvoteAsync(id, Alice)).Content.ReadFromJsonAsync<UpvoteStateJson>();

        Assert.Equal(new UpvoteStateJson(2, true), state);
    }

    [Fact]
    public async Task Board_shows_upvoted_only_to_the_browser_that_upvoted()
    {
        var id = await SubmitAsync("Dark mode please");
        await UpvoteAsync(id, Alice);

        var forAlice = Assert.Single(await BoardAsync("new", Alice));
        var forBob = Assert.Single(await BoardAsync("new", Bob));
        var forNoToken = Assert.Single(await BoardAsync("new", null));

        Assert.Equal((1, true), (forAlice.Upvotes, forAlice.Upvoted));
        Assert.Equal((1, false), (forBob.Upvotes, forBob.Upvoted));
        Assert.Equal((1, false), (forNoToken.Upvotes, forNoToken.Upvoted));
    }

    [Fact]
    public async Task Most_upvoted_sort_puts_the_most_upvoted_first_and_breaks_ties_by_newest()
    {
        var dark = await SubmitAsync("Dark mode please");
        var hours = await SubmitAsync("Filter spots by opening hours");
        await SubmitAsync("Share a spot as a link");
        await SubmitAsync("Sign in with Apple");
        await UpvoteAsync(hours, Alice);
        await UpvoteAsync(hours, Bob);
        await UpvoteAsync(dark, Alice);

        var board = await BoardAsync("top", null);

        Assert.Equal(
            ["Filter spots by opening hours", "Dark mode please", "Sign in with Apple", "Share a spot as a link"],
            board.Select(r => r.Title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("has spaces in it, not allowed")]
    public async Task Upvote_without_a_valid_browser_token_is_rejected(string? token)
    {
        var id = await SubmitAsync("Dark mode please");

        Assert.Equal(HttpStatusCode.BadRequest, (await UpvoteAsync(id, token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UnUpvoteAsync(id, token)).StatusCode);
        Assert.Equal(0, (await GetAsync(id, null)).Upvotes);
    }

    [Fact]
    public async Task Upvoting_a_request_that_does_not_exist_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await UpvoteAsync(999, Alice)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UnUpvoteAsync(999, Alice)).StatusCode);
    }

    private sealed record UpvoteStateJson(int Upvotes, bool Upvoted);

    private async Task<long> SubmitAsync(string title)
    {
        var response = await Client.PostAsJsonAsync("/api/requests", new
        {
            title,
            description = $"Description of {title}",
            category = "Other",
            email = "sam@example.com",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeatureRequestJson>())!.Id;
    }

    private Task<HttpResponseMessage> UpvoteAsync(long id, string? token) =>
        SendAsync(HttpMethod.Post, $"/api/requests/{id}/upvote", token);

    private Task<HttpResponseMessage> UnUpvoteAsync(long id, string? token) =>
        SendAsync(HttpMethod.Delete, $"/api/requests/{id}/upvote", token);

    private async Task<FeatureRequestJson> GetAsync(long id, string? token)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/requests/{id}", token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeatureRequestJson>())!;
    }

    private async Task<FeatureRequestJson[]> BoardAsync(string sort, string? token)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/requests?sort={sort}", token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeatureRequestJson[]>())!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token)
    {
        var message = new HttpRequestMessage(method, url);
        if (token is not null) message.Headers.Add("X-Browser-Token", token);
        return Client.SendAsync(message);
    }
}
