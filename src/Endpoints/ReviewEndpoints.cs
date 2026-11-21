using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// The approve/deny queue. A denial carries a reason the author sees in a notification (and by email), so they can fix
// the mod and send it back with Resubmit.
public static class ReviewEndpoints
{
    private const int MaxReason = 2000;

    private sealed class ReviewHistoryRow
    {
        public long Id { get; set; }
        public string ReviewerId { get; set; } = "";
        public string? ReviewerName { get; set; }
        public string Decision { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Version { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }

    private sealed class QueueRow
    {
        public string Guid { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Logo { get; set; }
        public string Category { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string Status { get; set; } = "";
        public string AuthorId { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string AuthorUsername { get; set; } = "";
        public int AuthorVerified { get; set; }
        public string? Report { get; set; }
        public long Denials { get; set; }
        public long PriceCents { get; set; }
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/admin/reviews", Queue);
        app.MapGet("/api/v1/admin/reviews/{guid}", Detail);
        app.MapPost("/api/v1/admin/reviews/{guid}", DecideAsync);

        app.MapGet("/api/v1/mods/{value}/review", LatestForOwner);
        app.MapPost("/api/v1/mods/{value}/resubmit", ResubmitAsync);
    }

    private const string QueueQuery = """
        SELECT m.guid,m.name,m.logo,m.category,m.current_version,m.updated_at,m.created_at,m.status,m.author_id,m.price_cents,
            COALESCE(u.public_username,u.display_name) author_name,COALESCE(u.public_username,u.username) author_username,u.is_verified author_verified,
            (SELECT report FROM monkeshield_reports r WHERE r.guid=m.guid AND r.channel='stable') report,
            (SELECT COUNT(*) FROM mod_reviews v WHERE v.guid=m.guid AND v.decision='denied') denials
        FROM mods m JOIN users u ON u.id=m.author_id
        """;

    private static object Shield(string? raw)
    {
        JsonObject report = MonkeShield.ReadReport(raw);
        return new
        {
            status = report["status"]?.ToString() ?? "not_scanned",
            eligible = report["eligible"]?.GetValueKind() == JsonValueKind.True,
            total_checks = report["total_checks"] is JsonValue total && total.TryGetValue(out long t) ? t : (long?)null,
            failed_checks = report["failed_checks"] is JsonValue failed && failed.TryGetValue(out long f) ? f : (long?)null,
        };
    }

    private static object PresentQueue(QueueRow r)
    {
        return new
        {
            guid = r.Guid, name = r.Name, logo = r.Logo, category = r.Category, version = r.CurrentVersion, status = r.Status,
            submitted_at = r.UpdatedAt, created_at = r.CreatedAt, denials = r.Denials, paid = r.PriceCents > 0,
            author = new { id = r.AuthorId, name = r.AuthorName, username = r.AuthorUsername, is_verified = r.AuthorVerified != 0 },
            shield = Shield(r.Report),
        };
    }

    // Oldest first, so nothing waits at the bottom of the pile forever.
    private static List<QueueRow> Pending(Db db)
    {
        return db.Query<QueueRow>(QueueQuery + " WHERE m.status='pending' ORDER BY m.updated_at,m.guid").ToList();
    }

    private static IResult Queue(HttpRequest request, ForgeContext context, Db db)
    {
        AdminEndpoints.RequireAdmin(context);
        string status = request.Query["status"].FirstOrDefault() ?? "pending";

        if (status is not ("pending" or "unapproved"))
            throw ApiException.BadRequest("Choose pending or unapproved.");

        List<QueueRow> rows = status == "pending" ? Pending(db) : db.Query<QueueRow>(QueueQuery + " WHERE m.status='unapproved' ORDER BY m.updated_at DESC LIMIT 200").ToList();
        return Api.Json(new
        {
            items = rows.Select(PresentQueue).ToList(),
            counts = new
            {
                pending = db.Scalar<long>("SELECT COUNT(*) FROM mods WHERE status='pending'"),
                unapproved = db.Scalar<long>("SELECT COUNT(*) FROM mods WHERE status='unapproved'"),
            },
        });
    }

    private static List<ReviewHistoryRow> History(Db db, string guid)
    {
        return db.Query<ReviewHistoryRow>("""
            SELECT r.*,COALESCE(u.public_username,u.display_name) reviewer_name FROM mod_reviews r LEFT JOIN users u ON u.id=r.reviewer_id
            WHERE r.guid=@guid ORDER BY r.id DESC
            """, new { guid }).ToList();
    }

    private static object PresentHistory(ReviewHistoryRow r)
    {
        return new { id = r.Id, reviewer = new { id = r.ReviewerId, name = r.ReviewerName ?? r.ReviewerId }, decision = r.Decision, reason = r.Reason, version = r.Version, created_at = r.CreatedAt };
    }

    private static IResult Detail(string guid, ForgeContext context, Db db, Access access, ModPresenter presenter, ForgeOptions options, Subscriptions subscriptions)
    {
        AdminEndpoints.RequireAdmin(context);
        ModListRow row = access.ModWithAuthor(guid) ?? throw ApiException.NotFound("Mod not found.");

        if (row.Status == "deleted")
            throw ApiException.NotFound("This mod was deleted.");

        UserRow author = db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = row.AuthorId })!;
        var reports = db.Query<ShieldReportRow>("SELECT * FROM monkeshield_reports WHERE guid=@guid ORDER BY channel!='stable',channel", new { guid })
            .Select(r =>
            {
                JsonObject report = (JsonObject)MonkeShield.ReadReport(r.Report).DeepClone();
                report.Remove("_claim");
                return new { channel = r.Channel, version = r.Version, release_id = r.ReleaseId, report };
            }).ToList();

        List<string> queue = Pending(db).Select(q => q.Guid).ToList();
        int position = queue.IndexOf(guid);
        var members = author.Group
            ? db.Query<UserRow>("SELECT u.* FROM users u JOIN group_members g ON g.user_id=u.id WHERE g.group_id=@id", new { id = author.Id }).Select(m => new { id = m.Id, name = m.PublicName }).ToList()
            : null;

        return Api.Json(new
        {
            mod = presenter.Mod(row, true),
            author = new
            {
                id = author.Id,
                name = author.PublicName,
                username = author.PublicUsername ?? author.Username,
                discord_username = author.DiscordName,
                avatar = author.Avatar,
                provider = author.Provider,
                is_verified = author.Verified,
                is_banned = author.Banned,
                is_admin = author.IsAdmin != 0 || author.Id == options.RootAdminId,
                is_developer = author.IsDeveloper != 0,
                is_group = author.Group,
                members,
                created_at = author.CreatedAt,
                email = author.Email,
                stripe_account_id = author.StripeAccountId,
                profile_url = Text.ProfileUrl(author.Id, author.CustomSlug, author.Verified),
                mods = db.Query<ModRow>("SELECT * FROM mods WHERE author_id=@id AND guid!=@guid AND status!='deleted' ORDER BY updated_at DESC LIMIT 20", new { id = author.Id, guid })
                    .Select(m => new { guid = m.Guid, name = m.Name, status = m.Status, logo = m.Logo }).ToList(),
                denials = db.Scalar<long>("SELECT COUNT(*) FROM mod_reviews r JOIN mods m ON m.guid=r.guid WHERE m.author_id=@id AND r.decision='denied'", new { id = author.Id }),
            },
            shield = reports,
            history = History(db, guid).Select(PresentHistory).ToList(),
            notes = Dapper.SqlMapper.Query(db.Connection, "SELECT c.id,c.body,c.created_at,COALESCE(u.public_username,u.display_name) name FROM admin_comments c JOIN users u ON u.id=c.author_id WHERE c.guid=@guid ORDER BY c.id", new { guid })
                .Select(n => new { id = (long)n.id, body = (string)n.body, created_at = (string)n.created_at, name = (string)n.name }).ToList(),
            live_subscriptions = subscriptions.LiveForMod(guid).Count,
            queue = new
            {
                position = position < 0 ? (int?)null : position + 1,
                total = queue.Count,
                previous = position > 0 ? queue[position - 1] : null,
                next = position >= 0 && position + 1 < queue.Count ? queue[position + 1] : position < 0 ? queue.FirstOrDefault() : null,
            },
        });
    }

    private static async Task<IResult> DecideAsync(string guid, HttpRequest request, ForgeContext context, Db db, Webhooks webhooks, Subscriptions subscriptions, Mailer mailer, ForgeOptions options)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send a decision and reason.");

        if (!payload["decision"].IsString(out string decision) || decision is not ("approve" or "deny"))
            throw ApiException.BadRequest("Choose approve or deny.");

        string reason = payload["reason"].IsString(out string given) ? given.Trim() : "";

        if (decision == "deny" && Text.Len(reason) < 5)
            throw ApiException.BadRequest("Tell the author why, so they know what to fix.");

        if (Text.Len(reason) > MaxReason)
            throw ApiException.BadRequest($"Keep the reason under {MaxReason:N0} characters.");

        ModRow mod = db.One<ModRow>("SELECT * FROM mods WHERE guid=@guid", new { guid }) ?? throw ApiException.NotFound("Mod not found.");

        if (mod.Status == "deleted")
            throw ApiException.BadRequest("This mod was deleted. Restore it from All mods first.");

        string status = decision == "approve" ? "approved" : "unapproved";
        string recorded = decision == "approve" ? "approved" : "denied";

        db.BeginImmediate();
        db.Exec("UPDATE mods SET status=@status WHERE guid=@guid", new { status, guid });
        db.Exec("INSERT INTO mod_reviews(guid,reviewer_id,decision,reason,version,created_at) VALUES(@guid,@actor,@recorded,@reason,@version,@now)",
            new { guid, actor = actor.Id, recorded, reason, version = mod.CurrentVersion, now = Text.NowIso() });
        AdminPortalEndpoints.Log(db, actor, "mod." + recorded, guid);

        string body = decision == "approve"
            ? $"{mod.Name} was approved and is live." + (reason == "" ? "" : " Note from staff: " + reason)
            : $"{mod.Name} wasn't approved: {reason} Fix it and resubmit from the mod page.";
        body = new string(body.EnumerateRunes().Take(400).SelectMany(r => r.ToString()).ToArray());

        List<UserRow> recipients = Recipients(db, mod.AuthorId);

        foreach (UserRow user in recipients)
            ManagementEndpoints.Notify(db, user.Id, "mod_review", body, guid);

        EmailAuthors(mailer, options, recipients, mod, decision == "approve", reason);
        db.Commit();
        mailer.Pulse();
        webhooks.NotifyModStatus(guid, mod.Status);

        (int cancelled, int failed) = status == "approved" ? (0, 0) : await subscriptions.TakeDownModAsync(guid);
        return Api.Json(new { guid, status, subscriptions_cancelled = cancelled, subscriptions_failed = failed });
    }

    // The author, or every member of the group that owns the mod.
    private static List<UserRow> Recipients(Db db, string authorId)
    {
        return db.Query<UserRow>("""
            SELECT u.* FROM users u WHERE (u.id=@owner OR u.id IN (SELECT user_id FROM group_members WHERE group_id=@owner)) AND u.is_group=0 AND u.deleted_at IS NULL
            """, new { owner = authorId }).ToList();
    }

    private static void EmailAuthors(Mailer mailer, ForgeOptions options, List<UserRow> recipients, ModRow mod, bool approved, string reason)
    {
        if (!mailer.Enabled)
            return;

        string url = options.PublicUrl + "/view/" + Uri.EscapeDataString(mod.Guid);
        string subject = approved ? $"{mod.Name} was approved" : $"{mod.Name} needs changes before it can go live";
        string reasonHtml = reason == "" ? "" : $"""
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:0 0 18px"><tr><td bgcolor="{Mailer.Surface2}" style="background:{Mailer.Surface2};border:1px solid {Mailer.Line};border-left:3px solid {(approved ? "#5cc98f" : Mailer.Accent)};border-radius:9px;padding:14px 16px">
            <div style="color:{Mailer.Muted};font-size:12px;text-transform:uppercase;letter-spacing:.06em;margin:0 0 6px">{(approved ? "Note from staff" : "Reason")}</div>{Mailer.Paragraphs(reason)}</td></tr></table>
            """;
        string html = approved
            ? $"""<p style="margin:0 0 14px;color:{Mailer.Ink}"><strong>{Mailer.Encode(mod.Name)}</strong> {Mailer.Encode(mod.CurrentVersion)} passed review and is now listed on {Mailer.Encode(options.BrandName)}.</p>{reasonHtml}"""
            : $"""<p style="margin:0 0 14px;color:{Mailer.Ink}">Staff reviewed <strong>{Mailer.Encode(mod.Name)}</strong> {Mailer.Encode(mod.CurrentVersion)} and couldn't approve it yet.</p>{reasonHtml}<p style="margin:0 0 14px;color:{Mailer.Ink}">Make the changes, then press <strong>Resubmit for review</strong> on the mod page. Publishing a new release sends it back for review too.</p>""";
        string text = approved
            ? $"{mod.Name} {mod.CurrentVersion} passed review and is now listed on {options.BrandName}.{(reason == "" ? "" : "\n\nNote from staff:\n" + reason)}\n\n{url}"
            : $"Staff reviewed {mod.Name} {mod.CurrentVersion} and couldn't approve it yet.\n\nReason:\n{reason}\n\nMake the changes, then press Resubmit for review on the mod page: {url}";

        foreach (UserRow user in recipients.Where(u => u.Email != null && u.EmailVerified != 0 && u.EmailReleaseNotices != 0 && !u.Banned))
        {
            string footer = $"You're getting this because you publish {Mailer.Encode(mod.Name)}. <a href=\"{Mailer.Encode(mailer.UnsubscribeUrl(user.Id, MailKinds.ReleasesTopic))}\" style=\"color:{Mailer.Muted}\">Unsubscribe from mod notices</a>.";
            mailer.Enqueue(user.Email!, subject, text + $"\n\nUnsubscribe: {mailer.UnsubscribeUrl(user.Id, MailKinds.ReleasesTopic)}", mailer.Layout(subject, html, approved ? "View mod" : "Open mod page", url, footer),
                MailKinds.Release, user.Id, listUnsubscribe: mailer.OneClickUnsubscribeUrl(user.Id, MailKinds.ReleasesTopic));
        }
    }

    private static ModRow OwnedMod(string value, ForgeContext context, Access access)
    {
        CurrentUser user = context.RequireUser("Sign in to manage this mod.");
        ModRow mod = access.ResolveMod(value) ?? throw ApiException.NotFound("Mod not found.");

        if (mod.Status == "deleted" || !access.CanManage(user, mod))
            throw ApiException.NotFound("Mod not found.");

        return mod;
    }

    private static IResult LatestForOwner(string value, ForgeContext context, Access access, Db db)
    {
        ModRow mod = OwnedMod(value, context, access);
        ReviewHistoryRow? latest = History(db, mod.Guid).FirstOrDefault(r => r.Decision is "approved" or "denied");

        return Api.Json(new
        {
            status = mod.Status,
            can_resubmit = mod.Status == "unapproved" && mod.Visibility == "public",
            latest = latest == null ? null : new { decision = latest.Decision, reason = latest.Reason, version = latest.Version, created_at = latest.CreatedAt },
        });
    }

    private static async Task<IResult> ResubmitAsync(string value, HttpRequest request, ForgeContext context, Access access, Db db, Webhooks webhooks)
    {
        ModRow mod = OwnedMod(value, context, access);
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);
        string note = payload?["note"].IsString(out string given) == true ? given.Trim() : "";

        if (Text.Len(note) > 1000)
            throw ApiException.BadRequest("Keep the note under 1,000 characters.");

        if (mod.Status != "unapproved")
            throw ApiException.BadRequest(mod.Status == "pending" ? "This mod is already waiting for review." : "Only mods that weren't approved can be resubmitted.");

        db.BeginImmediate();
        db.Exec("UPDATE mods SET status='pending',updated_at=@now WHERE guid=@guid AND status='unapproved'", new { now = Text.NowIso(), guid = mod.Guid });
        db.Exec("INSERT INTO mod_reviews(guid,reviewer_id,decision,reason,version,created_at) VALUES(@guid,@user,'resubmitted',@note,@version,@now)",
            new { guid = mod.Guid, user = context.User!.Id, note, version = mod.CurrentVersion, now = Text.NowIso() });
        db.Commit();
        webhooks.NotifyModStatus(mod.Guid, mod.Status);

        return Api.Json(new { guid = mod.Guid, status = "pending" });
    }
}
