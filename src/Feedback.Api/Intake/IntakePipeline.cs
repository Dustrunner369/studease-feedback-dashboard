using Feedback.Api.Data;

namespace Feedback.Api.Intake;

/// <summary>
/// Where a Feature Request enters the system. Today it only saves the submit;
/// later slices enqueue it here for the background embed → shortlist → match → score steps.
/// </summary>
public sealed class IntakePipeline(FeedbackDbContext db)
{
    public async Task<FeatureRequest> SubmitAsync(ValidSubmission submission, CancellationToken ct)
    {
        var request = new FeatureRequest
        {
            Title = submission.Title,
            Description = submission.Description,
            Category = submission.Category,
            Email = submission.Email,
        };
        db.FeatureRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return request;
    }
}

/// <summary>A submit that has passed validation, with its text trimmed.</summary>
public sealed record ValidSubmission(string Title, string Description, string Category, string Email);
