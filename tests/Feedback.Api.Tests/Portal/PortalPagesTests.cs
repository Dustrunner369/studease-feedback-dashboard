using System.Net;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Portal;

/// <summary>The pages themselves are checked by hand; this only checks they are served.</summary>
public sealed class PortalPagesTests(FeedbackDatabase database) : ApiTest(database)
{
    [Theory]
    [InlineData("/", "board")]
    [InlineData("/requests/42", "feature-request")]
    public async Task Portal_page_is_served_as_html(string url, string page)
    {
        var response = await Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains($"data-page=\"{page}\"", await response.Content.ReadAsStringAsync());
    }
}
