using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

public sealed record ScanFile(string Path, string Name, string Kind);

public sealed class ShieldReportRow
{
    public string Guid { get; set; } = "";
    public string Channel { get; set; } = "";
    public long ReleaseId { get; set; }
    public string Version { get; set; } = "";
    public string Report { get; set; } = "";
}

public sealed class MonkeShield
{
    private const long MaxAssemblyBytes = 50L * 1024 * 1024;
    private const long MaxExpandedBytes = 200L * 1024 * 1024;
    private const int MaxAssemblies = 32;
    private const int MaxReportBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> UnscannableExtensions =
    [
        ".zip", ".7z", ".rar", ".bat", ".cmd", ".ps1", ".sh", ".msi", ".com", ".scr",
        ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".py", ".pyc", ".so", ".dylib", ".jar",
    ];

    private readonly ForgeOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<MonkeShield> _logger;
    private readonly PayloadVault _vault;

    public MonkeShield(ForgeOptions options, IHttpClientFactory http, ILogger<MonkeShield> logger, PayloadVault vault)
    {
        _options = options;
        _http = http;
        _logger = logger;
        _vault = vault;
    }

    public static JsonObject ReadReport(string? raw)
    {
        JsonObject fallback = new JsonObject
        {
            ["status"] = "not_scanned",
            ["eligible"] = false,
            ["artifacts"] = new JsonArray(),
            ["diagnostics"] = new JsonArray("No usable saved scan is available. A backfill scan is required."),
        };

        JsonObject? value = JsonBody.ParseObject(raw);

        if (value == null || !value["status"].IsString(out string status))
            return fallback;

        if (value["artifacts"] is not JsonArray artifacts || value["diagnostics"] is not JsonArray)
            return fallback;

        foreach (JsonNode? artifact in artifacts)
        {
            if (artifact is not JsonObject artifactObject || artifactObject["report"] is not JsonObject report)
                return fallback;

            if (report["checks"] is not (null or JsonArray) || report["diagnostics"] is not (null or JsonArray))
                return fallback;

            foreach (JsonNode? check in report["checks"] as JsonArray ?? [])
            {
                if (check is not JsonObject checkObject || checkObject["evidence"] is not (null or JsonArray))
                    return fallback;
            }
        }

        bool anyIncomplete = artifacts.Any(a => CompleteCounts((JsonObject)a!["report"]!) == null);

        if (status == "complete" && (artifacts.Count == 0 || anyIncomplete))
            return fallback;

        return value;
    }

    public static (int Failed, int Total)? CompleteCounts(JsonObject report)
    {
        if (!report["status"].IsString(out string status) || status != "Complete")
            return null;

        if (report["checks"] is not JsonArray checks || checks.Count == 0)
            return null;

        var ids = new HashSet<string>();
        int failed = 0;

        foreach (JsonNode? check in checks)
        {
            if (check is not JsonObject checkObject || !checkObject["ruleId"].IsString(out string ruleId) || ruleId == "")
                return null;

            if (!ids.Add(ruleId) || !checkObject["status"].IsString(out string checkStatus) || checkStatus is not ("Pass" or "Fail" or "Inconclusive"))
                return null;

            if (checkStatus == "Fail")
                failed++;
        }

        if (!NumberIs(report["totalChecks"], checks.Count) || !NumberIs(report["checksEvaluated"], checks.Count) || !NumberIs(report["failedChecks"], failed))
            return null;

        return (failed, checks.Count);
    }

    public string Prepare(Db db, string guid, string channel, long releaseId, string version)
    {
        string claim = Text.Hex(16);
        var pending = new JsonObject
        {
            ["status"] = "scanning",
            ["eligible"] = false,
            ["artifacts"] = new JsonArray(),
            ["diagnostics"] = new JsonArray(),
            ["scan_started_at"] = Text.NowEpoch(),
            ["_claim"] = claim,
        };

        db.Exec("INSERT OR REPLACE INTO monkeshield_reports VALUES(@guid,@channel,@releaseId,@version,@report)",
            new { guid, channel, releaseId, version, report = pending.ToJsonString() });

        return claim;
    }

    public async Task<JsonObject> FinishAsync(Db db, string guid, string channel, long releaseId, List<ScanFile> files, string? claim = null)
    {
        JsonObject report = await ScanArtifactsAsync(files);

        db.BeginImmediate();

        try
        {
            ShieldReportRow? current = db.One<ShieldReportRow>("SELECT release_id,report FROM monkeshield_reports WHERE guid=@guid AND channel=@channel", new { guid, channel });

            bool stale = current == null || current.ReleaseId != releaseId;
            bool claimedElsewhere = claim != null && current != null && ReadReport(current.Report)["_claim"]?.ToString() != claim;

            // A slower, older scan must never overwrite the report for a newer release.
            if (stale || claimedElsewhere)
            {
                db.Rollback();
                return report;
            }

            db.Exec("UPDATE monkeshield_reports SET report=@report WHERE guid=@guid AND channel=@channel AND release_id=@releaseId",
                new { report = report.ToJsonString(), guid, channel, releaseId });

            long latest = db.Scalar<long>("SELECT MAX(id) FROM releases WHERE guid=@guid AND channel=@channel", new { guid, channel });

            if (report["eligible"]?.GetValueKind() == JsonValueKind.True && releaseId == latest)
            {
                List<string?> latestReports = db.Query<string?>("""
                    SELECT s.report FROM releases r
                    LEFT JOIN monkeshield_reports s ON s.release_id=r.id
                    WHERE r.guid=@guid AND r.id=(SELECT MAX(r2.id) FROM releases r2 WHERE r2.guid=r.guid AND r2.channel=r.channel)
                    """, new { guid }).ToList();

                // Injector mods are usually obfuscated, which hides exactly what static analysis looks for, so a person always approves them.
                if (latestReports.Count > 0 && latestReports.All(r => ReadReport(r)["eligible"]?.GetValueKind() == JsonValueKind.True))
                    db.Exec("UPDATE mods SET status='approved' WHERE guid=@guid AND status='pending' AND uses_injector=0", new { guid });
            }

            db.Commit();
        }
        catch
        {
            db.Rollback();
            throw;
        }

        return report;
    }

    public async Task<Dictionary<string, int>> BackfillAsync(Db db, CancellationToken cancellationToken)
    {
        var summary = new Dictionary<string, int> { ["scanned"] = 0, ["eligible"] = 0, ["review_required"] = 0, ["skipped"] = 0 };

        var releases = db.Query<(long Id, string Guid, string Channel, string Version)>("""
            SELECT r.id,r.guid,r.channel,r.version FROM releases r JOIN mods m ON m.guid=r.guid
            WHERE m.status!='deleted' AND r.id=(SELECT MAX(r2.id) FROM releases r2
                WHERE r2.guid=r.guid AND r2.channel=r.channel) ORDER BY r.id
            """).ToList();

        foreach (var release in releases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string claim;
            List<ScanFile> files;

            db.BeginImmediate();

            try
            {
                long? current = db.One<long?>("""
                    SELECT r.id FROM releases r JOIN mods m ON m.guid=r.guid
                    WHERE r.guid=@Guid AND r.channel=@Channel AND m.status!='deleted' ORDER BY r.id DESC LIMIT 1
                    """, new { release.Guid, release.Channel });

                ShieldReportRow? saved = db.One<ShieldReportRow>("SELECT release_id,report FROM monkeshield_reports WHERE guid=@Guid AND channel=@Channel", new { release.Guid, release.Channel });
                JsonObject report = ReadReport(saved != null && saved.ReleaseId == release.Id ? saved.Report : null);
                string status = report["status"]!.ToString();
                double started = report["scan_started_at"] is JsonValue startedValue && startedValue.TryGetValue(out double s) ? s : 0;
                bool running = status == "scanning" && started > 0 && Text.NowEpoch() - started < 300;

                if (current != release.Id || status == "complete" || running)
                {
                    db.Rollback();
                    summary["skipped"]++;
                    continue;
                }

                claim = Prepare(db, release.Guid, release.Channel, release.Id, release.Version);

                // Legacy public mods wait for a scan instead of staying approved without one.
                db.Exec("UPDATE mods SET status='pending' WHERE guid=@Guid AND visibility='public' AND status='approved'", new { release.Guid });

                files = db.Query<(string StoredName, string Filename, string Kind)>("SELECT stored_name,filename,kind FROM files WHERE release_id=@Id", new { release.Id })
                    .Select(f => new ScanFile(Path.Combine(_options.FilesDir, f.StoredName), f.Filename, f.Kind))
                    .ToList();

                db.Commit();
            }
            catch
            {
                db.Rollback();
                throw;
            }

            JsonObject result = await FinishAsync(db, release.Guid, release.Channel, release.Id, files, claim);
            bool eligible = result["eligible"]?.GetValueKind() == JsonValueKind.True;

            summary["scanned"]++;
            summary[eligible ? "eligible" : "review_required"]++;
        }

        return summary;
    }

    public async Task<JsonObject> ScanArtifactsAsync(List<ScanFile> files)
    {
        var artifacts = new JsonArray();
        var diagnostics = new JsonArray();
        var result = new JsonObject
        {
            ["status"] = "complete",
            ["eligible"] = false,
            ["artifacts"] = artifacts,
            ["diagnostics"] = diagnostics,
            ["threshold_percent"] = 2.5,
            ["scanned_at"] = Text.NowIso(),
        };

        if (_options.MonkeShieldUrl == null)
        {
            result["status"] = "unavailable";
            diagnostics.Add("MonkeShield scanning is not configured.");
            return result;
        }

        if (files.Count == 0)
        {
            result["status"] = "incomplete";
            diagnostics.Add("Release has no stored artifacts. Manual review is required.");
            return result;
        }

        var deadline = Stopwatch.StartNew();
        long expanded = 0;
        int count = 0;

        async Task ScanAsync(byte[] raw, string name)
        {
            count++;
            expanded += raw.Length;

            if (count > MaxAssemblies || raw.Length > MaxAssemblyBytes || expanded > MaxExpandedBytes)
                throw new InvalidDataException("Release exceeds the automatic scan budget; manual review is required.");

            double remaining = 120 - deadline.Elapsed.TotalSeconds;

            if (remaining <= 0)
                throw new InvalidDataException("Release scan timed out; manual review is required.");

            JsonObject report = await ScanAssemblyAsync(raw, name, Math.Min(30, remaining));
            artifacts.Add(new JsonObject { ["name"] = name, ["report"] = report });

            if (CompleteCounts(report) == null)
                result["status"] = "incomplete";
        }

        try
        {
            foreach (ScanFile file in files)
            {
                if (file.Kind == "dll")
                {
                    // Sealed injector binaries are opened in memory only for the scan.
                    byte[] assembly = _vault.ReadStored(file.Path);

                    try
                    {
                        await ScanAsync(assembly, file.Name);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(assembly);
                    }

                    continue;
                }

                using ZipArchive archive = new ZipArchive(new MemoryStream(_vault.ReadStored(file.Path)), ZipArchiveMode.Read);
                int assemblies = 0;

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/'))
                        continue;

                    string suffix = Path.GetExtension(entry.FullName).ToLowerInvariant();

                    if (UnscannableExtensions.Contains(suffix))
                        throw new InvalidDataException("Archive contains nested archives or executable files outside scanner coverage; manual review is required.");

                    if (suffix != ".dll" && suffix != ".exe")
                        continue;

                    if (entry.Length > MaxAssemblyBytes || expanded + entry.Length > MaxExpandedBytes)
                        throw new InvalidDataException("Archive exceeds the automatic scan size budget.");

                    assemblies++;

                    using Stream stream = entry.Open();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    await ScanAsync(buffer.ToArray(), file.Name + "/" + entry.FullName);
                }

                if (assemblies == 0)
                    throw new InvalidDataException("Archive contains no .NET assemblies to scan; manual review is required.");
            }
        }
        catch (InvalidDataException ex)
        {
            result["status"] = "incomplete";
            diagnostics.Add(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or JsonException or ScannerException or CryptographicException)
        {
            // Keep transport errors generic so internal service URLs and paths are not exposed.
            result["status"] = "incomplete";
            diagnostics.Add("MonkeShield could not complete the scan. Manual review is required.");
        }

        var counts = artifacts.Select(a => CompleteCounts((JsonObject)a!["report"]!)).ToList();

        result["eligible"] = result["status"]!.ToString() == "complete"
            && counts.Count > 0
            && counts.All(c => c != null && c.Value.Failed * 40 < c.Value.Total)
            && artifacts.All(a => a!["report"]!["verdict"]?.ToString() == "BelowThreshold");
        result["failed_checks"] = counts.Where(c => c != null).Sum(c => c!.Value.Failed);
        result["total_checks"] = counts.Where(c => c != null).Sum(c => c!.Value.Total);

        return result;
    }

    private async Task<JsonObject> ScanAssemblyAsync(byte[] raw, string name, double timeoutSeconds)
    {
        string url = _options.MonkeShieldUrl!.TrimEnd('/') + "/api/scan?name=" + Uri.EscapeDataString(name);
        HttpClient client = _http.CreateClient("monkeshield");
        var deadline = Stopwatch.StartNew();
        HttpResponseMessage response;

        while (true)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(raw) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            request.Headers.UserAgent.ParseAdd("MonkeForge/1.0");
            request.Headers.Accept.ParseAdd("application/json");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0.1, timeoutSeconds - deadline.Elapsed.TotalSeconds)));
            response = await client.SendAsync(request, timeout.Token);

            // 422 still carries a useful (incomplete) report. 429 means the scanner is busy with another scan.
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.UnprocessableEntity)
                break;

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
                throw new ScannerException();

            response.Dispose();
            double remaining = timeoutSeconds - deadline.Elapsed.TotalSeconds;

            if (remaining <= 0)
                throw new ScannerException();

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(1, remaining)));
        }

        using (response)
        {
            byte[] payload = await response.Content.ReadAsByteArrayAsync();

            if (payload.Length > MaxReportBytes)
                throw new InvalidDataException("Scanner report exceeded the response limit.");

            if (JsonNode.Parse(payload) is not JsonObject report)
                throw new InvalidDataException("Scanner returned an invalid report.");

            string hash = Convert.ToHexStringLower(SHA256.HashData(raw));

            if (report["sha256"]?.ToString() != hash || !NumberIs(report["size"], raw.Length))
                throw new InvalidDataException("Scanner report does not match the submitted bytes.");

            return report;
        }
    }

    private static bool NumberIs(JsonNode? node, long expected)
    {
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.GetValue<double>() == expected;
    }

    private sealed class ScannerException : Exception
    {
    }
}

public sealed class MonkeShieldBackfill : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ForgeOptions _options;
    private readonly ILogger<MonkeShieldBackfill> _logger;

    public MonkeShieldBackfill(IServiceProvider services, ForgeOptions options, ILogger<MonkeShieldBackfill> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.MonkeShieldBackfill || _options.MonkeShieldUrl == null)
            return;

        try
        {
            using IServiceScope scope = _services.CreateScope();
            Db db = scope.ServiceProvider.GetRequiredService<Db>();
            Dictionary<string, int> summary = await scope.ServiceProvider.GetRequiredService<MonkeShield>().BackfillAsync(db, stoppingToken);
            _logger.LogInformation("MonkeShield backfill complete: {Summary}", JsonSerializer.Serialize(summary));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MonkeShield backfill failed; it will retry on the next start");
        }
    }
}
