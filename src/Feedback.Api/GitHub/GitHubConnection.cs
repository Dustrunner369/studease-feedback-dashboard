using Microsoft.Extensions.Options;

namespace Feedback.Api.GitHub;

/// <summary>
/// The app's connection to the configured repo, kept in memory: the repo's current full name and default branch,
/// or why it can't be reached. Checked once at startup (<see cref="GitHubStartupCheck"/>) and again on
/// Settings → GitHub's Check again. Never throws for a GitHub failure; it records it.
/// Callers that need the repo (Create GitHub issue, refresh, Suggest Solutions) use <see cref="RepoAsync"/>.
/// </summary>
public sealed class GitHubConnection(IGitHubGateway github, IOptions<GitHubOptions> options, ILogger<GitHubConnection> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile GitHubStatus? _status;

    public string ConfiguredRepo => options.Value.Repo.Trim();

    /// <summary>The last check's result; checks first if there hasn't been one yet.</summary>
    public async Task<GitHubStatus> StatusAsync(CancellationToken ct) => _status ?? await CheckAsync(ct, onlyIfUnchecked: true);

    /// <summary>Reads the configured repo's current name and default branch again (and so whether the token works).</summary>
    public Task<GitHubStatus> CheckAsync(CancellationToken ct) => CheckAsync(ct, onlyIfUnchecked: false);

    /// <summary>
    /// The connected repo: the one from the last good check, or a fresh check if the last one failed.
    /// Throws <see cref="GitHubCallException"/> (with the reason) if it still can't be reached.
    /// </summary>
    public async Task<RepoInfo> RepoAsync(CancellationToken ct)
    {
        var status = _status is { Repo: not null } ok ? ok : await CheckAsync(ct);
        return status.Repo ?? throw new GitHubCallException(status.Error ?? "GitHub isn't connected.");
    }

    private async Task<GitHubStatus> CheckAsync(CancellationToken ct, bool onlyIfUnchecked)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (onlyIfUnchecked && _status is { } done) return done;
            var configured = ConfiguredRepo;
            string? error = null;
            RepoInfo? repo = null;
            if (!IsOwnerSlashName(configured)) error = "No repo is configured: set GitHub:Repo to owner/name.";
            else
            {
                try { repo = await github.GetRepoAsync(configured, ct); }
                catch (GitHubCallException e) { error = e.Message; }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.LogError(e, "GitHub check failed unexpectedly");
                    error = "The GitHub check failed unexpectedly. See the server log.";
                }
            }
            if (error is not null) log.LogWarning("GitHub not connected to {Repo}: {Error}", configured, error);
            return _status = new GitHubStatus(configured, repo, error, DateTimeOffset.UtcNow);
        }
        finally { _lock.Release(); }
    }

    internal static bool IsOwnerSlashName(string repo) =>
        repo.Split('/') is [{ Length: > 0 } owner, { Length: > 0 } name]
        && !owner.Any(char.IsWhiteSpace) && !name.Any(char.IsWhiteSpace);
}

/// <summary>Repo is null (and Error says why) when the last check failed.</summary>
public sealed record GitHubStatus(string ConfiguredRepo, RepoInfo? Repo, string? Error, DateTimeOffset CheckedAt);

/// <summary>Connects to GitHub once at startup, in the background. A missing or bad token never stops the app.</summary>
public sealed class GitHubStartupCheck(GitHubConnection connection, ILogger<GitHubStartupCheck> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await connection.StatusAsync(stoppingToken); }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.LogError(e, "GitHub startup check failed"); }
    }
}
