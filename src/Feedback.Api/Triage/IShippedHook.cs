namespace Feedback.Api.Triage;

/// <summary>
/// Runs after a Problem has been saved as Shipped (by <see cref="ProblemStatusChange"/>), e.g. the Outbox
/// drafting one update per Requester email. Register with <c>AddScoped&lt;IShippedHook, ...&gt;()</c>;
/// every registered hook runs, in registration order. A hook must not throw for an AI failure.
/// </summary>
public interface IShippedHook
{
    Task OnShippedAsync(long problemId, CancellationToken ct);
}
