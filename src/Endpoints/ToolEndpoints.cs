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

public static class ToolEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/api-keys", ListKeys);
        app.MapPost("/api/v1/api-keys", CreateKeyAsync);
        app.MapDelete("/api/v1/api-keys/{keyId}", RevokeKey);
        app.MapGet("/api/v1/mods/{value}/monkeshield-report", ShieldReport);
        app.MapPost("/api/v1/github-release", GitHubReleaseAsync);
        app.MapPost("/api/v1/github-release/asset", GitHubAssetAsync);
    }

    private static CurrentUser KeyOwner(ForgeContext context)
    {
        if (context.Session.UserId == null || context.User == null)
            throw ApiException.Unauthorized("Sign in to manage API keys.");

        return context.User;
    }

    private static IResult ListKeys(ForgeContext context, Db db)
    {
        CurrentUser current = KeyOwner(context);
        var items = db.Query<ApiKeyRow>("SELECT id,name,prefix,created_at,last_used_at,revoked_at FROM api_keys WHERE user_id=@id ORDER BY created_at DESC", new { id = current.Id })
            .Select(k => new { id = k.Id, name = k.Name, prefix = k.Prefix, created_at = k.CreatedAt, last_used_at = k.LastUsedAt, revoked_at = k.RevokedAt })
            .ToList();

        return Api.Json(new { items });
    }

    private static async Task<IResult> CreateKeyAsync(HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser current = KeyOwner(context);
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload["name"].IsString(out string name) || Text.Len(name.Trim()) is < 1 or > 80)
            throw ApiException.BadRequest("Give the key a name of 1–80 characters.");

        string token = "mf_" + Text.UrlToken(32);
        string id = Text.Hex(16);
        string now = Text.NowIso();

        db.Exec("INSERT INTO api_keys(id,user_id,name,token_hash,prefix,created_at) VALUES(@id,@user,@name,@hash,@prefix,@now)",
            new { id, user = current.Id, name = name.Trim(), hash = Text.Sha256Hex(token), prefix = token[..10], now });

        // The only time the full token leaves the server.
        return Api.Json(new { id, name = name.Trim(), token, created_at = now }, 201);
    }

    private static IResult RevokeKey(string keyId, ForgeContext context, Db db)
    {
        CurrentUser current = KeyOwner(context);

        if (db.Exec("UPDATE api_keys SET revoked_at=COALESCE(revoked_at,@now) WHERE id=@keyId AND user_id=@user", new { now = Text.NowIso(), keyId, user = current.Id }) == 0)
            throw ApiException.NotFound("API key not found.");

        return Api.Json(new { revoked = true });
    }

    public static (ModRow Mod, string Channel, ShieldReportRow? Row, JsonObject Report) LookupReport(string value, HttpRequest request, Access access, Channels channels, Db db)
    {
        ModRow? mod = access.ResolveMod(value);

        if (mod == null || mod.Status == "deleted")
            throw ApiException.NotFound("Mod not found.");

        string channelName = request.Query["channel"].FirstOrDefault() ?? "stable";
        ChannelDto? channel = channels.Get(mod.Guid, channelName);

        // Public-channel reports stay visible even while a failed scan keeps the mod out of the catalog.
        bool isPublic = mod.Visibility == "public" && channel != null && channel.Public && channel.Enabled;

        if (!isPublic && (!access.CanViewMod(mod) || !channels.Allowed(mod, channel)))
            throw ApiException.NotFound("Report not found.");

        ShieldReportRow? row = db.One<ShieldReportRow>("SELECT * FROM monkeshield_reports WHERE guid=@guid AND channel=@channelName", new { guid = mod.Guid, channelName });
        JsonObject report = (JsonObject)MonkeShield.ReadReport(row?.Report).DeepClone();
        report.Remove("_claim");

        return (mod, channelName, row, report);
    }

    private static IResult ShieldReport(string value, HttpRequest request, Access access, Channels channels, Db db)
    {
        (ModRow mod, string channel, ShieldReportRow? row, JsonObject report) = LookupReport(value, request, access, channels, db);
        return Api.Json(new { guid = mod.Guid, channel, version = row?.Version, release_id = row?.ReleaseId, report });
    }

    private static async Task<IResult> GitHubReleaseAsync(HttpRequest request, ForgeContext context, GitHubReleases github)
    {
        context.RequireUser("Sign in before importing a release.");
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);
        GitHubRelease release = await github.ReleaseAsync(payload?["url"]);

        var assets = release.Assets.Select(a => new JsonObject { ["id"] = a["id"]?.DeepClone(), ["name"] = a["name"]?.DeepClone(), ["size"] = a["size"]?.DeepClone() }).ToList();
        string tag = release.Data["tag_name"]?.ToString() ?? "";
        string notes = release.Data["body"]?.ToString() ?? "";

        return Api.Json(new { tag, notes, assets });
    }

    private static async Task<IResult> GitHubAssetAsync(HttpRequest request, ForgeContext context, GitHubReleases github)
    {
        context.RequireUser("Sign in before importing a release.");
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);
        GitHubRelease release = await github.ReleaseAsync(payload?["url"]);
        JsonNode? wanted = payload?["asset_id"];

        JsonObject asset = release.Assets.FirstOrDefault(a => wanted != null && JsonNode.DeepEquals(a["id"], wanted))
            ?? throw ApiException.BadRequest("Choose a DLL or ZIP asset of at most 50 MB from this release.");

        byte[] raw = await github.FetchAsync($"https://api.github.com/repos/{release.Owner}/{release.Repo}/releases/assets/{asset["id"]}", GitHubReleases.AssetLimit, true);

        if (raw.Length == 0)
            throw ApiException.BadRequest("GitHub asset is empty.");

        return Results.File(raw, "application/octet-stream", asset["name"]!.ToString());
    }
}
