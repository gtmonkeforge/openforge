using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using OpenForge.Infrastructure;
using OpenForge.Models;

namespace OpenForge.Services;

public sealed class TicketRow
{
    public long Id { get; set; }
    public string Reference { get; set; } = "";
    public string Subject { get; set; } = "";
    public string RequesterEmail { get; set; } = "";
    public string? RequesterName { get; set; }
    public string? UserId { get; set; }
    public string Status { get; set; } = "open";
    public string? AssigneeId { get; set; }
    public int Unread { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class TicketMessageRow
{
    public long Id { get; set; }
    public long TicketId { get; set; }
    public string Direction { get; set; } = "";
    public string? AuthorId { get; set; }
    public string FromAddress { get; set; } = "";
    public string ToAddress { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string? MessageId { get; set; }
    public long? OutboxId { get; set; }
    public string CreatedAt { get; set; } = "";
}

// Wakes the inbox poller early, for the "Check mail" button.
public sealed class SupportSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Pulse()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken token)
    {
        return _semaphore.WaitAsync(timeout, token);
    }
}

public sealed partial class SupportDesk
{
    public const int MaxBody = 100_000;
    public const long MaxAttachment = 20L * 1024 * 1024;
    public const int MaxAttachments = 15;

    // Crockford base32 without I, L, O and U, so a reference read over the phone can't be misheard.
    private const string ReferenceAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly string[] NoReplyLocals = ["mailer-daemon", "postmaster", "noreply", "no-reply", "donotreply", "do-not-reply", "bounce", "bounces"];

    private readonly Db _db;
    private readonly ForgeOptions _options;
    private readonly Mailer _mailer;
    private readonly MarkdownRenderer _markdown;

    public SupportDesk(Db db, ForgeOptions options, Mailer mailer, MarkdownRenderer markdown)
    {
        _db = db;
        _options = options;
        _mailer = mailer;
        _markdown = markdown;
    }

    [GeneratedRegex(@"\[(MF-[0-9A-Z]{8})\]")]
    private static partial Regex ReferenceTag();

    [GeneratedRegex(@"<(script|style|head)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBlocks();

    [GeneratedRegex(@"<(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTags();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    public string AttachmentDir => Path.Combine(_options.DataDir, "support");

    public static string NewReference()
    {
        return "MF-" + new string(RandomNumberGenerator.GetBytes(8).Select(b => ReferenceAlphabet[b % 32]).ToArray());
    }

    public string NewMessageId()
    {
        string domain = (_options.SupportEmail ?? _options.MailFrom).Split('@').LastOrDefault() ?? "localhost";
        return Text.Hex(16) + "@" + domain;
    }

    // "Hello <email or username>": the MonkeForge username when the address belongs to an account, otherwise the address itself.
    public string Greeting(string email, string? userId)
    {
        UserRow? user = userId != null
            ? _db.One<UserRow>("SELECT * FROM users WHERE id=@userId AND deleted_at IS NULL", new { userId })
            : _db.One<UserRow>("SELECT * FROM users WHERE email=@email AND email_verified=1 AND is_group=0 AND deleted_at IS NULL ORDER BY substr(id,1,3)='mf_' DESC LIMIT 1", new { email });

        return user == null ? email : user.PublicUsername ?? user.Username;
    }

    public string? AccountFor(string email)
    {
        return _db.One<string?>("SELECT id FROM users WHERE email=@email AND email_verified=1 AND is_group=0 AND deleted_at IS NULL ORDER BY substr(id,1,3)='mf_' DESC LIMIT 1", new { email });
    }

    public string ThreadSubject(TicketRow ticket)
    {
        string subject = ticket.Subject.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? ticket.Subject : "Re: " + ticket.Subject;
        return subject.Contains("[" + ticket.Reference + "]") ? subject : subject + " [" + ticket.Reference + "]";
    }

    public static string HtmlToText(string html)
    {
        string text = HtmlBlocks().Replace(html, "");
        text = HtmlBreaks().Replace(text, "\n");
        text = WebUtility.HtmlDecode(HtmlTags().Replace(text, ""));
        text = string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()));
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    private static string Limit(string value, int max)
    {
        return value.Length > max ? value[..max] + "\n\n[Message truncated]" : value;
    }

    // ---- inbound ------------------------------------------------------------------------------------------------

    public long? Ingest(MimeMessage message)
    {
        string? messageId = message.MessageId;

        if (messageId != null && _db.Exists("SELECT 1 FROM ticket_messages WHERE message_id=@messageId", new { messageId }))
            return null;

        MailboxAddress? from = message.ReplyTo.Mailboxes.FirstOrDefault() ?? message.From.Mailboxes.FirstOrDefault();

        if (from == null || string.IsNullOrWhiteSpace(from.Address))
            return null;

        string address = from.Address.Trim().ToLowerInvariant();

        // Our own messages can land back in the inbox (a CC, or a provider's sent copy). They're already on the ticket.
        if (_options.SupportEmail != null && address.Equals(_options.SupportEmail, StringComparison.OrdinalIgnoreCase))
            return null;

        string subject = Mailer.CleanSubject(string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject);
        string body = message.TextBody ?? (message.HtmlBody != null ? HtmlToText(message.HtmlBody) : "");
        body = Limit(body.Replace("\r\n", "\n").Trim(), MaxBody);
        string now = Text.NowIso();
        TicketRow? ticket = FindThread(message, subject);
        bool created = ticket == null;

        _db.BeginImmediate();

        if (ticket == null)
        {
            string? userId = AccountFor(address);
            string reference = NewReference();

            while (_db.Exists("SELECT 1 FROM support_tickets WHERE reference=@reference", new { reference }))
                reference = NewReference();

            _db.Exec("""
                INSERT INTO support_tickets(reference,subject,requester_email,requester_name,user_id,status,unread,created_at,updated_at)
                VALUES(@reference,@subject,@address,@name,@userId,'open',1,@now,@now)
                """, new { reference, subject, address, name = string.IsNullOrWhiteSpace(from.Name) ? null : from.Name.Trim(), userId, now });

            ticket = _db.One<TicketRow>("SELECT * FROM support_tickets WHERE id=@id", new { id = _db.LastInsertId() })!;
        }
        else
        {
            // A reply from the customer puts the ticket back in front of staff, even if it had been closed.
            _db.Exec("UPDATE support_tickets SET status='open',unread=1,updated_at=@now WHERE id=@id", new { now, id = ticket.Id });
        }

        _db.Exec("""
            INSERT INTO ticket_messages(ticket_id,direction,from_address,to_address,subject,body,message_id,created_at)
            VALUES(@ticket,'in',@from,@to,@subject,@body,@messageId,@created)
            """, new
        {
            ticket = ticket.Id,
            from = string.IsNullOrWhiteSpace(from.Name) ? address : $"{from.Name.Trim()} <{address}>",
            to = string.Join(", ", message.To.Mailboxes.Select(m => m.Address)),
            subject,
            body,
            messageId,
            created = message.Date == DateTimeOffset.MinValue ? now : message.Date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'"),
        });

        long rowId = _db.LastInsertId();
        SaveAttachments(message, rowId);

        if (created && ShouldAutoReply(message, address))
            AutoReply(ticket, message);

        _db.Commit();
        _mailer.Pulse();
        return ticket.Id;
    }

    private TicketRow? FindThread(MimeMessage message, string subject)
    {
        foreach (string reference in new[] { message.InReplyTo }.Concat(message.References).OfType<string>().Distinct())
        {
            TicketRow? found = _db.One<TicketRow>("SELECT t.* FROM support_tickets t JOIN ticket_messages m ON m.ticket_id=t.id WHERE m.message_id=@reference", new { reference });

            if (found != null)
                return found;
        }

        Match tag = ReferenceTag().Match(subject);
        return tag.Success ? _db.One<TicketRow>("SELECT * FROM support_tickets WHERE reference=@reference", new { reference = tag.Groups[1].Value }) : null;
    }

    private void SaveAttachments(MimeMessage message, long messageRowId)
    {
        Directory.CreateDirectory(AttachmentDir);
        int saved = 0;

        foreach (MimePart part in message.Attachments.OfType<MimePart>())
        {
            if (saved >= MaxAttachments || part.Content == null)
                break;

            string id = Text.Hex(16);
            string path = Path.Combine(AttachmentDir, id);

            using (FileStream file = File.Create(path))
                part.Content.DecodeTo(file);

            long length = new FileInfo(path).Length;

            if (length > MaxAttachment)
            {
                File.Delete(path);
                continue;
            }

            string filename = string.IsNullOrWhiteSpace(part.FileName) ? "attachment" : part.FileName.Trim();
            _db.Exec("INSERT INTO ticket_attachments VALUES(@id,@messageRowId,@filename,@type,@length,@id)",
                new { id, messageRowId, filename = filename.Length > 200 ? filename[..200] : filename, type = part.ContentType.MimeType, length });
            saved++;
        }
    }

    // Never answer another robot, a mailing list, or the same person over and over.
    private bool ShouldAutoReply(MimeMessage message, string address)
    {
        string? submitted = message.Headers["Auto-Submitted"];

        if (submitted != null && !submitted.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;

        string precedence = (message.Headers["Precedence"] ?? "").Trim().ToLowerInvariant();
        string suppress = (message.Headers["X-Auto-Response-Suppress"] ?? "").ToLowerInvariant();

        if (precedence is "bulk" or "junk" or "list" || message.Headers.Contains("List-Id") || message.Headers.Contains("List-Unsubscribe")
            || suppress.Contains("all") || suppress.Contains("autoreply") || suppress.Contains("oof"))
            return false;

        string local = address.Split('@')[0];

        if (NoReplyLocals.Any(n => local == n || local.StartsWith(n + "+", StringComparison.Ordinal)))
            return false;

        long recent = _db.Scalar<long>("SELECT COUNT(*) FROM ticket_messages WHERE direction='auto' AND to_address=@address AND created_at>@since",
            new { address, since = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss") });

        return recent < 3;
    }

    private void AutoReply(TicketRow ticket, MimeMessage original)
    {
        string brand = _options.BrandName;
        string name = Greeting(ticket.RequesterEmail, ticket.UserId);
        string text = $"""
            Hello {name},

            Thanks for opening a ticket with {brand} support. Here are some relevant details you might want later when you flex to your friends how awesome we were:

            - Reference ID: {ticket.Reference}
            - Ticket Number: #{ticket.Id}
            - Ticket Title: {ticket.Subject}

            We try to get back to you as quick as we can, so be patient and check every 30 minutes or so for a response. Thanks!

            - {brand} support
            """;

        string html = $"""
            <p style="margin:0 0 14px;color:{Mailer.Ink}">Hello {Mailer.Encode(name)},</p>
            <p style="margin:0 0 14px;color:{Mailer.Ink}">Thanks for opening a ticket with {Mailer.Encode(brand)} support. Here are some relevant details you might want later when you flex to your friends how awesome we were:</p>
            {Mailer.DetailsTable([("Reference ID", ticket.Reference), ("Ticket Number", "#" + ticket.Id), ("Ticket Title", ticket.Subject)])}
            <p style="margin:0 0 14px;color:{Mailer.Ink}">We try to get back to you as quick as we can, so be patient and check every 30 minutes or so for a response. Thanks!</p>
            <p style="margin:0;color:{Mailer.Ink}">- {Mailer.Encode(brand)} support</p>
            """;

        Send(ticket, "auto", null, text, Layout($"Ticket #{ticket.Id} received", html, ticket), MailKinds.SupportAuto, original.MessageId, Chain(original));
    }

    private static List<string> Chain(MimeMessage original)
    {
        List<string> chain = original.References.ToList();

        if (original.MessageId != null && !chain.Contains(original.MessageId))
            chain.Add(original.MessageId);

        return chain;
    }

    private string Layout(string heading, string html, TicketRow ticket)
    {
        string footer = $"Reply to this email to add to ticket #{ticket.Id}. Reference <span style=\"font-family:ui-monospace,Menlo,Consolas,monospace;color:{Mailer.Ink}\">{Mailer.Encode(ticket.Reference)}</span>.";
        return _mailer.Layout(heading, html, footerHtml: footer);
    }

    // ---- outbound -----------------------------------------------------------------------------------------------

    private long Send(TicketRow ticket, string direction, CurrentUser? author, string text, string html, string kind, string? inReplyTo, List<string> references)
    {
        string messageId = NewMessageId();
        string subject = ThreadSubject(ticket);
        long outbox = _mailer.Enqueue(ticket.RequesterEmail, subject, text, html, kind, ticket.UserId, sender: MailSenders.Support,
            messageId: messageId, inReplyTo: inReplyTo, references: references.Count == 0 ? null : string.Join(' ', references.TakeLast(20)));

        _db.Exec("""
            INSERT INTO ticket_messages(ticket_id,direction,author_id,from_address,to_address,subject,body,message_id,outbox_id,created_at)
            VALUES(@ticket,@direction,@author,@from,@to,@subject,@text,@messageId,@outbox,@now)
            """, new { ticket = ticket.Id, direction, author = author?.Id, from = _options.SupportEmail ?? "", to = ticket.RequesterEmail, subject, text, messageId, outbox = outbox == 0 ? (long?)null : outbox, now = Text.NowIso() });

        return _db.LastInsertId();
    }

    // The latest message we know of in the thread, so replies nest under it in the customer's mail client.
    private (string? InReplyTo, List<string> References) ThreadHeaders(TicketRow ticket)
    {
        List<string> ids = _db.Query<string>("SELECT message_id FROM ticket_messages WHERE ticket_id=@id AND message_id IS NOT NULL AND direction!='note' ORDER BY id", new { id = ticket.Id }).ToList();
        return (ids.LastOrDefault(), ids);
    }

    public long Reply(TicketRow ticket, CurrentUser admin, string markdown)
    {
        string brand = _options.BrandName;
        string name = Greeting(ticket.RequesterEmail, ticket.UserId);
        string signature = admin.Username;
        string text = $"""
            Hello {name},

            A {brand} support team member has replied to your ticket. We are not AI, and this is a real human who typed this, so please be mindful of any bad English skills (not all of us are native English speakers):

            {markdown.Trim()}

            Thanks,
            - {signature}, {brand} support
            """;

        string html = $"""
            <p style="margin:0 0 14px;color:{Mailer.Ink}">Hello {Mailer.Encode(name)},</p>
            <p style="margin:0 0 18px;color:{Mailer.Muted}">A {Mailer.Encode(brand)} support team member has replied to your ticket. We are not AI, and this is a real human who typed this, so please be mindful of any bad English skills (not all of us are native English speakers):</p>
            {Quote(markdown)}
            <p style="margin:0;color:{Mailer.Ink}">Thanks,<br>- {Mailer.Encode(signature)}, {Mailer.Encode(brand)} support</p>
            """;

        (string? inReplyTo, List<string> references) = ThreadHeaders(ticket);
        return Send(ticket, "out", admin, text, Layout($"New reply on ticket #{ticket.Id}", html, ticket), MailKinds.Support, inReplyTo, references);
    }

    // Staff starting a conversation (from the Users page, say) rather than answering one.
    public long Compose(TicketRow ticket, CurrentUser admin, string markdown)
    {
        string brand = _options.BrandName;
        string name = Greeting(ticket.RequesterEmail, ticket.UserId);
        string text = $"""
            Hello {name},

            {markdown.Trim()}

            Thanks,
            - {admin.Username}, {brand} support
            """;

        string html = $"""
            <p style="margin:0 0 14px;color:{Mailer.Ink}">Hello {Mailer.Encode(name)},</p>
            {Quote(markdown)}
            <p style="margin:0;color:{Mailer.Ink}">Thanks,<br>- {Mailer.Encode(admin.Username)}, {Mailer.Encode(brand)} support</p>
            """;

        return Send(ticket, "out", admin, text, Layout(ticket.Subject, html, ticket), MailKinds.Support, null, []);
    }

    private string Quote(string markdown)
    {
        string rendered = Mailer.StyleMarkdown(_markdown.Render(markdown));
        return $"""<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:0 0 20px"><tr><td bgcolor="{Mailer.Surface2}" style="background:{Mailer.Surface2};border:1px solid {Mailer.Line};border-left:3px solid {Mailer.Accent};border-radius:9px;padding:16px 18px 4px">{rendered}</td></tr></table>""";
    }

    // The HTML a reply would be sent with, for the preview pane.
    public string PreviewReply(TicketRow ticket, CurrentUser admin, string markdown, bool compose)
    {
        string brand = _options.BrandName;
        string name = Greeting(ticket.RequesterEmail, ticket.UserId);
        string intro = compose ? "" : $"""<p style="margin:0 0 18px;color:{Mailer.Muted}">A {Mailer.Encode(brand)} support team member has replied to your ticket. We are not AI, and this is a real human who typed this, so please be mindful of any bad English skills (not all of us are native English speakers):</p>""";
        string html = $"""
            <p style="margin:0 0 14px;color:{Mailer.Ink}">Hello {Mailer.Encode(name)},</p>
            {intro}
            {Quote(markdown)}
            <p style="margin:0;color:{Mailer.Ink}">Thanks,<br>- {Mailer.Encode(admin.Username)}, {Mailer.Encode(brand)} support</p>
            """;

        return Layout(compose ? ticket.Subject : $"New reply on ticket #{ticket.Id}", html, ticket);
    }

    public TicketRow Open(string email, string subject, string? userId)
    {
        string reference = NewReference();

        while (_db.Exists("SELECT 1 FROM support_tickets WHERE reference=@reference", new { reference }))
            reference = NewReference();

        string now = Text.NowIso();
        _db.Exec("""
            INSERT INTO support_tickets(reference,subject,requester_email,user_id,status,unread,created_at,updated_at)
            VALUES(@reference,@subject,@email,@userId,'pending',0,@now,@now)
            """, new { reference, subject = Mailer.CleanSubject(subject), email, userId = userId ?? AccountFor(email), now });

        return _db.One<TicketRow>("SELECT * FROM support_tickets WHERE id=@id", new { id = _db.LastInsertId() })!;
    }
}

// Polls the support mailbox over IMAP and turns new mail into tickets. Progress is the last UID handled, so reading
// the inbox in another mail client doesn't hide anything from the desk.
public sealed class SupportInbox : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ForgeOptions _options;
    private readonly SupportSignal _signal;
    private readonly ILogger<SupportInbox> _logger;
    private DateTime _lastReport = DateTime.MinValue;

    public SupportInbox(IServiceProvider services, ForgeOptions options, SupportSignal signal, ILogger<SupportInbox> logger)
    {
        _services = services;
        _options = options;
        _signal = signal;
        _logger = logger;
    }

    public static DateTime? LastCheck { get; private set; }
    public static string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SupportConfigured)
        {
            _logger.LogInformation("Support mailbox is not configured (SUPPORT_EMAIL, SUPPORT_IMAP_HOST, SUPPORT_MAIL_PASSWORD); tickets won't arrive by email.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
                LastError = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                _logger.LogWarning("Checking the support mailbox failed: {Type} {Message}", ex.GetType().Name, ex.Message);

                if (DateTime.UtcNow - _lastReport > TimeSpan.FromMinutes(30))
                {
                    _lastReport = DateTime.UtcNow;
                    _services.GetRequiredService<Diagnostics>().Report("Support mailbox", $"Could not read {_options.SupportMailUsername} on {_options.SupportImapHost}:{_options.SupportImapPort} ({LastError}).",
                        "Check SUPPORT_IMAP_HOST, SUPPORT_IMAP_PORT, SUPPORT_MAIL_USERNAME and SUPPORT_MAIL_PASSWORD.");
                }
            }

            LastCheck = DateTime.UtcNow;

            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(_options.SupportPollSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        using var imap = new ImapClient { CheckCertificateRevocation = false, Timeout = 60000 };
        SecureSocketOptions security = _options.SupportImapPort == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await imap.ConnectAsync(_options.SupportImapHost, _options.SupportImapPort, security, token);
        await imap.AuthenticateAsync(_options.SupportMailUsername, _options.SupportMailPassword, token);

        IMailFolder inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, token);

        using IServiceScope scope = _services.CreateScope();
        Db db = scope.ServiceProvider.GetRequiredService<Db>();
        string validity = inbox.UidValidity.ToString();
        string? savedValidity = db.One<string?>("SELECT value FROM support_state WHERE key='uidvalidity'");
        uint last = uint.TryParse(db.One<string?>("SELECT value FROM support_state WHERE key='last_uid'"), out uint parsed) ? parsed : 0;
        IList<UniqueId> uids;

        if (savedValidity != validity)
        {
            // First run, or the server renumbered the mailbox: only pick up recent unread mail rather than importing years of history.
            uids = await inbox.SearchAsync(SearchQuery.NotSeen.And(SearchQuery.DeliveredAfter(DateTime.UtcNow.AddDays(-7))), token);
            last = 0;
            Save(db, "uidvalidity", validity);
        }
        else
        {
            uids = await inbox.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(last + 1), UniqueId.MaxValue)), token);
        }

        foreach (UniqueId uid in uids.Where(u => u.Id > last).OrderBy(u => u.Id))
        {
            token.ThrowIfCancellationRequested();

            try
            {
                MimeMessage message = await inbox.GetMessageAsync(uid, token);
                scope.ServiceProvider.GetRequiredService<SupportDesk>().Ingest(message);
                await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.Rollback();
                _logger.LogWarning(ex, "Could not turn support email {Uid} into a ticket", uid.Id);
                scope.ServiceProvider.GetRequiredService<Diagnostics>().Report("Support mailbox", $"Could not turn email UID {uid.Id} into a ticket. It is still in the mailbox.", exception: ex);
            }

            Save(db, "last_uid", uid.Id.ToString());
            last = uid.Id;
        }

        await imap.DisconnectAsync(true, token);
    }

    private static void Save(Db db, string key, string value)
    {
        db.Exec("INSERT INTO support_state(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", new { key, value });
    }
}
