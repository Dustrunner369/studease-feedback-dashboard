namespace Feedback.Api.Scoring;

/// <summary>
/// The Priority Criteria, bound from the <c>PriorityCriteria</c> config section. Set once in the repo and
/// left alone (no editing, no versions). Also holds the deterministic arithmetic: Reach bands, score, level.
/// </summary>
public sealed class PriorityCriteria
{
    public const string Section = "PriorityCriteria";

    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";

    /// <summary>Studease's product goals; Strategic fit names the one a Problem serves best.</summary>
    public List<string> Goals { get; set; } = [];
    /// <summary>What "core task" means in the level descriptions.</summary>
    public string CoreTasks { get; set; } = "";
    public CriteriaWeights Weights { get; set; } = new();
    /// <summary>What each 1–5 rating means, per AI-rated criterion (index 0 = rating 1).</summary>
    public CriteriaLevels Levels { get; set; } = new();
    /// <summary>Reach points → rating, ascending by MinPoints.</summary>
    public List<ReachBand> ReachBands { get; set; } = [];
    public LevelCutOffs CutOffs { get; set; } = new();

    /// <summary>Reach points = Feature Requests × 3 + Upvotes.</summary>
    public static int ReachPoints(int featureRequests, int upvotes) => featureRequests * 3 + upvotes;

    public int ReachRating(int points) =>
        ReachBands.OrderBy(b => b.MinPoints).LastOrDefault(b => points >= b.MinPoints)?.Rating ?? 1;

    /// <summary>One criterion's share of the score: weight × (rating − 1) / 4.</summary>
    public static decimal Points(int weight, int rating) => weight * (rating - 1) / 4m;

    /// <summary>Σ weight × (rating − 1) / 4, rounded to a whole 0–100 score.</summary>
    public int Score(int strategicFit, int reach, int pain, int retentionRisk) => (int)Math.Round(
        Points(Weights.StrategicFit, strategicFit) + Points(Weights.Reach, reach)
        + Points(Weights.Pain, pain) + Points(Weights.RetentionRisk, retentionRisk),
        MidpointRounding.AwayFromZero);

    public string Level(int score) => score >= CutOffs.High ? High : score >= CutOffs.Medium ? Medium : Low;
}

/// <summary>The four criterion names as shown in /admin.</summary>
public static class Criterion
{
    public const string StrategicFit = "Strategic fit";
    public const string Reach = "Reach";
    public const string Pain = "Pain";
    public const string RetentionRisk = "Retention risk";
}

public sealed class CriteriaWeights
{
    public int StrategicFit { get; set; }
    public int Reach { get; set; }
    public int Pain { get; set; }
    public int RetentionRisk { get; set; }
}

public sealed class CriteriaLevels
{
    public List<string> StrategicFit { get; set; } = [];
    public List<string> Pain { get; set; } = [];
    public List<string> RetentionRisk { get; set; } = [];
}

public sealed class ReachBand
{
    public int MinPoints { get; set; }
    public int Rating { get; set; }
}

public sealed class LevelCutOffs
{
    public int High { get; set; }
    public int Medium { get; set; }
}
