using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OpenForge.Services;

// PBKDF2-HMAC-SHA256 at OWASP's recommended work factor. Stored as "pbkdf2-sha256$iterations$salt$hash" so the cost can rise later.
public static class Passwords
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // Verified against when no account matches, so a missing account takes as long to reject as a wrong password.
    private static readonly Lazy<string> Decoy = new(() => Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        string[] parts = (stored ?? Decoy.Value).Split('$');

        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out int iterations) || iterations < 1)
            return false;

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected) && stored != null;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string? Problem(string password, params string?[] personal)
    {
        int length = password.EnumerateRunes().Count();

        if (length < MinLength || length > MaxLength)
            return $"Use a password of {MinLength} to {MaxLength} characters.";

        foreach (string? value in personal)
        {
            if (!string.IsNullOrEmpty(value) && password.Equals(value, StringComparison.OrdinalIgnoreCase))
                return "Your password can't be your username or email address.";
        }

        return null;
    }
}
