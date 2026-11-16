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
    private HashSet<string>? _topTen;

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

    public ModDto Mod(ModListRow row, bool full = false, string channelName = "stable")
    {
        _topTen ??= _db.Query<string>("SELECT guid FROM mods WHERE status='approved' AND visibility='public' AND downloads>0 ORDER BY downloads DESC,guid LIMIT 10").ToHashSet();

        bool authorVerified = row.IsVerified != 0;
        var badges = new List<string>();

        if (_topTen.Contains(row.Guid))
            badges.Add("Top 10 Downloads");

        if (row.ChoiceCategory != null)
            badges.Add("Modder’s Choice · " + row.ChoiceCategory);

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
            ChoiceCategory = row.ChoiceCategory,
            CustomSlug = row.CustomSlug,
            Links = JsonNode.Parse(row.Links) ?? new JsonArray(),
            Author = new AuthorDto
            {
                Id = row.AuthorId,
                Name = row.DisplayName,
                DiscordUsername = row.AuthorId.StartsWith(Text.EmailAccountPrefix, StringComparison.Ordinal) || row.AuthorId.StartsWith("group-", StringComparison.Ordinal) ? "" : row.Username,
                IsAdmin = row.IsAdmin != 0 || (_options.RootAdminId != "" && row.AuthorId == _options.RootAdminId),
                IsVerified = authorVerified,
                Avatar = row.AuthorAvatar,
                ProfileUrl = Text.ProfileUrl(row.AuthorId, row.AuthorSlug, authorVerified),
            },
            Badges = badges,
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

        return mod;
    }
}
