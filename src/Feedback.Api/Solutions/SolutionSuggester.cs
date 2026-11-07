using Feedback.Api.Ai;
using Feedback.Api.Data;
using Feedback.Api.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Feedback.Api.Solutions;

/// <summary>The <c>Solutions</c> config section: how much of the repo Suggest Solutions reads.</summary>
public sealed class SolutionsOptions
{
    public const string Section = "Solutions";

    /// <summary>About how many files the picker chooses; more are ignored.</summary>
    public int MaxFiles { get; set; } = 8;
    /// <summary>Each chosen file is cut to this many bytes.</summary>
    public int MaxFileBytes { get; set; } = 24_000;
    /// <summary>All chosen files together; files past it are cut or left out.</summary>
    public int MaxTotalBytes { get; set; } = 120_000;
    /// <summary>At most this many filtered paths go to the AI.</summary>
    public int MaxPaths { get; set; } = 3_000;
    /// <summary>Each open issue's or PR's body is cut to this many characters.</summary>
    public int MaxItemBodyChars { get; set; } = 1_500;
}

/// <summary>
/// Suggest Solutions for one Problem: read the connected repo's tree (filtered), let the picker choose files,
/// read them within the size caps, list open issues and PRs, then write options. Both AI calls are logged;
/// on success the result replaces the Problem's previous one, on failure the previous one stays.
/// </summary>
public sealed class SolutionSuggester(
    FeedbackDbContext db, AiCallLog log, IAiGateway ai, GitHubConnection connection, IGitHubGateway github,
    IOptions<SolutionsOptions> options)
{
    public async Task<SuggestOutcome> SuggestAsync(long problemId, CancellationToken ct)
    {
        var problem = await db.Problems.AsNoTracking().SingleOrDefaultAsync(p => p.Id == problemId, ct);
        if (problem is null) return SuggestOutcome.NotFound;
        var config = options.Value;
        var about = new ScoreProblem(problem.Title, problem.Description, problem.Category);
        var @for = new AiCallFor(problemId);

        RepoInfo repo;
        IReadOnlyList<string> paths;
        IReadOnlyList<OpenItem> openItems;
        try
        {
            repo = await connection.RepoAsync(ct);
            var tree = await github.ListFilesAsync(repo.FullName, repo.DefaultBranch, ct);
            paths = RepoFilter.Keep(tree.Files.Select(f => f.Path)).Take(config.MaxPaths).ToList();
            openItems = await github.ListOpenIssuesAsync(repo.FullName, ct);
        }
        catch (GitHubCallException e) { return SuggestOutcome.Failed("Couldn't read the GitHub repo: " + e.Message); }
        if (paths.Count == 0) return SuggestOutcome.Failed($"{repo.FullName} has no code files to read.");

        var known = paths.ToHashSet(StringComparer.Ordinal);
        var picked = await log.RunLoggedAsync(AiSteps.PickFiles, @for,
            c => ai.PickFilesAsync(new PickFilesInput(about, paths, config.MaxFiles), c), ct,
            check: picks => picks.Paths.Any(known.Contains) ? null : "None of the picked paths are in the repo.",
            note: $"Choosing from {paths.Count} files");
        if (picked.Value is not { } picks) return SuggestOutcome.Failed("The AI couldn't choose files to read.");
        var chosen = picks.Paths.Where(known.Contains).Distinct(StringComparer.Ordinal).Take(config.MaxFiles).ToList();

        var files = new List<SourceFile>();
        try
        {
            var left = config.MaxTotalBytes;
            foreach (var path in chosen)
            {
                if (left <= 0) break;
                var cap = Math.Min(config.MaxFileBytes, left);
                if (await github.ReadFileAsync(repo.FullName, repo.DefaultBranch, path, cap, ct) is not { } file) continue;
                files.Add(new SourceFile(file.Path, file.Text, file.Truncated));
                left -= System.Text.Encoding.UTF8.GetByteCount(file.Text);
            }
        }
        catch (GitHubCallException e) { return SuggestOutcome.Failed("Couldn't read the chosen files: " + e.Message); }

        var items = openItems
            .Select(i => new RepoItem(i.Number, i.Title, Cut(i.Body, config.MaxItemBodyChars), i.IsPullRequest)).ToList();
        var open = items.Select(i => i.Number).ToHashSet();
        var written = await log.RunLoggedAsync(AiSteps.WriteSolutions, @for,
            c => ai.WriteSolutionsAsync(new WriteSolutionsInput(about, paths, files, items), c), ct,
            check: a => a.Ideas.Count > 0 ? null : "No Solution Ideas.",
            note: $"{files.Count} files, {items.Count} open issues and PRs.",
            noteFor: a => SolutionIdeaRules.Clean(a.Ideas, open).Shortened switch
            {
                0 => null,
                1 => "1 idea shortened.",
                var n => $"{n} ideas shortened.",
            });
        if (written.Value is not { } answer) return SuggestOutcome.Failed("The AI couldn't write solutions.");
        var ideas = SolutionIdeaRules.Write(SolutionIdeaRules.Clean(answer.Ideas, open).Ideas);

        var row = await db.SolutionSuggestions.SingleOrDefaultAsync(s => s.ProblemId == problemId, ct);
        if (row is null)
        {
            row = new SolutionSuggestion { ProblemId = problemId, Ideas = ideas };
            db.SolutionSuggestions.Add(row);
        }
        row.Ideas = ideas;
        row.Files = files.Select(f => f.Path).ToArray();
        row.GeneratedAt = DateTimeOffset.UtcNow;
        row.PickerAiCallId = picked.CallId;
        row.WriteAiCallId = written.CallId;
        await db.SaveChangesAsync(ct);
        return SuggestOutcome.Done;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Guardrail layer 3 for Solution Ideas: at most 3 ideas, titles and sentences on one line and cut with "…" at
/// <see cref="MaxTitle"/> / <see cref="MaxDescription"/> characters, and only open issue/PR numbers. Also stores the ideas as JSON and reads them back.
/// </summary>
public static class SolutionIdeaRules
{
    public const int MaxIdeas = 3, MaxTitle = 80, MaxDescription = 240;

    public static (IReadOnlyList<SolutionIdea> Ideas, int Shortened) Clean(
        IReadOnlyList<SolutionIdea> ideas, IReadOnlySet<int> openNumbers)
    {
        var shortened = 0;
        var kept = ideas.Take(MaxIdeas).Select(i =>
        {
            var title = Fit(i.Title, MaxTitle, out var cutTitle);
            var description = Fit(i.Description, MaxDescription, out var cutDescription);
            if (cutTitle || cutDescription) shortened++;
            return new SolutionIdea(title, description, i.Effort, i.Issues.Where(openNumbers.Contains).Distinct().ToList());
        }).ToList();
        return (kept, shortened);
    }

    private static string Fit(string text, int max, out bool cut)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        cut = line.Length > max;
        return cut ? line[..(max - 1)].TrimEnd() + "…" : line;
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string Write(IReadOnlyList<SolutionIdea> ideas) => System.Text.Json.JsonSerializer.Serialize(ideas, Json);

    public static IReadOnlyList<SolutionIdea> Read(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<List<SolutionIdea>>(json, Json) ?? [];
}

/// <summary>Found = the Problem exists; Error is set when the run failed (the previous result stays).</summary>
public sealed record SuggestOutcome(bool Found, string? Error)
{
    public static readonly SuggestOutcome NotFound = new(false, null);
    public static readonly SuggestOutcome Done = new(true, null);
    public static SuggestOutcome Failed(string error) => new(true, error);
}

/// <summary>Which repo paths are worth showing the AI: no images, no native <c>ios/</c> / <c>android/</c> projects, no EF <c>Migrations/</c>.</summary>
public static class RepoFilter
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif",
    };

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "ios", "android", "Migrations" };

    public static IEnumerable<string> Keep(IEnumerable<string> paths) => paths.Where(Keep);

    public static bool Keep(string path)
    {
        var segments = path.Split('/');
        if (segments[..^1].Any(SkippedFolders.Contains)) return false;
        return !ImageExtensions.Contains(Path.GetExtension(path));
    }
}
