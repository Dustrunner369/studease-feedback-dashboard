using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Feedback.Api.GitHub;

/// <summary>
/// The real <see cref="IGitHubGateway"/>: GitHub's REST API with a fine-grained, single-repo token from the
/// <c>GitHub:Token</c> secret (Contents: read, Issues: read/write, Metadata: read). The token is read per call, so a
/// missing one only fails calls, never startup. Not exercised by the automated tests (they use a fake);
/// smoke-test by hand with the real token.
/// </summary>
public sealed class GitHubApiGateway(IHttpClientFactory httpFactory, IConfiguration configuration) : IGitHubGateway
{
    public const string HttpClientName = "github";
    private const int IssuePageSize = 100;
    private const int MaxIssuePages = 10;

    /// <summary>Registers the named HttpClient this adapter uses.</summary>
    public static void AddHttpClient(IServiceCollection services) =>
        services.AddHttpClient(HttpClientName, (sp, http) =>
        {
            http.BaseAddress = new Uri("https://api.github.com/");
            http.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<GitHubOptions>>().Value.TimeoutSeconds);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("studease-feedback-dashboard");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        });

    public async Task<RepoInfo> GetRepoAsync(string repo, CancellationToken ct)
    {
        using var json = await GetJsonAsync(repo, RepoPath(repo), ct)
            ?? throw new GitHubCallException($"Repo {repo} wasn't found, or the token can't see it (404).");
        var root = json.RootElement;
        return new RepoInfo(root.GetProperty("full_name").GetString()!, root.GetProperty("default_branch").GetString()!);
    }

    public async Task<RepoTree> ListFilesAsync(string repo, string branch, CancellationToken ct)
    {
        using var json = await GetJsonAsync(repo, $"{RepoPath(repo)}/git/trees/{Segments(branch)}?recursive=1", ct)
            ?? throw new GitHubCallException($"Branch {branch} wasn't found in {repo} (404).");
        var files = json.RootElement.GetProperty("tree").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "blob")
            .Select(e => new RepoPath(e.GetProperty("path").GetString()!, e.TryGetProperty("size", out var s) ? s.GetInt64() : 0))
            .ToList();
        return new RepoTree(files, json.RootElement.TryGetProperty("truncated", out var t) && t.GetBoolean());
    }

    public async Task<RepoFile?> ReadFileAsync(string repo, string branch, string path, int maxBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{RepoPath(repo)}/contents/{Segments(path)}?ref={Uri.EscapeDataString(branch)}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        using var response = await SendAsync(repo, request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureOkAsync(repo, response, ct);

        // Read at most maxBytes + 1 so a huge file isn't downloaded in full.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes + 1];
        var read = 0;
        while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), ct) is var n and > 0) read += n;
        var truncated = read > maxBytes;
        return new RepoFile(path, Encoding.UTF8.GetString(buffer, 0, Math.Min(read, maxBytes)), truncated);
    }

    public async Task<IReadOnlyList<OpenItem>> ListOpenIssuesAsync(string repo, CancellationToken ct)
    {
        var items = new List<OpenItem>();
        for (var page = 1; page <= MaxIssuePages; page++)
        {
            using var json = await GetJsonAsync(repo,
                    $"{RepoPath(repo)}/issues?state=open&sort=created&direction=desc&per_page={IssuePageSize}&page={page}", ct)
                ?? throw new GitHubCallException($"Repo {repo} wasn't found (404).");
            var batch = json.RootElement.EnumerateArray().Select(e => new OpenItem(
                e.GetProperty("number").GetInt32(),
                e.GetProperty("title").GetString() ?? "",
                e.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()! : "",
                e.GetProperty("html_url").GetString()!,
                e.TryGetProperty("pull_request", out _))).ToList();
            items.AddRange(batch);
            if (batch.Count < IssuePageSize) break;
        }
        return items;
    }

    public async Task<IssueInfo> CreateIssueAsync(string repo, string title, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{RepoPath(repo)}/issues")
        {
            Content = JsonContent.Create(new { title, body }),
        };
        using var response = await SendAsync(repo, request, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureOkAsync(repo, response, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Issue(json.RootElement);
    }

    public async Task<IssueInfo?> GetIssueAsync(string repo, int number, CancellationToken ct)
    {
        using var json = await GetJsonAsync(repo, $"{RepoPath(repo)}/issues/{number}", ct);
        return json is null ? null : Issue(json.RootElement);
    }

    private static IssueInfo Issue(JsonElement e) => new(
        e.GetProperty("number").GetInt32(), e.GetProperty("html_url").GetString()!,
        e.GetProperty("state").GetString() == IssueStates.Closed ? IssueStates.Closed : IssueStates.Open);

    /// <summary>GET as JSON; null on 404 or 410 (gone).</summary>
    private async Task<JsonDocument?> GetJsonAsync(string repo, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(repo, request, HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        await EnsureOkAsync(repo, response, ct);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string repo, HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        var token = configuration["GitHub:Token"];
        if (string.IsNullOrWhiteSpace(token)) throw new GitHubCallException("No GitHub token is set (GitHub:Token).");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        try
        {
            return await httpFactory.CreateClient(HttpClientName).SendAsync(request, completion, ct);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new GitHubCallException("GitHub didn't answer in time.", e);
        }
        catch (HttpRequestException e)
        {
            throw new GitHubCallException($"Couldn't reach GitHub ({e.Message}).", e);
        }
    }

    private static async Task EnsureOkAsync(string repo, HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        var rateLimited = response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0";
        var detail = await GitHubMessageAsync(response, ct);
        throw new GitHubCallException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub said the token is invalid or expired (401).",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when rateLimited => $"GitHub's rate limit was hit ({code}). Try again later.",
            HttpStatusCode.Forbidden => $"The token isn't allowed to do this on {repo} (403). Check its permissions.",
            _ => $"GitHub answered {code}{(detail is null ? "" : $": {detail}")}.",
        });
    }

    private static async Task<string?> GitHubMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return json.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string RepoPath(string repo) => $"repos/{Segments(repo)}";

    // Escapes each path segment but keeps the slashes (branch names and file paths may contain them).
    private static string Segments(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
