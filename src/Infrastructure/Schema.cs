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
            ("custom_slug", "TEXT"), ("accent_color", "TEXT NOT NULL DEFAULT '#dc5c3b'"),
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
        foreach (string column in new[] { "banner", "choice_category", "custom_slug" })
            if (!modColumns.Contains(column))
                db.Execute($"ALTER TABLE mods ADD COLUMN {column} TEXT");

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
            CREATE TABLE IF NOT EXISTS native_auth_codes(
                code_hash TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id),
                redirect_uri TEXT NOT NULL, challenge TEXT NOT NULL, expires_at REAL NOT NULL);
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
