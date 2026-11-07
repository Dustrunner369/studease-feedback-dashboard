namespace Feedback.Api.Intake;

/// <summary>Drains the <see cref="IntakeQueue"/> one Feature Request at a time, each in its own DI scope.</summary>
public sealed class IntakeWorker(IntakeQueue queue, IServiceScopeFactory scopes, ILogger<IntakeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<MatchingStep>().RunAsync(id, stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    // A broken step must never stop the queue; the request stays unmatched (needs review).
                    logger.LogError(e, "Intake failed for Feature Request {Id}", id);
                }
                finally
                {
                    queue.MarkDone();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
