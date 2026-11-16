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

// Staff announcements to every registered user, and the unsubscribe links in the emails we send.
public static class EmailEndpoints
{
    private const int MaxSubject = 150;
    private const int MaxBody = 20000;

    private sealed class BroadcastRow
    {
        public string Id { get; set; } = "";
        public string AuthorId { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public int IncludeOptedOut { get; set; }
        public long Recipients { get; set; }
        public string CreatedAt { get; set; } = "";
        public long Sent { get; set; }
        public long Queued { get; set; }
        public long Failed { get; set; }
    }

    private sealed record Recipient(string Id, string Email);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/admin/email", Overview);
        app.MapPost("/api/v1/admin/email/test", SendTestAsync);
        app.MapPost("/api/v1/admin/email/broadcasts", BroadcastAsync);

        // Mail clients post here with no session for one-click unsubscribe (RFC 8058); the signed token is the authorization.
        app.MapPost("/api/v1/email/unsubscribe", Unsubscribe).WithMetadata(new CsrfExemptAttribute());
    }

    private static (string Subject, string Body) ReadMessage(JsonObject payload)
    {
        if (!payload["subject"].IsString(out string subject) || subject.Trim() == "" || Text.Len(subject.Trim()) > MaxSubject)
            throw ApiException.BadRequest($"Write a subject of up to {MaxSubject} characters.");

        if (!payload["body"].IsString(out string body) || body.Trim() == "" || Text.Len(body) > MaxBody)
            throw ApiException.BadRequest($"Write a message of up to {MaxBody:N0} characters.");

        return (Mailer.CleanSubject(subject), body.Trim());
    }

    private static List<Recipient> Audience(Db db, bool includeOptedOut)
    {
        // Someone with a Discord account and an email account on the same address gets one copy, not two.
        return db.Query<Recipient>("""
            SELECT id, email FROM users WHERE is_group=0 AND is_banned=0 AND email IS NOT NULL AND email_verified=1
            AND (@includeOptedOut OR email_announcements=1) ORDER BY id
            """, new { includeOptedOut = includeOptedOut ? 1 : 0 }).DistinctBy(r => r.Email).ToList();
    }

    private static IResult Overview(ForgeContext context, Db db, ForgeOptions options)
    {
        AdminEndpoints.RequireAdmin(context);
        List<BroadcastRow> broadcasts = db.Query<BroadcastRow>("""
            SELECT b.*, COALESCE(u.public_username,u.display_name) author_name,
                (SELECT COUNT(*) FROM email_outbox o WHERE o.broadcast_id=b.id AND o.status='sent') sent,
                (SELECT COUNT(*) FROM email_outbox o WHERE o.broadcast_id=b.id AND o.status='queued') queued,
                (SELECT COUNT(*) FROM email_outbox o WHERE o.broadcast_id=b.id AND o.status='failed') failed
            FROM email_broadcasts b JOIN users u ON u.id=b.author_id ORDER BY b.created_at DESC LIMIT 25
            """).ToList();

        return Api.Json(new
        {
            configured = options.MailConfigured,
            from = options.MailConfigured ? $"{options.MailFromName} <{options.MailFrom}>" : null,
            per_minute = options.MailPerMinute,
            audience = new { subscribed = Audience(db, false).Count, everyone = Audience(db, true).Count },
            broadcasts = broadcasts.Select(b => new
            {
                id = b.Id,
                subject = b.Subject,
                body = b.Body,
                author = new { id = b.AuthorId, name = b.AuthorName },
                include_opted_out = b.IncludeOptedOut != 0,
                recipients = b.Recipients,
                sent = b.Sent,
                queued = b.Queued,
                failed = b.Failed,
                created_at = b.CreatedAt,
            }).ToList(),
        });
    }

    private static async Task<IResult> SendTestAsync(HttpContext http, ForgeContext context, ForgeOptions options, Mailer mailer, MarkdownRenderer markdown, AuthThrottle throttle)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);

        if (!options.MailConfigured)
            throw new ApiException(503, "Email isn't configured on this server. Set SMTP_HOST, SMTP_USERNAME, SMTP_PASSWORD and MAIL_FROM.");

        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send a subject, body and address.");

        if (!payload["to"].IsString(out string to) || to.Trim().Length > 254 || !Text.Email().IsMatch(to.Trim()))
            throw ApiException.BadRequest("Enter a valid email address to send the test to.");

        // Tests can go to any address, so each admin gets a modest allowance to keep this from becoming a spam relay.
        throttle.Hit("email-test:" + actor.Id, 20, TimeSpan.FromHours(1), "You've sent a lot of test emails. Try again in a while.");

        (string subject, string body) = ReadMessage(payload);
        (string html, string text) = mailer.BroadcastBody(null, subject, Mailer.StyleMarkdown(markdown.Render(body)), body);
        mailer.Enqueue(to.Trim(), "[Test] " + subject, text, html, MailKinds.Broadcast, actor.Id);

        return Api.Json(new { ok = true, to = to.Trim() });
    }

    private static async Task<IResult> BroadcastAsync(HttpRequest request, ForgeContext context, Db db, ForgeOptions options, Mailer mailer, MarkdownRenderer markdown)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);

        if (!options.MailConfigured)
            throw new ApiException(503, "Email isn't configured on this server. Set SMTP_HOST, SMTP_USERNAME, SMTP_PASSWORD and MAIL_FROM.");

        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send a subject and body.");
        (string subject, string body) = ReadMessage(payload);
        bool everyone = payload["include_opted_out"].IsBool(out bool value) && value;

        // Skipping people's opt-out is for legal and security notices only, so it's limited to the root administrator.
        if (everyone && !actor.IsRoot)
            throw ApiException.Forbidden("Only the root administrator can email people who unsubscribed from announcements.");

        string bodyHtml = Mailer.StyleMarkdown(markdown.Render(body));
        List<Recipient> audience = Audience(db, everyone);

        if (audience.Count == 0)
            throw ApiException.BadRequest("Nobody has a confirmed email address yet.");

        string id = Text.Hex(12);
        db.BeginImmediate();
        db.Exec("INSERT INTO email_broadcasts(id,author_id,subject,body,include_opted_out,recipients,created_at) VALUES(@id,@author,@subject,@body,@everyone,@count,@now)",
            new { id, author = actor.Id, subject, body, everyone = everyone ? 1 : 0, count = audience.Count, now = Text.NowIso() });

        foreach (Recipient recipient in audience)
        {
            (string html, string text) = mailer.BroadcastBody(recipient.Id, subject, bodyHtml, body);
            mailer.Enqueue(recipient.Email, subject, text, html, MailKinds.Broadcast, recipient.Id, id, mailer.OneClickUnsubscribeUrl(recipient.Id, MailKinds.AnnouncementsTopic));
        }

        db.Exec("INSERT INTO admin_audit(actor_id,action,target,created_at) VALUES(@actor,'email.broadcast',@id,@now)", new { actor = actor.Id, id, now = Text.NowIso() });
        db.Commit();
        mailer.Pulse();

        return Api.Json(new { id, recipients = audience.Count }, 201);
    }

    private static IResult Unsubscribe(HttpRequest request, Db db, Mailer mailer)
    {
        string token = request.Query["token"].FirstOrDefault() ?? "";

        if (token.Length > 2000 || mailer.ReadUnsubscribe(token) is not (string userId, string topic))
            throw ApiException.BadRequest("This unsubscribe link isn't valid. Change your email settings from your account instead.");

        string column = topic == MailKinds.ReleasesTopic ? "email_release_notices" : "email_announcements";
        db.Exec($"UPDATE users SET {column}=0 WHERE id=@userId", new { userId });

        return Api.Json(new { ok = true, topic });
    }
}
