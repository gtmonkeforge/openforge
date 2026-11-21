# Signing in to MonkeForge

There are two kinds of MonkeForge account:

- **Discord accounts.** Sign in with Discord and your Discord account is your MonkeForge account. The account ID is your Discord user ID.
- **Email accounts.** Pick a username, give an email address and a password. The account ID starts with `mf_`. You have to confirm the address from the email we send before you can sign in.

Either kind can add **passkeys** (Face ID, Touch ID, Windows Hello, a phone or a security key) and sign in with those instead.

This page covers the ways to sign in, depending on what you're building:

- **The website.** Go to `/login` and pick Discord, a passkey, or your username/email and password.
- **Apps** like Launchpad, the CLI, or anything a creator builds. These are OAuth2 applications: the app opens your browser, you sign in any of those ways, approve the app, and it gets its own tokens.
- **Scripts and CI.** Use an access key. Keys can only publish releases, nothing else.

At the end there's a section for people running their own OpenForge server.

---

## On the website

Hit **Sign in** and choose how.

**Discord.** You'll land on Discord's approval screen, which asks for the `identify` and `email` permissions. That gives us your user ID, username, display name, avatar and, if Discord has verified it, your email address. We never see your servers or your Discord password. The email is only used for the notices described under [Email](#email); you can change it or turn the notices off in **Settings**.

**Username or email and password.** Make an account at `/signup`. We email a confirmation link that works for 24 hours; until you open it you can't sign in, and trying to sign in sends a fresh link. Forgot the password? `/reset-password` emails a link that works for an hour. Resetting signs you out everywhere else.

**Passkey.** Add one in **Settings -> Sign-in & security**, then use **Sign in with a passkey** on the login page. Passkeys only work on the site's main address (`PUBLIC_URL`).

Whichever you use, you're signed in for 30 days. Every time you use the site the 30 days start over, so you won't get kicked out while you're active.

A couple of things worth knowing:

- The approval has to happen within 10 minutes. If you wander off and come back later, just start again.
- If you started signing in from a page we don't redirect back to, you'll end up on the Publish page. That's normal.
- If your account is banned, sign-in stops with a message saying so.

## In an app (OAuth2)

Apps sign people in with OAuth2. This replaces the old desktop PKCE handoff (`/login?redirect_uri=...` plus `/api/v1/auth/native/exchange`), which has been removed.

1. Register the app under **Settings -> Developer -> Applications**. Desktop apps should be **public clients** with a loopback redirect such as `http://127.0.0.1/callback`. Any port is accepted at sign-in time.
2. Send the user to `/oauth/authorize` with `response_type=code`, your `client_id`, `redirect_uri`, `scope`, `state`, and a PKCE `code_challenge` (S256).
3. Exchange the returned `code` at `POST /api/v1/oauth/token` for an access token (1 hour) and a refresh token (60 days, rotated on every use).
4. Call the API with `Authorization: Bearer <access token>`. Bearer tokens don't need a CSRF token.

What a token can do depends on its scopes: `identify`, `email`, `publish` (like an access key), and `account` (the whole API as the user, minus security settings, payouts and admin). Only apps that staff have **verified** can ask for `account`. Verified apps show a checkmark on the approval screen.

The full walkthrough, with request and response examples, is in the developer guide at `/docs/guides/oauth-apps` ([source](../src/docs/guides/oauth-apps.md)).

People manage the apps they've approved in **Settings -> Sign-in & security -> Authorized apps**. Removing an app, signing out everywhere, or changing the password ends its tokens at once.

### Signing out

Revoke the refresh token with `POST /api/v1/oauth/revoke`, then delete what you stored. Keep tokens somewhere the OS protects, like the Windows Credential Manager or the macOS Keychain, not in a plain config file.

### Using the injector?

Once signed in with the `account` scope, Launchpad also registers the computer as a device before it can install injector mods. That's covered in [INJECTOR_PROTOCOL.md](INJECTOR_PROTOCOL.md).

## From scripts and CI

For publishing from GitHub Actions or a build script, don't do any of the above. Make an access key instead:

1. Go to **Settings -> Access keys** and create one. You'll see it once, so copy it straight into your repository secrets.
2. Send it as a bearer token:

   ```
   curl -X POST "https://monkeforge.org/api/v1/mods/com.you.mod/releases" \
     -H "Authorization: Bearer $MONKEFORGE_KEY" \
     -F version=1.2.0 -F channel=stable \
     -F downloads_bepinex5=@bin/YourMod.dll
   ```

Keys are deliberately limited. They can only `POST` to the two publishing endpoints, `/api/v1/mods` and `/api/v1/mods/{guid}/releases`. Use a key anywhere else and you'll get a 403, even for something harmless like reading a mod. Keys don't need a CSRF token. If one leaks, revoke it from the same settings page and it stops working immediately.

## When something goes wrong

| What you see | What it means |
|---|---|
| "Discord sign-in expired or could not be verified" | More than 10 minutes passed, or the browser lost its cookie partway through. Start again. |
| An app "asked to send you to an address it hasn't registered" | The app's `redirect_uri` isn't in its registered list. The app's developer needs to add it under **Settings -> Developer -> Applications**. |
| `invalid_grant` from `/api/v1/oauth/token` | The code was already used, is over 60 seconds old, or `redirect_uri` / `code_verifier` don't match; or the refresh token was already rotated or revoked. Start sign-in again. |
| "This app wasn't given permission to do that." | The access token's scopes don't cover that endpoint. |
| "Refresh the page and try again. The security token is missing or expired." | A POST/PUT/PATCH/DELETE without a valid `X-CSRF-Token`. Get a fresh one from `GET /api/v1/session`. |
| "API keys may only publish releases." | An access key was used on an endpoint other than publishing. |
| "Your account is banned from this site." | Exactly what it says. Talk to MonkeForge staff. |
| "Confirm your email address first." | The email account hasn't been confirmed. A new link was just sent; open it, then sign in. |
| "Too many sign-in attempts for this account." | Ten wrong passwords in 15 minutes. Wait, or reset the password. |
| "Passkeys only work on https://…" | The page wasn't on the site's main address. Open `PUBLIC_URL` and try again. |

---

## Email

Accounts with a confirmed address (email accounts, and Discord accounts whose Discord email is verified or who added one in settings) get:

- **Account email**, always: confirmation links, password resets, and alerts when the password, email address or passkeys change.
- **Release notices**: when anyone uploads a release to a mod you own, or to a mod owned by a publishing group you're in. This is worth keeping on: it's how you'd spot an upload you didn't make, say from a leaked access key.
- **Announcements** from staff, sent from the admin panel's **Email** tab.

The last two can be turned off in **Settings -> Email** or with the unsubscribe link in each message. Bulk mail carries a one-click `List-Unsubscribe` header too. Discord accounts created before email support pick up their address the next time they sign in with Discord.

---

## Running your own server

If you're hosting OpenForge yourself, sign-in needs a Discord application. It takes about five minutes.

1. Create an application in the [Discord Developer Portal](https://discord.com/developers/applications).
2. Under **OAuth2**, add a redirect: your public URL followed by `/auth/discord/callback`, for example `https://mods.example.com/auth/discord/callback`.
3. Put these in your `.env`:

   ```
   PUBLIC_URL=https://mods.example.com
   DISCORD_CLIENT_ID=...
   DISCORD_CLIENT_SECRET=...
   COOKIE_SECURE=true          # once you're behind HTTPS
   ROOT_ADMIN_ID=...           # your own Discord user ID, to get the admin panel
   ```

`PUBLIC_URL` has to match the redirect you registered, character for character. It also decides where passkeys work: they're tied to its hostname. If someone visits your server on a different hostname, sign-in sends them to `PUBLIC_URL` first, so the login cookie and Discord's redirect agree.

### Email

Email accounts, release notices and announcements need an SMTP server. Without one the site runs Discord-only and the email settings explain that email isn't set up.

```
SMTP_HOST=smtp.purelymail.com
SMTP_PORT=465                # 465 with ssl, 587 with starttls
SMTP_SECURITY=ssl            # ssl, starttls or none
SMTP_USERNAME=noreply@example.com
SMTP_PASSWORD=...
MAIL_FROM=noreply@example.com   # defaults to SMTP_USERNAME
MAIL_FROM_NAME=My Mods          # defaults to the brand name
MAIL_PER_MINUTE=120             # sending pace, to stay under your provider's limits
EMAIL_SIGNUP=true               # false keeps email for notices but stops new email accounts
```

Mail goes through a queue in the database (`email_outbox`) and a background sender, so a slow or unavailable mail server never holds up a request. Failed sends retry with backoff for about nine hours. Connection problems show up in the admin panel's **Errors** tab. Set up SPF, DKIM and DMARC for the sending domain with your provider, or a lot of this mail will land in spam.

Sessions are encrypted with keys stored in `<DATA_DIR>/keys`. Back that folder up. If those keys are lost, nobody's data is lost, but everyone gets signed out and has to sign in again.
