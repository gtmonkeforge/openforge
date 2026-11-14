using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

public static class ChannelEndpoints
{
    private const string CodeAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private sealed record ChannelSettings(string Notes, int Public, int Enabled, string Members);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mods/{value}/channels", List);
        app.MapPost("/api/v1/mods/{value}/channels", CreateAsync);
        app.MapPatch("/api/v1/mods/{value}/channels/{name}", EditAsync);
        app.MapPost("/api/v1/mods/{value}/channels/{name}/share-code", Share);
        app.MapPost("/api/v1/channels/unlock", UnlockAsync);
    }

    private static ModRow Owner(string value, ForgeContext context, Access access)
    {
        CurrentUser current = context.RequireUser("Sign in with Discord first.");
        ModRow? row = access.ResolveMod(value);

        if (row == null || row.Status == "deleted")
            throw ApiException.NotFound("Mod not found.");

        if (!access.CanPublish(current.Id, row.AuthorId))
            throw ApiException.Forbidden("Only the publisher can manage release channels.");

        return row;
    }

    private static ChannelSettings Settings(JsonObject? payload, Db db, ForgeOptions options)
    {
        if (payload == null || !payload.HasOnly("name", "notes", "public", "enabled", "members"))
            throw ApiException.BadRequest("Invalid channel settings.");

        string notes = "";

        if (payload.ContainsKey("notes") && !payload["notes"].IsString(out notes) || Text.Len(notes) > 10000)
            throw ApiException.BadRequest("Channel notes must be at most 10,000 characters.");

        bool isPublic = true;
        bool enabled = true;

        if ((payload.ContainsKey("public") && !payload["public"].IsBool(out isPublic)) || (payload.ContainsKey("enabled") && !payload["enabled"].IsBool(out enabled)))
            throw ApiException.BadRequest("Public and enabled must be booleans.");

        var members = new List<string>();

        if (payload.ContainsKey("members"))
        {
            if (payload["members"] is not JsonArray array || array.Count > 200)
                throw ApiException.BadRequest($"Provide up to 200 {options.BrandName} user IDs.");

            foreach (JsonNode? member in array)
            {
                if (!member.IsString(out string id))
                    throw ApiException.BadRequest($"Provide up to 200 {options.BrandName} user IDs.");

                if (!members.Contains(id))
                    members.Add(id);
            }
        }

        foreach (string member in members)
        {
            if (!db.Exists("SELECT 1 FROM users WHERE id=@member", new { member }))
                throw ApiException.BadRequest($"A channel member is not a registered {options.BrandName} user.");
        }

        return new ChannelSettings(notes.Trim(), isPublic ? 1 : 0, enabled ? 1 : 0, JsonBody.Serialize(members));
    }

    private static IResult List(string value, Access access, Channels channels)
    {
        ModRow? row = access.ResolveMod(value);

        if (!access.CanViewMod(row))
            throw ApiException.NotFound("Mod not found.");

        return Api.Json(new { items = channels.List(row!) });
    }

    private static async Task<IResult> CreateAsync(string value, HttpRequest request, ForgeContext context, Access access, Channels channels, Db db, ForgeOptions options)
    {
        ModRow row = Owner(value, context, access);
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);
        ChannelSettings settings = Settings(payload, db, options);

        if (!payload!["name"].IsString(out string name) || !Text.ChannelName().IsMatch(name))
            throw ApiException.BadRequest("Use a channel name of 1–40 lowercase letters, digits, or hyphens, starting with a letter.");

        db.BeginImmediate();

        if (db.Scalar<long>("SELECT COUNT(*) FROM release_channels WHERE guid=@guid", new { guid = row.Guid }) >= 20)
            throw ApiException.BadRequest("A mod can have at most 20 release channels, including stable.");

        try
        {
            db.Exec("INSERT INTO release_channels(guid,name,notes,public,enabled,members) VALUES(@guid,@name,@Notes,@Public,@Enabled,@Members)",
                new { guid = row.Guid, name, settings.Notes, settings.Public, settings.Enabled, settings.Members });
            db.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            db.Rollback();
            throw ApiException.Conflict("This channel already exists.");
        }

        return Api.Json(new { channel = channels.Get(row.Guid, name) }, 201);
    }

    private static async Task<IResult> EditAsync(string value, string name, HttpRequest request, ForgeContext context, Access access, Channels channels, Db db, ForgeOptions options)
    {
        ModRow row = Owner(value, context, access);
        ChannelDto channel = channels.Get(row.Guid, name) ?? throw ApiException.NotFound("Channel not found.");
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || payload.ContainsKey("name"))
            throw ApiException.BadRequest("Channel names are permanent.");

        var merged = new JsonObject
        {
            ["notes"] = channel.Notes,
            ["public"] = channel.Public,
            ["enabled"] = channel.Enabled,
            ["members"] = new JsonArray((channel.Members ?? []).Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
        };

        foreach (KeyValuePair<string, JsonNode?> pair in payload)
            merged[pair.Key] = pair.Value?.DeepClone();

        ChannelSettings settings = Settings(merged, db, options);

        if (name == "stable" && settings.Enabled == 0)
            throw ApiException.BadRequest("The stable channel cannot be disabled.");

        db.Exec("UPDATE release_channels SET notes=@Notes,public=@Public,enabled=@Enabled,members=@Members WHERE guid=@guid AND name=@name",
            new { settings.Notes, settings.Public, settings.Enabled, settings.Members, guid = row.Guid, name });

        return Api.Json(new { channel = channels.Get(row.Guid, name) });
    }

    private static IResult Share(string value, string name, ForgeContext context, Access access, Channels channels, Db db)
    {
        ModRow row = Owner(value, context, access);

        if (channels.Get(row.Guid, name) == null)
            throw ApiException.NotFound("Channel not found.");

        string code = new string(Enumerable.Range(0, 25).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]).ToArray());
        db.Exec("INSERT INTO channel_share_codes(code,guid,name) VALUES(@code,@guid,@name) ON CONFLICT(guid,name) DO UPDATE SET code=excluded.code",
            new { code, guid = row.Guid, name });

        return Api.Json(new { code = string.Join("-", Enumerable.Range(0, 5).Select(i => code.Substring(i * 5, 5))) });
    }

    private static async Task<IResult> UnlockAsync(HttpRequest request, ForgeContext context, Access access, Channels channels, Db db)
    {
        CurrentUser current = context.RequireUser("Sign in with Discord to unlock a release channel.");
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload["code"].IsString(out string code) || !Text.ShareCode().IsMatch(code.Trim()))
            throw ApiException.BadRequest("Enter a 25-character release channel code.");

        code = code.Trim().Replace("-", "").ToUpperInvariant();
        db.BeginImmediate();

        var shared = db.One<ChannelRow>("SELECT guid,name FROM channel_share_codes WHERE code=@code", new { code })
            ?? throw ApiException.NotFound("Invalid or unavailable release channel code.");

        ModRow? row = access.ResolveMod(shared.Guid);
        ChannelDto? channel = channels.Get(shared.Guid, shared.Name);

        if (row == null || row.Status != "approved" || row.Visibility == "private" || channel == null || !channel.Enabled)
            throw ApiException.NotFound("Invalid or unavailable release channel code.");

        List<string> members = channel.Members ?? [];

        if (!members.Contains(current.Id))
        {
            if (members.Count >= 200)
                throw ApiException.BadRequest("This channel has reached its member limit.");

            members.Add(current.Id);
            db.Exec("UPDATE release_channels SET members=@members WHERE guid=@guid AND name=@name",
                new { members = JsonBody.Serialize(members), guid = shared.Guid, name = shared.Name });
        }

        db.Commit();
        return Api.Json(new { name = channel.Name, url = "/view/" + shared.Guid + "?channel=" + channel.Name });
    }
}
