using System.Net;
using Feedback.Api.Ai;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class ProblemsTableTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task Each_problem_shows_its_category_status_requests_upvotes_and_no_priority_yet()
    {
        var (problemId, first) = await NewProblemAsync("Filter by opening hours", "Finding spots");
        var second = await AddToProblemAsync(problemId, "Show spots open now");
        await UpvoteAsync(first, 2);
        await UpvoteAsync(second, 3);

        var row = Assert.Single((await ProblemsAsync()).Problems);

        Assert.Equal(new ProblemRowJson(problemId, "Filter by opening hours", "Finding spots", "New", 2, 5, null), row);
    }

    [Fact]
    public async Task A_category_tab_shows_only_that_categorys_problems()
    {
        await NewProblemAsync("Filter by opening hours", "Finding spots");
        await NewProblemAsync("Share spots with friends", "Social");

        Assert.Equal(["Share spots with friends"], (await ProblemsAsync("Social")).Problems.Select(p => p.Title));
        Assert.Empty((await ProblemsAsync("Visits")).Problems);
        Assert.Equal(2, (await ProblemsAsync()).Problems.Length);
    }

    [Fact]
    public async Task An_unknown_category_is_rejected()
    {
        var response = await Client.GetAsync("/api/admin/problems?category=Nope");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Problems_are_ordered_by_recommended_score_not_by_popularity()
    {
        Ai.Score = input => input.Problem.Title switch
        {
            "Strategic need" => new(5, "Advances a goal.", "Grow the social side", 4, "Hard.", 3, "Uses it less."),
            "Popular need" => new(1, "No goal.", null, 2, "Minor.", 1, "No sign."),
            _ => new(3, "Small way.", "Make students come back", 3, "Friction.", 2, "Mild."),
        };
        var (popular, popularRequest) = await NewProblemAsync("Popular need");
        await UpvoteAsync(popularRequest, 40); // Reach 5 → 30 + 0 + 6.25 + 0 = 36
        var (middle, _) = await NewProblemAsync("Middle need");       // 15 + 7.5 + 12.5 + 3.75 = 39
        var (strategic, _) = await NewProblemAsync("Strategic need"); // 30 + 7.5 + 18.75 + 7.5 = 64

        var rows = (await ProblemsAsync()).Problems;

        Assert.Equal([strategic, middle, popular], rows.Select(p => p.Id));
    }

    [Fact]
    public async Task Problems_not_yet_scored_come_after_scored_ones()
    {
        var (scored, _) = await NewProblemAsync("Scored need");
        Ai.Score = _ => throw new AiCallException(AiFailureKind.Timeout, "Timed out");
        var (unscored, request) = await NewProblemAsync("Unscored need");
        await UpvoteAsync(request, 50);

        Assert.Equal([scored, unscored], (await ProblemsAsync()).Problems.Select(p => p.Id));
    }

    [Fact]
    public async Task Unmatched_requests_are_counted_as_needing_review_and_are_not_in_the_table()
    {
        await NewProblemAsync("Filter by opening hours");
        Ai.Match = _ => throw new AiCallException(AiFailureKind.ApiError, "500 from OpenAI");
        await SubmitAsync("Dark mode", "Other");
        await SubmitAsync("Night theme", "Other");
        await App.WhenIntakeIdleAsync();

        var page = await ProblemsAsync();

        Assert.Equal(2, page.NeedsReview);
        Assert.Single(page.Problems);
    }
}
