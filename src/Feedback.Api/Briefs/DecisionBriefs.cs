using System.Text.Json;
using Feedback.Api.Ai;
using Feedback.Api.Data;
using Feedback.Api.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Feedback.Api.Briefs;

/// <summary>
/// Writes and reads a Problem's Decision Brief. Only Generate brief / Regenerate writes one (one AI call);
/// code checks every claim's citations and fills Who's asking. Only the latest brief is kept, and a failed
/// call keeps the previous one.
/// </summary>
public sealed class DecisionBriefs(FeedbackDbContext db, IAiGateway ai, AiCallLog log, IOptions<PriorityCriteria> criteria)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The fixed sections in order. Who's asking holds no claims: code fills it from the counts.</summary>
    public static readonly IReadOnlyList<(string Key, string Title)> Sections =
    [
        ("need", "The need"),
        ("whoIsAsking", "Who's asking"),
        ("askingFor", "What they're asking for"),
        ("painAndImpact", "Pain and impact"),
        ("openQuestions", "Disagreements and open questions"),
    ];

    /// <summary>Writes a new brief for the Problem. False if the AI call failed (logged; any previous brief is kept).</summary>
    public async Task<bool> GenerateAsync(long problemId, CancellationToken ct)
    {
        var problem = await db.Problems.AsNoTracking().SingleAsync(p => p.Id == problemId, ct);
        var requests = await db.FeatureRequests.AsNoTracking().Where(r => r.ProblemId == problemId)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new BriefRequest(r.Id, r.Title, r.Description, r.Category, r.CreatedAt,
                db.Upvotes.Count(u => u.FeatureRequestId == r.Id)))
            .ToListAsync(ct);
        var input = new BriefInput(new ScoreProblem(problem.Title, problem.Description, problem.Category), requests, criteria.Value.Goals);

        var result = await log.RunLoggedAsync(AiSteps.Brief, new AiCallFor(ProblemId: problemId),
            call => ai.WriteBriefAsync(input, call), ct,
            check: form => AllClaims(form).Any() ? null : "The brief has no claims.",
            noteFor: form => Note(Check(form, requests)));
        if (result.Value is not { } written) return false;

        var content = new StoredBrief(WhoIsAsking(requests), Check(written, requests));
        var brief = await db.DecisionBriefs.SingleOrDefaultAsync(b => b.ProblemId == problemId, ct);
        if (brief is null) db.DecisionBriefs.Add(brief = new DecisionBrief { ProblemId = problemId, Content = "" });
        brief.Content = JsonSerializer.Serialize(content, Json);
        brief.CoveredFeatureRequestIds = requests.Select(r => r.Id).Order().ToArray();
        brief.AiCallId = result.CallId;
        brief.CreatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The Problem's brief as the drawer shows it (null if none), checked against its current Feature Requests.</summary>
    public static async Task<DecisionBriefView?> ViewAsync(
        FeedbackDbContext db, long problemId, IReadOnlyCollection<long> currentIds, CancellationToken ct)
    {
        var brief = await db.DecisionBriefs.AsNoTracking().SingleOrDefaultAsync(b => b.ProblemId == problemId, ct);
        if (brief is null) return null;
        var content = JsonSerializer.Deserialize<StoredBrief>(brief.Content, Json)!;
        var covered = brief.CoveredFeatureRequestIds.ToHashSet();
        var current = currentIds.ToHashSet();

        var citedIds = content.Sections.SelectMany(s => s.Claims).SelectMany(c => c.FeatureRequestIds).Distinct().ToList();
        var cited = await db.FeatureRequests.AsNoTracking().Where(r => citedIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Title, r.Description }).ToDictionaryAsync(r => r.Id, ct);

        CitationView Citation(long id)
        {
            var state = !covered.Contains(id) ? CitationStates.Unverified
                : !current.Contains(id) ? CitationStates.Moved
                : CitationStates.Ok;
            var request = cited.GetValueOrDefault(id);
            return new CitationView(id, request?.Title, request?.Description, state);
        }

        var sections = Sections.Select(s =>
        {
            var claims = content.Sections.FirstOrDefault(c => c.Key == s.Key)?.Claims ?? [];
            return new BriefSectionView(s.Key, s.Title, claims
                .Select(c => new BriefClaimView(c.Text, c.Quote, c.Verified, c.Problem, c.FeatureRequestIds.Select(Citation).ToList()))
                .ToList());
        }).ToList();
        return new DecisionBriefView(brief.CreatedAt, !covered.SetEquals(current), content.WhoIsAsking, sections);
    }

    private static IEnumerable<BriefClaim> AllClaims(BriefForm f) => f.Need.Concat(f.AskingFor).Concat(f.PainAndImpact).Concat(f.OpenQuestions);

    // Every cited ID must be one of the Problem's Feature Requests, and a quote must appear exactly in one of them.
    private static List<StoredSection> Check(BriefForm form, IReadOnlyList<BriefRequest> requests)
    {
        var byId = requests.ToDictionary(r => r.Id);
        StoredClaim CheckClaim(BriefClaim claim)
        {
            var ids = claim.FeatureRequestIds.Distinct().ToList();
            var quote = string.IsNullOrWhiteSpace(claim.Quote) ? null : claim.Quote.Trim();
            string? problem = null;
            if (ids.Count == 0) problem = "Cites no Feature Request.";
            else if (ids.Where(id => !byId.ContainsKey(id)).Select(id => (long?)id).FirstOrDefault() is { } stray)
                problem = $"Cites #{stray}, which isn't one of this Problem's Feature Requests.";
            else if (quote is not null && !ids.Any(id =>
                         byId[id].Title.Contains(quote, StringComparison.Ordinal) || byId[id].Description.Contains(quote, StringComparison.Ordinal)))
                problem = "The quote doesn't appear in the cited Feature Requests.";
            return new StoredClaim(claim.Text.Trim(), ids, quote, problem is null, problem);
        }

        return
        [
            new("need", form.Need.Select(CheckClaim).ToList()),
            new("askingFor", form.AskingFor.Select(CheckClaim).ToList()),
            new("painAndImpact", form.PainAndImpact.Select(CheckClaim).ToList()),
            new("openQuestions", form.OpenQuestions.Select(CheckClaim).ToList()),
        ];
    }

    private static string? Note(List<StoredSection> sections)
    {
        var claims = sections.SelectMany(s => s.Claims).ToList();
        var unverified = claims.Count(c => !c.Verified);
        return unverified == 0 ? null : $"{unverified} of {claims.Count} claims have an unverified citation.";
    }

    private static WhoIsAskingView WhoIsAsking(IReadOnlyList<BriefRequest> requests) => new(
        requests.Count,
        requests.Sum(r => r.Upvotes),
        requests.Count == 0 ? null : requests.Min(r => r.CreatedAt),
        requests.Count == 0 ? null : requests.Max(r => r.CreatedAt),
        requests.GroupBy(r => r.Category).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CategoryPickView(g.Key, g.Count())).ToList());

    private sealed record StoredBrief(WhoIsAskingView WhoIsAsking, IReadOnlyList<StoredSection> Sections);
    private sealed record StoredSection(string Key, IReadOnlyList<StoredClaim> Claims);
    private sealed record StoredClaim(string Text, IReadOnlyList<long> FeatureRequestIds, string? Quote, bool Verified, string? Problem);
}

/// <summary>A citation chip's state: ok; ⚠ unverified citation (not on the Problem when written); ⚠ no longer on this Problem.</summary>
public static class CitationStates
{
    public const string Ok = "ok";
    public const string Unverified = "unverified";
    public const string Moved = "moved";
}

/// <summary>
/// The drawer's Decision Brief. OutOfDate: the Problem's Feature Requests differ from those it covered (offer Regenerate).
/// Sections are the five fixed ones in order; Who's asking has no claims (see WhoIsAsking).
/// </summary>
public sealed record DecisionBriefView(
    DateTimeOffset WrittenAt, bool OutOfDate, WhoIsAskingView WhoIsAsking, IReadOnlyList<BriefSectionView> Sections);

/// <summary>Who's asking, filled by code when the brief was written. CategoryPicks are the Requesters' own picks, most first.</summary>
public sealed record WhoIsAskingView(
    int FeatureRequests, int Upvotes, DateTimeOffset? FirstAt, DateTimeOffset? LastAt, IReadOnlyList<CategoryPickView> CategoryPicks);

public sealed record CategoryPickView(string Category, int Count);

public sealed record BriefSectionView(string Key, string Title, IReadOnlyList<BriefClaimView> Claims);

/// <summary>A claim; Verified false = ⚠ unverified citation, with Problem saying why.</summary>
public sealed record BriefClaimView(string Text, string? Quote, bool Verified, string? Problem, IReadOnlyList<CitationView> Citations);

/// <summary>A citation chip: opens the Feature Request's original text (Title/Description null if no such request).</summary>
public sealed record CitationView(long FeatureRequestId, string? Title, string? Description, string State);
