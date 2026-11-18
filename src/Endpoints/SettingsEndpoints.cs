using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using MimeKit;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// Server settings in the admin panel (root only), and the first-run setup wizard that uses the same machinery.
public static class SettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/admin/settings", (HttpContext http, ForgeContext context, ServerSettings settings, ForgeOptions options, Db db) => Settings(http, context, settings, options, db, false));
        app.MapPut("/api/v1/admin/settings", (HttpContext http, ForgeContext context, ServerSettings settings, Db db) => SaveAsync(http, context, settings, db, false));
        app.MapPost("/api/v1/admin/settings/test/smtp", (HttpContext http, ForgeContext context, ServerSettings settings, Db db, ForgeOptions options) => TestSmtpAsync(http, context, settings, db, options, false));
        app.MapPost("/api/v1/admin/settings/test/imap", (HttpContext http, ForgeContext context, ServerSettings settings, Db db) => TestImapAsync(http, context, settings, db, false));
        app.MapPost("/api/v1/admin/server/restart", (HttpContext http, ForgeContext context, Db db, IHostApplicationLifetime lifetime) =>
        {
            CurrentUser actor = Guard(http, context, db, false);
            AdminPortalEndpoints.Log(db, actor, "server.restart", "");
            ServerControl.Restart(lifetime);
            return Api.Json(new { restarting = true });
        });

        app.MapGet("/setup", (HttpContext http, SetupGuard guard, Db db, AdminShell shell) =>
            !guard.Required && Completed(db) ? Results.Redirect("/admin") : shell.Page("setup.html"));
        app.MapGet("/api/v1/setup", Status);
        app.MapPost("/api/v1/setup/claim", ClaimAsync);
        app.MapPut("/api/v1/setup/settings", (HttpContext http, ForgeContext context, ServerSettings settings, Db db) => SaveAsync(http, context, settings, db, true));
        app.MapPost("/api/v1/setup/test/smtp", (HttpContext http, ForgeContext context, ServerSettings settings, Db db, ForgeOptions options) => TestSmtpAsync(http, context, settings, db, options, true));
        app.MapPost("/api/v1/setup/test/imap", (HttpContext http, ForgeContext context, ServerSettings settings, Db db) => TestImapAsync(http, context, settings, db, true));
        app.MapPost("/api/v1/setup/finish", (HttpContext http, ForgeContext context, Db db, IHostApplicationLifetime lifetime) =>
        {
            CurrentUser actor = Guard(http, context, db, true);
            db.Exec("INSERT OR REPLACE INTO server_state(key,value) VALUES('setup_completed',@now)", new { now = Text.NowIso() });
            AdminPortalEndpoints.Log(db, actor, "server.setup_finished", "");
            ServerControl.Restart(lifetime);
            return Api.Json(new { restarting = true });
        });
    }

    public static bool Completed(Db db)
    {
        return db.Exists("SELECT 1 FROM server_state WHERE key='setup_completed'");
    }

    // The setup routes only work for the new root admin while setup is unfinished; the panel's routes are root-only.
    private static CurrentUser Guard(HttpContext http, ForgeContext context, Db db, bool setup)
    {
        CurrentUser actor = AdminEndpoints.RequireAdmin(context, true);

        if (setup && Completed(db))
            throw ApiException.NotFound("Setup is already finished. Change settings in the admin panel.");

        return actor;
    }

    private static object Platform(ServerSettings settings)
    {
        string version = typeof(SettingsEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        return new
        {
            version,
            os = RuntimeInformation.OSDescription,
            linux = OperatingSystem.IsLinux(),
            systemd = OperatingSystem.IsLinux() && System.IO.Directory.Exists("/run/systemd/system"),
            under_systemd = Environment.GetEnvironmentVariable("INVOCATION_ID") != null,
            user = Environment.UserName,
            folder = System.IO.Directory.GetCurrentDirectory(),
            settings_file = settings.FilePath,
            started_at = ServerControl.StartedAt.ToString("o"),
            service_unit = settings.ServiceUnit(),
        };
    }

    private static IResult Settings(HttpContext http, ForgeContext context, ServerSettings settings, ForgeOptions options, Db db, bool setup)
    {
        Guard(http, context, db, setup);

        return Api.Json(new
        {
            groups = ServerSettings.Groups,
            settings = settings.Describe(),
            pending = settings.Pending(),
            platform = Platform(settings),
            derived = new
            {
                discord_redirect = options.PublicUrl + "/auth/discord/callback",
                passkey_rp_id = new Uri(options.PublicUrl).Host,
                passkey_origin = new Uri(options.PublicUrl).GetLeftPart(UriPartial.Authority),
                stripe_webhook = options.PublicUrl + "/api/v1/stripe/webhook",
            },
        });
    }

    private static Dictionary<string, string?> ReadChanges(JsonObject payload)
    {
        if (payload["values"] is not JsonObject values)
            throw ApiException.BadRequest("Send the settings to change as values.");

        var changes = new Dictionary<string, string?>();

        foreach ((string key, JsonNode? node) in values)
        {
            SettingDef def = ServerSettings.All.FirstOrDefault(d => d.Key == key) ?? throw ApiException.BadRequest($"{key} isn't a setting.");
            string? value = node == null ? null : node.IsString(out string text) ? text : node.IsBool(out bool flag) ? (flag ? "true" : "false") : node.IsInt(out long number) ? number.ToString() : throw ApiException.BadRequest($"{key} must be text.");

            if (value != null && !def.Secret)
                value = value.Trim();

            if (value != null && (value.Contains('\n') || value.Contains('\r') || value.Length > 4000))
                throw ApiException.BadRequest($"{def.Label} must be a single line.");

            // Empty means "use the default", which is the same as leaving the key out of the file.
            changes[key] = string.IsNullOrEmpty(value) ? null : value;
        }

        return changes;
    }

    private static async Task<IResult> SaveAsync(HttpContext http, ForgeContext context, ServerSettings settings, Db db, bool setup)
    {
        CurrentUser actor = Guard(http, context, db, setup);
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send the settings to change.");
        Dictionary<string, string?> changes = ReadChanges(payload);

        if (changes.TryGetValue("ROOT_ADMIN_ID", out string? root))
        {
            if (root == null)
                throw ApiException.BadRequest("The server needs a root administrator.");

            if (!db.Exists("SELECT 1 FROM users WHERE id=@root AND is_group=0 AND deleted_at IS NULL AND is_banned=0", new { root }))
                throw ApiException.BadRequest("The new root administrator must be an existing, active account.");
        }

        settings.Save(changes);

        foreach (string key in changes.Keys)
            AdminPortalEndpoints.Log(db, actor, "server.setting", key);

        return Api.Json(new { ok = true, pending = settings.Pending() });
    }

    private static string Value(JsonObject payload, string field, ServerSettings settings, string key)
    {
        return payload[field].IsString(out string value) && value != "" ? value : settings.Current(key) ?? "";
    }

    private static SecureSocketOptions Security(string security, int port)
    {
        return security switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            "none" => SecureSocketOptions.None,
            _ => port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
        };
    }

    // Connects, signs in and sends a real message, so the result reflects exactly what the outbox will do.
    private static async Task<IResult> TestSmtpAsync(HttpContext http, ForgeContext context, ServerSettings settings, Db db, ForgeOptions options, bool setup)
    {
        Guard(http, context, db, setup);
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send the SMTP settings to test.");
        bool support = payload["account"].IsString(out string account) && account == "support";
        string P(string key) => support ? "SUPPORT_" + key : key;

        string host = Value(payload, "host", settings, support ? "SUPPORT_SMTP_HOST" : "SMTP_HOST");
        if (host == "" && support)
            host = settings.Current("SMTP_HOST") ?? "";
        string security = Value(payload, "security", settings, P("SMTP_SECURITY"));
        if (security == "")
            security = settings.Current("SMTP_SECURITY") ?? "ssl";
        string portText = Value(payload, "port", settings, P("SMTP_PORT"));
        int port = int.TryParse(portText, out int parsed) ? parsed : security == "ssl" ? 465 : 587;
        string username = Value(payload, "username", settings, support ? "SUPPORT_MAIL_USERNAME" : "SMTP_USERNAME");
        string from = support ? Value(payload, "from", settings, "SUPPORT_EMAIL") : Value(payload, "from", settings, "MAIL_FROM");
        if (support && username == "")
            username = from;
        if (!support && from == "" && username.Contains('@'))
            from = username;
        string password = payload["password"].IsString(out string given) && given != "" ? given : settings.Current(support ? "SUPPORT_MAIL_PASSWORD" : "SMTP_PASSWORD") ?? "";

        if (!payload["to"].IsString(out string to) || !Text.Email().IsMatch(to.Trim()))
            throw ApiException.BadRequest("Enter an address to send the test email to.");

        if (host == "" || from == "")
            throw ApiException.BadRequest("Fill in the server and the from address first.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.BrandName, from));
        message.To.Add(MailboxAddress.Parse(to.Trim()));
        message.Subject = $"{options.BrandName} test email";
        string html = $"<p style=\"margin:0 0 14px;color:#ececee\">Email from {Mailer.Encode(options.BrandName)} works. It was sent through <span style=\"font-family:monospace\">{Mailer.Encode(host)}:{port}</span> as <span style=\"font-family:monospace\">{Mailer.Encode(username)}</span>.</p>";
        Mailer mailer = (Mailer)http.RequestServices.GetService(typeof(Mailer))!;
        message.Body = new BodyBuilder { TextBody = $"Email from {options.BrandName} works. Sent through {host}:{port} as {username}.", HtmlBody = mailer.Layout("Test email", html) }.ToMessageBody();

        using var smtp = new SmtpClient { CheckCertificateRevocation = false, Timeout = 20000 };

        try
        {
            await smtp.ConnectAsync(host, port, Security(security, port), http.RequestAborted);

            if (username != "")
                await smtp.AuthenticateAsync(username, password, http.RequestAborted);

            await smtp.SendAsync(message, http.RequestAborted);
            await smtp.DisconnectAsync(true, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Api.Json(new { ok = false, error = Explain(ex) });
        }

        return Api.Json(new { ok = true, message = $"Sent a test email to {to.Trim()}." });
    }

    private static async Task<IResult> TestImapAsync(HttpContext http, ForgeContext context, ServerSettings settings, Db db, bool setup)
    {
        Guard(http, context, db, setup);
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send the IMAP settings to test.");
        string host = Value(payload, "host", settings, "SUPPORT_IMAP_HOST");
        int port = int.TryParse(Value(payload, "port", settings, "SUPPORT_IMAP_PORT"), out int parsed) ? parsed : 993;
        string username = Value(payload, "username", settings, "SUPPORT_MAIL_USERNAME");
        if (username == "")
            username = Value(payload, "email", settings, "SUPPORT_EMAIL");
        string password = payload["password"].IsString(out string given) && given != "" ? given : settings.Current("SUPPORT_MAIL_PASSWORD") ?? "";

        if (host == "" || username == "")
            throw ApiException.BadRequest("Fill in the IMAP server and mailbox first.");

        using var imap = new ImapClient { CheckCertificateRevocation = false, Timeout = 20000 };

        try
        {
            await imap.ConnectAsync(host, port, port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, http.RequestAborted);
            await imap.AuthenticateAsync(username, password, http.RequestAborted);
            await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, http.RequestAborted);
            int count = imap.Inbox.Count;
            await imap.DisconnectAsync(true, http.RequestAborted);
            return Api.Json(new { ok = true, message = $"Signed in to {username}. The inbox has {count} message(s)." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Api.Json(new { ok = false, error = Explain(ex) });
        }
    }

    private static string Explain(Exception ex)
    {
        return ex switch
        {
            AuthenticationException => "The server rejected the username or password.",
            SslHandshakeException => "Couldn't start a secure connection. Check the port and connection security.",
            System.Net.Sockets.SocketException s => $"Couldn't connect ({s.SocketErrorCode}). Check the server name and port.",
            ServiceNotConnectedException or ServiceNotAuthenticatedException => ex.Message,
            _ => ex.GetType().Name + ": " + ex.Message,
        };
    }

    // ---- first-run setup ----------------------------------------------------------------------------------------

    private static IResult Status(HttpContext http, ForgeContext context, SetupGuard guard, Db db, ServerSettings settings, ForgeOptions options)
    {
        bool completed = Completed(db);
        CurrentUser? user = context.User;
        bool root = user != null && user.IsRoot;

        return Api.Json(new
        {
            required = guard.Required,
            completed,
            token_path = guard.Required ? guard.TokenPath : null,
            root = root ? new { id = user!.Id, username = user.Username } : null,
            signed_in = user != null,
            brand = options.BrandName,
            public_url = options.PublicUrl,
            settings = root && !completed ? settings.Describe() : null,
            groups = ServerSettings.Groups,
            platform = root && !completed ? Platform(settings) : null,
            derived = root && !completed ? new
            {
                discord_redirect = options.PublicUrl + "/auth/discord/callback",
                passkey_rp_id = new Uri(options.PublicUrl).Host,
                passkey_origin = new Uri(options.PublicUrl).GetLeftPart(UriPartial.Authority),
                stripe_webhook = options.PublicUrl + "/api/v1/stripe/webhook",
            } : null,
        });
    }

    private static async Task<IResult> ClaimAsync(HttpContext http, ForgeContext context, SetupGuard guard, Db db, ForgeOptions options, AuthThrottle throttle)
    {
        throttle.Hit(throttle.ClientKey(http) + ":setup", 10, TimeSpan.FromMinutes(10), "Too many attempts. Wait a few minutes.");
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send the setup token and the account details.");

        if (!guard.Required)
            throw ApiException.Conflict("This server already has a root administrator.");

        if (!guard.Check(payload["token"].IsString(out string token) ? token : null))
            throw ApiException.Forbidden($"That setup token isn't right. It's printed in the server log and saved in {guard.TokenPath}.");

        // Checking only the token lets the wizard confirm it before asking for the account.
        if (payload["verify_only"].IsBool(out bool verifyOnly) && verifyOnly)
            return Api.Ok();

        string username = payload["username"].IsString(out string u) ? u.Trim() : "";
        string email = payload["email"].IsString(out string e) ? e.Trim().ToLowerInvariant() : "";
        string password = payload["password"].IsString(out string p) ? p : "";

        if (!Text.PublicUsername().IsMatch(username))
            throw ApiException.BadRequest("Usernames must have 3–32 letters, digits, underscores, dots, or hyphens, starting with a letter or digit.");

        if (Text.Reserved.Contains(username.ToLowerInvariant()))
            throw ApiException.BadRequest("This username is reserved.");

        if (email.Length > 254 || !Text.Email().IsMatch(email))
            throw ApiException.BadRequest("Enter a valid email address.");

        if (Passwords.Problem(password, username, email) is string problem)
            throw ApiException.BadRequest(problem);

        if (db.Exists("SELECT 1 FROM users WHERE lower(COALESCE(public_username,username))=lower(@username)", new { username }))
            throw ApiException.Conflict("This username is already taken.");

        if (db.Exists("SELECT 1 FROM users WHERE substr(id,1,3)='mf_' AND email=@email", new { email }))
            throw ApiException.Conflict("An account already uses this email address.");

        string id = Text.EmailAccountPrefix + Text.Hex(8);

        // There's no mail server yet to confirm the address with; whoever holds the setup token is trusted with it.
        db.Exec("""
            INSERT INTO users(id,username,display_name,email,email_verified,email_source,password_hash,is_admin,created_at)
            VALUES(@id,@username,@username,@email,1,'user',@hash,1,@now)
            """, new { id, username, email, hash = Passwords.Hash(password), now = Text.NowIso() });

        try
        {
            DotEnvFile.Write(ForgeOptions.DotEnvPath, new Dictionary<string, string?> { ["ROOT_ADMIN_ID"] = id });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            db.Exec("DELETE FROM users WHERE id=@id", new { id });
            throw new ApiException(500, $"Couldn't write {ForgeOptions.DotEnvPath}: {ex.Message}. The server's user needs permission to write it.");
        }

        // Takes effect now, so the rest of the wizard runs as root without a restart.
        options.RootAdminId = id;
        guard.Claimed();
        context.SignIn(id);
        db.Exec("INSERT INTO admin_audit(actor_id,action,target,created_at) VALUES(@id,'server.claimed',@id,@now)", new { id, now = Text.NowIso() });

        return Api.Json(new { ok = true, id, csrf_token = context.Csrf() }, 201);
    }
}
