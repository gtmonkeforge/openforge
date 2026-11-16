using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace OpenForge.Infrastructure;

public sealed class ForgeOptions
{
    public const long MaxRequestBytes = 420L * 1024 * 1024;

    public string DataDir { get; set; } = "data";
    public string PublicUrl { get; set; } = "http://localhost:5264";
    public string RootAdminId { get; set; } = "";
    public bool CookieSecure { get; set; }
    public bool TrustCountryHeader { get; set; } = true;
    public string CountryHeader { get; set; } = "CF-IPCountry";
    public string DiscordClientId { get; set; } = "";
    public string DiscordClientSecret { get; set; } = "";
    public string? MonkeShieldUrl { get; set; } = "https://shield.monkeforge.org";
    public bool MonkeShieldBackfill { get; set; } = true;
    public string DiscordWebhookUrl { get; set; } = "";
    public bool DiscordWebhooksEnabled { get; set; }
    public string AppDownloadsDir { get; set; } = "";
    public string CliDir { get; set; } = "";
    public string BrandName { get; set; } = "MonkeForge";
    public string BrandDescription { get; set; } = "MonkeForge is an open Gorilla Tag plugin marketplace.";

    // The game this instance hosts mods for, used in page titles and search descriptions. Empty leaves it out.
    public string BrandGame { get; set; } = "Gorilla Tag";
    public string? BrandLogo { get; set; }
    public string? BrandFavicon { get; set; }
    public bool BrandTintLogo { get; set; } = true;
    public string StripeSecretKey { get; set; } = "";
    public List<string> StripeWebhookSecrets { get; set; } = [];
    public string Currency { get; set; } = "usd";
    public string? PayloadKey { get; set; }

    // A PNG or JPEG for the top of Stripe Checkout. Stripe can't show the SVG logo, so without this checkout shows the brand name only.
    public string? StripeBrandLogoUrl { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;

    // "ssl" connects with TLS from the start (port 465); "starttls" upgrades a plain connection (port 587).
    public string SmtpSecurity { get; set; } = "ssl";
    public string SmtpUsername { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string MailFrom { get; set; } = "";
    public string MailFromName { get; set; } = "";
    public int MailPerMinute { get; set; } = 120;
    public bool EmailSignup { get; set; } = true;

    // MonkeForge's share of paid mod sales. Donations never carry a platform fee.
    public const int PlatformFeePercent = 10;

    public string FilesDir => Path.Combine(DataDir, "files");
    public string RegistryPath => Path.Combine(DataDir, "registry.sqlite3");
    public string DiagnosticsPath => Path.Combine(DataDir, "diagnostics.sqlite3");
    public bool OAuthConfigured => DiscordClientId != "" && DiscordClientSecret != "";
    public bool MailConfigured => SmtpHost != "" && MailFrom != "";
    public bool EmailSignupEnabled => EmailSignup && MailConfigured;
    public bool StripeConfigured => StripeSecretKey != "" && StripeWebhookSecrets.Count > 0;

    public static ForgeOptions Load(IConfiguration config, string contentRoot)
    {
        IConfigurationSection section = config.GetSection("Forge");

        // The MonkeForge variable names win so a .env or server environment overrides appsettings.json defaults.
        string? Get(string key, string env)
        {
            return config[env] ?? section[key];
        }

        bool Flag(string key, string env, bool fallback)
        {
            string? value = Get(key, env);
            return value == null ? fallback : value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        var options = new ForgeOptions();
        options.DataDir = Path.GetFullPath(Path.Combine(contentRoot, Get("DataDir", "DATA_DIR") ?? options.DataDir));
        options.PublicUrl = (Get("PublicUrl", "PUBLIC_URL") ?? options.PublicUrl).TrimEnd('/');
        options.RootAdminId = (Get("RootAdminId", "ROOT_ADMIN_ID") ?? "").Trim();
        options.CookieSecure = Flag("CookieSecure", "COOKIE_SECURE", false);
        options.TrustCountryHeader = Flag("TrustCountryHeader", "TRUST_COUNTRY_HEADER", true);
        options.CountryHeader = Get("CountryHeader", "COUNTRY_HEADER") ?? options.CountryHeader;
        options.DiscordClientId = Get("DiscordClientId", "DISCORD_CLIENT_ID") ?? "";
        options.DiscordClientSecret = Get("DiscordClientSecret", "DISCORD_CLIENT_SECRET") ?? "";
        options.MonkeShieldBackfill = Flag("MonkeShieldBackfill", "MONKESHIELD_BACKFILL", true);
        options.DiscordWebhookUrl = Get("DiscordWebhookUrl", "DISCORD_WEBHOOK_URL") ?? "";
        options.DiscordWebhooksEnabled = Flag("DiscordWebhooksEnabled", "DISCORD_WEBHOOKS_ENABLED", false);
        options.AppDownloadsDir = Path.GetFullPath(Path.Combine(contentRoot, Get("AppDownloadsDir", "APP_DOWNLOADS_DIR") ?? Path.Combine(options.DataDir, "app")));
        options.CliDir = Path.GetFullPath(Path.Combine(contentRoot, Get("CliDir", "CLI_DIR") ?? "cli"));

        options.BrandName = NonEmpty(Get("Brand:Name", "BRAND_NAME")) ?? options.BrandName;
        options.BrandDescription = NonEmpty(Get("Brand:Description", "BRAND_DESCRIPTION")) ?? options.BrandDescription;
        options.BrandGame = (Get("Brand:Game", "BRAND_GAME") ?? options.BrandGame).Trim();
        options.BrandLogo = BrandImage(contentRoot, Get("Brand:Logo", "BRAND_LOGO"), "logo");
        options.BrandFavicon = BrandImage(contentRoot, Get("Brand:Favicon", "BRAND_FAVICON"), "favicon");

        // The built-in logo is a white silhouette tinted with the accent color, while custom logos are shown as drawn unless asked otherwise.
        options.BrandTintLogo = Flag("Brand:TintLogo", "BRAND_TINT_LOGO", options.BrandLogo == null);

        options.StripeSecretKey = (Get("Stripe:SecretKey", "STRIPE_SECRET_KEY") ?? "").Trim();

        // The platform endpoint and the Connect endpoint each have their own signing secret, so both are accepted.
        options.StripeWebhookSecrets = new[] { Get("Stripe:WebhookSecret", "STRIPE_WEBHOOK_SECRET"), Get("Stripe:ConnectWebhookSecret", "STRIPE_CONNECT_WEBHOOK_SECRET") }
            .Select(NonEmpty).OfType<string>().ToList();

        string currency = (NonEmpty(Get("Stripe:Currency", "STRIPE_CURRENCY")) ?? "usd").ToLowerInvariant();

        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterLower))
            throw new InvalidOperationException("STRIPE_CURRENCY must be a three-letter ISO currency code such as usd.");

        options.Currency = currency;
        options.PayloadKey = NonEmpty(Get("PayloadKey", "PAYLOAD_KEY"));
        options.StripeBrandLogoUrl = NonEmpty(Get("Stripe:BrandLogoUrl", "STRIPE_BRAND_LOGO_URL"));

        if (options.StripeBrandLogoUrl != null && !Text.IsWebUrl(options.StripeBrandLogoUrl))
            throw new InvalidOperationException("STRIPE_BRAND_LOGO_URL must be a full http or https URL to a PNG or JPEG image.");

        // A custom PNG or JPEG brand logo is served at /brand/logo, which Stripe can show directly.
        if (options.StripeBrandLogoUrl == null && options.BrandLogo != null && Path.GetExtension(options.BrandLogo).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
            options.StripeBrandLogoUrl = options.PublicUrl + "/brand/logo";

        options.SmtpHost = (Get("Mail:SmtpHost", "SMTP_HOST") ?? "").Trim();
        options.SmtpUsername = (Get("Mail:SmtpUsername", "SMTP_USERNAME") ?? "").Trim();
        options.SmtpPassword = Get("Mail:SmtpPassword", "SMTP_PASSWORD") ?? "";
        options.SmtpSecurity = (NonEmpty(Get("Mail:SmtpSecurity", "SMTP_SECURITY")) ?? "ssl").ToLowerInvariant();

        if (options.SmtpSecurity is not ("ssl" or "starttls" or "none"))
            throw new InvalidOperationException("SMTP_SECURITY must be ssl, starttls or none.");

        string? port = NonEmpty(Get("Mail:SmtpPort", "SMTP_PORT"));
        options.SmtpPort = port == null ? (options.SmtpSecurity == "ssl" ? 465 : 587) : int.TryParse(port, out int parsedPort) && parsedPort is > 0 and < 65536
            ? parsedPort : throw new InvalidOperationException("SMTP_PORT must be a port number.");

        options.MailFrom = (NonEmpty(Get("Mail:From", "MAIL_FROM")) ?? (options.SmtpUsername.Contains('@') ? options.SmtpUsername : "")).Trim();
        options.MailFromName = NonEmpty(Get("Mail:FromName", "MAIL_FROM_NAME")) ?? options.BrandName;

        string? rate = NonEmpty(Get("Mail:PerMinute", "MAIL_PER_MINUTE"));
        options.MailPerMinute = rate == null ? 120 : int.TryParse(rate, out int parsedRate) && parsedRate > 0
            ? parsedRate : throw new InvalidOperationException("MAIL_PER_MINUTE must be a positive number.");

        options.EmailSignup = Flag("Mail:Signup", "EMAIL_SIGNUP", true);

        string? shield = Get("MonkeShieldUrl", "MONKESHIELD_URL");

        if (shield != null)
            options.MonkeShieldUrl = shield.Trim() == "" ? null : shield.Trim();

        return options;
    }

    private static string? NonEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? BrandImage(string contentRoot, string? value, string name)
    {
        if (NonEmpty(value) == null)
            return null;

        string path = Path.GetFullPath(Path.Combine(contentRoot, value!.Trim()));
        string extension = Path.GetExtension(path).ToLowerInvariant();

        if (!File.Exists(path))
            throw new InvalidOperationException($"The brand {name} file \"{path}\" does not exist.");

        if (extension is not (".svg" or ".png" or ".webp" or ".jpg" or ".jpeg" or ".gif" or ".ico"))
            throw new InvalidOperationException($"The brand {name} must be an SVG, PNG, WebP, JPEG, GIF or ICO file.");

        return path;
    }

    public static void LoadDotEnv(string path)
    {
        if (!File.Exists(path))
            return;

        foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();

            if (trimmed == "" || trimmed.StartsWith('#') || !trimmed.Contains('='))
                continue;

            int index = trimmed.IndexOf('=');
            string key = trimmed[..index].Trim();
            string value = trimmed[(index + 1)..].Trim().Trim('"', '\'');

            if (Environment.GetEnvironmentVariable(key) == null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
