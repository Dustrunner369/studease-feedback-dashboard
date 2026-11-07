using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Ai;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class OutboxTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task Marking_shipped_drafts_one_update_per_requester_email_listing_their_requests()
    {
        var (problemId, first) = await NewProblemAsync("Filter by opening hours", email: "sam@example.com");
        var second = await AddToProblemAsync(problemId, "Show open now", email: "SAM@example.com ");
        var other = await AddToProblemAsync(problemId, "Opening times filter", email: "kim@example.com");

        await ShipAsync(problemId);

        var drafts = await OutboxAsync("drafts");
        Assert.Equal(2, drafts.Length);
        var sam = drafts.Single(d => d.ToEmail == "sam@example.com");
        Assert.Equal([first, second], sam.FeatureRequests.Select(r => r.Id).Order());
        Assert.Equal([other], drafts.Single(d => d.ToEmail == "kim@example.com").FeatureRequests.Select(r => r.Id));
        Assert.All(drafts, d =>
        {
            Assert.Equal(("Ready", problemId, "Filter by opening hours"), (d.Status, d.ProblemId, d.ProblemTitle));
            Assert.Equal("It's here: Filter by opening hours", d.Subject);
            Assert.Null(d.SentAt);
        });
        Assert.Contains("Show open now", sam.Body);
        // The AI never sees an email address.
        Assert.All(Ai.DraftUpdateCalls, call => Assert.DoesNotContain("@", System.Text.Json.JsonSerializer.Serialize(call)));
        Assert.Empty(await OutboxAsync("sent"));
        var calls = await Client.GetFromJsonAsync<AiCallRowJson[]>($"/api/admin/ai-calls?problemId={problemId}");
        Assert.Equal(2, calls!.Count(c => c.Step == "DraftUpdate" && c.Status == "OK"));
    }

    [Fact]
    public async Task Shipping_again_after_reopening_does_not_draft_a_second_update()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        await ShipAsync(problemId);
        (await Client.PatchAsJsonAsync($"/api/admin/problems/{problemId}", new { status = "In progress" })).EnsureSuccessStatusCode();

        await ShipAsync(problemId);

        Assert.Single(await OutboxAsync("drafts"));
    }

    [Fact]
    public async Task Approving_a_draft_marks_it_sent_and_moves_it_to_the_sent_tab()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        await ShipAsync(problemId);
        var draft = (await OutboxAsync("drafts")).Single();

        var response = await Client.PostAsync($"/api/admin/outbox/{draft.Id}/send", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = (await response.Content.ReadFromJsonAsync<OutboxMessageJson>())!;
        Assert.Equal("Sent", sent.Status);
        Assert.NotNull(sent.SentAt);
        Assert.Empty(await OutboxAsync("drafts"));
        Assert.Equal([draft.Id], (await OutboxAsync("sent")).Select(m => m.Id));
        // A sent message can't be sent again, edited or discarded.
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/admin/outbox/{draft.Id}/send", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await Client.PatchAsJsonAsync($"/api/admin/outbox/{draft.Id}", new { subject = "x", body = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client.DeleteAsync($"/api/admin/outbox/{draft.Id}")).StatusCode);
    }

    [Fact]
    public async Task Editing_a_draft_changes_its_subject_and_body()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        await ShipAsync(problemId);
        var draft = (await OutboxAsync("drafts")).Single();

        var response = await Client.PatchAsJsonAsync($"/api/admin/outbox/{draft.Id}",
            new { subject = "  Open-now filter is live ", body = "Hi,\n\nIt's in the app now.\n\nStudease" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edited = (await OutboxAsync("drafts")).Single();
        Assert.Equal(("Open-now filter is live", "Hi,\n\nIt's in the app now.\n\nStudease", "Ready"),
            (edited.Subject, edited.Body, edited.Status));
    }

    [Fact]
    public async Task An_edit_needs_a_subject_and_a_body()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        await ShipAsync(problemId);
        var draft = (await OutboxAsync("drafts")).Single();

        var response = await Client.PatchAsJsonAsync($"/api/admin/outbox/{draft.Id}", new { subject = " ", body = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<ValidationJson>())!.Errors;
        Assert.Equal(["body", "subject"], errors.Keys.Order());
        Assert.Equal(draft.Subject, (await OutboxAsync("drafts")).Single().Subject);
    }

    [Fact]
    public async Task Discarding_a_draft_deletes_it()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        await ShipAsync(problemId);
        var draft = (await OutboxAsync("drafts")).Single();

        var response = await Client.DeleteAsync($"/api/admin/outbox/{draft.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await OutboxAsync("drafts"));
        Assert.Empty(await OutboxAsync("sent"));
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/api/admin/outbox/{draft.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_failed_draft_shows_draft_failed_and_can_be_retried()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        Ai.DraftUpdate = _ => throw new AiCallException(AiFailureKind.Timeout, "Timed out.");

        // The status change still succeeds.
        await ShipAsync(problemId);

        var failed = (await OutboxAsync("drafts")).Single();
        Assert.Equal(("Draft failed", "", ""), (failed.Status, failed.Subject, failed.Body));
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/admin/outbox/{failed.Id}/send", null)).StatusCode);

        Ai.DraftUpdate = FakeAiGateway.DefaultDraft;
        var response = await Client.PostAsync($"/api/admin/outbox/{failed.Id}/retry", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var retried = (await response.Content.ReadFromJsonAsync<OutboxMessageJson>())!;
        Assert.Equal(("Ready", "It's here: Filter by opening hours"), (retried.Status, retried.Subject));
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/admin/outbox/{failed.Id}/retry", null)).StatusCode);
        var calls = await Client.GetFromJsonAsync<AiCallRowJson[]>($"/api/admin/ai-calls?problemId={problemId}");
        Assert.Equal(["OK", "Failed"], calls!.Where(c => c.Step == "DraftUpdate").Select(c => c.Status));
    }

    [Fact]
    public async Task A_failed_draft_can_be_written_by_hand_and_then_approved()
    {
        var (problemId, _) = await NewProblemAsync("Filter by opening hours");
        Ai.DraftUpdate = _ => throw new AiCallException(AiFailureKind.ApiError, "HTTP 500");
        await ShipAsync(problemId);
        var failed = (await OutboxAsync("drafts")).Single();

        (await Client.PatchAsJsonAsync($"/api/admin/outbox/{failed.Id}", new { subject = "It's live", body = "Thanks for asking." }))
            .EnsureSuccessStatusCode();

        Assert.Equal("Ready", (await OutboxAsync("drafts")).Single().Status);
        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync($"/api/admin/outbox/{failed.Id}/send", null)).StatusCode);
        Assert.Equal("It's live", (await OutboxAsync("sent")).Single().Subject);
    }

    [Fact]
    public async Task Unknown_messages_and_tabs_are_rejected()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync("/api/admin/outbox/999/send", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync("/api/admin/outbox/999/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.PatchAsJsonAsync("/api/admin/outbox/999", new { subject = "a", body = "b" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/admin/outbox?tab=spam")).StatusCode);
    }

    [Fact]
    public async Task The_outbox_needs_the_admin_cookie()
    {
        var anonymous = App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/outbox?tab=drafts")).StatusCode);
    }

    private async Task ShipAsync(long problemId) =>
        (await Client.PatchAsJsonAsync($"/api/admin/problems/{problemId}", new { status = "Shipped" })).EnsureSuccessStatusCode();

    private async Task<OutboxMessageJson[]> OutboxAsync(string tab) =>
        (await Client.GetFromJsonAsync<OutboxMessageJson[]>($"/api/admin/outbox?tab={tab}"))!;
}

public sealed record OutboxMessageJson(
    long Id, long? ProblemId, string ProblemTitle, OutboxRequestJson[] FeatureRequests, string ToEmail,
    string Subject, string Body, string Status, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);

public sealed record OutboxRequestJson(long Id, string Title);

public sealed record AiCallRowJson(string Step, string Status);
