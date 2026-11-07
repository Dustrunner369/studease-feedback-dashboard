namespace Feedback.Api.GitHub;

/// <summary>The <c>GitHub</c> config section. The token is a secret (<c>GitHub:Token</c>), read only by <see cref="GitHubApiGateway"/>.</summary>
public sealed class GitHubOptions
{
    public const string Section = "GitHub";

    /// <summary>The connected repo as <c>owner/name</c>. GitHub may report a newer name after a rename; that one is used for every call.</summary>
    public string Repo { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 20;
}
