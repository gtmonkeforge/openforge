using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// Extensions are free mods built on top of another mod. Their parent's developers decide which ones are listed on
// the parent's Extensions tab, and mods can read the same listing through the API to run in-game marketplaces.
public static class ExtensionEndpoints
{
    public const int MaxFeatured = 12;
    private const string NotAccepting = "This mod doesn't accept extensions.";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mods/{value}/extensions", List);
        app.MapPatch("/api/v1/mods/{value}/extensions/{extension}", ModerateAsync);
        app.MapPatch("/api/v1/mods/{value}/tabs", SetTabsAsync);
    }

    public static bool IsExtension(Db db, string guid)
    {
        return db.Exists("SELECT 1 FROM mod_extensions WHERE guid=@guid", new { guid });
    }

    // The mod a new extension will attach to. Throws unless the publisher may see it and it accepts extensions.
    public static ModRow ResolveParent(string value, string guid, Access access, Db db)
    {
        ModRow? parent = access.ResolveMod(value);

        if (!access.CanViewMod(parent))
            throw ApiException.NotFound("The mod this extends could not be found.");

        if (!parent!.AcceptsExtensions)
            throw ApiException.Conflict("That mod doesn't accept extensions. Its developers can turn on the Extensions tab.");

        if (parent.Guid == guid)
            throw ApiException.BadRequest("A mod can't extend itself.");

        if (IsExtension(db, parent.Guid))
            throw ApiException.BadRequest("Extensions can't have extensions of their own. Extend the original mod instead.");

        return parent;
    }

    public static void Attach(Db db, Access access, CurrentUser publisher, ModRow parent, string guid, string name)
    {
        // Developers of the parent publishing an extension for their own mod don't need to approve themselves.
        bool own = access.CanPublish(publisher.Id, parent.AuthorId);
        string now = Text.NowIso();

        db.Exec("INSERT INTO mod_extensions(guid,parent_guid,state,created_at,decided_at,decided_by) VALUES(@guid,@parent,@state,@now,@decided,@by)",
            new { guid, parent = parent.Guid, state = own ? "approved" : "pending", now, decided = own ? now : null, by = own ? publisher.Id : null });

        if (own)
            return;

        foreach (string recipient in Developers(db, parent.AuthorId).Where(id => id != publisher.Id))
        {
            ManagementEndpoints.Notify(db, recipient, "extension",
                $"{publisher.DisplayName} submitted {name}, an extension for {parent.Name}. It appears in your Extensions tab once staff approve it.",
                "/view/" + System.Uri.EscapeDataString(parent.Guid) + "/extensions");
        }
    }

    // A personal account is its own developer; a publishing group's owners act for it.
    private static List<string> Developers(Db db, string authorId)
    {
        List<string> owners = db.Query<string>("SELECT g.user_id FROM group_members g JOIN users u ON u.id=g.group_id WHERE g.group_id=@authorId AND u.is_group=1 AND g.role='owner'",
            new { authorId }).ToList();

        return owners.Count > 0 ? owners : [authorId];
    }

    private static ModRow Parent(string value, Access access, ForgeContext context, out bool manager)
    {
        ModRow? parent = access.ResolveMod(value);

        if (!access.CanViewMod(parent))
            throw ApiException.NotFound("This mod could not be found.");

        manager = access.CanManage(context.User, parent!);

        if (!parent!.AcceptsExtensions && !manager)
            throw ApiException.NotFound(NotAccepting);

        return parent;
    }

    private static IResult List(string value, HttpRequest request, Access access, ForgeContext context, Db db, ModPresenter presenter)
    {
        ModRow parent = Parent(value, access, context, out bool manager);
        string Query(string key) => request.Query[key].FirstOrDefault() ?? "";

        var conditions = new List<string> { "e.parent_guid=@parent", ModPresenter.ListedExtension };
        var parameters = new DynamicParameters();
        parameters.Add("parent", parent.Guid);

        // Everyone sees approved extensions. Only the parent's developers can look at pending or unapproved ones.
        string state = Query("state") == "" ? "approved" : Query("state");

        if (state != "all" && !ExtensionRow.States.Contains(state))
            throw ApiException.BadRequest("State must be approved, pending, unapproved or all.");

        if (state != "approved" && !manager)
            throw ApiException.Forbidden("Only this mod's developers can review its extensions.");

        if (state != "all")
        {
            conditions.Add("e.state=@state");
            parameters.Add("state", state);
        }

        if (Query("featured") == "true")
            conditions.Add("e.featured=1");
        else if (Query("featured") is not ("" or "false"))
            throw ApiException.BadRequest("featured must be true or false.");

        string q = Query("q").Trim();

        if (q.Length > 200)
            q = q[..200];

        if (q != "")
        {
            conditions.Add(@"(m.name LIKE @q ESCAPE '\' OR m.guid LIKE @q ESCAPE '\' OR m.description LIKE @q ESCAPE '\' OR COALESCE(u.public_username,u.username) LIKE @q ESCAPE '\')");
            parameters.Add("q", "%" + Text.LikeEscape(q) + "%");
        }

        if (Query("category") != "")
        {
            conditions.Add("m.category=@category");
            parameters.Add("category", Query("category"));
        }

        string? sort = (Query("sort") == "" ? "featured" : Query("sort")) switch
        {
            "featured" => "e.featured DESC,m.downloads DESC,m.guid",
            "popular" => "m.downloads DESC,m.guid",
            "newest" => "m.updated_at DESC,m.guid",
            "name" => "m.name COLLATE NOCASE,m.guid",
            _ => null,
        };

        if (sort == null)
            throw ApiException.BadRequest("Sort must be featured, popular, newest or name.");

        (int page, int limit) = ModEndpoints.Paging(request, 24);
        string from = ModListRow.BaseQuery + " JOIN mod_extensions e ON e.guid=m.guid WHERE " + string.Join(" AND ", conditions);
        parameters.Add("limit", limit);
        parameters.Add("offset", (page - 1) * limit);

        long total = db.Scalar<long>("SELECT COUNT(*) FROM (" + from + ")", parameters);
        List<ModDto> items = db.Query<ModListRow>(from + " ORDER BY " + sort + " LIMIT @limit OFFSET @offset", parameters)
            .ToList()
            .Select(row =>
            {
                ModDto mod = presenter.Mod(row);
                ReleaseRow? latest = db.One<ReleaseRow>("SELECT * FROM releases WHERE guid=@guid AND channel='stable' ORDER BY id DESC LIMIT 1", new { guid = row.Guid });
                mod.LatestRelease = latest == null ? null : presenter.Release(latest);
                return mod;
            })
            .ToList();

        Dictionary<string, long>? counts = null;

        if (manager)
        {
            counts = ExtensionRow.States.ToDictionary(s => s, _ => 0L);

            foreach ((string State, long Count) row in db.Query<(string, long)>(
                "SELECT e.state,COUNT(*) FROM mod_extensions e JOIN mods m ON m.guid=e.guid WHERE e.parent_guid=@parent AND " + ModPresenter.ListedExtension + " GROUP BY e.state",
                new { parent = parent.Guid }))
            {
                counts[row.State] = row.Count;
            }
        }

        return Api.Json(new
        {
            items,
            total,
            page,
            limit,
            accepting = parent.AcceptsExtensions,
            can_moderate = manager,
            counts,
        });
    }

    private static async Task<IResult> ModerateAsync(string value, string extension, HttpRequest request, Access access, ForgeContext context, Db db, ModPresenter presenter)
    {
        CurrentUser user = context.RequireUser("Sign in to moderate extensions.");
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send state and/or featured as JSON.");

        if (payload.Count == 0 || !payload.HasOnly("state", "featured"))
            throw ApiException.BadRequest("Send state and/or featured as JSON.");

        db.BeginImmediate();
        ModRow? found = access.ResolveMod(value);

        if (!access.CanViewMod(found))
            throw ApiException.NotFound("This mod could not be found.");

        ModRow parent = found!;

        if (!access.CanManage(user, parent))
            throw ApiException.Forbidden("Only this mod's developers can moderate its extensions.");

        ModRow? child = access.ResolveMod(extension);
        ExtensionRow? link = child == null ? null : db.One<ExtensionRow>("SELECT * FROM mod_extensions WHERE guid=@guid AND parent_guid=@parent", new { guid = child.Guid, parent = parent.Guid });

        if (child == null || link == null || child.Status == "deleted")
            throw ApiException.NotFound("This extension could not be found.");

        string state = link.State;
        bool featured = link.Featured != 0;

        if (payload.ContainsKey("state"))
        {
            if (!payload["state"].IsString(out state) || !ExtensionRow.States.Contains(state))
                throw ApiException.BadRequest("State must be approved, pending or unapproved.");
        }

        if (payload.ContainsKey("featured") && !payload["featured"].IsBool(out featured))
            throw ApiException.BadRequest("featured must be true or false.");

        // Featuring is a promotion of something already listed, so anything taken off the list loses it.
        if (state != "approved")
        {
            if (payload.ContainsKey("featured") && featured)
                throw ApiException.Conflict("Approve this extension before featuring it.");

            featured = false;
        }

        if (featured && link.Featured == 0 && db.Scalar<long>("SELECT COUNT(*) FROM mod_extensions WHERE parent_guid=@parent AND featured=1", new { parent = parent.Guid }) >= MaxFeatured)
            throw ApiException.Conflict($"A mod can feature up to {MaxFeatured} extensions. Unfeature one first.");

        bool decided = state != link.State;

        db.Exec("UPDATE mod_extensions SET state=@state,featured=@featured,decided_at=CASE WHEN @decided THEN @now ELSE decided_at END,decided_by=CASE WHEN @decided THEN @by ELSE decided_by END WHERE guid=@guid",
            new { state, featured = featured ? 1 : 0, decided, now = Text.NowIso(), by = user.Id, guid = child.Guid });

        if (decided && state != "pending")
        {
            string verdict = state == "approved" ? "approved" : "didn't approve";
            ManagementEndpoints.Notify(db, child.AuthorId, "extension", $"The developers of {parent.Name} {verdict} your extension {child.Name}.",
                "/view/" + System.Uri.EscapeDataString(child.Guid));
        }

        db.Commit();
        return Api.Json(presenter.Mod(access.ModWithAuthor(child.Guid)!));
    }

    private static async Task<IResult> SetTabsAsync(string value, HttpRequest request, Access access, ForgeContext context, Db db)
    {
        CurrentUser user = context.RequireUser("Sign in to manage this mod.");
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send extensions and/or docs as JSON booleans.");

        if (payload.Count == 0 || !payload.HasOnly("extensions", "docs"))
            throw ApiException.BadRequest("Send extensions and/or docs as JSON booleans.");

        db.BeginImmediate();
        ModRow? row = access.ResolveMod(value);

        if (row == null || row.Status == "deleted")
            throw ApiException.NotFound("Mod not found.");

        if (!access.CanManage(user, row))
            throw ApiException.Forbidden("Only the publisher can change this mod's tabs.");

        bool extensions = row.AcceptsExtensions;
        bool docs = row.HasDocs;

        if (payload.ContainsKey("extensions") && !payload["extensions"].IsBool(out extensions))
            throw ApiException.BadRequest("extensions must be true or false.");

        if (payload.ContainsKey("docs") && !payload["docs"].IsBool(out docs))
            throw ApiException.BadRequest("docs must be true or false.");

        if (extensions && !row.AcceptsExtensions && IsExtension(db, row.Guid))
            throw ApiException.BadRequest("Extensions can't have extensions of their own.");

        db.Exec("UPDATE mods SET extensions_enabled=@extensions,docs_enabled=@docs WHERE guid=@guid", new { extensions = extensions ? 1 : 0, docs = docs ? 1 : 0, guid = row.Guid });
        db.Commit();

        return Api.Json(new { extensions_enabled = extensions, docs_enabled = docs });
    }
}
