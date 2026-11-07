using System.Net;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

/// <summary>The /admin page is checked by hand; this only checks it is served (sign-in happens on the page).</summary>
public sealed class AdminPagesTests(FeedbackDatabase database) : ApiTest(database)
{
    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/")]
    public async Task Admin_page_is_served_as_html(string url)
    {
        var response = await Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("data-page=\"admin\"", await response.Content.ReadAsStringAsync());
    }
}
