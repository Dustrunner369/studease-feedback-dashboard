using Feedback.Api.Data;

namespace Feedback.Api.Intake;

/// <summary>
/// Where a Feature Request enters the system: saves the submit, then enqueues it for the
/// background embed → shortlist → match steps (<see cref="MatchingStep"/>), so the Requester never waits for the AI.
/// </summary>
public sealed class IntakePipeline(FeedbackDbContext db, IntakeQueue queue)
{
    /// <param name="submittedAt">Only <c>seed load</c> passes this, to backdate its synthetic requests; the portal uses now.</param>
    public async Task<FeatureRequest> SubmitAsync(ValidSubmission submission, CancellationToken ct, DateTimeOffset? submittedAt = null)
    {
        var request = new FeatureRequest
        {
            Title = submission.Title,
            Description = submission.Description,
            Category = submission.Category,
            Email = submission.Email,
        };
        if (submittedAt is { } at) request.CreatedAt = at;
        db.FeatureRequests.Add(request);
        await db.SaveChangesAsync(ct);
        queue.Enqueue(request.Id);
        return request;
    }
}

/// <summary>A submit that has passed validation, with its text trimmed.</summary>
public sealed record ValidSubmission(string Title, string Description, string Category, string Email);
