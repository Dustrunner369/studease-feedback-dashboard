using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Feedback.Api.Ai;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class PmDecisionTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task Renaming_a_problem_changes_its_title_and_description_and_is_logged()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");

        var response = await PatchAsync(problemId, new { title = "  Open-now filter ", description = "Show only spots open now." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var drawer = (await response.Content.ReadFromJsonAsync<DecisionDrawerJson>())!;
        Assert.Equal(("Open-now filter", "Show only spots open now."), (drawer.Title, drawer.Description));
        Assert.Equal(["rename", "rename"], drawer.Decisions.Select(d => d.Kind));
        var rename = drawer.Decisions.Single(d => d.Details.GetProperty("field").GetString() == "title");
        Assert.Null(rename.Reason);
        Assert.Equal("Filter by opening hours", rename.Details.GetProperty("from").GetString());
        Assert.Equal("Open-now filter", rename.Details.GetProperty("to").GetString());
        Assert.Equal("Open-now filter", (await ProblemsAsync()).Problems.Single().Title);
    }

    [Fact]
    public async Task Changing_the_category_moves_its_requests_to_that_tab_and_is_logged()
    {
        var (problemId, requestId) = await NewProblemAsync("Filter by opening hours", "Finding spots");

        var drawer = await PatchOkAsync(problemId, new { category = "Spot details" });

        Assert.Equal("Spot details", drawer.Category);
        var change = Assert.Single(drawer.Decisions);
        Assert.Equal("category", change.Kind);
        Assert.Equal(("Finding spots", "Spot details"),
            (change.Details.GetProperty("from").GetString(), change.Details.GetProperty("to").GetString()));
        Assert.Equal([requestId], (await BoardAsync("Spot details")).Select(r => r.Id));
        Assert.Empty(await BoardAsync("Finding spots"));
    }

    [Theory]
    [InlineData("category", "Billing")]
    [InlineData("status", "Done")]
    [InlineData("priority", "Urgent")]
    [InlineData("title", "  ")]
    public async Task Unknown_values_are_rejected_and_nothing_changes(string field, string value)
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");

        var response = await PatchAsync(problemId, new Dictionary<string, string> { [field] = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await response.Content.ReadFromJsonAsync<ValidationJson>())!.Errors.Keys);
        var drawer = await DrawerAsync(problemId);
        Assert.Equal(("Filter by opening hours", "Finding spots", "New", null), (drawer.Title, drawer.Category, drawer.Status, drawer.Priority));
        Assert.Empty(drawer.Decisions);
    }

    [Fact]
    public async Task A_priority_matching_the_recommended_level_needs_no_reason()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours"); // fake ratings → 39, Low

        var drawer = await PatchOkAsync(problemId, new { priority = "Low" });

        Assert.Equal(("Low", "Low", null), (drawer.Priority, drawer.Recommended!.Level, drawer.PriorityOverrideReason));
        var set = Assert.Single(drawer.Decisions);
        Assert.Equal(("priority", null), (set.Kind, set.Reason));
        Assert.Equal("Low", set.Details.GetProperty("to").GetString());
        Assert.Equal("Low", (await ProblemsAsync()).Problems.Single().Priority);
    }

    [Fact]
    public async Task A_priority_that_differs_from_the_recommended_level_needs_a_one_line_reason()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours"); // Low

        var missing = await PatchAsync(problemId, new { priority = "High" });
        var multiLine = await PatchAsync(problemId, new { priority = "High", priorityReason = "Exam season.\nAlso the CEO." });
        var drawer = await PatchOkAsync(problemId, new { priority = "High", priorityReason = " Exam season is coming. " });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("priorityReason", (await missing.Content.ReadFromJsonAsync<ValidationJson>())!.Errors.Keys);
        Assert.Equal(HttpStatusCode.BadRequest, multiLine.StatusCode);
        Assert.Equal(("High", "Exam season is coming."), (drawer.Priority, drawer.PriorityOverrideReason));
        var set = Assert.Single(drawer.Decisions);
        Assert.Equal(("priority", "Exam season is coming."), (set.Kind, set.Reason));
        Assert.Equal("Low", set.Details.GetProperty("recommended").GetString());
    }

    [Fact]
    public async Task One_patch_closing_as_wont_do_and_overriding_the_priority_keeps_the_two_reasons_apart()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours"); // Low

        var missing = await PatchAsync(problemId, new { status = "Won't do", reason = "Out of scope for v1.", priority = "High" });
        var drawer = await PatchOkAsync(problemId,
            new { status = "Won't do", reason = "Out of scope for v1.", priority = "High", priorityReason = "Exam season." });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(["priorityReason"], (await missing.Content.ReadFromJsonAsync<ValidationJson>())!.Errors.Keys);
        Assert.Equal(("Won't do", "High", "Exam season."), (drawer.Status, drawer.Priority, drawer.PriorityOverrideReason));
        Assert.Equal("Out of scope for v1.", drawer.Decisions.Single(d => d.Kind == "status").Reason);
        Assert.Equal("Exam season.", drawer.Decisions.Single(d => d.Kind == "priority").Reason);
    }

    [Fact]
    public async Task Without_a_recommended_priority_any_priority_can_be_set_and_null_clears_it()
    {
        Ai.Score = _ => throw new AiCallException(AiFailureKind.ApiError, "down");
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");

        var set = await PatchOkAsync(problemId, new { priority = "High" });
        var cleared = await PatchOkAsync(problemId, new { priority = (string?)null });

        Assert.Null(set.Recommended);
        Assert.Equal("High", set.Priority);
        Assert.Null(cleared.Priority);
        Assert.Equal(2, cleared.Decisions.Length);
    }

    [Fact]
    public async Task Wont_do_needs_a_reason_then_closes_the_problem_but_its_requests_stay_on_the_board()
    {
        var (problemId, requestId) = await NewProblemAsync("Filter by opening hours");

        var missing = await PatchAsync(problemId, new { status = "Won't do" });
        var drawer = await PatchOkAsync(problemId, new { status = "Won't do", reason = "Out of scope for v1." });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("reason", (await missing.Content.ReadFromJsonAsync<ValidationJson>())!.Errors.Keys);
        Assert.Equal("Won't do", drawer.Status);
        Assert.NotNull(drawer.ClosedAt);
        var change = Assert.Single(drawer.Decisions);
        Assert.Equal(("status", "Out of scope for v1."), (change.Kind, change.Reason));
        Assert.Equal(("New", "Won't do"), (change.Details.GetProperty("from").GetString(), change.Details.GetProperty("to").GetString()));

        Assert.Empty((await ProblemsAsync()).Problems);
        var archived = Assert.Single(await ArchiveAsync());
        Assert.Equal((problemId, "Won't do", "Out of scope for v1."), (archived.Id, archived.Status, archived.Reason));
        Assert.Equal([requestId], (await BoardAsync()).Select(r => r.Id));
        await UpvoteAsync(requestId, 2);
        Assert.Equal(2, (await BoardAsync()).Single().Upvotes);
    }

    [Fact]
    public async Task Other_statuses_need_no_reason_and_keep_the_problem_open()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");

        var drawer = await PatchOkAsync(problemId, new { status = "In progress" });

        Assert.Equal("In progress", drawer.Status);
        Assert.Null(drawer.ClosedAt);
        Assert.Equal("In progress", (await ProblemsAsync()).Problems.Single().Status);
        Assert.Empty(await ArchiveAsync());
    }

    [Fact]
    public async Task Shipped_closes_the_problem_into_the_archive_and_takes_its_requests_off_the_board()
    {
        Ai.Score = _ => new ScoreRatings(4, "fit", "Help students find new study spots", 3, "pain", 2, "retention");
        var (problemId, requestId) = await NewProblemAsync("Filter by opening hours");
        var other = await SubmitAsync("Dark mode please", "Other");
        await App.WhenIntakeIdleAsync();
        await PatchOkAsync(problemId, new { priority = "High", priorityReason = "Exam season." });

        var drawer = await PatchOkAsync(problemId, new { status = "Shipped" });

        Assert.Equal("Shipped", drawer.Status);
        Assert.NotNull(drawer.ClosedAt);
        Assert.DoesNotContain(problemId, (await ProblemsAsync()).Problems.Select(p => p.Id));
        var archived = Assert.Single(await ArchiveAsync());
        Assert.Equal((problemId, "Filter by opening hours", "Finding spots", "Shipped", "High"),
            (archived.Id, archived.Title, archived.Category, archived.Status, archived.Priority));
        Assert.Equal(("Medium", 46), (archived.Recommended!.Level, archived.Recommended.Score));
        Assert.Equal(drawer.ClosedAt, archived.ClosedAt);
        Assert.Equal(1, archived.FeatureRequests);
        Assert.Null(archived.Reason);
        Assert.Equal([other.Id], (await BoardAsync()).Select(r => r.Id));
        Assert.DoesNotContain(requestId, (await BoardAsync("Finding spots")).Select(r => r.Id));
    }

    [Fact]
    public async Task A_closed_problems_recommended_priority_is_frozen_until_it_is_reopened()
    {
        var (problemId, requestId) = await NewProblemAsync("Filter by opening hours"); // 39: Reach 2 (3 points)
        await PatchOkAsync(problemId, new { status = "Won't do", reason = "Not now." });

        await UpvoteAsync(requestId, 40); // 43 points would be Reach 5
        var frozen = Assert.Single(await ArchiveAsync()).Recommended!;

        Assert.Equal(39, frozen.Score);
        Assert.Single(Ai.ScoreCalls); // closing and Upvotes on a closed Problem call no AI
    }

    [Theory]
    [InlineData("Won't do")]
    [InlineData("Shipped")]
    public async Task Reopening_a_closed_problem_scores_it_again_with_the_ai(string closedAs)
    {
        var (problemId, requestId) = await NewProblemAsync("Filter by opening hours"); // 39: Reach 2 (3 points)
        await PatchOkAsync(problemId, new { status = closedAs, reason = "Not now." });
        await UpvoteAsync(requestId, 40); // 43 points: Reach 5
        Ai.Score = _ => new ScoreRatings(5, "fit", "Help students find new study spots", 5, "pain", 5, "retention");

        var reopened = await PatchOkAsync(problemId, new { status = "Under review" });

        Assert.Null(reopened.ClosedAt);
        Assert.Equal((100, "High"), (reopened.Recommended!.Score, reopened.Recommended.Level)); // every rating 5
        Assert.Equal(2, Ai.ScoreCalls.Count);
        Assert.Empty(await ArchiveAsync());
    }

    [Fact]
    public async Task Setting_the_same_value_again_writes_no_decision()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");

        var drawer = await PatchOkAsync(problemId, new { title = "Filter by opening hours", status = "New", category = "Finding spots" });

        Assert.Empty(drawer.Decisions);
    }

    [Fact]
    public async Task Patching_an_unknown_problem_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(999, new { title = "Anything" })).StatusCode);
    }

    private async Task<DecisionDrawerJson> PatchOkAsync(long problemId, object body)
    {
        var response = await PatchAsync(problemId, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DecisionDrawerJson>())!;
    }

    private async Task<ArchiveRowJson[]> ArchiveAsync() =>
        (await Client.GetFromJsonAsync<ArchiveRowJson[]>("/api/admin/archive"))!;

    private async Task<DecisionDrawerJson> DrawerAsync(long id) =>
        (await Client.GetFromJsonAsync<DecisionDrawerJson>($"/api/admin/problems/{id}"))!;

    private async Task<Portal.FeatureRequestJson[]> BoardAsync(string category = "") =>
        (await Client.GetFromJsonAsync<Portal.FeatureRequestJson[]>("/api/requests?category=" + Uri.EscapeDataString(category)))!;

    private Task<HttpResponseMessage> PatchAsync(long problemId, object body) =>
        Client.PatchAsJsonAsync($"/api/admin/problems/{problemId}", body);
}

public sealed record DecisionDrawerJson(
    long Id, string Title, string Description, string Category, string Status, string? Priority,
    string? PriorityOverrideReason, RecommendedJson? Recommended, DecisionJson[] Decisions, DateTimeOffset? ClosedAt);

public sealed record ArchiveRowJson(
    long Id, string Title, string Category, string Status, string? Priority, ArchivedRecommendedJson? Recommended,
    int FeatureRequests, int Upvotes, string? Reason, DateTimeOffset ClosedAt);

public sealed record ArchivedRecommendedJson(string Level, int Score);

public sealed record DecisionJson(string Kind, string? Reason, JsonElement Details, DateTimeOffset CreatedAt);

public sealed record ValidationJson(Dictionary<string, string[]> Errors);
