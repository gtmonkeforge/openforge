using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

public static class ManagementEndpoints
{
    private static readonly string[] EditableFields =
        ["name", "description", "category", "source_url", "capabilities", "links", "remove_logo", "remove_screenshots", "remove_banner"];

    private sealed class TransferListRow
    {
        public string Id { get; set; } = "";
        public string Guid { get; set; } = "";
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public string RequestedBy { get; set; } = "";
        public string Status { get; set; } = "";
        public double Created { get; set; }
        public string Name { get; set; } = "";
        public string TargetName { get; set; } = "";
        public string SourceName { get; set; } = "";
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPatch("/api/v1/mods/{value}", EditAsync);
        app.MapPost("/api/v1/mods/{value}/transfer", RequestTransferAsync);
        app.MapGet("/api/v1/mod-transfers", Transfers);
        app.MapPost("/api/v1/mod-transfers/{transferId}/{action}", TransferAction);
    }

    public static void Notify(Db db, string userId, string kind, string body, string target)
    {
        db.Exec("INSERT INTO notifications(user_id,kind,body,target,created) VALUES(@userId,@kind,@body,@target,@now)",
            new { userId, kind, body, target, now = Text.NowEpoch() });
    }

    private static async Task<IResult> EditAsync(string value, HttpRequest request, ForgeContext context, Access access, Db db, Storage storage, Webhooks webhooks)
    {
        CurrentUser user = context.RequireUser("Sign in to manage mods.");
        JsonObject? payload;
        IFormCollection? form = null;

        if (request.ContentType?.StartsWith("multipart/form-data") == true)
        {
            form = await request.ReadFormAsync();

            if (form.Files.Any(f => f.Name is not ("logo" or "screenshots" or "banner")))
                throw ApiException.BadRequest("DLL and ZIP uploads require a new release.");

            if (form.Keys.Any(k => k != "metadata"))
                throw ApiException.BadRequest("Supply metadata JSON and optional images only.");

            try
            {
                payload = JsonNode.Parse(form.ContainsKey("metadata") ? form["metadata"].ToString() : "{}") as JsonObject;
            }
            catch (JsonException)
            {
                throw ApiException.BadRequest("Supply valid metadata JSON.");
            }
        }
        else
        {
            payload = await JsonBody.ReadObjectAsync(request);
        }

        if (payload == null || !payload.HasOnly(EditableFields))
            throw ApiException.BadRequest("Only listing metadata and images can be edited. Downloads require a new release.");

        db.BeginImmediate();
        ModRow? row = access.ResolveMod(value);

        if (row == null || row.Status == "deleted")
            throw ApiException.NotFound("Mod not found.");

        if (!access.CanPublish(user.Id, row.AuthorId))
            throw ApiException.Forbidden("Only the publisher can edit this listing.");

        string name = row.Name;
        string description = row.Description;
        string category = row.Category;
        string? sourceUrl = row.SourceUrl;
        string? logo = row.Logo;
        string? banner = row.Banner;
        string screenshots = row.Screenshots;
        string capabilities = row.Capabilities;
        string links = row.Links;

        foreach ((string key, int limit) in new[] { ("name", 100), ("description", 30000) })
        {
            if (!payload.ContainsKey(key))
                continue;

            if (!payload[key].IsString(out string text) || text.Trim() == "" || Text.Len(text) > limit)
                throw ApiException.BadRequest($"{char.ToUpperInvariant(key[0]) + key[1..]} must contain 1–{limit} characters.");

            if (key == "name")
                name = text.Trim();
            else
                description = text.Trim();
        }

        if (payload.ContainsKey("category"))
        {
            if (!payload["category"].IsString(out string chosen) || !Text.Categories.Contains(chosen))
                throw ApiException.BadRequest("Choose a valid category.");

            category = chosen;
        }

        if (payload.ContainsKey("source_url"))
        {
            JsonNode? node = payload["source_url"];

            if (node != null && !node.IsString(out _))
                throw ApiException.BadRequest("Use an HTTP or HTTPS source URL.");

            sourceUrl = PublishEndpoints.ValidSourceUrl(node?.ToString() ?? "", "Use an HTTP or HTTPS source URL.");
        }

        if (payload.ContainsKey("links"))
            links = JsonBody.Serialize(PublishEndpoints.ValidateModLinks(payload["links"]));

        if (payload.ContainsKey("capabilities"))
        {
            if (payload["capabilities"] is not JsonObject caps || !caps.HasOnly("auto_update", "extensions", "internet") || caps.Any(c => !c.Value.IsBool(out _)))
                throw ApiException.BadRequest("Use boolean capability settings.");

            JsonObject merged = JsonBody.ParseObject(row.Capabilities) ?? new JsonObject();

            foreach (KeyValuePair<string, JsonNode?> pair in caps)
                merged[pair.Key] = pair.Value?.DeepClone();

            capabilities = merged.ToJsonString();
        }

        var flags = new Dictionary<string, bool>();

        foreach (string flag in new[] { "remove_logo", "remove_screenshots", "remove_banner" })
        {
            if (!payload.ContainsKey(flag))
                continue;

            if (!payload[flag].IsBool(out bool set))
                throw ApiException.BadRequest("Image removal flags must be boolean.");

            flags[flag] = set;
        }

        bool Flag(string key)
        {
            return flags.TryGetValue(key, out bool set) && set;
        }

        List<IFormFile> shots = form?.Files.GetFiles("screenshots").Where(f => f.FileName != "").ToList() ?? [];
        List<IFormFile> logos = form?.Files.GetFiles("logo").Where(f => f.FileName != "").ToList() ?? [];
        List<IFormFile> banners = form?.Files.GetFiles("banner").Where(f => f.FileName != "").ToList() ?? [];

        if (logos.Count > 1 || shots.Count > 20)
            throw ApiException.BadRequest("Supply one logo and at most 20 screenshots.");

        if ((logos.Count > 0 && Flag("remove_logo")) || (shots.Count > 0 && Flag("remove_screenshots")))
            throw ApiException.BadRequest("Choose replacement or removal for each image field.");

        var written = new List<string>();
        string status = row.Status;

        try
        {
            if (banners.Count > 1 || (banners.Count > 0 && Flag("remove_banner")))
                throw ApiException.BadRequest("Choose one banner or remove it.");

            if (banners.Count > 0)
                banner = await storage.SaveImageAsync(banners[0], written);
            else if (Flag("remove_banner"))
                banner = null;

            if (logos.Count > 0)
                logo = await storage.SaveImageAsync(logos[0], written);
            else if (Flag("remove_logo"))
                logo = null;

            if (shots.Count > 0)
            {
                var saved = new List<string>();

                foreach (IFormFile shot in shots)
                    saved.Add(await storage.SaveImageAsync(shot, written));

                screenshots = JsonBody.Serialize(saved);
            }
            else if (Flag("remove_screenshots"))
            {
                screenshots = "[]";
            }

            bool changed = name != row.Name || description != row.Description || category != row.Category || sourceUrl != row.SourceUrl
                || logo != row.Logo || banner != row.Banner || !JsonBody.SameJson(screenshots, row.Screenshots)
                || !JsonBody.SameJson(capabilities, row.Capabilities) || !JsonBody.SameJson(links, row.Links);

            bool verified = db.Scalar<long>("SELECT is_verified FROM users WHERE id=@author", new { author = row.AuthorId }) != 0;

            if (changed && status == "approved" && row.Visibility == "public" && !verified)
                status = "pending";

            if (changed)
            {
                db.Exec("""
                    UPDATE mods SET name=@name,description=@description,category=@category,source_url=@sourceUrl,logo=@logo,banner=@banner,
                    screenshots=@screenshots,capabilities=@capabilities,links=@links,status=@status,updated_at=@now WHERE guid=@guid
                    """, new { name, description, category, sourceUrl, logo, banner, screenshots, capabilities, links, status, now = Text.NowIso(), guid = row.Guid });
            }

            db.Commit();
        }
        catch
        {
            db.Rollback();
            Storage.DeleteAll(written);
            throw;
        }

        var before = JsonBody.StringList(row.Screenshots).Append(row.Logo).Append(row.Banner).OfType<string>().ToHashSet();
        var after = JsonBody.StringList(screenshots).Append(logo).Append(banner).OfType<string>().ToHashSet();
        storage.DeleteUnreferencedMedia(before.Except(after));
        webhooks.NotifyModStatus(row.Guid, row.Status);

        return Api.Json(new { guid = row.Guid, status, url = "/view/" + row.Guid });
    }

    private static void Complete(TransferRow transfer, CurrentUser actor, Db db)
    {
        UserRow? source = db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = transfer.Source });

        if (source == null || source.Banned)
            throw ApiException.Forbidden("The source publisher is unavailable.");

        UserRow? target = db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = transfer.Target });

        if (target == null || target.Banned)
            throw ApiException.Forbidden("The destination publisher is unavailable.");

        int changed = db.Exec("UPDATE mods SET author_id=@target,custom_slug=CASE WHEN @verified THEN custom_slug ELSE NULL END WHERE guid=@guid AND author_id=@source AND status!='deleted'",
            new { target = target.Id, verified = target.IsVerified, guid = transfer.Guid, source = transfer.Source });

        if (changed == 0)
            throw ApiException.Conflict("The mod publisher has changed or the mod was deleted.");

        if (target.Verified)
            db.Exec("UPDATE mods SET status='approved' WHERE guid=@guid AND status='pending'", new { guid = transfer.Guid });

        db.Exec("UPDATE mod_transfers SET status='accepted' WHERE id=@id", new { id = transfer.Id });
        db.Exec("INSERT INTO admin_audit(actor_id,action,target,created_at) VALUES(@actor,'mod.transferred',@guid,datetime('now'))", new { actor = actor.Id, guid = transfer.Guid });
        Notify(db, transfer.RequestedBy, "mod_transfer", "Mod ownership transfer completed.", transfer.Guid);
    }

    private static async Task<IResult> RequestTransferAsync(string value, HttpRequest request, ForgeContext context, Access access, Db db, Webhooks webhooks)
    {
        CurrentUser user = context.RequireUser("Sign in to manage mods.");
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload.HasExactly("target_id") || !payload["target_id"].IsString(out string targetValue))
            throw ApiException.BadRequest("Supply target_id as a username or publisher ID.");

        db.BeginImmediate();
        ModRow? row = access.ResolveMod(value);

        if (row == null || row.Status == "deleted")
            throw ApiException.NotFound("Mod not found.");

        if (!access.CanPublish(user.Id, row.AuthorId) || !access.CanTransfer(user.Id, row.AuthorId))
            throw ApiException.Forbidden("Only the personal publisher or group owner can transfer ownership.");

        targetValue = targetValue.Trim().TrimStart('@');
        List<UserRow> matches = SocialEndpoints.MatchUsers(db, targetValue);
        UserRow? target = matches.FirstOrDefault(m => m.Id == targetValue);

        if (target == null)
        {
            if (matches.Count != 1)
                throw ApiException.NotFound("Publisher not found or username is ambiguous. Use their ID.");

            target = matches[0];
        }

        if (target.Banned)
            throw ApiException.BadRequest("Choose an active publisher.");

        if (target.Id == row.AuthorId)
            throw ApiException.BadRequest("This account already owns the mod.");

        if (db.Exists("SELECT 1 FROM mod_transfers WHERE guid=@guid AND status='pending'", new { guid = row.Guid }))
            throw ApiException.Conflict("Cancel the current transfer before starting another.");

        string transferId = Text.Hex(16);
        db.Exec("INSERT INTO mod_transfers(id,guid,source,target,requested_by,created) VALUES(@transferId,@guid,@source,@target,@requester,@now)",
            new { transferId, guid = row.Guid, source = row.AuthorId, target = target.Id, requester = user.Id, now = Text.NowEpoch() });

        TransferRow transfer = db.One<TransferRow>("SELECT * FROM mod_transfers WHERE id=@transferId", new { transferId })!;
        bool immediate = access.CanTransfer(user.Id, target.Id);

        if (immediate)
        {
            Complete(transfer, user, db);
        }
        else
        {
            List<string> recipients = target.Group
                ? db.Query<string>("SELECT user_id FROM group_members WHERE group_id=@id AND role='owner'", new { id = target.Id }).ToList()
                : [target.Id];

            foreach (string recipient in recipients)
                Notify(db, recipient, "mod_transfer", "Ownership transfer requested for " + row.Name + ".", transferId);
        }

        db.Commit();

        if (immediate)
            webhooks.NotifyModStatus(row.Guid, row.Status);

        return Api.Json(new { id = transferId, status = immediate ? "accepted" : "pending" }, 201);
    }

    private static IResult Transfers(ForgeContext context, Access access, Db db)
    {
        string uid = context.RequireUser("Sign in to manage mods.").Id;
        var items = new List<object>();

        foreach (TransferListRow r in db.Query<TransferListRow>("SELECT t.*,m.name,u.display_name target_name,s.display_name source_name FROM mod_transfers t JOIN mods m ON m.guid=t.guid JOIN users u ON u.id=t.target JOIN users s ON s.id=t.source WHERE t.status='pending'").ToList())
        {
            bool incoming = access.CanTransfer(uid, r.Target);
            bool outgoing = access.CanTransfer(uid, r.Source);

            if (incoming || outgoing)
            {
                items.Add(new
                {
                    id = r.Id, guid = r.Guid, source = r.Source, target = r.Target, requested_by = r.RequestedBy, status = r.Status,
                    created = r.Created, name = r.Name, target_name = r.TargetName, source_name = r.SourceName, incoming, outgoing,
                });
            }
        }

        return Api.Json(new { items });
    }

    private static IResult TransferAction(string transferId, string action, ForgeContext context, Access access, Db db, Webhooks webhooks)
    {
        CurrentUser user = context.RequireUser("Sign in to manage mods.");
        db.BeginImmediate();
        TransferRow transfer = db.One<TransferRow>("SELECT * FROM mod_transfers WHERE id=@transferId AND status='pending'", new { transferId })
            ?? throw ApiException.NotFound("Pending transfer not found.");

        if (action == "cancel")
        {
            if (!access.CanTransfer(user.Id, transfer.Source))
                throw ApiException.Forbidden("Only the current publisher can cancel.");
        }
        else if (action is "accept" or "decline")
        {
            if (!access.CanTransfer(user.Id, transfer.Target))
                throw ApiException.Forbidden("Only the destination publisher or group owner can answer.");
        }
        else
        {
            throw ApiException.NotFound("Unknown transfer action.");
        }

        string? previous = db.One<string>("SELECT status FROM mods WHERE guid=@guid", new { guid = transfer.Guid });

        if (action == "accept")
            Complete(transfer, user, db);
        else
            db.Exec("UPDATE mod_transfers SET status=@status WHERE id=@transferId", new { status = action == "cancel" ? "cancelled" : "declined", transferId });

        db.Commit();

        if (action == "accept" && previous != null)
            webhooks.NotifyModStatus(transfer.Guid, previous);

        return Api.Ok();
    }
}
