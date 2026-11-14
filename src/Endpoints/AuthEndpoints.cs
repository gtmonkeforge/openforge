using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using OpenForge.Infrastructure;
using OpenForge.Models;

namespace OpenForge.Endpoints;

public static partial class AuthEndpoints
{
    private static readonly string[] NativeFields = ["redirect_uri", "code_challenge", "code_challenge_method", "state"];
    private static readonly string[] AllowedNext = ["/browse", "/upload", "/profile", "/admin"];

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/discord", Login);
        app.MapGet("/auth/discord/callback", CallbackAsync);
        app.MapPost("/api/v1/auth/native/exchange", NativeExchangeAsync).WithMetadata(new CsrfExemptAttribute());
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{43}\z")]
    private static partial Regex Challenge();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{22,128}\z")]
    private static partial Regex State();

    [GeneratedRegex(@"\A[A-Za-z0-9._~-]{43,128}\z")]
    private static partial Regex Verifier();

    [GeneratedRegex(@"\Ahttp://(?:127\.0\.0\.1|\[::1\]):([0-9]{1,5})(?:/|\z)")]
    private static partial Regex LoopbackRedirect();

    private static IResult Login(HttpRequest request, ForgeContext context, ForgeOptions options)
    {
        if (!options.OAuthConfigured)
            throw new ApiException(503, "Discord sign-in needs configuration. Add Discord application credentials to the local .env file.");

        // State must be created on the public host: TLS may end at Cloudflare, so the origin can see a different host.
        string publicHost = new Uri(options.PublicUrl).Authority.ToLowerInvariant();

        if (!request.Host.Value!.Equals(publicHost, StringComparison.OrdinalIgnoreCase))
        {
            string[] forwarded = ["next", .. NativeFields];
            var query = request.Query.Where(q => forwarded.Contains(q.Key)).SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string?>(q.Key, v)));
            return Results.Redirect(QueryHelpers.AddQueryString(options.PublicUrl + "/auth/discord", query));
        }

        PrepareNative(request, context);

        string next = request.Query["next"].FirstOrDefault() ?? "/upload";
        context.Session.OauthState = Text.UrlToken(32);
        context.Session.OauthStarted = Text.NowEpoch();
        context.Session.OauthNext = AllowedNext.Contains(next) ? next : "/upload";

        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = options.DiscordClientId,
            ["response_type"] = "code",
            ["scope"] = "identify",
            ["redirect_uri"] = options.PublicUrl + "/auth/discord/callback",
            ["state"] = context.Session.OauthState,
        };

        return Results.Redirect(QueryHelpers.AddQueryString("https://discord.com/oauth2/authorize", parameters));
    }

    private static void PrepareNative(HttpRequest request, ForgeContext context)
    {
        context.Session.OauthNative = null;

        if (!NativeFields.Any(field => request.Query.ContainsKey(field)))
            return;

        string Value(string key)
        {
            return request.Query[key].FirstOrDefault() ?? "";
        }

        string uri = Value("redirect_uri");
        Match loopback = LoopbackRedirect().Match(uri);
        bool valid = loopback.Success && int.Parse(loopback.Groups[1].Value) is >= 1 and <= 65535
            && uri.Length <= 2048 && !uri.Contains('?') && !uri.Contains('#') && !uri.Any(c => char.IsWhiteSpace(c) || c < 32)
            && Uri.TryCreate(uri, UriKind.Absolute, out _);

        if (!valid || Value("code_challenge_method") != "S256" || !Challenge().IsMatch(Value("code_challenge")) || !State().IsMatch(Value("state")))
            throw ApiException.BadRequest("Native sign-in requires a loopback HTTP redirect, S256 challenge, and random state.");

        context.Session.OauthNative = new NativeAuthRequest(uri, Value("code_challenge"), Value("code_challenge_method"), Value("state"));
    }

    private static async Task<IResult> CallbackAsync(HttpRequest request, HttpResponse response, ForgeContext context, ForgeOptions options, Db db, IHttpClientFactory http, ILoggerFactory loggers)
    {
        string? expected = context.Session.OauthState;
        double started = context.Session.OauthStarted;
        string supplied = request.Query["state"].FirstOrDefault() ?? "";
        context.Session.OauthState = null;
        context.Session.OauthStarted = 0;

        if (expected == null || !Text.SafeEquals(expected, supplied) || Text.NowEpoch() - started > 600)
            throw ApiException.BadRequest("Discord sign-in expired or could not be verified. Please sign in again.");

        string? code = request.Query["code"].FirstOrDefault();

        if (!string.IsNullOrEmpty(request.Query["error"].FirstOrDefault()) || string.IsNullOrEmpty(code))
            throw ApiException.BadRequest("Discord sign-in was canceled. You can try again at any time.");

        JsonObject identity = await DiscordIdentityAsync(code, options, http, loggers.CreateLogger("DiscordOAuth"));
        string id = identity["id"]!.ToString();
        string username = identity["username"]!.ToString();
        string? globalName = identity["global_name"]?.GetValueKind() == JsonValueKind.String ? identity["global_name"]!.ToString() : null;

        int? banned = db.One<int?>("SELECT is_banned FROM users WHERE id=@id", new { id });

        if (banned == 1 && id != options.RootAdminId)
        {
            context.Clear();
            throw ApiException.Forbidden("Your account is banned from this site.");
        }

        db.Exec("INSERT INTO users(id,username,display_name) VALUES(@id,@username,@display) ON CONFLICT(id) DO UPDATE SET username=excluded.username, display_name=excluded.display_name",
            new { id, username, display = string.IsNullOrEmpty(globalName) ? username : globalName });

        string next = context.Session.OauthNext ?? "/upload";
        NativeAuthRequest? native = context.Session.OauthNative;
        context.SignIn(id);

        if (native == null)
            return Results.Redirect(next);

        string nativeCode = Text.UrlToken(32);
        double now = Text.NowEpoch();
        db.Exec("DELETE FROM native_auth_codes WHERE expires_at<=@now", new { now });
        db.Exec("INSERT INTO native_auth_codes VALUES(@hash,@id,@uri,@challenge,@expires)",
            new { hash = Text.Sha256Hex(nativeCode), id, uri = native.RedirectUri, challenge = native.CodeChallenge, expires = now + 60 });

        response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Redirect(QueryHelpers.AddQueryString(native.RedirectUri, new Dictionary<string, string?> { ["code"] = nativeCode, ["state"] = native.State }));
    }

    private static async Task<JsonObject> DiscordIdentityAsync(string code, ForgeOptions options, IHttpClientFactory http, ILogger logger)
    {
        HttpClient client = http.CreateClient("discord");
        string stage = "token exchange";

        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.DiscordClientId,
                ["client_secret"] = options.DiscordClientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = options.PublicUrl + "/auth/discord/callback",
            });

            using HttpResponseMessage tokenResponse = await client.PostAsync("https://discord.com/api/v10/oauth2/token", form);
            int tokenStatus = (int)tokenResponse.StatusCode;

            if (!tokenResponse.IsSuccessStatusCode)
                throw new DiscordHttpException(tokenStatus);

            JsonNode? token = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync());
            string accessToken = token?["access_token"]?.ToString() ?? throw new JsonException();

            stage = "profile lookup";
            var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
            profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using HttpResponseMessage profileResponse = await client.SendAsync(profileRequest);

            if (!profileResponse.IsSuccessStatusCode)
                throw new DiscordHttpException((int)profileResponse.StatusCode);

            if (JsonNode.Parse(await profileResponse.Content.ReadAsStringAsync()) is not JsonObject identity || identity["id"] == null || identity["username"] == null)
                throw new JsonException();

            return identity;
        }
        catch (DiscordHttpException ex)
        {
            logger.LogWarning("Discord OAuth {Stage} failed: HTTP {Status}", stage, ex.Status);
            throw new ApiException(502, $"Discord rejected the {stage} (HTTP {ex.Status}). Please start sign-in again. If this persists, check the Discord application credentials and registered redirect URI.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning("Discord OAuth {Stage} failed: {Type}", stage, ex.GetType().Name);
            throw new ApiException(502, "Discord could not complete sign-in. Please try again.");
        }
    }

    private static async Task<IResult> NativeExchangeAsync(HttpRequest request, ForgeContext context, ForgeOptions options, Db db)
    {
        JsonObject payload = await JsonBody.ReadObjectAsync(request) ?? throw ApiException.BadRequest("Send a JSON code, code_verifier, and redirect_uri.");

        payload["code"].IsString(out string code);
        payload["code_verifier"].IsString(out string codeVerifier);
        payload["redirect_uri"].IsString(out string redirectUri);

        if (!Challenge().IsMatch(code) || !Verifier().IsMatch(codeVerifier) || !payload["redirect_uri"].IsString(out _) || redirectUri.Length > 2048)
            throw ApiException.BadRequest("Invalid native sign-in exchange parameters.");

        string challenge = Text.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        // DELETE ... RETURNING consumes the code atomically.
        string? userId = db.One<string>("""
            DELETE FROM native_auth_codes WHERE code_hash=@hash
            AND challenge=@challenge AND redirect_uri=@redirectUri AND expires_at>@now RETURNING user_id
            """, new { hash = Text.Sha256Hex(code), challenge, redirectUri, now = Text.NowEpoch() });

        if (userId == null)
            throw ApiException.BadRequest("Native sign-in expired or could not be verified. Start sign-in again.");

        int? banned = db.One<int?>("SELECT is_banned FROM users WHERE id=@userId", new { userId });

        if (banned == null || (banned == 1 && userId != options.RootAdminId))
            throw ApiException.Forbidden("This account cannot sign in.");

        context.SignIn(userId);
        return Api.Json(new { user = context.User?.ToDto(), csrf_token = context.Csrf() });
    }

    private sealed class DiscordHttpException : Exception
    {
        public DiscordHttpException(int status)
        {
            Status = status;
        }

        public int Status { get; }
    }
}
