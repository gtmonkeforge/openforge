using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenForge.Infrastructure;

public static partial class Text
{
    public const string DefaultAccent = "#dc5c3b";

    public static readonly string[] Capabilities = ["internet", "auto_update", "extensions"];
    public static readonly string[] Categories = ["Gameplay", "Cosmetic", "Utilities", "Accessibility", "Other"];
    public static readonly string[] Loaders = ["bepinex5", "bepinex6", "melonloader"];
    public static readonly string[] Visibilities = ["public", "private", "code"];

    public static readonly HashSet<string> Reserved =
    [
        "admin", "admins", "api", "auth", "browse", "upload", "profile", "users", "view",
        "static", "media", "settings", "root", "cli", "app", "get-app", "software",
    ];

    public static readonly HashSet<string> CountryCodes = """
        AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    // Patterns use \A and \z because .NET's $ also matches before a trailing newline.
    [GeneratedRegex(@"\A[a-z][a-z0-9_-]*(?:\.[a-z][a-z0-9_-]*){2,}\z")]
    public static partial Regex Guid();

    [GeneratedRegex(@"\A[0-9][a-zA-Z0-9.+_-]{0,63}\z")]
    public static partial Regex Version();

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{2,39}\z")]
    public static partial Regex Slug();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{2,31}\z")]
    public static partial Regex PublicUsername();

    [GeneratedRegex(@"\A#[0-9a-fA-F]{6}\z")]
    public static partial Regex HexColor();

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,39}\z")]
    public static partial Regex ChannelName();

    [GeneratedRegex(@"\A(?:[A-Za-z0-9]{25}|[A-Za-z0-9]{5}(?:-[A-Za-z0-9]{5}){4})\z")]
    public static partial Regex ShareCode();

    [GeneratedRegex(@"\A[a-f0-9]{32}\.(png|jpg|webp)\z")]
    public static partial Regex MediaName();

    [GeneratedRegex(@"\A/media/[a-f0-9]{32}\.(png|jpg|webp)\z")]
    public static partial Regex MediaUrl();

    [GeneratedRegex(@"[^A-Za-z0-9_.-]")]
    private static partial Regex UnsafeFilenameChars();
    public static int Len(string value)
    {
        return value.EnumerateRunes().Count();
    }

    public static string NowIso()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
    }

    public static double NowEpoch()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }

    public static string Hex(int bytes)
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
    }

    public static string UrlToken(int bytes)
    {
        return Base64Url(RandomNumberGenerator.GetBytes(bytes));
    }

    public static string Base64Url(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Sha256Hex(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static bool SafeEquals(string a, string b)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }

    public static bool IsWebUrl(string url)
    {
        if (url.Any(char.IsWhiteSpace))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;

        return (uri.Scheme == "http" || uri.Scheme == "https") && uri.Host != "" && uri.UserInfo == "";
    }

    public static string SecureFilename(string filename)
    {
        string ascii = new string(filename.Normalize(NormalizationForm.FormKD).Where(c => c < 128).ToArray());
        ascii = ascii.Replace('/', ' ').Replace('\\', ' ');
        ascii = string.Join("_", ascii.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return UnsafeFilenameChars().Replace(ascii, "").Trim('.', '_');
    }

    public static string? ValidSlug(string value)
    {
        value = value.Trim().ToLowerInvariant();

        if (value != "" && (!Slug().IsMatch(value) || Reserved.Contains(value)))
            throw ApiException.BadRequest("Use 3–40 lowercase letters, digits, or hyphens, starting with a letter. This URL must not be reserved.");

        return value == "" ? null : value;
    }

    public static string ProfileUrl(string id, string? slug, bool verified)
    {
        return "/profile/" + (verified && !string.IsNullOrEmpty(slug) ? slug : id);
    }

    public static string LikeEscape(string value)
    {
        return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
