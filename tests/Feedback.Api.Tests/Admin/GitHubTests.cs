using System.Net;
using System.Net.Http.Json;
using Feedback.Api.GitHub;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class GitHubTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task Settings_shows_the_configured_repo_under_its_current_name_with_the_token_ok()
    {
        var github = await Client.GetFromJsonAsync<GitHubStatusJson>("/api/admin/github");

        Assert.Equal(FakeGitHubGateway.ConfiguredRepo, github!.ConfiguredRepo);
        Assert.Equal(("Dustrunner369/studease", "main", true, null), (github.Repo, github.DefaultBranch, github.Ok, github.Error));
        Assert.NotNull(github.CheckedAt);
    }

    [Fact]
    public async Task Check_again_shows_a_failing_token_and_then_a_fixed_one()
    {
        GitHub.Failure = "GitHub said the token is invalid (401).";

        var failing = await CheckAsync();

        Assert.False(failing.Ok);
        Assert.Equal("GitHub said the token is invalid (401).", failing.Error);
        Assert.Equal(FakeGitHubGateway.ConfiguredRepo, failing.ConfiguredRepo);

        GitHub.Failure = null;
        var fixedToken = await CheckAsync();

        Assert.Equal(("Dustrunner369/studease", true, null), (fixedToken.Repo, fixedToken.Ok, fixedToken.Error));
    }

    [Fact]
    public async Task A_bad_token_at_startup_does_not_stop_the_app_and_settings_shows_it_failing()
    {
        await using var app = new FeedbackApp(Database);
        app.GitHub.Failure = "No GitHub token is set (GitHub:Token).";
        using var client = app.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/login", new { key = FeedbackApp.AdminKey })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/requests")).StatusCode);
        var github = await client.GetFromJsonAsync<GitHubStatusJson>("/api/admin/github");
        Assert.Equal((false, "No GitHub token is set (GitHub:Token).", null), (github!.Ok, github.Error, github.Repo));
    }

    [Fact]
    public async Task Create_github_issue_opens_an_issue_in_the_current_repo_and_links_it_to_the_problem()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours", email: "sam@example.com");
        await AddToProblemAsync(problemId, "Show only open cafés", email: "kim@example.com");

        var response = await Client.PostAsync($"/api/admin/problems/{problemId}/github-issue", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var issue = (await response.Content.ReadFromJsonAsync<GitHubDrawerJson>())!.GitHubIssue!;
        Assert.Equal(IssueStates.Open, issue.State);
        Assert.False(issue.MarkShipped);
        Assert.Equal($"https://github.com/Dustrunner369/studease/issues/{issue.Number}", issue.Url);
        Assert.Equal(issue, (await DrawerAsync(problemId)).GitHubIssue);

        Assert.Contains($"CreateIssue Dustrunner369/studease", GitHub.Calls);
        var created = GitHub.Issue(issue.Number);
        Assert.Equal("Filter by opening hours", created.Title);
        Assert.Contains("About Filter by opening hours.", created.Body);
        Assert.Contains("Show only open cafés", created.Body);
        Assert.DoesNotContain("@example.com", created.Body);
    }

    [Fact]
    public async Task A_problem_with_an_issue_cannot_get_a_second_one()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        (await Client.PostAsync($"/api/admin/problems/{problemId}/github-issue", null)).EnsureSuccessStatusCode();

        var again = await Client.PostAsync($"/api/admin/problems/{problemId}/github-issue", null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Single(GitHub.Calls, c => c.StartsWith("CreateIssue"));
    }

    [Fact]
    public async Task When_github_fails_no_issue_is_linked_and_the_reason_is_returned()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        GitHub.Failure = "The token isn't allowed to do this on Dustrunner369/studease (403). Check its permissions.";

        var response = await Client.PostAsync($"/api/admin/problems/{problemId}/github-issue", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("(403)", (await response.Content.ReadFromJsonAsync<ProblemDetailsJson>())!.Detail);
        Assert.Null((await DrawerAsync(problemId)).GitHubIssue);
    }

    [Fact]
    public async Task Create_github_issue_for_an_unknown_problem_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync("/api/admin/problems/999/github-issue", null)).StatusCode);
    }

    [Fact]
    public async Task Refresh_picks_up_a_closed_issue_and_the_drawer_asks_to_mark_the_problem_shipped()
    {
        var (closedOne, _) = await NewProblemAsync("Filter by opening hours");
        var (openOne, _) = await NewProblemAsync("Dark mode");
        var closedIssue = await CreateIssueAsync(closedOne);
        await CreateIssueAsync(openOne);
        GitHub.SetState(closedIssue, IssueStates.Closed);

        var refresh = await RefreshAsync();

        Assert.Equal((2, 0, null), (refresh.Checked, refresh.Failed, refresh.Error));
        var drawer = await DrawerAsync(closedOne);
        Assert.Equal((IssueStates.Closed, true), (drawer.GitHubIssue!.State, drawer.GitHubIssue.MarkShipped));
        Assert.Equal((IssueStates.Open, false), ((await DrawerAsync(openOne)).GitHubIssue!.State, (await DrawerAsync(openOne)).GitHubIssue!.MarkShipped));
    }

    [Fact]
    public async Task Marking_the_problem_shipped_ends_the_prompt_and_closed_problems_are_not_refreshed()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        GitHub.SetState(await CreateIssueAsync(problemId), IssueStates.Closed);
        await RefreshAsync();

        var shipped = await Client.PatchAsJsonAsync($"/api/admin/problems/{problemId}", new { status = "Shipped" });

        shipped.EnsureSuccessStatusCode();
        var drawer = (await shipped.Content.ReadFromJsonAsync<GitHubDrawerJson>())!;
        Assert.Equal(("Shipped", IssueStates.Closed, false), (drawer.Status, drawer.GitHubIssue!.State, drawer.GitHubIssue.MarkShipped));
        Assert.Equal(0, (await RefreshAsync()).Checked);
    }

    [Fact]
    public async Task A_reopened_issue_takes_the_prompt_away_again()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        var number = await CreateIssueAsync(problemId);
        GitHub.SetState(number, IssueStates.Closed);
        await RefreshAsync();

        GitHub.SetState(number, IssueStates.Open);
        await RefreshAsync();

        Assert.Equal((IssueStates.Open, false), ((await DrawerAsync(problemId)).GitHubIssue!.State, (await DrawerAsync(problemId)).GitHubIssue!.MarkShipped));
    }

    [Fact]
    public async Task When_github_fails_refresh_still_answers_and_keeps_the_last_known_states()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        var number = await CreateIssueAsync(problemId);
        GitHub.SetState(number, IssueStates.Closed);
        GitHub.Failure = "GitHub didn't answer in time.";

        var refresh = await RefreshAsync();

        Assert.Equal((0, 1, "GitHub didn't answer in time."), (refresh.Checked, refresh.Failed, refresh.Error));
        Assert.Equal(IssueStates.Open, (await DrawerAsync(problemId)).GitHubIssue!.State);
    }

    [Fact]
    public async Task Refresh_with_no_linked_issues_calls_nothing_on_github()
    {
        await NewProblemAsync("Filter by opening hours");
        var before = GitHub.Calls.Count;

        Assert.Equal((0, 0), ((await RefreshAsync()).Checked, (await RefreshAsync()).Failed));
        Assert.Equal(before, GitHub.Calls.Count);
    }

    private async Task<int> CreateIssueAsync(long problemId)
    {
        var response = await Client.PostAsync($"/api/admin/problems/{problemId}/github-issue", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GitHubDrawerJson>())!.GitHubIssue!.Number;
    }

    private async Task<RefreshJson> RefreshAsync()
    {
        var response = await Client.PostAsync("/api/admin/github/refresh", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RefreshJson>())!;
    }

    private async Task<GitHubDrawerJson> DrawerAsync(long problemId) =>
        (await Client.GetFromJsonAsync<GitHubDrawerJson>($"/api/admin/problems/{problemId}"))!;

    private async Task<GitHubStatusJson> CheckAsync()
    {
        var response = await Client.PostAsync("/api/admin/github/check", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GitHubStatusJson>())!;
    }
}

public sealed record GitHubDrawerJson(long Id, string Status, GitHubIssueJson? GitHubIssue);

public sealed record GitHubIssueJson(int Number, string Url, string State, bool MarkShipped);

public sealed record RefreshJson(int Checked, int Failed, string? Error);

public sealed record ProblemDetailsJson(string? Title, string? Detail);

public sealed record GitHubStatusJson(
    string ConfiguredRepo, string? Repo, string? DefaultBranch, bool Ok, string? Error, DateTimeOffset? CheckedAt);
