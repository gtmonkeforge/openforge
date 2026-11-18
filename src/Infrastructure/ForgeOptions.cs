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

    // The public contact details Stripe shows buyers in place of each creator's own. While all of them are set,
    // new creator accounts are pre-filled with them and any account showing something else can't sell.
    public string? SupportEmail { get; set; }
    public string? SupportPhone { get; set; }
    public SupportAddress? SupportAddress { get; set; }

    // The first part of every creator's statement descriptor, such as MONKEFORGE in "MONKEFORGE COOLMOD".
    public string StatementPrefix { get; set; } = "MONKEFORGE";
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

    // The admin panel runs on its own port so it can be kept off the public internet. 0 serves it on the main site instead.
    public int AdminPort { get; set; } = 5265;
    public string AdminBind { get; set; } = "localhost";
    public string AdminUrl { get; set; } = "";

    // The support@ mailbox: tickets arrive over IMAP and replies leave over SMTP, both signed in as the support account.
    public string SupportImapHost { get; set; } = "";
    public int SupportImapPort { get; set; } = 993;
    public string SupportMailUsername { get; set; } = "";
    public string SupportMailPassword { get; set; } = "";
    public string SupportSmtpHost { get; set; } = "";
    public int SupportSmtpPort { get; set; } = 465;
    public string SupportSmtpSecurity { get; set; } = "ssl";
    public string SupportFromName { get; set; } = "";
    public int SupportPollSeconds { get; set; } = 30;

    // MonkeForge's share of paid mod sales. Donations never carry a platform fee.
    public const int PlatformFeePercent = 10;

    public string FilesDir => Path.Combine(DataDir, "files");
    public string RegistryPath => Path.Combine(DataDir, "registry.sqlite3");
    public string DiagnosticsPath => Path.Combine(DataDir, "diagnostics.sqlite3");
    public bool OAuthConfigured => DiscordClientId != "" && DiscordClientSecret != "";
    public bool MailConfigured => SmtpHost != "" && MailFrom != "";
    public bool EmailSignupEnabled => EmailSignup && MailConfigured;
    public bool AdminPortEnabled => AdminPort > 0;
    public bool SupportConfigured => SupportEmail != null && SupportImapHost != "" && SupportMailUsername != "" && SupportMailPassword != "" && SupportSmtpHost != "";
    public bool StripeConfigured => StripeSecretKey != "" && StripeWebhookSecrets.Count > 0;
    public bool PrivacyShieldConfigured => SupportEmail != null && SupportPhone != null && SupportAddress != null;

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

        if (!Text.IsWebUrl(options.PublicUrl))
            throw new InvalidOperationException("PUBLIC_URL must be the site's full http or https address, such as https://monkeforge.org.");
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

        options.SupportEmail = NonEmpty(Get("Support:Email", "SUPPORT_EMAIL"));
        options.SupportPhone = NonEmpty(Get("Support:Phone", "SUPPORT_PHONE"));

        if (options.SupportEmail != null && !options.SupportEmail.Contains('@'))
            throw new InvalidOperationException("SUPPORT_EMAIL must be an email address.");

        if (options.SupportPhone != null && options.SupportPhone.Count(char.IsAsciiDigit) < 7)
            throw new InvalidOperationException("SUPPORT_PHONE must be a phone number, ideally with its country code, such as +15125550123.");

        var address = new SupportAddress
        {
            Line1 = NonEmpty(Get("Support:AddressLine1", "SUPPORT_ADDRESS_LINE1")),
            Line2 = NonEmpty(Get("Support:AddressLine2", "SUPPORT_ADDRESS_LINE2")),
            City = NonEmpty(Get("Support:AddressCity", "SUPPORT_ADDRESS_CITY")),
            State = NonEmpty(Get("Support:AddressState", "SUPPORT_ADDRESS_STATE")),
            PostalCode = NonEmpty(Get("Support:AddressPostalCode", "SUPPORT_ADDRESS_POSTAL_CODE")),
            Country = NonEmpty(Get("Support:AddressCountry", "SUPPORT_ADDRESS_COUNTRY"))?.ToUpperInvariant(),
        };

        if (address.Line1 != null || address.City != null || address.PostalCode != null || address.Country != null)
        {
            if (address.Line1 == null || address.City == null || address.PostalCode == null || address.Country == null)
                throw new InvalidOperationException("Set SUPPORT_ADDRESS_LINE1, SUPPORT_ADDRESS_CITY, SUPPORT_ADDRESS_POSTAL_CODE and SUPPORT_ADDRESS_COUNTRY together.");

            if (address.Country.Length != 2 || !address.Country.All(char.IsAsciiLetterUpper))
                throw new InvalidOperationException("SUPPORT_ADDRESS_COUNTRY must be a two-letter country code such as US.");

            options.SupportAddress = address;
        }

        // Stripe allows Latin letters, digits and spaces here, and the prefix leaves room for the creator's name in 22 characters.
        string? chosenPrefix = NonEmpty(Get("Stripe:StatementPrefix", "STRIPE_STATEMENT_PREFIX"));
        string prefix = new string((chosenPrefix ?? options.BrandName).ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

        if (prefix.Length > 10)
            prefix = prefix[..10];

        if (prefix.Length < 2 || !prefix.Any(char.IsAsciiLetter))
        {
            if (chosenPrefix != null)
                throw new InvalidOperationException("STRIPE_STATEMENT_PREFIX must contain at least two Latin letters or digits, including a letter.");

            // A brand name in another script can't be used on card statements, so it falls back to a neutral word.
            prefix = "MODS";
        }

        options.StatementPrefix = prefix;

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

        string? adminPort = NonEmpty(Get("Admin:Port", "ADMIN_PORT"));
        options.AdminPort = adminPort == null ? options.AdminPort : int.TryParse(adminPort, out int parsedAdmin) && parsedAdmin is >= 0 and < 65536
            ? parsedAdmin : throw new InvalidOperationException("ADMIN_PORT must be a port number, or 0 to serve the admin panel on the main site.");
        options.AdminBind = NonEmpty(Get("Admin:Bind", "ADMIN_BIND")) ?? options.AdminBind;
        options.AdminUrl = (NonEmpty(Get("Admin:Url", "ADMIN_URL")) ?? (options.AdminPortEnabled ? AdminDefaultUrl(options) : options.PublicUrl)).TrimEnd('/');

        if (!Text.IsWebUrl(options.AdminUrl))
            throw new InvalidOperationException("ADMIN_URL must be a full http or https URL, such as https://admin.example.org.");

        options.SupportImapHost = (Get("Support:ImapHost", "SUPPORT_IMAP_HOST") ?? "").Trim();
        options.SupportImapPort = Port(Get("Support:ImapPort", "SUPPORT_IMAP_PORT"), 993, "SUPPORT_IMAP_PORT");
        options.SupportMailUsername = (NonEmpty(Get("Support:Username", "SUPPORT_MAIL_USERNAME")) ?? options.SupportEmail ?? "").Trim();
        options.SupportMailPassword = Get("Support:Password", "SUPPORT_MAIL_PASSWORD") ?? "";
        options.SupportSmtpHost = (NonEmpty(Get("Support:SmtpHost", "SUPPORT_SMTP_HOST")) ?? options.SmtpHost).Trim();
        options.SupportSmtpSecurity = (NonEmpty(Get("Support:SmtpSecurity", "SUPPORT_SMTP_SECURITY")) ?? options.SmtpSecurity).ToLowerInvariant();

        if (options.SupportSmtpSecurity is not ("ssl" or "starttls" or "none"))
            throw new InvalidOperationException("SUPPORT_SMTP_SECURITY must be ssl, starttls or none.");

        options.SupportSmtpPort = Port(Get("Support:SmtpPort", "SUPPORT_SMTP_PORT"), NonEmpty(Get("Support:SmtpHost", "SUPPORT_SMTP_HOST")) == null ? options.SmtpPort : options.SupportSmtpSecurity == "ssl" ? 465 : 587, "SUPPORT_SMTP_PORT");
        options.SupportFromName = NonEmpty(Get("Support:FromName", "SUPPORT_FROM_NAME")) ?? options.BrandName + " Support";

        string? poll = NonEmpty(Get("Support:PollSeconds", "SUPPORT_POLL_SECONDS"));
        options.SupportPollSeconds = poll == null ? 30 : int.TryParse(poll, out int parsedPoll) && parsedPoll >= 10
            ? parsedPoll : throw new InvalidOperationException("SUPPORT_POLL_SECONDS must be at least 10.");

        string? shield = Get("MonkeShieldUrl", "MONKESHIELD_URL");

        if (shield != null)
            options.MonkeShieldUrl = shield.Trim() is "" or "off" or "none" ? null : shield.Trim();

        if (options.MonkeShieldUrl != null && !Text.IsWebUrl(options.MonkeShieldUrl))
            throw new InvalidOperationException("MONKESHIELD_URL must be a full http or https URL, or off.");

        return options;
    }

    private static string AdminDefaultUrl(ForgeOptions options)
    {
        var uri = new Uri(options.PublicUrl);
        string host = options.AdminBind is "localhost" or "127.0.0.1" or "::1" ? "localhost" : uri.Host;
        return $"http://{host}:{options.AdminPort}";
    }

    private static int Port(string? value, int fallback, string name)
    {
        value = NonEmpty(value);
        return value == null ? fallback : int.TryParse(value, out int port) && port is > 0 and < 65536 ? port : throw new InvalidOperationException(name + " must be a port number.");
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

    // The .env file the server reads, which the admin panel edits. Variables set by the real environment (systemd,
    // Docker, the shell) always win over it; variables that came from the file are replaced on every reload.
    public static string DotEnvPath { get; private set; } = Path.Combine(Directory.GetCurrentDirectory(), ".env");

    private static HashSet<string>? _external;
    private static readonly HashSet<string> FromFile = [];

    public static bool IsExternal(string key)
    {
        return _external?.Contains(key) ?? Environment.GetEnvironmentVariable(key) != null;
    }

    public static void LoadDotEnv(string path)
    {
        DotEnvPath = path;
        _external ??= Environment.GetEnvironmentVariables().Keys.Cast<string>().ToHashSet();
        Dictionary<string, string> values = DotEnvFile.Read(path);

        foreach (string key in FromFile.Where(k => !values.ContainsKey(k)).ToList())
        {
            Environment.SetEnvironmentVariable(key, null);
            FromFile.Remove(key);
        }

        foreach ((string key, string value) in values)
        {
            if (_external.Contains(key))
                continue;

            Environment.SetEnvironmentVariable(key, value);
            FromFile.Add(key);
        }
    }
}

public sealed class SupportAddress
{
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
}
