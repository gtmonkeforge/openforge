using System;
using System.IO;
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

        foreach (string path in new[] { "/upload", "/settings", "/settings/{section}", "/friends", "/chat", "/groups", "/transfers", "/notifications", "/software" })
            app.MapGet(path, (Shell shell) => shell.Page());

        app.MapGet("/software/launchpad", (Shell shell) => shell.Page("Launchpad"));
        app.MapGet("/software/cli", (Shell shell) => shell.Page("mforge CLI"));
        app.MapGet("/software/openforge", (Shell shell) => shell.Page("OpenForge Server"));
        app.MapGet("/terms", (Shell shell) => shell.Page("Terms of Service"));
        app.MapGet("/privacy", (Shell shell) => shell.Page("Privacy Policy"));
        app.MapGet("/app", () => Results.Redirect("/software/launchpad"));
        app.MapGet("/get-app", () => Results.Redirect("/software/launchpad"));

        app.MapGet("/view/{guid}", ModPage);
        app.MapGet("/view/{value}/monkeshield", ShieldPage);
        app.MapGet("/profile", MyProfile);
        app.MapGet("/profile/{userId}", ProfilePage);
        app.MapGet("/admin", (ForgeContext context, Shell shell) =>
        {
            AdminEndpoints.RequireAdmin(context);
            return shell.Page("Admin");
        });

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

    private static IResult Browse(HttpContext http, Shell shell, Activity activity, Diagnostics diagnostics)
    {
        RecordView(http, activity, diagnostics, null);
        return shell.Page("Browse");
    }

    private static IResult ModPage(string guid, HttpContext http, Access access, Shell shell, Activity activity, Diagnostics diagnostics)
    {
        ModRow? row = access.ResolveMod(guid);

        if (!access.CanViewMod(row))
            throw ApiException.NotFound("This mod could not be found.");

        RecordView(http, activity, diagnostics, row!.Guid);
        return shell.Page(row.Name);
    }

    private static IResult ShieldPage(string value, HttpContext http, Access access, Channels channels, Db db, Shell shell, Activity activity, Diagnostics diagnostics)
    {
        (ModRow mod, _, _, _) = ToolEndpoints.LookupReport(value, http.Request, access, channels, db);
        http.Response.Headers.CacheControl = "no-store";
        RecordView(http, activity, diagnostics, null);

        return shell.Page("MonkeShield · " + mod.Name);
    }

    private static IResult MyProfile(ForgeContext context)
    {
        return Results.Redirect(context.User?.ProfileUrl ?? "/auth/discord?next=/profile");
    }

    private static IResult ProfilePage(string userId, HttpContext http, Access access, Shell shell, Activity activity, Diagnostics diagnostics)
    {
        UserRow row = access.ResolveUser(userId) ?? throw ApiException.NotFound("This profile could not be found.");
        RecordView(http, activity, diagnostics, null);

        return shell.Page(row.PublicName);
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
