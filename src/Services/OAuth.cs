using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

public sealed class OAuthAppRow
{
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? HomepageUrl { get; set; }
    public string? Logo { get; set; }
    public string RedirectUris { get; set; } = "[]";
    public int PublicClient { get; set; }
    public string? VerifiedAt { get; set; }
    public string? VerifiedBy { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public bool Verified => VerifiedAt != null;
    public bool IsPublic => PublicClient != 0;
    public List<string> Redirects => JsonBody.StringList(RedirectUris);
}

public sealed class OAuthSecretRow
{
    public string Id { get; set; } = "";
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string? LastUsedAt { get; set; }
}

// What a bearer token resolved to: the account, the app acting for it, and what the person allowed.
public sealed record OAuthCaller(string UserId, string AppId, HashSet<string> Scopes);

public sealed record OAuthScope(string Key, string Label, bool VerifiedOnly);

// OAuth2 for apps that creators register under Settings -> Developer -> Applications. People sign in to those apps
// with their account through /oauth/authorize; the apps then call the API with short-lived bearer tokens.
public static partial class OAuth
{
    public const string AccessPrefix = "mfat_";
    public const string RefreshPrefix = "mfrt_";
    public const string SecretPrefix = "mfcs_";
    public const double CodeSeconds = 60;
    public const double AccessSeconds = 3600;
    public const double RefreshSeconds = 60 * 24 * 3600;
    public const int MaxAppsPerUser = 10;
    public const int MaxSecretsPerApp = 5;
    public const int MaxRedirectUris = 10;

    // identify is always granted. account is the whole API (bar the areas in AccountDenied) and only verified apps may ask for it,
    // because staff have checked who is behind them.
    public static readonly OAuthScope[] Scopes =
    [
        new("identify", "See your username, display name, avatar and account ID", false),
        new("email", "See your email address", false),
        new("publish", "Publish releases to mods you manage", false),
        new("account", "Use your account on your behalf: install and manage mods, purchases, friends and devices", true),
    ];

    // Even an account-scoped token can't change how the person signs in, mint more credentials, move money or reach staff tools.
    private static readonly string[] AccountDenied =
    [
        "/api/v1/admin", "/api/v1/setup", "/api/v1/auth", "/api/v1/account", "/api/v1/passkeys", "/api/v1/api-keys",
        "/api/v1/oauth", "/api/v1/payouts", "/api/v1/logout",
    ];

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{43}\z")]
    public static partial Regex Challenge();

    [GeneratedRegex(@"\A[A-Za-z0-9._~-]{43,128}\z")]
    public static partial Regex Verifier();

    // RFC 8252 private-use schemes are reverse domain names, such as com.example.app:/callback.
    [GeneratedRegex(@"\A[a-z][a-z0-9+-]*(?:\.[a-z0-9+-]+)+\z")]
    private static partial Regex PrivateScheme();

    public static OAuthScope? Scope(string key)
    {
        return Scopes.FirstOrDefault(s => s.Key == key);
    }

    // Parses a space-separated scope list, always including identify. Returns null when it names an unknown scope.
    public static SortedSet<string>? ParseScopes(string? value)
    {
        var scopes = new SortedSet<string>(StringComparer.Ordinal) { "identify" };

        foreach (string key in (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Scope(key) == null)
                return null;

            scopes.Add(key);
        }

        return scopes;
    }

    public static string Join(IEnumerable<string> scopes)
    {
        return string.Join(' ', scopes.OrderBy(s => s, StringComparer.Ordinal));
    }

    private static bool IsLoopbackHost(string host)
    {
        return host is "127.0.0.1" or "[::1]" or "localhost";
    }

    // The rules for a redirect URI an app registers. Returns a reason when it can't be used.
    public static string? RedirectProblem(string uri)
    {
        if (uri.Length is 0 or > 2048 || uri.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return "Redirect URIs must be a single line of at most 2048 characters.";

        if (uri.Contains('#'))
            return "Redirect URIs can't contain a # fragment.";

        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
            return $"{uri} isn't an absolute URL.";

        if (parsed.UserInfo != "")
            return "Redirect URIs can't contain a username or password.";

        if (parsed.Scheme == "https")
            return parsed.Host == "" ? $"{uri} needs a host name." : null;

        if (parsed.Scheme == "http")
            return IsLoopbackHost(parsed.Host) ? null : "Plain http:// is only allowed for 127.0.0.1, [::1] or localhost. Use https:// for anything else.";

        if (PrivateScheme().IsMatch(parsed.Scheme))
            return null;

        return "Use https://, http:// on a loopback address, or a reverse-domain app scheme such as com.example.app:/callback.";
    }

    // Exact match, except that a loopback http redirect may use any port: desktop apps pick a free one each time (RFC 8252 7.3).
    public static bool RedirectMatches(IEnumerable<string> registered, string requested)
    {
        if (requested.Length > 2048)
            return false;

        foreach (string uri in registered)
        {
            if (string.Equals(uri, requested, StringComparison.Ordinal))
                return true;

            if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? a) && Uri.TryCreate(requested, UriKind.Absolute, out Uri? b)
                && a.Scheme == "http" && b.Scheme == "http" && IsLoopbackHost(a.Host) && a.Host == b.Host
                && a.AbsolutePath == b.AbsolutePath && a.Query == b.Query && !requested.Contains('#') && b.UserInfo == "")
                return true;
        }

        return false;
    }

    public static string S256(string verifier)
    {
        return Text.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    public sealed record IssuedTokens(string AccessToken, string RefreshToken, string Scope);

    // Issues a fresh access and refresh token pair for a grant. The caller has already checked the client and the grant.
    public static IssuedTokens Issue(Db db, string grantId, string scope, long epoch)
    {
        string access = AccessPrefix + Text.UrlToken(32);
        string refresh = RefreshPrefix + Text.UrlToken(32);
        double now = Text.NowEpoch();

        db.Exec("DELETE FROM oauth_tokens WHERE expires_at<=@now", new { now });
        db.Exec("INSERT INTO oauth_tokens(token_hash,kind,grant_id,scope,epoch,created,expires_at) VALUES(@hash,'access',@grantId,@scope,@epoch,@now,@expires)",
            new { hash = Text.Sha256Hex(access), grantId, scope, epoch, now, expires = now + AccessSeconds });
        db.Exec("INSERT INTO oauth_tokens(token_hash,kind,grant_id,scope,epoch,created,expires_at) VALUES(@hash,'refresh',@grantId,@scope,@epoch,@now,@expires)",
            new { hash = Text.Sha256Hex(refresh), grantId, scope, epoch, now, expires = now + RefreshSeconds });
        db.Exec("UPDATE oauth_grants SET last_used_at=@iso WHERE id=@grantId", new { iso = Text.NowIso(), grantId });

        return new IssuedTokens(access, refresh, scope);
    }

    private sealed class AccessRow
    {
        public string UserId { get; set; } = "";
        public string AppId { get; set; } = "";
        public string Scope { get; set; } = "";
        public int Verified { get; set; }
    }

    // Looks up an access token. Tokens stop working when they expire, when the person revokes the app, and when the
    // account signs out everywhere or changes its password (both bump session_epoch).
    public static OAuthCaller? Resolve(Db db, string token)
    {
        if (!token.StartsWith(AccessPrefix, StringComparison.Ordinal) || token.Length > 200)
            return null;

        AccessRow? row = db.One<AccessRow>("""
            SELECT g.user_id, g.app_id, t.scope, a.verified_at IS NOT NULL verified FROM oauth_tokens t
            JOIN oauth_grants g ON g.id=t.grant_id JOIN users u ON u.id=g.user_id JOIN oauth_apps a ON a.id=g.app_id
            WHERE t.token_hash=@hash AND t.kind='access' AND t.expires_at>@now AND t.epoch=u.session_epoch AND u.deleted_at IS NULL
            """, new { hash = Text.Sha256Hex(token), now = Text.NowEpoch() });

        if (row == null)
            return null;

        // Losing verification (staff removed it, or the app was renamed) switches verified-only scopes off straight away.
        HashSet<string> scopes = row.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(key => row.Verified != 0 || Scope(key)?.VerifiedOnly != true).ToHashSet();

        return new OAuthCaller(row.UserId, row.AppId, scopes);
    }

    // Whether a token with these scopes may make this request. publish matches exactly what access keys can do.
    public static bool Allows(OAuthCaller caller, HttpRequest request, bool publishEndpoint)
    {
        PathString path = request.Path;

        if (!path.StartsWithSegments("/api"))
            return false;

        if (path.StartsWithSegments("/api/v1/oauth/userinfo"))
            return true;

        if (publishEndpoint && HttpMethods.IsPost(request.Method) && caller.Scopes.Contains("publish"))
            return true;

        return caller.Scopes.Contains("account") && !AccountDenied.Any(prefix => path.StartsWithSegments(prefix));
    }
}
