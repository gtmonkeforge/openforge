using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// Markdown pages a mod's developers write for its Docs tab. Pages are flat and ordered by hand.
public static partial class ModDocsEndpoints
{
    public const int MaxPages = 50;
    public const int MaxBody = 100_000;
    public const int MaxTitle = 120;
    private const string PageNotFound = "This page could not be found.";

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,63}\z")]
    private static partial Regex Slug();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlugChars();

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mods/{value}/docs", Index);
        app.MapPost("/api/v1/mods/{value}/docs", CreateAsync);
        app.MapPut("/api/v1/mods/{value}/docs", ReorderAsync);
        app.MapGet("/api/v1/mods/{value}/docs/{slug}", Page);
        app.MapPatch("/api/v1/mods/{value}/docs/{slug}", EditAsync);
        app.MapDelete("/api/v1/mods/{value}/docs/{slug}", Delete);
    }

    // Readers see the tab's pages only while it's switched on; developers can prepare pages before turning it on.
    private static ModRow Readable(string value, Access access, ForgeContext context, out bool manager)
    {
        ModRow? row = access.ResolveMod(value);

        if (!access.CanViewMod(row))
            throw ApiException.NotFound("This mod could not be found.");

        manager = access.CanManage(context.User, row!);

        if (!row!.HasDocs && !manager)
            throw ApiException.NotFound("This mod has no documentation.");

        return row;
    }

    private static ModRow Writable(string value, Access access, ForgeContext context)
    {
        CurrentUser user = context.RequireUser("Sign in to edit documentation.");
        ModRow? row = access.ResolveMod(value);

        if (row == null || row.Status == "deleted")
            throw ApiException.NotFound("Mod not found.");

        if (!access.CanManage(user, row))
            throw ApiException.Forbidden("Only this mod's developers can edit its documentation.");

        return row;
    }

    public static List<ModDocSummaryDto> Pages(Db db, string guid)
    {
        return db.Query<ModDocRow>("SELECT id,slug,title,position,updated_at FROM mod_docs WHERE guid=@guid ORDER BY position,id", new { guid })
            .Select(d => new ModDocSummaryDto { Slug = d.Slug, Title = d.Title, Position = d.Position, UpdatedAt = d.UpdatedAt })
            .ToList();
    }

    private static ModDocDto Present(ModDocRow row, MarkdownRenderer markdown)
    {
        return new ModDocDto
        {
            Slug = row.Slug,
            Title = row.Title,
            Body = row.Body,
            Html = markdown.Render(row.Body),
            Position = row.Position,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };
    }

    private static IResult Index(string value, Access access, ForgeContext context, Db db)
    {
        ModRow row = Readable(value, access, context, out bool manager);
        return Api.Json(new { enabled = row.HasDocs, can_edit = manager, pages = Pages(db, row.Guid) });
    }

    private static IResult Page(string value, string slug, Access access, ForgeContext context, Db db, MarkdownRenderer markdown)
    {
        ModRow row = Readable(value, access, context, out _);
        ModDocRow page = db.One<ModDocRow>("SELECT * FROM mod_docs WHERE guid=@guid AND slug=@slug", new { guid = row.Guid, slug })
            ?? throw ApiException.NotFound(PageNotFound);

        return Api.Json(Present(page, markdown));
    }

    private static string ReadTitle(JsonNode? node)
    {
        if (!node.IsString(out string title) || Text.Len(title.Trim()) is < 1 or > MaxTitle)
            throw ApiException.BadRequest($"Page titles must be 1–{MaxTitle} characters.");

        return title.Trim();
    }

    private static string ReadBody(JsonNode? node)
    {
        if (!node.IsString(out string body) || Text.Len(body) > MaxBody)
            throw ApiException.BadRequest($"Page content must be Markdown text of up to {MaxBody:N0} characters.");

        return body;
    }

    private static string ReadSlug(JsonNode? node)
    {
        if (!node.IsString(out string slug) || !Slug().IsMatch(slug))
            throw ApiException.BadRequest("Page addresses use 1–64 lowercase letters, digits or hyphens, starting with a letter or digit.");

        return slug;
    }

    // "Getting Started!" becomes "getting-started"; titles with no Latin letters or digits fall back to "page".
    private static string SlugFrom(string title)
    {
        string ascii = new string(title.Normalize(NormalizationForm.FormKD).Where(c => c < 128).ToArray()).ToLowerInvariant();
        string slug = NonSlugChars().Replace(ascii, "-").Trim('-');

        if (slug.Length > 64)
            slug = slug[..64].TrimEnd('-');

        return slug == "" ? "page" : slug;
    }

    private static async Task<IResult> CreateAsync(string value, HttpRequest request, Access access, ForgeContext context, Db db, MarkdownRenderer markdown)
    {
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send title, body and an optional slug as JSON.");

        if (!payload.HasOnly("title", "slug", "body") || !payload.ContainsKey("title"))
            throw ApiException.BadRequest("Send title, body and an optional slug as JSON.");

        string title = ReadTitle(payload["title"]);
        string body = payload.ContainsKey("body") ? ReadBody(payload["body"]) : "";
        bool chosen = payload.ContainsKey("slug") && payload["slug"] is not null && !(payload["slug"].IsString(out string given) && given == "");

        db.BeginImmediate();
        ModRow row = Writable(value, access, context);

        if (db.Scalar<long>("SELECT COUNT(*) FROM mod_docs WHERE guid=@guid", new { guid = row.Guid }) >= MaxPages)
            throw ApiException.Conflict($"A mod can have up to {MaxPages} documentation pages.");

        string slug = chosen ? ReadSlug(payload["slug"]) : SlugFrom(title);

        // A slug made from the title gets a number when taken; one the developer typed is theirs to fix.
        if (!chosen)
        {
            string stem = slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;

            for (int n = 2; db.Exists("SELECT 1 FROM mod_docs WHERE guid=@guid AND slug=@slug", new { guid = row.Guid, slug }); n++)
                slug = stem + "-" + n;
        }

        string now = Text.NowIso();

        try
        {
            db.Exec("""
                INSERT INTO mod_docs(guid,slug,title,body,position,created_at,updated_at,updated_by)
                VALUES(@guid,@slug,@title,@body,(SELECT COALESCE(MAX(position),0)+1 FROM mod_docs WHERE guid=@guid),@now,@now,@by)
                """, new { guid = row.Guid, slug, title, body, now, by = context.User!.Id });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw ApiException.Conflict("A page with this address already exists.");
        }

        db.Commit();

        ModDocRow created = db.One<ModDocRow>("SELECT * FROM mod_docs WHERE guid=@guid AND slug=@slug", new { guid = row.Guid, slug })!;
        return Api.Json(Present(created, markdown), 201);
    }

    private static async Task<IResult> EditAsync(string value, string slug, HttpRequest request, Access access, ForgeContext context, Db db, MarkdownRenderer markdown)
    {
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send title, slug and/or body as JSON.");

        if (payload.Count == 0 || !payload.HasOnly("title", "slug", "body"))
            throw ApiException.BadRequest("Send title, slug and/or body as JSON.");

        string? title = payload.ContainsKey("title") ? ReadTitle(payload["title"]) : null;
        string? body = payload.ContainsKey("body") ? ReadBody(payload["body"]) : null;
        string? renamed = payload.ContainsKey("slug") ? ReadSlug(payload["slug"]) : null;

        db.BeginImmediate();
        ModRow row = Writable(value, access, context);
        ModDocRow page = db.One<ModDocRow>("SELECT * FROM mod_docs WHERE guid=@guid AND slug=@slug", new { guid = row.Guid, slug })
            ?? throw ApiException.NotFound(PageNotFound);

        try
        {
            db.Exec("UPDATE mod_docs SET title=COALESCE(@title,title),body=COALESCE(@body,body),slug=COALESCE(@renamed,slug),updated_at=@now,updated_by=@by WHERE id=@id",
                new { title, body, renamed, now = Text.NowIso(), by = context.User!.Id, id = page.Id });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw ApiException.Conflict("A page with this address already exists.");
        }

        db.Commit();
        return Api.Json(Present(db.One<ModDocRow>("SELECT * FROM mod_docs WHERE id=@id", new { id = page.Id })!, markdown));
    }

    private static IResult Delete(string value, string slug, Access access, ForgeContext context, Db db)
    {
        ModRow row = Writable(value, access, context);

        if (db.Exec("DELETE FROM mod_docs WHERE guid=@guid AND slug=@slug", new { guid = row.Guid, slug }) == 0)
            throw ApiException.NotFound(PageNotFound);

        return Api.Json(new { deleted = true });
    }

    private static async Task<IResult> ReorderAsync(string value, HttpRequest request, Access access, ForgeContext context, Db db)
    {
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send order as a list of page slugs.");

        if (!payload.HasExactly("order") || payload["order"] is not JsonArray list || list.Any(n => !n.IsString(out _)))
            throw ApiException.BadRequest("Send order as a list of page slugs.");

        List<string> order = list.Select(n => n!.ToString()).ToList();

        db.BeginImmediate();
        ModRow row = Writable(value, access, context);
        HashSet<string> existing = db.Query<string>("SELECT slug FROM mod_docs WHERE guid=@guid", new { guid = row.Guid }).ToHashSet();

        if (order.Count != existing.Count || !existing.SetEquals(order))
            throw ApiException.BadRequest("List every page exactly once.");

        for (int i = 0; i < order.Count; i++)
            db.Exec("UPDATE mod_docs SET position=@position WHERE guid=@guid AND slug=@slug", new { position = i + 1, guid = row.Guid, slug = order[i] });

        db.Commit();
        return Api.Json(new { pages = Pages(db, row.Guid) });
    }
}
