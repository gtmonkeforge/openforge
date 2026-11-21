using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;

namespace OpenForge.Infrastructure;
public static class Schema
{
    public static void Migrate(ForgeOptions options)
    {
        Directory.CreateDirectory(options.DataDir);
        Directory.CreateDirectory(options.FilesDir);
        using var db = Db.Open(options.RegistryPath);

        db.Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY, username TEXT NOT NULL, display_name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS mods(
              guid TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT NOT NULL, author_id TEXT NOT NULL REFERENCES users(id),
              category TEXT NOT NULL, source_url TEXT, logo TEXT, screenshots TEXT NOT NULL DEFAULT '[]',
              capabilities TEXT NOT NULL DEFAULT '{}', current_version TEXT NOT NULL,
              created_at TEXT NOT NULL, updated_at TEXT NOT NULL, downloads INTEGER NOT NULL DEFAULT 0,
              demo INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS releases(
              id INTEGER PRIMARY KEY, guid TEXT NOT NULL REFERENCES mods(guid), version TEXT NOT NULL,
              released_at TEXT NOT NULL, notes TEXT NOT NULL DEFAULT '', UNIQUE(guid, version));
            CREATE TABLE IF NOT EXISTS files(
              id TEXT PRIMARY KEY, release_id INTEGER REFERENCES releases(id), stored_name TEXT NOT NULL,
              filename TEXT NOT NULL, size INTEGER NOT NULL, sha256 TEXT NOT NULL, kind TEXT NOT NULL);
            """);

        HashSet<string> userColumns = Columns(db, "users");

        foreach (var (name, definition) in new[]
        {
            ("description", "TEXT NOT NULL DEFAULT ''"), ("links", "TEXT NOT NULL DEFAULT '[]'"),
            ("is_admin", "INTEGER NOT NULL DEFAULT 0"), ("is_verified", "INTEGER NOT NULL DEFAULT 0"),
            ("is_banned", "INTEGER NOT NULL DEFAULT 0"), ("public_username", "TEXT"), ("avatar", "TEXT"),
            ("custom_slug", "TEXT"), ("accent_color", "TEXT NOT NULL DEFAULT '#dc5c3b'"), ("is_developer", "INTEGER NOT NULL DEFAULT 0"),
        })
        {
            if (!userColumns.Contains(name))
                db.Execute($"ALTER TABLE users ADD COLUMN {name} {definition}");
        }

        HashSet<string> modColumns = Columns(db, "mods");
        if (!modColumns.Contains("status"))
            db.Execute("ALTER TABLE mods ADD COLUMN status TEXT NOT NULL DEFAULT 'approved'");

        if (!modColumns.Contains("visibility"))
            db.Execute("ALTER TABLE mods ADD COLUMN visibility TEXT NOT NULL DEFAULT 'public'");

        db.Execute("UPDATE mods SET status='approved' WHERE visibility IN ('private','code') AND status='pending'");
        foreach (string column in new[] { "banner", "choice_category", "custom_slug", "choice_note" })
            if (!modColumns.Contains(column))
                db.Execute($"ALTER TABLE mods ADD COLUMN {column} TEXT");

        // Modder's Choice used to be one mod per category. It's now a single badge any number of mods can hold,
        // so every mod that held a category badge keeps the badge, without a note until staff write one.
        if (!modColumns.Contains("is_choice"))
            db.Execute("ALTER TABLE mods ADD COLUMN is_choice INTEGER NOT NULL DEFAULT 0");

        db.Execute("UPDATE mods SET is_choice=1,choice_category=NULL WHERE choice_category IS NOT NULL");

        // Medals are staff-named awards, such as a game jam placing. A mod can hold several, and its creator shows them too.
        db.Execute("""
            CREATE TABLE IF NOT EXISTS mod_medals(
                id INTEGER PRIMARY KEY, guid TEXT NOT NULL REFERENCES mods(guid) ON DELETE CASCADE,
                name TEXT NOT NULL, created_by TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS mod_medals_guid ON mod_medals(guid);
            """);

        db.Execute("""
            CREATE UNIQUE INDEX IF NOT EXISTS user_public_username ON users(public_username COLLATE NOCASE) WHERE public_username IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS user_slug ON users(custom_slug) WHERE custom_slug IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS mod_slug ON mods(custom_slug) WHERE custom_slug IS NOT NULL;
            CREATE TABLE IF NOT EXISTS admin_comments(id INTEGER PRIMARY KEY, guid TEXT NOT NULL REFERENCES mods(guid) ON DELETE CASCADE,
                author_id TEXT NOT NULL REFERENCES users(id), body TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS admin_audit(id INTEGER PRIMARY KEY, actor_id TEXT NOT NULL, action TEXT NOT NULL, target TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS activity(id INTEGER PRIMARY KEY, kind TEXT NOT NULL, guid TEXT, category TEXT, country TEXT NOT NULL,
                created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS activity_date ON activity(created_at);
            CREATE INDEX IF NOT EXISTS activity_guid_date ON activity(guid,created_at);
            """);

        if (!userColumns.Contains("accent_customized"))
        {
            db.Execute("ALTER TABLE users ADD COLUMN accent_customized INTEGER NOT NULL DEFAULT 0");
            db.Execute("UPDATE users SET accent_customized=1 WHERE accent_color NOT IN ('#60cdff','#dc5c3b')");
        }
        db.Execute("UPDATE users SET accent_color='#dc5c3b' WHERE accent_customized=0");

        MigrateChannels(db);

        db.Execute("""
            CREATE TABLE IF NOT EXISTS monkeshield_reports(
                guid TEXT NOT NULL, channel TEXT NOT NULL, release_id INTEGER NOT NULL,
                version TEXT NOT NULL, report TEXT NOT NULL, PRIMARY KEY(guid,channel));
            CREATE TABLE IF NOT EXISTS api_keys(
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id),
                name TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE, prefix TEXT NOT NULL,
                created_at TEXT NOT NULL, last_used_at TEXT, revoked_at TEXT);
            CREATE TABLE IF NOT EXISTS file_loaders(
                file_id TEXT PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
                loader TEXT NOT NULL CHECK(loader IN ('bepinex5','bepinex6','melonloader')));
            """);

        if (!Columns(db, "mods").Contains("links"))

            db.Execute("ALTER TABLE mods ADD COLUMN links TEXT NOT NULL DEFAULT '[]'");

        db.Execute("""
            CREATE TABLE IF NOT EXISTS mod_comments(
                id INTEGER PRIMARY KEY, guid TEXT NOT NULL REFERENCES mods(guid),
                user_id TEXT NOT NULL REFERENCES users(id), body TEXT NOT NULL,
                created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS comments_mod ON mod_comments(guid,id);
            CREATE TABLE IF NOT EXISTS mod_ratings(
                guid TEXT NOT NULL REFERENCES mods(guid), user_id TEXT NOT NULL REFERENCES users(id),
                stars INTEGER NOT NULL CHECK(stars BETWEEN 1 AND 5), PRIMARY KEY(guid,user_id));
            """);

        if (!Columns(db, "users").Contains("is_group"))

            db.Execute("ALTER TABLE users ADD COLUMN is_group INTEGER NOT NULL DEFAULT 0");

        db.Execute("""
            CREATE TABLE IF NOT EXISTS group_members(group_id TEXT REFERENCES users(id),user_id TEXT REFERENCES users(id),role TEXT NOT NULL DEFAULT 'member',PRIMARY KEY(group_id,user_id));
            CREATE TABLE IF NOT EXISTS friendships(sender TEXT REFERENCES users(id),recipient TEXT REFERENCES users(id),accepted INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(sender,recipient),CHECK(sender!=recipient));
            CREATE TABLE IF NOT EXISTS presence(user_id TEXT PRIMARY KEY REFERENCES users(id),room_code TEXT,visibility TEXT NOT NULL DEFAULT 'none',updated REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS conversations(id TEXT PRIMARY KEY,group_id TEXT UNIQUE REFERENCES users(id),sender TEXT REFERENCES users(id),recipient TEXT REFERENCES users(id),status TEXT NOT NULL DEFAULT 'pending',UNIQUE(sender,recipient));
            CREATE TABLE IF NOT EXISTS messages(id INTEGER PRIMARY KEY,conversation_id TEXT REFERENCES conversations(id),sender TEXT REFERENCES users(id),body TEXT NOT NULL,created REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS message_conversation ON messages(conversation_id,id);
            CREATE TABLE IF NOT EXISTS notifications(id INTEGER PRIMARY KEY,user_id TEXT REFERENCES users(id),kind TEXT NOT NULL,body TEXT NOT NULL,target TEXT NOT NULL,created REAL NOT NULL,seen INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS notifications_user ON notifications(user_id,id);
            CREATE TABLE IF NOT EXISTS mod_transfers(
              id TEXT PRIMARY KEY,guid TEXT NOT NULL REFERENCES mods(guid) ON DELETE CASCADE,
              source TEXT NOT NULL REFERENCES users(id),target TEXT NOT NULL REFERENCES users(id),
              requested_by TEXT NOT NULL REFERENCES users(id),status TEXT NOT NULL DEFAULT 'pending',created REAL NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS pending_mod_transfer ON mod_transfers(guid) WHERE status='pending';
            """);

        // Seeded on creation so enabling webhooks doesn't announce every old release.
        bool webhookTableExists = db.ExecuteScalar("SELECT 1 FROM sqlite_master WHERE name='discord_publications'") != null;
        db.Execute("CREATE TABLE IF NOT EXISTS discord_publications(release_id INTEGER PRIMARY KEY REFERENCES releases(id) ON DELETE CASCADE)");
        if (!webhookTableExists)
            db.Execute("INSERT INTO discord_publications SELECT r.id FROM releases r JOIN mods m ON m.guid=r.guid WHERE m.status='approved'");

        MigrateStore(db);
        MigrateAccounts(db);
        MigrateAdmin(db);
        MigrateExtensions(db);
        MigrateOAuth(db);

        // Servers that already had a root administrator were set up by hand, so they never see the setup wizard.
        db.Execute("CREATE TABLE IF NOT EXISTS server_state(key TEXT PRIMARY KEY, value TEXT NOT NULL)");

        if (options.RootAdminId != "")
            db.Execute("INSERT OR IGNORE INTO server_state(key,value) VALUES('setup_completed',@now)", new { now = Text.NowIso() });
    }

    private static void MigrateExtensions(SqliteConnection db)
    {
        HashSet<string> modColumns = Columns(db, "mods");

        // Both tabs are opt-in, so existing mod pages look the same until their developers turn one on.
        foreach (string column in new[] { "extensions_enabled", "docs_enabled" })
            if (!modColumns.Contains(column))
                db.Execute($"ALTER TABLE mods ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0");

        // An extension is an ordinary mod with a row here naming its parent. state is the parent developers' decision,
        // separate from staff review: staff decide whether the mod is safe, the parent decides whether it's listed.
        db.Execute("""
            CREATE TABLE IF NOT EXISTS mod_extensions(
                guid TEXT PRIMARY KEY REFERENCES mods(guid) ON DELETE CASCADE,
                parent_guid TEXT NOT NULL REFERENCES mods(guid) ON DELETE CASCADE,
                state TEXT NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','approved','unapproved')),
                featured INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, decided_at TEXT, decided_by TEXT,
                CHECK(guid != parent_guid));
            CREATE INDEX IF NOT EXISTS mod_extensions_parent ON mod_extensions(parent_guid,state,featured);
            CREATE TABLE IF NOT EXISTS mod_docs(
                id INTEGER PRIMARY KEY, guid TEXT NOT NULL REFERENCES mods(guid) ON DELETE CASCADE,
                slug TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL DEFAULT '', position INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL, updated_by TEXT, UNIQUE(guid,slug));
            CREATE INDEX IF NOT EXISTS mod_docs_order ON mod_docs(guid,position);
            """);
    }

    private static void MigrateOAuth(SqliteConnection db)
    {
        // OAuth2 apps replace the old desktop PKCE handoff, whose one-time codes lived in native_auth_codes.
        // A grant is one person's consent to one app; deleting it (or the app) takes every code and token with it.
        db.Execute("""
            DROP TABLE IF EXISTS native_auth_codes;
            CREATE TABLE IF NOT EXISTS oauth_apps(
                id TEXT PRIMARY KEY, owner_id TEXT NOT NULL REFERENCES users(id), name TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '', homepage_url TEXT, logo TEXT, redirect_uris TEXT NOT NULL DEFAULT '[]',
                public_client INTEGER NOT NULL DEFAULT 0, verified_at TEXT, verified_by TEXT,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS oauth_apps_owner ON oauth_apps(owner_id);
            CREATE TABLE IF NOT EXISTS oauth_secrets(
                id TEXT PRIMARY KEY, app_id TEXT NOT NULL REFERENCES oauth_apps(id) ON DELETE CASCADE, name TEXT NOT NULL,
                secret_hash TEXT NOT NULL UNIQUE, prefix TEXT NOT NULL, created_at TEXT NOT NULL, last_used_at TEXT);
            CREATE INDEX IF NOT EXISTS oauth_secrets_app ON oauth_secrets(app_id);
            CREATE TABLE IF NOT EXISTS oauth_grants(
                id TEXT PRIMARY KEY, app_id TEXT NOT NULL REFERENCES oauth_apps(id) ON DELETE CASCADE,
                user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE, scope TEXT NOT NULL,
                created_at TEXT NOT NULL, last_used_at TEXT, UNIQUE(app_id,user_id));
            CREATE INDEX IF NOT EXISTS oauth_grants_user ON oauth_grants(user_id);
            CREATE TABLE IF NOT EXISTS oauth_codes(
                code_hash TEXT PRIMARY KEY, app_id TEXT NOT NULL REFERENCES oauth_apps(id) ON DELETE CASCADE,
                user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE, redirect_uri TEXT NOT NULL, scope TEXT NOT NULL,
                challenge TEXT, epoch INTEGER NOT NULL, expires_at REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS oauth_tokens(
                token_hash TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('access','refresh')),
                grant_id TEXT NOT NULL REFERENCES oauth_grants(id) ON DELETE CASCADE, scope TEXT NOT NULL,
                epoch INTEGER NOT NULL, created REAL NOT NULL, expires_at REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS oauth_tokens_grant ON oauth_tokens(grant_id);
            CREATE INDEX IF NOT EXISTS oauth_tokens_expiry ON oauth_tokens(expires_at);
            """);
    }

    private static void MigrateAdmin(SqliteConnection db)
    {
        if (!Columns(db, "users").Contains("deleted_at"))
            db.Execute("ALTER TABLE users ADD COLUMN deleted_at TEXT");

        // NULL for an announcement to everyone; the address for one sent to a single person.
        if (!Columns(db, "email_broadcasts").Contains("target"))
            db.Execute("ALTER TABLE email_broadcasts ADD COLUMN target TEXT");

        HashSet<string> outbox = Columns(db, "email_outbox");

        // sender picks the SMTP account: 'default' is MAIL_FROM, 'support' is the support mailbox. The rest thread replies in mail clients.
        foreach (var (name, definition) in new[]
        {
            ("sender", "TEXT NOT NULL DEFAULT 'default'"), ("message_id", "TEXT"), ("in_reply_to", "TEXT"), ("references_header", "TEXT"),
        })
        {
            if (!outbox.Contains(name))
                db.Execute($"ALTER TABLE email_outbox ADD COLUMN {name} {definition}");
        }

        db.Execute("""
            CREATE TABLE IF NOT EXISTS mod_reviews(
                id INTEGER PRIMARY KEY, guid TEXT NOT NULL, reviewer_id TEXT NOT NULL, decision TEXT NOT NULL CHECK(decision IN ('approved','denied','resubmitted')),
                reason TEXT NOT NULL DEFAULT '', version TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS mod_reviews_guid ON mod_reviews(guid,id);
            CREATE TABLE IF NOT EXISTS support_tickets(
                id INTEGER PRIMARY KEY, reference TEXT NOT NULL UNIQUE, subject TEXT NOT NULL,
                requester_email TEXT NOT NULL, requester_name TEXT, user_id TEXT,
                status TEXT NOT NULL DEFAULT 'open' CHECK(status IN ('open','pending','closed')),
                assignee_id TEXT, unread INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS support_tickets_status ON support_tickets(status,updated_at);
            CREATE INDEX IF NOT EXISTS support_tickets_requester ON support_tickets(requester_email);
            CREATE TABLE IF NOT EXISTS ticket_messages(
                id INTEGER PRIMARY KEY, ticket_id INTEGER NOT NULL REFERENCES support_tickets(id) ON DELETE CASCADE,
                direction TEXT NOT NULL CHECK(direction IN ('in','out','auto','note')), author_id TEXT,
                from_address TEXT NOT NULL, to_address TEXT NOT NULL DEFAULT '', subject TEXT NOT NULL DEFAULT '',
                body TEXT NOT NULL, message_id TEXT, outbox_id INTEGER, created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ticket_messages_ticket ON ticket_messages(ticket_id,id);
            CREATE UNIQUE INDEX IF NOT EXISTS ticket_messages_mid ON ticket_messages(message_id) WHERE message_id IS NOT NULL;
            CREATE TABLE IF NOT EXISTS ticket_attachments(
                id TEXT PRIMARY KEY, message_id INTEGER NOT NULL REFERENCES ticket_messages(id) ON DELETE CASCADE,
                filename TEXT NOT NULL, content_type TEXT NOT NULL, size INTEGER NOT NULL, stored_name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS support_macros(
                id TEXT PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL, author_id TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS support_state(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS email_drafts(
                id TEXT PRIMARY KEY, author_id TEXT NOT NULL, subject TEXT NOT NULL DEFAULT '', body TEXT NOT NULL DEFAULT '',
                audience TEXT NOT NULL DEFAULT 'subscribed', to_address TEXT, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS admin_handoffs(token_hash TEXT PRIMARY KEY, user_id TEXT NOT NULL, epoch INTEGER NOT NULL, expires REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS admin_audit_created ON admin_audit(created_at);
            """);
    }

    private static void MigrateAccounts(SqliteConnection db)
    {
        HashSet<string> userColumns = Columns(db, "users");

        // email_source is "discord" when Discord supplied the address, so a later Discord sign-in may refresh it, or "user" when typed in.
        foreach (var (name, definition) in new[]
        {
            ("email", "TEXT"), ("email_verified", "INTEGER NOT NULL DEFAULT 0"), ("email_source", "TEXT"),
            ("password_hash", "TEXT"), ("session_epoch", "INTEGER NOT NULL DEFAULT 0"),
            ("email_release_notices", "INTEGER NOT NULL DEFAULT 1"), ("email_announcements", "INTEGER NOT NULL DEFAULT 1"),
            ("created_at", "TEXT"),
        })
        {
            if (!userColumns.Contains(name))
                db.Execute($"ALTER TABLE users ADD COLUMN {name} {definition}");
        }

        // Only email accounts sign in with their address, so only they must have a unique one. Discord accounts keep whatever Discord says.
        db.Execute("""
            CREATE UNIQUE INDEX IF NOT EXISTS user_login_email ON users(email) WHERE substr(id,1,3)='mf_';
            CREATE TABLE IF NOT EXISTS email_tokens(
                token_hash TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                purpose TEXT NOT NULL CHECK(purpose IN ('verify','reset','change')), email TEXT NOT NULL,
                created REAL NOT NULL, expires REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS email_tokens_user ON email_tokens(user_id,purpose);
            CREATE TABLE IF NOT EXISTS passkeys(
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE, name TEXT NOT NULL,
                public_key BLOB NOT NULL, algorithm INTEGER NOT NULL, sign_count INTEGER NOT NULL DEFAULT 0,
                transports TEXT NOT NULL DEFAULT '[]', created_at TEXT NOT NULL, last_used_at TEXT);
            CREATE INDEX IF NOT EXISTS passkeys_user ON passkeys(user_id);
            CREATE TABLE IF NOT EXISTS email_broadcasts(
                id TEXT PRIMARY KEY, author_id TEXT NOT NULL REFERENCES users(id), subject TEXT NOT NULL, body TEXT NOT NULL,
                include_opted_out INTEGER NOT NULL DEFAULT 0, recipients INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS email_outbox(
                id INTEGER PRIMARY KEY, user_id TEXT, to_address TEXT NOT NULL, subject TEXT NOT NULL,
                text_body TEXT NOT NULL, html_body TEXT NOT NULL, kind TEXT NOT NULL, priority INTEGER NOT NULL DEFAULT 1,
                broadcast_id TEXT, list_unsubscribe TEXT, status TEXT NOT NULL DEFAULT 'queued' CHECK(status IN ('queued','sent','failed')),
                attempts INTEGER NOT NULL DEFAULT 0, next_attempt REAL NOT NULL, created REAL NOT NULL, sent REAL, error TEXT);
            CREATE INDEX IF NOT EXISTS email_outbox_queue ON email_outbox(status,priority,next_attempt);
            CREATE INDEX IF NOT EXISTS email_outbox_broadcast ON email_outbox(broadcast_id,status);
            """);
    }

    private static void MigrateStore(SqliteConnection db)
    {
        HashSet<string> userColumns = Columns(db, "users");

        foreach (var (name, definition) in new[]
        {
            ("stripe_account_id", "TEXT"), ("stripe_charges_enabled", "INTEGER NOT NULL DEFAULT 0"),
            ("stripe_transfers_enabled", "INTEGER NOT NULL DEFAULT 0"), ("stripe_details_submitted", "INTEGER NOT NULL DEFAULT 0"),
            // Privacy check of the account's public Stripe details: what's wrong (comma-separated, NULL when fine),
            // when it first went wrong, and whether the account still gets a grace period. Accounts that existed
            // before the check get one; accounts MonkeForge pre-filled, or that once passed, don't.
            ("stripe_profile_issues", "TEXT"), ("stripe_profile_flagged_at", "TEXT"), ("stripe_profile_grace", "INTEGER NOT NULL DEFAULT 1"),
        })
        {
            if (!userColumns.Contains(name))
                db.Execute($"ALTER TABLE users ADD COLUMN {name} {definition}");
        }

        HashSet<string> modColumns = Columns(db, "mods");

        if (!modColumns.Contains("price_cents"))
            db.Execute("ALTER TABLE mods ADD COLUMN price_cents INTEGER NOT NULL DEFAULT 0");

        if (!modColumns.Contains("uses_injector"))
            db.Execute("ALTER TABLE mods ADD COLUMN uses_injector INTEGER NOT NULL DEFAULT 0");

        // Payment and entitlement rows deliberately have no foreign keys to mods: financial records must outlive a deleted listing.
        db.Execute("""
            CREATE UNIQUE INDEX IF NOT EXISTS user_stripe_account ON users(stripe_account_id) WHERE stripe_account_id IS NOT NULL;
            CREATE TABLE IF NOT EXISTS payments(
                id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('purchase','creator_donation','platform_donation')),
                buyer_id TEXT REFERENCES users(id), recipient_id TEXT, guid TEXT,
                amount_cents INTEGER NOT NULL, fee_cents INTEGER NOT NULL, currency TEXT NOT NULL,
                stripe_account TEXT, payment_intent TEXT, status TEXT NOT NULL DEFAULT 'open',
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS payments_intent ON payments(payment_intent);
            CREATE INDEX IF NOT EXISTS payments_recipient ON payments(recipient_id,status);
            CREATE INDEX IF NOT EXISTS payments_buyer ON payments(buyer_id,created_at);
            CREATE TABLE IF NOT EXISTS entitlements(
                user_id TEXT NOT NULL REFERENCES users(id), guid TEXT NOT NULL, payment_id TEXT REFERENCES payments(id),
                granted_at TEXT NOT NULL, revoked_at TEXT, PRIMARY KEY(user_id,guid));
            CREATE TABLE IF NOT EXISTS stripe_events(id TEXT PRIMARY KEY, type TEXT NOT NULL, received_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS injector_devices(
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), name TEXT NOT NULL,
                public_key TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL, last_used_at TEXT, revoked_at TEXT);
            CREATE INDEX IF NOT EXISTS injector_devices_user ON injector_devices(user_id);
            CREATE TABLE IF NOT EXISTS injector_challenges(
                nonce_hash TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), expires_at REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS injector_deliveries(
                id INTEGER PRIMARY KEY, user_id TEXT NOT NULL, device_id TEXT NOT NULL, guid TEXT NOT NULL,
                file_id TEXT NOT NULL, version TEXT NOT NULL, ip_hash TEXT NOT NULL, created REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS injector_deliveries_user ON injector_deliveries(user_id,created);
            """);

        MigrateSubscriptions(db);
    }

    private static void MigrateSubscriptions(SqliteConnection db)
    {
        // The payments kind CHECK predates subscriptions, and SQLite can't alter a CHECK, so the table is rebuilt once.
        string? paymentsSql = db.ExecuteScalar<string?>("SELECT sql FROM sqlite_master WHERE type='table' AND name='payments'");

        if (paymentsSql != null && !paymentsSql.Contains("'subscription'"))
        {
            db.Execute("PRAGMA foreign_keys=OFF");

            try
            {
                using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
                db.Execute("""
                    CREATE TABLE payments_new(
                        id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('purchase','subscription','creator_donation','platform_donation')),
                        buyer_id TEXT REFERENCES users(id), recipient_id TEXT, guid TEXT,
                        amount_cents INTEGER NOT NULL, fee_cents INTEGER NOT NULL, currency TEXT NOT NULL,
                        stripe_account TEXT, payment_intent TEXT, status TEXT NOT NULL DEFAULT 'open',
                        created_at TEXT NOT NULL, updated_at TEXT NOT NULL, subscription_id TEXT);
                    INSERT INTO payments_new(id,kind,buyer_id,recipient_id,guid,amount_cents,fee_cents,currency,stripe_account,payment_intent,status,created_at,updated_at)
                        SELECT id,kind,buyer_id,recipient_id,guid,amount_cents,fee_cents,currency,stripe_account,payment_intent,status,created_at,updated_at FROM payments;
                    DROP TABLE payments;
                    ALTER TABLE payments_new RENAME TO payments;
                    CREATE INDEX IF NOT EXISTS payments_intent ON payments(payment_intent);
                    CREATE INDEX IF NOT EXISTS payments_recipient ON payments(recipient_id,status);
                    CREATE INDEX IF NOT EXISTS payments_buyer ON payments(buyer_id,created_at);
                    """, transaction: transaction);
                transaction.Commit();
            }
            finally
            {
                db.Execute("PRAGMA foreign_keys=ON");
            }
        }

        // A tier with a guid belongs to that mod. A tier without one is a creator membership that unlocks the mods listed in tier_mods.
        db.Execute("""
            CREATE INDEX IF NOT EXISTS payments_subscription ON payments(subscription_id);
            CREATE TABLE IF NOT EXISTS subscription_tiers(
                id TEXT PRIMARY KEY, owner_id TEXT NOT NULL REFERENCES users(id), guid TEXT,
                name TEXT NOT NULL, benefits TEXT NOT NULL DEFAULT '[]', price_cents INTEGER NOT NULL,
                position INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, archived_at TEXT);
            CREATE INDEX IF NOT EXISTS tiers_owner ON subscription_tiers(owner_id);
            CREATE INDEX IF NOT EXISTS tiers_mod ON subscription_tiers(guid);
            CREATE TABLE IF NOT EXISTS tier_mods(
                tier_id TEXT NOT NULL REFERENCES subscription_tiers(id), guid TEXT NOT NULL, PRIMARY KEY(tier_id,guid));
            CREATE INDEX IF NOT EXISTS tier_mods_guid ON tier_mods(guid);
            CREATE TABLE IF NOT EXISTS subscriptions(
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), tier_id TEXT NOT NULL REFERENCES subscription_tiers(id),
                owner_id TEXT NOT NULL, stripe_account TEXT NOT NULL, checkout_session TEXT UNIQUE, stripe_subscription TEXT UNIQUE,
                price_cents INTEGER NOT NULL, currency TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending',
                cancel_at_period_end INTEGER NOT NULL DEFAULT 0, current_period_end TEXT,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS subscriptions_user ON subscriptions(user_id,status);
            CREATE INDEX IF NOT EXISTS subscriptions_tier ON subscriptions(tier_id,status);
            CREATE TABLE IF NOT EXISTS staff_disputes(
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), payment_id TEXT, subscription_id TEXT,
                seller_id TEXT, guid TEXT, reason TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'open'
                CHECK(status IN ('open','accepted','rejected')), staff_id TEXT, staff_note TEXT,
                refunded_cents INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, resolved_at TEXT);
            CREATE UNIQUE INDEX IF NOT EXISTS open_payment_dispute ON staff_disputes(payment_id) WHERE status='open' AND payment_id IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS open_subscription_dispute ON staff_disputes(subscription_id) WHERE status='open' AND subscription_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS disputes_status ON staff_disputes(status,created_at);
            """);

        // Billing pauses while the seller's public Stripe details would expose them, so renewal receipts can't.
        if (!Columns(db, "subscriptions").Contains("collection_paused"))
            db.Execute("ALTER TABLE subscriptions ADD COLUMN collection_paused INTEGER NOT NULL DEFAULT 0");
    }

    private static void MigrateChannels(SqliteConnection db)
    {
        db.Execute("""
            CREATE TABLE IF NOT EXISTS release_channels(
                guid TEXT NOT NULL REFERENCES mods(guid), name TEXT NOT NULL,
                notes TEXT NOT NULL DEFAULT '', public INTEGER NOT NULL DEFAULT 1,
                enabled INTEGER NOT NULL DEFAULT 1, members TEXT NOT NULL DEFAULT '[]',
                PRIMARY KEY(guid,name));
            CREATE TABLE IF NOT EXISTS channel_share_codes(
                code TEXT PRIMARY KEY, guid TEXT NOT NULL, name TEXT NOT NULL,
                UNIQUE(guid,name), FOREIGN KEY(guid,name) REFERENCES release_channels(guid,name));
            """);
        if (!Columns(db, "releases").Contains("channel"))
        {
            db.Execute("PRAGMA foreign_keys=OFF");
            try
            {
                using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
                db.Execute("""
                    CREATE TABLE releases_new(id INTEGER PRIMARY KEY, guid TEXT NOT NULL REFERENCES mods(guid),
                        version TEXT NOT NULL, released_at TEXT NOT NULL, notes TEXT NOT NULL DEFAULT '',
                        channel TEXT NOT NULL DEFAULT 'stable', UNIQUE(guid,channel,version));
                    INSERT INTO releases_new(id,guid,version,released_at,notes) SELECT id,guid,version,released_at,notes FROM releases;
                    DROP TABLE releases;
                    ALTER TABLE releases_new RENAME TO releases;
                    """, transaction: transaction);
                transaction.Commit();
            }
            finally
            {
                db.Execute("PRAGMA foreign_keys=ON");
            }
        }
        db.Execute("INSERT OR IGNORE INTO release_channels(guid,name) SELECT guid,'stable' FROM mods");
    }

    private static HashSet<string> Columns(SqliteConnection db, string table)
    {
        return db.Query<string>($"SELECT name FROM pragma_table_info('{table}')").ToHashSet();
    }
}
