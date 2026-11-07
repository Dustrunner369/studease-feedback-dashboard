using System.Threading.Channels;

namespace Feedback.Api.Intake;

/// <summary>
/// In-memory queue of Feature Request ids waiting for the background AI steps.
/// Not durable: after a restart, anything still queued stays unmatched (needs review).
/// </summary>
public sealed class IntakeQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private int _pending;
    private TaskCompletionSource _idle = Completed();

    public void Enqueue(long featureRequestId)
    {
        lock (_lock)
        {
            if (_pending++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _channel.Writer.TryWrite(featureRequestId);
    }

    /// <summary>Completes once every enqueued request has been fully processed (tests, and <c>seed load</c> between submits).</summary>
    public Task WhenIdleAsync()
    {
        lock (_lock) return _idle.Task;
    }

    internal IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    /// <summary>Called by the worker after each request, whether it succeeded or not.</summary>
    internal void MarkDone()
    {
        lock (_lock)
        {
            if (--_pending == 0) _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetResult();
        return tcs;
    }
}
