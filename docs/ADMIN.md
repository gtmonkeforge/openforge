# Admin panel

The admin panel is a separate app on its own port. The public site doesn't serve it: every `/api/v1/admin/*` request on the public port returns 404, so you can firewall the admin port, put it behind a VPN, or reach it over an SSH tunnel.

## Setup

| Variable | Default | What it does |
|---|---|---|
| `ADMIN_PORT` | `5265` | Port the panel listens on. `0` serves the panel on the main site at `/admin` instead (the old behaviour). |
| `ADMIN_BIND` | `localhost` | Interface for the admin listener. `localhost` keeps it off the network; `*` listens on every interface. |
| `ADMIN_URL` | `http://localhost:<ADMIN_PORT>` | The panel's address as admins' browsers see it, such as `https://admin.monkeforge.org` behind a reverse proxy. The public site sends admins here. |

The admin listener is added next to the site's normal URLs (`ASPNETCORE_URLS`, `--urls` or launchSettings). If you configure Kestrel endpoints in `appsettings.json` instead, add the admin port there yourself.

### Signing in

Admins sign in on the public site as usual, then choose **Admin panel** in the account menu. `/admin` on the public site creates a single-use token that's valid for one minute, and redirects to `ADMIN_URL/admin/handoff`, which signs them in on the admin origin. This works whatever host the panel is on, and with every sign-in method (Discord, password or passkey). Passkeys and Discord stay tied to the public host.

On the admin port, the panel serves only its own pages and APIs, plus read-only mod, docs, media and download routes so reviewers see a submission the way users do.

## First-run setup

A server with no root administrator (`ROOT_ADMIN_ID` unset) is unclaimed. Every page redirects to `/setup`, and the server prints a one-time **setup token** to its log and saves it in `DATA_DIR/setup-token`, so only someone with access to the machine can claim it:

```
journalctl -u openforge | grep -A1 "setup token"     # or: cat data/setup-token
```

The wizard then:

1. checks the token,
2. creates the **root admin**, an email account with a password (its address is trusted without a confirmation email, since mail isn't set up yet), writes `ROOT_ADMIN_ID` and signs you in,
3. asks for the public URL and site name, outgoing email (with a live test send) and, optionally, the support mailbox (with an IMAP sign-in test), Discord sign-in and passkeys, the admin port and listen URLs,
4. shows a systemd unit generated for this machine, then restarts the server with the new settings.

Optional steps can be skipped and done later under **Settings**. Servers that already had `ROOT_ADMIN_ID` set never see the wizard.

## Settings

**Settings** (root only) edits every server setting, grouped into Site, Sign-in, Email, Support desk, Admin panel, Payments, Integrations and Server. Changes are written to the `.env` file in the server's working directory (permissions `600`), then applied by **Restart server**, which restarts OpenForge inside the same process in a few seconds. Open pages reconnect on their own.

- Each setting shows where its value comes from: `.env`, `appsettings.json`, the default, or **the environment**. Variables set by systemd (`Environment=`), Docker or the shell override `.env`, so those are shown locked; change them where the server is started.
- Before saving, the panel loads the whole configuration the way the server will on restart and refuses anything that wouldn't start (a bad URL, port or currency, a missing logo file, the site and admin panel on the same port, and so on).
- Secrets (passwords, API keys, webhook secrets) are never sent back to the browser. The panel only shows whether one is set; type a new value to replace it, or tick **Clear**.
- Email and support mailbox settings have test buttons that connect, sign in and send a real message using the values in the form, falling back to the saved password.
- The Sign-in tab shows the Discord redirect URL to register. Passkeys need no keys: they're tied to the public URL's host, so changing that host makes existing passkeys stop working.
- Settings marked **Careful** (listen URLs, admin port and bind address, data folder, root admin, payload key) can move the site, lock you out, or make sealed files unreadable. The panel asks again before saving them.

## Running as a service

`deploy/openforge.service` is a systemd unit, and Linux release builds include it with `install.sh`:

```
sudo ./install.sh              # creates an openforge user, installs to /opt/openforge, enables and starts the service
sudo ./install.sh /srv/forge   # somewhere else
```

Running it again upgrades the server and keeps `.env`, `data/` and `docs/`. **Settings → Server** shows a unit generated for the folder, user and executable the running server uses. The unit sets `Restart=always`, so the server also comes back if it crashes.

The unit uses `ProtectSystem=strict` with only the install folder writable. If `DATA_DIR` or `DOCS_DIR` point elsewhere, add them to `ReadWritePaths=`.

## Database

**Database** (root only) works on the raw registry database. Every change is recorded in the audit log, and none of the site's own checks run, so this is for repairs and investigations.

- **Tables** lists every table and view with row counts. Browse rows with sorting, a contains-filter on one or all columns, and paging. Double-click a cell to edit it, choosing whether it's stored as TEXT, INTEGER, REAL, BLOB (hex) or NULL. You can also insert and delete rows. Foreign keys are enforced, so a change that would break a reference is refused with the constraint's message. Views and `WITHOUT ROWID` tables are read-only here.
- **SQL console** runs any SQLite SQL. SQLite parses it one statement at a time, which is how statements are split and how each one is known to read or write before it runs:
  - Without **Allow writes**, the connection is read-only (`PRAGMA query_only`), and the first writing statement stops the run.
  - With writes, every statement runs in one transaction that commits only if all succeed. **Dry run** rolls it back at the end, so you can see the results and change counts first.
  - `ATTACH`, `DETACH`, `VACUUM` and transaction statements are refused. Each run stops after 15 seconds, and at most 500 rows are shown per statement.
  - Snippets, a table list and your recent queries (kept in this browser) are on the side. ⌘/Ctrl+Enter runs.

## Support mailbox (Purelymail)

Tickets come from the support mailbox over IMAP. Replies go out over SMTP, signed in as the same account, so they come from `support@` and thread in the customer's mail client.

```
SUPPORT_EMAIL=support@monkeforge.org
SUPPORT_IMAP_HOST=imap.purelymail.com
SUPPORT_IMAP_PORT=993
SUPPORT_MAIL_USERNAME=support@monkeforge.org   # defaults to SUPPORT_EMAIL
SUPPORT_MAIL_PASSWORD=...                       # the mailbox's password or an app password
SUPPORT_SMTP_HOST=smtp.purelymail.com           # defaults to SMTP_HOST
SUPPORT_SMTP_PORT=465                           # defaults to 465 for ssl, 587 for starttls
SUPPORT_SMTP_SECURITY=ssl                       # defaults to SMTP_SECURITY
SUPPORT_FROM_NAME=MonkeForge Support            # defaults to "<BRAND_NAME> Support"
SUPPORT_POLL_SECONDS=30                         # how often the inbox is checked (minimum 10)
```

`SUPPORT_EMAIL` is also the contact address Stripe shows buyers (see PAYMENTS.md), so it's usually set already.

How mail becomes tickets:

- The inbox is polled every `SUPPORT_POLL_SECONDS`, and right away when someone presses **Check mail**. Progress is the last IMAP UID handled, so reading the mailbox in another client doesn't hide anything. On the first run, only unread mail from the last 7 days is imported.
- A message joins an existing ticket when its `In-Reply-To` or `References` header matches a message on the ticket, or its subject contains the ticket's reference, such as `[MF-7K3Q9DXA]`. Anything else opens a new ticket with a new reference and number. A reply from the customer reopens a closed ticket.
- New tickets get the auto-reply with the reference ID, ticket number and title. Auto-replies are skipped for mail that is itself automated (`Auto-Submitted`, `Precedence: bulk/list/junk`, `List-Id`, `X-Auto-Response-Suppress`), for `noreply@`, `mailer-daemon@` and similar senders, and after 3 auto-replies to the same address within an hour. Auto-replies carry `Auto-Submitted: auto-replied`.
- Attachments up to 20 MB each (15 per message) are saved in `DATA_DIR/support` and can only be downloaded by admins, never shown inline.
- The greeting uses the person's username when the address belongs to a confirmed account, and the address otherwise.

Staff replies use the support reply template and are signed with the admin's username. Each reply is queued in the normal outbox, so the **Mail queue** page shows its delivery and lets you retry failures.

## Pages

- **Dashboard**: pending reviews, tickets, disputes, mail failures, errors, recent staff activity and mailbox status.
- **Reviews**: the queue, oldest first. Each review is one full-screen page with the mod page, the author, every MonkeShield report and failing checks, review history and staff notes. Approve or deny with a reason (required to deny). The author, or every member of the owning group, gets a notification and an email (if they get mod notices) with the reason. They fix the mod and press **Resubmit for review** on the mod page, or publish a new release. Shortcuts: `A` approve, `D` write a reason, `J`/`K` next/previous.
- **All mods**: every listing, with unapprove, remove, restore, Modder's Choice and permanent purge.
- **Tickets**: folders, list and reading pane. Reply or add internal notes, set the status (open, waiting on customer, closed) and assignee, insert links to docs, mods, profiles and site pages, use saved replies, and preview the exact email. **New message** starts a conversation from support; replies to it come back as a ticket.
- **Users**: nickname, username, mod count, user ID and Stripe account ID, with search and filters. Actions: email from support, send a password reset (email accounts only), remove passkeys (all or one), sign out everywhere, verify, ban, and delete. Only the root administrator can act on another admin.
- **Announcements**: a Markdown drafter with live, site-styled preview, autosaved drafts, and an audience of all users, everyone including unsubscribed (root only), or one address. Single-address emails can come from support so replies become a ticket.
- **Settings** and **Database** (root only) are described above.
- **Disputes**, **Analytics**, **Server errors** and **Administrators** (root only) work as before. **Mail queue** lists every queued, sent and failed email; **Audit log** lists every staff action.

## Deleting an account

Deleting keeps the user row so payments, refunds and disputes still add up, but removes everything that identifies or signs in the person: name (shown as "Deleted user"), email, password, passkeys, API keys, sign-in sessions, friends, group memberships, ratings, comments and notifications. Their mods are removed from the site, and live subscriptions they buy or sell are cancelled in Stripe. The account can't sign in again. If a Stripe cancellation fails, the result says how many to cancel by hand.
