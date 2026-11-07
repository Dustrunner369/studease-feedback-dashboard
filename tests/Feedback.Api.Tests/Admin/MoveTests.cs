using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Tests.Infrastructure;
using Feedback.Api.Tests.Intake;

namespace Feedback.Api.Tests.Admin;

public sealed class MoveTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task Moving_some_requests_to_a_new_problem_splits_it_and_rescores_both()
    {
        var (sourceId, first) = await NewProblemAsync("Opening hours and dark mode");
        var second = await AddToProblemAsync(sourceId, "Dark mode please");
        var third = await AddToProblemAsync(sourceId, "Night theme");
        await UpvoteAsync(second, 2);
        var scoreCalls = Ai.ScoreCalls.Count;

        var response = await MoveAsync(new
        {
            featureRequestIds = new[] { second, third },
            newProblem = new { title = "Dark mode", category = "Profile & account" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<MoveResultJson>())!;
        Assert.Empty(result.RemovedProblemIds);
        var created = await DrawerAsync(result.ProblemId);
        Assert.Equal(("Dark mode", "Profile & account", "New"), (created.Title, created.Category, created.Status));
        Assert.Equal([second, third], created.FeatureRequests.Select(r => r.Id).Order());
        Assert.Equal(2, created.FeatureRequests.Sum(r => r.Upvotes));
        Assert.All(created.FeatureRequests, r => Assert.Equal("Moved here by the product team.", r.MatchReason));
        Assert.Equal("move", Assert.Single(created.Decisions).Kind);

        var source = await DrawerAsync(sourceId);
        Assert.Equal([first], source.FeatureRequests.Select(r => r.Id));
        Assert.Equal("move", Assert.Single(source.Decisions).Kind);
        // Both re-scored: the source now has Reach 2 again (3 points) → 39; the new one 2 × 3 + 2 = 8 → Reach 2 → 39.
        Assert.Equal(scoreCalls + 2, Ai.ScoreCalls.Count);
        Assert.Equal((39, 39), (source.Recommended!.Score, created.Recommended!.Score));
        Assert.Equal(2, (await ProblemsAsync()).Problems.Length);

        // Requesters see the new grouping as Related requests.
        var page = (await Client.GetFromJsonAsync<FeatureRequestPageJson>($"/api/requests/{second}"))!;
        Assert.Equal([third], page.Related.Select(r => r.Id));
    }

    [Fact]
    public async Task Moving_every_request_to_an_existing_problem_deletes_the_emptied_one()
    {
        var (keptId, keptRequest) = await NewProblemAsync("Filter by opening hours");
        var (emptiedId, moved) = await NewProblemAsync("Show when spots are open");
        await UpvoteAsync(moved, 4);
        var scoreCalls = Ai.ScoreCalls.Count;

        var response = await MoveAsync(new { featureRequestIds = new[] { moved }, toProblemId = keptId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<MoveResultJson>())!;
        Assert.Equal(keptId, result.ProblemId);
        Assert.Equal([emptiedId], result.RemovedProblemIds);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/admin/problems/{emptiedId}")).StatusCode);
        var row = Assert.Single((await ProblemsAsync()).Problems);
        Assert.Equal((keptId, 2, 4), (row.Id, row.FeatureRequests, row.Upvotes));
        var kept = await DrawerAsync(keptId);
        Assert.Equal([keptRequest, moved], kept.FeatureRequests.Select(r => r.Id).Order());
        var move = Assert.Single(kept.Decisions);
        Assert.Equal(["Show when spots are open"],
            move.Details.GetProperty("from").EnumerateArray().Select(p => p.GetProperty("title").GetString()));
        Assert.Equal(scoreCalls + 1, Ai.ScoreCalls.Count); // only the surviving Problem
    }

    [Fact]
    public async Task A_move_needs_requests_and_exactly_one_open_destination()
    {
        var (sourceId, request) = await NewProblemAsync("Filter by opening hours");
        var (shippedId, shippedRequest) = await NewProblemAsync("Dark mode");
        await SetProblemStatusAsync(shippedRequest, "Shipped");

        Assert.Equal(HttpStatusCode.BadRequest, (await MoveAsync(new { featureRequestIds = Array.Empty<long>(), toProblemId = sourceId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MoveAsync(new { featureRequestIds = new[] { request } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MoveAsync(new
        {
            featureRequestIds = new[] { request }, toProblemId = sourceId, newProblem = new { title = "X", category = "Other" },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MoveAsync(new { featureRequestIds = new[] { request }, toProblemId = shippedId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MoveAsync(new { featureRequestIds = new[] { request }, toProblemId = 999 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MoveAsync(new { featureRequestIds = new[] { 999L }, toProblemId = sourceId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MoveAsync(new
        {
            featureRequestIds = new[] { request }, newProblem = new { title = " ", category = "Other" },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MoveAsync(new
        {
            featureRequestIds = new[] { request }, newProblem = new { title = "Hours", category = "Billing" },
        })).StatusCode);

        Assert.Equal([request], (await DrawerAsync(sourceId)).FeatureRequests.Select(r => r.Id));
        Assert.Empty((await DrawerAsync(sourceId)).Decisions);
    }

    private Task<HttpResponseMessage> MoveAsync(object body) => Client.PostAsJsonAsync("/api/admin/feature-requests/move", body);

    private async Task<MergedDrawerJson> DrawerAsync(long id) =>
        (await Client.GetFromJsonAsync<MergedDrawerJson>($"/api/admin/problems/{id}"))!;
}

public sealed record MoveResultJson(long ProblemId, long[] RemovedProblemIds);
