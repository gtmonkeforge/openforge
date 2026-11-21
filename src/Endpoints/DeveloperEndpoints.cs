using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// Settings -> Developer -> Applications: creators register OAuth2 apps, and staff verify them from the admin panel.
// Also the list of apps a person has authorized, so they can disconnect one.
public static class DeveloperEndpoints
{
    private static readonly string[] AppFields = ["name", "description", "homepage_url", "redirect_uris", "public_client"];
    private static readonly string[] AdminFilters = ["all", "verified", "unverified"];

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/oauth/apps", ListApps);
        app.MapPost("/api/v1/oauth/apps", CreateAppAsync);
        app.MapGet("/api/v1/oauth/apps/{id}", GetApp);
        app.MapPatch("/api/v1/oauth/apps/{id}", UpdateAppAsync);
        app.MapDelete("/api/v1/oauth/apps/{id}", DeleteApp);
        app.MapPost("/api/v1/oauth/apps/{id}/logo", UploadLogoAsync);
        app.MapDelete("/api/v1/oauth/apps/{id}/logo", RemoveLogo);
        app.MapPost("/api/v1/oauth/apps/{id}/secrets", CreateSecretAsync);
        app.MapDelete("/api/v1/oauth/apps/{id}/secrets/{secretId}", DeleteSecret);

        app.MapGet("/api/v1/oauth/authorizations", Authorizations);
        app.MapDelete("/api/v1/oauth/authorizations/{appId}", Disconnect);

        app.MapGet("/api/v1/admin/oauth-apps", AdminApps);
        app.MapPut("/api/v1/admin/oauth-apps/{id}/verified", SetVerifiedAsync);
    }

    // Managing apps needs a real browser session: access keys and OAuth tokens can't create more credentials.
    private static CurrentUser Developer(ForgeContext context)
    {
        if (context.Session.UserId == null || context.User == null)
            throw ApiException.Unauthorized("Sign in to manage your applications.");

        if (context.User.IsGroup)
            throw ApiException.Forbidden("Publishing groups can't own applications.");

        return context.User;
    }

    private static OAuthAppRow Owned(Db db, CurrentUser user, string id)
    {
        return db.One<OAuthAppRow>("SELECT * FROM oauth_apps WHERE id=@id AND owner_id=@owner", new { id, owner = user.Id })
            ?? throw ApiException.NotFound("Application not found.");
    }

    private static object Describe(OAuthAppRow app, Db db, bool full)
    {
        long users = db.Scalar<long>("SELECT COUNT(*) FROM oauth_grants WHERE app_id=@id", new { id = app.Id });

        if (!full)
            return new { id = app.Id, name = app.Name, logo = app.Logo, verified = app.Verified, public_client = app.IsPublic, users, created_at = app.CreatedAt };

        var secrets = db.Query<OAuthSecretRow>("SELECT id,app_id,name,prefix,created_at,last_used_at FROM oauth_secrets WHERE app_id=@id ORDER BY created_at", new { id = app.Id })
            .Select(s => new { id = s.Id, name = s.Name, prefix = s.Prefix, created_at = s.CreatedAt, last_used_at = s.LastUsedAt });

        return new
        {
            id = app.Id,
            client_id = app.Id,
            name = app.Name,
            description = app.Description,
            homepage_url = app.HomepageUrl,
            logo = app.Logo,
            redirect_uris = app.Redirects,
            public_client = app.IsPublic,
            verified = app.Verified,
            verified_at = app.VerifiedAt,
            users,
            secrets,
            created_at = app.CreatedAt,
            updated_at = app.UpdatedAt,
        };
    }

    private static IResult ListApps(ForgeContext context, Db db)
    {
        CurrentUser user = Developer(context);
        var items = db.Query<OAuthAppRow>("SELECT * FROM oauth_apps WHERE owner_id=@id ORDER BY created_at DESC", new { id = user.Id }).Select(a => Describe(a, db, false)).ToList();
        return Api.Json(new { items, max_apps = OAuth.MaxAppsPerUser });
    }

    private static IResult GetApp(string id, ForgeContext context, Db db)
    {
        return Api.Json(Describe(Owned(db, Developer(context), id), db, true));
    }

    private static string AppName(JsonNode? node)
    {
        if (!node.IsString(out string name) || Text.Len(name.Trim()) is < 2 or > 40 || name.Any(char.IsControl))
            throw ApiException.BadRequest("Give the application a name of 2–40 characters.");

        return name.Trim();
    }

    private static async Task<IResult> CreateAppAsync(HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser user = Developer(context);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send a name for the application.");
        string name = AppName(payload["name"]);

        if (db.Scalar<long>("SELECT COUNT(*) FROM oauth_apps WHERE owner_id=@id", new { id = user.Id }) >= OAuth.MaxAppsPerUser)
            throw ApiException.Conflict($"You can have up to {OAuth.MaxAppsPerUser} applications. Delete one you no longer use first.");

        // The client ID is public: it goes in authorization URLs. Secrets are separate and can be rotated.
        string id = Text.Hex(12);
        string now = Text.NowIso();
        db.Exec("INSERT INTO oauth_apps(id,owner_id,name,created_at,updated_at) VALUES(@id,@owner,@name,@now,@now)", new { id, owner = user.Id, name, now });

        return Api.Json(Describe(Owned(db, user, id), db, true), 201);
    }

    private static async Task<IResult> UpdateAppAsync(string id, HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser user = Developer(context);
        OAuthAppRow app = Owned(db, user, id);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send the fields to change.");

        if (!payload.HasOnly(AppFields))
            throw ApiException.BadRequest("Only " + string.Join(", ", AppFields) + " can be changed.");

        string name = payload.ContainsKey("name") ? AppName(payload["name"]) : app.Name;
        string description = app.Description;
        string? homepage = app.HomepageUrl;
        List<string> redirects = app.Redirects;
        bool isPublic = app.IsPublic;

        if (payload.ContainsKey("description"))
        {
            if (!payload["description"].IsString(out description) || Text.Len(description.Trim()) > 400)
                throw ApiException.BadRequest("Keep the description to 400 characters.");

            description = description.Trim();
        }

        if (payload.ContainsKey("homepage_url"))
        {
            homepage = payload["homepage_url"].IsString(out string url) && url.Trim() != "" ? url.Trim() : null;

            if (homepage != null && (homepage.Length > 500 || !Text.IsWebUrl(homepage)))
                throw ApiException.BadRequest("The website must be an http:// or https:// address.");
        }

        if (payload.ContainsKey("redirect_uris"))
        {
            if (payload["redirect_uris"] is not JsonArray list || list.Any(node => !node.IsString(out _)))
                throw ApiException.BadRequest("redirect_uris must be a list of URLs.");

            redirects = list.Select(node => node!.GetValue<string>().Trim()).Where(uri => uri != "").Distinct(StringComparer.Ordinal).ToList();

            if (redirects.Count > OAuth.MaxRedirectUris)
                throw ApiException.BadRequest($"An application can have up to {OAuth.MaxRedirectUris} redirect URIs.");

            foreach (string uri in redirects)
            {
                if (OAuth.RedirectProblem(uri) is string problem)
                    throw ApiException.BadRequest(problem);
            }
        }

        if (payload.ContainsKey("public_client") && !payload["public_client"].IsBool(out isPublic))
            throw ApiException.BadRequest("public_client must be true or false.");

        // Verification vouches for the name and website people saw when staff checked the app, so changing those undoes it.
        bool keepVerified = name == app.Name && homepage == app.HomepageUrl;

        db.Exec("""
            UPDATE oauth_apps SET name=@name, description=@description, homepage_url=@homepage, redirect_uris=@redirects, public_client=@isPublic,
            verified_at=CASE WHEN @keepVerified THEN verified_at END, verified_by=CASE WHEN @keepVerified THEN verified_by END, updated_at=@now WHERE id=@id
            """, new { name, description, homepage, redirects = JsonBody.Serialize(redirects), isPublic = isPublic ? 1 : 0, keepVerified, now = Text.NowIso(), id });

        return Api.Json(Describe(Owned(db, user, id), db, true));
    }

    private static IResult DeleteApp(string id, ForgeContext context, Db db, Storage storage)
    {
        OAuthAppRow app = Owned(db, Developer(context), id);
        db.Exec("DELETE FROM oauth_apps WHERE id=@id", new { id });

        if (app.Logo != null)
            storage.DeleteUnreferencedMedia([app.Logo]);

        return Api.Json(new { deleted = true });
    }

    private static async Task<IResult> UploadLogoAsync(string id, HttpRequest request, ForgeContext context, Db db, Storage storage)
    {
        CurrentUser user = Developer(context);
        OAuthAppRow app = Owned(db, user, id);

        if (!request.HasFormContentType)
            throw ApiException.BadRequest("Upload the logo as multipart/form-data in a field named logo.");

        IFormFile file = (await request.ReadFormAsync()).Files.GetFile("logo") ?? throw ApiException.BadRequest("Choose an image for the logo.");
        var written = new List<string>();

        try
        {
            // Square-cropped to 256×256 like avatars, since the consent screen shows it in a square.
            string logo = await storage.SaveImageAsync(file, written, avatar: true);

            // A new logo is a new look for a verified app, so staff check it again.
            db.Exec("UPDATE oauth_apps SET logo=@logo, verified_at=NULL, verified_by=NULL, updated_at=@now WHERE id=@id", new { logo, now = Text.NowIso(), id });
        }
        catch
        {
            Storage.DeleteAll(written);
            throw;
        }

        if (app.Logo != null)
            storage.DeleteUnreferencedMedia([app.Logo]);

        return Api.Json(Describe(Owned(db, user, id), db, true));
    }

    private static IResult RemoveLogo(string id, ForgeContext context, Db db, Storage storage)
    {
        CurrentUser user = Developer(context);
        OAuthAppRow app = Owned(db, user, id);
        db.Exec("UPDATE oauth_apps SET logo=NULL, updated_at=@now WHERE id=@id", new { now = Text.NowIso(), id });

        if (app.Logo != null)
            storage.DeleteUnreferencedMedia([app.Logo]);

        return Api.Json(Describe(Owned(db, user, id), db, true));
    }

    private static async Task<IResult> CreateSecretAsync(string id, HttpRequest request, ForgeContext context, Db db)
    {
        OAuthAppRow app = Owned(db, Developer(context), id);
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);
        string name = payload != null && payload["name"].IsString(out string given) && given.Trim() != "" ? given.Trim() : "Client secret";

        if (Text.Len(name) > 60 || name.Any(char.IsControl))
            throw ApiException.BadRequest("Keep the secret's name to 60 characters.");

        if (db.Scalar<long>("SELECT COUNT(*) FROM oauth_secrets WHERE app_id=@id", new { id }) >= OAuth.MaxSecretsPerApp)
            throw ApiException.Conflict($"An application can have up to {OAuth.MaxSecretsPerApp} secrets. Delete an old one first.");

        string secret = OAuth.SecretPrefix + Text.UrlToken(32);
        string secretId = Text.Hex(12);
        string now = Text.NowIso();
        db.Exec("INSERT INTO oauth_secrets(id,app_id,name,secret_hash,prefix,created_at) VALUES(@secretId,@app,@name,@hash,@prefix,@now)",
            new { secretId, app = app.Id, name, hash = Text.Sha256Hex(secret), prefix = secret[..12], now });

        // The only time the full secret leaves the server.
        return Api.Json(new { id = secretId, name, prefix = secret[..12], secret, created_at = now }, 201);
    }

    private static IResult DeleteSecret(string id, string secretId, ForgeContext context, Db db)
    {
        OAuthAppRow app = Owned(db, Developer(context), id);

        if (db.Exec("DELETE FROM oauth_secrets WHERE id=@secretId AND app_id=@app", new { secretId, app = app.Id }) == 0)
            throw ApiException.NotFound("Secret not found.");

        return Api.Json(new { deleted = true });
    }

    // ---- apps the person has authorized ---------------------------------------------------------------------------

    private sealed class AuthorizationRow
    {
        public string AppId { get; set; } = "";
        public string Scope { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string? LastUsedAt { get; set; }
    }

    private static CurrentUser SessionUser(ForgeContext context)
    {
        if (context.Session.UserId == null || context.User == null)
            throw ApiException.Unauthorized("Sign in to manage the apps you've authorized.");

        return context.User;
    }

    private static IResult Authorizations(ForgeContext context, Db db, ForgeOptions options)
    {
        CurrentUser user = SessionUser(context);
        var items = db.Query<AuthorizationRow>("SELECT app_id,scope,created_at,last_used_at FROM oauth_grants WHERE user_id=@id ORDER BY created_at DESC", new { id = user.Id })
            .Select(grant => (grant, app: db.One<OAuthAppRow>("SELECT * FROM oauth_apps WHERE id=@id", new { id = grant.AppId })))
            .Where(pair => pair.app != null)
            .Select(pair => new
            {
                app = OAuthEndpoints.PublicApp(pair.app!, db, options),
                scopes = pair.grant.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(key => new { key, label = OAuth.Scope(key)?.Label ?? key }),
                created_at = pair.grant.CreatedAt,
                last_used_at = pair.grant.LastUsedAt,
            })
            .ToList();

        return Api.Json(new { items });
    }

    private static IResult Disconnect(string appId, ForgeContext context, Db db)
    {
        CurrentUser user = SessionUser(context);

        // The grant's tokens go with it (ON DELETE CASCADE), so the app is signed out at once.
        if (db.Exec("DELETE FROM oauth_grants WHERE app_id=@appId AND user_id=@user", new { appId, user = user.Id }) == 0)
            throw ApiException.NotFound("You haven't authorized this app.");

        db.Exec("DELETE FROM oauth_codes WHERE app_id=@appId AND user_id=@user", new { appId, user = user.Id });
        return Api.Json(new { revoked = true });
    }

    // ---- staff verification ---------------------------------------------------------------------------------------

    private sealed class AdminAppRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string? HomepageUrl { get; set; }
        public string? Logo { get; set; }
        public string RedirectUris { get; set; } = "[]";
        public int PublicClient { get; set; }
        public string? VerifiedAt { get; set; }
        public string? VerifierName { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string OwnerId { get; set; } = "";
        public string? OwnerName { get; set; }
        public long Users { get; set; }
    }

    private static IResult AdminApps(HttpRequest request, ForgeContext context, Db db)
    {
        AdminEndpoints.RequireAdmin(context);
        (int page, int limit) = ModEndpoints.Paging(request, 25);
        string filter = request.Query["filter"].FirstOrDefault() ?? "all";
        string q = (request.Query["q"].FirstOrDefault() ?? "").Trim();

        if (!AdminFilters.Contains(filter))
            throw ApiException.BadRequest("Unknown filter.");

        var conditions = new List<string>();

        if (filter == "verified")
            conditions.Add("a.verified_at IS NOT NULL");
        else if (filter == "unverified")
            conditions.Add("a.verified_at IS NULL");

        if (q != "")
            conditions.Add(@"(a.name LIKE @like ESCAPE '\' OR a.id=@q OR a.owner_id=@q OR u.username LIKE @like ESCAPE '\' OR u.public_username LIKE @like ESCAPE '\')");

        string where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        var args = new { q, like = "%" + Text.LikeEscape(q) + "%", limit, offset = (page - 1) * limit };
        long total = db.Scalar<long>($"SELECT COUNT(*) FROM oauth_apps a LEFT JOIN users u ON u.id=a.owner_id {where}", args);
        var items = db.Query<AdminAppRow>($"""
            SELECT a.*, COALESCE(u.public_username,u.display_name) owner_name, COALESCE(v.public_username,v.display_name) verifier_name,
            (SELECT COUNT(*) FROM oauth_grants g WHERE g.app_id=a.id) users
            FROM oauth_apps a LEFT JOIN users u ON u.id=a.owner_id LEFT JOIN users v ON v.id=a.verified_by {where}
            ORDER BY (a.verified_at IS NULL) DESC, a.updated_at DESC LIMIT @limit OFFSET @offset
            """, args).Select(a => new
        {
            id = a.Id, name = a.Name, description = a.Description, homepage_url = a.HomepageUrl, logo = a.Logo,
            redirect_uris = JsonBody.StringList(a.RedirectUris), public_client = a.PublicClient != 0,
            verified = a.VerifiedAt != null, verified_at = a.VerifiedAt, verified_by = a.VerifierName,
            owner = new { id = a.OwnerId, name = a.OwnerName }, users = a.Users, created_at = a.CreatedAt, updated_at = a.UpdatedAt,
        }).ToList();

        return Api.Json(new { items, total, page, limit });
    }

    private static async Task<IResult> SetVerifiedAsync(string id, HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send verified as true or false.");

        if (!payload["verified"].IsBool(out bool verified))
            throw ApiException.BadRequest("Send verified as true or false.");

        int changed = verified
            ? db.Exec("UPDATE oauth_apps SET verified_at=COALESCE(verified_at,@now), verified_by=COALESCE(verified_by,@actor) WHERE id=@id", new { now = Text.NowIso(), actor = actor.Id, id })
            : db.Exec("UPDATE oauth_apps SET verified_at=NULL, verified_by=NULL WHERE id=@id", new { id });

        if (changed == 0)
            throw ApiException.NotFound("Application not found.");

        AdminPortalEndpoints.Log(db, actor, verified ? "oauth_app.verify" : "oauth_app.unverify", id);
        return Api.Json(new { verified });
    }
}
