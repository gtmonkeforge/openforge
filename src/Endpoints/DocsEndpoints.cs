using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenForge.Infrastructure;
using OpenForge.Services;

namespace OpenForge.Endpoints;

public static class DocsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/docs", Index);
        app.MapGet("/api/v1/docs/pages/{**slug}", Page);
        app.MapPut("/api/v1/docs/pages/{**slug}", SaveAsync);
        app.MapDelete("/api/v1/docs/pages/{**slug}", DeleteAsync);
        app.MapPost("/api/v1/docs/preview", PreviewAsync);

        app.MapGet("/docs", (HttpContext http, ForgeContext context, DocsStore docs, Shell shell) => Shell(http, "", context, docs, shell));
        app.MapGet("/docs/{**path}", (string path, HttpContext http, ForgeContext context, DocsStore docs, Shell shell) => Shell(http, path, context, docs, shell));
    }

    private static bool CanEdit(ForgeContext context)
    {
        return context.User?.IsAdmin == true;
    }

    // Every page's title and text in one response, so the sidebar search can filter without asking the server again.
    private static IResult Index(ForgeContext context, DocsStore docs)
    {
        return Api.Json(new
        {
            can_edit = CanEdit(context),
            pages = docs.Pages.Select(page => new { page.Slug, page.Title, page.Section, page.Url, page.Description, page.Text }),
        });
    }

    private static IResult Page(string slug, ForgeContext context, DocsStore docs)
    {
        DocPage page = docs.Find(DocsStore.NormalizeSlug(slug)) ?? throw ApiException.NotFound("This page could not be found.");
        bool editor = CanEdit(context);

        return Api.Json(new
        {
            page.Slug,
            page.Title,
            page.Section,
            page.Url,
            page.Description,
            page.Html,
            updated_at = page.UpdatedAt.ToString("O"),
            version = editor ? page.Version : null,
            markdown = editor ? page.Markdown : null,
        });
    }

    private static async Task<IResult> SaveAsync(string slug, HttpRequest request, ForgeContext context, DocsStore docs)
    {
        AdminEndpoints.RequireAdmin(context);
        JsonObject body = await JsonBody.RequireObjectAsync(request, "Send the page as JSON.");

        if (!body.HasOnly("markdown", "version") || !body["markdown"].IsString(out string markdown))
            throw ApiException.BadRequest("Send the page's markdown.");

        // No version means the page is new and must not replace one that already exists.
        string? version = body["version"].IsString(out string given) ? given : null;
        string normalized = DocsStore.NormalizeSlug(slug);
        docs.Save(normalized, markdown, version);

        DocPage page = docs.Find(normalized)!;
        return Api.Json(new { page.Slug, page.Url, page.Version });
    }

    private static async Task<IResult> DeleteAsync(string slug, HttpRequest request, ForgeContext context, DocsStore docs)
    {
        AdminEndpoints.RequireAdmin(context);
        JsonObject? body = await JsonBody.ReadObjectAsync(request);
        string? version = body?["version"].IsString(out string given) == true ? given : null;

        docs.Delete(DocsStore.NormalizeSlug(slug), version);
        return Api.Ok();
    }

    private static async Task<IResult> PreviewAsync(HttpRequest request, ForgeContext context, DocsStore docs)
    {
        AdminEndpoints.RequireAdmin(context);
        JsonObject body = await JsonBody.RequireObjectAsync(request, "Send the page as JSON.");

        if (!body["markdown"].IsString(out string markdown) || !body["slug"].IsString(out string slug))
            throw ApiException.BadRequest("Send the page's slug and markdown.");

        if (markdown.Length > DocsStore.MaxBytes)
            throw ApiException.BadRequest("Pages can be up to 512 KB.");

        return Api.Json(new { html = docs.Preview(DocsStore.NormalizeSlug(slug), markdown) });
    }

    private static IResult Shell(HttpContext http, string path, ForgeContext context, DocsStore docs, Shell shell)
    {
        if (path.EndsWith('/') && path.Trim('/') != "")
            return Results.Redirect("/docs/" + path.Trim('/'));

        if (docs.ImagePath(path) is string image)
            return Results.File(image, DocsStore.ImageTypes[Path.GetExtension(image)], lastModified: File.GetLastWriteTimeUtc(image));

        string slug = DocsStore.NormalizeSlug(path);
        DocPage? page = docs.Find(slug);

        if (page == null)
        {
            // The docs home works before anyone has written an index page; it lists what exists instead.
            if (slug == DocsStore.IndexSlug)
                return shell.Page("Docs", new PageMeta { Canonical = "/docs", Description = "Documentation and guides." });

            // Editors open a missing address to start a new page there.
            if (CanEdit(context) && DocsStore.ValidSlug(slug))
                return shell.Page("New page", PageMeta.Private);

            throw ApiException.NotFound("This page could not be found.");
        }

        // The rendered page goes out with the HTML so search engines and link previews see it without running the script.
        string content = $"""<article class="docs-prerender markdown"><h1>{WebUtility.HtmlEncode(page.Title)}</h1>{page.Html}</article>""";
        string title = page.Slug == DocsStore.IndexSlug ? "Docs" : page.Title + " · Docs";

        return shell.Page(title, new PageMeta { Canonical = page.Url, Description = page.Description, OgType = "article", Content = content });
    }
}
