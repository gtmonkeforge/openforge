using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

// A server with no root administrator is unclaimed: whoever reaches it first could take it over. The setup wizard
// therefore asks for a one-time token that only someone with access to the server's log or files can read.
public sealed class SetupGuard
{
    private readonly ForgeOptions _options;
    private string? _token;

    public SetupGuard(ForgeOptions options, ILogger<SetupGuard> logger)
    {
        _options = options;
        TokenPath = Path.Combine(options.DataDir, "setup-token");

        if (!Required)
            return;

        _token = File.Exists(TokenPath) ? File.ReadAllText(TokenPath).Trim() : null;

        if (string.IsNullOrEmpty(_token) || _token.Length < 20)
        {
            _token = Text.UrlToken(18);
            File.WriteAllText(TokenPath, _token + "\n");

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        logger.LogWarning("""

            ================================================================
             OpenForge isn't set up yet. Open {Url}/setup and enter this
             setup token:  {Token}
             (also saved in {Path})
            ================================================================
            """, options.PublicUrl, _token, TokenPath);
    }

    public string TokenPath { get; }

    public bool Required => _options.RootAdminId == "";

    public bool Check(string? token)
    {
        return Required && _token != null && token != null && Text.SafeEquals(token.Trim(), _token);
    }

    public void Claimed()
    {
        _token = null;

        try
        {
            File.Delete(TokenPath);
        }
        catch (IOException)
        {
        }
    }
}

public static class SetupRedirect
{
    private static readonly string[] Passthrough = ["/setup", "/api", "/static", "/brand", "/media", "/auth"];

    // Until someone claims the server, every page leads to the setup wizard.
    public static void UseSetupRedirect(this WebApplication app, SetupGuard guard)
    {
        app.Use(async (context, next) =>
        {
            PathString path = context.Request.Path;

            if (guard.Required && HttpMethods.IsGet(context.Request.Method) && !Array.Exists(Passthrough, p => path.StartsWithSegments(p)) && path != "/robots.txt")
            {
                context.Response.Redirect("/setup");
                return;
            }

            await next(context);
        });
    }
}
