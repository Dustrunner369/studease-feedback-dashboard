using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class MergeTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task Merging_moves_every_request_and_upvote_to_the_kept_problem_with_the_picked_fields()
    {
        var (keptId, keptRequest) = await NewProblemAsync("Filter by opening hours", "Finding spots");
        var (otherId, otherRequest) = await NewProblemAsync("Show when spots are open", "Spot details");
        var secondOther = await AddToProblemAsync(otherId, "Opening times on the map");
        await UpvoteAsync(keptRequest, 4);
        await UpvoteAsync(otherRequest, 3);
        (await Client.PatchAsJsonAsync($"/api/admin/problems/{keptId}", new { status = "Planned" })).EnsureSuccessStatusCode();
        (await Client.PatchAsJsonAsync($"/api/admin/problems/{otherId}", new { priority = "Low" })).EnsureSuccessStatusCode();
        var scoreCalls = Ai.ScoreCalls.Count;

        var response = await MergeAsync(new
        {
            problemIds = new[] { keptId, otherId },
            keepId = keptId,
            categoryFrom = otherId,
            priorityFrom = otherId,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var drawer = (await response.Content.ReadFromJsonAsync<MergedDrawerJson>())!;
        Assert.Equal((keptId, "Filter by opening hours", "Spot details", "Planned", "Low"),
            (drawer.Id, drawer.Title, drawer.Category, drawer.Status, drawer.Priority));
        Assert.Equal([keptRequest, otherRequest, secondOther], drawer.FeatureRequests.Select(r => r.Id).Order());
        Assert.Equal(7, drawer.FeatureRequests.Sum(r => r.Upvotes));
        // Reach recounted: 3 Feature Requests × 3 + 7 Upvotes = 16 → 3; one new Score call.
        // 15 + 30 × 2/4 + 12.5 + 3.75 = 46.25
        Assert.Equal(46, drawer.Recommended!.Score);
        Assert.Equal(scoreCalls + 1, Ai.ScoreCalls.Count);

        var merge = drawer.Decisions.First();
        Assert.Equal("merge", merge.Kind);
        Assert.Equal(["Show when spots are open"],
            merge.Details.GetProperty("removed").EnumerateArray().Select(r => r.GetProperty("title").GetString()));

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/admin/problems/{otherId}")).StatusCode);
        var row = Assert.Single((await ProblemsAsync()).Problems);
        Assert.Equal((keptId, 3, 7), (row.Id, row.FeatureRequests, row.Upvotes));
    }

    [Fact]
    public async Task Merging_three_problems_keeps_one_and_removes_the_other_two()
    {
        var (a, _) = await NewProblemAsync("Filter by opening hours");
        var (b, _) = await NewProblemAsync("Open now filter");
        var (c, _) = await NewProblemAsync("Hide closed spots");

        var response = await MergeAsync(new { problemIds = new[] { a, b, c }, keepId = b, titleFrom = c });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single((await ProblemsAsync()).Problems);
        Assert.Equal((b, "Hide closed spots", 3), (row.Id, row.Title, row.FeatureRequests));
        var merge = (await response.Content.ReadFromJsonAsync<MergedDrawerJson>())!.Decisions.Single(d => d.Kind == "merge");
        Assert.Equal(["Filter by opening hours", "Hide closed spots"],
            merge.Details.GetProperty("removed").EnumerateArray().Select(r => r.GetProperty("title").GetString()).Order());
    }

    [Fact]
    public async Task The_kept_problem_takes_over_a_github_issue_when_it_has_none_and_the_merge_records_the_others()
    {
        var (kept, _) = await NewProblemAsync("Filter by opening hours");
        var (withIssue, _) = await NewProblemAsync("Open now filter");
        var (alsoWithIssue, _) = await NewProblemAsync("Hide closed spots");
        var first = await CreateIssueAsync(withIssue);
        var second = await CreateIssueAsync(alsoWithIssue);

        var response = await MergeAsync(new { problemIds = new[] { kept, withIssue, alsoWithIssue }, keepId = kept });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(first, (await Client.GetFromJsonAsync<GitHubDrawerJson>($"/api/admin/problems/{kept}"))!.GitHubIssue!.Number);
        var merge = (await response.Content.ReadFromJsonAsync<MergedDrawerJson>())!.Decisions.Single(d => d.Kind == "merge");
        Assert.Equal([first, second],
            merge.Details.GetProperty("gitHubIssues").EnumerateArray().Select(i => i.GetProperty("number").GetInt32()).Order());
        var toClose = Assert.Single(merge.Details.GetProperty("issuesToClose").EnumerateArray());
        Assert.Equal((second, alsoWithIssue), (toClose.GetProperty("number").GetInt32(), toClose.GetProperty("problemId").GetInt64()));
        Assert.EndsWith($"/issues/{second}", toClose.GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_kept_problem_with_its_own_issue_leaves_every_other_linked_issue_to_close_by_hand()
    {
        var (kept, _) = await NewProblemAsync("Filter by opening hours");
        var (other, _) = await NewProblemAsync("Open now filter");
        var (plain, _) = await NewProblemAsync("Hide closed spots");
        var own = await CreateIssueAsync(kept);
        var theirs = await CreateIssueAsync(other);

        var response = await MergeAsync(new { problemIds = new[] { other, plain, kept }, keepId = kept });

        var drawer = (await response.Content.ReadFromJsonAsync<GitHubDrawerJson>())!;
        Assert.Equal(own, drawer.GitHubIssue!.Number);
        var merge = (await Client.GetFromJsonAsync<MergedDrawerJson>($"/api/admin/problems/{kept}"))!.Decisions.Single(d => d.Kind == "merge");
        Assert.Equal([theirs], merge.Details.GetProperty("issuesToClose").EnumerateArray().Select(i => i.GetProperty("number").GetInt32()));
    }

    private async Task<int> CreateIssueAsync(long problemId)
    {
        var response = await Client.PostAsync($"/api/admin/problems/{problemId}/github-issue", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GitHubDrawerJson>())!.GitHubIssue!.Number;
    }

    [Fact]
    public async Task A_merge_needs_two_open_problems_and_a_kept_one_among_them()
    {
        var (a, aRequest) = await NewProblemAsync("Filter by opening hours");
        var (b, _) = await NewProblemAsync("Open now filter");
        var (shipped, shippedRequest) = await NewProblemAsync("Dark mode");
        await SetProblemStatusAsync(shippedRequest, "Shipped");

        Assert.Equal(HttpStatusCode.BadRequest, (await MergeAsync(new { problemIds = new[] { a }, keepId = a })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MergeAsync(new { problemIds = new[] { a, a }, keepId = a })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MergeAsync(new { problemIds = new[] { a, b }, keepId = 999 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MergeAsync(new { problemIds = new[] { a, b }, keepId = a, titleFrom = 999 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MergeAsync(new { problemIds = new[] { a, 999 }, keepId = a })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MergeAsync(new { problemIds = new[] { a, shipped }, keepId = a })).StatusCode);

        Assert.Equal(2, (await ProblemsAsync()).Problems.Length);
        Assert.Equal([aRequest], (await Client.GetFromJsonAsync<MergedDrawerJson>($"/api/admin/problems/{a}"))!
            .FeatureRequests.Select(r => r.Id));
    }

    [Fact]
    public async Task Merging_needs_the_admin_cookie()
    {
        await Client.PostAsync("/api/admin/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await MergeAsync(new { problemIds = new[] { 1, 2 }, keepId = 1 })).StatusCode);
    }

    private Task<HttpResponseMessage> MergeAsync(object body) => Client.PostAsJsonAsync("/api/admin/problems/merge", body);
}

public sealed record MergedDrawerJson(
    long Id, string Title, string Category, string Status, string? Priority, RecommendedJson? Recommended,
    bool ScoringFailed, DrawerRequestJson[] FeatureRequests, DecisionJson[] Decisions);
