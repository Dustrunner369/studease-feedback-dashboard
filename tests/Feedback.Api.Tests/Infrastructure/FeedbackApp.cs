using Feedback.Api.Ai;
using Feedback.Api.Data;
using Feedback.Api.GitHub;
using Feedback.Api.Intake;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Feedback.Api.Tests.Infrastructure;

/// <summary>
/// The whole app booted in-process against the test container.
/// Pass extra settings (e.g. a low rate limit) to override configuration.
/// The AI and GitHub gateways are always the in-memory <see cref="Ai"/> and <see cref="GitHub"/> fakes.
/// </summary>
public sealed class FeedbackApp(FeedbackDatabase database, IDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    /// <summary>The admin key the test app is configured with (a server secret in production).</summary>
    public const string AdminKey = "test-admin-key-0123456789";

    public FakeAiGateway Ai { get; } = new();
    public FakeGitHubGateway GitHub { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Feedback", database.ConnectionString);
        // Generous by default so tests never trip it by accident.
        builder.UseSetting("Portal:RateLimit:PermitLimit", "10000");
        builder.UseSetting("Admin:Key", AdminKey);
        builder.UseSetting("GitHub:Repo", FakeGitHubGateway.ConfiguredRepo);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAiGateway>();
            services.AddSingleton<IAiGateway>(Ai);
            services.RemoveAll<IGitHubGateway>();
            services.AddSingleton<IGitHubGateway>(GitHub);
        });
    }

    /// <summary>Test-only hook: waits until the background intake queue has finished every submit so far.</summary>
    public Task WhenIntakeIdleAsync() =>
        Services.GetRequiredService<IntakeQueue>().WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));

    /// <summary>Applies the app's migrations, as a deploy would.</summary>
    public async Task MigrateAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FeedbackDbContext>().Database.MigrateAsync();
    }
}
