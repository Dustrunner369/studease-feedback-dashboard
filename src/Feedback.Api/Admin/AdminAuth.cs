using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Feedback.Api.Admin;

/// <summary>Config section <c>Admin</c>. <see cref="Key"/> is a server secret (env var <c>Admin__Key</c> / user-secrets), never committed.</summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";
    public string Key { get; set; } = "";
}

/// <summary>
/// Sign-in with the admin key. The HttpOnly cookie holds an HMAC of a fixed label keyed by the admin key,
/// never the key itself: it survives restarts and every session ends when the key is rotated.
/// No key configured means nobody can sign in.
/// </summary>
public sealed class AdminAuth(IOptions<AdminOptions> options, IHostEnvironment env)
{
    public const string CookieName = "studease_admin";

    private string Key => options.Value.Key ?? "";

    public bool KeyMatches(string? key) =>
        Key.Length > 0 && key is not null &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(Key)));

    public bool IsSignedIn(HttpRequest request) =>
        Key.Length > 0 &&
        request.Cookies.TryGetValue(CookieName, out var value) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(value ?? ""), Encoding.ASCII.GetBytes(SessionValue()));

    public void SignIn(HttpResponse response) =>
        response.Cookies.Append(CookieName, SessionValue(), CookieOptions());

    public void SignOut(HttpResponse response) =>
        response.Cookies.Delete(CookieName, CookieOptions());

    private CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        // Strict keeps other sites from riding the session (no CSRF tokens needed for this demo).
        SameSite = SameSiteMode.Strict,
        Secure = !(env.IsDevelopment() || env.IsEnvironment("Testing")),
        Path = "/api/admin",
        MaxAge = TimeSpan.FromDays(7),
    };

    private string SessionValue() =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Key), "studease-admin-session-v1"u8))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
