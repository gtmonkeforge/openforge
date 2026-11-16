using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

public static class SessionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/status", () => "ooo ooo aaa aaa");
        app.MapGet("/api/v2/version", Version);
        app.MapGet("/api/v1/health", () => Api.Json(new { status = "ok", api_version = 1 }));
        app.MapGet("/api/v1/session", Session);
        app.MapPut("/api/v1/settings", SaveSettingsAsync);
        app.MapPost("/api/v1/logout", Logout);
        app.MapPost("/api/v1/markdown-preview", MarkdownPreviewAsync);
    }

    private static IResult Version()
    {
        // The SDK appends "+<commit>" to the informational version when building from a git checkout.
        string informational = typeof(SessionEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = informational.IndexOf('+');
        string version = plus < 0 ? informational : informational[..plus];
        string? commit = plus < 0 ? null : informational[(plus + 1)..];

        if (commit != null && commit.Length > 12)
            commit = commit[..12];

        return Api.Json(new
        {
            openforge = version,
            commit,
            dotnet = Environment.Version.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            license = new { name = "GNU Lesser General Public License v3.0", spdx = "LGPL-3.0", url = "https://www.gnu.org/licenses/lgpl-3.0.html" },
        });
    }

    private static IResult Session(ForgeContext context, ForgeOptions options)
    {
        return Api.Json(new SessionDto(context.User?.ToDto(), context.Csrf(), options.OAuthConfigured, options.EmailSignupEnabled, options.MailConfigured));
    }

    private static async Task<IResult> SaveSettingsAsync(HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser current = context.RequireUser("Sign in to save settings to your account.");
        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload.HasExactly("accent_color") || !payload["accent_color"].IsString(out string color) || !Text.HexColor().IsMatch(color))
            throw ApiException.BadRequest("Choose an accent color in #RRGGBB format.");

        color = color.ToLowerInvariant();
        db.Exec("UPDATE users SET accent_color=@color,accent_customized=@customized WHERE id=@id",
            new { color, customized = color != Text.DefaultAccent ? 1 : 0, id = current.Id });

        return Api.Json(new { accent_color = color });
    }

    private static IResult Logout(ForgeContext context)
    {
        context.Clear();
        return Api.Ok();
    }

    private static async Task<IResult> MarkdownPreviewAsync(HttpRequest request, ForgeContext context, MarkdownRenderer markdown)
    {
        if (context.User == null)
            throw new ApiException(401);

        JsonObject? payload = await JsonBody.ReadObjectAsync(request);

        if (payload == null || !payload["text"].IsString(out string text) || Text.Len(text) > 30000)
            throw new ApiException(400);

        return Api.Json(new { html = markdown.Render(text) });
    }
}
