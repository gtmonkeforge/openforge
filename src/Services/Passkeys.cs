using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

public sealed class PasskeyRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public byte[] PublicKey { get; set; } = [];
    public long Algorithm { get; set; }
    public long SignCount { get; set; }
    public string Transports { get; set; } = "[]";
    public string CreatedAt { get; set; } = "";
    public string? LastUsedAt { get; set; }
}

public sealed record AuthenticatorData(byte[] RpIdHash, byte Flags, uint SignCount, string? CredentialId)
{
    public bool UserPresent => (Flags & 0x01) != 0;
    public bool UserVerified => (Flags & 0x04) != 0;
}

// WebAuthn relying-party checks for passkeys. Attestation is "none", so the browser's getPublicKey() (SPKI) is trusted for the key,
// exactly as it would be if we decoded the COSE key ourselves: without attestation there's nothing to verify it against.
public sealed class Passkeys
{
    // COSE algorithm IDs. Ed25519 (-8) is left out because .NET has no built-in verifier for it; authenticators all offer ES256.
    public const int ES256 = -7;
    public const int RS256 = -257;

    public const double ChallengeSeconds = 300;

    private readonly ForgeOptions _options;

    public Passkeys(ForgeOptions options)
    {
        _options = options;
    }

    public string RpId => new Uri(_options.PublicUrl).Host;
    public string Origin => new Uri(_options.PublicUrl).GetLeftPart(UriPartial.Authority);

    public static byte[] FromBase64Url(string value)
    {
        if (value.Length > 8192)
            throw ApiException.BadRequest("The passkey response is too large.");

        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);

        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            throw ApiException.BadRequest("The passkey response is not valid base64url.");
        }
    }

    // Checks clientDataJSON: the ceremony type, our challenge, and that the browser was on our origin.
    public byte[] CheckClientData(string clientDataB64, string type, string expectedChallenge)
    {
        byte[] raw = FromBase64Url(clientDataB64);
        JsonObject? data;

        try
        {
            data = JsonNode.Parse(raw) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            data = null;
        }

        if (data == null || !data["type"].IsString(out string actualType) || !data["challenge"].IsString(out string challenge) || !data["origin"].IsString(out string origin))
            throw ApiException.BadRequest("The passkey response is malformed.");

        if (actualType != type || !Text.SafeEquals(challenge, expectedChallenge))
            throw ApiException.BadRequest("This passkey request expired. Try again.");

        if (!string.Equals(origin, Origin, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest($"Passkeys only work on {Origin}.");

        return raw;
    }

    public AuthenticatorData ParseAuthenticatorData(byte[] data, bool expectCredential)
    {
        // rpIdHash (32) + flags (1) + signCount (4), then attested credential data when the AT flag is set.
        if (data.Length < 37)
            throw ApiException.BadRequest("The passkey response is malformed.");

        byte[] rpIdHash = data[..32];
        byte flags = data[32];
        uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(33, 4));
        string? credentialId = null;

        if (!CryptographicOperations.FixedTimeEquals(rpIdHash, SHA256.HashData(Encoding.UTF8.GetBytes(RpId))))
            throw ApiException.BadRequest($"This passkey belongs to a different site than {RpId}.");

        if ((flags & 0x01) == 0)
            throw ApiException.BadRequest("Confirm the passkey prompt to continue.");

        if (expectCredential)
        {
            // aaguid (16) + credentialIdLength (2) + credentialId.
            if ((flags & 0x40) == 0 || data.Length < 55)
                throw ApiException.BadRequest("The authenticator didn't return a new credential.");

            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(53, 2));

            if (length is < 16 or > 1023 || data.Length < 55 + length)
                throw ApiException.BadRequest("The passkey response is malformed.");

            credentialId = Text.Base64Url(data[55..(55 + length)]);
        }

        return new AuthenticatorData(rpIdHash, flags, count, credentialId);
    }

    // Validates the SPKI public key the browser reported and returns it, so we only ever store keys we can verify with.
    public static byte[] CheckPublicKey(byte[] spki, long algorithm)
    {
        try
        {
            if (algorithm == ES256)
            {
                using ECDsa key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(spki, out _);

                if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                    throw ApiException.BadRequest("ES256 passkeys must use the P-256 curve.");

                return spki;
            }

            if (algorithm == RS256)
            {
                using RSA key = RSA.Create();
                key.ImportSubjectPublicKeyInfo(spki, out _);

                if (key.KeySize < 2048)
                    throw ApiException.BadRequest("RSA passkeys must be at least 2048 bits.");

                return spki;
            }
        }
        catch (CryptographicException)
        {
            throw ApiException.BadRequest("The passkey's public key could not be read.");
        }

        throw ApiException.BadRequest("This authenticator uses a signature algorithm that isn't supported. Try another passkey provider.");
    }

    public static bool VerifySignature(PasskeyRow key, byte[] authenticatorData, byte[] clientData, byte[] signature)
    {
        byte[] signed = [.. authenticatorData, .. SHA256.HashData(clientData)];

        try
        {
            if (key.Algorithm == ES256)
            {
                using ECDsa ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(key.PublicKey, out _);
                return ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }

            if (key.Algorithm == RS256)
            {
                using RSA rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(key.PublicKey, out _);
                return rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }
        catch (CryptographicException)
        {
        }

        return false;
    }
}
