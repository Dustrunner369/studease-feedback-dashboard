using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class AdminAuthTests(FeedbackDatabase database) : ApiTest(database)
{
    public static TheoryData<string> AdminReads => new()
    {
        "/api/admin/problems",
        "/api/admin/problems/1",
        "/api/admin/ai-calls",
    };

    [Theory]
    [MemberData(nameof(AdminReads))]
    public async Task Admin_endpoints_reject_a_caller_who_has_not_signed_in(string url)
    {
        var response = await Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_right_key_signs_in_with_an_http_only_cookie()
    {
        var response = await Client.PostAsJsonAsync("/api/admin/login", new { key = FeedbackApp.AdminKey });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FeedbackApp.AdminKey, cookie);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/api/admin/problems")).StatusCode);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("")]
    public async Task A_wrong_key_does_not_sign_in(string key)
    {
        var response = await Client.PostAsJsonAsync("/api/admin/login", new { key });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/admin/problems")).StatusCode);
    }

    [Fact]
    public async Task A_made_up_cookie_is_rejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/problems");
        request.Headers.Add("Cookie", $"studease_admin={FeedbackApp.AdminKey}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Logging_out_ends_the_session()
    {
        await SignInAsync();

        var response = await Client.PostAsync("/api/admin/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/admin/problems")).StatusCode);
    }
}

/// <summary>With no admin key configured, nobody can sign in.</summary>
public sealed class AdminAuthWithoutKeyTests(FeedbackDatabase database) : ApiTest(database)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Admin:Key"] = "" };

    [Fact]
    public async Task Sign_in_is_refused_when_no_admin_key_is_configured()
    {
        var response = await Client.PostAsJsonAsync("/api/admin/login", new { key = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
