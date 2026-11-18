using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using OpenForge.Infrastructure;
using SQLitePCL;

namespace OpenForge.Services;

public sealed class TableInfo
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "table";
    public string Sql { get; init; } = "";
    public long Rows { get; init; }
    public bool Editable { get; init; }
    public List<ColumnInfo> Columns { get; init; } = [];
}

public sealed class ColumnInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool NotNull { get; set; }
    public string? Default { get; set; }
    public int Pk { get; set; }
}

public sealed class StatementResult
{
    public string Sql { get; init; } = "";
    public bool ReadOnly { get; init; }
    public List<string> Columns { get; } = [];
    public List<List<object?>> Rows { get; } = [];
    public bool Truncated { get; set; }
    public long Changes { get; set; }
    public double Milliseconds { get; set; }
    public string? Error { get; set; }
}

// The raw database tools: browsing and editing table rows by rowid, and running SQL typed by the root admin.
// Values travel as JSON: null, numbers, strings, and {"$blob": "<hex>"} for binary data.
public sealed class DatabaseConsole
{
    public const int MaxRows = 500;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly string[] Blocked = ["ATTACH", "DETACH", "VACUUM"];
    private static readonly string[] TransactionWords = ["BEGIN", "COMMIT", "END", "ROLLBACK", "SAVEPOINT", "RELEASE"];

    private readonly ForgeOptions _options;

    public DatabaseConsole(ForgeOptions options)
    {
        _options = options;
    }

    // A connection of its own, never pooled, so settings like query_only can't leak into the site's connections.
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _options.RegistryPath, Pooling = false, DefaultTimeout = 10 }.ToString());
        connection.Open();
        connection.Execute("PRAGMA foreign_keys=ON");
        return connection;
    }

    public static string Quote(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public List<TableInfo> Tables()
    {
        using SqliteConnection connection = Open();
        var tables = new List<TableInfo>();

        foreach (var (name, type, sql) in connection.Query<(string Name, string Type, string? Sql)>("SELECT name,type,sql FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY type,name"))
        {
            List<ColumnInfo> columns = connection.Query<ColumnInfo>($"SELECT name,type,\"notnull\" AS \"not_null\",dflt_value AS \"default\",pk FROM pragma_table_info({Literal(name)})").ToList();
            bool rowid = type == "table" && !(sql ?? "").Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase);
            tables.Add(new TableInfo
            {
                Name = name, Type = type, Sql = sql ?? "", Columns = columns, Editable = rowid,
                Rows = connection.ExecuteScalar<long>($"SELECT COUNT(*) FROM {Quote(name)}"),
            });
        }

        return tables;
    }

    private static string Literal(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static TableInfo Find(List<TableInfo> tables, string name)
    {
        return tables.FirstOrDefault(t => t.Name == name) ?? throw ApiException.NotFound("No such table.");
    }

    public static object? FromReader(SqliteDataReader reader, int i)
    {
        if (reader.IsDBNull(i))
            return null;

        return reader.GetFieldType(i) == typeof(byte[]) ? Blob((byte[])reader.GetValue(i)) : reader.GetValue(i);
    }

    private static object Blob(ReadOnlySpan<byte> bytes)
    {
        // Large blobs are cut short for display; editing them whole isn't something a form can do well anyway.
        int shown = Math.Min(bytes.Length, 2048);
        return new Dictionary<string, object> { ["$blob"] = Convert.ToHexStringLower(bytes[..shown]), ["size"] = bytes.Length, ["truncated"] = shown < bytes.Length };
    }

    public static object? ToDb(JsonNode? node)
    {
        if (node == null)
            return null;

        if (node is JsonObject obj && obj["$blob"].IsString(out string hex))
        {
            try
            {
                return Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                throw ApiException.BadRequest("Blob values must be hexadecimal.");
            }
        }

        if (node.IsString(out string text))
            return text;

        if (node.IsBool(out bool flag))
            return flag ? 1L : 0L;

        if (node.IsInt(out long integer))
            return integer;

        if (node is JsonValue value && value.TryGetValue(out double real))
            return real;

        throw ApiException.BadRequest("Values must be null, a number, text or {\"$blob\": \"hex\"}.");
    }

    public object Rows(string table, int page, int limit, string? sort, bool descending, string? filterColumn, string? filter)
    {
        List<TableInfo> tables = Tables();
        TableInfo info = Find(tables, table);
        using SqliteConnection connection = Open();
        var parameters = new DynamicParameters();
        string where = "";

        if (!string.IsNullOrEmpty(filter))
        {
            IEnumerable<string> columns = filterColumn != null && filterColumn != "" ? [Find(tables, table).Columns.FirstOrDefault(c => c.Name == filterColumn)?.Name ?? throw ApiException.BadRequest("No such column.")] : info.Columns.Select(c => c.Name);
            where = " WHERE " + string.Join(" OR ", columns.Select(c => $"CAST({Quote(c)} AS TEXT) LIKE @filter ESCAPE '\\'"));
            parameters.Add("filter", "%" + Text.LikeEscape(filter) + "%");
        }

        string order = "";

        if (!string.IsNullOrEmpty(sort))
        {
            if (sort != "rowid" && info.Columns.All(c => c.Name != sort))
                throw ApiException.BadRequest("No such column.");

            order = $" ORDER BY {(sort == "rowid" ? "rowid" : Quote(sort))} {(descending ? "DESC" : "ASC")}";
        }
        else if (info.Editable)
        {
            order = " ORDER BY rowid";
        }

        long total = connection.ExecuteScalar<long>($"SELECT COUNT(*) FROM {Quote(table)}{where}", parameters);
        parameters.Add("limit", limit);
        parameters.Add("offset", (page - 1) * limit);

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {(info.Editable ? "rowid AS \"__rowid\"," : "")}* FROM {Quote(table)}{where}{order} LIMIT @limit OFFSET @offset";

        foreach (string name in parameters.ParameterNames)
            command.Parameters.AddWithValue("@" + name, parameters.Get<object>(name));

        var rows = new List<object>();
        using SqliteDataReader reader = command.ExecuteReader();
        int offset = info.Editable ? 1 : 0;

        while (reader.Read())
        {
            var values = new List<object?>();

            for (int i = offset; i < reader.FieldCount; i++)
                values.Add(FromReader(reader, i));

            rows.Add(new { rowid = info.Editable ? reader.GetInt64(0) : (long?)null, values });
        }

        return new { table = info.Name, editable = info.Editable, columns = info.Columns, rows, total, page, limit };
    }

    private TableInfo Writable(string table)
    {
        TableInfo info = Find(Tables(), table);

        if (!info.Editable)
            throw ApiException.BadRequest("Views and WITHOUT ROWID tables can't be edited here. Use the SQL console.");

        return info;
    }

    private static string Fail(SqliteException ex)
    {
        return ex.SqliteErrorCode == 19 ? "A constraint stopped this change: " + ex.Message.Split('\'').ElementAtOrDefault(1) ?? ex.Message : ex.Message;
    }

    public void Update(string table, long rowid, JsonObject values)
    {
        TableInfo info = Writable(table);

        if (values.Count == 0)
            throw ApiException.BadRequest("Send at least one column.");

        using SqliteConnection connection = Open();
        using var command = connection.CreateCommand();
        var sets = new List<string>();
        int n = 0;

        foreach ((string column, JsonNode? node) in values)
        {
            if (info.Columns.All(c => c.Name != column))
                throw ApiException.BadRequest($"No column {column}.");

            sets.Add($"{Quote(column)}=$p{n}");
            command.Parameters.AddWithValue("$p" + n++, ToDb(node) ?? DBNull.Value);
        }

        command.CommandText = $"UPDATE {Quote(table)} SET {string.Join(',', sets)} WHERE rowid=$rowid";
        command.Parameters.AddWithValue("$rowid", rowid);

        try
        {
            if (command.ExecuteNonQuery() == 0)
                throw ApiException.NotFound("That row no longer exists.");
        }
        catch (SqliteException ex)
        {
            throw ApiException.BadRequest(Fail(ex));
        }
    }

    public long Insert(string table, JsonObject values)
    {
        TableInfo info = Writable(table);
        using SqliteConnection connection = Open();
        using var command = connection.CreateCommand();
        var columns = new List<string>();
        int n = 0;

        foreach ((string column, JsonNode? node) in values)
        {
            if (info.Columns.All(c => c.Name != column))
                throw ApiException.BadRequest($"No column {column}.");

            columns.Add(Quote(column));
            command.Parameters.AddWithValue("$p" + n++, ToDb(node) ?? DBNull.Value);
        }

        command.CommandText = columns.Count == 0
            ? $"INSERT INTO {Quote(table)} DEFAULT VALUES; SELECT last_insert_rowid()"
            : $"INSERT INTO {Quote(table)}({string.Join(',', columns)}) VALUES({string.Join(',', Enumerable.Range(0, n).Select(i => "$p" + i))}); SELECT last_insert_rowid()";

        try
        {
            return (long)command.ExecuteScalar()!;
        }
        catch (SqliteException ex)
        {
            throw ApiException.BadRequest(Fail(ex));
        }
    }

    public void Delete(string table, long rowid)
    {
        Writable(table);
        using SqliteConnection connection = Open();

        try
        {
            if (connection.Execute($"DELETE FROM {Quote(table)} WHERE rowid=@rowid", new { rowid }) == 0)
                throw ApiException.NotFound("That row no longer exists.");
        }
        catch (SqliteException ex)
        {
            throw ApiException.BadRequest(Fail(ex));
        }
    }

    // The first keyword of a statement, skipping whitespace and comments.
    private static string Keyword(string sql)
    {
        int i = 0;

        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i]))
                i++;
            else if (sql.AsSpan(i).StartsWith("--"))
                i = sql.IndexOf('\n', i) is int end && end >= 0 ? end + 1 : sql.Length;
            else if (sql.AsSpan(i).StartsWith("/*"))
                i = sql.IndexOf("*/", i + 2, StringComparison.Ordinal) is int end && end >= 0 ? end + 2 : sql.Length;
            else
                break;
        }

        int start = i;

        while (i < sql.Length && char.IsLetter(sql[i]))
            i++;

        return sql[start..i].ToUpperInvariant();
    }

    // SQLite parses the text one statement at a time (prepare_v2 hands back the unparsed tail), which is how
    // statements are split and how each one is known to read or write before it runs. Read-only runs also turn on
    // query_only, so nothing can write even by surprise. Writes run in one transaction: any error undoes them all.
    public (List<StatementResult> Results, bool Committed, string? Error) Run(string sql, bool allowWrites, bool rollback)
    {
        using SqliteConnection connection = Open();
        sqlite3 handle = connection.Handle!;
        var results = new List<StatementResult>();
        bool transaction = false;
        string? error = null;

        connection.Execute(allowWrites ? "PRAGMA query_only=OFF" : "PRAGMA query_only=ON");
        using var timer = new Timer(_ => raw.sqlite3_interrupt(handle), null, Timeout, System.Threading.Timeout.InfiniteTimeSpan);

        try
        {
            string remaining = sql;

            while (remaining.Trim() != "")
            {
                int rc = raw.sqlite3_prepare_v2(handle, remaining, out sqlite3_stmt stmt, out string tail);
                string text = remaining[..(remaining.Length - (tail?.Length ?? 0))].Trim();
                remaining = tail ?? "";

                using (stmt)
                {
                    if (rc != raw.SQLITE_OK)
                    {
                        error = raw.sqlite3_errmsg(handle).utf8_to_string();
                        results.Add(new StatementResult { Sql = text, ReadOnly = true, Error = error });
                        break;
                    }

                    // A trailing comment or a lone semicolon prepares to nothing.
                    if (stmt == null || stmt.IsInvalid)
                        continue;

                    string keyword = Keyword(text);
                    bool readOnly = raw.sqlite3_stmt_readonly(stmt) != 0;
                    var result = new StatementResult { Sql = text, ReadOnly = readOnly };
                    results.Add(result);

                    if (Blocked.Contains(keyword))
                    {
                        error = result.Error = $"{keyword} isn't allowed here: it can read or write files outside the database.";
                        break;
                    }

                    if (TransactionWords.Contains(keyword))
                    {
                        error = result.Error = "Transactions are handled for you: writes run in one transaction that commits at the end, or rolls back on any error.";
                        break;
                    }

                    if (!readOnly && !allowWrites)
                    {
                        error = result.Error = "This statement changes the database. Turn on “Allow writes” to run it.";
                        break;
                    }

                    if (!readOnly && !transaction)
                    {
                        connection.Execute("BEGIN IMMEDIATE");
                        transaction = true;
                    }

                    var watch = Stopwatch.StartNew();
                    long before = raw.sqlite3_total_changes(handle);
                    int count = raw.sqlite3_column_count(stmt);

                    for (int i = 0; i < count; i++)
                        result.Columns.Add(raw.sqlite3_column_name(stmt, i).utf8_to_string());

                    while (true)
                    {
                        rc = raw.sqlite3_step(stmt);

                        if (rc == raw.SQLITE_DONE)
                            break;

                        if (rc != raw.SQLITE_ROW)
                        {
                            error = result.Error = rc == raw.SQLITE_INTERRUPT ? $"Stopped after {Timeout.TotalSeconds:0} seconds." : raw.sqlite3_errmsg(handle).utf8_to_string();
                            break;
                        }

                        if (result.Rows.Count >= MaxRows)
                        {
                            result.Truncated = true;

                            // A read can stop early; a write keeps stepping so it does all of its work.
                            if (readOnly)
                                break;

                            continue;
                        }

                        var row = new List<object?>(count);

                        for (int i = 0; i < count; i++)
                        {
                            row.Add(raw.sqlite3_column_type(stmt, i) switch
                            {
                                raw.SQLITE_INTEGER => raw.sqlite3_column_int64(stmt, i),
                                raw.SQLITE_FLOAT => raw.sqlite3_column_double(stmt, i),
                                raw.SQLITE_TEXT => raw.sqlite3_column_text(stmt, i).utf8_to_string(),
                                raw.SQLITE_BLOB => Blob(raw.sqlite3_column_blob(stmt, i)),
                                _ => null,
                            });
                        }

                        result.Rows.Add(row);
                    }

                    result.Milliseconds = Math.Round(watch.Elapsed.TotalMilliseconds, 2);

                    if (!readOnly && result.Error == null)
                        result.Changes = raw.sqlite3_total_changes(handle) - before;

                    if (result.Error != null)
                        break;
                }
            }

            if (transaction)
                connection.Execute(error == null && !rollback ? "COMMIT" : "ROLLBACK");
        }
        catch
        {
            if (transaction && raw.sqlite3_get_autocommit(handle) == 0)
                connection.Execute("ROLLBACK");

            throw;
        }

        return (results, transaction && error == null && !rollback, error);
    }
}
