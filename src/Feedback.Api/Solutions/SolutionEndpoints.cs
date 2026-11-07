using Feedback.Api.Admin;
using Feedback.Api.Ai;
using Feedback.Api.Data;

namespace Feedback.Api.Solutions;

public static class SolutionEndpoints
{
    /// <summary>
    /// POST /problems/{id}/solutions: runs Suggest Solutions synchronously and returns the refreshed drawer.
    /// A failure is a 502 whose detail says what went wrong (the drawer offers Try again; the last result stays).
    /// </summary>
    public static void MapSolutions(this RouteGroupBuilder admin)
    {
        admin.MapPost("/problems/{id:long}/solutions", async (
            long id, SolutionSuggester suggester, DrawerBuilder drawers, CancellationToken ct) =>
        {
            var outcome = await suggester.SuggestAsync(id, ct);
            if (!outcome.Found) return Results.NotFound();
            if (outcome.Error is { } error)
                return Results.Problem(error, title: "Suggest Solutions failed", statusCode: StatusCodes.Status502BadGateway);
            return await drawers.OkAsync(id, ct);
        });
    }
}

/// <summary>
/// The drawer's Suggest Solutions section: the latest Solution Ideas, the files read, the two ai_calls ids, and the
/// configured repo (<c>owner/name</c>) so the page can link each idea's issue numbers.
/// </summary>
public sealed record SolutionsView(
    IReadOnlyList<SolutionIdea> Ideas, IReadOnlyList<string> Files, DateTimeOffset GeneratedAt, long PickerAiCallId, long WriteAiCallId,
    string Repo)
{
    public static SolutionsView? Of(SolutionSuggestion? s, string repo) =>
        s is null ? null : new(SolutionIdeaRules.Read(s.Ideas), s.Files, s.GeneratedAt, s.PickerAiCallId, s.WriteAiCallId, repo);
}
