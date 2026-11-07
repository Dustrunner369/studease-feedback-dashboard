using Feedback.Api.Data;
using Feedback.Api.Domain;
using Feedback.Api.Intake;
using Feedback.Api.Scoring;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Triage;

/// <summary>
/// The product team fixing the AI's grouping: merge Problems, and move Feature Requests to a new or existing
/// Problem (also "Match by hand" for a request that needs review). Each writes a decision_log row in the same
/// save, deletes any Problem left with no Feature Requests, then re-scores every surviving Problem it touched.
/// Upvotes belong to Feature Requests, so they move with them. Only open Problems take part.
/// </summary>
public sealed class Regrouping(FeedbackDbContext db, PriorityScorer scorer)
{
    public const string MovedReason = "Moved here by the product team.";

    public async Task<RegroupResult<long>> MergeAsync(MergeRequest merge, CancellationToken ct)
    {
        var ids = merge.ProblemIds ?? [];
        var errors = new Dictionary<string, string[]>();
        if (ids.Length < 2 || ids.Distinct().Count() != ids.Length) errors["problemIds"] = ["Pick two or more different Problems."];
        else if (!ids.Contains(merge.KeepId)) errors["keepId"] = ["Keep one of the Problems being merged."];
        foreach (var (field, from) in new[] { ("titleFrom", merge.TitleFrom), ("categoryFrom", merge.CategoryFrom),
                     ("statusFrom", merge.StatusFrom), ("priorityFrom", merge.PriorityFrom) })
            if (from is { } id && !ids.Contains(id)) errors[field] = ["Pick one of the Problems being merged."];
        if (errors.Count > 0) return RegroupResult<long>.Invalid(errors);

        var problems = await db.Problems.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        if (problems.Count != ids.Length) return RegroupResult<long>.Invalid(new() { ["problemIds"] = ["A Problem no longer exists."] });
        if (problems.Values.Any(p => ProblemStatuses.IsClosed(p.Status)))
            return RegroupResult<long>.Invalid(new() { ["problemIds"] = ["Only open Problems can be merged."] });

        var kept = problems[merge.KeepId];
        var removed = ids.Where(id => id != kept.Id).Select(id => problems[id]).ToList();
        var removedIds = removed.Select(p => p.Id).ToList();

        var picked = new
        {
            title = problems[merge.TitleFrom ?? kept.Id].Title,
            description = problems[merge.TitleFrom ?? kept.Id].Description,
            category = problems[merge.CategoryFrom ?? kept.Id].Category,
            status = problems[merge.StatusFrom ?? kept.Id].Status,
            priority = problems[merge.PriorityFrom ?? kept.Id].Priority,
            priorityOverrideReason = problems[merge.PriorityFrom ?? kept.Id].PriorityOverrideReason,
        };
        var moved = await db.FeatureRequests.Where(r => r.ProblemId != null && removedIds.Contains(r.ProblemId.Value)).ToListAsync(ct);

        DecisionLog.Add(db, kept.Id, DecisionKinds.Merge, null, new
        {
            removed = removed.Select(p => new { id = p.Id, title = p.Title }),
            kept = new { picked.title, picked.category, picked.status, picked.priority },
            featureRequestIds = moved.Select(r => r.Id),
        });
        (kept.Title, kept.Description, kept.Category, kept.Status) = (picked.title, picked.description, picked.category, picked.status);
        (kept.Priority, kept.PriorityOverrideReason) = (picked.priority, picked.priorityOverrideReason);
        foreach (var request in moved) request.ProblemId = kept.Id;
        await db.SaveChangesAsync(ct);
        await DeleteAsync(removed, ct);

        await scorer.RescoreAsync(kept.Id, ct);
        return RegroupResult<long>.Ok(kept.Id);
    }

    public async Task<RegroupResult<MoveResult>> MoveAsync(MoveRequest move, CancellationToken ct)
    {
        var ids = (move.FeatureRequestIds ?? []).Distinct().ToArray();
        var errors = new Dictionary<string, string[]>();
        if (ids.Length == 0) errors["featureRequestIds"] = ["Pick at least one Feature Request."];
        if ((move.ToProblemId is null) == (move.NewProblem is null)) errors["to"] = ["Move to an existing Problem or a new one."];

        var newTitle = move.NewProblem?.Title?.Trim();
        var newDescription = move.NewProblem?.Description?.Trim();
        if (move.NewProblem is { } np)
        {
            if (string.IsNullOrEmpty(newTitle)) errors["newProblem.title"] = ["Give the new Problem a title."];
            else if (newTitle.Length > SubmissionValidator.TitleMax)
                errors["newProblem.title"] = [$"Keep the title to {SubmissionValidator.TitleMax} characters."];
            if (newDescription?.Length > SubmissionValidator.DescriptionMax)
                errors["newProblem.description"] = [$"Keep it to {SubmissionValidator.DescriptionMax} characters."];
            if (!Categories.IsValid(np.Category ?? "")) errors["newProblem.category"] = ["Unknown Category."];
        }
        if (errors.Count > 0) return RegroupResult<MoveResult>.Invalid(errors);

        var requests = await db.FeatureRequests.Include(r => r.Problem).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        if (requests.Count != ids.Length)
            return RegroupResult<MoveResult>.Invalid(new() { ["featureRequestIds"] = ["A Feature Request no longer exists."] });
        if (requests.Any(r => r.Problem is { } p && ProblemStatuses.IsClosed(p.Status)))
            return RegroupResult<MoveResult>.Invalid(new() { ["featureRequestIds"] = ["Only requests of open Problems can be moved."] });

        Problem target;
        if (move.ToProblemId is { } toId)
        {
            var existing = await db.Problems.SingleOrDefaultAsync(p => p.Id == toId, ct);
            if (existing is null || ProblemStatuses.IsClosed(existing.Status))
                return RegroupResult<MoveResult>.Invalid(new() { ["toProblemId"] = ["Move to an open Problem."] });
            target = existing;
        }
        else
        {
            target = new Problem
            {
                Title = newTitle!,
                Description = string.IsNullOrEmpty(newDescription) ? newTitle! : newDescription,
                Category = move.NewProblem!.Category!,
                Status = ProblemStatuses.New,
            };
            db.Problems.Add(target);
        }

        // Requests already on the target stay put.
        var moving = move.ToProblemId is { } to ? requests.Where(r => r.ProblemId != to).ToList() : requests;
        var origin = moving.ToDictionary(r => r.Id, r => r.ProblemId);
        var sources = moving.Where(r => r.Problem is not null).Select(r => r.Problem!).DistinctBy(p => p.Id).ToList();
        foreach (var request in moving)
        {
            request.Problem = target;
            request.MatchReason = MovedReason;
        }
        await db.SaveChangesAsync(ct);

        var movedIds = moving.Select(r => r.Id).ToList();
        DecisionLog.Add(db, target.Id, DecisionKinds.Move, null, new
        {
            featureRequestIds = movedIds,
            from = sources.Select(p => new { id = p.Id, title = p.Title }),
            to = new { id = target.Id, title = target.Title },
            newProblem = move.NewProblem is not null,
        });
        var sourceIds = sources.Select(p => p.Id).ToList();
        var stillUsed = await db.FeatureRequests.Where(r => r.ProblemId != null && sourceIds.Contains(r.ProblemId.Value))
            .Select(r => r.ProblemId!.Value).Distinct().ToListAsync(ct);
        var emptied = sources.Where(p => !stillUsed.Contains(p.Id)).ToList();
        var survivors = sources.Except(emptied).ToList();
        foreach (var source in survivors)
            DecisionLog.Add(db, source.Id, DecisionKinds.Move, null, new
            {
                featureRequestIds = movedIds.Where(id => origin[id] == source.Id).ToList(),
                from = new[] { new { id = source.Id, title = source.Title } },
                to = new { id = target.Id, title = target.Title },
                newProblem = move.NewProblem is not null,
            });
        await db.SaveChangesAsync(ct);
        await DeleteAsync(emptied, ct);

        if (movedIds.Count > 0) await scorer.RescoreAsync(target.Id, ct);
        foreach (var source in survivors) await scorer.RescoreAsync(source.Id, ct);
        return RegroupResult<MoveResult>.Ok(new MoveResult(target.Id, emptied.Select(p => p.Id).ToList()));
    }

    // A Problem merged away or left with no Feature Requests is deleted (not archived). Its decision_log rows
    // stay with problem_id set null; its priority_scorings cascade.
    private async Task DeleteAsync(IReadOnlyList<Problem> problems, CancellationToken ct)
    {
        if (problems.Count == 0) return;
        foreach (var p in problems) (p.CurrentScoringId, p.FirstScoringId, p.CurrentScoring) = (null, null, null);
        await db.SaveChangesAsync(ct);
        // Decision Briefs and Suggest Solutions (later tickets): delete the removed Problems' decision_briefs and
        // solution_suggestions rows here (or give their problem_id FK ON DELETE CASCADE).
        db.Problems.RemoveRange(problems);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// POST /problems/merge. KeepId is the Problem that survives (its id, decision log and GitHub issue);
/// each *From picks which merged Problem that field comes from (default: the kept one). TitleFrom also brings the description.
/// </summary>
public sealed record MergeRequest(
    long[]? ProblemIds, long KeepId, long? TitleFrom = null, long? CategoryFrom = null, long? StatusFrom = null, long? PriorityFrom = null);

/// <summary>POST /feature-requests/move: to an existing Problem (ToProblemId) or a new one (NewProblem), not both.</summary>
public sealed record MoveRequest(long[]? FeatureRequestIds, long? ToProblemId, NewProblemRequest? NewProblem);

/// <summary>A Problem the product team creates by moving requests to it. Description defaults to the title.</summary>
public sealed record NewProblemRequest(string? Title, string? Description, string? Category);

/// <summary>Where the requests went, and which Problems were left empty and deleted.</summary>
public sealed record MoveResult(long ProblemId, IReadOnlyList<long> RemovedProblemIds);

public sealed record RegroupResult<T>(T? Value, Dictionary<string, string[]>? Errors)
{
    public static RegroupResult<T> Ok(T value) => new(value, null);
    public static RegroupResult<T> Invalid(Dictionary<string, string[]> errors) => new(default, errors);
}
