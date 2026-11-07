using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Portal;

public sealed class RateLimitTests(FeedbackDatabase database) : ApiTest(database)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Portal:RateLimit:PermitLimit"] = "3",
        ["Portal:RateLimit:WindowSeconds"] = "60",
    };

    [Fact]
    public async Task Submitting_too_often_from_one_IP_is_refused()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            var response = await Client.PostAsJsonAsync("/api/requests", new
            {
                title = $"Request {i}",
                description = "Something I need.",
                category = "Other",
                email = "sam@example.com",
            });
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests],
            statuses);
    }

    [Fact]
    public async Task Upvoting_too_often_from_one_IP_is_refused()
    {
        var created = await Client.PostAsJsonAsync("/api/requests", new
        {
            title = "Dark mode please",
            description = "Something I need.",
            category = "Other",
            email = "sam@example.com",
        });
        var id = (await created.Content.ReadFromJsonAsync<FeatureRequestJson>())!.Id;

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            // A fresh token each time, as a vote-inflater clearing their browser would.
            var message = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/upvote");
            message.Headers.Add("X-Browser-Token", $"inflater-token-{i:D10}");
            statuses.Add((await Client.SendAsync(message)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }
}
