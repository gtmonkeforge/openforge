using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

public static class PageEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new FileExtensionContentTypeProvider();

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Redirect("/browse"));
        app.MapGet("/browse", Browse);

        // Signed-in tools carry noindex; only the public pages below are offered to search engines.
        foreach (string path in new[] { "/upload", "/settings", "/settings/{section}", "/friends", "/chat", "/groups", "/transfers", "/notifications" })
            app.MapGet(path, (Shell shell) => shell.Page(meta: PageMeta.Private));

        // Sign-in pages must be on the public host: passkeys are bound to it, and so is the session cookie.
        foreach (string path in new[] { "/login", "/signup" })
        {
            app.MapGet(path, (HttpRequest request, Shell shell, ForgeOptions options) =>
            {
                string publicHost = new Uri(options.PublicUrl).Authority;

                if (!request.Host.Value!.Equals(publicHost, StringComparison.OrdinalIgnoreCase))
                    return Results.Redirect(options.PublicUrl + request.Path + request.QueryString);

                return shell.Page(path == "/login" ? "Sign in" : "Create an account", PageMeta.Private);
            });
        }

        foreach (string path in new[] { "/verify-email", "/reset-password", "/unsubscribe" })
            app.MapGet(path, (HttpResponse response, Shell shell) =>
            {
                // These URLs carry one-time tokens, which must not leak to other sites through the Referer header.
                response.Headers["Referrer-Policy"] = "no-referrer";
                return shell.Page(meta: PageMeta.Private);
            });

        app.MapGet("/software", (Shell shell, Seo seo, ForgeOptions options) => shell.Page("Software", seo.Static("/software", $"Tools for using {options.BrandName} outside the browser: the Launchpad app, the mforge command line, and the OpenForge server.")));
        app.MapGet("/software/launchpad", (Shell shell, Seo seo, ForgeOptions options) => shell.Page("Launchpad", seo.Static("/software/launchpad", $"Launchpad installs your {options.BrandName} mods and starts {(options.BrandGame == "" ? "the game" : options.BrandGame)}. It runs on Windows and Linux, and it’s open source.")));
        app.MapGet("/software/cli", (Shell shell, Seo seo, ForgeOptions options) => shell.Page("mforge CLI", seo.Static("/software/cli", $"mforge installs and updates {options.BrandName} mods from the command line. It’s written in Go and released under the MIT license.")));
        app.MapGet("/software/openforge", (Shell shell, Seo seo, ForgeOptions options) => shell.Page("OpenForge Server", seo.Static("/software/openforge", $"OpenForge is the open-source server that runs {options.BrandName}, so you can host your own mod site.")));
        app.MapGet("/terms", (Shell shell, Seo seo, ForgeOptions options) => shell.Page("Terms of Service", seo.Static("/terms", $"The terms of service for using {options.BrandName}.")));
        app.MapGet("/privacy", (Shell shell, Seo seo, ForgeOptions options) => shell.Page("Privacy Policy", seo.Static("/privacy", $"How {options.BrandName} collects, uses and protects your data.")));
        app.MapGet("/robots.txt", (Seo seo) => Results.Text(seo.Robots(), "text/plain; charset=utf-8"));
        app.MapGet("/sitemap.xml", (HttpContext http, Seo seo) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(seo.Sitemap(), "application/xml; charset=utf-8");
        });
        app.MapGet("/app", () => Results.Redirect("/software/launchpad"));
        app.MapGet("/get-app", () => Results.Redirect("/software/launchpad"));

        app.MapGet("/view/{guid}", ModPage);
        app.MapGet("/view/{value}/monkeshield", ShieldPage);
        app.MapGet("/profile", MyProfile);
        app.MapGet("/profile/{userId}", ProfilePage);

        app.MapGet("/brand/logo", (ForgeOptions options, IWebHostEnvironment environment) => BrandFile(options.BrandLogo ?? Path.Combine(environment.WebRootPath, "static", "logo.svg")));
        app.MapGet("/brand/favicon", (ForgeOptions options, IWebHostEnvironment environment) => BrandFile(options.BrandFavicon ?? options.BrandLogo ?? Path.Combine(environment.WebRootPath, "static", "favicon.svg")));
        app.MapGet("/app/{**path}", (string path, ForgeOptions options) => ServeFile(options.AppDownloadsDir, path));
        app.MapGet("/cli/{**path}", (string path, ForgeOptions options) => ServeFile(options.CliDir, path));

        app.MapFallback(() =>
        {
            throw new ApiException(404);
        });
    }

    private static void RecordView(HttpContext http, Activity activity, Diagnostics diagnostics, string? guid)
    {
        // Analytics are optional: a failure here must not turn a working page into an error.
        try
        {
            activity.Record("view", guid);
        }
        catch (SqliteException ex)
        {
            diagnostics.Record(http, ex);
        }
    }

    private static IResult Browse(HttpContext http, Shell shell, Seo seo, Activity activity, Diagnostics diagnostics)
    {
        RecordView(http, activity, diagnostics, null);

        // "Your mods" is personal; every other filter combination points search engines at the plain or category listing.
        if (http.Request.Query["mine"] == "true")
            return shell.Page("Your mods", PageMeta.Private);

        string? category = http.Request.Query["category"].FirstOrDefault();
        category = Text.Categories.Contains(category) ? category : null;
        return shell.Page(seo.BrowseTitle(category), seo.Browse(category));
    }

    private static IResult ModPage(string guid, HttpContext http, Access access, Shell shell, Seo seo, Activity activity, Diagnostics diagnostics)
    {
        ModRow? row = access.ResolveMod(guid);

        if (!access.CanViewMod(row))
            throw ApiException.NotFound("This mod could not be found.");

        RecordView(http, activity, diagnostics, row!.Guid);
        ModListRow full = access.ModWithAuthor(row.Guid)!;
        return shell.Page(seo.ModTitle(row.Name), seo.Mod(full));
    }

    private static IResult ShieldPage(string value, HttpContext http, Access access, Channels channels, Db db, Shell shell, Seo seo, Activity activity, Diagnostics diagnostics)
    {
        (ModRow mod, _, _, _) = ToolEndpoints.LookupReport(value, http.Request, access, channels, db);
        http.Response.Headers.CacheControl = "no-store";
        RecordView(http, activity, diagnostics, null);

        return shell.Page("MonkeShield · " + mod.Name, seo.Shield(access.ModWithAuthor(mod.Guid)!));
    }

    private static IResult MyProfile(ForgeContext context)
    {
        return Results.Redirect(context.User?.ProfileUrl ?? "/login?next=/profile");
    }

    private static IResult ProfilePage(string userId, HttpContext http, Access access, Shell shell, Seo seo, Activity activity, Diagnostics diagnostics)
    {
        UserRow row = access.ResolveUser(userId) ?? throw ApiException.NotFound("This profile could not be found.");
        RecordView(http, activity, diagnostics, null);

        return shell.Page(row.PublicName, seo.Profile(row));
    }

    private static IResult BrandFile(string path)
    {
        if (!ContentTypes.TryGetContentType(path, out string? type))
            type = "application/octet-stream";

        return Results.File(path, type, lastModified: File.GetLastWriteTimeUtc(path));
    }

    private static IResult ServeFile(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(fullRoot, path));

        if (!full.StartsWith(fullRoot, StringComparison.Ordinal) || !File.Exists(full))
            throw new ApiException(404);

        if (!ContentTypes.TryGetContentType(full, out string? type))
            type = "application/octet-stream";

        return Results.File(full, type, enableRangeProcessing: true);
    }
}
