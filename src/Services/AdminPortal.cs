using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

// The admin panel listens on its own port (ADMIN_PORT) so it can be firewalled, tunnelled or put behind a VPN on its own.
// That port serves the panel and the few public APIs it reads from; the public port refuses every admin API.
public static class AdminPortal
{
    private static readonly string[] AdminOnlyPrefixes = ["/admin", "/api/v1/admin"];

    // What the panel itself needs on the admin port. Reads of mods, docs and media let reviewers see a submission as users would.
    private static readonly string[] AdminPortPrefixes =
    [
        "/admin", "/api/v1/admin", "/api/v1/session", "/api/v1/logout", "/api/v1/markdown-preview",
        "/static", "/brand", "/media",
    ];

    private static readonly string[] AdminPortReadPrefixes = ["/api/v1/mods", "/api/v1/docs", "/api/v1/downloads", "/api/v1/users"];

    public static bool IsAdminPort(HttpContext context, ForgeOptions options)
    {
        return options.AdminPortEnabled && context.Connection.LocalPort == options.AdminPort;
    }

    private static bool Starts(PathString path, string[] prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (path.StartsWithSegments(prefix))
                return true;
        }

        return false;
    }

    public static void UseAdminPort(this WebApplication app, ForgeOptions options)
    {
        if (!options.AdminPortEnabled)
            return;

        app.Use(async (context, next) =>
        {
            PathString path = context.Request.Path;

            if (IsAdminPort(context, options))
            {
                if (path == "/" || path == "")
                {
                    context.Response.Redirect("/admin");
                    return;
                }

                bool read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);

                if (!Starts(path, AdminPortPrefixes) && !(read && Starts(path, AdminPortReadPrefixes)))
                    throw new ApiException(404);
            }
            else if (Starts(path, AdminOnlyPrefixes) && path != "/admin")
            {
                // /admin itself stays on the public site: it hands the signed-in admin over to the admin port.
                throw new ApiException(404);
            }

            await next(context);
        });
    }
}

public sealed class AdminShell
{
    private readonly string _template;
    private readonly ForgeOptions _options;

    public AdminShell(IWebHostEnvironment environment, ForgeOptions options)
    {
        _options = options;
        _template = File.ReadAllText(Path.Combine(environment.WebRootPath, "admin.html"))
            .Replace("{{brand}}", WebUtility.HtmlEncode(options.BrandName));
    }

    public IResult Page()
    {
        var boot = new
        {
            brand = new { name = _options.BrandName, game = _options.BrandGame, tint_favicon = _options.BrandLogo == null && _options.BrandFavicon == null },
            public_url = _options.PublicUrl,
            admin_url = _options.AdminUrl,
            support_email = _options.SupportEmail,
            support_configured = _options.SupportConfigured,
            mail_configured = _options.MailConfigured,
            stripe_configured = _options.StripeConfigured,
        };

        string html = _template.Replace("{{boot}}", JsonSerializer.Serialize(boot));
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
