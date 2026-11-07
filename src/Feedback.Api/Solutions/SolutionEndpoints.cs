using Feedback.Api.Admin;
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

/// <summary>The drawer's Suggest Solutions section: the latest result, the files it read, and its two ai_calls ids.</summary>
public sealed record SolutionsView(string Body, IReadOnlyList<string> Files, DateTimeOffset GeneratedAt, long PickerAiCallId, long WriteAiCallId)
{
    public static SolutionsView? Of(SolutionSuggestion? s) =>
        s is null ? null : new(s.Body, s.Files, s.GeneratedAt, s.PickerAiCallId, s.WriteAiCallId);
}
