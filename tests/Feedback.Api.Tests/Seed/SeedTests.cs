using System.Net.Http.Json;
using System.Text.Json;
using Feedback.Api.Ai;
using Feedback.Api.Seed;
using Feedback.Api.Tests.Admin;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Seed;

/// <summary>
/// The seed CLI (seam 2), run in-process against the test database with the fake AI gateway,
/// observed through the HTTP API.
/// </summary>
public sealed class SeedTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task Load_scores_the_ais_first_answers_against_the_answer_key()
    {
        // Answer key: A1..A4 are Problem "a", B1..B3 Problem "b", S1 is spam (no intended Problem).
        // The AI: A3 gets a second new Problem (missed duplicate), B3 joins A's Problem (wrong group), A4's match fails.
        ScriptMatches(new()
        {
            ["A1"] = "new:PA", ["A2"] = "existing:PA", ["B1"] = "new:PB", ["A3"] = "new:PA2",
            ["B2"] = "existing:PB", ["S1"] = "new:PS", ["B3"] = "existing:PA", ["A4"] = "fail",
        });
        var file = SeedFile(
            Req("A1", "a"), Req("A2", "a"), Req("B1", "b"), Req("A3", "a"),
            Req("B2", "b"), Req("S1", null, "spam"), Req("B3", "b"), Req("A4", "a"));

        var (exit, output) = await SeedAsync("load", "--file", file);

        Assert.Equal(0, exit);
        Assert.Contains("7 matched, 1 failed", output);
        var seed = (await MetricsAsync()).Seed!;
        // Duplicates: A2, A3, B2, B3, A4 had their Problem already; A2, B2, B3 were linked to an existing one.
        Assert.Equal(0.6, seed.DuplicateCatchRate);
        // PA stands for a (A1, A2 right; B3 wrong), PB for b, PA2 for a, PS for nothing (S1 right); A4 failed.
        Assert.Equal(0.75, seed.GroupingAccuracy);
        Assert.Equal((4, 2, 1), (seed.AiProblemCount, seed.IntendedProblemCount, seed.FailedMatches));
        Assert.True(DateTimeOffset.UtcNow - seed.ComputedAt < TimeSpan.FromMinutes(1));
        // The AI never sees the answer key.
        Assert.All(Ai.MatchCalls, call => Assert.DoesNotContain("\"a\"", JsonSerializer.Serialize(call)));
    }

    [Fact]
    public async Task Metrics_are_empty_until_a_seed_load_and_carry_the_targets()
    {
        var metrics = await MetricsAsync();

        Assert.Null(metrics.Seed);
        Assert.Equal((0.90, 0.85), (metrics.Targets.DuplicateCatchRate, metrics.Targets.GroupingAccuracy));
    }

    [Fact]
    public async Task Load_refuses_when_the_schema_already_has_data()
    {
        await SubmitAsync("Already here", "Other");
        await App.WhenIntakeIdleAsync();

        var (exit, output) = await SeedAsync("load", "--file", SeedFile(Req("A1", "a")));

        Assert.NotEqual(0, exit);
        Assert.Contains("already has data", output);
        Assert.Equal(["Already here"], (await BoardAsync()).Select(r => r.Title));
        Assert.Null((await MetricsAsync()).Seed);
    }

    [Fact]
    public async Task Load_backdates_requests_and_gives_them_their_upvotes()
    {
        var file = SeedFile(Req("A1", "a", daysAgo: 80, upvotes: 7), Req("B1", "b", daysAgo: 2.5, upvotes: 0));

        await SeedAsync("load", "--file", file);

        var board = await BoardAsync();
        var a1 = board.Single(r => r.Title == "A1");
        Assert.Equal(7, a1.Upvotes);
        Assert.InRange((DateTimeOffset.UtcNow - a1.CreatedAt).TotalDays, 79.9, 80.1);
        Assert.Equal(0, board.Single(r => r.Title == "B1").Upvotes);
        // Reach counts the seeded Upvotes: 1 request × 3 + 7.
        Assert.Equal(7, (await ProblemsAsync()).Problems.Single(p => p.Title == "Need: A1").Upvotes);
    }

    [Fact]
    public async Task Load_applies_the_product_team_actions_through_the_triage_services()
    {
        var file = SeedFile(
            [Req("A1", "a", email: "ann@example.com"), Req("A2", "a", email: "bob@example.com"),
             Req("B1", "b"), Req("C1", "c"), Req("D1", "d")],
            new { request = "A1", status = "Shipped" },
            new { request = "B1", priority = "recommended" },
            new { request = "C1", priority = "High", reason = "Small now, but it is the social side we are betting on." },
            new { request = "D1", status = "Won't do", reason = "Out of scope for a study-spot app." });
        Ai.Match = input => input.Request.Title == "A2" ? FakeAiGateway.Existing(input.Candidates.Single(c => c.Title == "Need: A1").ProblemId)
            : FakeAiGateway.NewProblemFromRequest(input);

        var (exit, output) = await SeedAsync("load", "--file", file);

        Assert.Equal(0, exit);
        var archive = (await Client.GetFromJsonAsync<ArchiveRowJson[]>("/api/admin/archive"))!;
        Assert.Equal([("Need: A1", "Shipped"), ("Need: D1", "Won't do")],
            archive.Select(a => (a.Title, a.Status)).Order());
        Assert.Equal("Out of scope for a study-spot app.", archive.Single(a => a.Status == "Won't do").Reason);
        var drafts = (await Client.GetFromJsonAsync<OutboxMessageJson[]>("/api/admin/outbox?tab=drafts"))!;
        Assert.Equal(["ann@example.com", "bob@example.com"], drafts.Select(d => d.ToEmail).Order());

        var problems = (await ProblemsAsync()).Problems;
        var b = await DrawerAsync(problems.Single(p => p.Title == "Need: B1").Id);
        Assert.Equal(b.Recommended!.Level, b.Priority);
        Assert.Null(b.PriorityOverrideReason);
        var c = await DrawerAsync(problems.Single(p => p.Title == "Need: C1").Id);
        Assert.Equal(("High", "Small now, but it is the social side we are betting on."), (c.Priority, c.PriorityOverrideReason));
        Assert.NotEqual("High", c.Recommended!.Level);
        Assert.Contains("Shipped", output);
    }

    [Fact]
    public async Task Load_carries_on_past_failed_calls_and_skips_actions_on_unmatched_requests()
    {
        Ai.Embed = text => text.StartsWith("A2") ? throw new AiCallException(AiFailureKind.ApiError, "Boom.")
            : FakeAiGateway.Axis(text.Length % 1000);
        var file = SeedFile([Req("A1", "a"), Req("A2", "a"), Req("A3", "a")], new { request = "A2", status = "Shipped" });

        var (exit, output) = await SeedAsync("load", "--file", file);

        Assert.Equal(0, exit);
        Assert.Contains("2 matched, 1 failed", output);
        Assert.Contains("A2", output);
        Assert.Equal(1, (await ProblemsAsync()).NeedsReview);
        Assert.Empty((await Client.GetFromJsonAsync<ArchiveRowJson[]>("/api/admin/archive"))!);
        Assert.Equal(1, (await MetricsAsync()).Seed!.FailedMatches);
    }

    [Fact]
    public async Task Remove_empties_every_table_and_a_fresh_load_works_afterwards()
    {
        var file = SeedFile([Req("A1", "a", upvotes: 3), Req("B1", "b")], new { request = "A1", status = "Shipped" });
        await SeedAsync("load", "--file", file);

        var (exit, output) = await SeedAsync("remove");

        Assert.Equal(0, exit);
        Assert.Contains("feature_requests: 2", output);
        Assert.Contains("upvotes: 3", output);
        Assert.Contains("seed_metrics: 1", output);
        Assert.Empty(await BoardAsync());
        Assert.Empty((await ProblemsAsync()).Problems);
        Assert.Empty((await Client.GetFromJsonAsync<ArchiveRowJson[]>("/api/admin/archive"))!);
        Assert.Empty((await Client.GetFromJsonAsync<OutboxMessageJson[]>("/api/admin/outbox?tab=drafts"))!);
        Assert.Empty((await Client.GetFromJsonAsync<AiCallRowJson[]>("/api/admin/ai-calls"))!);
        Assert.Null((await MetricsAsync()).Seed);

        // The schema and the vector extension survive: a second load embeds and matches again.
        var (again, _) = await SeedAsync("load", "--file", file);
        Assert.Equal(0, again);
        Assert.Equal(2, (await MetricsAsync()).Seed!.AiProblemCount);
    }

    [Fact]
    public async Task The_checked_in_seed_file_loads_with_its_answer_key_and_actions()
    {
        var (exit, output) = await SeedAsync("load");

        Assert.Equal(0, exit);
        var seed = (await MetricsAsync()).Seed!;
        Assert.Equal(15, seed.IntendedProblemCount);
        Assert.Equal(0, seed.FailedMatches);
        Assert.Contains("matched, 0 failed", output);
        var archive = (await Client.GetFromJsonAsync<ArchiveRowJson[]>("/api/admin/archive"))!;
        Assert.Equal(2, archive.Count(a => a.Status == "Shipped"));
        Assert.Equal(1, archive.Count(a => a.Status == "Won't do"));
        Assert.NotEmpty((await Client.GetFromJsonAsync<OutboxMessageJson[]>("/api/admin/outbox?tab=drafts"))!);
        var problems = (await ProblemsAsync()).Problems;
        Assert.Equal(5, problems.Count(p => p.Priority is not null));
        Assert.Equal(7, problems.Select(p => p.Category).Distinct().Count());
        Assert.DoesNotContain(GitHub.Calls, c => !c.StartsWith("GetRepo"));
    }

    [Fact]
    public async Task Unknown_subcommand_prints_usage()
    {
        var (exit, output) = await SeedAsync("explode");

        Assert.NotEqual(0, exit);
        Assert.Contains("seed load", output);
        Assert.Contains("seed remove", output);
    }

    // ---------- helpers ----------

    private async Task<(int Exit, string Output)> SeedAsync(params string[] args)
    {
        var output = new StringWriter();
        var exit = await SeedCli.RunAsync(App.Services, args, output, CancellationToken.None);
        return (exit, output.ToString());
    }

    private static object Req(string title, string? problem, string? kind = null, double daysAgo = 10, int upvotes = 0,
        string email = "sam@example.com", string category = "Finding spots") =>
        new { title, description = $"{title} please.", category, email, daysAgo, upvotes, problem, kind };

    private static string SeedFile(params object[] requests) => SeedFile(requests, []);

    private static string SeedFile(object[] requests, params object[] actions)
    {
        var path = Path.Combine(Path.GetTempPath(), $"seed-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { requests, actions }, JsonSerializerOptions.Web));
        return path;
    }

    /// <summary>title → "new:&lt;Problem title&gt;", "existing:&lt;Problem title&gt;" or "fail".</summary>
    private void ScriptMatches(Dictionary<string, string> answers) => Ai.Match = input =>
    {
        var answer = answers[input.Request.Title];
        if (answer == "fail") throw new AiCallException(AiFailureKind.InvalidOutput, "Scripted failure.");
        var (kind, title) = (answer.Split(':')[0], answer.Split(':')[1]);
        return kind == "new"
            ? new MatchDecision(null, new NewProblemDraft(title, $"About {title}.", "Finding spots"), false, "New need.")
            : FakeAiGateway.Existing(input.Candidates.Single(c => c.Title == title).ProblemId);
    };

    private async Task<MetricsJson> MetricsAsync() => (await Client.GetFromJsonAsync<MetricsJson>("/api/admin/metrics"))!;

    private async Task<Portal.FeatureRequestJson[]> BoardAsync() =>
        (await Client.GetFromJsonAsync<Portal.FeatureRequestJson[]>("/api/requests?sort=new"))!;

    private async Task<DecisionDrawerJson> DrawerAsync(long problemId) =>
        (await Client.GetFromJsonAsync<DecisionDrawerJson>($"/api/admin/problems/{problemId}"))!;
}

public sealed record MetricsJson(MetricTargetsJson Targets, SeedMetricsJson? Seed);

public sealed record MetricTargetsJson(double DuplicateCatchRate, double GroupingAccuracy);

public sealed record SeedMetricsJson(
    DateTimeOffset ComputedAt, double? DuplicateCatchRate, double? GroupingAccuracy,
    int AiProblemCount, int IntendedProblemCount, int FailedMatches);
