using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Portal;

/// <summary>
/// Suggest-as-you-type. The fake embeds each text by its first line (the title), so each test
/// places requests at known similarities to the typed query (which sits on axis 0).
/// Similarity of <c>Axis(n, 0, w)</c> to <c>Axis(0)</c> is w / sqrt(1 + w²):
/// w = 0.3 → 0.29, 0.5 → 0.45, 1 → 0.71, 2 → 0.89, 4 → 0.97.
/// </summary>
public sealed class SuggestTests(FeedbackDatabase database) : ApiTest(database)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Suggest:ShowThreshold"] = "0.40",
        ["Suggest:StrongThreshold"] = "0.80",
    };

    private readonly Dictionary<string, float[]> _vectors = new() { ["Query"] = FakeAiGateway.Axis(0) };

    [Fact]
    public async Task Matches_above_the_show_threshold_come_back_closest_first_and_only_a_top_match_above_the_strong_threshold_is_strong()
    {
        UseVectors();
        await SubmitAsync("Weak", FakeAiGateway.Axis(10, 0, 0.3f));
        await SubmitAsync("Ordinary", FakeAiGateway.Axis(11, 0, 0.5f));
        await SubmitAsync("Strong", FakeAiGateway.Axis(12, 0, 2f));
        await SubmitAsync("Also ordinary", FakeAiGateway.Axis(13, 0, 1f));
        await App.WhenIntakeIdleAsync();

        var matches = await SuggestAsync();

        Assert.Equal(["Strong", "Also ordinary", "Ordinary"], matches.Select(m => m.Title));
        Assert.Equal([true, false, false], matches.Select(m => m.Strong));
        Assert.Equal(0.89, matches[0].Similarity, 2);
        Assert.Equal("Description of Strong", matches[0].Description);
    }

    [Fact]
    public async Task Each_problem_is_offered_once_through_its_best_matching_request()
    {
        UseVectors();
        await SubmitAsync("P far", FakeAiGateway.Axis(10, 0, 1f));
        await App.WhenIntakeIdleAsync();
        Ai.Match = input => FakeAiGateway.Existing(input.Candidates[0].ProblemId);
        await SubmitAsync("P close", FakeAiGateway.Axis(11, 0, 2f));
        await App.WhenIntakeIdleAsync();
        Ai.Match = FakeAiGateway.NewProblemFromRequest;
        await SubmitAsync("Q", FakeAiGateway.Axis(12, 0, 0.5f));
        await App.WhenIntakeIdleAsync();

        var matches = await SuggestAsync();

        Assert.Equal(["P close", "Q"], matches.Select(m => m.Title));
    }

    [Fact]
    public async Task At_most_four_matches_come_back()
    {
        UseVectors();
        for (var i = 1; i <= 5; i++) await SubmitAsync($"Match {i}", FakeAiGateway.Axis(10 + i, 0, 5f - i * 0.5f));
        await App.WhenIntakeIdleAsync();

        var matches = await SuggestAsync();

        Assert.Equal(["Match 1", "Match 2", "Match 3", "Match 4"], matches.Select(m => m.Title));
    }

    [Fact]
    public async Task Only_the_top_match_is_strong_even_when_the_next_one_also_passes_the_strong_threshold()
    {
        UseVectors();
        await SubmitAsync("Strong", FakeAiGateway.Axis(11, 0, 2f));
        await SubmitAsync("Strongest", FakeAiGateway.Axis(12, 0, 4f));
        await App.WhenIntakeIdleAsync();

        var matches = await SuggestAsync();

        Assert.Equal(["Strongest", "Strong"], matches.Select(m => m.Title));
        Assert.Equal([true, false], matches.Select(m => m.Strong));
    }

    [Fact]
    public async Task Nothing_is_strong_when_the_top_match_is_below_the_strong_threshold()
    {
        UseVectors();
        await SubmitAsync("Ordinary", FakeAiGateway.Axis(11, 0, 1f));
        await App.WhenIntakeIdleAsync();

        var match = Assert.Single(await SuggestAsync());

        Assert.False(match.Strong);
    }

    [Fact]
    public async Task Requests_whose_problem_has_shipped_are_never_suggested_but_wont_do_ones_are()
    {
        UseVectors();
        var shipped = await SubmitAsync("Shipped one", FakeAiGateway.Axis(11, 0, 4f));
        var wontDo = await SubmitAsync("Won't do one", FakeAiGateway.Axis(12, 0, 2f));
        await App.WhenIntakeIdleAsync();
        await SetProblemStatusAsync(shipped.Id, "Shipped");
        await SetProblemStatusAsync(wontDo.Id, "Won't do");

        var matches = await SuggestAsync();

        Assert.Equal(["Won't do one"], matches.Select(m => m.Title));
        Assert.True(matches[0].Strong);
    }

    [Fact]
    public async Task Upvote_counts_and_this_browsers_upvote_come_with_each_match()
    {
        UseVectors();
        var request = await SubmitAsync("Strong", FakeAiGateway.Axis(12, 0, 2f));
        await App.WhenIntakeIdleAsync();
        await UpvoteAsync(request.Id, "browser-token-alice-0001");
        await UpvoteAsync(request.Id, "browser-token-bob-000002");

        var message = new HttpRequestMessage(HttpMethod.Post, "/api/suggest")
        {
            Content = JsonContent.Create(new { title = "Query", description = "what I am typing" }),
        };
        message.Headers.Add("X-Browser-Token", "browser-token-alice-0001");
        var response = await Client.SendAsync(message);
        var match = Assert.Single((await response.Content.ReadFromJsonAsync<SuggestionJson[]>())!);

        Assert.Equal(2, match.Upvotes);
        Assert.True(match.Upvoted);
        Assert.DoesNotContain("example.com", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Fewer_than_four_characters_suggest_nothing_and_embed_nothing()
    {
        UseVectors();
        await SubmitAsync("Strong", FakeAiGateway.Axis(12, 0, 2f));
        await App.WhenIntakeIdleAsync();
        var embedded = 0;
        Ai.Embed = _ => { embedded++; return FakeAiGateway.Axis(0); };

        Assert.Empty(await SuggestAsync("ab", " c "));
        Assert.Equal(0, embedded);
    }

    [Fact]
    public async Task An_embedding_failure_suggests_nothing_and_the_form_still_submits()
    {
        UseVectors();
        await SubmitAsync("Strong", FakeAiGateway.Axis(12, 0, 2f));
        await App.WhenIntakeIdleAsync();
        Ai.Embed = _ => throw new Feedback.Api.Ai.AiCallException(Feedback.Api.Ai.AiFailureKind.Timeout, "Timed out");

        Assert.Empty(await SuggestAsync());
        var submitted = await Client.PostAsJsonAsync("/api/requests", new
        {
            title = "Query", description = "what I am typing", category = "Other", email = "sam@example.com",
        });
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
    }

    private async Task UpvoteAsync(long id, string token)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/upvote");
        message.Headers.Add("X-Browser-Token", token);
        (await Client.SendAsync(message)).EnsureSuccessStatusCode();
    }

    private void UseVectors() => Ai.Embed = text => _vectors[text[..text.IndexOf('\n')]];

    private async Task<FeatureRequestJson> SubmitAsync(string title, float[] vector)
    {
        _vectors[title] = vector;
        var response = await Client.PostAsJsonAsync("/api/requests", new
        {
            title,
            description = $"Description of {title}",
            category = "Other",
            email = "sam@example.com",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeatureRequestJson>())!;
    }

    private async Task<SuggestionJson[]> SuggestAsync(string title = "Query", string description = "what I am typing")
    {
        var response = await Client.PostAsJsonAsync("/api/suggest", new { title, description });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SuggestionJson[]>())!;
    }
}

/// <summary>One suggest-as-you-type match: a Feature Request plus how close it is.</summary>
public sealed record SuggestionJson(
    long Id, string Title, string Description, string Category, int Upvotes, bool Upvoted, double Similarity, bool Strong);
