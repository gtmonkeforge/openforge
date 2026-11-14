using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenForge.Infrastructure;
using OpenForge.Models;

namespace OpenForge.Services;
public sealed partial class Webhooks
{
    private readonly Db _db;
    private readonly ForgeOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<Webhooks> _logger;

    public Webhooks(Db db, ForgeOptions options, IHttpClientFactory http, ILogger<Webhooks> logger)
    {
        _db = db;
        _options = options;
        _http = http;
        _logger = logger;
    }

    [GeneratedRegex(@"([\\`*_{}\[\]()~|>])")]
    private static partial Regex MarkdownChars();

    public static string DiscordText(string value, int limit)
    {
        value = MarkdownChars().Replace(value, @"\$1");
        return value.Length > limit ? value[..(limit - 1)] + "…" : value;
    }

    public void NotifyModStatus(string guid, string previousStatus, long? releaseId = null)
    {
        if (!_options.DiscordWebhooksEnabled || _options.DiscordWebhookUrl == "")
            return;

        try
        {
            ModRow? mod = _db.One<ModRow>("SELECT * FROM mods WHERE guid=@guid", new { guid });

            if (mod == null)
                return;

            ReleaseRow? release = releaseId == null ? null : _db.One<ReleaseRow>("SELECT * FROM releases WHERE id=@releaseId", new { releaseId });

            if (mod.Status == "pending" && (previousStatus != "pending" || release != null))
            {
                Send("submitted", mod, release);
            }
            else if (mod.Status == "unapproved" && previousStatus != "unapproved")
            {
                Send("rejected", mod, null);
            }
            else if (mod.Status == "approved" && (previousStatus != "approved" || release != null))
            {
                List<ReleaseRow> pending = _db.Query<ReleaseRow>("SELECT * FROM releases WHERE guid=@guid AND id NOT IN (SELECT release_id FROM discord_publications) ORDER BY id", new { guid }).ToList();

                foreach (ReleaseRow published in pending)
                {
                    // Claim before sending so two moderation requests can't announce the same release.
                    if (_db.Exec("INSERT OR IGNORE INTO discord_publications VALUES(@id)", new { id = published.Id }) > 0)
                        Send("published", mod, published);
                }
            }
        }
        catch (Exception)
        {
            // The exception text can contain the secret webhook URL, so it is not logged.
            _logger.LogWarning("Discord webhook delivery failed for mod {Guid}; notification was not retried.", guid);
        }
    }

    private void Send(string kind, ModRow mod, ReleaseRow? release)
    {
        var titles = new Dictionary<string, string> { ["submitted"] = "Mod submitted for approval", ["published"] = "New release published", ["rejected"] = "Mod rejected" };
        var colors = new Dictionary<string, int> { ["submitted"] = 0xf0b232, ["published"] = 0x57f287, ["rejected"] = 0xed4245 };

        string url = _options.PublicUrl + "/view/" + Uri.EscapeDataString(mod.Guid);

        if (release != null && release.Channel != "stable")
            url += "?channel=" + Uri.EscapeDataString(release.Channel);

        var fields = new JsonArray
        {
            new JsonObject { ["name"] = "Mod", ["value"] = DiscordText(mod.Name, 256) },
            new JsonObject { ["name"] = "GUID", ["value"] = DiscordText(mod.Guid, 256) },
            new JsonObject { ["name"] = "Visibility", ["value"] = mod.Visibility, ["inline"] = true },
        };

        if (release != null)
        {
            fields.Add(new JsonObject { ["name"] = "Version", ["value"] = DiscordText(release.Version, 128), ["inline"] = true });
            fields.Add(new JsonObject { ["name"] = "Channel", ["value"] = DiscordText(release.Channel, 128), ["inline"] = true });
        }

        var payload = new JsonObject
        {
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            ["embeds"] = new JsonArray(new JsonObject
            {
                ["title"] = titles[kind],
                ["url"] = url,
                ["color"] = colors[kind],
                ["fields"] = fields,
                ["footer"] = new JsonObject { ["text"] = _options.BrandName + " · " + kind },
            }),
        };

        string endpoint = _options.DiscordWebhookUrl + (_options.DiscordWebhookUrl.Contains('?') ? "&" : "?") + "wait=true";
        HttpClient client = _http.CreateClient("webhooks");
        client.Timeout = TimeSpan.FromSeconds(3);

        using HttpResponseMessage response = client.PostAsJsonAsync(endpoint, payload).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
    }
}
