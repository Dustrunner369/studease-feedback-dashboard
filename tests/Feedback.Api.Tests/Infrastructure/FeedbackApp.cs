using Feedback.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Feedback.Api.Tests.Infrastructure;

/// <summary>
/// The whole app booted in-process against the test container.
/// Pass extra settings (e.g. a low rate limit) to override configuration.
/// </summary>
public sealed class FeedbackApp(FeedbackDatabase database, IDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Feedback", database.ConnectionString);
        // Generous by default so tests never trip it by accident.
        builder.UseSetting("Portal:RateLimit:PermitLimit", "10000");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);
    }

    /// <summary>Applies the app's migrations, as a deploy would.</summary>
    public async Task MigrateAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FeedbackDbContext>().Database.MigrateAsync();
    }
}
