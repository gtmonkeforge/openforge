using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

public static class ModEndpoints
{
    private const string NotFound = "This mod could not be found.";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mods", Browse);
        app.MapGet("/api/v1/mods/{guid}", Detail);
        app.MapGet("/api/v1/mods/{guid}/releases", Releases);
        app.MapPatch("/api/v1/mods/{guid}/url", ClaimUrlAsync);
        app.MapPatch("/api/v1/mods/{value}/visibility", SetVisibilityAsync);
        app.MapMethods("/api/v1/downloads/{fileId}", ["GET", "HEAD"], DownloadAsync);
        app.MapGet("/media/{filename}", Media);
    }

    private static IResult Browse(HttpRequest request, ForgeContext context, Db db, ModPresenter presenter)
    {
        CurrentUser? current = context.User;
        var conditions = new List<string> { "m.status != 'deleted'" };
        var parameters = new DynamicParameters();
        string Query(string key) => request.Query[key].FirstOrDefault() ?? "";

        if (current == null || !current.IsAdmin)
        {
            conditions.Add("(m.author_id=@viewer OR m.author_id IN (SELECT group_id FROM group_members WHERE user_id=@viewer) OR (m.status='approved' AND (m.visibility='public' OR (m.visibility='code' AND EXISTS(SELECT 1 FROM release_channels c,json_each(c.members) member WHERE c.guid=m.guid AND c.enabled=1 AND member.value=@viewer)))))");
            parameters.Add("viewer", current?.Id ?? "");
        }

        string q = Query("q").Trim();

        if (q.Length > 200)
            q = q[..200];

        if (q != "")
        {
            conditions.Add(@"(m.name LIKE @q ESCAPE '\' OR m.guid LIKE @q ESCAPE '\' OR m.description LIKE @q ESCAPE '\' OR COALESCE(u.public_username,u.username) LIKE @q ESCAPE '\')");
            parameters.Add("q", "%" + Text.LikeEscape(q) + "%");
        }

        if (Query("author_id") != "")
        {
            conditions.Add("m.author_id=@author");
            parameters.Add("author", Query("author_id"));
        }

        if (Query("category") != "")
        {
            conditions.Add("m.category=@category");
            parameters.Add("category", Query("category"));
        }

        string capability = Query("capability");

        if (capability != "")
        {
            if (!Text.Capabilities.Contains(capability))
                throw ApiException.BadRequest("Unknown capability filter.");

            conditions.Add("json_extract(m.capabilities, @capability) = 1");
            parameters.Add("capability", "$." + capability);
        }

        const string latestStableFiles = "SELECT 1 FROM files lf LEFT JOIN file_loaders ll ON ll.file_id=lf.id WHERE lf.release_id=(SELECT id FROM releases WHERE guid=m.guid AND channel='stable' ORDER BY id DESC LIMIT 1) AND COALESCE(ll.loader,'bepinex5')=@loader";
        string loader = Query("loader");
        string fileType = Query("type");

        if (loader != "")
        {
            if (!Text.Loaders.Contains(loader))
                throw ApiException.BadRequest("Choose BepInEx 5, BepInEx 6, or MelonLoader.");

            conditions.Add("EXISTS(" + latestStableFiles + ")");
            parameters.Add("loader", loader);
        }

        if (fileType != "")
        {
            if (fileType != "dll" && fileType != "zip")
                throw ApiException.BadRequest("Type must be dll or zip.");

            if (loader != "")
                conditions.Add("EXISTS(" + latestStableFiles + " AND lf.kind=@kind)");

            conditions.Add("EXISTS(SELECT 1 FROM releases r JOIN files f ON f.release_id=r.id WHERE r.guid=m.guid AND r.version=m.current_version AND r.channel='stable' AND f.kind=@kind)");
            parameters.Add("kind", fileType);
        }

        if (Query("mine") == "true")
        {
            CurrentUser mine = context.RequireUser("Sign in to view your mods.");
            conditions.Add("m.author_id=@mine");
            parameters.Add("mine", mine.Id);
        }

        string? sort = (request.Query["sort"].FirstOrDefault() ?? "newest") switch
        {
            "newest" => "m.updated_at DESC,m.guid",
            "popular" => "m.downloads DESC,m.guid",
            "name" => "m.name COLLATE NOCASE,m.guid",
            _ => null,
        };

        if (sort == null)
            throw ApiException.BadRequest("Unknown sort order.");

        (int page, int limit) = Paging(request, 12);
        string where = " WHERE " + string.Join(" AND ", conditions);
        parameters.Add("limit", limit);
        parameters.Add("offset", (page - 1) * limit);

        long total = db.Scalar<long>("SELECT COUNT(*) FROM (" + ModListRow.BaseQuery + where + ")", parameters);
        List<ModListRow> rows = db.Query<ModListRow>(ModListRow.BaseQuery + where + " ORDER BY " + sort + " LIMIT @limit OFFSET @offset", parameters).ToList();

        return Api.Json(new PageDto<ModDto>(rows.Select(row => presenter.Mod(row)).ToList(), total, page, limit));
    }

    public static (int Page, int Limit) Paging(HttpRequest request, int defaultLimit)
    {
        string pageText = request.Query["page"].FirstOrDefault() ?? "1";
        string limitText = request.Query["limit"].FirstOrDefault() ?? defaultLimit.ToString();

        if (!int.TryParse(pageText.Trim(), out int page) || !int.TryParse(limitText.Trim(), out int limit))
            throw ApiException.BadRequest("Page and limit must be integers.");

        return (Math.Max(1, page), Math.Clamp(limit, 1, 100));
    }

    private static IResult Detail(string guid, HttpRequest request, Access access, ModPresenter presenter)
    {
        ModRow found = access.ResolveMod(guid) ?? throw ApiException.NotFound(NotFound);
        ModListRow? row = access.ModWithAuthor(found.Guid);

        if (!access.CanViewMod(row))
            throw ApiException.NotFound(NotFound);

        return Api.Json(presenter.Mod(row!, true, request.Query["channel"].FirstOrDefault() ?? "stable"));
    }

    private static IResult Releases(string guid, HttpRequest request, Access access, Channels channels, ModPresenter presenter)
    {
        ModRow found = access.ResolveMod(guid) ?? throw new ApiException(404);

        if (!access.CanViewMod(found))
            throw new ApiException(404);

        ChannelDto channel = channels.Select(found, request.Query["channel"].FirstOrDefault() ?? "stable");
        return Api.Json(new { items = presenter.Releases(found.Guid, channel.Name) });
    }

    private static async Task<IResult> ClaimUrlAsync(string guid, HttpRequest request, ForgeContext context, Access access, Db db, ModPresenter presenter)
    {
        CurrentUser current = context.RequireUser("Sign in to claim a custom mod URL.");
        ModRow? row = access.ResolveMod(guid);

        if (row == null || row.Status == "deleted")
            throw ApiException.NotFound("Mod not found.");

        if (!access.CanPublish(current.Id, row.AuthorId))
            throw ApiException.Forbidden("Only the author can change this mod URL.");

        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload.HasExactly("custom_slug"))
            throw ApiException.BadRequest("Supply custom_slug.");

        if (!payload["custom_slug"].IsString(out string value))
            throw ApiException.BadRequest("Custom URLs must be text.");

        string? slug = Text.ValidSlug(value);

        // Group mods use the group's verification, so any member of a verified group can claim one.
        bool authorVerified = db.Scalar<long>("SELECT is_verified FROM users WHERE id=@id", new { id = row.AuthorId }) != 0;

        if (slug != null && !authorVerified)
            throw ApiException.Forbidden("Only verified users can claim custom mod URLs.");

        try
        {
            db.Exec("UPDATE mods SET custom_slug=@slug WHERE guid=@guid", new { slug, guid = row.Guid });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw ApiException.Conflict("This custom mod URL is already claimed.");
        }

        return Api.Json(presenter.Mod(access.ModWithAuthor(row.Guid)!, true));
    }

    private static async Task<IResult> SetVisibilityAsync(string value, HttpRequest request, ForgeContext context, Access access, Db db)
    {
        CurrentUser current = context.RequireUser("Sign in to manage visibility.");
        db.BeginImmediate();
        ModRow? row = access.ResolveMod(value);

        if (row == null || row.Status == "deleted")
            throw ApiException.NotFound("Mod not found.");

        if (!access.CanPublish(current.Id, row.AuthorId))
            throw ApiException.Forbidden("Only the publisher can change mod visibility.");

        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload.HasExactly("visibility") || !payload["visibility"].IsString(out string visibility) || !Text.Visibilities.Contains(visibility))
            throw ApiException.BadRequest("Choose public, private, or code visibility.");

        string status = row.Status;

        if (visibility != row.Visibility)
            status = visibility == "public" ? "pending" : "approved";

        db.Exec("UPDATE mods SET visibility=@visibility,status=@status WHERE guid=@guid", new { visibility, status, guid = row.Guid });
        db.Commit();

        return Api.Json(new { visibility, status });
    }

    private static async Task DownloadAsync(string fileId, HttpContext http, Db db, Access access, Channels channels, Storage storage, Activity activity)
    {
        FileRow? row = db.One<FileRow>("SELECT * FROM files WHERE id=@fileId", new { fileId });
        ReleaseRow? release = row == null ? null : db.One<ReleaseRow>("SELECT * FROM releases WHERE id=@id", new { id = row.ReleaseId });

        if (row == null || release == null)
            throw ApiException.NotFound("Download not found.");

        ModRow? mod = db.One<ModRow>("SELECT * FROM mods WHERE guid=@guid", new { guid = release.Guid });

        if (!access.CanViewMod(mod))
            throw ApiException.NotFound("Download not found.");

        channels.Select(mod!, release.Channel);

        string path = storage.FilePath(row.StoredName);

        if (!File.Exists(path))
            throw new ApiException(404);

        HttpRequest request = http.Request;
        HttpResponse response = http.Response;

        // Range and HEAD requests are served normally but don't count as a completed download.
        if (HttpMethods.IsHead(request.Method) || request.Headers.ContainsKey("Range"))
        {
            await Results.File(path, "application/octet-stream", row.Filename, enableRangeProcessing: true).ExecuteAsync(http);
            return;
        }

        var info = new FileInfo(path);
        response.ContentType = "application/octet-stream";
        response.ContentLength = info.Length;
        response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = row.Filename, DispositionType = "attachment" }.ToString();

        long sent = 0;

        try
        {
            await using FileStream stream = File.OpenRead(path);
            byte[] buffer = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(buffer, http.RequestAborted)) > 0)
            {
                await response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                sent += read;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (sent > 0 && sent == info.Length)
        {
            db.Exec("UPDATE mods SET downloads=downloads+1 WHERE guid=@guid", new { guid = release.Guid });
            activity.Record("download", release.Guid);
        }
    }

    private static IResult Media(string filename, Db db, Access access, Storage storage)
    {
        if (!Text.MediaName().IsMatch(filename))
            throw new ApiException(404);

        string url = "/media/" + filename;
        List<ModRow> linked = db.Query<ModRow>("SELECT * FROM mods WHERE logo=@url OR banner=@url OR EXISTS(SELECT 1 FROM json_each(mods.screenshots) WHERE value=@url)", new { url }).ToList();

        if (linked.Count > 0 && !linked.Any(access.CanViewMod))
            throw new ApiException(404);

        string path = storage.FilePath(filename);

        if (!File.Exists(path))
            throw new ApiException(404);

        string type = filename.EndsWith(".png") ? "image/png" : filename.EndsWith(".jpg") ? "image/jpeg" : "image/webp";
        return Results.File(path, type, enableRangeProcessing: true);
    }
}
