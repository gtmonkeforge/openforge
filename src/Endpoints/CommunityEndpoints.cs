using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

public static class CommunityEndpoints
{
    private sealed class CommentRow
    {
        public long Id { get; set; }
        public string UserId { get; set; } = "";
        public string Body { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string Name { get; set; } = "";
        public string? CustomSlug { get; set; }
        public int IsVerified { get; set; }
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mods/{value}/ratings", Ratings);
        app.MapPut("/api/v1/mods/{value}/ratings", RateAsync);
        app.MapDelete("/api/v1/mods/{value}/ratings", RemoveRating);
        app.MapGet("/api/v1/mods/{value}/comments", Comments);
        app.MapPost("/api/v1/mods/{value}/comments", AddCommentAsync);
        app.MapDelete("/api/v1/mods/{value}/comments/{commentId:long}", DeleteComment);
        app.MapPatch("/api/v1/mods/{value}/links", UpdateLinksAsync);
    }

    private static ModRow Mod(string value, Access access, bool write = false)
    {
        ModRow? row = access.ResolveMod(value);

        if (!access.CanViewMod(row))
            throw ApiException.NotFound("This mod could not be found.");

        if (write && row!.Status != "approved")
            throw ApiException.Forbidden("This mod is not approved for public discussion.");

        return row!;
    }

    private static CurrentUser SignedIn(ForgeContext context)
    {
        return context.RequireUser("Sign in with Discord first.");
    }

    private static IResult Ratings(string value, Access access, ForgeContext context, Db db)
    {
        ModRow row = Mod(value, access);
        return Api.Json(RatingSummary(row.Guid, context, db));
    }

    private static object RatingSummary(string guid, ForgeContext context, Db db)
    {
        var stats = db.One<(long Count, double? Average)>("SELECT COUNT(*) count,AVG(stars) average FROM mod_ratings WHERE guid=@guid", new { guid });
        int? mine = context.User == null ? null : db.One<int?>("SELECT stars FROM mod_ratings WHERE guid=@guid AND user_id=@uid", new { guid, uid = context.User.Id });

        return new { count = stats.Count, average = stats.Average, mine };
    }

    private static async Task<IResult> RateAsync(string value, HttpRequest request, Access access, ForgeContext context, Db db)
    {
        CurrentUser current = SignedIn(context);
        ModRow row = Mod(value, access, true);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Use a JSON object.");

        if (!payload["stars"].IsInt(out long stars) || stars is < 1 or > 5)
            throw ApiException.BadRequest("Choose an integer rating from 1 to 5.");

        db.Exec("INSERT INTO mod_ratings VALUES(@guid,@uid,@stars) ON CONFLICT(guid,user_id) DO UPDATE SET stars=excluded.stars", new { guid = row.Guid, uid = current.Id, stars });
        return Api.Json(RatingSummary(row.Guid, context, db));
    }

    private static IResult RemoveRating(string value, Access access, ForgeContext context, Db db)
    {
        CurrentUser current = SignedIn(context);
        ModRow row = Mod(value, access, true);

        db.Exec("DELETE FROM mod_ratings WHERE guid=@guid AND user_id=@uid", new { guid = row.Guid, uid = current.Id });
        return Api.Json(RatingSummary(row.Guid, context, db));
    }

    private static IResult Comments(string value, HttpRequest request, Access access, ForgeContext context, Db db)
    {
        ModRow row = Mod(value, access);

        if (!int.TryParse(request.Query["page"].FirstOrDefault() ?? "1", out int page))
            throw ApiException.BadRequest("Invalid page.");

        page = System.Math.Max(1, page);
        CurrentUser? current = context.User;

        List<CommentRow> rows = db.Query<CommentRow>("""
            SELECT c.*,COALESCE(u.public_username,u.display_name) name,u.custom_slug,u.is_verified
            FROM mod_comments c JOIN users u ON u.id=c.user_id WHERE guid=@guid ORDER BY c.id DESC LIMIT 20 OFFSET @offset
            """, new { guid = row.Guid, offset = (page - 1) * 20 }).ToList();

        var items = rows.Select(c => new
        {
            id = c.Id,
            body = c.Body,
            created_at = c.CreatedAt,
            author = new { id = c.UserId, name = c.Name, profile_url = Text.ProfileUrl(c.UserId, c.CustomSlug, c.IsVerified != 0) },
            can_delete = current != null && (current.Id == c.UserId || current.IsAdmin),
        }).ToList();

        long total = db.Scalar<long>("SELECT COUNT(*) FROM mod_comments WHERE guid=@guid", new { guid = row.Guid });
        return Api.Json(new { items, total, page, limit = 20 });
    }

    private static async Task<IResult> AddCommentAsync(string value, HttpRequest request, Access access, ForgeContext context, Db db)
    {
        CurrentUser current = SignedIn(context);
        ModRow row = Mod(value, access, true);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Use a JSON object.");

        if (!payload["body"].IsString(out string body) || Text.Len(body.Trim()) is < 1 or > 3000)
            throw ApiException.BadRequest("Comments must be 1–3,000 characters.");

        db.Exec("INSERT INTO mod_comments(guid,user_id,body,created_at) VALUES(@guid,@uid,@body,@now)", new { guid = row.Guid, uid = current.Id, body = body.Trim(), now = Text.NowIso() });
        return Api.Json(new { id = db.LastInsertId() }, 201);
    }

    private static IResult DeleteComment(string value, long commentId, Access access, ForgeContext context, Db db)
    {
        CurrentUser current = SignedIn(context);
        ModRow row = Mod(value, access);
        string? author = db.One<string>("SELECT user_id FROM mod_comments WHERE id=@commentId AND guid=@guid", new { commentId, guid = row.Guid });

        if (author == null)
            throw ApiException.NotFound("Comment not found.");

        if (current.Id != author && !current.IsAdmin)
            throw ApiException.Forbidden("Only the commenter or an administrator can delete this comment.");

        db.Exec("DELETE FROM mod_comments WHERE id=@commentId", new { commentId });
        return Api.Json(new { deleted = true });
    }

    private static async Task<IResult> UpdateLinksAsync(string value, HttpRequest request, Access access, ForgeContext context, Db db)
    {
        CurrentUser current = SignedIn(context);
        ModRow row = Mod(value, access);

        if (!access.CanPublish(current.Id, row.AuthorId))
            throw ApiException.Forbidden("Only the publisher can edit these links.");

        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Use a JSON object.");
        List<LinkDto> links = PublishEndpoints.ValidateModLinks(payload["links"]);

        db.Exec("UPDATE mods SET links=@links WHERE guid=@guid", new { links = JsonBody.Serialize(links), guid = row.Guid });
        return Api.Json(new { links });
    }
}
