using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

public sealed class Shell
{
    private readonly string _template;
    private readonly ForgeOptions _options;

    public Shell(IWebHostEnvironment environment, ForgeOptions options)
    {
        _options = options;
        _template = File.ReadAllText(Path.Combine(environment.WebRootPath, "index.html"))
            .Replace("{{brand}}", WebUtility.HtmlEncode(options.BrandName))
            .Replace("{{description}}", WebUtility.HtmlEncode(options.BrandDescription))
            .Replace("{{brandClass}}", options.BrandTintLogo ? "" : "brand-image");
    }

    public async Task WriteAsync(HttpContext context, int status = 200, string? error = null, string? title = null)
    {
        var boot = new { error, status, brand = new { name = _options.BrandName, tint_favicon = _options.BrandLogo == null && _options.BrandFavicon == null } };
        string html = _template
            .Replace("{{title}}", WebUtility.HtmlEncode(title == null ? _options.BrandName : title + " · " + _options.BrandName))
            .Replace("{{boot}}", JsonSerializer.Serialize(boot));

        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    public IResult Page(string? title = null)
    {
        return new ShellResult(this, title);
    }

    private sealed class ShellResult : IResult
    {
        private readonly Shell _shell;
        private readonly string? _title;

        public ShellResult(Shell shell, string? title)
        {
            _shell = shell;
            _title = title;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            return _shell.WriteAsync(httpContext, title: _title);
        }
    }
}
