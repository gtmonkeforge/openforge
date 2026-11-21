using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// Email-and-password accounts, the account settings every account shares, and passkeys.
public static class AccountEndpoints
{
    private const double VerifySeconds = 24 * 3600;
    internal const double ResetSeconds = 3600;
    private const int MaxPasskeys = 20;

    // An unverified account can't sign in or own anything, so after a day its username and address are released for someone else.
    private const double UnverifiedGraceSeconds = 24 * 3600;

    private sealed class TokenRow
    {
        public string TokenHash { get; set; } = "";
        public string UserId { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string Email { get; set; } = "";
        public double Created { get; set; }
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/signup", SignUpAsync);
        app.MapPost("/api/v1/auth/login", LoginAsync);
        app.MapPost("/api/v1/auth/verify", VerifyAsync);
        app.MapPost("/api/v1/auth/resend", ResendAsync);
        app.MapPost("/api/v1/auth/forgot", ForgotAsync);
        app.MapPost("/api/v1/auth/reset", ResetAsync);
        app.MapPost("/api/v1/auth/passkey/options", PasskeyLoginOptions);
        app.MapPost("/api/v1/auth/passkey", PasskeyLoginAsync);

        app.MapGet("/api/v1/account", Account);
        app.MapPut("/api/v1/account/email", ChangeEmailAsync);
        app.MapPut("/api/v1/account/password", ChangePasswordAsync);
        app.MapPut("/api/v1/account/notifications", NotificationsAsync);
        app.MapPost("/api/v1/account/sign-out-everywhere", SignOutEverywhere);

        app.MapGet("/api/v1/passkeys", ListPasskeys);
        app.MapPost("/api/v1/passkeys/options", PasskeyRegisterOptions);
        app.MapPost("/api/v1/passkeys", RegisterPasskeyAsync);
        app.MapPatch("/api/v1/passkeys/{id}", RenamePasskeyAsync);
        app.MapDelete("/api/v1/passkeys/{id}", DeletePasskey);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static string NormalizeEmail(JsonNode? node)
    {
        if (!node.IsString(out string email) || email.Trim().Length > 254 || !Text.Email().IsMatch(email.Trim()))
            throw ApiException.BadRequest("Enter a valid email address.");

        return email.Trim().ToLowerInvariant();
    }

    private static string Field(JsonObject payload, string key, string message)
    {
        return payload[key].IsString(out string value) ? value : throw ApiException.BadRequest(message);
    }

    private static string? Next(JsonObject payload)
    {
        return payload["next"].IsString(out string next) ? next : null;
    }

    internal static string IssueToken(Db db, string userId, string purpose, string email, double seconds)
    {
        string token = Text.UrlToken(32);
        double now = Text.NowEpoch();
        // Every confirmation link stays valid until one is used, so an older email still works. Reset and change links replace each other.
        db.Exec("DELETE FROM email_tokens WHERE expires<=@now OR (user_id=@userId AND purpose=@purpose AND purpose!='verify')", new { now, userId, purpose });
        db.Exec("INSERT INTO email_tokens VALUES(@hash,@userId,@purpose,@email,@now,@expires)",
            new { hash = Text.Sha256Hex(token), userId, purpose, email, now, expires = now + seconds });

        return token;
    }

    private static TokenRow? ReadToken(Db db, string token, string[] purposes)
    {
        if (token.Length is < 20 or > 100)
            return null;

        TokenRow? row = db.One<TokenRow>("SELECT * FROM email_tokens WHERE token_hash=@hash AND expires>@now", new { hash = Text.Sha256Hex(token), now = Text.NowEpoch() });
        return row != null && purposes.Contains(row.Purpose) ? row : null;
    }

    private static UserRow? EmailAccount(Db db, string email)
    {
        return db.One<UserRow>("SELECT * FROM users WHERE substr(id,1,3)='mf_' AND email=@email", new { email });
    }

    private static void SendVerification(Mailer mailer, ForgeOptions options, Db db, UserRow user, string email)
    {
        string token = IssueToken(db, user.Id, "verify", email, VerifySeconds);
        string url = options.PublicUrl + "/verify-email?token=" + Uri.EscapeDataString(token);
        string name = user.PublicUsername ?? user.Username;
        string subject = $"Confirm your {options.BrandName} account";
        string html = $"""
            <p style="margin:0 0 12px">Hi {Mailer.Encode(name)},</p>
            <p style="margin:0 0 12px">Confirm that this is your email address to finish creating your {Mailer.Encode(options.BrandName)} account. The link works for 24 hours.</p>
            <p style="margin:0;color:#9b9ea6;font-size:14px">If you didn't sign up, ignore this email and the account will be removed.</p>
            """;
        string text = $"Hi {name},\n\nConfirm that this is your email address to finish creating your {options.BrandName} account. The link works for 24 hours:\n\n{url}\n\nIf you didn't sign up, ignore this email and the account will be removed.";
        mailer.Enqueue(email, subject, text, mailer.Layout(subject, html, "Confirm email", url), MailKinds.Account, user.Id);
    }

    internal static void SendNotice(Mailer mailer, ForgeOptions options, string to, string? userId, string subject, string message)
    {
        string html = $"<p style=\"margin:0 0 12px\">{Mailer.Encode(message)}</p><p style=\"margin:0;color:#9b9ea6;font-size:14px\">If this wasn't you, reset your password and remove any passkeys you don't recognise in your account settings.</p>";
        string text = message + "\n\nIf this wasn't you, reset your password and remove any passkeys you don't recognise: " + options.PublicUrl + "/settings/security";
        mailer.Enqueue(to, subject, text, mailer.Layout(subject, html, "Review security settings", options.PublicUrl + "/settings/security"), MailKinds.Account, userId);
    }

    private static void RequireMail(ForgeOptions options)
    {
        if (!options.MailConfigured)
            throw new ApiException(503, "Email isn't set up on this server yet. Sign in with Discord instead.");
    }

    private static void CheckBan(UserRow user, ForgeOptions options)
    {
        if (user.Banned && user.Id != options.RootAdminId)
            throw ApiException.Forbidden("Your account is banned from this site.");
    }

    private static IResult SignedIn(ForgeContext context, Db db, HttpResponse response, UserRow user, JsonObject payload)
    {
        context.SignIn(user.Id);
        string redirect = AuthEndpoints.SafeNext(Next(payload));
        return Api.Json(new { redirect, user = context.User?.ToDto(), csrf_token = context.Csrf() });
    }

    // ---- sign-up and sign-in -----------------------------------------------------------------------------------------

    private static async Task<IResult> SignUpAsync(HttpContext http, ForgeContext context, Db db, ForgeOptions options, Mailer mailer, AuthThrottle throttle)
    {
        if (!options.EmailSignupEnabled)
            throw new ApiException(503, options.MailConfigured ? "New email accounts are turned off on this server. Sign in with Discord instead." : "Email isn't set up on this server yet. Sign in with Discord instead.");

        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send a username, email and password.");
        throttle.Hit(throttle.ClientKey(http) + ":signup", 10, TimeSpan.FromHours(1), "Too many sign-ups from this network. Try again later.");

        string username = Field(payload, "username", "Choose a username.").Trim();
        string email = NormalizeEmail(payload["email"]);
        string password = Field(payload, "password", "Choose a password.");

        if (!Text.PublicUsername().IsMatch(username))
            throw ApiException.BadRequest("Usernames must have 3–32 letters, digits, underscores, dots, or hyphens, starting with a letter or digit.");

        if (Text.Reserved.Contains(username.ToLowerInvariant()))
            throw ApiException.BadRequest("This username is reserved.");

        if (Passwords.Problem(password, username, email) is string problem)
            throw ApiException.BadRequest(problem);

        string cutoff = DateTime.UtcNow.AddSeconds(-UnverifiedGraceSeconds).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'");
        try
        {
            db.Exec("DELETE FROM users WHERE substr(id,1,3)='mf_' AND email_verified=0 AND created_at<@cutoff AND (email=@email OR lower(username)=lower(@username))",
                new { cutoff, email, username });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // Something references the stale account after all, so it stays and its name stays taken.
        }

        // Hashing first keeps an existing address from answering faster than a new one.
        string hash = Passwords.Hash(password);
        UserRow? existing = EmailAccount(db, email);

        // Signing up again with the same name and address, before confirming, just resends the link below.
        bool retry = existing != null && existing.EmailVerified == 0 && existing.Username.Equals(username, StringComparison.OrdinalIgnoreCase);

        if (!retry && db.Exists("SELECT 1 FROM users WHERE lower(COALESCE(public_username,username))=lower(@username)", new { username }))
            throw ApiException.Conflict("This username is already taken.");

        if (existing != null)
        {
            // Don't reveal that the address has an account; tell its owner instead.
            throttle.Hit("mail:" + email, 3, TimeSpan.FromHours(1), "We've already sent several emails to this address. Check your inbox or try again later.");

            if (existing.EmailVerified == 0)
            {
                SendVerification(mailer, options, db, existing, email);
            }
            else
            {
                string subject = $"You already have a {options.BrandName} account";
                string html = $"<p style=\"margin:0 0 12px\">Someone tried to create a new {Mailer.Encode(options.BrandName)} account with this email address, but you already have one: <strong>{Mailer.Encode(existing.PublicUsername ?? existing.Username)}</strong>.</p><p style=\"margin:0;color:#9b9ea6;font-size:14px\">If that was you, sign in instead, or reset your password if you've forgotten it. Otherwise you can ignore this email.</p>";
                string text = $"Someone tried to create a new {options.BrandName} account with this email address, but you already have one: {existing.PublicUsername ?? existing.Username}.\n\nSign in: {options.PublicUrl}/login\nForgot your password? {options.PublicUrl}/reset-password";
                mailer.Enqueue(email, subject, text, mailer.Layout(subject, html, "Sign in", options.PublicUrl + "/login"), MailKinds.Account, existing.Id);
            }

            return Api.Json(new { ok = true, email }, 201);
        }

        string id = Text.EmailAccountPrefix + Text.Hex(8);

        try
        {
            db.Exec("""
                INSERT INTO users(id,username,display_name,email,email_verified,email_source,password_hash,created_at)
                VALUES(@id,@username,@username,@email,0,'user',@hash,@now)
                """, new { id, username, email, hash, now = Text.NowIso() });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw ApiException.Conflict("This username or email was just taken. Try again.");
        }

        throttle.Hit("mail:" + email, 3, TimeSpan.FromHours(1));
        SendVerification(mailer, options, db, db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id })!, email);
        return Api.Json(new { ok = true, email }, 201);
    }

    private static async Task<IResult> LoginAsync(HttpContext http, ForgeContext context, Db db, ForgeOptions options, Mailer mailer, AuthThrottle throttle)
    {
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send your username or email and password.");
        string login = Field(payload, "login", "Enter your username or email.").Trim();
        string password = Field(payload, "password", "Enter your password.");

        if (login == "" || login.Length > 254 || password.Length > 1024)
            throw ApiException.Unauthorized("Incorrect username, email or password.");

        string accountKey = "login:" + login.ToLowerInvariant();
        throttle.Hit(throttle.ClientKey(http) + ":login", 30, TimeSpan.FromMinutes(10));
        throttle.Hit(accountKey, 10, TimeSpan.FromMinutes(15), "Too many sign-in attempts for this account. Wait 15 minutes, or reset your password.");

        UserRow? user = login.Contains('@')
            ? EmailAccount(db, login.ToLowerInvariant())
            : db.One<UserRow>("SELECT * FROM users WHERE substr(id,1,3)='mf_' AND (lower(username)=lower(@login) OR lower(public_username)=lower(@login)) ORDER BY email_verified DESC LIMIT 1", new { login });

        if (!Passwords.Verify(password, user?.PasswordHash) || user == null)
            throw ApiException.Unauthorized("Incorrect username, email or password.");

        throttle.Reset(accountKey);
        CheckBan(user, options);

        if (user.EmailVerified == 0)
        {
            TokenRow? recent = db.One<TokenRow>("SELECT * FROM email_tokens WHERE user_id=@id AND purpose='verify' AND expires>@now ORDER BY created DESC LIMIT 1", new { id = user.Id, now = Text.NowEpoch() });

            if (recent == null || Text.NowEpoch() - recent.Created > 120)
                SendVerification(mailer, options, db, user, user.Email!);

            throw ApiException.Forbidden($"Confirm your email address first. We've sent a link to {user.Email}.");
        }

        return SignedIn(context, db, http.Response, user, payload);
    }

    private static async Task<IResult> VerifyAsync(HttpContext http, ForgeContext context, Db db, ForgeOptions options, Mailer mailer, AuthThrottle throttle)
    {
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send the token from your email.");
        throttle.Hit(throttle.ClientKey(http) + ":verify", 30, TimeSpan.FromMinutes(10));
        TokenRow? token = ReadToken(db, payload["token"].IsString(out string value) ? value : "", ["verify", "change"]);

        if (token == null)
            throw ApiException.BadRequest("This link has expired or was already used. Sign in to get a new one.");

        UserRow user = db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = token.UserId }) ?? throw ApiException.BadRequest("This account no longer exists.");
        CheckBan(user, options);
        db.Exec("DELETE FROM email_tokens WHERE token_hash=@hash", new { hash = token.TokenHash });

        if (token.Purpose == "verify")
        {
            if (user.Email != token.Email)
                throw ApiException.BadRequest("This link is for an address that's no longer on the account.");

            db.Exec("UPDATE users SET email_verified=1 WHERE id=@id", new { id = user.Id });
            db.Exec("DELETE FROM email_tokens WHERE user_id=@id AND purpose='verify'", new { id = user.Id });
            return SignedIn(context, db, http.Response, user, payload);
        }

        try
        {
            db.Exec("UPDATE users SET email=@email,email_verified=1,email_source='user' WHERE id=@id", new { email = token.Email, id = user.Id });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw ApiException.Conflict("Another account already uses this email address.");
        }

        if (user.Email != null && user.EmailVerified != 0 && user.Email != token.Email)
            SendNotice(mailer, options, user.Email, user.Id, $"Your {options.BrandName} email address was changed", $"The email address on your {options.BrandName} account was changed to {token.Email}.");

        context.Refresh();
        return Api.Json(new { ok = true, email = token.Email, changed = true });
    }

    private static async Task<IResult> ResendAsync(HttpContext http, Db db, ForgeOptions options, Mailer mailer, AuthThrottle throttle)
    {
        RequireMail(options);
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send your email address.");
        string email = NormalizeEmail(payload["email"]);
        throttle.Hit(throttle.ClientKey(http) + ":resend", 10, TimeSpan.FromHours(1));
        throttle.Hit("mail:" + email, 3, TimeSpan.FromHours(1), "We've already sent several emails to this address. Check your inbox or try again later.");
        UserRow? user = EmailAccount(db, email);

        if (user != null && user.EmailVerified == 0)
            SendVerification(mailer, options, db, user, email);

        return Api.Ok();
    }

    private static async Task<IResult> ForgotAsync(HttpContext http, Db db, ForgeOptions options, Mailer mailer, AuthThrottle throttle)
    {
        RequireMail(options);
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send your email address.");
        string email = NormalizeEmail(payload["email"]);
        throttle.Hit(throttle.ClientKey(http) + ":forgot", 10, TimeSpan.FromHours(1));
        throttle.Hit("mail:" + email, 3, TimeSpan.FromHours(1), "We've already sent several emails to this address. Check your inbox or try again later.");
        UserRow? user = EmailAccount(db, email);

        if (user != null)
        {
            string token = IssueToken(db, user.Id, "reset", email, ResetSeconds);
            string url = options.PublicUrl + "/reset-password?token=" + Uri.EscapeDataString(token);
            string subject = $"Reset your {options.BrandName} password";
            string html = $"<p style=\"margin:0 0 12px\">Someone asked to reset the password for <strong>{Mailer.Encode(user.PublicUsername ?? user.Username)}</strong>. The link works for one hour.</p><p style=\"margin:0;color:#9b9ea6;font-size:14px\">If that wasn't you, ignore this email. Your password won't change.</p>";
            string text = $"Someone asked to reset the password for {user.PublicUsername ?? user.Username}. The link works for one hour:\n\n{url}\n\nIf that wasn't you, ignore this email. Your password won't change.";
            mailer.Enqueue(email, subject, text, mailer.Layout(subject, html, "Choose a new password", url), MailKinds.Account, user.Id);
        }
        else if (db.One<UserRow>("SELECT * FROM users WHERE substr(id,1,3)!='mf_' AND is_group=0 AND email=@email AND email_verified=1", new { email }) is UserRow discord)
        {
            string subject = $"Your {options.BrandName} account uses Discord";
            string html = $"<p style=\"margin:0 0 12px\">Someone asked to reset a {Mailer.Encode(options.BrandName)} password for this address. Your account doesn't have a password: it signs in with Discord, or with a passkey if you've added one.</p>";
            string text = $"Someone asked to reset a {options.BrandName} password for this address. Your account doesn't have a password: it signs in with Discord, or with a passkey if you've added one.\n\n{options.PublicUrl}/login";
            mailer.Enqueue(email, subject, text, mailer.Layout(subject, html, "Sign in", options.PublicUrl + "/login"), MailKinds.Account, discord.Id);
        }

        return Api.Ok();
    }

    private static async Task<IResult> ResetAsync(HttpContext http, ForgeContext context, Db db, ForgeOptions options, Mailer mailer, AuthThrottle throttle)
    {
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send the token and a new password.");
        throttle.Hit(throttle.ClientKey(http) + ":reset", 20, TimeSpan.FromMinutes(10));
        TokenRow? token = ReadToken(db, payload["token"].IsString(out string value) ? value : "", ["reset"]);

        if (token == null)
            throw ApiException.BadRequest("This reset link has expired or was already used. Ask for a new one.");

        UserRow user = db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = token.UserId }) ?? throw ApiException.BadRequest("This account no longer exists.");
        string password = Field(payload, "password", "Choose a new password.");

        if (Passwords.Problem(password, user.Username, user.Email) is string problem)
            throw ApiException.BadRequest(problem);

        CheckBan(user, options);

        // Following the link proves the address, and the new password ends every other session.
        db.Exec("DELETE FROM email_tokens WHERE user_id=@id AND purpose='reset'", new { id = user.Id });
        db.Exec("UPDATE users SET password_hash=@hash,session_epoch=session_epoch+1,email_verified=CASE WHEN email=@email THEN 1 ELSE email_verified END WHERE id=@id",
            new { hash = Passwords.Hash(password), email = token.Email, id = user.Id });
        throttle.Reset("login:" + (user.Email ?? ""));
        throttle.Reset("login:" + user.Username.ToLowerInvariant());

        SendNotice(mailer, options, token.Email, user.Id, $"Your {options.BrandName} password was changed", $"The password for your {options.BrandName} account was just reset, and every other device was signed out.");
        return SignedIn(context, db, http.Response, user, payload);
    }

    // ---- passkey sign-in ----------------------------------------------------------------------------------------

    private static IResult PasskeyLoginOptions(HttpContext http, ForgeContext context, Passkeys passkeys, AuthThrottle throttle)
    {
        throttle.Hit(throttle.ClientKey(http) + ":passkey", 60, TimeSpan.FromMinutes(10));
        string challenge = Text.UrlToken(32);
        context.Session.PasskeyChallenge = challenge;
        context.Session.PasskeyPurpose = "login";
        context.Session.PasskeyStarted = Text.NowEpoch();

        return Api.Json(new { challenge, rp_id = passkeys.RpId, timeout = (int)(Passkeys.ChallengeSeconds * 1000), user_verification = "preferred" });
    }

    private static string TakeChallenge(ForgeContext context, string purpose)
    {
        string? challenge = context.Session.PasskeyChallenge;
        bool fresh = context.Session.PasskeyPurpose == purpose && Text.NowEpoch() - context.Session.PasskeyStarted <= Passkeys.ChallengeSeconds;
        context.Session.PasskeyChallenge = null;
        context.Session.PasskeyPurpose = null;
        context.Session.PasskeyStarted = 0;

        if (challenge == null || !fresh)
            throw ApiException.BadRequest("This passkey request expired. Try again.");

        return challenge;
    }

    private static async Task<IResult> PasskeyLoginAsync(HttpContext http, ForgeContext context, Db db, ForgeOptions options, Passkeys passkeys, AuthThrottle throttle)
    {
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send the passkey response.");
        throttle.Hit(throttle.ClientKey(http) + ":passkey", 60, TimeSpan.FromMinutes(10));
        string challenge = TakeChallenge(context, "login");

        string id = Field(payload, "id", "Send the passkey response.");
        byte[] clientData = passkeys.CheckClientData(Field(payload, "client_data_json", "Send the passkey response."), "webauthn.get", challenge);
        byte[] authData = Passkeys.FromBase64Url(Field(payload, "authenticator_data", "Send the passkey response."));
        byte[] signature = Passkeys.FromBase64Url(Field(payload, "signature", "Send the passkey response."));

        PasskeyRow key = db.One<PasskeyRow>("SELECT * FROM passkeys WHERE id=@id", new { id })
            ?? throw ApiException.Unauthorized("This passkey isn't registered here. It may have been removed from your account.");

        if (payload["user_handle"].IsString(out string handle) && handle != "" && !Text.SafeEquals(handle, Text.Base64Url(Encoding.UTF8.GetBytes(key.UserId))))
            throw ApiException.Unauthorized("This passkey doesn't match its account.");

        AuthenticatorData parsed = passkeys.ParseAuthenticatorData(authData, false);

        if (!Passkeys.VerifySignature(key, authData, clientData, signature))
            throw ApiException.Unauthorized("The passkey signature couldn't be verified.");

        // Authenticators that count must count up; a repeat or step back means the key may have been copied.
        if ((parsed.SignCount != 0 || key.SignCount != 0) && parsed.SignCount <= key.SignCount)
            throw ApiException.Unauthorized("This passkey reported an unexpected counter and may have been cloned. Remove it and add it again.");

        UserRow user = db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = key.UserId }) ?? throw ApiException.Unauthorized("This account no longer exists.");
        CheckBan(user, options);
        db.Exec("UPDATE passkeys SET sign_count=@count,last_used_at=@now WHERE id=@id", new { count = (long)parsed.SignCount, now = Text.NowIso(), id });

        return SignedIn(context, db, http.Response, user, payload);
    }

    // ---- account settings ---------------------------------------------------------------------------------------

    private static object PasskeyDto(PasskeyRow row)
    {
        return new { id = row.Id, name = row.Name, created_at = row.CreatedAt, last_used_at = row.LastUsedAt };
    }

    private static IResult Account(ForgeContext context, Db db, ForgeOptions options)
    {
        CurrentUser current = context.RequireUser("Sign in to see your account.");
        UserRow row = current.Row;
        string? pending = db.One<string>("SELECT email FROM email_tokens WHERE user_id=@id AND purpose='change' AND expires>@now", new { id = row.Id, now = Text.NowEpoch() });

        return Api.Json(new
        {
            id = row.Id,
            provider = row.Provider,
            username = row.Username,
            discord_username = row.DiscordName,
            email = row.EmailVerified != 0 ? row.Email : null,
            email_source = row.EmailVerified != 0 ? row.EmailSource : null,
            pending_email = pending,
            has_password = row.PasswordHash != null,
            mail_configured = options.MailConfigured,
            notifications = new { releases = row.EmailReleaseNotices != 0, announcements = row.EmailAnnouncements != 0 },
            passkeys = db.Query<PasskeyRow>("SELECT * FROM passkeys WHERE user_id=@id ORDER BY created_at", new { id = row.Id }).Select(PasskeyDto).ToList(),
        });
    }

    private static async Task<IResult> ChangeEmailAsync(HttpContext http, ForgeContext context, Db db, ForgeOptions options, Mailer mailer, AuthThrottle throttle)
    {
        CurrentUser current = context.RequireUser("Sign in to change your email.");
        RequireMail(options);
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send the new email address.");
        string email = NormalizeEmail(payload["email"]);
        UserRow row = current.Row;

        if (row.PasswordHash != null && !Passwords.Verify(payload["password"].IsString(out string password) ? password : "", row.PasswordHash))
        {
            throttle.Hit("password:" + row.Id, 10, TimeSpan.FromMinutes(15));
            throw ApiException.Forbidden("Your current password is incorrect.");
        }

        if (row.Email == email && row.EmailVerified != 0)
            throw ApiException.BadRequest("That's already your email address.");

        if (row.IsEmailAccount && db.Exists("SELECT 1 FROM users WHERE substr(id,1,3)='mf_' AND email=@email AND id!=@id", new { email, id = row.Id }))
            throw ApiException.Conflict("Another account already uses this email address.");

        throttle.Hit("change-email:" + row.Id, 5, TimeSpan.FromHours(1), "You've asked for several changes recently. Try again later.");
        string token = IssueToken(db, row.Id, "change", email, VerifySeconds);
        string url = options.PublicUrl + "/verify-email?token=" + Uri.EscapeDataString(token);
        string subject = $"Confirm your new {options.BrandName} email";
        string html = $"<p style=\"margin:0 0 12px\">Confirm that you want to use this address for your {Mailer.Encode(options.BrandName)} account <strong>{Mailer.Encode(current.Username)}</strong>. The link works for 24 hours.</p><p style=\"margin:0;color:#9b9ea6;font-size:14px\">If you didn't ask for this, ignore this email.</p>";
        string text = $"Confirm that you want to use this address for your {options.BrandName} account {current.Username}. The link works for 24 hours:\n\n{url}\n\nIf you didn't ask for this, ignore this email.";
        mailer.Enqueue(email, subject, text, mailer.Layout(subject, html, "Confirm email", url), MailKinds.Account, row.Id);

        return Api.Json(new { ok = true, pending_email = email });
    }

    private static async Task<IResult> ChangePasswordAsync(HttpContext http, ForgeContext context, Db db, ForgeOptions options, Mailer mailer, AuthThrottle throttle)
    {
        CurrentUser current = context.RequireUser("Sign in to change your password.");
        UserRow row = current.Row;

        if (!row.IsEmailAccount)
            throw ApiException.BadRequest("This account signs in with Discord, so it doesn't have a password.");

        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send your current and new password.");
        throttle.Hit("password:" + row.Id, 10, TimeSpan.FromMinutes(15));

        if (!Passwords.Verify(payload["current_password"].IsString(out string currentPassword) ? currentPassword : "", row.PasswordHash))
            throw ApiException.Forbidden("Your current password is incorrect.");

        string password = Field(payload, "password", "Choose a new password.");

        if (Passwords.Problem(password, row.Username, row.Email) is string problem)
            throw ApiException.BadRequest(problem);

        // Other devices are signed out; this one moves to the new epoch so it stays signed in.
        db.Exec("UPDATE users SET password_hash=@hash,session_epoch=session_epoch+1 WHERE id=@id", new { hash = Passwords.Hash(password), id = row.Id });
        context.SignIn(row.Id);

        if (row.Email != null && row.EmailVerified != 0)
            SendNotice(mailer, options, row.Email, row.Id, $"Your {options.BrandName} password was changed", $"The password for your {options.BrandName} account was changed, and every other device was signed out.");

        return Api.Json(new { ok = true, csrf_token = context.Csrf() });
    }

    private static async Task<IResult> NotificationsAsync(HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser current = context.RequireUser("Sign in to change email settings.");
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send releases and announcements as true or false.");

        if (!payload.HasOnly("releases", "announcements") || payload.Count == 0)
            throw ApiException.BadRequest("Send releases and announcements as true or false.");

        bool releases = current.Row.EmailReleaseNotices != 0;
        bool announcements = current.Row.EmailAnnouncements != 0;

        if ((payload.ContainsKey("releases") && !payload["releases"].IsBool(out releases)) || (payload.ContainsKey("announcements") && !payload["announcements"].IsBool(out announcements)))
            throw ApiException.BadRequest("Send releases and announcements as true or false.");

        db.Exec("UPDATE users SET email_release_notices=@releases,email_announcements=@announcements WHERE id=@id",
            new { releases = releases ? 1 : 0, announcements = announcements ? 1 : 0, id = current.Id });

        return Api.Json(new { releases, announcements });
    }

    private static IResult SignOutEverywhere(ForgeContext context, Db db)
    {
        CurrentUser current = context.RequireUser("Sign in first.");
        db.Exec("UPDATE users SET session_epoch=session_epoch+1 WHERE id=@id", new { id = current.Id });
        context.SignIn(current.Id);
        return Api.Json(new { ok = true, csrf_token = context.Csrf() });
    }

    // ---- passkey management -------------------------------------------------------------------------------------

    private static IResult ListPasskeys(ForgeContext context, Db db)
    {
        CurrentUser current = context.RequireUser("Sign in to manage passkeys.");
        return Api.Json(new { items = db.Query<PasskeyRow>("SELECT * FROM passkeys WHERE user_id=@id ORDER BY created_at", new { id = current.Id }).Select(PasskeyDto).ToList() });
    }

    private static IResult PasskeyRegisterOptions(ForgeContext context, Db db, ForgeOptions options, Passkeys passkeys)
    {
        CurrentUser current = context.RequireUser("Sign in to add a passkey.");
        string challenge = Text.UrlToken(32);
        context.Session.PasskeyChallenge = challenge;
        context.Session.PasskeyPurpose = "register";
        context.Session.PasskeyStarted = Text.NowEpoch();

        List<string> existing = db.Query<string>("SELECT id FROM passkeys WHERE user_id=@id", new { id = current.Id }).ToList();

        // The name is what password managers list the passkey under, so email accounts show their address.
        string name = current.Row.IsEmailAccount && current.Row.Email != null ? current.Row.Email : current.Username;

        return Api.Json(new
        {
            challenge,
            rp = new { id = passkeys.RpId, name = options.BrandName },
            user = new { id = Text.Base64Url(Encoding.UTF8.GetBytes(current.Id)), name, display_name = current.DisplayName },
            algorithms = new[] { Passkeys.ES256, Passkeys.RS256 },
            timeout = (int)(Passkeys.ChallengeSeconds * 1000),
            exclude = existing,
        });
    }

    private static async Task<IResult> RegisterPasskeyAsync(HttpRequest request, ForgeContext context, Db db, ForgeOptions options, Passkeys passkeys, Mailer mailer)
    {
        CurrentUser current = context.RequireUser("Sign in to add a passkey.");
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send the new passkey.");
        string challenge = TakeChallenge(context, "register");

        if (db.Scalar<long>("SELECT COUNT(*) FROM passkeys WHERE user_id=@id", new { id = current.Id }) >= MaxPasskeys)
            throw ApiException.BadRequest($"An account can have up to {MaxPasskeys} passkeys. Remove one first.");

        string id = Field(payload, "id", "Send the new passkey.");
        passkeys.CheckClientData(Field(payload, "client_data_json", "Send the new passkey."), "webauthn.create", challenge);
        AuthenticatorData parsed = passkeys.ParseAuthenticatorData(Passkeys.FromBase64Url(Field(payload, "authenticator_data", "Send the new passkey.")), true);

        if (parsed.CredentialId != id)
            throw ApiException.BadRequest("The passkey response is inconsistent. Try again.");

        if (!payload["public_key"].IsString(out string publicKey) || publicKey == "")
            throw ApiException.BadRequest("This browser didn't share the passkey's public key. Update your browser and try again.");

        if (!payload["algorithm"].IsInt(out long algorithm))
            throw ApiException.BadRequest("Send the passkey's algorithm.");

        byte[] spki = Passkeys.CheckPublicKey(Passkeys.FromBase64Url(publicKey), algorithm);
        string name = payload["name"].IsString(out string given) && given.Trim() != "" ? given.Trim() : "Passkey";

        if (Text.Len(name) > 60)
            throw ApiException.BadRequest("Use a passkey name of up to 60 characters.");

        List<string> transports = payload["transports"] is JsonArray list
            ? list.Select(t => t.IsString(out string value) ? value : "").Where(t => t is "usb" or "nfc" or "ble" or "internal" or "hybrid" or "smart-card").Distinct().ToList()
            : [];

        try
        {
            db.Exec("INSERT INTO passkeys(id,user_id,name,public_key,algorithm,sign_count,transports,created_at) VALUES(@id,@uid,@name,@spki,@algorithm,@count,@transports,@now)",
                new { id, uid = current.Id, name, spki, algorithm, count = (long)parsed.SignCount, transports = JsonBody.Serialize(transports), now = Text.NowIso() });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw ApiException.Conflict("This passkey is already registered.");
        }

        if (current.Row.Email != null && current.Row.EmailVerified != 0)
            SendNotice(mailer, options, current.Row.Email, current.Id, $"A passkey was added to your {options.BrandName} account", $"A passkey named \"{name}\" was added to your {options.BrandName} account. It can be used to sign in without a password.");

        return Api.Json(PasskeyDto(db.One<PasskeyRow>("SELECT * FROM passkeys WHERE id=@id", new { id })!), 201);
    }

    private static async Task<IResult> RenamePasskeyAsync(string id, HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser current = context.RequireUser("Sign in to manage passkeys.");
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send a name.");

        if (!payload.HasExactly("name") || !payload["name"].IsString(out string name) || name.Trim() == "" || Text.Len(name.Trim()) > 60)
            throw ApiException.BadRequest("Use a passkey name of 1–60 characters.");

        if (db.Exec("UPDATE passkeys SET name=@name WHERE id=@id AND user_id=@uid", new { name = name.Trim(), id, uid = current.Id }) == 0)
            throw ApiException.NotFound("Passkey not found.");

        return Api.Ok();
    }

    private static IResult DeletePasskey(string id, ForgeContext context, Db db)
    {
        CurrentUser current = context.RequireUser("Sign in to manage passkeys.");

        // Every account keeps another way in: Discord for Discord accounts, the password for email accounts.
        if (db.Exec("DELETE FROM passkeys WHERE id=@id AND user_id=@uid", new { id, uid = current.Id }) == 0)
            throw ApiException.NotFound("Passkey not found.");

        return Api.Ok();
    }
}
