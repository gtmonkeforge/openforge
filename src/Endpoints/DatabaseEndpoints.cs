using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using OpenForge.Infrastructure;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// Raw access to the registry database. Root only, and every change is in the audit log.
public static class DatabaseEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/admin/db/tables", (ForgeContext context, DatabaseConsole console) =>
        {
            AdminEndpoints.RequireAdmin(context, true);
            return Api.Json(new { items = console.Tables(), path = "registry.sqlite3" });
        });

        app.MapGet("/api/v1/admin/db/tables/{table}/rows", (string table, HttpRequest request, ForgeContext context, DatabaseConsole console) =>
        {
            AdminEndpoints.RequireAdmin(context, true);
            (int page, int limit) = ModEndpoints.Paging(request, 50);
            string? Q(string key) => request.Query[key].FirstOrDefault();
            return Api.Json(console.Rows(table, page, limit, Q("sort"), Q("dir") == "desc", Q("column"), Q("filter")));
        });

        app.MapPatch("/api/v1/admin/db/tables/{table}/rows/{rowid:long}", async (string table, long rowid, HttpRequest request, ForgeContext context, DatabaseConsole console, Db db) =>
        {
            CurrentUser actor = AdminEndpoints.RequireAdmin(context, true);
            JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send the columns to change.");
            console.Update(table, rowid, payload["values"] as JsonObject ?? throw ApiException.BadRequest("Send values."));
            AdminPortalEndpoints.Log(db, actor, "db.update", $"{table}#{rowid} {string.Join(',', ((JsonObject)payload["values"]!).Select(p => p.Key))}");
            return Api.Ok();
        });

        app.MapPost("/api/v1/admin/db/tables/{table}/rows", async (string table, HttpRequest request, ForgeContext context, DatabaseConsole console, Db db) =>
        {
            CurrentUser actor = AdminEndpoints.RequireAdmin(context, true);
            JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send the new row.");
            long rowid = console.Insert(table, payload["values"] as JsonObject ?? new JsonObject());
            AdminPortalEndpoints.Log(db, actor, "db.insert", $"{table}#{rowid}");
            return Api.Json(new { rowid }, 201);
        });

        app.MapDelete("/api/v1/admin/db/tables/{table}/rows/{rowid:long}", (string table, long rowid, ForgeContext context, DatabaseConsole console, Db db) =>
        {
            CurrentUser actor = AdminEndpoints.RequireAdmin(context, true);
            console.Delete(table, rowid);
            AdminPortalEndpoints.Log(db, actor, "db.delete", $"{table}#{rowid}");
            return Api.Ok();
        });

        app.MapPost("/api/v1/admin/db/sql", async (HttpRequest request, ForgeContext context, DatabaseConsole console, Db db) =>
        {
            CurrentUser actor = AdminEndpoints.RequireAdmin(context, true);
            JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send the SQL to run.");

            if (!payload["sql"].IsString(out string sql) || sql.Trim() == "" || sql.Length > 100_000)
                throw ApiException.BadRequest("Write some SQL, up to 100,000 characters.");

            bool writes = payload["allow_writes"].IsBool(out bool w) && w;
            bool rollback = payload["rollback"].IsBool(out bool r) && r;
            var (results, committed, error) = console.Run(sql, writes, rollback);

            if (results.Any(s => !s.ReadOnly && s.Error == null))
                AdminPortalEndpoints.Log(db, actor, committed ? "db.sql" : "db.sql_rolled_back", sql.Length > 300 ? sql[..300] + "…" : sql);

            return Api.Json(new
            {
                committed,
                error,
                statements = results.Select(s => new
                {
                    sql = s.Sql, read_only = s.ReadOnly, columns = s.Columns, rows = s.Rows, truncated = s.Truncated,
                    changes = s.Changes, ms = s.Milliseconds, error = s.Error,
                }).ToList(),
            });
        });
    }
}
