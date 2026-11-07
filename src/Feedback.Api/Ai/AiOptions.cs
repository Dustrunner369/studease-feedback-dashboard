namespace Feedback.Api.Ai;

/// <summary>The AI steps, as config keys under <c>Ai:Steps</c> and as logged in ai_calls.step.</summary>
public static class AiSteps
{
    public const string Embed = "Embed";
    public const string Match = "Match";
    public const string Score = "Score";
    public const string DraftUpdate = "DraftUpdate";
    public const string Brief = "Brief";
    public const string PickFiles = "PickFiles";
    public const string WriteSolutions = "WriteSolutions";
}

/// <summary>Binds the <c>Ai</c> section: model and effort per step, prices per model, timeout.</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    public Dictionary<string, AiStepOptions> Steps { get; set; } = [];
    /// <summary>USD per 1M tokens, keyed by the configured model name.</summary>
    public Dictionary<string, AiPrice> Prices { get; set; } = [];
    /// <summary>Per-attempt network timeout; the package retries twice on top of it.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    public AiStepOptions Step(string step) =>
        Steps.TryGetValue(step, out var options) ? options
        : throw new InvalidOperationException($"Ai:Steps:{step} is not configured.");
}

public sealed class AiStepOptions
{
    public string Model { get; set; } = "";
    /// <summary>Reasoning effort (e.g. none, minimal, low, medium); empty for embeddings.</summary>
    public string? Effort { get; set; }
    /// <summary>Sent only when Effort is none (OpenAI rejects it otherwise).</summary>
    public float? Temperature { get; set; }
}

public sealed class AiPrice
{
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
}
