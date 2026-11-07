using System.Collections.Concurrent;
using System.Text;
using Feedback.Api.GitHub;

namespace Feedback.Api.Tests.Infrastructure;

/// <summary>
/// In-memory stand-in for GitHub: one repo (<see cref="Repo"/>, reachable under <see cref="ConfiguredRepo"/> or its
/// current full name), its files on the default branch, and its issues and PRs.
/// Set <see cref="Failure"/> to a message to make every call throw <see cref="GitHubCallException"/> (e.g. a bad token).
/// </summary>
public sealed class FakeGitHubGateway : IGitHubGateway
{
    /// <summary>What the test app's <c>GitHub:Repo</c> is set to (the old name, before the rename).</summary>
    public const string ConfiguredRepo = "Dustrunner369/study-spot-app";

    public RepoInfo Repo { get; set; } = new("Dustrunner369/studease", "main");

    /// <summary>Path → text on the default branch.</summary>
    public Dictionary<string, string> Files { get; } = new();

    public string? Failure { get; set; }

    private readonly ConcurrentDictionary<int, Item> _items = new();
    private int _next;

    /// <summary>Every GitHub call so far, as "Operation repo" (e.g. "CreateIssue Dustrunner369/studease").</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    public sealed record Item(int Number, string Title, string Body, bool IsPullRequest, string State);

    public Item Issue(int number) => _items[number];

    /// <summary>Adds an existing issue or PR, as if someone opened it on GitHub.</summary>
    public int Add(string title, string body = "", bool pullRequest = false, string state = IssueStates.Open)
    {
        var number = Interlocked.Increment(ref _next);
        _items[number] = new Item(number, title, body, pullRequest, state);
        return number;
    }

    public void SetState(int number, string state) => _items[number] = _items[number] with { State = state };

    public void Delete(int number) => _items.TryRemove(number, out _);

    public Task<RepoInfo> GetRepoAsync(string repo, CancellationToken ct) => Run(nameof(GetRepoAsync), repo, () => Repo);

    public Task<RepoTree> ListFilesAsync(string repo, string branch, CancellationToken ct) => Run(nameof(ListFilesAsync), repo, () =>
        new RepoTree(Files.OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new RepoPath(f.Key, Encoding.UTF8.GetByteCount(f.Value))).ToList(), false));

    public Task<RepoFile?> ReadFileAsync(string repo, string branch, string path, int maxBytes, CancellationToken ct) =>
        Run(nameof(ReadFileAsync), repo, () =>
        {
            if (!Files.TryGetValue(path, out var text)) return null;
            var bytes = Encoding.UTF8.GetBytes(text);
            return bytes.Length <= maxBytes ? new RepoFile(path, text, false)
                : new RepoFile(path, Encoding.UTF8.GetString(bytes, 0, maxBytes), true);
        });

    public Task<IReadOnlyList<OpenItem>> ListOpenIssuesAsync(string repo, CancellationToken ct) =>
        Run(nameof(ListOpenIssuesAsync), repo, () => (IReadOnlyList<OpenItem>)_items.Values
            .Where(i => i.State == IssueStates.Open).OrderByDescending(i => i.Number)
            .Select(i => new OpenItem(i.Number, i.Title, i.Body, Url(i), i.IsPullRequest)).ToList());

    public Task<IssueInfo> CreateIssueAsync(string repo, string title, string body, CancellationToken ct) =>
        Run(nameof(CreateIssueAsync), repo, () =>
        {
            var number = Add(title, body);
            return new IssueInfo(number, Url(_items[number]), IssueStates.Open);
        });

    public Task<IssueInfo?> GetIssueAsync(string repo, int number, CancellationToken ct) => Run(nameof(GetIssueAsync), repo, () =>
        _items.TryGetValue(number, out var i) ? new IssueInfo(i.Number, Url(i), i.State) : null);

    private string Url(Item i) => $"https://github.com/{Repo.FullName}/{(i.IsPullRequest ? "pull" : "issues")}/{i.Number}";

    private Task<T> Run<T>(string operation, string repo, Func<T> answer)
    {
        Calls.Enqueue($"{operation.Replace("Async", "")} {repo}");
        if (Failure is { } failure) throw new GitHubCallException(failure);
        if (repo != Repo.FullName && repo != ConfiguredRepo) throw new GitHubCallException($"Repo {repo} not found.");
        return Task.FromResult(answer());
    }
}
