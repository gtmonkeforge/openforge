using Microsoft.AspNetCore.Http;
using OpenForge.Infrastructure;

namespace OpenForge.Services;
public sealed class Activity
{
    private readonly Db _db;
    private readonly ForgeOptions _options;
    private readonly IHttpContextAccessor _http;

    public Activity(Db db, ForgeOptions options, IHttpContextAccessor http)
    {
        _db = db;
        _options = options;
        _http = http;
    }

    public void Record(string kind, string? guid = null)
    {
        string country = "XX";

        if (_options.TrustCountryHeader && _http.HttpContext != null)
        {
            string value = _http.HttpContext.Request.Headers[_options.CountryHeader].ToString().Trim().ToUpperInvariant();

            if (Text.CountryCodes.Contains(value))
                country = value;
        }

        string? category = guid == null ? null : _db.One<string>("SELECT category FROM mods WHERE guid=@guid", new { guid });

        _db.Exec("INSERT INTO activity(kind,guid,category,country,created_at) VALUES(@kind,@guid,@category,@country,@now)",
            new { kind, guid, category, country, now = Text.NowIso() });
    }
}
