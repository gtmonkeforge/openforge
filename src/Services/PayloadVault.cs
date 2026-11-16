using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

// Injector mod binaries never sit on disk or travel in the clear.
// At rest each file is sealed with AES-256-GCM under a server key, bound to its stored name.
// For delivery it is re-encrypted to a one-time key agreed with the injector's ephemeral ECDH key,
// so a captured response is useless without the private key that only the requesting injector holds.
public sealed class PayloadVault
{
    public const string SealedSuffix = ".sealed";
    public const string DeliveryInfo = "monkeforge-inject-v1";

    private static readonly byte[] SealedMagic = "MFS1"u8.ToArray();
    private static readonly byte[] DeliveryMagic = "MFI1"u8.ToArray();
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _key;

    public PayloadVault(ForgeOptions options)
    {
        _key = LoadKey(options);
    }

    private static byte[] LoadKey(ForgeOptions options)
    {
        if (options.PayloadKey != null)
        {
            byte[] configured;

            try
            {
                configured = Convert.FromBase64String(options.PayloadKey);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("PAYLOAD_KEY must be 32 random bytes encoded as Base64.");
            }

            if (configured.Length != 32)
                throw new InvalidOperationException("PAYLOAD_KEY must be 32 random bytes encoded as Base64.");

            return configured;
        }

        string directory = Path.Combine(options.DataDir, "keys");
        string path = Path.Combine(directory, "payload.key");
        Directory.CreateDirectory(directory);

        if (File.Exists(path))
        {
            byte[] saved = File.ReadAllBytes(path);

            if (saved.Length != 32)
                throw new InvalidOperationException($"The payload key at \"{path}\" is damaged. Restore it from a backup; sealed mods cannot be opened without it.");

            return saved;
        }

        byte[] key = RandomNumberGenerator.GetBytes(32);
        var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

        if (!OperatingSystem.IsWindows())
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(path, create))
            stream.Write(key);

        return key;
    }

    public static bool IsSealed(string storedName)
    {
        return storedName.EndsWith(SealedSuffix, StringComparison.Ordinal);
    }

    public byte[] Seal(byte[] plaintext, string storedName)
    {
        byte[] output = new byte[SealedMagic.Length + NonceBytes + TagBytes + plaintext.Length];
        SealedMagic.CopyTo(output, 0);
        Span<byte> nonce = output.AsSpan(SealedMagic.Length, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plaintext, output.AsSpan(SealedMagic.Length + NonceBytes + TagBytes), output.AsSpan(SealedMagic.Length + NonceBytes, TagBytes), SealedAad(storedName));

        return output;
    }

    public byte[] Open(byte[] sealedBytes, string storedName)
    {
        int header = SealedMagic.Length + NonceBytes + TagBytes;

        if (sealedBytes.Length < header || !sealedBytes.AsSpan(0, SealedMagic.Length).SequenceEqual(SealedMagic))
            throw new CryptographicException("The sealed payload is damaged.");

        byte[] plaintext = new byte[sealedBytes.Length - header];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(sealedBytes.AsSpan(SealedMagic.Length, NonceBytes), sealedBytes.AsSpan(header), sealedBytes.AsSpan(SealedMagic.Length + NonceBytes, TagBytes), plaintext, SealedAad(storedName));

        return plaintext;
    }

    // Reads a stored file, opening it first when it is sealed. Callers must zero the result once done.
    public byte[] ReadStored(string path)
    {
        string name = Path.GetFileName(path);
        byte[] raw = File.ReadAllBytes(path);

        if (!IsSealed(name))
            return raw;

        try
        {
            return Open(raw, name);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    // The AAD binds a sealed file to its own stored name, so swapping files on disk is detected.
    private static byte[] SealedAad(string storedName)
    {
        string bare = IsSealed(storedName) ? storedName[..^SealedSuffix.Length] : storedName;
        return Encoding.UTF8.GetBytes("monkeforge-sealed-v1|" + bare);
    }

    // Envelope layout (all lengths in bytes):
    //   "MFI1" | u16 big-endian key length | server ephemeral P-256 public key (SubjectPublicKeyInfo DER)
    //   | salt (32) | nonce (12) | tag (16) | ciphertext
    // key = HKDF-SHA256(ECDH(server ephemeral, client ephemeral), salt, info "monkeforge-inject-v1"), 32 bytes.
    public static byte[] EncryptForDevice(byte[] plaintext, ECDiffieHellmanPublicKey clientKey, byte[] aad)
    {
        using ECDiffieHellman server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] serverPublic = server.ExportSubjectPublicKeyInfo();
        byte[] shared = server.DeriveRawSecretAgreement(clientKey);
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Encoding.ASCII.GetBytes(DeliveryInfo));

        try
        {
            int header = DeliveryMagic.Length + 2 + serverPublic.Length + salt.Length + NonceBytes + TagBytes;
            byte[] output = new byte[header + plaintext.Length];
            int offset = 0;

            DeliveryMagic.CopyTo(output, offset);
            offset += DeliveryMagic.Length;
            output[offset++] = (byte)(serverPublic.Length >> 8);
            output[offset++] = (byte)serverPublic.Length;
            serverPublic.CopyTo(output, offset);
            offset += serverPublic.Length;
            salt.CopyTo(output, offset);
            offset += salt.Length;

            Span<byte> nonce = output.AsSpan(offset, NonceBytes);
            RandomNumberGenerator.Fill(nonce);
            offset += NonceBytes;

            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, output.AsSpan(header), output.AsSpan(offset, TagBytes), aad);

            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
