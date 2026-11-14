using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

public sealed class ServerErrorRow
{
    public string Id { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string ExceptionType { get; set; } = "";
    public string Message { get; set; } = "";
    public string Traceback { get; set; } = "";
}

public sealed class Diagnostics
{
    private readonly ForgeOptions _options;
    private readonly ILogger<Diagnostics> _logger;

    public Diagnostics(ForgeOptions options, ILogger<Diagnostics> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string Record(HttpContext context, Exception exception)
    {
        string reference = Text.Hex(8);
        string trace = exception.ToString();

        if (trace.Length > 16000)
            trace = trace[^16000..];

        _logger.LogError(exception, "Request error {Reference}: {Method} {Path}", reference, context.Request.Method, context.Request.Path);

        try
        {
            using SqliteConnection db = Connect();
            string path = context.Request.Path.ToString();
            string message = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;

            db.Execute("INSERT INTO server_errors VALUES(@reference,@now,@method,@path,@type,@message,@trace)", new
            {
                reference,
                now = Text.NowIso(),
                method = context.Request.Method,
                path = path.Length > 1024 ? path[..1024] : path,
                type = exception.GetType().Name,
                message,
                trace,
            });
            db.Execute("DELETE FROM server_errors WHERE rowid NOT IN (SELECT rowid FROM server_errors ORDER BY rowid DESC LIMIT 50)");
        }
        catch (SqliteException)
        {
            _logger.LogError("Could not persist error reference {Reference}", reference);
        }

        return reference;
    }

    public List<ServerErrorRow> Recent()
    {
        using SqliteConnection db = Connect();
        return db.Query<ServerErrorRow>("SELECT * FROM server_errors ORDER BY rowid DESC LIMIT 50").ToList();
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _options.DiagnosticsPath, DefaultTimeout = 1 }.ToString());
        connection.Open();
        connection.Execute("""
            CREATE TABLE IF NOT EXISTS server_errors(
                id TEXT PRIMARY KEY, created_at TEXT NOT NULL, method TEXT NOT NULL,
                path TEXT NOT NULL, exception_type TEXT NOT NULL, message TEXT NOT NULL,
                traceback TEXT NOT NULL)
            """);

        return connection;
    }
}
