using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Portal;

public sealed class BoardTests(FeedbackDatabase database) : ApiTest(database)
{
    [Fact]
    public async Task Newest_sort_lists_the_latest_request_first()
    {
        await SubmitAsync("Dark mode please", "Other");
        await SubmitAsync("Filter spots by opening hours", "Finding spots");
        await SubmitAsync("Share a spot as a link", "Social");

        var board = await Client.GetFromJsonAsync<FeatureRequestJson[]>("/api/requests?category=&sort=new");

        Assert.Equal(
            ["Share a spot as a link", "Filter spots by opening hours", "Dark mode please"],
            board!.Select(r => r.Title));
    }

    [Fact]
    public async Task Board_rows_carry_title_description_category_and_date()
    {
        await SubmitAsync("Dark mode please", "Other");

        var row = Assert.Single((await Client.GetFromJsonAsync<FeatureRequestJson[]>("/api/requests?sort=new"))!);

        Assert.Equal("Dark mode please", row.Title);
        Assert.Equal("Description of Dark mode please", row.Description);
        Assert.Equal("Other", row.Category);
        Assert.True(DateTimeOffset.UtcNow - row.CreatedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Board_never_shows_the_requesters_email()
    {
        await SubmitAsync("Dark mode please", "Other");

        var json = await Client.GetStringAsync("/api/requests?sort=new");

        Assert.DoesNotContain("example.com", json);
    }

    [Fact]
    public async Task Category_tab_shows_only_requests_in_that_category()
    {
        await SubmitAsync("Filter spots by opening hours", "Finding spots");
        await SubmitAsync("Dark mode please", "Other");
        await SubmitAsync("Show open-now spots", "Finding spots");

        var board = await Client.GetFromJsonAsync<FeatureRequestJson[]>(
            "/api/requests?category=" + Uri.EscapeDataString("Finding spots") + "&sort=new");

        Assert.Equal(["Show open-now spots", "Filter spots by opening hours"], board!.Select(r => r.Title));
    }

    [Theory]
    [InlineData("/api/requests?category=Nonsense")]
    [InlineData("/api/requests?sort=oldest")]
    public async Task Unknown_category_or_sort_is_rejected(string url)
    {
        var response = await Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task SubmitAsync(string title, string category)
    {
        var response = await Client.PostAsJsonAsync("/api/requests", new
        {
            title,
            description = $"Description of {title}",
            category,
            email = "sam@example.com",
        });
        response.EnsureSuccessStatusCode();
    }
}
