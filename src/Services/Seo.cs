using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using OpenForge.Infrastructure;
using OpenForge.Models;

namespace OpenForge.Services;

// Builds what crawlers read: titles, descriptions, canonical URLs, schema.org data, a server-rendered
// first view of each public page, and the sitemap. Only approved, public mods are ever offered for indexing.
public sealed partial class Seo
{
    public const string PublicMods = "m.status='approved' AND m.visibility='public'";

    private const int DescriptionLength = 158;
    private const int ListLimit = 48;

    private readonly Db _db;
    private readonly ForgeOptions _options;
    private readonly MarkdownRenderer _markdown;
    private readonly Payments _payments;
    private readonly DocsStore _docs;

    public Seo(Db db, ForgeOptions options, MarkdownRenderer markdown, Payments payments, DocsStore docs)
    {
        _db = db;
        _options = options;
        _markdown = markdown;
        _payments = payments;
        _docs = docs;
    }

    private string Game => _options.BrandGame;

    // "Utilla · Gorilla Tag mod", or just "Utilla" when no game is configured.
    public string ModTitle(string name)
    {
        return Game == "" ? name : name + " · " + Game + " mod";
    }

    public string BrowseTitle(string? category)
    {
        string mods = Game == "" ? "Mods" : Game + " mods";
        return category == null ? mods : category + " " + (Game == "" ? "mods" : mods);
    }

    public PageMeta Browse(string? category)
    {
        if (category != null && !Text.Categories.Contains(category))
            category = null;

        string filter = category == null ? "" : " AND m.category=@category";
        long count = _db.Scalar<long>("SELECT COUNT(*) FROM mods m WHERE " + PublicMods + filter, new { category });
        List<ModListRow> mods = _db.Query<ModListRow>(ModListRow.BaseQuery + " WHERE " + PublicMods + filter + " ORDER BY m.downloads DESC,m.guid LIMIT " + ListLimit, new { category }).ToList();

        string canonical = category == null ? "/browse" : "/browse?category=" + Uri.EscapeDataString(category);
        string noun = count == 1 ? "mod" : "mods";
        string what = (category == null ? "" : category.ToLowerInvariant() + " ") + (Game == "" ? noun : Game + " " + noun);
        string description = $"Browse {count.ToString("N0", CultureInfo.InvariantCulture)} {what} on {_options.BrandName}, free and paid, for BepInEx and MelonLoader."
            + (string.IsNullOrEmpty(_options.MonkeShieldUrl) ? " Each comes with ratings and full version history." : " Every release is security scanned by MonkeShield.");

        var html = new StringBuilder();
        html.Append("<section class=\"surface panel\"><h1>").Append(Encode(BrowseTitle(category))).Append("</h1><p class=\"hint\">").Append(Encode(description)).Append("</p>");
        html.Append(ModList(mods)).Append("</section>");

        var site = new Dictionary<string, object?>
        {
            ["@type"] = "WebSite",
            ["@id"] = _options.PublicUrl + "/#website",
            ["name"] = _options.BrandName,
            ["url"] = _options.PublicUrl + "/browse",
            ["description"] = _options.BrandDescription,
        };
        var list = new Dictionary<string, object?>
        {
            ["@type"] = "ItemList",
            ["name"] = BrowseTitle(category),
            ["numberOfItems"] = mods.Count,
            ["itemListElement"] = mods.Select((m, i) => new Dictionary<string, object?>
            {
                ["@type"] = "ListItem",
                ["position"] = i + 1,
                ["url"] = _options.PublicUrl + ModPath(m),
                ["name"] = m.Name,
            }).ToList(),
        };

        return new PageMeta
        {
            Description = description,
            Canonical = canonical,
            JsonLd = Graph(site, list),
            Content = html.ToString(),
        };
    }

    public PageMeta Mod(ModListRow row)
    {
        // Private and code-only mods stay out of search results even for the people who can open them.
        if (row.Status != "approved" || row.Visibility != "public")
            return PageMeta.Private;

        bool verified = row.IsVerified != 0;
        string path = ModPath(row);
        string url = _options.PublicUrl + path;
        string authorUrl = _options.PublicUrl + Text.ProfileUrl(row.AuthorId, row.AuthorSlug, verified);
        string excerpt = Excerpt(row.Description, name: row.Name);
        string byline = $"A {row.Category.ToLowerInvariant()} {(Game == "" ? "mod" : Game + " mod")} by {row.DisplayName} on {_options.BrandName}.";
        string description = excerpt.Length >= 70 ? excerpt : Truncate((excerpt == "" ? "" : excerpt.TrimEnd('.') + ". ") + byline);
        var stats = _db.One<(long Count, double? Average)>("SELECT COUNT(*) count,AVG(stars) average FROM mod_ratings WHERE guid=@guid", new { guid = row.Guid });

        var app = new Dictionary<string, object?>
        {
            ["@type"] = "SoftwareApplication",
            ["@id"] = url + "#mod",
            ["name"] = row.Name,
            ["description"] = excerpt == "" ? byline : excerpt,
            ["url"] = url,
            ["applicationCategory"] = "GameApplication",
            ["applicationSubCategory"] = row.Category,
            ["operatingSystem"] = "Windows",
            ["softwareVersion"] = NullIfEmpty(row.CurrentVersion),
            ["datePublished"] = NullIfEmpty(row.CreatedAt),
            ["dateModified"] = NullIfEmpty(row.UpdatedAt),
            ["image"] = Absolute(row.Banner ?? row.Logo),
            ["author"] = new Dictionary<string, object?> { ["@type"] = "Person", ["name"] = row.DisplayName, ["url"] = authorUrl },
            ["offers"] = Offer(row, url),
            ["interactionStatistic"] = new Dictionary<string, object?>
            {
                ["@type"] = "InteractionCounter",
                ["interactionType"] = "https://schema.org/DownloadAction",
                ["userInteractionCount"] = row.Downloads,
            },
        };

        if (Game != "")
            app["gamePlatform"] = Game;

        if (stats.Count > 0 && stats.Average != null)
        {
            app["aggregateRating"] = new Dictionary<string, object?>
            {
                ["@type"] = "AggregateRating",
                ["ratingValue"] = Math.Round(stats.Average.Value, 1),
                ["ratingCount"] = stats.Count,
                ["bestRating"] = 5,
                ["worstRating"] = 1,
            };
        }

        if (!string.IsNullOrEmpty(row.SourceUrl))
            app["codeRepository"] = row.SourceUrl;

        var crumbs = Breadcrumbs(
            (BrowseTitle(null), _options.PublicUrl + "/browse"),
            (row.Category, _options.PublicUrl + "/browse?category=" + Uri.EscapeDataString(row.Category)),
            (row.Name, url));

        var html = new StringBuilder();
        html.Append("<article class=\"surface panel\"><nav class=\"breadcrumb\"><a href=\"/browse\">All mods</a></nav><h1>").Append(Encode(row.Name)).Append("</h1>");
        html.Append("<p class=\"hint\">").Append(Encode($"A {row.Category.ToLowerInvariant()} mod by ")).Append("<a href=\"").Append(Encode(Text.ProfileUrl(row.AuthorId, row.AuthorSlug, verified))).Append("\">").Append(Encode(row.DisplayName)).Append("</a>");

        if (row.CurrentVersion != "")
            html.Append(" · v").Append(Encode(row.CurrentVersion));

        html.Append(" · ").Append(row.Downloads.ToString("N0", CultureInfo.InvariantCulture)).Append(" downloads</p>");
        html.Append("<div class=\"markdown\">").Append(_markdown.Render(row.Description)).Append("</div></article>");

        return new PageMeta
        {
            Description = description,
            Canonical = path,
            Image = row.Banner ?? row.Logo,
            JsonLd = Graph(app, crumbs),
            Content = html.ToString(),
        };
    }

    public PageMeta Shield(ModListRow row)
    {
        if (row.Status != "approved" || row.Visibility != "public")
            return PageMeta.Private;

        return new PageMeta
        {
            Description = Truncate($"MonkeShield security scan for {row.Name}, a {(Game == "" ? "mod" : Game + " mod")} by {row.DisplayName}: what the latest release can access and any findings."),
            Canonical = ModPath(row) + "/monkeshield",
            Image = row.Banner ?? row.Logo,
        };
    }

    public PageMeta Profile(UserRow user)
    {
        if (user.Banned)
            return PageMeta.Private;

        List<ModListRow> mods = _db.Query<ModListRow>(ModListRow.BaseQuery + " WHERE m.author_id=@id AND " + PublicMods + " ORDER BY m.downloads DESC,m.guid LIMIT " + ListLimit, new { id = user.Id }).ToList();

        // A profile with nothing public on it is a thin page; keep it out of the index.
        if (mods.Count == 0)
            return PageMeta.Private;

        string path = Text.ProfileUrl(user.Id, user.CustomSlug, user.Verified);
        string excerpt = Excerpt(user.Description);
        string names = string.Join(", ", mods.Take(3).Select(m => m.Name));
        string fallback = $"{mods.Count} {(Game == "" ? "mods" : Game + " mods")} by {user.PublicName} on {_options.BrandName}, including {names}.";
        string description = excerpt.Length >= 70 ? excerpt : Truncate(fallback);

        var person = new Dictionary<string, object?>
        {
            ["@type"] = user.Group ? "Organization" : "Person",
            ["name"] = user.PublicName,
            ["alternateName"] = user.Username,
            ["identifier"] = user.Id,
            ["url"] = _options.PublicUrl + path,
            ["image"] = Absolute(user.Avatar),
            ["description"] = NullIfEmpty(excerpt),
        };
        var page = new Dictionary<string, object?>
        {
            ["@type"] = "ProfilePage",
            ["url"] = _options.PublicUrl + path,
            ["mainEntity"] = person,
        };

        var html = new StringBuilder();
        html.Append("<section class=\"surface panel\"><h1>").Append(Encode(user.PublicName)).Append("</h1>");

        if (excerpt != "")
            html.Append("<p class=\"hint\">").Append(Encode(excerpt)).Append("</p>");

        html.Append("<h2>Mods</h2>").Append(ModList(mods)).Append("</section>");

        return new PageMeta
        {
            Description = description,
            Canonical = path,
            OgType = "profile",
            Image = user.Avatar,
            JsonLd = Graph(page),
            Content = html.ToString(),
        };
    }

    public PageMeta Static(string path, string description)
    {
        return new PageMeta { Description = description, Canonical = path };
    }

    public string Sitemap()
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");

        void Url(string path, string? modified = null)
        {
            xml.Append("  <url><loc>").Append(Encode(_options.PublicUrl + path)).Append("</loc>");

            if (DateTimeOffset.TryParse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when))
                xml.Append("<lastmod>").Append(when.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</lastmod>");

            xml.Append("</url>\n");
        }

        List<ModListRow> mods = _db.Query<ModListRow>(ModListRow.BaseQuery + " WHERE " + PublicMods + " ORDER BY m.updated_at DESC").ToList();

        Url("/browse", mods.FirstOrDefault()?.UpdatedAt);

        foreach (string category in Text.Categories)
            Url("/browse?category=" + Uri.EscapeDataString(category), mods.FirstOrDefault(m => m.Category == category)?.UpdatedAt);

        foreach (ModListRow mod in mods)
        {
            Url(ModPath(mod), mod.UpdatedAt);
            Url(ModPath(mod) + "/monkeshield", mod.UpdatedAt);
        }

        // One entry per author with public work, using their newest mod as the change date.
        var authors = _db.Query<(string Id, string? Slug, long Verified, string? Updated, long Banned)>(
            "SELECT u.id,u.custom_slug,u.is_verified,MAX(m.updated_at),u.is_banned FROM mods m JOIN users u ON u.id=m.author_id WHERE " + PublicMods + " GROUP BY u.id");

        foreach (var author in authors.Where(a => a.Banned == 0))
            Url(Text.ProfileUrl(author.Id, author.Slug, author.Verified != 0), author.Updated);

        foreach (string path in new[] { "/software", "/software/launchpad", "/software/cli", "/software/openforge", "/terms", "/privacy" })
            Url(path);

        if (_docs.Find(DocsStore.IndexSlug) == null)
            Url("/docs");

        foreach (DocPage page in _docs.Pages)
            Url(page.Url, page.UpdatedAt.ToString("O"));

        return xml.Append("</urlset>\n").ToString();
    }

    public string Robots()
    {
        // The page scripts read /api, so it must stay crawlable or Google renders empty pages.
        // Downloads are left out so crawlers do not inflate download counts.
        return $"""
            User-agent: *
            Disallow: /api/v1/downloads/
            Disallow: /auth/
            Disallow: /admin
            Disallow: /upload
            Disallow: /settings
            Disallow: /chat
            Disallow: /friends
            Disallow: /groups
            Disallow: /transfers
            Disallow: /notifications

            Sitemap: {_options.PublicUrl}/sitemap.xml

            """;
    }

    private string ModPath(ModListRow row)
    {
        string id = row.IsVerified != 0 && !string.IsNullOrEmpty(row.CustomSlug) ? row.CustomSlug : row.Guid;
        return "/view/" + Uri.EscapeDataString(id);
    }

    private Dictionary<string, object?> Offer(ModListRow row, string url)
    {
        string currency = _options.Currency.ToUpperInvariant();

        if (row.Paid)
            return OfferFor(Money(row.PriceCents), currency, url);

        long? tier = _payments.IsGated(row)
            ? _db.One<long?>("""
                SELECT MIN(t.price_cents) FROM subscription_tiers t WHERE t.archived_at IS NULL
                AND (t.guid=@guid OR EXISTS(SELECT 1 FROM tier_mods tm WHERE tm.tier_id=t.id AND tm.guid=@guid))
                """, new { guid = row.Guid })
            : null;

        if (tier == null)
            return OfferFor(0, currency, url);

        Dictionary<string, object?> offer = OfferFor(Money(tier.Value), currency, url);
        offer["priceSpecification"] = new Dictionary<string, object?>
        {
            ["@type"] = "UnitPriceSpecification",
            ["price"] = Money(tier.Value),
            ["priceCurrency"] = currency,
            ["billingDuration"] = "P1M",
        };
        return offer;
    }

    private static Dictionary<string, object?> OfferFor(decimal price, string currency, string url)
    {
        return new Dictionary<string, object?>
        {
            ["@type"] = "Offer",
            ["price"] = price,
            ["priceCurrency"] = currency,
            ["availability"] = "https://schema.org/InStock",
            ["url"] = url,
        };
    }

    // Matches the client: zero-decimal currencies are stored in whole units.
    private decimal Money(long cents)
    {
        string[] zeroDecimal = ["bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf"];
        return zeroDecimal.Contains(_options.Currency.ToLowerInvariant()) ? cents : cents / 100m;
    }

    private string ModList(List<ModListRow> mods)
    {
        if (mods.Count == 0)
            return "<p class=\"hint\">No mods have been published yet.</p>";

        var html = new StringBuilder("<ul class=\"prerender-list\">");

        foreach (ModListRow mod in mods)
        {
            string excerpt = Excerpt(mod.Description, 120, mod.Name);
            html.Append("<li><a href=\"").Append(Encode(ModPath(mod))).Append("\">").Append(Encode(mod.Name)).Append("</a>");

            if (excerpt != "")
                html.Append("<span class=\"hint\">").Append(Encode(excerpt)).Append("</span>");

            html.Append("</li>");
        }

        return html.Append("</ul>").ToString();
    }

    private static Dictionary<string, object?> Breadcrumbs(params (string Name, string Url)[] items)
    {
        return new Dictionary<string, object?>
        {
            ["@type"] = "BreadcrumbList",
            ["itemListElement"] = items.Select((item, i) => new Dictionary<string, object?>
            {
                ["@type"] = "ListItem",
                ["position"] = i + 1,
                ["name"] = item.Name,
                ["item"] = item.Url,
            }).ToList(),
        };
    }

    // Drops null properties so the JSON-LD only states what is known.
    private static Dictionary<string, object?> Graph(params Dictionary<string, object?>[] nodes)
    {
        return new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@graph"] = nodes.Select(Clean).ToList(),
        };
    }

    private static Dictionary<string, object?> Clean(Dictionary<string, object?> node)
    {
        return node.Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value is Dictionary<string, object?> child ? Clean(child) : p.Value);
    }

    private string? Absolute(string? path)
    {
        return string.IsNullOrEmpty(path) ? null : path.StartsWith('/') ? _options.PublicUrl + path : path;
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    // The first stretch of a Markdown description as one line of plain text.
    // A description that opens by repeating the mod's name as a heading skips it, since the title already says it.
    public static string Excerpt(string markdown, int length = DescriptionLength, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        string plain = Whitespace().Replace(Markdown.ToPlainText(markdown), " ").Trim();

        if (name != null && plain.Length > name.Length && plain.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
            plain = plain[(name.Length + 1)..].TrimStart(' ', ':', '-', '–', '—');

        return Truncate(plain, length);
    }

    private static string Truncate(string text, int length = DescriptionLength)
    {
        if (text.Length <= length)
            return text;

        int cut = text.LastIndexOf(' ', length - 1);
        return text[..(cut > length / 2 ? cut : length - 1)].TrimEnd(',', ';', ':', ' ') + "…";
    }

    private static string Encode(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
