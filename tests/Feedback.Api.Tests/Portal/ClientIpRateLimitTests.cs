using System.Net;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Portal;

/// <summary>Behind Fly's proxy every request arrives from the proxy; the client IP is in a header Fly sets.</summary>
public sealed class ClientIpRateLimitTests(FeedbackDatabase database) : ApiTest(database)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Portal:RateLimit:PermitLimit"] = "2",
        ["Portal:RateLimit:WindowSeconds"] = "60",
        ["Portal:RateLimit:ClientIpHeader"] = "Fly-Client-IP",
    };

    [Fact]
    public async Task Each_client_ip_in_the_configured_header_gets_its_own_limit()
    {
        var statuses = new List<(string Ip, HttpStatusCode Status)>();
        foreach (var ip in new[] { "203.0.113.7", "203.0.113.7", "203.0.113.7", "198.51.100.4", "198.51.100.4" })
            statuses.Add((ip, (await BoardFromAsync(ip)).StatusCode));

        Assert.Equal(
            [
                ("203.0.113.7", HttpStatusCode.OK), ("203.0.113.7", HttpStatusCode.OK), ("203.0.113.7", HttpStatusCode.TooManyRequests),
                ("198.51.100.4", HttpStatusCode.OK), ("198.51.100.4", HttpStatusCode.OK),
            ],
            statuses);
    }

    [Fact]
    public async Task A_missing_or_garbled_header_falls_back_to_the_connection_ip()
    {
        var statuses = new List<HttpStatusCode>
        {
            (await BoardFromAsync(null)).StatusCode,
            (await BoardFromAsync("not an ip")).StatusCode,
            (await BoardFromAsync(null)).StatusCode,
        };

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    private Task<HttpResponseMessage> BoardFromAsync(string? ip)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, "/api/requests");
        if (ip is not null) message.Headers.Add("Fly-Client-IP", ip);
        return Client.SendAsync(message);
    }
}
