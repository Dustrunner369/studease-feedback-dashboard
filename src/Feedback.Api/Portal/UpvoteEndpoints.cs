using System.Text.RegularExpressions;
using Feedback.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Portal;

/// <summary>
/// The random token a browser makes once and sends as the <c>X-Browser-Token</c> header.
/// It is the only thing that tells one upvoter from another; it carries no identity.
/// </summary>
public static partial class BrowserToken
{
    public const string Header = "X-Browser-Token";

    /// <summary>The request's token, or null when it is missing or malformed.</summary>
    public static string? From(HttpRequest request)
    {
        var token = request.Headers[Header].ToString();
        return Shape().IsMatch(token) ? token : null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex Shape();
}

/// <summary>POST / DELETE /api/requests/{id}/upvote: at most one Upvote per browser token per Feature Request.</summary>
public static class UpvoteEndpoints
{
    public static void MapUpvotes(this RouteGroupBuilder api)
    {
        api.MapPost("/requests/{id:long}/upvote", (long id, HttpRequest http, FeedbackDbContext db, CancellationToken ct) =>
            Change(id, http, db, ct, token => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO feedback.upvotes (feature_request_id, browser_token) VALUES ({id}, {token}) ON CONFLICT DO NOTHING", ct)));

        api.MapDelete("/requests/{id:long}/upvote", (long id, HttpRequest http, FeedbackDbContext db, CancellationToken ct) =>
            Change(id, http, db, ct, token => db.Upvotes
                .Where(u => u.FeatureRequestId == id && u.BrowserToken == token).ExecuteDeleteAsync(ct)));
    }

    private static async Task<IResult> Change(
        long id, HttpRequest http, FeedbackDbContext db, CancellationToken ct, Func<string, Task<int>> apply)
    {
        var token = BrowserToken.From(http);
        if (token is null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["browserToken"] = ["Missing or malformed browser token."] });
        if (!await db.FeatureRequests.AnyAsync(r => r.Id == id, ct)) return Results.NotFound();

        await apply(token);

        var upvotes = await db.Upvotes.CountAsync(u => u.FeatureRequestId == id, ct);
        var upvoted = await db.Upvotes.AnyAsync(u => u.FeatureRequestId == id && u.BrowserToken == token, ct);
        return Results.Ok(new UpvoteState(upvotes, upvoted));
    }
}

/// <summary>A Feature Request's Upvote count and whether this browser has upvoted it.</summary>
public sealed record UpvoteState(int Upvotes, bool Upvoted);
