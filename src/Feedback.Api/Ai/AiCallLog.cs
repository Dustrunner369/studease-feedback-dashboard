using System.Diagnostics;
using Feedback.Api.Data;
using Microsoft.Extensions.Options;

namespace Feedback.Api.Ai;

/// <summary>What an AI call was for (ai_calls has no foreign keys, so these survive deletes).</summary>
public sealed record AiCallFor(long? ProblemId = null, long? FeatureRequestId = null);

/// <summary>
/// Runs one gateway call and writes its ai_calls row: OK or Failed (with kind and message),
/// model as reported, tokens, cost (from Ai:Prices for the step's configured model) and latency.
/// The package's automatic retries fold into the one row; a later Retry makes a new row.
/// </summary>
public sealed class AiCallLog(FeedbackDbContext db, IOptions<AiOptions> options, ILogger<AiCallLog> logger)
{
    /// <param name="check">
    /// Optional check of the parsed answer beyond the strict form (e.g. "the id must be on the shortlist").
    /// Returning a message fails the call as Invalid output. No retry either way.
    /// </param>
    /// <param name="noteFor">Optional note worked out from an accepted answer (e.g. "2 claims unverified"); the call stays OK.</param>
    /// <returns>The answer, or null if the call failed (the failure is logged, never thrown).</returns>
    public async Task<T?> RunAsync<T>(
        string step, AiCallFor @for, Func<CancellationToken, Task<AiReply<T>>> call,
        CancellationToken ct, Func<T, string?>? check = null, string? note = null, Func<T, string?>? noteFor = null) where T : class =>
        (await RunLoggedAsync(step, @for, call, ct, check, note, noteFor)).Value;

    /// <summary>As <see cref="RunAsync{T}"/>, also returning the ai_calls row id (for rows that point at their call).</summary>
    public async Task<AiCallResult<T>> RunLoggedAsync<T>(
        string step, AiCallFor @for, Func<CancellationToken, Task<AiReply<T>>> call,
        CancellationToken ct, Func<T, string?>? check = null, string? note = null, Func<T, string?>? noteFor = null) where T : class
    {
        var row = new AiCall
        {
            Step = step,
            Status = AiCallStatus.Ok,
            ProblemId = @for.ProblemId,
            FeatureRequestId = @for.FeatureRequestId,
            Note = note,
        };
        var watch = Stopwatch.StartNew();
        T? result = null;
        try
        {
            var reply = await call(ct);
            row.Model = reply.Model;
            row.InputTokens = reply.InputTokens;
            row.OutputTokens = reply.OutputTokens;
            row.CostUsd = Cost(step, reply.InputTokens, reply.OutputTokens);
            var problem = check?.Invoke(reply.Value);
            if (problem is null)
            {
                result = reply.Value;
                if (noteFor?.Invoke(result) is { } extra) row.Note = row.Note is null ? extra : $"{row.Note} {extra}";
            }
            else Fail(row, AiFailureKind.InvalidOutput, problem);
        }
        catch (AiCallException e)
        {
            row.Model ??= e.Model;
            Fail(row, e.Kind, e.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Fail(row, AiFailureKind.ApiError, e.Message);
        }
        row.LatencyMs = (int)watch.ElapsedMilliseconds;

        if (row.Status == AiCallStatus.Failed)
            logger.LogWarning("AI step {Step} failed ({Kind}): {Message}", step, row.FailureKind, row.ErrorMessage);
        db.AiCalls.Add(row);
        await db.SaveChangesAsync(ct);
        return new AiCallResult<T>(result, row.Id);
    }

    private static void Fail(AiCall row, string kind, string message)
    {
        row.Status = AiCallStatus.Failed;
        row.FailureKind = kind;
        row.ErrorMessage = message.Length > 2000 ? message[..2000] : message;
    }

    private decimal? Cost(string step, int input, int output)
    {
        var config = options.Value;
        if (!config.Steps.TryGetValue(step, out var s) || !config.Prices.TryGetValue(s.Model, out var price)) return null;
        return (input * price.InputPerMillion + output * price.OutputPerMillion) / 1_000_000m;
    }
}

/// <summary>The answer (null if the call failed) and the id of the ai_calls row written for it.</summary>
public sealed record AiCallResult<T>(T? Value, long CallId) where T : class;
