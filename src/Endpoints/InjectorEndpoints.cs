using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// Delivery API for mods marked as injector mods. See docs/INJECTOR_PROTOCOL.md for the client side.
// These binaries are never served by /api/v1/downloads. A signed-in injector proves it holds a registered
// device key, answers a one-time challenge, and receives the binary encrypted to a key only it can derive.
public static class InjectorEndpoints
{
    private const int MaxDevices = 3;
    private const int MaxDeliveriesPerHour = 30;
    private const double ChallengeSeconds = 60;
    private const string P256Oid = "1.2.840.10045.3.1.7";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/injector/devices", Devices);
        app.MapPost("/api/v1/injector/devices", RegisterDeviceAsync);
        app.MapDelete("/api/v1/injector/devices/{deviceId}", RevokeDevice);
        app.MapPost("/api/v1/injector/challenge", Challenge);
        app.MapPost("/api/v1/injector/payload", PayloadAsync);
    }

    private static IResult Devices(ForgeContext context, Db db)
    {
        CurrentUser user = context.RequireUser("Sign in to manage injector devices.");
        List<InjectorDeviceRow> rows = db.Query<InjectorDeviceRow>(
            "SELECT * FROM injector_devices WHERE user_id=@id AND revoked_at IS NULL ORDER BY created_at DESC", new { id = user.Id }).ToList();

        return Api.Json(new
        {
            max_devices = MaxDevices,
            items = rows.Select(d => new { id = d.Id, name = d.Name, created_at = d.CreatedAt, last_used_at = d.LastUsedAt }),
        });
    }

    private static byte[]? DecodeP256(string base64, bool forSigning)
    {
        byte[] raw;

        try
        {
            raw = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }

        if (raw.Length is < 64 or > 256)
            return null;

        try
        {
            using AsymmetricAlgorithm key = forSigning ? ECDsa.Create() : ECDiffieHellman.Create();
            key.ImportSubjectPublicKeyInfo(raw, out int read);

            ECParameters parameters = key is ECDsa ecdsa ? ecdsa.ExportParameters(false) : ((ECDiffieHellman)key).ExportParameters(false);

            if (read != raw.Length || parameters.Curve.Oid?.Value != P256Oid)
                return null;

            return key.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static async Task<IResult> RegisterDeviceAsync(HttpRequest request, ForgeContext context, Db db)
    {
        CurrentUser user = context.RequireUser("Sign in to register this device.");
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send name and public_key as JSON.");

        if (!payload.HasExactly("name", "public_key") || !payload["name"].IsString(out string name) || !payload["public_key"].IsString(out string key))
            throw ApiException.BadRequest("Send name and public_key as JSON.");

        name = name.Trim();

        if (Text.Len(name) is < 1 or > 60)
            throw ApiException.BadRequest("Device names must be 1–60 characters.");

        byte[] publicKey = DecodeP256(key, true) ?? throw ApiException.BadRequest("public_key must be a Base64 P-256 SubjectPublicKeyInfo.");
        string normalized = Convert.ToBase64String(publicKey);

        db.BeginImmediate();

        if (db.Exists("SELECT 1 FROM injector_devices WHERE public_key=@normalized", new { normalized }))
            throw ApiException.Conflict("This device key is already registered.");

        if (db.Scalar<long>("SELECT COUNT(*) FROM injector_devices WHERE user_id=@id AND revoked_at IS NULL", new { id = user.Id }) >= MaxDevices)
            throw ApiException.Conflict($"You can have up to {MaxDevices} devices. Remove one in Settings first.");

        string id = Text.Hex(16);
        db.Exec("INSERT INTO injector_devices(id,user_id,name,public_key,created_at) VALUES(@id,@uid,@name,@normalized,@now)",
            new { id, uid = user.Id, name, normalized, now = Text.NowIso() });
        db.Commit();

        return Api.Json(new { id, name }, 201);
    }

    private static IResult RevokeDevice(string deviceId, ForgeContext context, Db db)
    {
        CurrentUser user = context.RequireUser("Sign in to manage injector devices.");
        int changed = db.Exec("UPDATE injector_devices SET revoked_at=@now WHERE id=@deviceId AND user_id=@uid AND revoked_at IS NULL",
            new { now = Text.NowIso(), deviceId, uid = user.Id });

        if (changed == 0)
            throw ApiException.NotFound("Device not found.");

        return Api.Ok();
    }

    private static IResult Challenge(ForgeContext context, Db db)
    {
        CurrentUser user = context.RequireUser("Sign in to install mods.");
        string nonce = Text.UrlToken(32);
        double now = Text.NowEpoch();

        db.Exec("DELETE FROM injector_challenges WHERE expires_at<=@now", new { now });
        db.Exec("INSERT INTO injector_challenges VALUES(@hash,@uid,@expires)", new { hash = Text.Sha256Hex(nonce), uid = user.Id, expires = now + ChallengeSeconds });

        return Api.Json(new { nonce, expires_in = (int)ChallengeSeconds });
    }

    public static string SignedMessage(string nonce, string deviceId, string guid, string channel, string loader, string clientKey)
    {
        return string.Join('\n', PayloadVault.DeliveryInfo, nonce, deviceId, guid, channel, loader, clientKey);
    }

    private static async Task<IResult> PayloadAsync(HttpContext http, ForgeContext context, Db db, Access access, Channels channels, Payments payments, Storage storage, PayloadVault vault, Activity activity)
    {
        CurrentUser user = context.RequireUser("Sign in to install mods.");
        JsonObject payload = await JsonBody.RequireObjectAsync(http.Request, "Send the payload request as JSON.");
        string[] fields = ["device_id", "guid", "channel", "loader", "client_key", "nonce", "signature"];

        if (!payload.HasExactly(fields))
            throw ApiException.BadRequest("Send device_id, guid, channel, loader, client_key, nonce and signature.");

        var values = new Dictionary<string, string>();

        foreach (string field in fields)
        {
            if (!payload[field].IsString(out string text) || text.Length > 512)
                throw ApiException.BadRequest("Every payload field must be a short string.");

            values[field] = text;
        }

        if (!Text.Loaders.Contains(values["loader"]))
            throw ApiException.BadRequest("Choose BepInEx 5, BepInEx 6, or MelonLoader.");

        double now = Text.NowEpoch();

        if (db.Scalar<long>("SELECT COUNT(*) FROM injector_deliveries WHERE user_id=@uid AND created>@since", new { uid = user.Id, since = now - 3600 }) >= MaxDeliveriesPerHour)
            throw new ApiException(429, "Too many installs in the last hour. Please wait a little while and try again.");

        // The challenge is consumed first, so a failed or replayed request can never be retried with the same nonce.
        bool fresh = db.One<long?>("DELETE FROM injector_challenges WHERE nonce_hash=@hash AND user_id=@uid AND expires_at>@now RETURNING 1",
            new { hash = Text.Sha256Hex(values["nonce"]), uid = user.Id, now }) != null;

        if (!fresh)
            throw ApiException.Unauthorized("This install request expired. Please try again.");

        InjectorDeviceRow device = db.One<InjectorDeviceRow>("SELECT * FROM injector_devices WHERE id=@id AND user_id=@uid AND revoked_at IS NULL",
            new { id = values["device_id"], uid = user.Id }) ?? throw ApiException.Unauthorized("This device isn't registered to your account. Sign in again on this device.");

        if (!VerifyDevice(device, values))
            throw ApiException.Unauthorized("This device could not be verified. Sign in again on this device.");

        byte[] clientKeyBytes = DecodeP256(values["client_key"], false) ?? throw ApiException.BadRequest("client_key must be a Base64 P-256 SubjectPublicKeyInfo.");

        ModRow mod = access.ResolveMod(values["guid"]) ?? throw ApiException.NotFound("Mod not found.");

        if (!access.CanViewMod(mod))
            throw ApiException.NotFound("Mod not found.");

        if (!mod.Injected)
            throw ApiException.BadRequest("This mod is installed as a normal download, not through the injector.");

        if (!payments.CanUse(mod, user))
            throw new ApiException(402, "Buy this mod before installing it.");

        ChannelDto channel = channels.Select(mod, values["channel"]);
        ReleaseRow release = db.One<ReleaseRow>("SELECT * FROM releases WHERE guid=@guid AND channel=@channel ORDER BY id DESC LIMIT 1",
            new { guid = mod.Guid, channel = channel.Name }) ?? throw ApiException.NotFound("This channel has no releases yet.");

        FileRow file = db.One<FileRow>("""
            SELECT f.* FROM files f LEFT JOIN file_loaders l ON l.file_id=f.id
            WHERE f.release_id=@id AND f.kind='dll' AND COALESCE(l.loader,'bepinex5')=@loader LIMIT 1
            """, new { id = release.Id, loader = values["loader"] }) ?? throw ApiException.NotFound("The latest release has no DLL for this mod loader.");

        byte[] plain = vault.ReadStored(storage.FilePath(file.StoredName));
        byte[] envelope;

        try
        {
            if (Convert.ToHexStringLower(SHA256.HashData(plain)) != file.Sha256)
                throw new InvalidOperationException("Stored payload for file " + file.Id + " does not match its recorded checksum.");

            using ECDiffieHellman client = ECDiffieHellman.Create();
            client.ImportSubjectPublicKeyInfo(clientKeyBytes, out _);

            string aad = string.Join('|', PayloadVault.DeliveryInfo, mod.Guid, release.Version, file.Id, device.Id, values["nonce"]);
            envelope = PayloadVault.EncryptForDevice(plain, client.PublicKey, Encoding.UTF8.GetBytes(aad));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        bool firstInstall = !db.Exists("SELECT 1 FROM injector_deliveries WHERE user_id=@uid AND guid=@guid", new { uid = user.Id, guid = mod.Guid });
        string ip = http.Connection.RemoteIpAddress?.ToString() ?? "";

        db.Exec("INSERT INTO injector_deliveries(user_id,device_id,guid,file_id,version,ip_hash,created) VALUES(@uid,@device,@guid,@file,@version,@ip,@now)",
            new { uid = user.Id, device = device.Id, guid = mod.Guid, file = file.Id, version = release.Version, ip = Text.Sha256Hex("monkeforge-ip|" + ip), now });
        db.Exec("UPDATE injector_devices SET last_used_at=@at WHERE id=@id", new { at = Text.NowIso(), id = device.Id });

        // Injectors fetch on every launch, so only a user's first install counts as a download.
        if (firstInstall)
        {
            db.Exec("UPDATE mods SET downloads=downloads+1 WHERE guid=@guid", new { guid = mod.Guid });
            activity.Record("download", mod.Guid);
        }

        IHeaderDictionary headers = http.Response.Headers;
        headers["X-Forge-Guid"] = mod.Guid;
        headers["X-Forge-Version"] = release.Version;
        headers["X-Forge-File"] = file.Id;
        headers["X-Forge-Sha256"] = file.Sha256;

        return Results.Bytes(envelope, "application/octet-stream");
    }

    private static bool VerifyDevice(InjectorDeviceRow device, Dictionary<string, string> values)
    {
        byte[] signature;

        try
        {
            signature = Convert.FromBase64String(values["signature"]);
        }
        catch (FormatException)
        {
            return false;
        }

        using ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(device.PublicKey), out _);
        byte[] message = Encoding.UTF8.GetBytes(SignedMessage(values["nonce"], device.Id, values["guid"], values["channel"], values["loader"], values["client_key"]));

        // Accept both DER (what most libraries produce) and raw r||s (what Windows CNG and WebCrypto produce).
        return key.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
            || key.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
