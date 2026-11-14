using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

public static class UserEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/users/{userId}", Profile);
        app.MapPut("/api/v1/users/{userId}", UpdateProfileAsync);
    }

    public static ProfileDto BuildProfile(UserRow row, Db db, ForgeContext context, ForgeOptions options)
    {
        CurrentUser? current = context.User;
        bool allowed = current != null && (current.IsAdmin || current.Id == row.Id || (row.Group && db.Exists("SELECT 1 FROM group_members WHERE group_id=@id AND user_id=@uid", new { id = row.Id, uid = current.Id })));
        string visibleOnly = allowed ? "" : " AND status='approved' AND visibility='public'";

        List<GroupMemberDto>? members = null;

        if (row.Group)
        {
            members = db.Query<GroupMemberDto>("""
                SELECT u.id, COALESCE(u.public_username,u.display_name) AS name, u.avatar, m.role
                FROM group_members m JOIN users u ON u.id=m.user_id
                WHERE m.group_id=@id ORDER BY m.role='owner' DESC, name COLLATE NOCASE, u.id
                """, new { id = row.Id }).ToList();
        }

        return new ProfileDto
        {
            Id = row.Id,
            Username = row.PublicUsername ?? row.Username,
            DisplayName = row.PublicUsername ?? row.DisplayName,
            Description = row.Description,
            Links = ParseLinks(row.Links),
            IsAdmin = row.IsAdmin != 0 || (options.RootAdminId != "" && row.Id == options.RootAdminId),
            IsVerified = row.Verified,
            PublicUsername = row.PublicUsername,
            Avatar = row.Avatar,
            CustomSlug = row.CustomSlug,
            IsGroup = row.Group,
            DiscordUsername = row.Username,
            ProfileUrl = Text.ProfileUrl(row.Id, row.CustomSlug, row.Verified),
            ModCount = db.Scalar<long>("SELECT COUNT(*) FROM mods WHERE author_id=@id AND status != 'deleted'" + visibleOnly, new { id = row.Id }),
            Members = members,
            GroupName = row.Group ? row.DisplayName : null,
        };
    }

    private static List<LinkDto> ParseLinks(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<LinkDto>>(json, JsonBody.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IResult Profile(string userId, Access access, Db db, ForgeContext context, ForgeOptions options)
    {
        UserRow row = access.ResolveUser(userId) ?? throw ApiException.NotFound("This profile could not be found.");
        return Api.Json(BuildProfile(row, db, context, options));
    }

    private static async Task<IResult> UpdateProfileAsync(string userId, HttpRequest request, ForgeContext context, Access access, Db db, Storage storage, ForgeOptions options)
    {
        CurrentUser current = context.RequireUser("Sign in with Discord to edit your profile.");

        UserRow? target = current.Id == userId ? current.Row : db.One<UserRow>("SELECT * FROM users WHERE id=@userId AND is_group=1", new { userId });

        if (target == null)
            throw ApiException.Forbidden("You can only edit your own profile.");

        if (target.Id != current.Id && !access.CanTransfer(current.Id, target.Id))
            throw ApiException.Forbidden("Only the group owner can edit this group.");

        JsonObject? payload;
        IFormFile? avatarFile = null;

        if (request.HasFormContentType && request.ContentType!.StartsWith("multipart/form-data"))
        {
            IFormCollection form = await request.ReadFormAsync();

            try
            {
                payload = JsonNode.Parse(form["profile"].ToString()) as JsonObject;
            }
            catch (JsonException)
            {
                throw ApiException.BadRequest("Supply valid profile JSON.");
            }

            avatarFile = form.Files.GetFile("avatar");
        }
        else
        {
            payload = await JsonBody.ReadObjectAsync(request);
        }

        string[] editable = target.Group
            ? ["description", "links", "public_username", "custom_slug", "remove_avatar", "display_name"]
            : ["description", "links", "public_username", "custom_slug", "remove_avatar"];

        if (payload == null || !payload.HasOnly(editable))
            throw ApiException.BadRequest("Only public profile fields can be edited here.");

        string displayName = target.DisplayName;

        if (payload.ContainsKey("display_name") && (!payload["display_name"].IsString(out displayName) || Text.Len(displayName.Trim()) is < 3 or > 60))
            throw ApiException.BadRequest("Use a group name of 3–60 characters.");

        string description = target.Description;

        if (payload.ContainsKey("description") && !payload["description"].IsString(out description))
            throw ApiException.BadRequest("Description must be 3,000 characters or fewer.");

        if (Text.Len(description) > 3000)
            throw ApiException.BadRequest("Description must be 3,000 characters or fewer.");

        string? publicName = target.PublicUsername;

        if (payload.ContainsKey("public_username"))
            publicName = payload["public_username"] == null ? null : payload["public_username"].IsString(out string name) ? name : "\0";

        if (publicName != null)
        {
            if (!Text.PublicUsername().IsMatch(publicName))
                throw ApiException.BadRequest("Usernames must have 3–32 letters, digits, underscores, dots, or hyphens, starting with a letter or digit.");

            if (Text.Reserved.Contains(publicName.ToLowerInvariant()))
                throw ApiException.BadRequest("This username is reserved.");

            if (db.Exists("SELECT 1 FROM users WHERE id != @userId AND lower(COALESCE(public_username,username))=lower(@publicName)", new { userId, publicName }))
                throw ApiException.Conflict("This username is already claimed.");
        }

        string? slug = target.CustomSlug;

        if (payload.ContainsKey("custom_slug"))
        {
            if (!payload["custom_slug"].IsString(out string slugValue))
                throw ApiException.BadRequest("Custom URLs must be text.");

            slug = Text.ValidSlug(slugValue);

            if (slug != null && !target.Verified)
                throw ApiException.Forbidden("Only verified users can claim custom profile URLs.");
        }

        bool removeAvatar = false;

        if (payload.ContainsKey("remove_avatar") && !payload["remove_avatar"].IsBool(out removeAvatar))
            throw ApiException.BadRequest("remove_avatar must be a boolean.");

        List<LinkDto> links = payload.ContainsKey("links") ? ProfileLinks(payload["links"]) : ParseLinks(target.Links);
        var written = new List<string>();

        try
        {
            string? avatar = target.Avatar;

            if (removeAvatar)
                avatar = null;
            else if (avatarFile != null && avatarFile.FileName != "")
                avatar = await storage.SaveImageAsync(avatarFile, written, avatar: true);

            db.Exec("UPDATE users SET display_name=@displayName,description=@description,links=@links,public_username=@publicName,custom_slug=@slug,avatar=@avatar WHERE id=@userId",
                new { displayName = displayName.Trim(), description = description.Trim(), links = JsonBody.Serialize(links), publicName, slug, avatar, userId });
        }
        catch (Exception ex)
        {
            Storage.DeleteAll(written);

            if (ex is SqliteException { SqliteErrorCode: 19 })
                throw ApiException.Conflict("This username or custom profile URL is already claimed.");

            throw;
        }

        context.Refresh();
        return Api.Json(BuildProfile(db.One<UserRow>("SELECT * FROM users WHERE id=@userId", new { userId })!, db, context, options));
    }

    private static List<LinkDto> ProfileLinks(JsonNode? node)
    {
        if (node is not JsonArray links || links.Count > 5)
            throw ApiException.BadRequest("A profile can have at most five links.");

        var cleaned = new List<LinkDto>();

        foreach (JsonNode? link in links)
        {
            if (link is not JsonObject item || !item.HasExactly("label", "url"))
                throw ApiException.BadRequest("Each link needs a label and URL.");

            if (!item["label"].IsString(out string label) || label.Trim() == "" || Text.Len(label) > 80 || !item["url"].IsString(out string url) || Text.Len(url) > 2000)
                throw ApiException.BadRequest("Links need a label up to 80 characters and a URL up to 2,000 characters.");

            if (!Text.IsWebUrl(url.Trim()))
                throw ApiException.BadRequest("Profile links must use valid HTTP or HTTPS URLs.");

            cleaned.Add(new LinkDto(label.Trim(), url.Trim()));
        }

        return cleaned;
    }
}
