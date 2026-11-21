using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

public static class AdminEndpoints
{
    private static readonly string[] ModerationStatuses = ["pending", "approved", "unapproved", "deleted", "all"];

    private sealed class AdminUserRow
    {
        public string Id { get; set; } = "";
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int IsAdmin { get; set; }
        public int IsDeveloper { get; set; }
        public int IsVerified { get; set; }
        public int IsBanned { get; set; }
        public long ModCount { get; set; }
    }

    private sealed class AdminCommentRow
    {
        public long Id { get; set; }
        public string Guid { get; set; } = "";
        public string AuthorId { get; set; } = "";
        public string Body { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/admin/mods", Mods);
        app.MapPatch("/api/v1/admin/mods/{guid}", ModerateAsync);
        app.MapDelete("/api/v1/admin/mods/{guid}", PermanentlyDelete);
        app.MapPut("/api/v1/admin/mods/{guid}/choice", ElectChoiceAsync);
        app.MapDelete("/api/v1/admin/mods/{guid}/choice", RemoveChoice);
        app.MapPost("/api/v1/admin/mods/{guid}/medals", AddMedalAsync);
        app.MapPatch("/api/v1/admin/mods/{guid}/medals/{id:long}", RenameMedalAsync);
        app.MapDelete("/api/v1/admin/mods/{guid}/medals/{id:long}", RemoveMedal);
        app.MapGet("/api/v1/admin/mods/{guid}/comments", Comments);
        app.MapPost("/api/v1/admin/mods/{guid}/comments", AddCommentAsync);
        app.MapGet("/api/v1/admin/users", (HttpRequest request, ForgeContext context, Db db, ForgeOptions options) => Users(request, context, db, options, false));
        app.MapGet("/api/v1/admin/admins", (HttpRequest request, ForgeContext context, Db db, ForgeOptions options) => Users(request, context, db, options, true));
        app.MapPatch("/api/v1/admin/users/{uid}", ManageUserAsync);
        app.MapPut("/api/v1/admin/admins/{uid}", ManageAdminAsync);
        app.MapPut("/api/v1/admin/developers/{uid}", ManageDeveloperAsync);
        app.MapGet("/api/v1/admin/analytics", Analytics);
        app.MapGet("/api/v1/admin/errors", Errors);
    }

    public static CurrentUser RequireAdmin(ForgeContext context, bool root = false)
    {
        CurrentUser current = context.RequireUser("Sign in with an administrator account.");

        if (!current.IsAdmin || (root && !current.IsRoot))
            throw ApiException.Forbidden(root ? "Root administrator access is required." : "Administrator access is required.");

        return current;
    }

    private static void Audit(Db db, CurrentUser actor, string action, string target)
    {
        db.Exec("INSERT INTO admin_audit(actor_id,action,target,created_at) VALUES(@actor,@action,@target,@now)", new { actor = actor.Id, action, target, now = Text.NowIso() });
    }

    private static IResult Mods(HttpRequest request, ForgeContext context, Db db, ModPresenter presenter)
    {
        RequireAdmin(context);
        (int page, int limit) = ModEndpoints.Paging(request, 25);
        string status = request.Query["status"].FirstOrDefault() ?? "pending";

        if (!ModerationStatuses.Contains(status))
            throw ApiException.BadRequest("Unknown moderation status.");

        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (status != "all")
        {
            conditions.Add("m.status=@status");
            parameters.Add("status", status);
        }

        string q = (request.Query["q"].FirstOrDefault() ?? "").Trim();

        if (q.Length > 200)
            q = q[..200];

        if (q != "")
        {
            conditions.Add("(instr(lower(m.name),lower(@q))>0 OR instr(lower(m.guid),lower(@q))>0)");
            parameters.Add("q", q);
        }

        string where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
        parameters.Add("limit", limit);
        parameters.Add("offset", (page - 1) * limit);

        long total = db.Scalar<long>("SELECT COUNT(*) FROM (" + ModListRow.BaseQuery + where + ")", parameters);
        List<ModListRow> rows = db.Query<ModListRow>(ModListRow.BaseQuery + where + " ORDER BY m.updated_at DESC,m.guid LIMIT @limit OFFSET @offset", parameters).ToList();
        Dictionary<string, long> counts = db.Query<(string Status, long N)>("SELECT status,COUNT(*) n FROM mods GROUP BY status").ToDictionary(r => r.Status, r => r.N);

        return Api.Json(new { items = rows.Select(r => presenter.Mod(r)).ToList(), total, page, limit, counts });
    }

    private static async Task<IResult> ModerateAsync(string guid, HttpRequest request, ForgeContext context, Db db, Webhooks webhooks, Subscriptions subscriptions)
    {
        CurrentUser actor = RequireAdmin(context);
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload.HasExactly("status") || !payload["status"].IsString(out string status) || status is not ("approved" or "unapproved" or "deleted"))
            throw ApiException.BadRequest("Choose approved, unapproved, or deleted.");

        string previous = db.One<string>("SELECT status FROM mods WHERE guid=@guid", new { guid }) ?? throw ApiException.NotFound("Mod not found.");

        db.BeginImmediate();
        db.Exec("UPDATE mods SET status=@status WHERE guid=@guid", new { status, guid });
        Audit(db, actor, "mod." + status, guid);
        db.Commit();
        webhooks.NotifyModStatus(guid, previous);

        // Taking a mod down ends its paid tiers straight away, so nobody keeps paying for something they can't open.
        (int cancelled, int failed) = status == "approved" ? (0, 0) : await subscriptions.TakeDownModAsync(guid);

        return Api.Json(new { guid, status, subscriptions_cancelled = cancelled, subscriptions_failed = failed });
    }

    private static async Task<IResult> PermanentlyDelete(string guid, ForgeContext context, Db db, Subscriptions subscriptions)
    {
        RequireAdmin(context);
        await subscriptions.TakeDownModAsync(guid);
        db.BeginImmediate();

        if (!db.Exists("SELECT 1 FROM mods WHERE guid=@guid", new { guid }))
            throw ApiException.NotFound("Mod not found.");

        db.Exec("DELETE FROM files WHERE release_id IN (SELECT id FROM releases WHERE guid=@guid)", new { guid });

        foreach (string table in new[] { "monkeshield_reports", "channel_share_codes", "releases", "release_channels", "mod_medals", "mod_comments", "mod_ratings", "admin_comments", "activity", "mod_transfers" })
            db.Exec($"DELETE FROM {table} WHERE guid=@guid", new { guid });

        // Extensions of a deleted mod stay up as ordinary mods.
        db.Exec("DELETE FROM mod_extensions WHERE guid=@guid OR parent_guid=@guid", new { guid });
        db.Exec("DELETE FROM mod_docs WHERE guid=@guid", new { guid });
        db.Exec("DELETE FROM admin_audit WHERE target=@guid AND action LIKE 'mod.%'", new { guid });
        db.Exec("DELETE FROM mods WHERE guid=@guid", new { guid });
        db.Commit();

        return Api.Json(new { guid, deleted = true });
    }

    public const int ChoiceNoteLimit = 1000;

    // Modder's Choice is one badge any number of mods can hold. The note is staff's reason, shown on the mod page.
    private static async Task<IResult> ElectChoiceAsync(string guid, HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser actor = RequireAdmin(context);
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);
        string note = "";

        bool valid = payload != null && payload.HasOnly("note")
            && (payload["note"] == null || payload["note"].IsString(out note));

        if (!valid)
            throw ApiException.BadRequest("Send an optional note explaining why this is Modder's Choice.");

        note = (note ?? "").Trim();

        if (Text.Len(note) > ChoiceNoteLimit)
            throw ApiException.BadRequest($"Keep the note to {ChoiceNoteLimit:N0} characters.");

        db.BeginImmediate();

        if (!db.Exists("SELECT 1 FROM mods WHERE guid=@guid AND status!='deleted'", new { guid }))
            throw new ApiException(404);

        db.Exec("UPDATE mods SET is_choice=1,choice_note=@note WHERE guid=@guid", new { note = note == "" ? null : note, guid });
        Audit(db, actor, "mod.choice", guid);
        db.Commit();

        return Api.Ok();
    }

    private static IResult RemoveChoice(string guid, ForgeContext context, Db db)
    {
        CurrentUser actor = RequireAdmin(context);
        db.BeginImmediate();

        if (!db.Exists("SELECT 1 FROM mods WHERE guid=@guid", new { guid }))
            throw new ApiException(404);

        db.Exec("UPDATE mods SET is_choice=0,choice_note=NULL WHERE guid=@guid", new { guid });
        Audit(db, actor, "mod.choice.remove", guid);
        db.Commit();

        return Api.Ok();
    }

    public const int MedalNameLimit = 80;

    private static async Task<string> MedalNameAsync(HttpRequest request)
    {
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload.HasExactly("name") || !payload["name"].IsString(out string name) || (name = name.Trim()) == "")
            throw ApiException.BadRequest("Name the medal, such as \"#1 Winter Modding Contest\".");

        if (Text.Len(name) > MedalNameLimit)
            throw ApiException.BadRequest($"Keep the medal name to {MedalNameLimit} characters.");

        return name;
    }

    private static async Task<IResult> AddMedalAsync(string guid, HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser actor = RequireAdmin(context);
        string name = await MedalNameAsync(request);
        db.BeginImmediate();

        if (!db.Exists("SELECT 1 FROM mods WHERE guid=@guid AND status!='deleted'", new { guid }))
            throw new ApiException(404);

        long id = db.Scalar<long>("INSERT INTO mod_medals(guid,name,created_by,created_at) VALUES(@guid,@name,@actor,@now) RETURNING id",
            new { guid, name, actor = actor.Id, now = Text.NowIso() });
        Audit(db, actor, "mod.medal.add", guid);
        db.Commit();

        return Api.Json(new MedalDto { Id = id, Name = name }, 201);
    }

    private static async Task<IResult> RenameMedalAsync(string guid, long id, HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser actor = RequireAdmin(context);
        string name = await MedalNameAsync(request);
        db.BeginImmediate();

        if (db.Exec("UPDATE mod_medals SET name=@name WHERE id=@id AND guid=@guid", new { name, id, guid }) == 0)
            throw ApiException.NotFound("Medal not found.");

        Audit(db, actor, "mod.medal.rename", guid);
        db.Commit();

        return Api.Json(new MedalDto { Id = id, Name = name });
    }

    private static IResult RemoveMedal(string guid, long id, ForgeContext context, Db db)
    {
        CurrentUser actor = RequireAdmin(context);
        db.BeginImmediate();

        if (db.Exec("DELETE FROM mod_medals WHERE id=@id AND guid=@guid", new { id, guid }) == 0)
            throw ApiException.NotFound("Medal not found.");

        Audit(db, actor, "mod.medal.remove", guid);
        db.Commit();

        return Api.Ok();
    }

    private static IResult Comments(string guid, ForgeContext context, Db db)
    {
        RequireAdmin(context);

        if (!db.Exists("SELECT 1 FROM mods WHERE guid=@guid", new { guid }))
            throw new ApiException(404);

        List<AdminCommentRow> items = db.Query<AdminCommentRow>(
            "SELECT c.*,COALESCE(u.public_username,u.display_name) display_name FROM admin_comments c JOIN users u ON u.id=c.author_id WHERE guid=@guid ORDER BY c.id",
            new { guid }).ToList();

        return Api.Json(new { items });
    }

    private static async Task<IResult> AddCommentAsync(string guid, HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser actor = RequireAdmin(context);
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload["body"].IsString(out string body) || body.Trim() == "" || Text.Len(body) > 5000)
            throw ApiException.BadRequest("Enter a comment of up to 5,000 characters.");

        if (!db.Exists("SELECT 1 FROM mods WHERE guid=@guid", new { guid }))
            throw new ApiException(404);

        db.BeginImmediate();
        db.Exec("INSERT INTO admin_comments(guid,author_id,body,created_at) VALUES(@guid,@actor,@body,@now)", new { guid, actor = actor.Id, body = body.Trim(), now = Text.NowIso() });
        Audit(db, actor, "mod.comment", guid);
        db.Commit();

        return Api.Json(new { ok = true }, 201);
    }

    private static IResult Users(HttpRequest request, ForgeContext context, Db db, ForgeOptions options, bool admins)
    {
        RequireAdmin(context, admins);
        (int page, int limit) = ModEndpoints.Paging(request, 25);
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (admins)
        {
            conditions.Add("(u.is_admin=1 OR u.id=@root)");
            parameters.Add("root", options.RootAdminId);
        }

        string q = (request.Query["q"].FirstOrDefault() ?? "").Trim();

        if (q.Length > 200)
            q = q[..200];

        if (q != "")
        {
            conditions.Add("(instr(lower(u.username),lower(@q))>0 OR instr(lower(u.display_name),lower(@q))>0 OR u.id=@q)");
            parameters.Add("q", q);
        }

        string where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
        parameters.Add("limit", limit);
        parameters.Add("offset", (page - 1) * limit);

        long total = db.Scalar<long>("SELECT COUNT(*) FROM users u" + where, parameters);
        List<AdminUserRow> rows = db.Query<AdminUserRow>(
            "SELECT u.id,u.username,COALESCE(u.public_username,u.display_name) display_name,u.is_admin,u.is_developer,u.is_verified,u.is_banned,(SELECT COUNT(*) FROM mods m WHERE m.author_id=u.id AND m.status!='deleted') mod_count FROM users u"
            + where + " ORDER BY u.display_name COLLATE NOCASE,u.id LIMIT @limit OFFSET @offset", parameters).ToList();

        var items = rows.Select(u =>
        {
            bool root = options.RootAdminId != "" && u.Id == options.RootAdminId;
            return new
            {
                id = u.Id, username = u.Username, display_name = u.DisplayName, is_admin = u.IsAdmin != 0 || root, is_developer = u.IsDeveloper != 0,
                is_verified = u.IsVerified != 0, is_banned = u.IsBanned != 0, mod_count = u.ModCount, is_root = root,
            };
        }).ToList();

        return Api.Json(new { items, total, page, limit });
    }

    private static async Task<IResult> ManageUserAsync(string uid, HttpRequest request, ForgeContext context, Db db, ForgeOptions options, Subscriptions subscriptions)
    {
        CurrentUser actor = RequireAdmin(context);
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || payload.Count == 0 || !payload.HasOnly("is_banned", "is_verified") || payload.Any(p => !p.Value.IsBool(out _)))
            throw ApiException.BadRequest("Only boolean is_banned and is_verified fields are allowed.");

        UserRow target = db.One<UserRow>("SELECT * FROM users WHERE id=@uid", new { uid }) ?? throw ApiException.NotFound("User not found.");
        bool banning = payload["is_banned"].IsBool(out bool ban) && ban;

        if (uid == options.RootAdminId && banning)
            throw ApiException.Forbidden("The root administrator cannot be banned.");

        if (banning && (target.IsAdmin != 0 || uid == actor.Id) && !actor.IsRoot)
            throw ApiException.Forbidden("Only the root administrator can ban an administrator.");

        db.BeginImmediate();

        foreach (KeyValuePair<string, JsonNode?> pair in payload)
        {
            pair.Value.IsBool(out bool value);
            db.Exec($"UPDATE users SET {pair.Key}=@value WHERE id=@uid", new { value = value ? 1 : 0, uid });
            Audit(db, actor, "user." + pair.Key + "." + (value ? "true" : "false"), uid);
        }

        db.Commit();

        // A banned account can't sell, so its memberships stop billing now.
        (int cancelled, int failed) = banning ? await subscriptions.StopSellerAsync(uid) : (0, 0);
        return Api.Json(new { ok = true, subscriptions_cancelled = cancelled, subscriptions_failed = failed });
    }

    private static async Task<IResult> ManageAdminAsync(string uid, HttpRequest request, ForgeContext context, Db db, ForgeOptions options)
    {
        CurrentUser actor = RequireAdmin(context, true);
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload.HasExactly("is_admin") || !payload["is_admin"].IsBool(out bool isAdmin))
            throw ApiException.BadRequest("Supply a boolean is_admin value.");

        if (uid == options.RootAdminId)
            throw ApiException.Forbidden("The root administrator is configured in .env and cannot be removed here.");

        UserRow target = db.One<UserRow>("SELECT * FROM users WHERE id=@uid", new { uid })
            ?? throw ApiException.NotFound("This user must sign in at least once before being added as an admin.");

        if (isAdmin && target.Banned)
            throw ApiException.BadRequest("Unban this user before granting administrator access.");

        db.BeginImmediate();
        db.Exec("UPDATE users SET is_admin=@isAdmin WHERE id=@uid", new { isAdmin = isAdmin ? 1 : 0, uid });
        Audit(db, actor, isAdmin ? "admin.grant" : "admin.revoke", uid);
        db.Commit();

        return Api.Ok();
    }

    // Developer is a badge for people who work on the site itself. Only the root administrator hands it out.
    private static async Task<IResult> ManageDeveloperAsync(string uid, HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser actor = RequireAdmin(context, true);
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload.HasExactly("is_developer") || !payload["is_developer"].IsBool(out bool isDeveloper))
            throw ApiException.BadRequest("Supply a boolean is_developer value.");

        UserRow target = db.One<UserRow>("SELECT * FROM users WHERE id=@uid AND deleted_at IS NULL", new { uid })
            ?? throw ApiException.NotFound("User not found.");

        if (isDeveloper && target.Banned)
            throw ApiException.BadRequest("Unban this user before giving them the Developer badge.");

        db.BeginImmediate();
        db.Exec("UPDATE users SET is_developer=@value WHERE id=@uid", new { value = isDeveloper ? 1 : 0, uid });
        Audit(db, actor, isDeveloper ? "developer.grant" : "developer.revoke", uid);
        db.Commit();

        return Api.Ok();
    }

    private static IResult Analytics(HttpRequest request, ForgeContext context, Db db, ForgeOptions options)
    {
        RequireAdmin(context);

        if (!int.TryParse(request.Query["days"].FirstOrDefault() ?? "30", out int days))
            throw ApiException.BadRequest("Days must be an integer.");

        if (days is not (7 or 30 or 90 or 365))
            throw ApiException.BadRequest("Choose 7, 30, 90, or 365 days.");

        string scope = request.Query["scope"].FirstOrDefault() ?? "site";
        string key = request.Query["key"].FirstOrDefault() ?? "";

        if (scope is not ("site" or "category" or "mod"))
            throw ApiException.BadRequest("Invalid analytics scope.");

        var conditions = new List<string> { "a.created_at>=@since" };
        string modWhere = "status!='deleted'";
        var parameters = new DynamicParameters();
        parameters.Add("since", DateTime.UtcNow.AddDays(-(days - 1)).ToString("yyyy-MM-dd"));
        parameters.Add("key", key);

        if (scope == "category")
        {
            if (!Text.Categories.Contains(key))
                throw ApiException.BadRequest("Choose a category.");

            conditions.Add("a.category=@key");
            modWhere += " AND category=@key";
        }

        if (scope == "mod")
        {
            if (!db.Exists("SELECT 1 FROM mods WHERE guid=@key", new { key }))
                throw ApiException.NotFound("Mod not found.");

            conditions.Add("a.guid=@key");
            modWhere += " AND guid=@key";
        }

        string where = " WHERE " + string.Join(" AND ", conditions);
        SqliteConnection connection = db.Connection;

        Dictionary<string, long> totals = connection.Query<(string Kind, long N)>("SELECT a.kind,COUNT(*) n FROM activity a" + where + " GROUP BY a.kind", parameters).ToDictionary(r => r.Kind, r => r.N);
        var daily = connection.Query("SELECT substr(a.created_at,1,10) day,SUM(a.kind='view') views,SUM(a.kind='download') downloads FROM activity a" + where + " GROUP BY day ORDER BY day", parameters)
            .Select(r => new { day = (string)r.day, views = (long)r.views, downloads = (long)r.downloads }).ToList();
        var countries = connection.Query("SELECT a.country,COUNT(*) activity,SUM(a.kind='download') downloads,SUM(a.kind='view') views FROM activity a" + where + " GROUP BY a.country ORDER BY activity DESC", parameters)
            .Select(r => new { country = (string)r.country, activity = (long)r.activity, downloads = (long)r.downloads, views = (long)r.views }).ToList();
        var categories = connection.Query("SELECT COALESCE(a.category,'Site pages') category,COUNT(*) activity,SUM(a.kind='download') downloads FROM activity a" + where + " GROUP BY a.category ORDER BY activity DESC", parameters)
            .Select(r => new { category = (string)r.category, activity = (long)r.activity, downloads = (long)r.downloads }).ToList();
        var top = connection.Query("SELECT a.guid,m.name,COUNT(*) downloads FROM activity a LEFT JOIN mods m ON m.guid=a.guid" + where + " AND a.kind='download' GROUP BY a.guid ORDER BY downloads DESC LIMIT 10", parameters)
            .Select(r => new { guid = (string?)r.guid, name = (string?)r.name, downloads = (long)r.downloads }).ToList();
        var lifetime = connection.QueryFirst<(long Count, long Downloads)>("SELECT COUNT(*),COALESCE(SUM(downloads),0) FROM mods WHERE " + modWhere, parameters);
        string? tracked = connection.ExecuteScalar<string?>("SELECT MIN(created_at) FROM activity");

        return Api.Json(new
        {
            scope, key, days,
            views = totals.GetValueOrDefault("view"),
            downloads = totals.GetValueOrDefault("download"),
            lifetime_downloads = lifetime.Downloads,
            mod_count = lifetime.Count,
            daily, countries, categories,
            top_mods = top,
            tracked_since = tracked,
            geo_configured = options.TrustCountryHeader,
            unknown_activity = countries.Where(c => c.country == "XX").Sum(c => c.activity),
        });
    }

    private static IResult Errors(ForgeContext context, Diagnostics diagnostics)
    {
        RequireAdmin(context);

        try
        {
            return Api.Json(new { items = diagnostics.Recent() });
        }
        catch (SqliteException)
        {
            return Api.Json(new { error = "Error history storage is unavailable." }, 503);
        }
    }
}
