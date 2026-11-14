using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

public sealed record GitHubRelease(string Owner, string Repo, JsonObject Data, List<JsonObject> Assets);
public sealed partial class GitHubReleases
{
    public const int AssetLimit = 50 * 1024 * 1024;

    private static readonly HashSet<string> AllowedHosts =
        ["api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"];

    private readonly IHttpClientFactory _http;

    public GitHubReleases(IHttpClientFactory http)
    {
        _http = http;
    }

    [GeneratedRegex(@"\Ahttps://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/releases/(?:tag/([^?#]+)|(latest))/?\z")]
    private static partial Regex ReleaseUrl();

    public async Task<GitHubRelease> ReleaseAsync(JsonNode? url)
    {
        if (!url.IsString(out string value) || value.Length > 2000)
            throw ApiException.BadRequest("Enter a GitHub release URL.");

        Match match = ReleaseUrl().Match(value.Trim());
        string owner = match.Groups[1].Value;
        string repo = match.Groups[2].Value;

        if (!match.Success || owner is "." or ".." || repo is "." or "..")
            throw ApiException.BadRequest("Use https://github.com/owner/repository/releases/tag/version or /releases/latest.");

        string path = match.Groups[4].Success
            ? "latest"
            : "tags/" + Uri.EscapeDataString(Uri.UnescapeDataString(match.Groups[3].Value.TrimEnd('/')));

        JsonObject data;

        try
        {
            byte[] raw = await FetchAsync($"https://api.github.com/repos/{owner}/{repo}/releases/{path}", 2 * 1024 * 1024, false);
            data = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ApiException(502, "GitHub returned an invalid release.");
        }

        List<JsonObject> assets = (data["assets"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(a => a["state"]?.ToString() == "uploaded")
            .Where(a => (a["name"]?.ToString() ?? "").ToLowerInvariant() is var name && (name.EndsWith(".dll") || name.EndsWith(".zip")))
            .Where(a => a["size"] is JsonValue size && size.TryGetValue(out long bytes) && bytes > 0 && bytes <= AssetLimit)
            .ToList();

        return new GitHubRelease(owner, repo, data, assets);
    }

    public async Task<byte[]> FetchAsync(string url, int limit, bool binary)
    {
        HttpClient client = _http.CreateClient("github");
        var target = new Uri(url);

        try
        {
            // Redirects are followed by hand so each hop can be checked against the allowed GitHub hosts.
            for (int hop = 0; hop < 10; hop++)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.Accept.ParseAdd(binary ? "application/octet-stream" : "application/vnd.github+json");
                request.Headers.UserAgent.ParseAdd("MonkeForge");

                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
                {
                    target = new Uri(target, response.Headers.Location);

                    if (target.Scheme != "https" || !AllowedHosts.Contains(target.Host) || target.UserInfo != "" || !target.IsDefaultPort)
                        throw new HttpRequestException("Unsupported GitHub redirect");

                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw ApiException.BadRequest(response.StatusCode == HttpStatusCode.NotFound
                        ? "GitHub release or asset is unavailable. Use a public release URL."
                        : "GitHub could not serve this release. Try again later (GitHub may be rate limiting requests).");
                }

                using Stream stream = await response.Content.ReadAsStreamAsync();
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[81920];
                int read;

                while ((read = await stream.ReadAsync(chunk)) > 0)
                {
                    buffer.Write(chunk, 0, read);

                    if (buffer.Length > limit)
                        throw ApiException.BadRequest("GitHub asset exceeds the size limit.");
                }

                return buffer.ToArray();
            }

            throw new HttpRequestException("Too many redirects");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ApiException(502, "Could not download from GitHub. Try again.");
        }
    }
}
