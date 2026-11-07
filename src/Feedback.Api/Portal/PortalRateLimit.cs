using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Feedback.Api.Portal;

/// <summary>Config section <c>Portal:RateLimit</c>: a fixed window per client IP across the portal API.</summary>
public sealed class PortalRateLimitOptions
{
    public const string Section = "Portal:RateLimit";
    public int PermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;
}

public static class PortalRateLimit
{
    public const string Policy = "portal";

    public static IServiceCollection AddPortalRateLimit(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection(PortalRateLimitOptions.Section).Get<PortalRateLimitOptions>() ?? new();
        return services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // RemoteIpAddress is the client only when nothing proxies us; deployment must
            // add forwarded-header handling for its proxy (e.g. Fly-Client-IP) before relying on this.
            limiter.AddPolicy(Policy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                }));
        });
    }
}
