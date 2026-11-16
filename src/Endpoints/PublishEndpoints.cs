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

public static class PublishEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/mods", PublishModAsync).WithMetadata(new ApiKeyAllowedAttribute());
        app.MapPost("/api/v1/mods/{guid}/releases", PublishReleaseAsync).WithMetadata(new ApiKeyAllowedAttribute());
    }

    public static List<LinkDto> ValidateModLinks(JsonNode? node)
    {
        if (node is not JsonArray links || links.Count > 3)
            throw ApiException.BadRequest("A mod can have at most three links, excluding source code.");

        var cleaned = new List<LinkDto>();

        foreach (JsonNode? link in links)
        {
            if (link is not JsonObject item || !item.HasExactly("label", "url"))
                throw ApiException.BadRequest("Each link needs a label and URL.");

            bool validLabel = item["label"].IsString(out string label) && Text.Len(label.Trim()) is >= 1 and <= 80;

            if (!validLabel || !item["url"].IsString(out string url) || Text.Len(url) > 2000)
                throw ApiException.BadRequest("Use a label up to 80 characters and a URL up to 2,000 characters.");

            url = url.Trim();

            if (!Text.IsWebUrl(url))
                throw ApiException.BadRequest("Links must use valid HTTP or HTTPS URLs.");

            cleaned.Add(new LinkDto(label.Trim(), url));
        }

        return cleaned;
    }

    public static string? ValidSourceUrl(string url, string message)
    {
        if (url == "")
            return null;

        if (Text.Len(url) > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.Host == "" || uri.UserInfo != "")
            throw ApiException.BadRequest(message);

        return url;
    }

    private static List<(IFormFile File, string Loader)> LoaderDownloads(IFormCollection form)
    {
        string primary = form["loader"].FirstOrDefault() ?? "bepinex5";

        if (!Text.Loaders.Contains(primary))
            throw ApiException.BadRequest("Choose BepInEx 5, BepInEx 6, or MelonLoader.");

        var result = form.Files.GetFiles("downloads").Where(f => f.FileName != "").Select(f => (f, primary)).ToList();

        foreach (string loader in Text.Loaders)
            result.AddRange(form.Files.GetFiles("downloads_" + loader).Where(f => f.FileName != "").Select(f => (f, loader)));

        if (result.Count == 0)
            throw ApiException.BadRequest("Attach downloads for at least one mod loader.");

        foreach (string loader in Text.Loaders)
        {
            List<string> kinds = result.Where(r => r.Item2 == loader).Select(r => r.Item1.FileName.Split('.').Last().ToLowerInvariant()).ToList();

            if (kinds.Count > 2 || kinds.Distinct().Count() != kinds.Count || kinds.Any(k => k != "dll" && k != "zip"))
                throw ApiException.BadRequest("Each loader supports one DLL, one ZIP, or one of each.");
        }

        return result;
    }

    private static Task<IResult> PublishModAsync(HttpRequest request, ForgeContext context, Db db, Access access, Channels channels, Storage storage, MonkeShield shield, Webhooks webhooks)
    {
        return PublishAsync(null, request, context, db, access, channels, storage, shield, webhooks);
    }

    private static Task<IResult> PublishReleaseAsync(string guid, HttpRequest request, ForgeContext context, Db db, Access access, Channels channels, Storage storage, MonkeShield shield, Webhooks webhooks)
    {
        return PublishAsync(guid, request, context, db, access, channels, storage, shield, webhooks);
    }

    private static async Task<IResult> PublishAsync(string? targetGuid, HttpRequest request, ForgeContext context, Db db, Access access, Channels channels, Storage storage, MonkeShield shield, Webhooks webhooks)
    {
        CurrentUser current = context.RequireUser("Sign in with Discord before publishing.");

        if (request.ContentType == null || !request.ContentType.StartsWith("multipart/form-data"))
            throw ApiException.BadRequest("Use multipart/form-data.");

        IFormCollection form = await request.ReadFormAsync();
        string Field(string key) => (form[key].FirstOrDefault() ?? "").Trim();

        string name = Field("name");
        string guid = Field("guid");
        string version = Field("version");
        string description = Field("description");
        string category = Field("category");
        string sourceUrl = Field("source_url");
        string notes = Field("notes");
        string visibility = form["visibility"].FirstOrDefault() ?? "public";
        string author = Field("author_id") == "" ? current.Id : Field("author_id");
        bool releaseOnly = targetGuid != null;

        if (!Text.Visibilities.Contains(visibility))
            throw ApiException.BadRequest("Choose public, private, or code visibility.");

        if (releaseOnly)
        {
            ModRow? target = access.ResolveMod(targetGuid!);

            if (target == null || target.Status == "deleted")
                throw ApiException.NotFound("Mod not found.");

            if (!access.CanPublish(current.Id, target.AuthorId))
                throw ApiException.Forbidden("Only the publisher can push releases to this mod.");

            (name, guid, description, category, sourceUrl) = (target.Name, target.Guid, target.Description, target.Category, target.SourceUrl ?? "");
        }

        if (name == "" || Text.Len(name) > 100)
            throw ApiException.BadRequest("Name is required and must be 100 characters or fewer.");

        if (!Text.Guid().IsMatch(guid) || guid.Length > 200)
            throw ApiException.BadRequest("Use a reverse-domain GUID, such as com.yourname.yourmod.");

        if (!Text.Version().IsMatch(version))
            throw ApiException.BadRequest("Use a version such as 1.0.0 (up to 64 characters).");

        if (description == "" || Text.Len(description) > 30000)
            throw ApiException.BadRequest("Description is required and must be 30,000 characters or fewer.");

        if (Text.Len(notes) > 10000)
            throw ApiException.BadRequest("Release notes must be 10,000 characters or fewer.");

        category = category == "" ? "Other" : category;

        if (!Text.Categories.Contains(category))
            throw ApiException.BadRequest("Choose a valid category.");

        string? source = ValidSourceUrl(sourceUrl, "Source code must be a valid HTTP or HTTPS URL.");
        List<LinkDto>? links = null;

        if (form.ContainsKey("links") && !releaseOnly)
        {
            JsonNode? parsed;

            try
            {
                parsed = JsonNode.Parse(form["links"].ToString());
            }
            catch (JsonException)
            {
                throw ApiException.BadRequest("Links must be a JSON array.");
            }

            links = ValidateModLinks(parsed);
        }

        db.BeginImmediate();
        ModRow? existing = db.One<ModRow>("SELECT * FROM mods WHERE guid=@guid", new { guid });

        if (existing != null && existing.Status == "deleted")
            throw ApiException.Conflict("This mod has been deleted. Contact an administrator.");

        if (existing != null && !access.CanPublish(current.Id, existing.AuthorId))
            throw ApiException.Forbidden("This GUID belongs to another author.");

        if (existing == null && author != current.Id && !access.CanPublish(current.Id, author))
            throw ApiException.Forbidden("You can only publish as yourself or as a publishing group you belong to.");

        if (releaseOnly)
            (name, description, category, source) = (existing!.Name, existing.Description, existing.Category, existing.SourceUrl);

        string channelName = (form["channel"].FirstOrDefault() ?? "stable").Trim();

        if (existing != null)
        {
            ChannelDto? channel = channels.Get(existing.Guid, channelName);

            if (channel == null || !channel.Enabled)
                throw ApiException.BadRequest("Choose an enabled release channel.");
        }
        else if (channelName != "stable")
        {
            throw ApiException.BadRequest("Publish the first release to stable, then create other channels.");
        }

        if (db.Exists("SELECT 1 FROM releases WHERE guid=@guid AND channel=@channelName AND version=@version", new { guid, channelName, version }))
            throw ApiException.Conflict("This version already exists. Publish with a new version number.");

        List<(IFormFile File, string Loader)> uploads = LoaderDownloads(form);
        List<IFormFile> shots = form.Files.GetFiles("screenshots").Where(f => f.FileName != "").ToList();

        if (shots.Count > 20)
            throw ApiException.BadRequest("A mod can have at most 20 screenshots.");

        var written = new List<string>();
        var stored = new List<StoredDownload>();
        long releaseId;
        string previousStatus = existing?.Status ?? "pending";

        try
        {
            // Injector delivery is chosen when a mod is created and then applies to every release.
            bool injector = existing?.Injected ?? form["uses_injector"] == "true";

            foreach ((IFormFile file, string _) in uploads)
                stored.Add(await storage.SaveDownloadAsync(file, written, injector));

            IFormFile? logoFile = form.Files.GetFile("logo");
            string? logo;

            if (releaseOnly)
                logo = existing!.Logo;
            else if (form["remove_logo"] == "true")
                logo = null;
            else if (logoFile != null && logoFile.FileName != "")
                logo = await storage.SaveImageAsync(logoFile, written);
            else
                logo = existing?.Logo;

            List<string> screenshots;

            if (releaseOnly)
            {
                screenshots = JsonBody.StringList(existing!.Screenshots);
            }
            else if (form["remove_screenshots"] == "true")
            {
                screenshots = [];
            }
            else if (shots.Count > 0)
            {
                screenshots = [];

                foreach (IFormFile shot in shots)
                    screenshots.Add(await storage.SaveImageAsync(shot, written));
            }
            else
            {
                screenshots = JsonBody.StringList(existing?.Screenshots);
            }

            string capabilities = releaseOnly
                ? existing!.Capabilities
                : JsonBody.Serialize(Text.Capabilities.ToDictionary(key => key, key => form[key] == "true"));

            string now = Text.NowIso();

            if (channelName == "stable")
            {
                db.Exec("""
                    INSERT INTO mods(guid,name,description,author_id,category,source_url,logo,screenshots,capabilities,current_version,created_at,updated_at)
                    VALUES(@guid,@name,@description,@author,@category,@source,@logo,@screenshots,@capabilities,@version,@now,@now) ON CONFLICT(guid) DO UPDATE SET name=excluded.name,description=excluded.description,
                    category=excluded.category,source_url=excluded.source_url,logo=excluded.logo,screenshots=excluded.screenshots,
                    capabilities=excluded.capabilities,current_version=excluded.current_version,updated_at=excluded.updated_at
                    """, new { guid, name, description, author, category, source, logo, screenshots = JsonBody.Serialize(screenshots), capabilities, version, now });
            }

            IFormFile? banner = form.Files.GetFile("banner");

            if (!releaseOnly && banner != null && banner.FileName != "")
                db.Exec("UPDATE mods SET banner=@banner WHERE guid=@guid", new { banner = await storage.SaveImageAsync(banner, written), guid });

            if (existing == null)
                db.Exec("UPDATE mods SET visibility=@visibility,uses_injector=@injector WHERE guid=@guid", new { visibility, injector = injector ? 1 : 0, guid });

            db.Exec("INSERT OR IGNORE INTO release_channels(guid,name) VALUES(@guid,'stable')", new { guid });

            if (links != null && channelName == "stable")
                db.Exec("UPDATE mods SET links=@links WHERE guid=@guid", new { links = JsonBody.Serialize(links), guid });

            string releaseVisibility = existing?.Visibility ?? visibility;
            db.Exec("UPDATE mods SET status=@status WHERE guid=@guid", new { status = releaseVisibility == "public" ? "pending" : "approved", guid });

            db.Exec("INSERT INTO releases(guid,version,released_at,notes,channel) VALUES(@guid,@version,@now,@notes,@channelName)", new { guid, version, now, notes, channelName });
            releaseId = db.LastInsertId();

            for (int i = 0; i < stored.Count; i++)
            {
                StoredDownload file = stored[i];
                db.Exec("INSERT INTO files VALUES(@Id,@releaseId,@StoredName,@Filename,@Size,@Sha256,@Kind)",
                    new { file.Id, releaseId, file.StoredName, file.Filename, file.Size, file.Sha256, file.Kind });
                db.Exec("INSERT INTO file_loaders(file_id,loader) VALUES(@id,@loader)", new { id = file.Id, loader = uploads[i].Loader });
            }

            shield.Prepare(db, guid, channelName, releaseId, version);
            db.Commit();
        }
        catch (Exception ex)
        {
            db.Rollback();
            Storage.DeleteAll(written);

            if (ex is SqliteException { SqliteErrorCode: 19 })
                throw ApiException.Conflict("This GUID or version was just published. Refresh and try again.");

            throw;
        }

        List<ScanFile> scanFiles = stored.Select(file => new ScanFile(storage.FilePath(file.StoredName), file.Filename, file.Kind)).ToList();
        await shield.FinishAsync(db, guid, channelName, releaseId, scanFiles);
        webhooks.NotifyModStatus(guid, previousStatus, releaseId);

        string url = "/view/" + guid + (channelName != "stable" ? "?channel=" + channelName : "");
        return Api.Json(new { guid, url }, 201);
    }
}
