using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

public static class AuthEndpoints
{
    public static readonly string[] AllowedNext = ["/browse", "/upload", "/profile", "/admin", "/settings"];

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/discord", Login);
        app.MapGet("/auth/discord/callback", CallbackAsync);
    }

    private static IResult Login(HttpRequest request, ForgeContext context, ForgeOptions options)
    {
        if (!options.OAuthConfigured)
            throw new ApiException(503, "Discord sign-in needs configuration. Add Discord application credentials to the local .env file.");

        // State must be created on the public host: TLS may end at Cloudflare, so the origin can see a different host.
        string publicHost = new Uri(options.PublicUrl).Authority.ToLowerInvariant();

        if (!request.Host.Value!.Equals(publicHost, StringComparison.OrdinalIgnoreCase))
        {
            var query = request.Query.Where(q => q.Key == "next").SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string?>(q.Key, v)));
            return Results.Redirect(QueryHelpers.AddQueryString(options.PublicUrl + "/auth/discord", query));
        }

        context.Session.OauthState = Text.UrlToken(32);
        context.Session.OauthStarted = Text.NowEpoch();
        context.Session.OauthNext = SafeNext(request.Query["next"].FirstOrDefault());

        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = options.DiscordClientId,
            ["response_type"] = "code",
            // email lets us send release notices and announcements to Discord accounts too.
            ["scope"] = "identify email",
            ["redirect_uri"] = options.PublicUrl + "/auth/discord/callback",
            ["state"] = context.Session.OauthState,
        };

        return Results.Redirect(QueryHelpers.AddQueryString("https://discord.com/oauth2/authorize", parameters));
    }

    // Where sign-in may send people afterwards: one of the site's own pages, or back to an app's authorization screen.
    public static string SafeNext(string? next)
    {
        if (next != null && AllowedNext.Contains(next))
            return next;

        if (next != null && next.StartsWith(OAuthEndpoints.AuthorizePath + "?", StringComparison.Ordinal) && next.Length <= 4096
            && !next.Any(c => c is '\\' or '#' || char.IsWhiteSpace(c) || char.IsControl(c)))
            return next;

        return "/upload";
    }

    private static async Task<IResult> CallbackAsync(HttpRequest request, HttpResponse response, ForgeContext context, ForgeOptions options, Db db, IHttpClientFactory http, ILoggerFactory loggers, Diagnostics diagnostics)
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

        JsonObject identity = await DiscordIdentityAsync(code, options, http, loggers.CreateLogger("DiscordOAuth"), diagnostics);
        string id = identity["id"]!.ToString();
        string username = identity["username"]!.ToString();
        string? globalName = identity["global_name"]?.GetValueKind() == JsonValueKind.String ? identity["global_name"]!.ToString() : null;

        int? banned = db.One<int?>("SELECT is_banned FROM users WHERE id=@id", new { id });

        if (banned == 1 && id != options.RootAdminId)
        {
            context.Clear();
            throw ApiException.Forbidden("Your account is banned from this site.");
        }

        db.Exec("INSERT INTO users(id,username,display_name,created_at) VALUES(@id,@username,@display,@now) ON CONFLICT(id) DO UPDATE SET username=excluded.username, display_name=excluded.display_name",
            new { id, username, display = string.IsNullOrEmpty(globalName) ? username : globalName, now = Text.NowIso() });

        // Discord only vouches for an address it has verified. One the person typed into settings themselves is left alone.
        if (identity["email"].IsString(out string email) && identity["verified"]?.GetValueKind() == JsonValueKind.True && Text.Email().IsMatch(email.Trim()))
            db.Exec("UPDATE users SET email=@email,email_verified=1,email_source='discord' WHERE id=@id AND (email IS NULL OR email_source='discord' OR email_verified=0)",
                new { email = email.Trim().ToLowerInvariant(), id });

        string? next = context.Session.OauthNext;
        context.SignIn(id);
        return Results.Redirect(SafeNext(next));
    }

    private static async Task<JsonObject> DiscordIdentityAsync(string code, ForgeOptions options, IHttpClientFactory http, ILogger logger, Diagnostics diagnostics)
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
            diagnostics.Report("Discord sign-in", $"Discord rejected the {stage} with HTTP {ex.Status}.", "Check the Discord application credentials and the registered redirect URI.");
            throw new ApiException(502, $"Discord rejected the {stage} (HTTP {ex.Status}). Please start sign-in again. If this persists, check the Discord application credentials and registered redirect URI.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning("Discord OAuth {Stage} failed: {Type}", stage, ex.GetType().Name);
            diagnostics.Report("Discord sign-in", $"Discord {stage} failed: {ex.GetType().Name}.", exception: ex);
            throw new ApiException(502, "Discord could not complete sign-in. Please try again.");
        }
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
