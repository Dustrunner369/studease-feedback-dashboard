namespace Feedback.Api.Tests.Portal;

/// <summary>A Feature Request as the public portal API returns it.</summary>
public sealed record FeatureRequestJson(long Id, string Title, string Description, string Category, DateTimeOffset CreatedAt);
