using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Infrastructure;

public sealed class ApiKeyAllowedAttribute : Attribute
{
    
}

public sealed class CsrfExemptAttribute : Attribute
{
    
}

public static class Pipeline
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' blob: data:; style-src 'self'; script-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";

    public static bool IsApi(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/api");
    }

    public static void UseForgeErrors(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                IHeaderDictionary headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers.ContentSecurityPolicy = ContentSecurityPolicy;

                if (!headers.ContainsKey("Referrer-Policy"))
                    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

                if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/auth"))
                    headers.CacheControl = "no-store";

                return Task.CompletedTask;
            });

            try
            {
                await next(context);

                if (context.Response.StatusCode == 405 && !context.Response.HasStarted && context.Response.ContentLength == null)
                    throw new ApiException(405);
            }
            catch (Exception ex) when (!context.Response.HasStarted)
            {
                await HandleAsync(context, ex);
            }
        });
    }

    public static void UseForgeSession(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            SessionStore store = context.RequestServices.GetRequiredService<SessionStore>();
            ForgeContext forge = context.RequestServices.GetRequiredService<ForgeContext>();
            bool hadCookie = context.Request.Cookies.ContainsKey(SessionStore.CookieName);
            forge.Session = store.Load(context.Request);

            context.Response.OnStarting(() =>
            {
                if (!forge.Session.IsEmpty || hadCookie)
                    store.Save(context.Response, forge.Session);

                return Task.CompletedTask;
            });

            await next(context);
        });
    }

    public static void UseForgeAuth(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            ForgeContext forge = context.RequestServices.GetRequiredService<ForgeContext>();
            Endpoint? endpoint = context.GetEndpoint();
            bool apiAuthenticated = AuthenticateApiKey(context, forge, endpoint);
            CurrentUser? current = forge.User;

            if (current != null && current.IsBanned && !current.IsRoot)
            {
                forge.Clear();
                throw ApiException.Forbidden("Your account is banned from this site.");
            }

            if (current != null && forge.Session.UserId != null && !apiAuthenticated)
                forge.Session.Permanent = true;

            bool unsafeMethod = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
                || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
            bool exempt = endpoint?.Metadata.GetMetadata<CsrfExemptAttribute>() != null;

            if (unsafeMethod && !apiAuthenticated && !exempt)
            {
                string supplied = context.Request.Headers["X-CSRF-Token"].ToString();

                if (forge.Session.Csrf == null || !Text.SafeEquals(supplied, forge.Session.Csrf))
                    throw ApiException.Forbidden("Refresh the page and try again. The security token is missing or expired.");
            }

            await next(context);
        });
    }

    private static bool AuthenticateApiKey(HttpContext context, ForgeContext forge, Endpoint? endpoint)
    {
        if (!context.Request.Headers.TryGetValue("Authorization", out var header))
            return false;

        if (endpoint?.Metadata.GetMetadata<ApiKeyAllowedAttribute>() == null || !HttpMethods.IsPost(context.Request.Method))
            throw ApiException.Forbidden("API keys may only publish releases.");

        string authorization = header.ToString();
        int space = authorization.IndexOf(' ');
        string scheme = space < 0 ? authorization : authorization[..space];
        string token = space < 0 ? "" : authorization[(space + 1)..];

        if (!scheme.Equals("bearer", StringComparison.OrdinalIgnoreCase) || token == "" || token.Length > 200)
            throw ApiException.Unauthorized("Use Authorization: Bearer YOUR_API_KEY.");

        Db db = context.RequestServices.GetRequiredService<Db>();
        ApiKeyRow? key = db.One<ApiKeyRow>("SELECT * FROM api_keys WHERE token_hash=@hash AND revoked_at IS NULL", new { hash = Text.Sha256Hex(token) });

        if (key == null)
            throw ApiException.Unauthorized("Invalid or revoked API key.");

        forge.ApiUserId = key.UserId;
        CurrentUser? current = forge.User;

        if (current == null || (current.IsBanned && !current.IsRoot))
            throw ApiException.Forbidden("This account cannot publish releases.");

        db.Exec("UPDATE api_keys SET last_used_at=@now WHERE id=@id", new { now = Text.NowIso(), id = key.Id });
        return true;
    }

    private static async Task HandleAsync(HttpContext context, Exception exception)
    {
        ApiException? known = exception switch
        {
            ApiException api => api,
            BadHttpRequestException bad => new ApiException(bad.StatusCode),
            InvalidDataException => new ApiException(400),
            _ => null,
        };

        context.Response.Clear();

        if (known != null)
        {
            await WriteErrorAsync(context, known.Status, known.Message, null);
            return;
        }

        string reference = context.RequestServices.GetRequiredService<Diagnostics>().Record(context, exception);
        context.Response.Headers.CacheControl = "no-store";
        await WriteErrorAsync(context, 500, "The server could not complete this request. Error reference: " + reference, reference);
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string message, string? reference)
    {
        if (IsApi(context.Request))
        {
            context.Response.StatusCode = status;
            object body = reference == null ? new { error = message } : new { error = message, error_id = reference };
            await context.Response.WriteAsJsonAsync(body);
            return;
        }

        await context.RequestServices.GetRequiredService<Shell>().WriteAsync(context, status, message);
    }
}
