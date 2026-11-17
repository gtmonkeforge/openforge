using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using OpenForge.Infrastructure;
using OpenForge.Models;

namespace OpenForge.Services;

public static class MailKinds
{
    // Account mail is sent first and its body is wiped once delivered, because it carries sign-in links.
    public const string Account = "account";
    public const string Release = "release";
    public const string Broadcast = "broadcast";

    // Receipts and refund notices: always sent, sent first, and kept so staff can see what a buyer was told.
    public const string Receipt = "receipt";

    // The two kinds a person can turn off. They match the users.email_* columns.
    public const string ReleasesTopic = "releases";

    // Support desk mail leaves from the support mailbox. Staff replies are written by a person; auto-replies say so in their headers.
    public const string Support = "support";
    public const string SupportAuto = "support_auto";
    public const string AnnouncementsTopic = "announcements";
}

public sealed class OutboxRow
{
    public long Id { get; set; }
    public string? UserId { get; set; }
    public string ToAddress { get; set; } = "";
    public string Subject { get; set; } = "";
    public string TextBody { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? ListUnsubscribe { get; set; }
    public int Attempts { get; set; }
    public string Sender { get; set; } = MailSenders.Default;
    public string? MessageId { get; set; }
    public string? InReplyTo { get; set; }
    public string? ReferencesHeader { get; set; }
}

public static class MailSenders
{
    public const string Default = "default";
    public const string Support = "support";
}

// One SMTP login and the address it sends as.
public sealed record SmtpAccount(string Host, int Port, string Security, string Username, string Password, string FromAddress, string FromName)
{
    public static SmtpAccount? For(ForgeOptions options, string sender)
    {
        if (sender == MailSenders.Support)
            return options.SupportConfigured ? new SmtpAccount(options.SupportSmtpHost, options.SupportSmtpPort, options.SupportSmtpSecurity, options.SupportMailUsername, options.SupportMailPassword, options.SupportEmail!, options.SupportFromName) : null;

        return options.MailConfigured ? new SmtpAccount(options.SmtpHost, options.SmtpPort, options.SmtpSecurity, options.SmtpUsername, options.SmtpPassword, options.MailFrom, options.MailFromName) : null;
    }
}

// Wakes the mail worker as soon as something is queued, instead of waiting for its next poll.
public sealed class MailSignal
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

public sealed class Mailer
{
    private readonly Db _db;
    private readonly ForgeOptions _options;
    private readonly MailSignal _signal;
    private readonly IDataProtector _unsubscribe;
    private readonly ILogger<Mailer> _logger;
    private readonly Diagnostics _diagnostics;

    public Mailer(Db db, ForgeOptions options, MailSignal signal, IDataProtectionProvider protection, ILogger<Mailer> logger, Diagnostics diagnostics)
    {
        _db = db;
        _options = options;
        _signal = signal;
        _unsubscribe = protection.CreateProtector("OpenForge.Email.Unsubscribe.v1");
        _logger = logger;
        _diagnostics = diagnostics;
    }

    public bool Enabled => _options.MailConfigured;

    public static string Encode(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    public static string CleanSubject(string subject)
    {
        subject = string.Join(' ', subject.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return subject.Length > 200 ? subject[..199] + "…" : subject;
    }

    public long Enqueue(string to, string subject, string text, string html, string kind, string? userId = null, string? broadcastId = null, string? listUnsubscribe = null,
        string sender = MailSenders.Default, string? messageId = null, string? inReplyTo = null, string? references = null)
    {
        if (SmtpAccount.For(_options, sender) == null)
            return 0;

        int priority = kind is MailKinds.Account or MailKinds.Receipt or MailKinds.Support or MailKinds.SupportAuto ? 0 : kind == MailKinds.Release ? 1 : 2;
        _db.Exec("""
            INSERT INTO email_outbox(user_id,to_address,subject,text_body,html_body,kind,priority,broadcast_id,list_unsubscribe,next_attempt,created,sender,message_id,in_reply_to,references_header)
            VALUES(@userId,@to,@subject,@text,@html,@kind,@priority,@broadcastId,@listUnsubscribe,@now,@now,@sender,@messageId,@inReplyTo,@references)
            """, new { userId, to, subject = CleanSubject(subject), text, html, kind, priority, broadcastId, listUnsubscribe, now = Text.NowEpoch(), sender, messageId, inReplyTo, references });

        long id = _db.LastInsertId();
        _signal.Pulse();
        return id;
    }

    public void Pulse()
    {
        _signal.Pulse();
    }

    // Matches the site's dark theme. Everything is one table-based layout with inline styles, since mail clients ignore stylesheets.
    public const string Accent = "#dc5c3b";
    public const string Ink = "#ececee";
    public const string Muted = "#9b9ea6";
    public const string Surface = "#18191c";
    public const string Surface2 = "#1f2125";
    public const string Line = "#2a2c31";
    public const string Background = "#111214";

    public string Layout(string heading, string bodyHtml, string? actionLabel = null, string? actionUrl = null, string? footerHtml = null)
    {
        string brand = Encode(_options.BrandName);
        string button = actionUrl == null ? "" : $"""
            <table role="presentation" cellpadding="0" cellspacing="0" style="margin:28px 0 20px"><tr><td bgcolor="{Accent}" style="border-radius:9px">
            <a href="{Encode(actionUrl)}" style="display:inline-block;padding:12px 22px;border-radius:9px;background:{Accent};color:#ffffff;text-decoration:none;font-weight:600;font-size:15px">{Encode(actionLabel ?? "Open")}</a>
            </td></tr></table>
            <p style="margin:0 0 4px;color:{Muted};font-size:13px">If the button doesn't work, paste this link into your browser:<br><a href="{Encode(actionUrl)}" style="color:{Accent};word-break:break-all">{Encode(actionUrl)}</a></p>
            """;

        // Mail clients can't show the SVG logo, so a PNG or JPEG brand image is used when there is one, and a wordmark otherwise.
        string mark = _options.StripeBrandLogoUrl != null
            ? $"""<img src="{Encode(_options.StripeBrandLogoUrl)}" alt="{brand}" height="28" style="height:28px;width:auto;border:0;vertical-align:middle">"""
            : $"""<span style="display:inline-block;width:12px;height:12px;border-radius:4px;background:{Accent};vertical-align:middle;margin-right:9px"></span><span style="vertical-align:middle;font-weight:700;font-size:18px;color:{Ink};letter-spacing:-.01em">{brand}</span>""";

        return $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="dark"><meta name="supported-color-schemes" content="dark"><title>{Encode(heading)}</title></head>
            <body bgcolor="{Background}" style="margin:0;padding:0;background:{Background};font-family:ui-sans-serif,system-ui,-apple-system,'Segoe UI',Roboto,'Helvetica Neue',Arial,sans-serif;color:{Ink};-webkit-font-smoothing:antialiased">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" bgcolor="{Background}" style="background:{Background};padding:36px 12px"><tr><td align="center">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:580px">
            <tr><td style="padding:0 4px 18px"><a href="{Encode(_options.PublicUrl)}" style="text-decoration:none">{mark}</a></td></tr>
            <tr><td bgcolor="{Surface}" style="background:{Surface};border:1px solid {Line};border-top:3px solid {Accent};border-radius:12px;padding:32px;font-size:15px;line-height:1.6;color:{Ink}">
            <h1 style="margin:0 0 18px;font-size:21px;line-height:1.3;font-weight:650;color:{Ink}">{Encode(heading)}</h1>
            {bodyHtml}
            {button}
            </td></tr>
            <tr><td style="padding:18px 4px;color:{Muted};font-size:12px;line-height:1.55">{footerHtml ?? $"You're receiving this because of activity on your {brand} account."}</td></tr>
            </table></td></tr></table></body></html>
            """;
    }

    // A muted, monospaced box of label/value rows, like the details lists on the site.
    public static string DetailsTable(IEnumerable<(string Label, string Value)> rows)
    {
        string cells = string.Concat(rows.Select(r => $"""<tr><td style="padding:5px 18px 5px 0;color:{Muted};font-size:13px;white-space:nowrap;vertical-align:top">{Encode(r.Label)}</td><td style="padding:5px 0;color:{Ink};font-size:14px;font-family:ui-monospace,'SF Mono',Menlo,Consolas,monospace">{Encode(r.Value)}</td></tr>"""));
        return $"""<table role="presentation" cellpadding="0" cellspacing="0" width="100%" bgcolor="{Surface2}" style="background:{Surface2};border:1px solid {Line};border-radius:9px;margin:4px 0 18px"><tr><td style="padding:12px 16px"><table role="presentation" cellpadding="0" cellspacing="0">{cells}</table></td></tr></table>""";
    }

    // Plain text with line breaks and links kept, for messages a person typed.
    public static string Paragraphs(string text)
    {
        return string.Concat(text.Replace("\r\n", "\n").Split("\n\n").Select(p => $"<p style=\"margin:0 0 14px;color:{Ink}\">{Encode(p).Replace("\n", "<br>")}</p>"));
    }

    public string UnsubscribeUrl(string userId, string topic)
    {
        return _options.PublicUrl + "/unsubscribe?token=" + Uri.EscapeDataString(_unsubscribe.Protect(userId + "\n" + topic));
    }

    public string OneClickUnsubscribeUrl(string userId, string topic)
    {
        return _options.PublicUrl + "/api/v1/email/unsubscribe?token=" + Uri.EscapeDataString(_unsubscribe.Protect(userId + "\n" + topic));
    }

    public (string UserId, string Topic)? ReadUnsubscribe(string token)
    {
        try
        {
            string[] parts = _unsubscribe.Unprotect(token).Split('\n');

            if (parts.Length == 2 && parts[1] is MailKinds.ReleasesTopic or MailKinds.AnnouncementsTopic)
                return (parts[0], parts[1]);
        }
        catch (CryptographicException)
        {
        }

        return null;
    }

    private string Footer(string userId, string topic, string why)
    {
        return $"{why} <a href=\"{Encode(UnsubscribeUrl(userId, topic))}\" style=\"color:#9b9ea6\">Unsubscribe</a> or change what you get in <a href=\"{Encode(_options.PublicUrl + "/settings/emails")}\" style=\"color:#9b9ea6\">email settings</a>.";
    }

    // Emails everyone who can publish to the mod: the author, or every member of the publishing group that owns it.
    public void ReleaseNotice(string guid, long releaseId, string uploaderName)
    {
        if (!Enabled)
            return;

        try
        {
            ModRow? mod = _db.One<ModRow>("SELECT * FROM mods WHERE guid=@guid", new { guid });
            ReleaseRow? release = _db.One<ReleaseRow>("SELECT * FROM releases WHERE id=@releaseId", new { releaseId });

            if (mod == null || release == null)
                return;

            UserRow? owner = _db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = mod.AuthorId });

            if (owner == null)
                return;

            List<UserRow> recipients = _db.Query<UserRow>("""
                SELECT u.* FROM users u WHERE (u.id=@owner OR u.id IN (SELECT user_id FROM group_members WHERE group_id=@owner))
                AND u.is_group=0 AND u.is_banned=0 AND u.email IS NOT NULL AND u.email_verified=1 AND u.email_release_notices=1
                """, new { owner = owner.Id }).ToList();

            string url = _options.PublicUrl + "/view/" + Uri.EscapeDataString(mod.Guid) + (release.Channel != "stable" ? "?channel=" + Uri.EscapeDataString(release.Channel) : "");
            string channel = release.Channel == "stable" ? "" : $" on the {release.Channel} channel";
            string review = mod.Status == "pending" ? "It's waiting for staff review before it appears publicly." : "";
            string subject = $"{mod.Name} {release.Version} was published";

            foreach (UserRow user in recipients)
            {
                string relation = owner.Group ? $"you're a member of {owner.DisplayName}, which owns this mod" : "you own this mod";
                string html = $"""
                    <p style="margin:0 0 12px">A new release of <strong>{Encode(mod.Name)}</strong> was uploaded{Encode(channel)}.</p>
                    <table role="presentation" cellpadding="0" cellspacing="0" style="margin:0 0 12px;font-size:14px">
                    <tr><td style="color:#9b9ea6;padding:2px 16px 2px 0">Version</td><td><strong>{Encode(release.Version)}</strong></td></tr>
                    <tr><td style="color:#9b9ea6;padding:2px 16px 2px 0">Channel</td><td>{Encode(release.Channel)}</td></tr>
                    <tr><td style="color:#9b9ea6;padding:2px 16px 2px 0">Uploaded by</td><td>{Encode(uploaderName)}</td></tr>
                    <tr><td style="color:#9b9ea6;padding:2px 16px 2px 0">GUID</td><td style="font-family:monospace">{Encode(mod.Guid)}</td></tr>
                    </table>
                    {(review == "" ? "" : $"<p style=\"margin:0 0 12px\">{Encode(review)}</p>")}
                    <p style="margin:0;color:#9b9ea6;font-size:14px">Didn't expect this? Check who's in your publishing group and revoke any access keys you don't recognise in <a href="{Encode(_options.PublicUrl + "/settings/keys")}" style="color:#dc5c3b">access keys</a>.</p>
                    """;
                string text = $"""
                    A new release of {mod.Name} was uploaded{channel}.

                    Version: {release.Version}
                    Channel: {release.Channel}
                    Uploaded by: {uploaderName}
                    GUID: {mod.Guid}
                    {(review == "" ? "" : "\n" + review + "\n")}
                    View it: {url}

                    Didn't expect this? Check your publishing group and access keys: {_options.PublicUrl}/settings/keys

                    You're getting this because {relation}. Unsubscribe: {UnsubscribeUrl(user.Id, MailKinds.ReleasesTopic)}
                    """;

                Enqueue(user.Email!, subject, text, Layout(subject, html, "View release", url, Footer(user.Id, MailKinds.ReleasesTopic, $"You're getting this because {Encode(relation)}.")),
                    MailKinds.Release, user.Id, listUnsubscribe: OneClickUnsubscribeUrl(user.Id, MailKinds.ReleasesTopic));
            }
        }
        catch (Exception ex)
        {
            // The release is already saved; a failed notice must not turn the upload into an error.
            _logger.LogWarning(ex, "Could not queue release emails for {Guid}", guid);
            _diagnostics.Report("Release email", $"Could not queue release emails for {guid}.", exception: ex);
        }
    }

    // A null userId builds a test copy: its footer has no working unsubscribe link, since the link would act on the sender's account.
    public (string Html, string Text) BroadcastBody(string? userId, string subject, string bodyHtml, string bodyMarkdown)
    {
        string why = $"You're getting this announcement because you have a {Encode(_options.BrandName)} account.";
        string footer = userId == null ? why + " [Unsubscribe link goes here]" : Footer(userId, MailKinds.AnnouncementsTopic, why);
        string unsubscribe = userId == null ? "[unsubscribe link goes here]" : UnsubscribeUrl(userId, MailKinds.AnnouncementsTopic);
        string text = bodyMarkdown.Trim() + $"\n\n—\nYou're getting this announcement because you have a {_options.BrandName} account. Unsubscribe: {unsubscribe}";
        return (Layout(subject, $"<div>{bodyHtml}</div>", footerHtml: footer), text);
    }

    // Email bodies are laid out in Layout; Markdown HTML needs inline styles for links to show in the brand color.
    public static string StyleMarkdown(string html)
    {
        return html.Replace("<a href=", $"<a style=\"color:{Accent}\" href=").Replace("<p>", $"<p style=\"margin:0 0 14px;color:{Ink}\">")
            .Replace("<li>", $"<li style=\"margin:0 0 6px;color:{Ink}\">").Replace("<code>", $"<code style=\"background:{Surface2};border:1px solid {Line};border-radius:5px;padding:1px 5px;font-size:13px\">")
            .Replace("<blockquote>", $"<blockquote style=\"margin:0 0 14px;padding:4px 14px;border-left:3px solid {Line};color:{Muted}\">")
            .Replace("<h2>", $"<h2 style=\"margin:22px 0 10px;font-size:17px;color:{Ink}\">").Replace("<h3>", $"<h3 style=\"margin:18px 0 8px;font-size:15px;color:{Ink}\">");
    }
}

public sealed class MailWorker : BackgroundService
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];

    private readonly IServiceProvider _services;
    private readonly ForgeOptions _options;
    private readonly MailSignal _signal;
    private readonly ILogger<MailWorker> _logger;
    private DateTime _lastReport = DateTime.MinValue;

    public MailWorker(IServiceProvider services, ForgeOptions options, MailSignal signal, ILogger<MailWorker> logger)
    {
        _services = services;
        _options = options;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.MailConfigured && !_options.SupportConfigured)
        {
            _logger.LogInformation("Email is not configured (SMTP_HOST and MAIL_FROM); email sign-up and notices are off.");
            return;
        }

        TimeSpan pace = TimeSpan.FromMilliseconds(60000.0 / _options.MailPerMinute);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<OutboxRow> batch;

                using (IServiceScope scope = _services.CreateScope())
                {
                    batch = scope.ServiceProvider.GetRequiredService<Db>().Query<OutboxRow>(
                        "SELECT * FROM email_outbox WHERE status='queued' AND next_attempt<=@now ORDER BY priority,id LIMIT 25", new { now = Text.NowEpoch() }).ToList();
                }

                if (batch.Count == 0)
                {
                    await _signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                foreach (IGrouping<string, OutboxRow> group in batch.GroupBy(row => row.Sender))
                    await SendBatchAsync(group.ToList(), group.Key, pace, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mail worker loop failed; retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task SendBatchAsync(List<OutboxRow> batch, string sender, TimeSpan pace, CancellationToken token)
    {
        using IServiceScope scope = _services.CreateScope();
        Db db = scope.ServiceProvider.GetRequiredService<Db>();
        SmtpAccount? account = SmtpAccount.For(_options, sender);

        if (account == null)
        {
            foreach (OutboxRow row in batch)
                db.Exec("UPDATE email_outbox SET status='failed',error=@error WHERE id=@id", new { error = $"The {sender} mail account isn't configured.", id = row.Id });

            return;
        }

        // Chain and hostname are still validated. Online revocation lookups often can't complete (Let's Encrypt no longer runs OCSP),
        // and a failed lookup would stop all mail, so it's skipped like most mail clients do.
        using var smtp = new SmtpClient { CheckCertificateRevocation = false, Timeout = 30000 };

        try
        {
            SecureSocketOptions security = account.Security switch
            {
                "ssl" => SecureSocketOptions.SslOnConnect,
                "starttls" => SecureSocketOptions.StartTls,
                _ => SecureSocketOptions.None,
            };

            await smtp.ConnectAsync(account.Host, account.Port, security, token);

            if (account.Username != "")
                await smtp.AuthenticateAsync(account.Username, account.Password, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The server itself is unreachable or refused the login: every message in the batch waits and tries again.
            _logger.LogWarning("Could not connect to the SMTP server {Host}:{Port}: {Type} {Message}", account.Host, account.Port, ex.GetType().Name, ex.Message);
            ReportOnce($"Could not connect or sign in to {account.Host}:{account.Port} as {account.Username} ({ex.GetType().Name}: {ex.Message}). Queued email will retry.");

            foreach (OutboxRow row in batch)
                Retry(db, row, ex.Message);

            return;
        }

        foreach (OutboxRow row in batch)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await smtp.SendAsync(Build(row, account), token);

                // Sign-in links are useless once delivered, and only a risk if the database leaks, so account mail keeps no body.
                db.Exec(row.Kind == MailKinds.Account
                    ? "UPDATE email_outbox SET status='sent',sent=@now,attempts=attempts+1,text_body='',html_body='',error=NULL WHERE id=@id"
                    : "UPDATE email_outbox SET status='sent',sent=@now,attempts=attempts+1,error=NULL WHERE id=@id", new { now = Text.NowEpoch(), id = row.Id });
            }
            catch (Exception ex) when (ex is SmtpCommandException { StatusCode: >= (SmtpStatusCode)500 } or ParseException or FormatException)
            {
                // The server said no for good (bad address, rejected recipient): retrying would only be refused again.
                db.Exec("UPDATE email_outbox SET status='failed',attempts=attempts+1,error=@error,text_body=CASE WHEN kind='account' THEN '' ELSE text_body END,html_body=CASE WHEN kind='account' THEN '' ELSE html_body END WHERE id=@id",
                    new { error = Trim(ex.Message), id = row.Id });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Sending email {Id} failed: {Type} {Message}", row.Id, ex.GetType().Name, ex.Message);
                Retry(db, row, ex.Message);

                if (!smtp.IsConnected)
                {
                    foreach (OutboxRow rest in batch.SkipWhile(r => r.Id != row.Id).Skip(1))
                        Retry(db, rest, "Connection closed");

                    return;
                }
            }

            await Task.Delay(pace, token);
        }

        try
        {
            await smtp.DisconnectAsync(true, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    private static MimeMessage Build(OutboxRow row, SmtpAccount account)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.FromName, account.FromAddress));
        message.To.Add(MailboxAddress.Parse(row.ToAddress));
        message.Subject = row.Subject;

        if (row.MessageId != null)
            message.MessageId = row.MessageId;

        if (row.InReplyTo != null)
            message.InReplyTo = row.InReplyTo;

        foreach (string reference in (row.ReferencesHeader ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            message.References.Add(reference);

        if (row.ListUnsubscribe != null)
        {
            // RFC 8058 one-click unsubscribe, which Gmail and Yahoo expect on bulk mail.
            message.Headers.Add("List-Unsubscribe", "<" + row.ListUnsubscribe + ">");
            message.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }

        // RFC 3834: auto-replies say so, which stops two autoresponders answering each other forever. People's replies carry nothing.
        if (row.Kind == MailKinds.SupportAuto)
            message.Headers.Add("Auto-Submitted", "auto-replied");
        else if (row.Kind is not (MailKinds.Account or MailKinds.Support))
            message.Headers.Add("Auto-Submitted", "auto-generated");

        message.Body = new BodyBuilder { TextBody = row.TextBody, HtmlBody = row.HtmlBody }.ToMessageBody();
        return message;
    }

    private void Retry(Db db, OutboxRow row, string error)
    {
        int attempts = row.Attempts + 1;

        if (attempts > Backoff.Length)
        {
            db.Exec("UPDATE email_outbox SET status='failed',attempts=@attempts,error=@error WHERE id=@id", new { attempts, error = Trim(error), id = row.Id });
            return;
        }

        db.Exec("UPDATE email_outbox SET attempts=@attempts,next_attempt=@next,error=@error WHERE id=@id",
            new { attempts, next = Text.NowEpoch() + Backoff[attempts - 1].TotalSeconds, error = Trim(error), id = row.Id });
    }

    private void ReportOnce(string message)
    {
        if (DateTime.UtcNow - _lastReport < TimeSpan.FromMinutes(30))
            return;

        _lastReport = DateTime.UtcNow;
        _services.GetRequiredService<Diagnostics>().Report("Email delivery", message, "Check SMTP_HOST, SMTP_PORT, SMTP_SECURITY, SMTP_USERNAME and SMTP_PASSWORD.");
    }

    private static string Trim(string value)
    {
        return value.Length > 500 ? value[..500] : value;
    }
}
