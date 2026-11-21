using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

public static class AdminUserEndpoints
{
    private static readonly string[] Filters = ["all", "banned", "admins", "verified", "sellers", "email", "discord", "deleted"];

    private sealed class UserListRow
    {
        public string Id { get; set; } = "";
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? PublicUsername { get; set; }
        public string? Avatar { get; set; }
        public string? Email { get; set; }
        public int EmailVerified { get; set; }
        public int IsAdmin { get; set; }
        public int IsDeveloper { get; set; }
        public int IsVerified { get; set; }
        public int IsBanned { get; set; }
        public int IsGroup { get; set; }
        public string? StripeAccountId { get; set; }
        public int StripeChargesEnabled { get; set; }
        public string? CreatedAt { get; set; }
        public string? DeletedAt { get; set; }
        public long ModCount { get; set; }
        public long PasskeyCount { get; set; }
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/admin/people", List);
        app.MapGet("/api/v1/admin/people/{uid}", Detail);
        app.MapPost("/api/v1/admin/people/{uid}/password-reset", PasswordReset);
        app.MapPost("/api/v1/admin/people/{uid}/sign-out", SignOut);
        app.MapDelete("/api/v1/admin/people/{uid}/passkeys", (string uid, ForgeContext context, Db db, ForgeOptions options, Mailer mailer) => RemovePasskeys(uid, null, context, db, options, mailer));
        app.MapDelete("/api/v1/admin/people/{uid}/passkeys/{id}", (string uid, string id, ForgeContext context, Db db, ForgeOptions options, Mailer mailer) => RemovePasskeys(uid, id, context, db, options, mailer));
        app.MapDelete("/api/v1/admin/people/{uid}", DeleteAsync);
    }

    private static bool IsRoot(ForgeOptions options, string id)
    {
        return options.RootAdminId != "" && id == options.RootAdminId;
    }

    private static object Present(UserListRow u, ForgeOptions options)
    {
        bool root = IsRoot(options, u.Id);
        return new
        {
            id = u.Id,
            nickname = u.PublicUsername ?? u.DisplayName,
            display_name = u.DisplayName,
            username = u.PublicUsername ?? u.Username,
            discord_username = u.Id.StartsWith(Text.EmailAccountPrefix, StringComparison.Ordinal) || u.IsGroup != 0 ? null : u.Username,
            avatar = u.Avatar,
            email = u.Email,
            email_verified = u.EmailVerified != 0,
            provider = u.IsGroup != 0 ? "group" : u.Id.StartsWith(Text.EmailAccountPrefix, StringComparison.Ordinal) ? "email" : "discord",
            is_admin = u.IsAdmin != 0 || root,
            is_root = root,
            is_developer = u.IsDeveloper != 0,
            is_verified = u.IsVerified != 0,
            is_banned = u.IsBanned != 0,
            is_group = u.IsGroup != 0,
            stripe_account_id = u.StripeAccountId,
            stripe_charges_enabled = u.StripeChargesEnabled != 0,
            created_at = u.CreatedAt,
            deleted_at = u.DeletedAt,
            mod_count = u.ModCount,
            passkey_count = u.PasskeyCount,
        };
    }

    private const string Select = """
        SELECT u.*,(SELECT COUNT(*) FROM mods m WHERE m.author_id=u.id AND m.status!='deleted') mod_count,
            (SELECT COUNT(*) FROM passkeys p WHERE p.user_id=u.id) passkey_count FROM users u
        """;

    private static IResult List(HttpRequest request, ForgeContext context, Db db, ForgeOptions options)
    {
        AdminEndpoints.RequireAdmin(context);
        (int page, int limit) = ModEndpoints.Paging(request, 50);
        string filter = request.Query["filter"].FirstOrDefault() ?? "all";

        if (!Filters.Contains(filter))
            throw ApiException.BadRequest("Unknown filter.");

        var conditions = new List<string> { filter == "deleted" ? "u.deleted_at IS NOT NULL" : "u.deleted_at IS NULL" };
        var parameters = new DynamicParameters();
        parameters.Add("root", options.RootAdminId);

        switch (filter)
        {
            case "banned": conditions.Add("u.is_banned=1"); break;
            case "admins": conditions.Add("(u.is_admin=1 OR u.id=@root)"); break;
            case "verified": conditions.Add("u.is_verified=1"); break;
            case "sellers": conditions.Add("u.stripe_account_id IS NOT NULL"); break;
            case "email": conditions.Add("substr(u.id,1,3)='mf_'"); break;
            case "discord": conditions.Add("substr(u.id,1,3)!='mf_' AND u.is_group=0"); break;
        }

        string q = (request.Query["q"].FirstOrDefault() ?? "").Trim();

        if (q != "")
        {
            q = q[..Math.Min(q.Length, 200)];
            conditions.Add(@"(u.username LIKE @q ESCAPE '\' OR u.display_name LIKE @q ESCAPE '\' OR u.public_username LIKE @q ESCAPE '\' OR u.email LIKE @q ESCAPE '\' OR u.id=@exact OR u.stripe_account_id=@exact)");
            parameters.Add("q", "%" + Text.LikeEscape(q) + "%");
            parameters.Add("exact", q);
        }

        string sort = (request.Query["sort"].FirstOrDefault() ?? "name") switch
        {
            "name" => "COALESCE(u.public_username,u.display_name) COLLATE NOCASE,u.id",
            "newest" => "u.created_at DESC,u.id",
            "mods" => "mod_count DESC,u.id",
            _ => throw ApiException.BadRequest("Sort by name, newest or mods."),
        };

        string where = " WHERE " + string.Join(" AND ", conditions);
        parameters.Add("limit", limit);
        parameters.Add("offset", (page - 1) * limit);

        long total = db.Scalar<long>("SELECT COUNT(*) FROM users u" + where, parameters);
        List<UserListRow> rows = db.Query<UserListRow>(Select + where + " ORDER BY " + sort + " LIMIT @limit OFFSET @offset", parameters).ToList();

        return Api.Json(new { items = rows.Select(r => Present(r, options)).ToList(), total, page, limit });
    }

    private static UserRow Target(Db db, string uid)
    {
        return db.One<UserRow>("SELECT * FROM users WHERE id=@uid", new { uid }) ?? throw ApiException.NotFound("User not found.");
    }

    // Admins can act on ordinary accounts; only the root administrator can act on another admin, and nobody on root.
    private static CurrentUser RequireOver(ForgeContext context, ForgeOptions options, UserRow target)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);

        if (IsRoot(options, target.Id) && !actor.IsRoot)
            throw ApiException.Forbidden("Only the root administrator can change the root account.");

        if (target.IsAdmin != 0 && target.Id != actor.Id && !actor.IsRoot)
            throw ApiException.Forbidden("Only the root administrator can change another administrator's account.");

        if (target.DeletedAt != null)
            throw ApiException.BadRequest("This account was deleted.");

        return actor;
    }

    private static IResult Detail(string uid, ForgeContext context, Db db, ForgeOptions options)
    {
        AdminEndpoints.RequireAdmin(context);
        UserListRow row = db.One<UserListRow>(Select + " WHERE u.id=@uid", new { uid }) ?? throw ApiException.NotFound("User not found.");
        UserRow user = Target(db, uid);

        return Api.Json(new
        {
            user = Present(row, options),
            profile_url = options.PublicUrl + Text.ProfileUrl(user.Id, user.CustomSlug, user.Verified),
            has_password = user.PasswordHash != null,
            stripe = user.StripeAccountId == null ? null : new
            {
                account_id = user.StripeAccountId,
                charges_enabled = user.StripeChargesEnabled != 0,
                transfers_enabled = user.StripeTransfersEnabled != 0,
                details_submitted = user.StripeDetailsSubmitted != 0,
                profile_issues = user.StripeProfileIssues,
            },
            passkeys = db.Connection.Query("SELECT id,name,created_at,last_used_at FROM passkeys WHERE user_id=@uid ORDER BY created_at", new { uid }),
            mods = db.Query<ModRow>("SELECT * FROM mods WHERE author_id=@uid ORDER BY updated_at DESC LIMIT 50", new { uid })
                .Select(m => new { guid = m.Guid, name = m.Name, status = m.Status, logo = m.Logo, downloads = m.Downloads }).ToList(),
            tickets = db.Query<TicketRow>("SELECT * FROM support_tickets WHERE user_id=@uid OR (requester_email=@email AND @email IS NOT NULL) ORDER BY id DESC LIMIT 20", new { uid, email = user.Email })
                .Select(t => new { id = t.Id, reference = t.Reference, subject = t.Subject, status = t.Status, updated_at = t.UpdatedAt }).ToList(),
            groups = db.Connection.Query("SELECT g.id,g.display_name name,m.role FROM group_members m JOIN users g ON g.id=m.group_id WHERE m.user_id=@uid", new { uid }),
            api_keys = db.Scalar<long>("SELECT COUNT(*) FROM api_keys WHERE user_id=@uid AND revoked_at IS NULL", new { uid }),
            live_subscriptions = db.Scalar<long>("SELECT COUNT(*) FROM subscriptions WHERE user_id=@uid AND status IN ('active','trialing','past_due')", new { uid }),
            audit = db.Connection.Query("""
                SELECT a.action,a.created_at,COALESCE(u.public_username,u.display_name) actor FROM admin_audit a LEFT JOIN users u ON u.id=a.actor_id
                WHERE a.target=@uid ORDER BY a.id DESC LIMIT 20
                """, new { uid }),
        });
    }

    private static IResult PasswordReset(string uid, ForgeContext context, Db db, ForgeOptions options, Mailer mailer)
    {
        UserRow target = Target(db, uid);
        CurrentUser actor = RequireOver(context, options, target);

        if (!options.MailConfigured)
            throw new ApiException(503, "Email isn't configured on this server.");

        if (!target.IsEmailAccount)
            throw ApiException.BadRequest("This account signs in with Discord, so it has no password to reset.");

        if (target.Email == null)
            throw ApiException.BadRequest("This account has no email address to send a reset link to.");

        string token = AccountEndpoints.IssueToken(db, target.Id, "reset", target.Email, AccountEndpoints.ResetSeconds);
        string url = options.PublicUrl + "/reset-password?token=" + Uri.EscapeDataString(token);
        string name = target.PublicUsername ?? target.Username;
        string subject = $"Reset your {options.BrandName} password";
        string html = $"""
            <p style="margin:0 0 14px;color:{Mailer.Ink}">Hi {Mailer.Encode(name)},</p>
            <p style="margin:0 0 14px;color:{Mailer.Ink}">{Mailer.Encode(options.BrandName)} staff sent you a link to choose a new password, usually because you asked support for one. The link works for one hour.</p>
            <p style="margin:0;color:{Mailer.Muted};font-size:14px">If you didn't contact us, you can ignore this email and keep your current password.</p>
            """;
        string text = $"Hi {name},\n\n{options.BrandName} staff sent you a link to choose a new password, usually because you asked support for one. The link works for one hour:\n\n{url}\n\nIf you didn't contact us, you can ignore this email and keep your current password.";
        mailer.Enqueue(target.Email, subject, text, mailer.Layout(subject, html, "Choose a new password", url), MailKinds.Account, target.Id);
        AdminPortalEndpoints.Log(db, actor, "user.password_reset", uid);

        return Api.Json(new { ok = true, to = target.Email });
    }

    private static IResult SignOut(string uid, ForgeContext context, Db db, ForgeOptions options)
    {
        UserRow target = Target(db, uid);
        CurrentUser actor = RequireOver(context, options, target);
        db.Exec("UPDATE users SET session_epoch=session_epoch+1 WHERE id=@uid", new { uid });
        AdminPortalEndpoints.Log(db, actor, "user.sign_out", uid);
        return Api.Ok();
    }

    private static IResult RemovePasskeys(string uid, string? id, ForgeContext context, Db db, ForgeOptions options, Mailer mailer)
    {
        UserRow target = Target(db, uid);
        CurrentUser actor = RequireOver(context, options, target);
        int removed = id == null
            ? db.Exec("DELETE FROM passkeys WHERE user_id=@uid", new { uid })
            : db.Exec("DELETE FROM passkeys WHERE user_id=@uid AND id=@id", new { uid, id });

        if (removed == 0)
            throw ApiException.NotFound(id == null ? "This account has no passkeys." : "Passkey not found.");

        AdminPortalEndpoints.Log(db, actor, id == null ? "user.passkeys_removed" : "user.passkey_removed", uid);

        if (target.Email != null && target.EmailVerified != 0)
            AccountEndpoints.SendNotice(mailer, options, target.Email, target.Id, $"Passkeys removed from your {options.BrandName} account",
                $"{options.BrandName} staff removed {(removed == 1 ? "a passkey" : removed + " passkeys")} from your account. You can add a new one from your security settings.");

        return Api.Json(new { removed });
    }

    // Deleting keeps the row so payments, refunds and disputes still add up, but removes everything that identifies the
    // person or lets them sign in, and takes their mods down.
    private static async Task<IResult> DeleteAsync(string uid, HttpRequest request, ForgeContext context, Db db, ForgeOptions options, Subscriptions subscriptions, Storage storage)
    {
        UserRow target = Target(db, uid);
        CurrentUser actor = RequireOver(context, options, target);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Confirm with the account's username.");

        if (IsRoot(options, uid) || uid == actor.Id)
            throw ApiException.Forbidden("You can't delete your own account or the root administrator from here.");

        if (!payload["confirm"].IsString(out string confirm) || !confirm.Trim().Equals(target.PublicUsername ?? target.Username, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest("Type the account's username to confirm.");

        List<SubscriptionRow> buying = db.Query<SubscriptionRow>("SELECT * FROM subscriptions WHERE user_id=@uid AND status IN ('active','trialing','past_due')", new { uid }).ToList();
        (int cancelled, int failed) = await subscriptions.CancelManyAsync(buying, true);
        (int sellerCancelled, int sellerFailed) = await subscriptions.StopSellerAsync(uid);
        List<string> guids = db.Query<string>("SELECT guid FROM mods WHERE author_id=@uid AND status!='deleted'", new { uid }).ToList();

        foreach (string guid in guids)
        {
            (int c, int f) = await subscriptions.TakeDownModAsync(guid);
            sellerCancelled += c;
            sellerFailed += f;
        }

        string? avatar = target.Avatar;
        string placeholder = "deleted-" + Text.Hex(6);

        db.BeginImmediate();
        db.Exec("UPDATE mods SET status='deleted' WHERE author_id=@uid", new { uid });

        foreach (string table in new[] { "passkeys", "email_tokens", "oauth_codes", "oauth_grants", "presence", "notifications", "injector_challenges" })
            db.Exec($"DELETE FROM {table} WHERE user_id=@uid", new { uid });

        db.Exec("UPDATE api_keys SET revoked_at=COALESCE(revoked_at,@now) WHERE user_id=@uid", new { uid, now = Text.NowIso() });
        // Their OAuth apps go too: an app nobody owns can't be maintained, and its secrets would outlive the account.
        db.Exec("DELETE FROM oauth_apps WHERE owner_id=@uid", new { uid });
        db.Exec("UPDATE injector_devices SET revoked_at=COALESCE(revoked_at,@now) WHERE user_id=@uid", new { uid, now = Text.NowIso() });
        db.Exec("DELETE FROM friendships WHERE sender=@uid OR recipient=@uid", new { uid });
        db.Exec("DELETE FROM group_members WHERE user_id=@uid", new { uid });
        db.Exec("DELETE FROM mod_ratings WHERE user_id=@uid", new { uid });
        db.Exec("DELETE FROM mod_comments WHERE user_id=@uid", new { uid });
        db.Exec("UPDATE mod_transfers SET status='cancelled' WHERE status='pending' AND (source=@uid OR target=@uid)", new { uid });
        db.Exec("""
            UPDATE users SET username=@placeholder,display_name='Deleted user',public_username=NULL,custom_slug=NULL,avatar=NULL,description='',links='[]',
                email=NULL,email_verified=0,email_source=NULL,password_hash=NULL,is_admin=0,is_verified=0,is_banned=1,
                email_release_notices=0,email_announcements=0,session_epoch=session_epoch+1,deleted_at=@now WHERE id=@uid
            """, new { placeholder, uid, now = Text.NowIso() });
        AdminPortalEndpoints.Log(db, actor, "user.delete", uid);
        db.Commit();

        if (avatar != null)
            storage.DeleteUnreferencedMedia([avatar]);

        return Api.Json(new
        {
            ok = true,
            mods_removed = guids.Count,
            subscriptions_cancelled = cancelled + sellerCancelled,
            subscriptions_failed = failed + sellerFailed,
        });
    }
}
