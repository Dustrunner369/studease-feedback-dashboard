using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Feedback.Api.Ai;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class SuggestSolutionsTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task Suggest_solutions_reads_the_repo_writes_options_and_keeps_them_on_the_problem()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        GitHub.Files["lib/spots/spot_list.dart"] = "class SpotList {}";
        GitHub.Files["lib/spots/spot.dart"] = "class Spot { String? hours; }";
        GitHub.Files["README.md"] = "# Studease";
        var issue = GitHub.Add("Opening hours are wrong for libraries", "They close at 6.");
        var pr = GitHub.Add("Add hours field to spot form", pullRequest: true);
        Ai.PickFiles = _ => new FilePicks(["lib/spots/spot.dart", "lib/spots/spot_list.dart"], "Spots and the list.");
        Ai.WriteSolutions = input => $"Option A: add an open-now filter to {input.Problem.Title}.";

        var response = await Client.PostAsync($"/api/admin/problems/{problemId}/solutions", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var solutions = (await response.Content.ReadFromJsonAsync<SolutionsDrawerJson>())!.Solutions!;
        Assert.Equal("Option A: add an open-now filter to Filter by opening hours.", solutions.Body);
        Assert.Equal(["lib/spots/spot.dart", "lib/spots/spot_list.dart"], solutions.Files);
        Assert.True(solutions.GeneratedAt > DateTimeOffset.UtcNow.AddMinutes(-5));

        // Shown again next time the drawer opens.
        Assert.Equivalent(solutions, (await SolutionsDrawerAsync(problemId)).Solutions);

        // The write call got the Problem, every path, the chosen files' text and all open issues and PRs.
        var write = Assert.Single(Ai.WriteSolutionsCalls);
        Assert.Equal("Filter by opening hours", write.Problem.Title);
        Assert.Equal(["README.md", "lib/spots/spot.dart", "lib/spots/spot_list.dart"], write.Paths);
        Assert.Equal(["class Spot { String? hours; }", "class SpotList {}"], write.Files.Select(f => f.Text));
        Assert.Equal([(pr, true), (issue, false)], write.OpenItems.Select(i => (i.Number, i.IsPullRequest)));
        Assert.Equal("They close at 6.", write.OpenItems.Single(i => i.Number == issue).Body);

        // Both calls are logged against the Problem, and the result points at them.
        var calls = await CallsAsync(problemId);
        var picker = calls.Single(c => c.Step == "PickFiles");
        var writer = calls.Single(c => c.Step == "WriteSolutions");
        Assert.Equal(("OK", "OK"), (picker.Status, writer.Status));
        Assert.Equal((picker.Id, writer.Id), (solutions.PickerAiCallId, solutions.WriteAiCallId));
        Assert.Equal(problemId, writer.ProblemId);
    }

    [Fact]
    public async Task Images_native_projects_and_ef_migrations_are_filtered_out_of_the_file_list()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        foreach (var path in new[]
        {
            "assets/logo.png", "assets/photos/Library.JPG", "web/icons/icon.svg", "web/favicon.ico",
            "ios/Runner/AppDelegate.swift", "android/app/build.gradle",
            "backend/Studease.Api/Data/Migrations/20260101_Init.cs", "backend/Studease.Api/Migrations/Snapshot.cs",
            "lib/spots/spot.dart", "backend/Studease.Api/Data/SpotsDbContext.cs", "pubspec.yaml", "lib/iosish.dart",
        })
            GitHub.Files[path] = "x";

        await SuggestAsync(problemId);

        var kept = new[] { "backend/Studease.Api/Data/SpotsDbContext.cs", "lib/iosish.dart", "lib/spots/spot.dart", "pubspec.yaml" };
        Assert.Equal(kept, Assert.Single(Ai.PickFilesCalls).Paths);
        Assert.Equal(kept, Assert.Single(Ai.WriteSolutionsCalls).Paths);
    }

    [Fact]
    public async Task Only_files_from_the_list_are_read_and_at_most_max_files_of_them()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        for (var i = 1; i <= 10; i++) GitHub.Files[$"lib/f{i:D2}.dart"] = $"file {i}";
        GitHub.Files["ios/Runner/AppDelegate.swift"] = "native";
        Ai.PickFiles = _ => new FilePicks(
            ["ios/Runner/AppDelegate.swift", "lib/made_up.dart", .. Enumerable.Range(1, 10).Select(i => $"lib/f{i:D2}.dart")], "All of them.");

        var solutions = await SuggestAsync(problemId);

        var expected = Enumerable.Range(1, 8).Select(i => $"lib/f{i:D2}.dart").ToArray();
        Assert.Equal(expected, solutions.Files);
        Assert.Equal(expected, Assert.Single(Ai.WriteSolutionsCalls).Files.Select(f => f.Path));
        Assert.Equal(8, Assert.Single(Ai.PickFilesCalls).MaxFiles);
    }

    [Fact]
    public async Task Picks_that_are_all_outside_the_list_fail_as_invalid_output()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        GitHub.Files["lib/spot.dart"] = "class Spot {}";
        Ai.PickFiles = _ => new FilePicks(["lib/made_up.dart"], "Guessing.");

        var response = await Client.PostAsync($"/api/admin/problems/{problemId}/solutions", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var picker = (await CallsAsync(problemId)).Single(c => c.Step == "PickFiles");
        Assert.Equal(("Failed", "Invalid output"), (picker.Status, picker.FailureKind));
        Assert.Empty(Ai.WriteSolutionsCalls);
    }

    [Fact]
    public async Task Running_again_replaces_the_result_with_the_latest_one()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        var (otherId, _) = await NewProblemAsync("Share spots with friends", "Social");
        GitHub.Files["lib/spot.dart"] = "class Spot {}";
        Ai.WriteSolutions = input => $"First take on {input.Problem.Title}.";
        await SuggestAsync(problemId);
        await SuggestAsync(otherId);

        Ai.WriteSolutions = input => $"Second take on {input.Problem.Title}.";
        var latest = await SuggestAsync(problemId);

        Assert.Equal("Second take on Filter by opening hours.", latest.Body);
        Assert.Equal("Second take on Filter by opening hours.", (await SolutionsDrawerAsync(problemId)).Solutions!.Body);
        Assert.Equal(latest.WriteAiCallId, (await CallsAsync(problemId)).First(c => c.Step == "WriteSolutions").Id);
        Assert.Equal("First take on Share spots with friends.", (await SolutionsDrawerAsync(otherId)).Solutions!.Body);
    }

    [Fact]
    public async Task A_problem_without_a_run_has_no_solutions_yet()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");

        Assert.Null((await SolutionsDrawerAsync(problemId)).Solutions);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync("/api/admin/problems/999999/solutions", null)).StatusCode);
    }

    [Fact]
    public async Task A_failed_write_says_so_logs_the_failure_and_keeps_the_previous_result()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        GitHub.Files["lib/spot.dart"] = "class Spot {}";
        var previous = await SuggestAsync(problemId);
        Ai.WriteSolutions = _ => throw new AiCallException(AiFailureKind.Timeout, "Timed out after 60 s");

        var response = await Client.PostAsync($"/api/admin/problems/{problemId}/solutions", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetailsJson>())!;
        Assert.Equal("The AI couldn't write solutions.", problem.Detail);
        var write = (await CallsAsync(problemId)).First(c => c.Step == "WriteSolutions");
        Assert.Equal(("Failed", "Timeout"), (write.Status, write.FailureKind));
        Assert.Equivalent(previous, (await SolutionsDrawerAsync(problemId)).Solutions);

        Ai.WriteSolutions = _ => "Try again worked.";
        Assert.Equal("Try again worked.", (await SuggestAsync(problemId)).Body);
    }

    [Fact]
    public async Task A_github_failure_says_why_and_makes_no_ai_call()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        GitHub.Files["lib/spot.dart"] = "class Spot {}";
        GitHub.Failure = "GitHub said the token is invalid (401).";

        var response = await Client.PostAsync($"/api/admin/problems/{problemId}/solutions", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("Couldn't read the GitHub repo: GitHub said the token is invalid (401).",
            (await response.Content.ReadFromJsonAsync<ProblemDetailsJson>())!.Detail);
        Assert.Empty(Ai.PickFilesCalls);
        Assert.Null((await SolutionsDrawerAsync(problemId)).Solutions);
    }

    [Fact]
    public async Task Requester_emails_never_reach_the_ai()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours", email: "sam.secret@example.com");
        await AddToProblemAsync(problemId, "Show open cafés", email: "kim.secret@example.com");
        GitHub.Files["lib/spot.dart"] = "class Spot {}";

        await SuggestAsync(problemId);

        var sent = JsonSerializer.Serialize(Ai.PickFilesCalls) + JsonSerializer.Serialize(Ai.WriteSolutionsCalls);
        Assert.Contains("Filter by opening hours", sent);
        Assert.DoesNotContain("secret@example.com", sent);
    }

    [Fact]
    public async Task Merging_away_a_problem_with_solutions_works_and_the_kept_problem_keeps_its_own()
    {
        var (keptId, _) = await NewProblemAsync("Filter by opening hours");
        var (otherId, _) = await NewProblemAsync("Show only open spots");
        GitHub.Files["lib/spot.dart"] = "class Spot {}";
        Ai.WriteSolutions = input => $"Options for {input.Problem.Title}.";
        await SuggestAsync(keptId);
        await SuggestAsync(otherId);

        var merge = await Client.PostAsJsonAsync("/api/admin/problems/merge", new { problemIds = new[] { keptId, otherId }, keepId = keptId });

        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);
        Assert.Equal("Options for Filter by opening hours.", (await merge.Content.ReadFromJsonAsync<SolutionsDrawerJson>())!.Solutions!.Body);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/admin/problems/{otherId}")).StatusCode);
    }

    private async Task<SolutionsJson> SuggestAsync(long problemId)
    {
        var response = await Client.PostAsync($"/api/admin/problems/{problemId}/solutions", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SolutionsDrawerJson>())!.Solutions!;
    }

    private async Task<SolutionsDrawerJson> SolutionsDrawerAsync(long problemId) =>
        (await Client.GetFromJsonAsync<SolutionsDrawerJson>($"/api/admin/problems/{problemId}"))!;

    private async Task<AiCallJson[]> CallsAsync(long problemId) =>
        (await Client.GetFromJsonAsync<AiCallJson[]>($"/api/admin/ai-calls?problemId={problemId}"))!;
}

public sealed class SuggestSolutionsSizeCapTests(FeedbackDatabase database) : AdminTest(database)
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Solutions:MaxFileBytes"] = "10",
        ["Solutions:MaxTotalBytes"] = "25",
        ["Solutions:MaxItemBodyChars"] = "12",
    };

    [Fact]
    public async Task Chosen_files_are_cut_to_the_per_file_cap_and_stop_at_the_total_cap()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        GitHub.Files["lib/a.dart"] = "aaaaaaaaaaaaaaaaaaaa"; // 20 bytes
        GitHub.Files["lib/b.dart"] = "bbbbbbbb";             // 8 bytes, under the per-file cap
        GitHub.Files["lib/c.dart"] = "cccccccccccccccccccc"; // 20 bytes, only 7 left in the total
        GitHub.Files["lib/d.dart"] = "dddddddddd";           // nothing left
        GitHub.Add("Hours are wrong", "Libraries close at six on Sundays.");

        var response = await Client.PostAsync($"/api/admin/problems/{problemId}/solutions", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var write = Assert.Single(Ai.WriteSolutionsCalls);
        Assert.Equal(
            [("lib/a.dart", "aaaaaaaaaa", true), ("lib/b.dart", "bbbbbbbb", false), ("lib/c.dart", "ccccccc", true)],
            write.Files.Select(f => (f.Path, f.Text, f.Truncated)));
        Assert.Equal("Libraries cl…", Assert.Single(write.OpenItems).Body);
        var solutions = (await response.Content.ReadFromJsonAsync<SolutionsDrawerJson>())!.Solutions!;
        Assert.Equal(["lib/a.dart", "lib/b.dart", "lib/c.dart"], solutions.Files);
    }
}

public sealed record SolutionsDrawerJson(long Id, SolutionsJson? Solutions);

public sealed record SolutionsJson(
    string Body, string[] Files, DateTimeOffset GeneratedAt, long PickerAiCallId, long WriteAiCallId);
