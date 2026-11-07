using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Ai;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class DecisionBriefTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task Generating_a_brief_saves_cited_claims_and_fills_who_is_asking_by_code()
    {
        var (problemId, first) = await NewProblemAsync("Opening hours", email: "sam@example.com");
        var second = await AddToProblemAsync(problemId, "Show closing times", pick: "Spot details", email: "kim@example.com");
        await UpvoteAsync(second, 3);
        Ai.Brief = input =>
        {
            var ids = input.FeatureRequests.Select(r => r.Id).Order().ToArray();
            return new BriefForm(
                Need: [new("Students can't tell when spots are open.", ids, null)],
                AskingFor: [new("Closing times on each spot.", [second], "Description of Show closing times")],
                PainAndImpact: [new("They travel to closed spots.", [first], null)],
                OpenQuestions: []);
        };

        var response = await Client.PostAsync($"/api/admin/problems/{problemId}/brief", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var input = Assert.Single(Ai.BriefCalls);
        Assert.Equal(("Opening hours", "About Opening hours.", "Finding spots"),
            (input.Problem.Title, input.Problem.Description, input.Problem.Category));
        var sent = input.FeatureRequests.Single(r => r.Id == second);
        Assert.Equal(("Show closing times", "Description of Show closing times", "Spot details", 3),
            (sent.Title, sent.Description, sent.Category, sent.Upvotes));
        Assert.Equal(["Help students find new study spots", "Make students come back", "Grow the social side"], input.Goals);

        var brief = (await DrawerAsync(problemId)).Brief!;
        Assert.False(brief.OutOfDate);
        Assert.Equal(["The need", "Who's asking", "What they're asking for", "Pain and impact", "Disagreements and open questions"],
            brief.Sections.Select(s => s.Title));
        var asking = brief.WhoIsAsking;
        Assert.Equal((2, 3), (asking.FeatureRequests, asking.Upvotes));
        Assert.True(asking.FirstAt <= asking.LastAt);
        Assert.Equal([("Other", 1), ("Spot details", 1)], asking.CategoryPicks.Select(c => (c.Category, c.Count)));

        var claim = Assert.Single(brief.Sections[2].Claims);
        Assert.Equal(("Closing times on each spot.", "Description of Show closing times", true),
            (claim.Text, claim.Quote, claim.Verified));
        var citation = Assert.Single(claim.Citations);
        Assert.Equal((second, "Show closing times", "Description of Show closing times", "ok"),
            (citation.FeatureRequestId, citation.Title, citation.Description, citation.State));
        Assert.All(brief.Sections.SelectMany(s => s.Claims), c => Assert.True(c.Verified));
        Assert.Empty(brief.Sections[1].Claims);

        var call = (await Client.GetFromJsonAsync<AiCallJson[]>($"/api/admin/ai-calls?problemId={problemId}"))!
            .First(c => c.Step == "Brief");
        Assert.Equal(("OK", null, problemId), (call.Status, call.Note, call.ProblemId));
    }

    [Fact]
    public async Task Claims_citing_other_problems_requests_or_misquoting_are_marked_unverified_and_noted_in_the_log()
    {
        var (problemId, request) = await NewProblemAsync("Opening hours");
        var (_, elsewhere) = await NewProblemAsync("Dark mode");
        Ai.Brief = _ => new BriefForm(
            Need: [new("Students can't tell when spots are open.", [request], "Request for Opening hours")],
            AskingFor: [new("Dark mode too.", [request, elsewhere], null)],
            PainAndImpact: [new("They are furious.", [request], "I am furious")],
            OpenQuestions: [new("Case matters in quotes.", [request], "request for opening hours")]);

        (await Client.PostAsync($"/api/admin/problems/{problemId}/brief", null)).EnsureSuccessStatusCode();

        var claims = (await DrawerAsync(problemId)).Brief!.Sections.SelectMany(s => s.Claims).ToArray();
        Assert.Equal([true, false, false, false], claims.Select(c => c.Verified));
        Assert.Null(claims[0].Problem);
        Assert.All(claims[1..], c => Assert.NotNull(c.Problem));
        Assert.Equal([("ok", "Request for Opening hours"), ("unverified", "Request for Dark mode")],
            claims[1].Citations.Select(c => (c.State, c.Title)));

        var call = (await Client.GetFromJsonAsync<AiCallJson[]>($"/api/admin/ai-calls?problemId={problemId}"))!
            .First(c => c.Step == "Brief");
        Assert.Equal(("OK", "3 of 4 claims have an unverified citation."), (call.Status, call.Note));
    }

    [Fact]
    public async Task A_brief_is_out_of_date_once_a_new_request_links_but_not_after_upvotes_until_regenerated()
    {
        var (problemId, request) = await NewProblemAsync("Opening hours");
        await GenerateAsync(problemId);
        await UpvoteAsync(request, 2);
        Assert.False((await DrawerAsync(problemId)).Brief!.OutOfDate);

        var added = await AddToProblemAsync(problemId, "Show closing times");

        Assert.True((await DrawerAsync(problemId)).Brief!.OutOfDate);
        await GenerateAsync(problemId);
        var brief = (await DrawerAsync(problemId)).Brief!;
        Assert.False(brief.OutOfDate);
        Assert.Equal((2, 2), (brief.WhoIsAsking.FeatureRequests, brief.WhoIsAsking.Upvotes));
        Assert.Equal([request, added], brief.Sections[0].Claims.Single().Citations.Select(c => c.FeatureRequestId).Order());
    }

    [Fact]
    public async Task After_a_move_the_brief_is_out_of_date_and_citations_to_the_moved_request_say_so()
    {
        var (problemId, stays) = await NewProblemAsync("Opening hours");
        var moves = await AddToProblemAsync(problemId, "Dark mode please");
        var (otherId, _) = await NewProblemAsync("Dark mode");
        await GenerateAsync(problemId);
        await GenerateAsync(otherId);

        (await Client.PostAsJsonAsync("/api/admin/feature-requests/move", new { featureRequestIds = new[] { moves }, toProblemId = otherId }))
            .EnsureSuccessStatusCode();

        var brief = (await DrawerAsync(problemId)).Brief!;
        Assert.True(brief.OutOfDate);
        var citations = brief.Sections[0].Claims.Single().Citations;
        Assert.Equal([(stays, "ok"), (moves, "moved")], citations.Select(c => (c.FeatureRequestId, c.State)).Order());
        Assert.Equal("Dark mode please", citations.Single(c => c.State == "moved").Title);
        Assert.True((await DrawerAsync(otherId)).Brief!.OutOfDate);

        // Merging a Problem with a brief away takes its brief with it.
        (await Client.PostAsJsonAsync("/api/admin/problems/merge", new { problemIds = new[] { problemId, otherId }, keepId = otherId }))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/admin/problems/{problemId}")).StatusCode);
        Assert.True((await DrawerAsync(otherId)).Brief!.OutOfDate);
    }

    [Fact]
    public async Task A_failed_brief_answers_try_again_and_keeps_the_previous_brief()
    {
        var (problemId, _) = await NewProblemAsync("Opening hours");
        Ai.Brief = _ => throw new AiCallException(AiFailureKind.Timeout, "Timed out after 60 s");

        var first = await Client.PostAsync($"/api/admin/problems/{problemId}/brief", null);

        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        Assert.Null((await DrawerAsync(problemId)).Brief);

        Ai.Brief = FakeAiGateway.DefaultBrief;
        await GenerateAsync(problemId);
        var kept = (await DrawerAsync(problemId)).Brief!;
        Ai.Brief = _ => new BriefForm([], [], [], []); // nothing usable

        Assert.Equal(HttpStatusCode.BadGateway, (await Client.PostAsync($"/api/admin/problems/{problemId}/brief", null)).StatusCode);
        var after = (await DrawerAsync(problemId)).Brief!;
        Assert.Equal((kept.WrittenAt, kept.Sections[0].Claims.Single().Text), (after.WrittenAt, after.Sections[0].Claims.Single().Text));
        var calls = (await Client.GetFromJsonAsync<AiCallJson[]>($"/api/admin/ai-calls?problemId={problemId}"))!
            .Where(c => c.Step == "Brief").Select(c => (c.Status, c.FailureKind)).ToArray();
        Assert.Equal([("Failed", "Invalid output"), ("OK", null), ("Failed", "Timeout")], calls);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync("/api/admin/problems/999/brief", null)).StatusCode);
    }

    private async Task GenerateAsync(long problemId) =>
        (await Client.PostAsync($"/api/admin/problems/{problemId}/brief", null)).EnsureSuccessStatusCode();

    private async Task<BriefDrawerJson> DrawerAsync(long id) =>
        (await Client.GetFromJsonAsync<BriefDrawerJson>($"/api/admin/problems/{id}"))!;
}

public sealed record BriefDrawerJson(long Id, BriefJson? Brief);

public sealed record BriefJson(DateTimeOffset WrittenAt, bool OutOfDate, WhoIsAskingJson WhoIsAsking, BriefSectionJson[] Sections);

public sealed record WhoIsAskingJson(int FeatureRequests, int Upvotes, DateTimeOffset FirstAt, DateTimeOffset LastAt, CategoryPickJson[] CategoryPicks);

public sealed record CategoryPickJson(string Category, int Count);

public sealed record BriefSectionJson(string Key, string Title, BriefClaimJson[] Claims);

public sealed record BriefClaimJson(string Text, string? Quote, bool Verified, string? Problem, CitationJson[] Citations);

public sealed record CitationJson(long FeatureRequestId, string? Title, string? Description, string State);
