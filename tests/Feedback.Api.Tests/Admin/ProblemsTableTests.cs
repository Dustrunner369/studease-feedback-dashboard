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
    public async Task Until_scoring_exists_the_most_requested_problems_come_first()
    {
        var (quiet, _) = await NewProblemAsync("Quiet need");
        var (busy, _) = await NewProblemAsync("Busy need");
        await AddToProblemAsync(busy, "Me too");
        var (upvoted, upvotedRequest) = await NewProblemAsync("Upvoted need");
        await UpvoteAsync(upvotedRequest, 4);

        // Reach points: Feature Requests × 3 + Upvotes → Upvoted 7, Busy 6, Quiet 3.
        Assert.Equal([upvoted, busy, quiet], (await ProblemsAsync()).Problems.Select(p => p.Id));
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
