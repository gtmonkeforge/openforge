---
order: 30
description: Register an OAuth2 application so people can sign in to your website, bot, launcher or tool with their account.
---
# Sign in with MonkeForge (OAuth2 apps)

Any account can register an **OAuth2 application** and let people sign in to it with their account. Your app gets a client ID and secret. When people sign in, they see your app's name and logo, and a checkmark if staff have verified it.

This is standard OAuth2 (authorization code grant, with PKCE), so most OAuth client libraries work with it unchanged. The discovery document at `/.well-known/oauth-authorization-server` lists every endpoint.

## Registering an app

1. Go to **Settings → Developer → Applications** and create an application.
2. Set the name, and optionally a logo, description and website. These are what people see when they approve your app.
3. Add your **redirect URIs**, one per line, up to 10. Each can be:
   - an `https://` URL, matched exactly;
   - `http://127.0.0.1/...`, `http://[::1]/...` or `http://localhost/...` for desktop apps. Any port works, so you can pick a free one each time;
   - a reverse-domain app scheme, such as `com.example.launcher:/callback`.
4. Choose the client type:
   - **Confidential** (the default) is for apps with a server. Create a **client secret** and keep it on that server.
   - **Public** is for desktop and mobile apps, which can't keep a secret. They must use PKCE instead.

Secrets are shown once. To rotate one, create a new secret, deploy it, then delete the old one.

## Scopes

| Scope | What the app gets |
|---|---|
| `identify` | Account ID, username, display name, avatar and profile URL. Always included. |
| `email` | The account's confirmed email address. |
| `publish` | Publishing releases to mods the person manages, like an access key. |
| `account` | Uses the API as the person: installing mods, purchases, friends, devices and so on. **Only for verified apps.** |

Even with `account`, a token can't reach account security settings (password, email, passkeys, access keys, authorized apps), payouts, or the admin panel.

## The flow

### 1. Send the person to the authorization page

```
https://monkeforge.org/oauth/authorize
  ?response_type=code
  &client_id=<client id>
  &redirect_uri=<one of your redirect URIs>
  &scope=identify email
  &state=<random value>
  &code_challenge=<S256 challenge>
  &code_challenge_method=S256
```

- `state` comes back unchanged. Check it to make sure the response is for a request you made.
- PKCE is required for public clients and recommended for everyone. Make a random `code_verifier` of 43–128 characters (`A-Z a-z 0-9 . _ ~ -`). The `code_challenge` is its SHA-256, base64url-encoded without padding.
- Add `prompt=consent` to show the approval screen even if the person already approved your app. Otherwise, someone who already approved the same scopes goes straight back to your app.

If they aren't signed in, they sign in first (with a password, a passkey or Discord), then see your app.

### 2. Handle the redirect

If they approve, they're sent to your redirect URI with `?code=...&state=...&iss=...`. If they cancel, you get `?error=access_denied&state=...`. Codes work once and expire after 60 seconds.

### 3. Exchange the code for tokens

```http
POST /api/v1/oauth/token
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

grant_type=authorization_code&code=<code>&redirect_uri=<same redirect URI>&code_verifier=<verifier>
```

Confidential clients can send `client_id` and `client_secret` in the body instead of the Basic header. Public clients send only `client_id`.

```json
{ "access_token": "mfat_...", "token_type": "Bearer", "expires_in": 3600, "refresh_token": "mfrt_...", "scope": "email identify" }
```

### 4. Call the API

```http
GET /api/v1/oauth/userinfo
Authorization: Bearer mfat_...
```

```json
{ "sub": "mf_1a2b...", "id": "mf_1a2b...", "username": "player", "display_name": "Player", "avatar": "https://...", "profile_url": "https://...", "is_verified": false, "scope": "email identify", "email": "player@example.com", "email_verified": true }
```

Use the account ID (`sub`) to identify people, not the username, which can change. Tokens with `publish` or `account` scope work on the rest of the API in the same way. They don't need a CSRF token.

### 5. Refresh

Access tokens last an hour. Refresh tokens last 60 days and change every time you use one, so always store the new one:

```http
POST /api/v1/oauth/token
grant_type=refresh_token&refresh_token=mfrt_...   (plus client authentication)
```

You can pass `scope` to ask for fewer scopes, but not more.

### Signing out

`POST /api/v1/oauth/revoke` with `token=<token>` and client authentication. Revoking a refresh token signs your app out of that account entirely.

Tokens also stop working when the person removes your app (**Settings → Sign-in & security → Authorized apps**), signs out everywhere, changes or resets their password, or is banned. Your next refresh then fails with `invalid_grant`. When that happens, send them through sign-in again.

## Verification

Staff can verify apps once they've checked who is behind them. Verified apps get a checkmark next to their name on the sign-in screen and can request the `account` scope. Unverified apps show people a short warning.

Changing a verified app's name, website or logo removes its verification until staff check it again. The `account` scope stops working as soon as an app loses verification.

## Errors

The token and revoke endpoints return standard OAuth errors (`{"error": "...", "error_description": "..."}`):

| Error | Meaning |
|---|---|
| `invalid_client` (401) | Wrong client ID or secret, or a confidential app sent no secret. |
| `invalid_grant` | The code or refresh token is wrong, expired or already used, or `redirect_uri` / `code_verifier` don't match. |
| `invalid_scope` | An unknown scope, `account` from an unverified app, or a refresh that tried to widen scopes. |
| `unsupported_grant_type` | Only `authorization_code` and `refresh_token` are supported. |

The authorization page never redirects to an unregistered redirect URI. If the client ID or redirect URI is wrong, the person sees an error page instead.
