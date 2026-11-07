using System.Text;
using Feedback.Api.Admin;
using Feedback.Api.Data;
using Feedback.Api.Domain;
using Feedback.Api.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Feedback.Api.GitHub;

/// <summary>Settings → GitHub, Create GitHub issue, and the linked-issue refresh.</summary>
public static class GitHubEndpoints
{
    private const int MaxListedRequests = 50;
    private const int MaxRequestText = 300;

    public static void MapGitHub(this RouteGroupBuilder admin)
    {
        admin.MapGet("/github", async (GitHubConnection connection, CancellationToken ct) =>
            Results.Ok(View(await connection.StatusAsync(ct))));

        admin.MapPost("/github/check", async (GitHubConnection connection, CancellationToken ct) =>
            Results.Ok(View(await connection.CheckAsync(ct))));

        // Called when the Problems page opens: re-reads each open Problem's linked issue state.
        // Never fails the page: GitHub errors are counted, and the last known states stay.
        admin.MapPost("/github/refresh", async (
            FeedbackDbContext db, GitHubConnection connection, IGitHubGateway github, CancellationToken ct) =>
        {
            var linked = await db.Problems
                .Where(p => p.GitHubIssueNumber != null && p.Status != ProblemStatuses.Shipped && p.Status != ProblemStatuses.WontDo)
                .OrderBy(p => p.Id).ToListAsync(ct);
            if (linked.Count == 0) return Results.Ok(new RefreshResult(0, 0, null));

            RepoInfo repo;
            try { repo = await connection.RepoAsync(ct); }
            catch (GitHubCallException e) { return Results.Ok(new RefreshResult(0, linked.Count, e.Message)); }

            int checkedCount = 0, failed = 0;
            string? error = null;
            foreach (var problem in linked)
            {
                try
                {
                    if (await github.GetIssueAsync(repo.FullName, problem.GitHubIssueNumber!.Value, ct) is not { } issue)
                        throw new GitHubCallException($"Issue #{problem.GitHubIssueNumber} no longer exists in {repo.FullName}.");
                    problem.GitHubIssueState = issue.State;
                    problem.GitHubIssueUrl = issue.Url;
                    problem.GitHubCheckedAt = DateTimeOffset.UtcNow;
                    checkedCount++;
                }
                catch (GitHubCallException e)
                {
                    failed++;
                    error ??= e.Message;
                }
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new RefreshResult(checkedCount, failed, error));
        });

        // Create GitHub issue (by hand): one issue per Problem; returns the refreshed drawer.
        admin.MapPost("/problems/{id:long}/github-issue", async (
            long id, FeedbackDbContext db, GitHubConnection connection, IGitHubGateway github,
            IOptions<PriorityCriteria> criteria, CancellationToken ct) =>
        {
            var problem = await db.Problems.SingleOrDefaultAsync(p => p.Id == id, ct);
            if (problem is null) return Results.NotFound();
            if (problem.GitHubIssueNumber is { } existing)
                return Results.Problem($"This Problem already has GitHub issue #{existing}.", statusCode: StatusCodes.Status409Conflict);

            var requests = await db.FeatureRequests.AsNoTracking().Where(r => r.ProblemId == id)
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                .Select(r => new { r.Title, r.Description, Upvotes = db.Upvotes.Count(u => u.FeatureRequestId == r.Id) })
                .ToListAsync(ct);
            var body = IssueBody(problem, requests.Select(r => (r.Title, r.Description, r.Upvotes)).ToList());

            IssueInfo issue;
            try
            {
                var repo = await connection.RepoAsync(ct);
                issue = await github.CreateIssueAsync(repo.FullName, NoMentions(problem.Title), body, ct);
            }
            catch (GitHubCallException e)
            {
                return Results.Problem(e.Message, title: "GitHub issue not created", statusCode: StatusCodes.Status502BadGateway);
            }

            problem.GitHubIssueNumber = issue.Number;
            problem.GitHubIssueUrl = issue.Url;
            problem.GitHubIssueState = issue.State;
            problem.GitHubCheckedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ProblemEndpoints.DrawerAsync(id, db, criteria.Value, ct));
        });
    }

    /// <summary>
    /// The issue text: the Problem and what Requesters asked for. Never emails. Requester and AI text has
    /// @-mentions broken so an issue can't ping GitHub users.
    /// </summary>
    private static string IssueBody(Problem p, IReadOnlyList<(string Title, string Description, int Upvotes)> requests)
    {
        var upvotes = requests.Sum(r => r.Upvotes);
        var text = new StringBuilder()
            .AppendLine(NoMentions(p.Description)).AppendLine()
            .AppendLine($"**Category:** {p.Category} · **Feature Requests:** {requests.Count} · **Upvotes:** {upvotes}")
            .AppendLine().AppendLine("### What Requesters asked for");
        foreach (var r in requests.Take(MaxListedRequests))
            text.AppendLine($"- **{NoMentions(OneLine(r.Title))}** ({r.Upvotes} ▲): {NoMentions(OneLine(Cut(r.Description)))}");
        if (requests.Count > MaxListedRequests) text.AppendLine($"- …and {requests.Count - MaxListedRequests} more.");
        return text.AppendLine().Append($"_Created from the Studease feedback dashboard (Problem {p.Id})._").ToString();
    }

    private static string Cut(string s) => s.Length <= MaxRequestText ? s : s[..MaxRequestText] + "…";
    private static string OneLine(string s) => string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    private static string NoMentions(string s) => s.Replace("@", "@​");

    private static GitHubStatusView View(GitHubStatus s) =>
        new(s.ConfiguredRepo, s.Repo?.FullName, s.Repo?.DefaultBranch, s.Repo is not null, s.Error, s.CheckedAt);
}

/// <summary>A refresh: issues read, issues that couldn't be (their last known state stays), and the first error.</summary>
public sealed record RefreshResult(int Checked, int Failed, string? Error);

/// <summary>Settings → GitHub. Repo is GitHub's current name for <see cref="ConfiguredRepo"/>; null (with Error) when the check failed.</summary>
public sealed record GitHubStatusView(
    string ConfiguredRepo, string? Repo, string? DefaultBranch, bool Ok, string? Error, DateTimeOffset CheckedAt);
