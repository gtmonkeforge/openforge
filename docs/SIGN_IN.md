# Signing in to MonkeForge

MonkeForge doesn't have passwords. Everyone signs in with Discord, and that's it. Your Discord account is your MonkeForge account.

This page covers the three ways to sign in, depending on what you're building:

- **The website.** Click the button, approve on Discord, done.
- **Desktop apps** like Launchpad or the CLI. The app opens your browser, you approve on Discord, and the app gets its own session.
- **Scripts and CI.** Use an access key. Keys can only publish releases, nothing else.

At the end there's a section for people running their own OpenForge server.

---

## On the website

Hit **Sign in with Discord**. You'll land on Discord's approval screen, which asks for the `identify` permission only. That gives us your user ID, username, display name and avatar. We never see your email, your servers, or your password.

After you approve, Discord sends you back and you're signed in for 30 days. Every time you use the site the 30 days start over, so you won't get kicked out while you're active.

A couple of things worth knowing:

- The approval has to happen within 10 minutes. If you wander off and come back later, just start again.
- If you started signing in from a page we don't redirect back to, you'll end up on the Publish page. That's normal.
- If your account is banned, sign-in stops with a message saying so.

## In a desktop app

Desktop apps can't safely hold a Discord client secret, so they use a slightly different flow. It's the standard "sign in through the browser and come back to the app" pattern ([OAuth with PKCE](https://oauth.net/2/pkce/)), and if you've built one of these before it'll look familiar.

Here's the whole thing:

1. **Make three random values.**
   - A `code_verifier`: 43 to 128 characters from `A-Z a-z 0-9 . _ ~ -`. Keep it secret.
   - The `code_challenge`: SHA-256 of the verifier, base64url-encoded with no padding. It's always 43 characters.
   - A `state`: 22 to 128 random base64url characters. This is how you'll know the response is for you.

2. **Start a tiny web server on the loopback address**, on any free port, like `http://127.0.0.1:53682/callback`. It must be `127.0.0.1` or `[::1]`. `localhost` is rejected, and so is anything with a `?` or `#` in it.

3. **Open the user's browser** to:

   ```
   https://monkeforge.org/auth/discord
     ?redirect_uri=http://127.0.0.1:53682/callback
     &code_challenge=<challenge>
     &code_challenge_method=S256
     &state=<state>
   ```

4. **Wait for the browser to hit your server.** After the user approves on Discord, they'll be sent to your `redirect_uri` with `?code=...&state=...`. Check that `state` matches what you sent. If it doesn't, throw the request away. Then show the user a "You can close this tab now" page.

5. **Swap the code for a session, quickly.** The code works once and expires after 60 seconds.

   ```http
   POST /api/v1/auth/native/exchange
   Content-Type: application/json

   { "code": "<code>", "code_verifier": "<verifier>", "redirect_uri": "http://127.0.0.1:53682/callback" }
   ```

   `redirect_uri` must be exactly the same string you used in step 3.

   You'll get back the user and a CSRF token:

   ```json
   { "user": { "id": "...", "username": "...", ... }, "csrf_token": "..." }
   ```

   along with a `session` cookie in `Set-Cookie`.

6. **Keep the cookie and the CSRF token.** The cookie is the sign-in, and it lasts 30 days like on the website. Send it with every request. For anything that changes data (POST, PUT, PATCH, DELETE), also send the token as an `X-CSRF-Token` header. Store both somewhere the OS protects, like the Windows Credential Manager or the macOS Keychain, not in a plain config file.

One side effect: because the approval happened in the user's browser, the browser ends up signed in too. That's expected.

### A minimal example

Python, standard library only, minus the bit where you open the browser and catch the redirect:

```python
import base64, hashlib, secrets, json, urllib.request

verifier = secrets.token_urlsafe(48)          # 64 chars
challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
state = secrets.token_urlsafe(24)
redirect = "http://127.0.0.1:53682/callback"

# ...open the browser, receive ?code=...&state=... on your loopback server, check state...

body = json.dumps({"code": code, "code_verifier": verifier, "redirect_uri": redirect}).encode()
req = urllib.request.Request("https://monkeforge.org/api/v1/auth/native/exchange", body, {"Content-Type": "application/json"})
with urllib.request.urlopen(req) as res:
    session_cookie = res.headers["Set-Cookie"]   # keep the "session=..." part
    csrf_token = json.load(res)["csrf_token"]
```

### Signing out

Send `POST /api/v1/logout` with the cookie and the CSRF header, then delete what you stored. To be honest about a gap here: sessions live in an encrypted cookie, so the server doesn't keep a list of them, and there's no "sign out of all devices" button yet. A banned account is locked out right away on every device, though, because every request checks the account.

### Using the injector?

Once you're signed in, Launchpad also registers the computer as a device before it can install injector mods. That's covered in [INJECTOR_PROTOCOL.md](INJECTOR_PROTOCOL.md).

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
| "Native sign-in requires a loopback HTTP redirect, S256 challenge, and random state" | One of the step 3 values is off. Check the redirect is `http://127.0.0.1:<port>/...` and the challenge is exactly 43 characters. |
| "Native sign-in expired or could not be verified" | The code was already used, is over 60 seconds old, or `redirect_uri` / `code_verifier` don't match. Start the flow again. |
| "Refresh the page and try again. The security token is missing or expired." | A POST/PUT/PATCH/DELETE without a valid `X-CSRF-Token`. Get a fresh one from `GET /api/v1/session`. |
| "API keys may only publish releases." | An access key was used on an endpoint other than publishing. |
| "Your account is banned from this site." | Exactly what it says. Talk to MonkeForge staff. |

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

`PUBLIC_URL` has to match the redirect you registered, character for character. If someone visits your server on a different hostname, sign-in sends them to `PUBLIC_URL` first, so the login cookie and Discord's redirect agree.

Sessions are encrypted with keys stored in `<DATA_DIR>/keys`. Back that folder up. If those keys are lost, nobody's data is lost, but everyone gets signed out and has to sign in again.
