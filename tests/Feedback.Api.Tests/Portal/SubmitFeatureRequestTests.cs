using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Portal;

public sealed class SubmitFeatureRequestTests(FeedbackDatabase database) : ApiTest(database)
{
    [Fact]
    public async Task Submitted_request_can_be_opened_on_its_own_page()
    {
        var response = await Client.PostAsJsonAsync("/api/requests", new
        {
            title = "Filter spots by opening hours",
            description = "I keep walking to cafés that turn out to be closed.",
            category = "Finding spots",
            email = "sam@example.com",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<FeatureRequestJson>();
        Assert.NotNull(created);
        Assert.Equal($"/api/requests/{created.Id}", response.Headers.Location?.OriginalString);

        var opened = await Client.GetFromJsonAsync<FeatureRequestJson>($"/api/requests/{created.Id}");
        Assert.NotNull(opened);
        Assert.Equal("Filter spots by opening hours", opened.Title);
        Assert.Equal("I keep walking to cafés that turn out to be closed.", opened.Description);
        Assert.Equal("Finding spots", opened.Category);
        Assert.True(DateTimeOffset.UtcNow - opened.CreatedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Surrounding_whitespace_is_trimmed()
    {
        var response = await Client.PostAsJsonAsync("/api/requests",
            Valid with { Title = "  Dark mode  ", Email = " night@example.com " });

        var created = await response.Content.ReadFromJsonAsync<FeatureRequestJson>();
        Assert.Equal("Dark mode", created!.Title);
    }

    public static TheoryData<string, Submit> InvalidSubmits => new()
    {
        { "title", Valid with { Title = null } },
        { "title", Valid with { Title = "   " } },
        { "title", Valid with { Title = new string('a', 121) } },
        { "description", Valid with { Description = null } },
        { "description", Valid with { Description = "" } },
        { "description", Valid with { Description = new string('a', 2001) } },
        { "category", Valid with { Category = null } },
        { "category", Valid with { Category = "Dark mode" } },
        { "category", Valid with { Category = "finding spots" } },
        { "email", Valid with { Email = null } },
        { "email", Valid with { Email = "not-an-email" } },
        { "email", Valid with { Email = "a@b" } },
        { "email", Valid with { Email = "two words@example.com" } },
    };

    [Theory]
    [MemberData(nameof(InvalidSubmits))]
    public async Task Invalid_submit_is_rejected_with_the_failing_field_and_not_saved(string field, Submit submit)
    {
        var response = await Client.PostAsJsonAsync("/api/requests", submit);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemJson>();
        Assert.Equal([field], problem!.Errors.Keys);

        var board = await Client.GetFromJsonAsync<FeatureRequestJson[]>("/api/requests");
        Assert.Empty(board!);
    }

    [Fact]
    public async Task Unknown_request_is_not_found()
    {
        var response = await Client.GetAsync("/api/requests/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static readonly Submit Valid = new(
        "Filter spots by opening hours", "I keep walking to cafés that turn out to be closed.", "Finding spots", "sam@example.com");

    public sealed record Submit(string? Title, string? Description, string? Category, string? Email);

    private sealed record ValidationProblemJson(Dictionary<string, string[]> Errors);
}
