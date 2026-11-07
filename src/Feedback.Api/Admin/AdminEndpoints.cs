using Feedback.Api.GitHub;
using Feedback.Api.Outbox;
using Feedback.Api.Portal;

namespace Feedback.Api.Admin;

/// <summary>The /admin API under /api/admin. Everything except login/logout needs the admin cookie.</summary>
public static class AdminEndpoints
{
    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin");

        // Rate-limited like the portal so the key can't be guessed quickly.
        admin.MapPost("/login", (LoginBody body, AdminAuth auth, HttpResponse response) =>
        {
            if (!auth.KeyMatches(body.Key)) return Results.Unauthorized();
            auth.SignIn(response);
            return Results.NoContent();
        }).RequireRateLimiting(PortalRateLimit.Policy);

        admin.MapPost("/logout", (AdminAuth auth, HttpResponse response) =>
        {
            auth.SignOut(response);
            return Results.NoContent();
        });

        var signedIn = admin.MapGroup("").AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<AdminAuth>().IsSignedIn(context.HttpContext.Request)
                ? await next(context)
                : Results.Unauthorized());

        signedIn.MapProblems();
        signedIn.MapFeatureRequests();
        signedIn.MapAiCalls();
        signedIn.MapPriority();
        signedIn.MapArchive();
        signedIn.MapGitHub();
        signedIn.MapOutbox();
        signedIn.MapMetrics();
    }
}

public sealed record LoginBody(string? Key);
