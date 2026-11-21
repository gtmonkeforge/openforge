using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// The OAuth2 authorization server: the consent screen at /oauth/authorize, the token and revocation endpoints apps call,
// and userinfo. Apps are registered under Settings -> Developer -> Applications (DeveloperEndpoints).
public static class OAuthEndpoints
{
    public const string AuthorizePath = "/oauth/authorize";

    private static readonly string[] AuthorizeFields = ["response_type", "client_id", "redirect_uri", "scope", "state", "code_challenge", "code_challenge_method", "prompt"];

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(AuthorizePath, AuthorizePage);
        app.MapGet("/api/v1/oauth/authorize", AuthorizeInfo);
        app.MapPost("/api/v1/oauth/authorize", DecideAsync);
        app.MapPost("/api/v1/oauth/token", TokenAsync).WithMetadata(new CsrfExemptAttribute(), new ClientAuthAttribute());
        app.MapPost("/api/v1/oauth/revoke", RevokeAsync).WithMetadata(new CsrfExemptAttribute(), new ClientAuthAttribute());
        app.MapGet("/api/v1/oauth/userinfo", UserInfo);
        app.MapGet("/.well-known/oauth-authorization-server", Metadata);
    }

    // ---- the authorization request --------------------------------------------------------------------------------

    private sealed record AuthorizeRequest(OAuthAppRow App, string RedirectUri, SortedSet<string> Scopes, string? State, string? Challenge, bool ForceConsent);

    // A request whose client and redirect URI check out, but which is wrong in some other way. The app gets told through its redirect.
    private sealed class RedirectableError : Exception
    {
        public RedirectableError(string redirect, string? state, string code, string description) : base(description)
        {
            Redirect = ErrorRedirect(redirect, state, code, description);
        }

        public string Redirect { get; }
    }

    private static string ErrorRedirect(string redirect, string? state, string code, string description)
    {
        var query = new Dictionary<string, string?> { ["error"] = code, ["error_description"] = description };

        if (state != null)
            query["state"] = state;

        return QueryHelpers.AddQueryString(redirect, query);
    }

    // An unknown client or redirect URI throws a plain 400: sending the person anywhere would make this an open redirect.
    private static AuthorizeRequest Parse(Func<string, string?> get, Db db, ForgeOptions options)
    {
        string clientId = get("client_id") ?? "";
        OAuthAppRow app = (clientId.Length is > 0 and <= 64 ? db.One<OAuthAppRow>("SELECT * FROM oauth_apps WHERE id=@clientId", new { clientId }) : null)
            ?? throw ApiException.BadRequest("This app isn't registered with " + options.BrandName + ". Tell its developer that the client_id is wrong.");

        string redirect = get("redirect_uri") ?? "";

        if (redirect == "" || !OAuth.RedirectMatches(app.Redirects, redirect))
            throw ApiException.BadRequest($"{app.Name} asked to send you to an address it hasn't registered. Tell its developer to add the redirect URI in their app settings.");

        string? state = get("state");

        if (state != null && (state.Length > 512 || state.Any(char.IsControl)))
            throw new RedirectableError(redirect, null, "invalid_request", "state must be at most 512 characters.");

        void Fail(string code, string description) => throw new RedirectableError(redirect, state, code, description);

        if (get("response_type") != "code")
            Fail("unsupported_response_type", "Only response_type=code is supported.");

        SortedSet<string>? scopes = OAuth.ParseScopes(get("scope"));

        if (scopes == null)
            Fail("invalid_scope", "Unknown scope. Supported scopes: " + string.Join(", ", OAuth.Scopes.Select(s => s.Key)) + ".");

        if (!app.Verified && scopes!.Any(s => OAuth.Scope(s)!.VerifiedOnly))
            Fail("invalid_scope", "Only apps verified by " + options.BrandName + " staff can ask for the account scope.");

        string? challenge = get("code_challenge");

        if (challenge != null && (get("code_challenge_method") != "S256" || !OAuth.Challenge().IsMatch(challenge)))
            Fail("invalid_request", "code_challenge must be a 43-character S256 challenge, with code_challenge_method=S256.");

        if (challenge == null && app.IsPublic)
            Fail("invalid_request", "Public clients must use PKCE: send code_challenge and code_challenge_method=S256.");

        return new AuthorizeRequest(app, redirect, scopes!, state, challenge, get("prompt") == "consent");
    }

    private static Func<string, string?> FromQuery(HttpRequest request)
    {
        return key => request.Query.TryGetValue(key, out var values) && values.Count == 1 ? values[0] : null;
    }

    private static IResult AuthorizePage(HttpRequest request, HttpResponse response, Shell shell, ForgeOptions options)
    {
        // Same as /login: the session cookie and passkeys belong to the public host.
        string publicHost = new Uri(options.PublicUrl).Authority;

        if (!request.Host.Value!.Equals(publicHost, StringComparison.OrdinalIgnoreCase))
            return Results.Redirect(options.PublicUrl + request.Path + request.QueryString);

        return shell.Page("Authorize an app", PageMeta.Private);
    }

    public static object PublicApp(OAuthAppRow app, Db db, ForgeOptions options)
    {
        UserRow? owner = db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = app.OwnerId });

        return new
        {
            id = app.Id,
            name = app.Name,
            description = app.Description,
            logo = app.Logo,
            homepage_url = app.HomepageUrl,
            verified = app.Verified,
            created_at = app.CreatedAt,
            owner = owner == null ? null : new
            {
                id = owner.Id,
                name = owner.PublicName,
                avatar = owner.Avatar,
                is_verified = owner.Verified,
                profile_url = Text.ProfileUrl(owner.Id, owner.CustomSlug, owner.Verified),
            },
        };
    }

    private static string Host(string redirect)
    {
        return Uri.TryCreate(redirect, UriKind.Absolute, out Uri? uri) && uri.Host != "" ? uri.Host : uri?.Scheme + ":";
    }

    // What the consent screen (and the sign-in page in front of it) shows. Works signed out too.
    private static IResult AuthorizeInfo(HttpRequest request, ForgeContext context, Db db, ForgeOptions options)
    {
        AuthorizeRequest auth;

        try
        {
            auth = Parse(FromQuery(request), db, options);
        }
        catch (RedirectableError error)
        {
            OAuthAppRow? app = db.One<OAuthAppRow>("SELECT * FROM oauth_apps WHERE id=@id", new { id = request.Query["client_id"].ToString() });
            return Api.Json(new { app = app == null ? null : PublicApp(app, db, options), error = error.Message, redirect = error.Redirect });
        }

        CurrentUser? user = context.Session.UserId == null ? null : context.User;
        string? granted = user == null ? null : db.One<string>("SELECT scope FROM oauth_grants WHERE app_id=@app AND user_id=@user", new { app = auth.App.Id, user = user.Id });
        bool covered = granted != null && auth.Scopes.IsSubsetOf(granted.Split(' ')) && !auth.ForceConsent;

        return Api.Json(new
        {
            app = PublicApp(auth.App, db, options),
            scopes = auth.Scopes.Select(key => new { key, label = OAuth.Scope(key)!.Label }),
            redirect_host = Host(auth.RedirectUri),
            granted = covered,
            signed_in = user != null,
        });
    }

    private static async Task<IResult> DecideAsync(HttpRequest request, ForgeContext context, Db db, ForgeOptions options)
    {
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send the authorization request and your decision.");

        if (context.Session.UserId == null || context.User == null)
            throw ApiException.Unauthorized("Sign in to authorize this app.");

        CurrentUser user = context.User;

        if (payload["request"] is not JsonObject fields || !fields.HasOnly(AuthorizeFields) || !payload["approve"].IsBool(out bool approve))
            throw ApiException.BadRequest("Send the authorization request and approve as true or false.");

        AuthorizeRequest auth;

        try
        {
            auth = Parse(key => fields[key].IsString(out string value) ? value : null, db, options);
        }
        catch (RedirectableError error)
        {
            return Api.Json(new { redirect = error.Redirect });
        }

        if (!approve)
            return Api.Json(new { redirect = ErrorRedirect(auth.RedirectUri, auth.State, "access_denied", "The person declined to authorize the app.") });

        string now = Text.NowIso();
        string? existing = db.One<string>("SELECT scope FROM oauth_grants WHERE app_id=@app AND user_id=@user", new { app = auth.App.Id, user = user.Id });
        string combined = OAuth.Join(auth.Scopes.Union(existing?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []).Distinct());

        db.Exec("""
            INSERT INTO oauth_grants(id,app_id,user_id,scope,created_at) VALUES(@id,@app,@user,@combined,@now)
            ON CONFLICT(app_id,user_id) DO UPDATE SET scope=excluded.scope
            """, new { id = Text.Hex(16), app = auth.App.Id, user = user.Id, combined, now });

        string code = Text.UrlToken(32);
        double epoch = Text.NowEpoch();
        db.Exec("DELETE FROM oauth_codes WHERE expires_at<=@epoch", new { epoch });
        db.Exec("""
            INSERT INTO oauth_codes(code_hash,app_id,user_id,redirect_uri,scope,challenge,epoch,expires_at)
            VALUES(@hash,@app,@user,@redirect,@scope,@challenge,@sessionEpoch,@expires)
            """, new
        {
            hash = Text.Sha256Hex(code), app = auth.App.Id, user = user.Id, redirect = auth.RedirectUri, scope = OAuth.Join(auth.Scopes),
            challenge = auth.Challenge, sessionEpoch = user.Row.SessionEpoch, expires = epoch + OAuth.CodeSeconds,
        });

        var query = new Dictionary<string, string?> { ["code"] = code, ["iss"] = options.PublicUrl };

        if (auth.State != null)
            query["state"] = auth.State;

        return Api.Json(new { redirect = QueryHelpers.AddQueryString(auth.RedirectUri, query) });
    }

    // ---- token, revoke and userinfo -------------------------------------------------------------------------------

    private static IResult OAuthError(string error, string description, int status = 400)
    {
        return Results.Json(new { error, error_description = description }, statusCode: status);
    }

    private static async Task<Dictionary<string, string>> ReadParametersAsync(HttpRequest request)
    {
        var values = new Dictionary<string, string>();

        if (request.HasFormContentType)
        {
            IFormCollection form = await request.ReadFormAsync();

            foreach (var (key, value) in form)
            {
                if (value.Count != 1)
                    throw ApiException.BadRequest($"Send {key} once.");

                values[key] = value[0] ?? "";
            }

            return values;
        }

        JsonObject payload = await JsonBody.ReadObjectAsync(request) ?? throw ApiException.BadRequest("Send the parameters as application/x-www-form-urlencoded.");

        foreach (var (key, node) in payload)
        {
            if (node.IsString(out string value))
                values[key] = value;
        }

        return values;
    }

    // Accepts HTTP Basic (client_secret_basic), client_id and client_secret in the body (client_secret_post),
    // or client_id alone for public apps. Returns null when the client can't be authenticated.
    private static OAuthAppRow? AuthenticateClient(HttpRequest request, Dictionary<string, string> values, Db db)
    {
        string? clientId = values.GetValueOrDefault("client_id");
        string? secret = values.GetValueOrDefault("client_secret");
        string header = request.Headers.Authorization.ToString();

        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                int colon = decoded.IndexOf(':');

                if (colon < 0 || (clientId != null && clientId != Uri.UnescapeDataString(decoded[..colon])))
                    return null;

                clientId = Uri.UnescapeDataString(decoded[..colon]);
                secret = Uri.UnescapeDataString(decoded[(colon + 1)..]);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        if (string.IsNullOrEmpty(clientId) || clientId.Length > 64)
            return null;

        OAuthAppRow? app = db.One<OAuthAppRow>("SELECT * FROM oauth_apps WHERE id=@clientId", new { clientId });

        if (app == null)
            return null;

        if (string.IsNullOrEmpty(secret))
            return app.IsPublic ? app : null;

        string? secretId = secret.Length <= 200 ? db.One<string>("SELECT id FROM oauth_secrets WHERE app_id=@app AND secret_hash=@hash", new { app = app.Id, hash = Text.Sha256Hex(secret) }) : null;

        if (secretId == null)
            return null;

        db.Exec("UPDATE oauth_secrets SET last_used_at=@now WHERE id=@secretId", new { now = Text.NowIso(), secretId });
        return app;
    }

    private sealed class CodeRow
    {
        public string UserId { get; set; } = "";
        public string RedirectUri { get; set; } = "";
        public string Scope { get; set; } = "";
        public string? Challenge { get; set; }
        public long Epoch { get; set; }
    }

    private sealed class RefreshRow
    {
        public string GrantId { get; set; } = "";
        public string Scope { get; set; } = "";
        public long Epoch { get; set; }
    }

    // The account must still exist, not be banned, and not have signed out everywhere since the code or token was issued.
    private static bool AccountUsable(Db db, ForgeOptions options, string userId, long epoch)
    {
        UserRow? user = db.One<UserRow>("SELECT * FROM users WHERE id=@userId", new { userId });
        return user != null && user.DeletedAt == null && (!user.Banned || user.Id == options.RootAdminId) && user.SessionEpoch == epoch;
    }

    private static async Task<IResult> TokenAsync(HttpContext http, Db db, ForgeOptions options, AuthThrottle throttle)
    {
        throttle.Hit(throttle.ClientKey(http) + ":oauth-token", 60, TimeSpan.FromMinutes(1), "Too many token requests. Slow down and try again.");
        http.Response.Headers.Pragma = "no-cache";
        Dictionary<string, string> values = await ReadParametersAsync(http.Request);
        OAuthAppRow? app = AuthenticateClient(http.Request, values, db);

        if (app == null)
            return OAuthError("invalid_client", "Client authentication failed. Check the client_id and client_secret.", 401);

        string grantType = values.GetValueOrDefault("grant_type") ?? "";
        double now = Text.NowEpoch();

        if (grantType == "authorization_code")
        {
            string code = values.GetValueOrDefault("code") ?? "";

            // DELETE ... RETURNING uses the code up whether or not the rest checks out, so it can't be tried twice.
            CodeRow? row = code.Length is < 20 or > 100 ? null : db.One<CodeRow>(
                "DELETE FROM oauth_codes WHERE code_hash=@hash AND app_id=@app AND expires_at>@now RETURNING user_id,redirect_uri,scope,challenge,epoch",
                new { hash = Text.Sha256Hex(code), app = app.Id, now });

            if (row == null)
                return OAuthError("invalid_grant", "The code is invalid, expired or already used. Start sign-in again.");

            if (values.GetValueOrDefault("redirect_uri") != row.RedirectUri)
                return OAuthError("invalid_grant", "redirect_uri must be exactly the one sent to /oauth/authorize.");

            string verifier = values.GetValueOrDefault("code_verifier") ?? "";

            if (row.Challenge != null ? !OAuth.Verifier().IsMatch(verifier) || !Text.SafeEquals(OAuth.S256(verifier), row.Challenge) : verifier != "")
                return OAuthError("invalid_grant", "The code_verifier doesn't match the code_challenge.");

            string? grantId = db.One<string>("SELECT id FROM oauth_grants WHERE app_id=@app AND user_id=@user", new { app = app.Id, user = row.UserId });

            if (grantId == null || !AccountUsable(db, options, row.UserId, row.Epoch))
                return OAuthError("invalid_grant", "This authorization is no longer valid. Start sign-in again.");

            return Tokens(OAuth.Issue(db, grantId, row.Scope, row.Epoch));
        }

        if (grantType == "refresh_token")
        {
            string refresh = values.GetValueOrDefault("refresh_token") ?? "";

            // Refresh tokens rotate: the old one is gone the moment this runs.
            RefreshRow? row = !refresh.StartsWith(OAuth.RefreshPrefix, StringComparison.Ordinal) || refresh.Length > 200 ? null : db.One<RefreshRow>("""
                DELETE FROM oauth_tokens WHERE token_hash=@hash AND kind='refresh' AND expires_at>@now
                AND grant_id IN (SELECT id FROM oauth_grants WHERE app_id=@app) RETURNING grant_id,scope,epoch
                """, new { hash = Text.Sha256Hex(refresh), app = app.Id, now });

            if (row == null)
                return OAuthError("invalid_grant", "The refresh token is invalid, expired or revoked. Sign in again.");

            string? userId = db.One<string>("SELECT user_id FROM oauth_grants WHERE id=@id", new { id = row.GrantId });

            if (userId == null || !AccountUsable(db, options, userId, row.Epoch))
                return OAuthError("invalid_grant", "This authorization is no longer valid. Sign in again.");

            string scope = row.Scope;

            if (values.TryGetValue("scope", out string? narrower) && narrower != "")
            {
                SortedSet<string>? asked = OAuth.ParseScopes(narrower);

                if (asked == null || !asked.IsSubsetOf(row.Scope.Split(' ')))
                    return OAuthError("invalid_scope", "A refresh can only keep or narrow the scopes that were granted.");

                scope = OAuth.Join(asked);
            }

            return Tokens(OAuth.Issue(db, row.GrantId, scope, row.Epoch));
        }

        return OAuthError("unsupported_grant_type", "Use grant_type=authorization_code or refresh_token.");
    }

    private static IResult Tokens(OAuth.IssuedTokens tokens)
    {
        return Results.Json(new
        {
            access_token = tokens.AccessToken,
            token_type = "Bearer",
            expires_in = (int)OAuth.AccessSeconds,
            refresh_token = tokens.RefreshToken,
            scope = tokens.Scope,
        });
    }

    // RFC 7009. Revoking a refresh token signs the app out of that account; revoking an access token ends just that token.
    private static async Task<IResult> RevokeAsync(HttpContext http, Db db, AuthThrottle throttle)
    {
        throttle.Hit(throttle.ClientKey(http) + ":oauth-token", 60, TimeSpan.FromMinutes(1), "Too many token requests. Slow down and try again.");
        Dictionary<string, string> values = await ReadParametersAsync(http.Request);
        OAuthAppRow? app = AuthenticateClient(http.Request, values, db);

        if (app == null)
            return OAuthError("invalid_client", "Client authentication failed. Check the client_id and client_secret.", 401);

        string token = values.GetValueOrDefault("token") ?? "";
        var args = new { hash = Text.Sha256Hex(token), app = app.Id };

        if (token.StartsWith(OAuth.RefreshPrefix, StringComparison.Ordinal))
            db.Exec("DELETE FROM oauth_tokens WHERE grant_id IN (SELECT grant_id FROM oauth_tokens WHERE token_hash=@hash AND kind='refresh' AND grant_id IN (SELECT id FROM oauth_grants WHERE app_id=@app))", args);
        else if (token.StartsWith(OAuth.AccessPrefix, StringComparison.Ordinal))
            db.Exec("DELETE FROM oauth_tokens WHERE token_hash=@hash AND kind='access' AND grant_id IN (SELECT id FROM oauth_grants WHERE app_id=@app)", args);

        // Unknown tokens get the same answer, as the RFC asks.
        return Results.Json(new { ok = true });
    }

    private static IResult UserInfo(ForgeContext context, ForgeOptions options)
    {
        OAuthCaller caller = context.OAuth ?? throw ApiException.Unauthorized("Send an OAuth access token as Authorization: Bearer <token>.");
        CurrentUser user = context.User ?? throw ApiException.Unauthorized("This access token is invalid, expired or revoked.");
        string Absolute(string path) => path.StartsWith('/') ? options.PublicUrl + path : path;

        var info = new Dictionary<string, object?>
        {
            ["sub"] = user.Id,
            ["id"] = user.Id,
            ["username"] = user.Username,
            ["display_name"] = user.DisplayName,
            ["avatar"] = user.Row.Avatar == null ? null : Absolute(user.Row.Avatar),
            ["profile_url"] = Absolute(user.ProfileUrl),
            ["is_verified"] = user.IsVerified,
            ["scope"] = OAuth.Join(caller.Scopes),
        };

        if (caller.Scopes.Contains("email"))
        {
            bool confirmed = user.Row.Email != null && user.Row.EmailVerified != 0;
            info["email"] = confirmed ? user.Row.Email : null;
            info["email_verified"] = confirmed;
        }

        return Api.Json(info);
    }

    private static IResult Metadata(ForgeOptions options)
    {
        string root = options.PublicUrl;

        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = root,
            ["authorization_endpoint"] = root + AuthorizePath,
            ["token_endpoint"] = root + "/api/v1/oauth/token",
            ["revocation_endpoint"] = root + "/api/v1/oauth/revoke",
            ["userinfo_endpoint"] = root + "/api/v1/oauth/userinfo",
            ["scopes_supported"] = OAuth.Scopes.Select(s => s.Key).ToArray(),
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post", "none" },
            ["revocation_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post", "none" },
            ["authorization_response_iss_parameter_supported"] = true,
        });
    }
}
