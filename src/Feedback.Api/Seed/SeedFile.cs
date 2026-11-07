using System.Text.Json;
using Feedback.Api.Domain;
using Feedback.Api.Intake;

namespace Feedback.Api.Seed;

/// <summary>
/// The checked-in synthetic backlog (<c>seed/feature-requests.json</c>): requests in submission order, each with its
/// answer key (intended Problem key, hard-case kind), then the product-team actions to apply after the load.
/// </summary>
public sealed record SeedFile(IReadOnlyList<SeedProblem>? Problems, IReadOnlyList<SeedRequest> Requests, IReadOnlyList<SeedAction>? Actions)
{
    public const string DefaultPath = "seed/feature-requests.json";

    public static readonly IReadOnlyList<string> HardCaseKinds =
        ["near-duplicate", "bundled", "wrong-category", "vague", "rant", "spam"];

    /// <summary>Reads and checks the file; throws <see cref="SeedFileException"/> with every problem found.</summary>
    public static SeedFile Read(string path)
    {
        SeedFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SeedFile>(File.ReadAllText(path), JsonSerializerOptions.Web);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            throw new SeedFileException($"Can't read {path}: {e.Message}");
        }
        if (file?.Requests is null) throw new SeedFileException($"{path} has no \"requests\".");
        var errors = file.Check();
        if (errors.Count > 0) throw new SeedFileException($"{path} has problems:\n  " + string.Join("\n  ", errors));
        return file;
    }

    /// <summary>The default file: the first <see cref="DefaultPath"/> found walking up from the working or app directory.</summary>
    public static string? FindDefault()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, DefaultPath);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    private List<string> Check()
    {
        var errors = new List<string>();
        var keys = Problems?.Select(p => p.Key).ToHashSet();
        for (var i = 0; i < Requests.Count; i++)
        {
            var r = Requests[i];
            var at = $"request {i + 1} (\"{r.Title}\")";
            var (valid, invalid) = SubmissionValidator.Validate(r.Title, r.Description, r.Category, r.Email);
            if (valid is null) errors.Add($"{at}: {string.Join(" ", invalid.Values.SelectMany(v => v))}");
            if (r.DaysAgo < 0) errors.Add($"{at}: daysAgo can't be negative.");
            if (i > 0 && r.DaysAgo > Requests[i - 1].DaysAgo) errors.Add($"{at}: requests must be in submission order (daysAgo going down).");
            if (r.Upvotes < 0) errors.Add($"{at}: upvotes can't be negative.");
            if (r.Kind is not null && !HardCaseKinds.Contains(r.Kind)) errors.Add($"{at}: unknown kind \"{r.Kind}\".");
            if (r.Problem is not null && keys is not null && !keys.Contains(r.Problem)) errors.Add($"{at}: unknown Problem key \"{r.Problem}\".");
            if (r.Problem is null && r.Kind != "spam") errors.Add($"{at}: only spam has no intended Problem.");
        }
        foreach (var a in Actions ?? [])
        {
            if (!Requests.Any(r => r.Title == a.Request)) errors.Add($"action on \"{a.Request}\": no request has that title.");
            if ((a.Priority is null) == (a.Status is null)) errors.Add($"action on \"{a.Request}\": give a priority or a status.");
            if (a.Priority is not null && a.Priority != SeedAction.Recommended && !PriorityLevels.IsValid(a.Priority))
                errors.Add($"action on \"{a.Request}\": unknown priority \"{a.Priority}\".");
            if (a.Status is not null && !ProblemStatuses.IsValid(a.Status)) errors.Add($"action on \"{a.Request}\": unknown status \"{a.Status}\".");
        }
        return errors;
    }
}

/// <summary>An intended Problem, for whoever reads the file. Only its key is used (to check the requests' keys).</summary>
public sealed record SeedProblem(string Key, string? Title, string? Category);

/// <summary>
/// One synthetic Feature Request. <see cref="Problem"/> (intended Problem key, null for spam) and
/// <see cref="Kind"/> (hard-case kind) are the answer key: never stored and never shown to the AI.
/// </summary>
public sealed record SeedRequest(
    string Title, string Description, string Category, string Email, double DaysAgo, int Upvotes, string? Problem, string? Kind);

/// <summary>
/// A product-team action on the Problem a request ended up in: a Priority (<c>"recommended"</c> = agree with the
/// Recommended level) or a status. Reason as the /admin drawer asks for one.
/// </summary>
public sealed record SeedAction(string Request, string? Priority, string? Status, string? Reason)
{
    public const string Recommended = "recommended";
}

public sealed class SeedFileException(string message) : Exception(message);
