using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ganss.Xss;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

public sealed class DocPage
{
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Section { get; init; } = "";
    public int Order { get; init; }
    public string Description { get; init; } = "";
    public string Markdown { get; init; } = "";
    public string Html { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTime UpdatedAt { get; init; }

    // Changes whenever the file does, so an editor can tell that someone else saved first.
    public string Version { get; init; } = "";

    public string Url => Slug == DocsStore.IndexSlug ? "/docs" : "/docs/" + Slug;
}

// Documentation pages are Markdown files in the docs folder. The folder is read again whenever a file in it changes,
// so pages can be edited on disk or from the site while the server is running.
public sealed partial class DocsStore
{
    public const string IndexSlug = "index";
    public const int MaxBytes = 512 * 1024;

    // Images next to the docs are served with them. SVG is left out because it can carry script.
    public static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    private readonly MarkdownPipeline _pipeline;
    private readonly HtmlSanitizer _sanitizer;
    private readonly object _lock = new();
    private string? _fingerprint;
    private List<DocPage> _pages = [];

    public string Root { get; }

    public DocsStore(IConfiguration config, IWebHostEnvironment environment)
    {
        string configured = config["DOCS_DIR"] ?? config["Forge:DocsDir"] ?? "docs";
        Root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured.Trim()));

        _pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedSchemes.Add("mailto");
        _sanitizer.AllowedAttributes.Add("id");
        _sanitizer.AllowDataAttributes = false;

        // Heading ids share the page with the site's own elements, so they get a prefix that can't collide with them.
        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not AngleSharp.Dom.IElement element)
                return;

            string? id = element.GetAttribute("id");

            if (!string.IsNullOrEmpty(id))
                element.SetAttribute("id", "doc-" + id);

            string? href = element.GetAttribute("href");

            if (element.TagName == "A" && href != null && href.StartsWith('#') && href.Length > 1)
                element.SetAttribute("href", "#doc-" + href[1..]);
        };
    }

    public IReadOnlyList<DocPage> Pages
    {
        get
        {
            lock (_lock)
            {
                string fingerprint = Fingerprint();

                if (fingerprint != _fingerprint)
                {
                    _pages = Load();
                    _fingerprint = fingerprint;
                }

                return _pages;
            }
        }
    }

    public DocPage? Find(string slug)
    {
        return Pages.FirstOrDefault(page => page.Slug == slug);
    }

    // Lowercase letters, digits and dashes, up to three folders deep: "index", "creators/payouts".
    public static bool ValidSlug(string slug)
    {
        return slug.Length <= 200 && SlugPattern().IsMatch(slug);
    }

    public static string NormalizeSlug(string? path)
    {
        string slug = (path ?? "").Trim('/');

        if (slug.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            slug = slug[..^3];

        return slug == "" ? IndexSlug : slug;
    }

    public string? ImagePath(string path)
    {
        if (!ImageTypes.ContainsKey(Path.GetExtension(path)))
            return null;

        string full = Path.GetFullPath(Path.Combine(Root, path));
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    public void Save(string slug, string markdown, string? expectedVersion)
    {
        if (!ValidSlug(slug))
            throw ApiException.BadRequest("Page addresses use lowercase letters, numbers and dashes, with up to three folders, like creators/payouts.");

        if (Encoding.UTF8.GetByteCount(markdown) > MaxBytes)
            throw ApiException.BadRequest("Pages can be up to 512 KB.");

        string path = FilePath(slug);

        lock (_lock)
        {
            DocPage? current = Find(slug);

            if (expectedVersion != null && (current?.Version ?? "") != expectedVersion)
                throw ApiException.Conflict(current == null ? "This page was deleted while you were editing it." : "Someone else saved this page while you were editing it. Copy your changes, reload, and try again.");

            if (expectedVersion == null && current != null)
                throw ApiException.Conflict("A page already exists at this address.");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write beside the page and swap it in, so a reader never sees half a file.
            string temp = path + ".tmp";
            File.WriteAllText(temp, markdown.Replace("\r\n", "\n"), new UTF8Encoding(false));
            File.Move(temp, path, true);
            _fingerprint = null;
        }
    }

    public void Delete(string slug, string? expectedVersion)
    {
        lock (_lock)
        {
            DocPage page = Find(slug) ?? throw ApiException.NotFound("This page could not be found.");

            if (expectedVersion != null && page.Version != expectedVersion)
                throw ApiException.Conflict("Someone else saved this page while you were editing it. Reload before deleting it.");

            File.Delete(FilePath(slug));
            _fingerprint = null;
        }
    }

    public string Preview(string slug, string markdown)
    {
        return Parse(slug, markdown, DateTime.UtcNow, "").Html;
    }

    private string FilePath(string slug)
    {
        return Path.Combine(Root, slug.Replace('/', Path.DirectorySeparatorChar) + ".md");
    }

    private IEnumerable<FileInfo> Files()
    {
        if (!Directory.Exists(Root))
            return [];

        return new DirectoryInfo(Root).EnumerateFiles("*.md", SearchOption.AllDirectories)
            .Where(file => ValidSlug(SlugOf(file)));
    }

    private string SlugOf(FileInfo file)
    {
        return Path.GetRelativePath(Root, file.FullName)[..^3].Replace(Path.DirectorySeparatorChar, '/');
    }

    private string Fingerprint()
    {
        var builder = new StringBuilder();

        foreach (FileInfo file in Files().OrderBy(f => f.FullName, StringComparer.Ordinal))
            builder.Append(file.FullName).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('|').Append(file.Length).Append('\n');

        return builder.ToString();
    }

    private List<DocPage> Load()
    {
        var pages = new List<DocPage>();

        foreach (FileInfo file in Files())
        {
            try
            {
                string text = File.ReadAllText(file.FullName);
                string version = Text.Sha256Hex(text)[..16];
                pages.Add(Parse(SlugOf(file), text, file.LastWriteTimeUtc, version));
            }
            catch (IOException)
            {
                // A file being written by an editor is picked up on the next request once it's finished.
            }
        }

        return pages.OrderBy(p => p.Slug == IndexSlug ? 0 : 1).ThenBy(p => p.Section, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Order).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private DocPage Parse(string slug, string source, DateTime updated, string version)
    {
        (Dictionary<string, string> front, string body) = FrontMatter(source);
        MarkdownDocument document = Markdig.Markdown.Parse(body, _pipeline);

        // The page shows its title above the content, so a leading "# Title" isn't repeated.
        string? heading = null;

        if (document.FirstOrDefault() is HeadingBlock { Level: 1 } first)
        {
            heading = Inline(first.Inline);
            document.Remove(first);
        }

        foreach (LinkInline link in document.Descendants<LinkInline>())
            link.Url = Rewrite(slug, link.Url, link.IsImage);

        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.Render(document);

        string plain = Regex.Replace(Markdig.Markdown.ToPlainText(body, _pipeline), @"\s+", " ").Trim();
        string leaf = slug.Split('/')[^1];
        string title = front.GetValueOrDefault("title") ?? heading ?? (slug == IndexSlug ? "Documentation" : Pretty(leaf));
        string section = front.GetValueOrDefault("section") ?? (slug.Contains('/') ? Pretty(slug.Split('/')[0]) : "");
        string description = front.GetValueOrDefault("description") ?? Truncate(plain, 160);

        return new DocPage
        {
            Slug = slug,
            Title = title,
            Section = section,
            Order = int.TryParse(front.GetValueOrDefault("order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int order) ? order : 100,
            Description = description,
            Markdown = source,
            Html = _sanitizer.Sanitize(writer.ToString()).Trim(),
            Text = plain,
            UpdatedAt = updated,
            Version = version,
        };
    }

    // Links written for GitHub or an editor ("../creators/payouts.md", "shot.png") are pointed at their place under /docs.
    private static string? Rewrite(string slug, string? url, bool image)
    {
        if (string.IsNullOrEmpty(url) || url.StartsWith('/') || url.StartsWith('#') || url.Contains(':'))
            return url;

        int split = url.IndexOfAny(['#', '?']);
        string path = split < 0 ? url : url[..split];
        string rest = split < 0 ? "" : url[split..];
        bool markdown = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

        if (!markdown && !image)
            return url;

        var parts = slug.Split('/').SkipLast(1).ToList();

        foreach (string part in path.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (part != "." && part != "")
            {
                parts.Add(part);
            }
        }

        string target = string.Join('/', parts);

        if (markdown)
        {
            target = target[..^3];
            target = target == IndexSlug ? "" : target;

            if (rest.StartsWith('#'))
                rest = "#doc-" + rest[1..];
        }

        return "/docs" + (target == "" ? "" : "/" + target) + rest;
    }

    private static (Dictionary<string, string>, string) FrontMatter(string source)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string text = source.Replace("\r\n", "\n").TrimStart('﻿');

        if (!text.StartsWith("---\n"))
            return (values, text);

        int end = text.IndexOf("\n---", 4, StringComparison.Ordinal);

        if (end < 0)
            return (values, text);

        foreach (string line in text[4..end].Split('\n'))
        {
            int colon = line.IndexOf(':');

            if (colon > 0)
                values[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"', '\'');
        }

        int bodyStart = text.IndexOf('\n', end + 1);
        return (values, bodyStart < 0 ? "" : text[(bodyStart + 1)..]);
    }

    private static string Inline(ContainerInline? inline)
    {
        if (inline == null)
            return "";

        var builder = new StringBuilder();

        foreach (Markdig.Syntax.Inlines.Inline child in inline.Descendants())
        {
            if (child is LiteralInline literal)
                builder.Append(literal.Content.ToString());
            else if (child is CodeInline code)
                builder.Append(code.Content);
        }

        return builder.ToString().Trim();
    }

    private static string Truncate(string text, int length)
    {
        if (text.Length <= length)
            return text;

        int space = text.LastIndexOf(' ', length - 1);
        return text[..(space > length / 2 ? space : length - 1)].TrimEnd(',', '.', ';', ':') + "…";
    }

    private static string Pretty(string name)
    {
        string spaced = name.Replace('-', ' ');
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*(/[a-z0-9][a-z0-9-]*){0,3}$")]
    private static partial Regex SlugPattern();
}
