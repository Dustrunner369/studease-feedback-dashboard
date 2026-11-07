using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Feedback.Api.Portal;

/// <summary>Config section <c>Portal:RateLimit</c>: a fixed window per client IP across the portal API.</summary>
public sealed class PortalRateLimitOptions
{
    public const string Section = "Portal:RateLimit";
    public int PermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// The header a trusted proxy puts the real client IP in (appsettings: <c>Fly-Client-IP</c>, which Fly's proxy
    /// always overwrites). Set it only when every request comes through that proxy; empty = not behind one, so
    /// only the connection's own address counts (a client could spoof the header).
    /// </summary>
    public string? ClientIpHeader { get; set; }
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
            limiter.AddPolicy(Policy, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientIp(http, options.ClientIpHeader),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                }));
        });
    }

    /// <summary>
    /// The trusted header's IP when configured and it parses; otherwise the connection's address
    /// (which, behind a proxy, is the proxy's).
    /// </summary>
    public static string ClientIp(HttpContext http, string? header)
    {
        if (!string.IsNullOrWhiteSpace(header)
            && IPAddress.TryParse(http.Request.Headers[header].ToString().Trim(), out var forwarded))
            return forwarded.ToString();
        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
