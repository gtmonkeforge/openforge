using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenForge.Infrastructure;
using OpenForge.Models;

namespace OpenForge.Services;

public sealed class ModPresenter
{
    private readonly Db _db;
    private readonly ForgeOptions _options;
    private readonly Channels _channels;
    private readonly MarkdownRenderer _markdown;
    private readonly Payments _payments;
    private readonly ForgeContext _context;
    private readonly Dictionary<string, List<MedalDto>> _creatorMedals = new();

    public ModPresenter(Db db, ForgeOptions options, Channels channels, MarkdownRenderer markdown, Payments payments, ForgeContext context)
    {
        _context = context;
        _db = db;
        _options = options;
        _channels = channels;
        _markdown = markdown;
        _payments = payments;
    }

    public ReleaseDto Release(ReleaseRow row)
    {
        List<DownloadDto> downloads = _db.Query<FileRow>(
            "SELECT f.id,f.stored_name,f.filename,f.size,f.sha256,f.kind,COALESCE(l.loader,'bepinex5') AS loader FROM files f LEFT JOIN file_loaders l ON l.file_id=f.id WHERE f.release_id=@id",
            new { id = row.Id })
            .Select(file => new DownloadDto
            {
                Id = file.Id,
                Filename = file.Filename,
                Size = file.Size,
                Sha256 = file.Sha256,
                Kind = file.Kind,
                Loader = file.Loader,
                // Sealed injector files have no download URL at all; the injector API is the only way to get them.
                Url = PayloadVault.IsSealed(file.StoredName) ? "" : _options.PublicUrl + "/api/v1/downloads/" + file.Id,
            })
            .ToList();

        List<string> loaders = downloads.Select(file => file.Loader).Distinct().Order(System.StringComparer.Ordinal).ToList();

        return new ReleaseDto
        {
            Id = row.Id,
            Guid = row.Guid,
            Version = row.Version,
            ReleasedAt = row.ReleasedAt,
            Notes = row.Notes,
            Channel = row.Channel,
            Downloads = downloads,
            Loaders = loaders,
            DownloadsByLoader = loaders.ToDictionary(loader => loader, loader => downloads.Where(file => file.Loader == loader).ToList()),
        };
    }

    public List<ReleaseDto> Releases(string guid, string channel)
    {
        return _db.Query<ReleaseRow>("SELECT * FROM releases WHERE guid=@guid AND channel=@channel ORDER BY id DESC", new { guid, channel })
            .ToList()
            .Select(Release)
            .ToList();
    }

    public List<string> ReleaseLoaders(string guid, string channel)
    {
        return _db.Query<string>("""
            SELECT DISTINCT COALESCE(l.loader,'bepinex5') FROM files f
            LEFT JOIN file_loaders l ON l.file_id=f.id
            WHERE f.release_id=(SELECT id FROM releases WHERE guid=@guid AND channel=@channel ORDER BY id DESC LIMIT 1)
            ORDER BY 1
            """, new { guid, channel }).ToList();
    }

    public static List<MedalDto> ModMedals(Db db, string guid)
    {
        return db.Query<MedalDto>("SELECT id,name FROM mod_medals WHERE guid=@guid ORDER BY id", new { guid }).ToList();
    }

    // A creator shows the medals of their listed mods. Private, unapproved and removed mods don't count, so their names stay hidden.
    public static List<MedalDto> CreatorMedals(Db db, string userId)
    {
        return db.Query<MedalDto>("""
            SELECT x.id,x.name,m.guid mod_id,m.name mod_name FROM mod_medals x JOIN mods m ON m.guid=x.guid
            WHERE m.author_id=@userId AND m.status='approved' AND m.visibility='public' ORDER BY x.id
            """, new { userId }).ToList();
    }

    private List<MedalDto> CreatorMedals(string userId)
    {
        if (!_creatorMedals.TryGetValue(userId, out List<MedalDto>? medals))
            _creatorMedals[userId] = medals = CreatorMedals(_db, userId);

        return medals;
    }

    public ModDto Mod(ModListRow row, bool full = false, string channelName = "stable")
    {
        bool authorVerified = row.IsVerified != 0;
        string id = authorVerified && !string.IsNullOrEmpty(row.CustomSlug) ? row.CustomSlug : row.Guid;

        var mod = new ModDto
        {
            Guid = row.Guid,
            Name = row.Name,
            Description = row.Description,
            Category = row.Category,
            SourceUrl = row.SourceUrl,
            Logo = row.Logo,
            Screenshots = JsonBody.StringList(row.Screenshots),
            Capabilities = JsonBody.ParseObject(row.Capabilities) ?? new JsonObject(),
            CurrentVersion = row.CurrentVersion,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            Downloads = row.Downloads,
            Demo = row.Demo,
            Status = row.Status,
            Visibility = row.Visibility,
            Banner = row.Banner,
            ModdersChoice = row.IsChoice != 0,
            Medals = ModMedals(_db, row.Guid),
            ChoiceNote = row.IsChoice != 0 && !string.IsNullOrWhiteSpace(row.ChoiceNote) ? row.ChoiceNote : null,
            CustomSlug = row.CustomSlug,
            Links = JsonNode.Parse(row.Links) ?? new JsonArray(),
            Author = new AuthorDto
            {
                Id = row.AuthorId,
                Name = row.DisplayName,
                DiscordUsername = row.AuthorId.StartsWith(Text.EmailAccountPrefix, StringComparison.Ordinal) || row.AuthorId.StartsWith("group-", StringComparison.Ordinal) ? "" : row.Username,
                IsAdmin = row.IsAdmin != 0 || (_options.RootAdminId != "" && row.AuthorId == _options.RootAdminId),
                IsDeveloper = row.IsDeveloper != 0,
                IsVerified = authorVerified,
                Medals = CreatorMedals(row.AuthorId),
                Avatar = row.AuthorAvatar,
                ProfileUrl = Text.ProfileUrl(row.AuthorId, row.AuthorSlug, authorVerified),
            },
            Id = id,
            Url = _options.PublicUrl + "/view/" + id,
            Loaders = ReleaseLoaders(row.Guid, channelName),
            PriceCents = row.PriceCents,
            Currency = _options.Currency,
            UsesInjector = row.Injected,
            Owned = _payments.CanUse(row),
            Gated = _payments.IsGated(row),
            MinTierCents = _db.One<long?>("""
                SELECT MIN(t.price_cents) FROM subscription_tiers t WHERE t.archived_at IS NULL
                AND (t.guid=@guid OR EXISTS(SELECT 1 FROM tier_mods tm WHERE tm.tier_id=t.id AND tm.guid=@guid))
                """, new { guid = row.Guid }),
            DonationsEnabled = _options.StripeConfigured && row.AuthorTransfers != 0,
            ExtensionsEnabled = row.AcceptsExtensions,
            DocsEnabled = row.HasDocs,
            Extends = Extends(row.Guid),
        };

        if (!full && row.Visibility == "code")
        {
            List<ChannelDto> accessible = _channels.List(row);

            if (accessible.Count > 0)
            {
                mod.CurrentVersion = accessible[0].CurrentVersion;
                mod.Url += "?channel=" + accessible[0].Name;
            }
        }

        if (!full)
            return mod;

        ChannelDto selected = _channels.Select(row, channelName);
        selected.Members = null;

        mod.Channel = selected;
        mod.Channels = _channels.List(row);
        mod.CurrentVersion = selected.CurrentVersion;
        mod.Releases = Releases(row.Guid, channelName);
        mod.DescriptionHtml = _markdown.Render(row.Description);
        mod.Tiers = Endpoints.SubscriptionEndpoints.TiersForMod(_db, row.Guid)
            .Select(t => Endpoints.SubscriptionEndpoints.Present(t, _db, _options, _context.User?.Id, false)).ToList();
        mod.ExtensionCount = _db.Scalar<long>("SELECT COUNT(*) FROM mod_extensions e JOIN mods m ON m.guid=e.guid WHERE e.parent_guid=@guid AND e.state='approved' AND " + ListedExtension,
            new { guid = row.Guid });
        mod.DocCount = _db.Scalar<long>("SELECT COUNT(*) FROM mod_docs WHERE guid=@guid", new { guid = row.Guid });

        return mod;
    }

    // An extension shows up in its parent's listing only once staff have approved it as a public mod, so parent
    // developers never moderate, and marketplaces never install, files that haven't been through review.
    public const string ListedExtension = "m.status='approved' AND m.visibility='public'";

    private sealed class ExtendsRow
    {
        public string State { get; set; } = "";
        public int Featured { get; set; }
        public string Guid { get; set; } = "";
        public string Name { get; set; } = "";
        public string? CustomSlug { get; set; }
        public int IsVerified { get; set; }
    }

    public ExtendsDto? Extends(string guid)
    {
        ExtendsRow? parent = _db.One<ExtendsRow>("""
            SELECT e.state,e.featured,p.guid,p.name,p.custom_slug,u.is_verified FROM mod_extensions e
            JOIN mods p ON p.guid=e.parent_guid JOIN users u ON u.id=p.author_id WHERE e.guid=@guid AND p.status!='deleted'
            """, new { guid });

        if (parent == null)
            return null;

        string id = parent.IsVerified != 0 && !string.IsNullOrEmpty(parent.CustomSlug) ? parent.CustomSlug : parent.Guid;

        return new ExtendsDto
        {
            Guid = parent.Guid,
            Id = id,
            Name = parent.Name,
            Url = _options.PublicUrl + "/view/" + Uri.EscapeDataString(id),
            State = parent.State,
            Featured = parent.Featured != 0,
        };
    }
}
