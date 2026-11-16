using System;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace OpenForge.Infrastructure;

public sealed class SessionData
{
    public string? UserId { get; set; }
    public string? Csrf { get; set; }
    public string? OauthState { get; set; }
    public double OauthStarted { get; set; }
    public string? OauthNext { get; set; }
    public NativeAuthRequest? OauthNative { get; set; }
    public bool Permanent { get; set; }

    // Matches users.session_epoch at sign-in. Bumping the column signs the account out everywhere.
    public long Epoch { get; set; }
    public string? PasskeyChallenge { get; set; }
    public string? PasskeyPurpose { get; set; }
    public double PasskeyStarted { get; set; }

    public bool IsEmpty => UserId == null && Csrf == null && OauthState == null && OauthNative == null && OauthNext == null && PasskeyChallenge == null;
}

public sealed record NativeAuthRequest(string RedirectUri, string CodeChallenge, string CodeChallengeMethod, string State);

public sealed class SessionStore
{
    // Same cookie name as Flask so clients that store it keep working. Old Flask cookies just fail to decrypt.
    public const string CookieName = "session";

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private readonly IDataProtector _protector;
    private readonly ForgeOptions _options;

    public SessionStore(IDataProtectionProvider provider, ForgeOptions options)
    {
        _protector = provider.CreateProtector("OpenForge.Session.v1");
        _options = options;
    }

    public SessionData Load(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out string? value) || string.IsNullOrEmpty(value))
            return new SessionData();

        try
        {
            return JsonSerializer.Deserialize<SessionData>(_protector.Unprotect(value)) ?? new SessionData();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return new SessionData();
        }
    }

    public void Save(HttpResponse response, SessionData session)
    {
        var cookie = new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = _options.CookieSecure,
            SameSite = SameSiteMode.Lax,
        };

        if (session.IsEmpty)
        {
            response.Cookies.Delete(CookieName, cookie);
            return;
        }

        cookie.Expires = session.Permanent ? DateTimeOffset.UtcNow.Add(Lifetime) : null;
        response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(session)), cookie);
    }
}
