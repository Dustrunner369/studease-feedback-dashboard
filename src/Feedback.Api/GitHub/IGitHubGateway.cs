namespace Feedback.Api.GitHub;

/// <summary>
/// Every GitHub REST call the app makes, against one repo. The real adapter is <see cref="GitHubApiGateway"/>
/// (fine-grained token from the <c>GitHub:Token</c> secret); tests swap in an in-memory fake.
/// <para>
/// Interface contract: <paramref name="repo"/> is always <c>owner/name</c>. Callers pass the configured repo only to
/// <see cref="GetRepoAsync"/>; everything else gets the current <see cref="RepoInfo.FullName"/> it returned
/// (via <see cref="GitHubConnection"/>), because the demo repo was renamed and GitHub search does not follow renames.
/// Every operation either returns or throws <see cref="GitHubCallException"/> (missing/invalid token, no access,
/// not found, rate limit, network). No retries. Never pass Requester emails into anything that writes to GitHub.
/// </para>
/// </summary>
public interface IGitHubGateway
{
    /// <summary>The repo's current full name and default branch (GET /repos/{repo}). Also proves the token works.</summary>
    Task<RepoInfo> GetRepoAsync(string repo, CancellationToken ct);

    /// <summary>Every file (blob) on a branch, recursively, in one request. <see cref="RepoTree.Truncated"/> when GitHub cut the list short.</summary>
    Task<RepoTree> ListFilesAsync(string repo, string branch, CancellationToken ct);

    /// <summary>One file's text on a branch, cut to at most <paramref name="maxBytes"/> bytes; null if there's no such file.</summary>
    Task<RepoFile?> ReadFileAsync(string repo, string branch, string path, int maxBytes, CancellationToken ct);

    /// <summary>All open issues and pull requests (GitHub lists PRs as issues), newest first.</summary>
    Task<IReadOnlyList<OpenItem>> ListOpenIssuesAsync(string repo, CancellationToken ct);

    /// <summary>Creates an issue and returns its number, URL and state.</summary>
    Task<IssueInfo> CreateIssueAsync(string repo, string title, string body, CancellationToken ct);

    /// <summary>An issue's current number, URL and state; null if it no longer exists (deleted or transferred away).</summary>
    Task<IssueInfo?> GetIssueAsync(string repo, int number, CancellationToken ct);
}

public sealed record RepoInfo(string FullName, string DefaultBranch);

public sealed record RepoTree(IReadOnlyList<RepoPath> Files, bool Truncated);

/// <summary>A file in the tree, with its size in bytes.</summary>
public sealed record RepoPath(string Path, long Size);

/// <summary>A file's text; <see cref="Truncated"/> when it was longer than the cap.</summary>
public sealed record RepoFile(string Path, string Text, bool Truncated);

/// <summary>An open issue or pull request (<see cref="IsPullRequest"/>). Body may be empty.</summary>
public sealed record OpenItem(int Number, string Title, string Body, string Url, bool IsPullRequest);

/// <summary>A GitHub issue. State is <see cref="IssueStates.Open"/> or <see cref="IssueStates.Closed"/>.</summary>
public sealed record IssueInfo(int Number, string Url, string State);

public static class IssueStates
{
    public const string Open = "open";
    public const string Closed = "closed";
}

/// <summary>A GitHub call that failed. The message is safe to show the product team (never contains the token).</summary>
public sealed class GitHubCallException(string message, Exception? inner = null) : Exception(message, inner);
