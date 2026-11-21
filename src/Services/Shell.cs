using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

// What search engines and link previews see before the page's script runs.
public sealed class PageMeta
{
    public string? Description { get; init; }

    // A site path such as /view/my-mod. Leave null for pages that should not be indexed.
    public string? Canonical { get; init; }
    public string? Image { get; init; }
    public string OgType { get; init; } = "website";
    public bool NoIndex { get; init; }

    // Serialized as application/ld+json.
    public object? JsonLd { get; init; }

    // Trusted, already-encoded HTML placed in <main> until the page's script replaces it.
    public string? Content { get; init; }

    public static readonly PageMeta Private = new() { NoIndex = true };
}

public sealed class Shell
{
    private const string Loading = """<div class="page-loading" role="status"><span class="spinner" aria-hidden="true"></span>Loading…</div>""";

    private readonly string _template;
    private readonly ForgeOptions _options;

    public Shell(IWebHostEnvironment environment, ForgeOptions options)
    {
        _options = options;
        _template = File.ReadAllText(Path.Combine(environment.WebRootPath, "index.html"))
            .Replace("{{brand}}", WebUtility.HtmlEncode(options.BrandName))
            .Replace("{{brandClass}}", options.BrandTintLogo ? "" : "brand-image");
    }

    public async Task WriteAsync(HttpContext context, int status = 200, string? error = null, string? title = null, PageMeta? meta = null)
    {
        // Error pages are never worth indexing, whatever the route asked for.
        if (status >= 400)
            meta = PageMeta.Private;

        var boot = new { error, status, brand = new { name = _options.BrandName, game = _options.BrandGame, tint_favicon = _options.BrandLogo == null && _options.BrandFavicon == null } };
        string fullTitle = title == null ? _options.BrandName : title + " · " + _options.BrandName;
        string description = meta?.Description ?? _options.BrandDescription;
        string html = _template
            .Replace("{{title}}", WebUtility.HtmlEncode(fullTitle))
            .Replace("{{description}}", WebUtility.HtmlEncode(description))
            .Replace("{{head}}", Head(fullTitle, description, meta))
            .Replace("{{content}}", meta?.Content ?? Loading)
            .Replace("{{boot}}", JsonSerializer.Serialize(boot));

        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        // Pages name their scripts and styles, so a stale copy would keep loading old code after an update.
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync(html);
    }

    private string Head(string title, string description, PageMeta? meta)
    {
        var head = new StringBuilder();

        void Tag(string format, params string[] values)
        {
            object[] encoded = new object[values.Length];

            for (int i = 0; i < values.Length; i++)
                encoded[i] = WebUtility.HtmlEncode(values[i]);

            head.Append("    ").AppendFormat(format, encoded).Append('\n');
        }

        if (meta == null || meta.NoIndex || meta.Canonical == null)
        {
            Tag("""<meta name="robots" content="noindex">""");
            return head.ToString();
        }

        string url = _options.PublicUrl + meta.Canonical;
        Tag("""<link rel="canonical" href="{0}">""", url);
        Tag("""<meta property="og:site_name" content="{0}">""", _options.BrandName);
        Tag("""<meta property="og:type" content="{0}">""", meta.OgType);
        Tag("""<meta property="og:title" content="{0}">""", title);
        Tag("""<meta property="og:description" content="{0}">""", description);
        Tag("""<meta property="og:url" content="{0}">""", url);

        if (meta.Image != null)
        {
            string image = meta.Image.StartsWith('/') ? _options.PublicUrl + meta.Image : meta.Image;
            Tag("""<meta property="og:image" content="{0}">""", image);
            Tag("""<meta name="twitter:card" content="summary_large_image">""");
        }
        else
        {
            Tag("""<meta name="twitter:card" content="summary">""");
        }

        // The default encoder escapes <, > and &, so the JSON cannot close the script element early.
        if (meta.JsonLd != null)
            head.Append("    <script type=\"application/ld+json\">").Append(JsonSerializer.Serialize(meta.JsonLd)).Append("</script>\n");

        return head.ToString();
    }

    public IResult Page(string? title = null, PageMeta? meta = null)
    {
        return new ShellResult(this, title, meta);
    }

    private sealed class ShellResult : IResult
    {
        private readonly Shell _shell;
        private readonly string? _title;
        private readonly PageMeta? _meta;

        public ShellResult(Shell shell, string? title, PageMeta? meta)
        {
            _shell = shell;
            _title = title;
            _meta = meta;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            return _shell.WriteAsync(httpContext, title: _title, meta: _meta);
        }
    }
}
