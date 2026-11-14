using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using OpenForge.Infrastructure;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace OpenForge.Services;

public sealed record StoredDownload(string Id, string StoredName, string Filename, long Size, string Sha256, string Kind);

public sealed class Storage
{
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private const int MaxDownloadBytes = 50 * 1024 * 1024;

    private readonly ForgeOptions _options;
    private readonly Db _db;
    private readonly ILogger<Storage> _logger;

    public Storage(ForgeOptions options, Db db, ILogger<Storage> logger)
    {
        _options = options;
        _db = db;
        _logger = logger;
    }

    public string FilePath(string storedName)
    {
        return Path.Combine(_options.FilesDir, storedName);
    }

    public async Task<string> SaveImageAsync(IFormFile file, List<string> written, bool avatar = false)
    {
        byte[] raw = await ReadLimitedAsync(file, MaxImageBytes);

        if (raw.Length > MaxImageBytes)
            throw ApiException.BadRequest("Each image must be 5 MB or smaller.");

        using SKCodec? codec = SKCodec.Create(new SKMemoryStream(raw));

        if (codec == null)
            throw ApiException.BadRequest("Upload valid PNG, JPEG, or WebP images.");

        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
            throw ApiException.BadRequest("Use PNG, JPEG, or WebP images.");

        if ((long)codec.Info.Width * codec.Info.Height > 20_000_000)
            throw ApiException.BadRequest("Images must be under 20 megapixels.");

        using SKBitmap? decoded = SKBitmap.Decode(codec);

        if (decoded == null)
            throw ApiException.BadRequest("Upload valid PNG, JPEG, or WebP images.");

        using SKBitmap oriented = ApplyOrientation(decoded, codec.EncodedOrigin);
        SKRectI source = new SKRectI(0, 0, oriented.Width, oriented.Height);
        SKSizeI target = new SKSizeI(oriented.Width, oriented.Height);

        if (avatar)
        {
            int edge = Math.Min(oriented.Width, oriented.Height);
            int x = (oriented.Width - edge) / 2;
            int y = (oriented.Height - edge) / 2;
            source = new SKRectI(x, y, x + edge, y + edge);
            target = new SKSizeI(256, 256);
        }
        else if (oriented.Width > 2400 || oriented.Height > 2400)
        {
            double scale = Math.Min(2400.0 / oriented.Width, 2400.0 / oriented.Height);
            target = new SKSizeI(Math.Max(1, (int)(oriented.Width * scale)), Math.Max(1, (int)(oriented.Height * scale)));
        }

        using var surface = SKSurface.Create(new SKImageInfo(target.Width, target.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var paint = new SKPaint();
        surface.Canvas.Clear(SKColors.Transparent);

        using (SKImage sourceImage = SKImage.FromBitmap(oriented))
            surface.Canvas.DrawImage(sourceImage, source, new SKRect(0, 0, target.Width, target.Height), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);

        using SKImage result = surface.Snapshot();
        using SKData encoded = result.Encode(SKEncodedImageFormat.Webp, avatar ? 90 : 88);

        string name = Text.Hex(16) + ".webp";
        string path = FilePath(name);
        await File.WriteAllBytesAsync(path, encoded.ToArray());
        written.Add(path);

        return "/media/" + name;
    }

    private static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return bitmap.Copy();

        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var rotated = new SKBitmap(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height);
        using var canvas = new SKCanvas(rotated);
        canvas.SetMatrix(origin switch
        {
            SKEncodedOrigin.TopRight => SKMatrix.CreateScale(-1, 1, bitmap.Width / 2f, 0),
            SKEncodedOrigin.BottomRight => SKMatrix.CreateRotationDegrees(180, bitmap.Width / 2f, bitmap.Height / 2f),
            SKEncodedOrigin.BottomLeft => SKMatrix.CreateScale(1, -1, 0, bitmap.Height / 2f),
            SKEncodedOrigin.RightTop => SKMatrix.CreateTranslation(bitmap.Height, 0).PreConcat(SKMatrix.CreateRotationDegrees(90)),
            SKEncodedOrigin.LeftBottom => SKMatrix.CreateTranslation(0, bitmap.Width).PreConcat(SKMatrix.CreateRotationDegrees(270)),
            _ => SKMatrix.Identity,
        });

        using (SKImage image = SKImage.FromBitmap(bitmap))
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));

        return rotated;
    }

    public async Task<StoredDownload> SaveDownloadAsync(IFormFile file, List<string> written)
    {
        string name = Text.SecureFilename(file.FileName);
        string kind = name.Split('.').Last().ToLowerInvariant();

        if (kind != "dll" && kind != "zip")
            throw ApiException.BadRequest("Downloads must be DLL or ZIP files.");

        byte[] raw = await ReadLimitedAsync(file, MaxDownloadBytes);

        if (raw.Length == 0 || raw.Length > MaxDownloadBytes)
            throw ApiException.BadRequest("Downloads must be between 1 byte and 50 MB.");

        if (kind == "dll")
            ValidateDll(raw);
        else
            ValidateZip(raw);

        string id = Text.Hex(16);
        string stored = id + "." + kind;
        string path = FilePath(stored);
        await File.WriteAllBytesAsync(path, raw);
        written.Add(path);

        return new StoredDownload(id, stored, name, raw.Length, Convert.ToHexStringLower(SHA256.HashData(raw)), kind);
    }

    public static void DeleteAll(IEnumerable<string> paths)
    {
        foreach (string path in paths)
            File.Delete(path);
    }

    public void DeleteUnreferencedMedia(IEnumerable<string> urls)
    {
        foreach (string url in urls)
        {
            if (!Text.MediaUrl().IsMatch(url))
                continue;

            bool linked = _db.Exists(
                "SELECT 1 FROM mods WHERE logo=@url OR banner=@url OR EXISTS(SELECT 1 FROM json_each(mods.screenshots) WHERE value=@url) UNION ALL SELECT 1 FROM users WHERE avatar=@url LIMIT 1",
                new { url });

            if (linked)
                continue;

            try
            {
                File.Delete(FilePath(url[7..]));
            }
            catch (IOException)
            {
                _logger.LogWarning("Could not remove replaced listing image");
            }
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(IFormFile file, int limit)
    {
        using Stream stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > limit)
                break;
        }

        return buffer.ToArray();
    }

    private static void ValidateDll(byte[] raw)
    {
        int offset = raw.Length >= 64 ? BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(60, 4)) : raw.Length;

        bool valid = raw.Length >= 2 && raw[0] == 'M' && raw[1] == 'Z'
            && offset >= 0 && raw.Length >= offset + 24
            && raw.AsSpan(offset, 4).SequenceEqual("PE\0\0"u8)
            && (BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset + 22, 2)) & 0x2000) != 0;

        if (!valid)
            throw ApiException.BadRequest("The DLL does not have a valid Windows DLL header.");
    }

    private static void ValidateZip(byte[] raw)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(raw), ZipArchiveMode.Read);
            var entries = archive.Entries;

            if (entries.Count == 0 || entries.Count > 2000 || entries.Sum(e => e.Length) > 250L * 1024 * 1024)
                throw ApiException.BadRequest("ZIP must contain 1–2000 entries and expand to at most 250 MB.");

            foreach (ZipArchiveEntry entry in entries)
            {
                string path = entry.FullName.Replace('\\', '/');
                bool symlink = ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

                if (path.StartsWith('/') || path.Split('/').Contains("..") || entry.FullName.Contains(':') || entry.IsEncrypted || symlink)
                    throw ApiException.BadRequest("ZIP cannot contain unsafe paths, symlinks, or encrypted entries.");
            }

            // Reading each entry to the end makes .NET verify its CRC, like zipfile.testzip().
            foreach (ZipArchiveEntry entry in entries)
            {
                using Stream stream = entry.Open();
                stream.CopyTo(Stream.Null);
            }
        }
        catch (InvalidDataException)
        {
            throw ApiException.BadRequest("Upload a valid, unencrypted ZIP archive.");
        }
        catch (NotSupportedException)
        {
            throw ApiException.BadRequest("Upload a valid, unencrypted ZIP archive.");
        }
    }
}
