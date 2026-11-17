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

// The support desk: email sent to the support address becomes a ticket, and staff answer it from the admin panel.
public static class TicketEndpoints
{
    private const int MaxReply = 20000;
    private static readonly string[] Folders = ["open", "pending", "closed", "all", "mine", "unassigned"];

    private sealed class TicketListRow
    {
        public long Id { get; set; }
        public string Reference { get; set; } = "";
        public string Subject { get; set; } = "";
        public string RequesterEmail { get; set; } = "";
        public string? RequesterName { get; set; }
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string Status { get; set; } = "";
        public string? AssigneeId { get; set; }
        public string? AssigneeName { get; set; }
        public int Unread { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string? Snippet { get; set; }
        public string? LastDirection { get; set; }
        public long MessageCount { get; set; }
        public long AttachmentCount { get; set; }
    }

    private sealed class ThreadRow
    {
        public long Id { get; set; }
        public string Direction { get; set; } = "";
        public string? AuthorId { get; set; }
        public string? AuthorName { get; set; }
        public string FromAddress { get; set; } = "";
        public string ToAddress { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string? DeliveryStatus { get; set; }
        public string? DeliveryError { get; set; }
    }

    private sealed class AttachmentRow
    {
        public string Id { get; set; } = "";
        public long MessageId { get; set; }
        public string Filename { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long Size { get; set; }
        public string StoredName { get; set; } = "";
    }

    private sealed class MacroRow
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string AuthorId { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/admin/tickets", List);
        app.MapPost("/api/v1/admin/tickets", ComposeAsync);
        app.MapPost("/api/v1/admin/tickets/check", (ForgeContext context, SupportSignal signal, ForgeOptions options) =>
        {
            AdminEndpoints.RequireAdmin(context);

            if (!options.SupportConfigured)
                throw new ApiException(503, "The support mailbox isn't configured. Set SUPPORT_EMAIL, SUPPORT_IMAP_HOST and SUPPORT_MAIL_PASSWORD.");

            signal.Pulse();
            return Api.Ok();
        });
        app.MapPost("/api/v1/admin/tickets/compose-preview", ComposePreviewAsync);
        app.MapGet("/api/v1/admin/tickets/attachments/{id}", Attachment);
        app.MapGet("/api/v1/admin/tickets/{id:long}", Detail);
        app.MapPatch("/api/v1/admin/tickets/{id:long}", UpdateAsync);
        app.MapDelete("/api/v1/admin/tickets/{id:long}", Delete);
        app.MapPost("/api/v1/admin/tickets/{id:long}/messages", ReplyAsync);
        app.MapPost("/api/v1/admin/tickets/{id:long}/preview", PreviewAsync);

        app.MapGet("/api/v1/admin/links", Links);
        app.MapGet("/api/v1/admin/macros", Macros);
        app.MapPost("/api/v1/admin/macros", (HttpRequest request, ForgeContext context, Db db) => SaveMacroAsync(null, request, context, db));
        app.MapPut("/api/v1/admin/macros/{id}", (string id, HttpRequest request, ForgeContext context, Db db) => SaveMacroAsync(id, request, context, db));
        app.MapDelete("/api/v1/admin/macros/{id}", DeleteMacro);
    }

    private static TicketRow Load(Db db, long id)
    {
        return db.One<TicketRow>("SELECT * FROM support_tickets WHERE id=@id", new { id }) ?? throw ApiException.NotFound("Ticket not found.");
    }

    private static IResult List(HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        (int page, int limit) = ModEndpoints.Paging(request, 50);
        string folder = request.Query["folder"].FirstOrDefault() ?? "open";

        if (!Folders.Contains(folder))
            throw ApiException.BadRequest("Unknown folder.");

        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("me", actor.Id);

        conditions.Add(folder switch
        {
            "open" => "t.status='open'",
            "pending" => "t.status='pending'",
            "closed" => "t.status='closed'",
            "mine" => "t.assignee_id=@me AND t.status!='closed'",
            "unassigned" => "t.assignee_id IS NULL AND t.status='open'",
            _ => "1=1",
        });

        string q = (request.Query["q"].FirstOrDefault() ?? "").Trim();

        if (q != "")
        {
            q = q[..Math.Min(q.Length, 200)];
            conditions.Add(@"(t.subject LIKE @q ESCAPE '\' OR t.requester_email LIKE @q ESCAPE '\' OR t.reference=@exact OR CAST(t.id AS TEXT)=@number
                OR EXISTS(SELECT 1 FROM ticket_messages m WHERE m.ticket_id=t.id AND m.body LIKE @q ESCAPE '\'))");
            parameters.Add("q", "%" + Text.LikeEscape(q) + "%");
            parameters.Add("exact", q.ToUpperInvariant());
            parameters.Add("number", q.TrimStart('#'));
        }

        string where = " WHERE " + string.Join(" AND ", conditions);
        parameters.Add("limit", limit);
        parameters.Add("offset", (page - 1) * limit);

        long total = db.Scalar<long>("SELECT COUNT(*) FROM support_tickets t" + where, parameters);
        List<TicketListRow> rows = db.Query<TicketListRow>("""
            SELECT t.*,COALESCE(u.public_username,u.username) user_name,COALESCE(a.public_username,a.display_name) assignee_name,
                (SELECT substr(body,1,220) FROM ticket_messages m WHERE m.ticket_id=t.id AND m.direction!='auto' ORDER BY m.id DESC LIMIT 1) snippet,
                (SELECT direction FROM ticket_messages m WHERE m.ticket_id=t.id AND m.direction!='auto' ORDER BY m.id DESC LIMIT 1) last_direction,
                (SELECT COUNT(*) FROM ticket_messages m WHERE m.ticket_id=t.id AND m.direction IN ('in','out')) message_count,
                (SELECT COUNT(*) FROM ticket_attachments x JOIN ticket_messages m ON m.id=x.message_id WHERE m.ticket_id=t.id) attachment_count
            FROM support_tickets t LEFT JOIN users u ON u.id=t.user_id LEFT JOIN users a ON a.id=t.assignee_id
            """ + where + " ORDER BY t.updated_at DESC,t.id DESC LIMIT @limit OFFSET @offset", parameters).ToList();

        var counts = db.Connection.QueryFirst<(long Open, long Pending, long Closed, long All, long Mine, long Unassigned, long Unread)>("""
            SELECT COALESCE(SUM(status='open'),0),COALESCE(SUM(status='pending'),0),COALESCE(SUM(status='closed'),0),COUNT(*),
                COALESCE(SUM(assignee_id=@me AND status!='closed'),0),COALESCE(SUM(assignee_id IS NULL AND status='open'),0),COALESCE(SUM(unread=1 AND status='open'),0)
            FROM support_tickets
            """, new { me = actor.Id });

        return Api.Json(new
        {
            items = rows.Select(r => new
            {
                id = r.Id, reference = r.Reference, subject = r.Subject, requester = new { email = r.RequesterEmail, name = r.RequesterName },
                user = r.UserId == null ? null : new { id = r.UserId, name = r.UserName },
                status = r.Status, assignee = r.AssigneeId == null ? null : new { id = r.AssigneeId, name = r.AssigneeName },
                unread = r.Unread != 0, created_at = r.CreatedAt, updated_at = r.UpdatedAt, snippet = r.Snippet, last_direction = r.LastDirection,
                message_count = r.MessageCount, attachment_count = r.AttachmentCount,
            }).ToList(),
            total, page, limit,
            counts = new { open = counts.Open, pending = counts.Pending, closed = counts.Closed, all = counts.All, mine = counts.Mine, unassigned = counts.Unassigned, unread = counts.Unread },
        });
    }

    private static IResult Detail(long id, ForgeContext context, Db db, ForgeOptions options)
    {
        AdminEndpoints.RequireAdmin(context);
        TicketRow ticket = Load(db, id);
        db.Exec("UPDATE support_tickets SET unread=0 WHERE id=@id", new { id });

        List<ThreadRow> messages = db.Query<ThreadRow>("""
            SELECT m.*,COALESCE(u.public_username,u.username) author_name,o.status delivery_status,o.error delivery_error
            FROM ticket_messages m LEFT JOIN users u ON u.id=m.author_id LEFT JOIN email_outbox o ON o.id=m.outbox_id
            WHERE m.ticket_id=@id ORDER BY m.id
            """, new { id }).ToList();
        ILookup<long, AttachmentRow> attachments = db.Query<AttachmentRow>("SELECT x.* FROM ticket_attachments x JOIN ticket_messages m ON m.id=x.message_id WHERE m.ticket_id=@id", new { id })
            .ToLookup(a => a.MessageId);

        UserRow? user = ticket.UserId == null ? null : db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = ticket.UserId });
        var others = db.Query<TicketRow>("SELECT * FROM support_tickets WHERE requester_email=@email AND id!=@id ORDER BY id DESC LIMIT 10", new { email = ticket.RequesterEmail, id })
            .Select(t => new { id = t.Id, reference = t.Reference, subject = t.Subject, status = t.Status, updated_at = t.UpdatedAt }).ToList();

        return Api.Json(new
        {
            id = ticket.Id,
            reference = ticket.Reference,
            subject = ticket.Subject,
            status = ticket.Status,
            assignee_id = ticket.AssigneeId,
            created_at = ticket.CreatedAt,
            updated_at = ticket.UpdatedAt,
            requester = new { email = ticket.RequesterEmail, name = ticket.RequesterName },
            greeting = user == null ? ticket.RequesterEmail : user.PublicUsername ?? user.Username,
            user = user == null ? null : new
            {
                id = user.Id, name = user.PublicName, username = user.PublicUsername ?? user.Username, avatar = user.Avatar, provider = user.Provider,
                is_banned = user.Banned, is_verified = user.Verified, created_at = user.CreatedAt, stripe_account_id = user.StripeAccountId,
                mod_count = db.Scalar<long>("SELECT COUNT(*) FROM mods WHERE author_id=@id AND status!='deleted'", new { id = user.Id }),
                profile_url = options.PublicUrl + Text.ProfileUrl(user.Id, user.CustomSlug, user.Verified),
            },
            other_tickets = others,
            messages = messages.Select(m => new
            {
                id = m.Id, direction = m.Direction, author = m.AuthorId == null ? null : new { id = m.AuthorId, name = m.AuthorName },
                from = m.FromAddress, to = m.ToAddress, subject = m.Subject, body = m.Body, created_at = m.CreatedAt,
                delivery = m.DeliveryStatus == null ? null : new { status = m.DeliveryStatus, error = m.DeliveryError },
                attachments = attachments[m.Id].Select(a => new { id = a.Id, filename = a.Filename, content_type = a.ContentType, size = a.Size }).ToList(),
            }).ToList(),
        });
    }

    private static string ReadBody(JsonObject payload)
    {
        if (!payload["body"].IsString(out string body) || body.Trim() == "" || Text.Len(body) > MaxReply)
            throw ApiException.BadRequest($"Write a message of up to {MaxReply:N0} characters.");

        return body.Trim();
    }

    private static string? ReadStatus(JsonObject payload)
    {
        if (payload["status"] == null)
            return null;

        if (!payload["status"].IsString(out string status) || status is not ("open" or "pending" or "closed"))
            throw ApiException.BadRequest("Status must be open, pending or closed.");

        return status;
    }

    private static void RequireSupport(ForgeOptions options)
    {
        if (!options.SupportConfigured)
            throw new ApiException(503, "The support mailbox isn't configured, so replies can't be sent. Set SUPPORT_EMAIL, SUPPORT_IMAP_HOST and SUPPORT_MAIL_PASSWORD.");
    }

    private static async Task<IResult> ReplyAsync(long id, HttpRequest request, ForgeContext context, Db db, SupportDesk desk, ForgeOptions options, Mailer mailer)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send a message.");
        string kind = payload["kind"].IsString(out string k) ? k : "reply";

        if (kind is not ("reply" or "note"))
            throw ApiException.BadRequest("Kind must be reply or note.");

        string body = ReadBody(payload);
        string? status = ReadStatus(payload);
        TicketRow ticket = Load(db, id);

        if (kind == "reply")
            RequireSupport(options);

        db.BeginImmediate();

        if (kind == "note")
        {
            db.Exec("INSERT INTO ticket_messages(ticket_id,direction,author_id,from_address,body,created_at) VALUES(@id,'note',@actor,'',@body,@now)",
                new { id, actor = actor.Id, body, now = Text.NowIso() });
        }
        else
        {
            desk.Reply(ticket, actor, body);
            status ??= "pending";
        }

        // Answering a ticket nobody owns makes it yours.
        db.Exec("UPDATE support_tickets SET status=COALESCE(@status,status),assignee_id=COALESCE(assignee_id,CASE WHEN @reply THEN @actor END),unread=0,updated_at=@now WHERE id=@id",
            new { status, reply = kind == "reply", actor = actor.Id, now = Text.NowIso(), id });
        AdminPortalEndpoints.Log(db, actor, "ticket." + kind, ticket.Reference);
        db.Commit();
        mailer.Pulse();

        return Api.Json(new { ok = true }, 201);
    }

    private static async Task<IResult> UpdateAsync(long id, HttpRequest request, ForgeContext context, Db db, ForgeOptions options)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send the fields to change.");

        if (payload.Count == 0 || !payload.HasOnly("status", "assignee_id", "subject", "unread"))
            throw ApiException.BadRequest("Only status, assignee_id, subject and unread can change.");

        TicketRow ticket = Load(db, id);
        db.BeginImmediate();

        if (ReadStatus(payload) is string status)
        {
            db.Exec("UPDATE support_tickets SET status=@status,updated_at=@now WHERE id=@id", new { status, now = Text.NowIso(), id });
            AdminPortalEndpoints.Log(db, actor, "ticket.status." + status, ticket.Reference);
        }

        if (payload.ContainsKey("assignee_id"))
        {
            string? assignee = payload["assignee_id"] == null ? null : payload["assignee_id"].IsString(out string a) ? a : throw ApiException.BadRequest("assignee_id must be a user ID or null.");

            if (assignee != null && !db.Exists("SELECT 1 FROM users WHERE id=@assignee AND (is_admin=1 OR id=@root) AND deleted_at IS NULL", new { assignee, root = options.RootAdminId }))
                throw ApiException.BadRequest("Tickets can only be assigned to administrators.");

            db.Exec("UPDATE support_tickets SET assignee_id=@assignee WHERE id=@id", new { assignee, id });
            AdminPortalEndpoints.Log(db, actor, assignee == null ? "ticket.unassign" : "ticket.assign", ticket.Reference);
        }

        if (payload.ContainsKey("subject"))
        {
            if (!payload["subject"].IsString(out string subject) || subject.Trim() == "" || Text.Len(subject) > 200)
                throw ApiException.BadRequest("Give the ticket a title of up to 200 characters.");

            db.Exec("UPDATE support_tickets SET subject=@subject WHERE id=@id", new { subject = Mailer.CleanSubject(subject), id });
        }

        if (payload["unread"].IsBool(out bool unread))
            db.Exec("UPDATE support_tickets SET unread=@unread WHERE id=@id", new { unread = unread ? 1 : 0, id });

        db.Commit();
        return Api.Ok();
    }

    private static IResult Delete(long id, ForgeContext context, Db db, SupportDesk desk)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        TicketRow ticket = Load(db, id);
        List<string> files = db.Query<string>("SELECT x.stored_name FROM ticket_attachments x JOIN ticket_messages m ON m.id=x.message_id WHERE m.ticket_id=@id", new { id }).ToList();

        db.BeginImmediate();
        db.Exec("DELETE FROM ticket_attachments WHERE message_id IN (SELECT id FROM ticket_messages WHERE ticket_id=@id)", new { id });
        db.Exec("DELETE FROM ticket_messages WHERE ticket_id=@id", new { id });
        db.Exec("DELETE FROM support_tickets WHERE id=@id", new { id });
        AdminPortalEndpoints.Log(db, actor, "ticket.delete", ticket.Reference);
        db.Commit();

        foreach (string file in files)
        {
            try
            {
                File.Delete(Path.Combine(desk.AttachmentDir, file));
            }
            catch (IOException)
            {
            }
        }

        return Api.Ok();
    }

    private static async Task<IResult> ComposeAsync(HttpRequest request, ForgeContext context, Db db, SupportDesk desk, ForgeOptions options, AuthThrottle throttle, Mailer mailer)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        RequireSupport(options);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send an address, subject and message.");
        string? userId = payload["user_id"].IsString(out string uid) ? uid : null;
        string to;

        if (userId != null)
        {
            UserRow user = db.One<UserRow>("SELECT * FROM users WHERE id=@userId AND deleted_at IS NULL", new { userId }) ?? throw ApiException.NotFound("User not found.");
            to = user.Email != null && user.EmailVerified != 0 ? user.Email : throw ApiException.BadRequest("This user has no confirmed email address.");
        }
        else if (!payload["to"].IsString(out to) || to.Trim().Length > 254 || !Text.Email().IsMatch(to.Trim()))
        {
            throw ApiException.BadRequest("Enter a valid email address.");
        }

        if (!payload["subject"].IsString(out string subject) || subject.Trim() == "" || Text.Len(subject) > 200)
            throw ApiException.BadRequest("Write a subject of up to 200 characters.");

        string body = ReadBody(payload);
        throttle.Hit("support-compose:" + actor.Id, 60, TimeSpan.FromHours(1), "You've started a lot of conversations. Try again in a while.");

        db.BeginImmediate();
        TicketRow ticket = desk.Open(to.Trim().ToLowerInvariant(), subject.Trim(), userId);
        db.Exec("UPDATE support_tickets SET assignee_id=@actor WHERE id=@id", new { actor = actor.Id, id = ticket.Id });
        desk.Compose(ticket, actor, body);
        AdminPortalEndpoints.Log(db, actor, "ticket.compose", ticket.Reference);
        db.Commit();
        mailer.Pulse();

        return Api.Json(new { id = ticket.Id, reference = ticket.Reference }, 201);
    }

    private static async Task<IResult> PreviewAsync(long id, HttpRequest request, ForgeContext context, Db db, SupportDesk desk, EmailPreviews previews)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send a message.");
        string body = ReadBody(payload);
        string token = previews.Add(actor.Id, desk.PreviewReply(Load(db, id), actor, body, false));
        return Api.Json(new { url = "/api/v1/admin/email/preview/" + token });
    }

    private static async Task<IResult> ComposePreviewAsync(HttpRequest request, ForgeContext context, SupportDesk desk, EmailPreviews previews)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send a message.");
        string body = ReadBody(payload);
        string subject = payload["subject"].IsString(out string s) && s.Trim() != "" ? s.Trim() : "(no subject)";
        string to = payload["to"].IsString(out string t) ? t.Trim() : "";
        var draft = new TicketRow { Id = 0, Reference = "MF-XXXXXXXX", Subject = subject, RequesterEmail = to };
        string token = previews.Add(actor.Id, desk.PreviewReply(draft, actor, body, true));
        return Api.Json(new { url = "/api/v1/admin/email/preview/" + token });
    }

    private static IResult Attachment(string id, ForgeContext context, Db db, SupportDesk desk)
    {
        AdminEndpoints.RequireAdmin(context);
        AttachmentRow row = db.One<AttachmentRow>("SELECT * FROM ticket_attachments WHERE id=@id", new { id }) ?? throw ApiException.NotFound("Attachment not found.");
        string path = Path.Combine(desk.AttachmentDir, row.StoredName);

        if (!File.Exists(path))
            throw ApiException.NotFound("The attachment file is missing.");

        // Always a download, never rendered: an attachment is whatever a stranger chose to send.
        return Results.File(path, "application/octet-stream", row.Filename);
    }

    // Quick links for replies: docs articles, mods, profiles and the site's own pages.
    private static IResult Links(HttpRequest request, ForgeContext context, Db db, DocsStore docs, ForgeOptions options)
    {
        AdminEndpoints.RequireAdmin(context);
        string q = (request.Query["q"].FirstOrDefault() ?? "").Trim();
        string like = "%" + Text.LikeEscape(q[..Math.Min(q.Length, 100)]) + "%";
        bool Matches(string value) => q == "" || value.Contains(q, StringComparison.OrdinalIgnoreCase);
        string site = options.PublicUrl;

        var resources = new (string Title, string Path, string Hint)[]
        {
            ("Browse mods", "/browse", "Catalog"), ("Upload a mod", "/upload", "Publishing"), ("Launchpad app", "/software/launchpad", "Software"),
            ("mforge CLI", "/software/cli", "Software"), ("OpenForge server", "/software/openforge", "Software"), ("Docs home", "/docs", "Docs"),
            ("Account security settings", "/settings/security", "Settings"), ("Email settings", "/settings/emails", "Settings"),
            ("Payout settings", "/settings/payouts", "Settings"), ("API keys", "/settings/keys", "Settings"),
            ("Sign in", "/login", "Account"), ("Reset password", "/login?forgot=1", "Account"),
            ("Terms of Service", "/terms", "Legal"), ("Privacy Policy", "/privacy", "Legal"),
        };

        var items = new List<object>();
        items.AddRange(docs.Pages.Where(p => Matches(p.Title) || Matches(p.Slug) || Matches(p.Section)).Take(8)
            .Select(p => new { type = "doc", title = p.Title, hint = p.Section == "" ? "Docs" : "Docs · " + p.Section, url = site + p.Url }));
        items.AddRange(resources.Where(r => Matches(r.Title) || Matches(r.Hint)).Take(8).Select(r => new { type = "page", title = r.Title, hint = r.Hint, url = site + r.Path }));

        if (q != "")
        {
            items.AddRange(db.Query<ModListRow>(ModListRow.BaseQuery + @" WHERE m.status='approved' AND m.visibility='public' AND (m.name LIKE @like ESCAPE '\' OR m.guid LIKE @like ESCAPE '\') ORDER BY m.downloads DESC LIMIT 8", new { like })
                .Select(m => new { type = "mod", title = m.Name, hint = "Mod · by " + m.DisplayName, url = site + "/view/" + Uri.EscapeDataString(m.IsVerified != 0 && m.CustomSlug != null ? m.CustomSlug : m.Guid) }));
            items.AddRange(db.Query<UserRow>(@"SELECT * FROM users WHERE deleted_at IS NULL AND is_banned=0 AND (COALESCE(public_username,username) LIKE @like ESCAPE '\' OR display_name LIKE @like ESCAPE '\') LIMIT 6", new { like })
                .Select(u => new { type = "user", title = u.PublicName, hint = "Profile · @" + (u.PublicUsername ?? u.Username), url = site + Text.ProfileUrl(u.Id, u.CustomSlug, u.Verified) }));
        }

        return Api.Json(new { items });
    }

    private static IResult Macros(ForgeContext context, Db db)
    {
        AdminEndpoints.RequireAdmin(context);
        return Api.Json(new { items = db.Query<MacroRow>("SELECT * FROM support_macros ORDER BY title COLLATE NOCASE").Select(m => new { id = m.Id, title = m.Title, body = m.Body, updated_at = m.UpdatedAt }).ToList() });
    }

    private static async Task<IResult> SaveMacroAsync(string? id, HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send a title and body.");

        if (!payload["title"].IsString(out string title) || title.Trim() == "" || Text.Len(title) > 80)
            throw ApiException.BadRequest("Give the saved reply a title of up to 80 characters.");

        string body = ReadBody(payload);

        if (id == null)
        {
            id = Text.Hex(8);
            db.Exec("INSERT INTO support_macros VALUES(@id,@title,@body,@actor,@now)", new { id, title = title.Trim(), body, actor = actor.Id, now = Text.NowIso() });
        }
        else if (db.Exec("UPDATE support_macros SET title=@title,body=@body,updated_at=@now WHERE id=@id", new { id, title = title.Trim(), body, now = Text.NowIso() }) == 0)
        {
            throw ApiException.NotFound("Saved reply not found.");
        }

        return Api.Json(new { id });
    }

    private static IResult DeleteMacro(string id, ForgeContext context, Db db)
    {
        AdminEndpoints.RequireAdmin(context);
        db.Exec("DELETE FROM support_macros WHERE id=@id", new { id });
        return Api.Ok();
    }
}
