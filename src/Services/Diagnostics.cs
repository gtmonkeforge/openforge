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
    private const int Kept = 100;
    private const int MaxTrace = 16000;

    private readonly ForgeOptions _options;
    private readonly ILogger<Diagnostics> _logger;
    private readonly IHttpContextAccessor _http;

    public Diagnostics(ForgeOptions options, ILogger<Diagnostics> logger, IHttpContextAccessor http)
    {
        _options = options;
        _logger = logger;
        _http = http;
    }

    public string Record(HttpContext context, Exception exception)
    {
        string reference = Text.Hex(8);
        _logger.LogError(exception, "Request error {Reference}: {Method} {Path}", reference, context.Request.Method, context.Request.Path);
        Save(reference, context.Request.Method, context.Request.Path.ToString(), exception.GetType().Name, exception.Message, exception.ToString());
        return reference;
    }

    // Saves a handled failure that staff should see, such as a payment provider rejecting a request, to the admin Errors list.
    // The caller has already logged it. Source is a short label shown as the error type; detail is free text shown below the message.
    // Never pass secrets (API keys, webhook URLs) in message or detail: admins can read them in the panel.
    public string Report(string source, string message, string detail = "", Exception? exception = null)
    {
        string reference = Text.Hex(8);
        HttpRequest? request = _http.HttpContext?.Request;

        if (exception != null)
            detail = detail == "" ? exception.ToString() : detail + "\n\n" + exception;

        Save(reference, request?.Method ?? "", request?.Path.ToString() ?? "(background task)", source, message, detail);
        return reference;
    }

    private void Save(string reference, string method, string path, string type, string message, string trace)
    {
        try
        {
            using SqliteConnection db = Connect();

            db.Execute("INSERT INTO server_errors VALUES(@reference,@now,@method,@path,@type,@message,@trace)", new
            {
                reference,
                now = Text.NowIso(),
                method,
                path = path.Length > 1024 ? path[..1024] : path,
                type = type.Length > 200 ? type[..200] : type,
                message = message.Length > 1000 ? message[..1000] : message,
                trace = trace.Length > MaxTrace ? trace[^MaxTrace..] : trace,
            });
            db.Execute($"DELETE FROM server_errors WHERE rowid NOT IN (SELECT rowid FROM server_errors ORDER BY rowid DESC LIMIT {Kept})");
        }
        catch (SqliteException)
        {
            _logger.LogError("Could not persist error reference {Reference}", reference);
        }
    }

    public List<ServerErrorRow> Recent()
    {
        using SqliteConnection db = Connect();
        return db.Query<ServerErrorRow>($"SELECT * FROM server_errors ORDER BY rowid DESC LIMIT {Kept}").ToList();
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
