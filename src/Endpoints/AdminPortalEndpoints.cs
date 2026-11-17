using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// The admin panel's pages, the sign-in handoff from the public site to the admin port, and the panel's overview screens.
public static class AdminPortalEndpoints
{
    private const double HandoffSeconds = 60;

    private sealed class AuditRow
    {
        public long Id { get; set; }
        public string ActorId { get; set; } = "";
        public string? ActorName { get; set; }
        public string Action { get; set; } = "";
        public string Target { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }

    private sealed class OutboxListRow
    {
        public long Id { get; set; }
        public string? UserId { get; set; }
        public string ToAddress { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Sender { get; set; } = "";
        public string Status { get; set; } = "";
        public int Attempts { get; set; }
        public double Created { get; set; }
        public double? Sent { get; set; }
        public double NextAttempt { get; set; }
        public string? Error { get; set; }
        public int HasBody { get; set; }
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin", Entry);
        app.MapGet("/admin/handoff", Handoff);
        app.MapGet("/admin/{**path}", (HttpContext http, ForgeOptions options, AdminShell shell) => shell.Page());

        app.MapGet("/api/v1/admin/dashboard", Dashboard);
        app.MapGet("/api/v1/admin/audit", Audit);
        app.MapGet("/api/v1/admin/staff", Staff);
        app.MapGet("/api/v1/admin/mail", MailQueue);
        app.MapPost("/api/v1/admin/mail/{id:long}/retry", RetryMail);
        app.MapGet("/api/v1/admin/email/preview/{token}", Preview);
    }

    public static void Log(Db db, CurrentUser actor, string action, string target)
    {
        db.Exec("INSERT INTO admin_audit(actor_id,action,target,created_at) VALUES(@actor,@action,@target,@now)", new { actor = actor.Id, action, target, now = Text.NowIso() });
    }

    // On the admin port (or when the panel shares the main port) this is the panel. On the public site it signs the
    // admin in on the admin port with a one-minute, single-use token, since that port may be another origin entirely.
    private static IResult Entry(HttpContext http, ForgeContext context, ForgeOptions options, AdminShell shell, Db db)
    {
        if (!options.AdminPortEnabled || AdminPortal.IsAdminPort(http, options))
            return shell.Page();

        CurrentUser? current = context.User;

        if (current == null)
            return Results.Redirect("/login?next=/admin");

        AdminEndpoints.RequireAdmin(context);
        string token = Text.UrlToken(32);
        double now = Text.NowEpoch();
        db.Exec("DELETE FROM admin_handoffs WHERE expires<=@now", new { now });
        db.Exec("INSERT INTO admin_handoffs VALUES(@hash,@user,@epoch,@expires)", new { hash = Text.Sha256Hex(token), user = current.Id, epoch = current.Row.SessionEpoch, expires = now + HandoffSeconds });
        http.Response.Headers["Referrer-Policy"] = "no-referrer";

        return Results.Redirect(options.AdminUrl + "/admin/handoff?token=" + Uri.EscapeDataString(token));
    }

    private sealed class HandoffRow
    {
        public string UserId { get; set; } = "";
        public long Epoch { get; set; }
    }

    private static IResult Handoff(HttpRequest request, HttpResponse response, ForgeContext context, ForgeOptions options, Db db)
    {
        response.Headers["Referrer-Policy"] = "no-referrer";
        string token = request.Query["token"].FirstOrDefault() ?? "";
        string hash = Text.Sha256Hex(token);
        HandoffRow? row = token.Length is < 20 or > 100 ? null : db.One<HandoffRow>("SELECT user_id,epoch FROM admin_handoffs WHERE token_hash=@hash AND expires>@now", new { hash, now = Text.NowEpoch() });
        db.Exec("DELETE FROM admin_handoffs WHERE token_hash=@hash", new { hash });

        if (row == null)
            return Results.Redirect("/admin?signin=expired");

        UserRow? user = db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = row.UserId });
        bool root = options.RootAdminId != "" && row.UserId == options.RootAdminId;

        if (user == null || user.SessionEpoch != row.Epoch || (!root && (user.IsAdmin == 0 || user.Banned || user.DeletedAt != null)))
            return Results.Redirect("/admin?signin=denied");

        // Keep the existing session when it's already this admin, so a shared cookie doesn't get a new CSRF token under an open tab.
        if (context.User?.Id != row.UserId)
            context.SignIn(row.UserId);

        return Results.Redirect("/admin");
    }

    private static IResult Dashboard(ForgeContext context, Db db, ForgeOptions options, Diagnostics diagnostics)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        string week = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        string day = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss");

        var tickets = db.Connection.QueryFirst<(long Open, long Pending, long Unassigned, long Mine, long Unread)>("""
            SELECT COALESCE(SUM(status='open'),0), COALESCE(SUM(status='pending'),0), COALESCE(SUM(status='open' AND assignee_id IS NULL),0),
                COALESCE(SUM(status!='closed' AND assignee_id=@me),0), COALESCE(SUM(unread=1 AND status!='closed'),0) FROM support_tickets
            """, new { me = actor.Id });

        int recentErrors = 0;

        try
        {
            recentErrors = diagnostics.Recent().Count(e => string.CompareOrdinal(e.CreatedAt, day) >= 0);
        }
        catch (SqliteException)
        {
        }

        List<AuditRow> audit = db.Query<AuditRow>("""
            SELECT a.*,COALESCE(u.public_username,u.display_name) actor_name FROM admin_audit a LEFT JOIN users u ON u.id=a.actor_id ORDER BY a.id DESC LIMIT 12
            """).ToList();

        return Api.Json(new
        {
            reviews = new
            {
                pending = db.Scalar<long>("SELECT COUNT(*) FROM mods WHERE status='pending'"),
                oldest = db.One<string?>("SELECT MIN(updated_at) FROM mods WHERE status='pending'"),
                decided_week = db.Scalar<long>("SELECT COUNT(*) FROM mod_reviews WHERE decision IN ('approved','denied') AND created_at>=@week", new { week }),
            },
            tickets = new { open = tickets.Open, pending = tickets.Pending, unassigned = tickets.Unassigned, mine = tickets.Mine, unread = tickets.Unread },
            disputes_open = db.Scalar<long>("SELECT COUNT(*) FROM staff_disputes WHERE status='open'"),
            mail = new
            {
                queued = db.Scalar<long>("SELECT COUNT(*) FROM email_outbox WHERE status='queued'"),
                failed_week = db.Scalar<long>("SELECT COUNT(*) FROM email_outbox WHERE status='failed' AND created>=@since", new { since = Text.NowEpoch() - 7 * 86400 }),
            },
            users = new
            {
                total = db.Scalar<long>("SELECT COUNT(*) FROM users WHERE is_group=0 AND deleted_at IS NULL"),
                new_week = db.Scalar<long>("SELECT COUNT(*) FROM users WHERE is_group=0 AND deleted_at IS NULL AND created_at>=@week", new { week }),
                banned = db.Scalar<long>("SELECT COUNT(*) FROM users WHERE is_banned=1 AND deleted_at IS NULL"),
            },
            downloads_week = db.Scalar<long>("SELECT COUNT(*) FROM activity WHERE kind='download' AND created_at>=@week", new { week }),
            errors_day = recentErrors,
            support = new
            {
                configured = options.SupportConfigured,
                address = options.SupportEmail,
                last_check = SupportInbox.LastCheck?.ToString("o"),
                last_error = SupportInbox.LastError,
            },
            audit = audit.Select(Present).ToList(),
        });
    }

    private static object Present(AuditRow a)
    {
        return new { id = a.Id, actor = new { id = a.ActorId, name = a.ActorName ?? a.ActorId }, action = a.Action, target = a.Target, created_at = a.CreatedAt };
    }

    private static IResult Audit(HttpRequest request, ForgeContext context, Db db)
    {
        AdminEndpoints.RequireAdmin(context);
        (int page, int limit) = ModEndpoints.Paging(request, 50);
        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        string q = (request.Query["q"].FirstOrDefault() ?? "").Trim();
        string action = (request.Query["action"].FirstOrDefault() ?? "").Trim();

        if (q != "")
        {
            conditions.Add(@"(a.target LIKE @q ESCAPE '\' OR a.actor_id=@exact OR COALESCE(u.public_username,u.display_name) LIKE @q ESCAPE '\')");
            parameters.Add("q", "%" + Text.LikeEscape(q[..Math.Min(q.Length, 200)]) + "%");
            parameters.Add("exact", q);
        }

        if (action != "")
        {
            conditions.Add(@"a.action LIKE @action ESCAPE '\'");
            parameters.Add("action", Text.LikeEscape(action[..Math.Min(action.Length, 60)]) + "%");
        }

        string where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        const string from = " FROM admin_audit a LEFT JOIN users u ON u.id=a.actor_id";
        parameters.Add("limit", limit);
        parameters.Add("offset", (page - 1) * limit);

        long total = db.Scalar<long>("SELECT COUNT(*)" + from + where, parameters);
        List<AuditRow> rows = db.Query<AuditRow>("SELECT a.*,COALESCE(u.public_username,u.display_name) actor_name" + from + where + " ORDER BY a.id DESC LIMIT @limit OFFSET @offset", parameters).ToList();

        return Api.Json(new { items = rows.Select(Present).ToList(), total, page, limit });
    }

    private static IResult Staff(ForgeContext context, Db db, ForgeOptions options)
    {
        AdminEndpoints.RequireAdmin(context);
        var items = db.Query<UserRow>("SELECT * FROM users WHERE (is_admin=1 OR id=@root) AND is_banned=0 AND deleted_at IS NULL ORDER BY COALESCE(public_username,display_name) COLLATE NOCASE", new { root = options.RootAdminId })
            .Select(u => new { id = u.Id, name = u.PublicName, username = u.PublicUsername ?? u.Username, avatar = u.Avatar }).ToList();

        return Api.Json(new { items, me = context.User!.Id });
    }

    private static IResult MailQueue(HttpRequest request, ForgeContext context, Db db, ForgeOptions options)
    {
        AdminEndpoints.RequireAdmin(context);
        (int page, int limit) = ModEndpoints.Paging(request, 50);
        string status = request.Query["status"].FirstOrDefault() ?? "all";

        if (status is not ("all" or "queued" or "sent" or "failed"))
            throw ApiException.BadRequest("Choose queued, sent, failed or all.");

        string q = (request.Query["q"].FirstOrDefault() ?? "").Trim();
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (status != "all")
        {
            conditions.Add("status=@status");
            parameters.Add("status", status);
        }

        if (q != "")
        {
            conditions.Add(@"(to_address LIKE @q ESCAPE '\' OR subject LIKE @q ESCAPE '\')");
            parameters.Add("q", "%" + Text.LikeEscape(q[..Math.Min(q.Length, 200)]) + "%");
        }

        string where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        parameters.Add("limit", limit);
        parameters.Add("offset", (page - 1) * limit);

        long total = db.Scalar<long>("SELECT COUNT(*) FROM email_outbox" + where, parameters);
        List<OutboxListRow> rows = db.Query<OutboxListRow>(
            "SELECT id,user_id,to_address,subject,kind,sender,status,attempts,created,sent,next_attempt,error,(text_body!='') has_body FROM email_outbox" + where + " ORDER BY id DESC LIMIT @limit OFFSET @offset", parameters).ToList();
        Dictionary<string, long> counts = db.Query<(string Status, long N)>("SELECT status,COUNT(*) FROM email_outbox GROUP BY status").ToDictionary(r => r.Status, r => r.N);

        return Api.Json(new
        {
            items = rows.Select(r => new
            {
                id = r.Id, user_id = r.UserId, to = r.ToAddress, subject = r.Subject, kind = r.Kind, sender = r.Sender, status = r.Status,
                attempts = r.Attempts, created = r.Created, sent = r.Sent, next_attempt = r.NextAttempt, error = r.Error, retryable = r.Status == "failed" && r.HasBody != 0,
            }).ToList(),
            total, page, limit, counts,
            accounts = new
            {
                @default = options.MailConfigured ? $"{options.MailFromName} <{options.MailFrom}>" : null,
                support = options.SupportConfigured ? $"{options.SupportFromName} <{options.SupportEmail}>" : null,
            },
        });
    }

    private static IResult RetryMail(long id, ForgeContext context, Db db, Mailer mailer)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);

        // Account mail has no body once it fails, since it carried a sign-in link; the person has to ask for a new one.
        if (db.Exec("UPDATE email_outbox SET status='queued',attempts=0,next_attempt=@now,error=NULL WHERE id=@id AND status='failed' AND text_body!=''", new { now = Text.NowEpoch(), id }) == 0)
            throw ApiException.BadRequest("Only failed emails that still have their message can be retried.");

        Log(db, actor, "email.retry", id.ToString());
        mailer.Pulse();
        return Api.Ok();
    }

    private static IResult Preview(string token, HttpContext http, ForgeContext context, EmailPreviews previews)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        string html = previews.Get(token, actor.Id) ?? throw ApiException.NotFound("This preview expired. Make a new one.");

        // Framed by the panel, with the inline styles emails need, and no scripts or remote requests beyond images.
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src 'self' data: https:; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";
        http.Response.Headers.XFrameOptions = "SAMEORIGIN";
        http.Response.Headers.CacheControl = "no-store";
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
