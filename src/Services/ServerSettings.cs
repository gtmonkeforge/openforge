using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

public sealed record SettingDef(
    string Key, string Group, string Label, string Help = "", string Type = "text", string? Section = null,
    string? Default = null, string[]? Choices = null, bool Secret = false, bool Danger = false);

// Lets the panel restart the server in-process: Program.cs builds a fresh app when the old one stops for this reason.
public static class ServerControl
{
    private static volatile bool _restart;

    public static DateTime StartedAt { get; private set; } = DateTime.UtcNow;

    public static void MarkStarted()
    {
        StartedAt = DateTime.UtcNow;
    }

    public static void Restart(IHostApplicationLifetime lifetime)
    {
        _restart = true;

        // Let the response that asked for the restart finish first.
        _ = Task.Run(async () =>
        {
            await Task.Delay(600);
            lifetime.StopApplication();
        });
    }

    public static bool ConsumeRestart()
    {
        bool value = _restart;
        _restart = false;
        return value;
    }
}

public sealed class ServerSettings
{
    public static readonly string[] Groups = ["Site", "Sign-in", "Email", "Support desk", "Admin panel", "Payments", "Integrations", "Server"];

    public static readonly SettingDef[] All =
    [
        new("PUBLIC_URL", "Site", "Public URL", "The address people use, such as https://monkeforge.org. Passkeys, Discord sign-in, emails and links all use it.", "url", "PublicUrl", "http://localhost:5264"),
        new("BRAND_NAME", "Site", "Site name", "Shown in the header, page titles and emails.", Section: "Brand:Name", Default: "MonkeForge"),
        new("BRAND_DESCRIPTION", "Site", "Description", "Used for search engines and link previews.", "textarea", "Brand:Description", "MonkeForge is an open Gorilla Tag plugin marketplace."),
        new("BRAND_GAME", "Site", "Game", "The game this site hosts mods for. Leave empty to leave it out of titles.", Section: "Brand:Game", Default: "Gorilla Tag"),
        new("BRAND_LOGO", "Site", "Logo file", "Path to an SVG, PNG, WebP, JPEG, GIF or ICO, relative to the server folder.", Section: "Brand:Logo"),
        new("BRAND_FAVICON", "Site", "Favicon file", "Defaults to the logo.", Section: "Brand:Favicon"),
        new("BRAND_TINT_LOGO", "Site", "Tint the logo with the accent colour", "On by default for the built-in logo, off for custom logos.", "bool", "Brand:TintLogo"),

        new("ROOT_ADMIN_ID", "Sign-in", "Root administrator", "User ID of the one account that can change settings, the database and other admins.", Section: "RootAdminId", Danger: true),
        new("EMAIL_SIGNUP", "Sign-in", "Allow email sign-up", "Lets people create accounts with an email address and password. Needs outgoing email.", "bool", "Mail:Signup", "true"),
        new("DISCORD_CLIENT_ID", "Sign-in", "Discord client ID", "From the Discord Developer Portal → your application → OAuth2.", Section: "DiscordClientId"),
        new("DISCORD_CLIENT_SECRET", "Sign-in", "Discord client secret", "Shown once in the Discord Developer Portal. Reset it there if lost.", "password", "DiscordClientSecret", Secret: true),
        new("COOKIE_SECURE", "Sign-in", "Secure cookies", "Only send the sign-in cookie over HTTPS. Turn on when the site is served over HTTPS.", "bool", "CookieSecure", "false"),

        new("SMTP_HOST", "Email", "SMTP server", "For Purelymail: smtp.purelymail.com", Section: "Mail:SmtpHost"),
        new("SMTP_PORT", "Email", "SMTP port", "465 for SSL, 587 for STARTTLS.", "number", "Mail:SmtpPort"),
        new("SMTP_SECURITY", "Email", "Connection security", "", "select", "Mail:SmtpSecurity", "ssl", ["ssl", "starttls", "none"]),
        new("SMTP_USERNAME", "Email", "SMTP username", "Usually the full sending address.", Section: "Mail:SmtpUsername"),
        new("SMTP_PASSWORD", "Email", "SMTP password", "", "password", "Mail:SmtpPassword", Secret: true),
        new("MAIL_FROM", "Email", "From address", "Defaults to the SMTP username when that's an address.", "email", "Mail:From"),
        new("MAIL_FROM_NAME", "Email", "From name", "Defaults to the site name.", Section: "Mail:FromName"),
        new("MAIL_PER_MINUTE", "Email", "Emails per minute", "How fast the outbox sends. Check your provider's limits.", "number", "Mail:PerMinute", "120"),

        new("SUPPORT_EMAIL", "Support desk", "Support address", "Tickets arrive here, replies come from here, and Stripe shows it to buyers.", "email", "Support:Email"),
        new("SUPPORT_IMAP_HOST", "Support desk", "IMAP server", "For Purelymail: imap.purelymail.com", Section: "Support:ImapHost"),
        new("SUPPORT_IMAP_PORT", "Support desk", "IMAP port", "", "number", "Support:ImapPort", "993"),
        new("SUPPORT_MAIL_USERNAME", "Support desk", "Mailbox username", "Defaults to the support address.", Section: "Support:Username"),
        new("SUPPORT_MAIL_PASSWORD", "Support desk", "Mailbox password", "", "password", "Support:Password", Secret: true),
        new("SUPPORT_SMTP_HOST", "Support desk", "SMTP server for replies", "Defaults to the main SMTP server.", Section: "Support:SmtpHost"),
        new("SUPPORT_SMTP_PORT", "Support desk", "SMTP port for replies", "", "number", "Support:SmtpPort"),
        new("SUPPORT_SMTP_SECURITY", "Support desk", "Reply connection security", "Defaults to the main SMTP setting.", "select", "Support:SmtpSecurity", null, ["", "ssl", "starttls", "none"]),
        new("SUPPORT_FROM_NAME", "Support desk", "Reply from name", "Defaults to “<site name> Support”.", Section: "Support:FromName"),
        new("SUPPORT_POLL_SECONDS", "Support desk", "Check mail every (seconds)", "At least 10.", "number", "Support:PollSeconds", "30"),
        new("SUPPORT_PHONE", "Support desk", "Support phone", "Shown to buyers by Stripe instead of creators' own numbers. Include the country code.", Section: "Support:Phone"),

        new("ADMIN_PORT", "Admin panel", "Admin port", "The panel's own port. 0 serves it on the main site at /admin instead.", "number", "Admin:Port", "5265", Danger: true),
        new("ADMIN_BIND", "Admin panel", "Listen on", "localhost keeps the panel off the network (reach it over a tunnel or proxy). * listens everywhere.", Section: "Admin:Bind", Default: "localhost", Danger: true),
        new("ADMIN_URL", "Admin panel", "Admin URL", "The panel's address as admins' browsers see it, such as https://admin.monkeforge.org.", "url", "Admin:Url"),

        new("STRIPE_SECRET_KEY", "Payments", "Stripe secret key", "sk_live_… or sk_test_…", "password", "Stripe:SecretKey", Secret: true),
        new("STRIPE_WEBHOOK_SECRET", "Payments", "Webhook signing secret", "For the platform endpoint at <public URL>/api/v1/stripe/webhook.", "password", "Stripe:WebhookSecret", Secret: true),
        new("STRIPE_CONNECT_WEBHOOK_SECRET", "Payments", "Connect webhook signing secret", "For the Connect endpoint at the same URL.", "password", "Stripe:ConnectWebhookSecret", Secret: true),
        new("STRIPE_CURRENCY", "Payments", "Currency", "Three-letter ISO code.", Section: "Stripe:Currency", Default: "usd"),
        new("STRIPE_BRAND_LOGO_URL", "Payments", "Checkout logo URL", "A PNG or JPEG, since Stripe can't show SVG.", "url", "Stripe:BrandLogoUrl"),
        new("STRIPE_STATEMENT_PREFIX", "Payments", "Statement prefix", "First part of card statement descriptors, such as MONKEFORGE.", Section: "Stripe:StatementPrefix"),
        new("SUPPORT_ADDRESS_LINE1", "Payments", "Support address line 1", "The public address Stripe shows buyers instead of creators' own.", Section: "Support:AddressLine1"),
        new("SUPPORT_ADDRESS_LINE2", "Payments", "Address line 2", "", Section: "Support:AddressLine2"),
        new("SUPPORT_ADDRESS_CITY", "Payments", "City", "", Section: "Support:AddressCity"),
        new("SUPPORT_ADDRESS_STATE", "Payments", "State or region", "", Section: "Support:AddressState"),
        new("SUPPORT_ADDRESS_POSTAL_CODE", "Payments", "Postal code", "", Section: "Support:AddressPostalCode"),
        new("SUPPORT_ADDRESS_COUNTRY", "Payments", "Country", "Two-letter code, such as US.", Section: "Support:AddressCountry"),

        new("MONKESHIELD_URL", "Integrations", "MonkeShield scanner URL", "Set to off to stop scanning uploads.", Section: "MonkeShieldUrl", Default: "https://shield.monkeforge.org"),
        new("MONKESHIELD_BACKFILL", "Integrations", "Scan older releases", "Scan releases that were uploaded before MonkeShield was set up.", "bool", "MonkeShieldBackfill", "true"),
        new("DISCORD_WEBHOOKS_ENABLED", "Integrations", "Post releases to Discord", "", "bool", "DiscordWebhooksEnabled", "false"),
        new("DISCORD_WEBHOOK_URL", "Integrations", "Discord webhook URL", "", "password", "DiscordWebhookUrl", Secret: true),
        new("TRUST_COUNTRY_HEADER", "Integrations", "Trust the country header", "Use a proxy's country header (such as Cloudflare's) for analytics.", "bool", "TrustCountryHeader", "true"),
        new("COUNTRY_HEADER", "Integrations", "Country header name", "", Section: "CountryHeader", Default: "CF-IPCountry"),

        new("ASPNETCORE_URLS", "Server", "Listen URLs", "Where the public site listens, separated by semicolons, such as http://localhost:5264. Usually behind a reverse proxy.", Default: "http://localhost:5000", Danger: true),
        new("DATA_DIR", "Server", "Data folder", "Database, uploads and keys. Moving it doesn't move the files: copy them first.", Section: "DataDir", Default: "data", Danger: true),
        new("DOCS_DIR", "Server", "Docs folder", "Markdown files for /docs.", Section: "DocsDir", Default: "docs"),
        new("APP_DOWNLOADS_DIR", "Server", "Launchpad downloads folder", "Defaults to <data folder>/app.", Section: "AppDownloadsDir"),
        new("CLI_DIR", "Server", "CLI files folder", "", Section: "CliDir", Default: "cli"),
        new("PAYLOAD_KEY", "Server", "Payload key", "Encrypts injector-only files. Changing it makes every sealed file unreadable.", "password", "PayloadKey", Secret: true, Danger: true),
    ];

    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _environment;
    private static Dictionary<string, string>? _atStartup;

    public ServerSettings(IConfiguration config, IWebHostEnvironment environment)
    {
        _config = config;
        _environment = environment;
        _atStartup ??= DotEnvFile.Read(ForgeOptions.DotEnvPath);
    }

    public static void Snapshot()
    {
        _atStartup = DotEnvFile.Read(ForgeOptions.DotEnvPath);
    }

    public string FilePath => ForgeOptions.DotEnvPath;

    private string? Appsettings(SettingDef def)
    {
        return def.Section == null ? null : _config.GetSection("Forge")[def.Section];
    }

    public List<object> Describe()
    {
        Dictionary<string, string> file = DotEnvFile.Read(FilePath);

        return All.Select(def =>
        {
            bool external = ForgeOptions.IsExternal(def.Key);
            string? appsettings = Appsettings(def);
            string? value = external ? Environment.GetEnvironmentVariable(def.Key) : file.TryGetValue(def.Key, out string? v) ? v : appsettings;
            string source = external ? "environment" : file.ContainsKey(def.Key) ? "file" : string.IsNullOrEmpty(appsettings) ? "default" : "appsettings";
            bool pending = !external && (file.GetValueOrDefault(def.Key) ?? "") != (_atStartup!.GetValueOrDefault(def.Key) ?? "");

            return (object)new
            {
                key = def.Key, group = def.Group, label = def.Label, help = def.Help, type = def.Type, choices = def.Choices,
                @default = def.Default, secret = def.Secret, danger = def.Danger,
                // Secrets never leave the server; the panel only learns whether one is set.
                value = def.Secret ? null : value,
                is_set = !string.IsNullOrEmpty(value),
                source, locked = external, pending,
            };
        }).ToList();
    }

    // The value a setting has right now, secrets included, for tests that reuse a saved password.
    public string? Current(string key)
    {
        if (ForgeOptions.IsExternal(key))
            return Environment.GetEnvironmentVariable(key);

        SettingDef? def = All.FirstOrDefault(d => d.Key == key);
        return DotEnvFile.Read(FilePath).TryGetValue(key, out string? value) ? value : def == null ? null : Appsettings(def);
    }

    public List<string> Pending()
    {
        Dictionary<string, string> file = DotEnvFile.Read(FilePath);
        return All.Where(d => !ForgeOptions.IsExternal(d.Key) && (file.GetValueOrDefault(d.Key) ?? "") != (_atStartup!.GetValueOrDefault(d.Key) ?? "")).Select(d => d.Key).ToList();
    }

    // Builds the configuration the server would see after a restart and runs the real loader over it,
    // so a bad value is refused here instead of stopping the server from starting.
    public ForgeOptions Validate(IReadOnlyDictionary<string, string?> changes)
    {
        Dictionary<string, string> file = DotEnvFile.Read(FilePath);

        foreach ((string key, string? value) in changes)
        {
            if (value == null)
                file.Remove(key);
            else
                file[key] = value;
        }

        var values = new Dictionary<string, string?>();

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string key = (string)entry.Key;

            if (ForgeOptions.IsExternal(key))
                values[key] = (string?)entry.Value;
        }

        foreach ((string key, string value) in file)
        {
            if (!ForgeOptions.IsExternal(key))
                values[key] = value;
        }

        IConfiguration config = new ConfigurationBuilder()
            .SetBasePath(_environment.ContentRootPath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{_environment.EnvironmentName}.json", optional: true)
            .AddInMemoryCollection(values)
            .Build();

        try
        {
            ForgeOptions options = ForgeOptions.Load(config, _environment.ContentRootPath);

            if (values.TryGetValue("ASPNETCORE_URLS", out string? urls) && urls != null)
            {
                foreach (string url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Uri.TryCreate(url.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
                        throw new InvalidOperationException($"“{url}” isn't a listen URL. Use something like http://localhost:5264.");

                    if (options.AdminPortEnabled && uri.Port == options.AdminPort)
                        throw new InvalidOperationException("The site and the admin panel can't listen on the same port.");
                }
            }

            return options;
        }
        catch (InvalidOperationException ex)
        {
            throw ApiException.BadRequest(ex.Message);
        }
    }

    public void Save(IReadOnlyDictionary<string, string?> changes)
    {
        foreach (string key in changes.Keys)
        {
            if (All.All(d => d.Key != key))
                throw ApiException.BadRequest($"{key} isn't a setting.");

            if (ForgeOptions.IsExternal(key))
                throw ApiException.BadRequest($"{key} is set by the server's environment (systemd, Docker or the shell), which overrides the settings file. Change it there.");
        }

        Validate(changes);

        try
        {
            DotEnvFile.Write(FilePath, changes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ApiException(500, $"Couldn't write {FilePath}: {ex.Message}. The server's user needs permission to write it.");
        }
    }

    // A systemd unit for the way this copy of the server is running right now.
    public string ServiceUnit()
    {
        string folder = Directory.GetCurrentDirectory();
        string process = Environment.ProcessPath ?? Path.Combine(folder, "OpenForge");
        string exec = Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"{process} {Path.Combine(AppContext.BaseDirectory, "OpenForge.dll")}"
            : process;
        string user = Environment.UserName;

        return $"""
            [Unit]
            Description=OpenForge mod server ({_config.GetSection("Forge")["Brand:Name"] ?? "MonkeForge"})
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=exec
            User={user}
            Group={user}
            WorkingDirectory={folder}
            ExecStart={exec}
            Environment=ASPNETCORE_ENVIRONMENT=Production
            # Settings live in {folder}/.env, which the admin panel edits. Variables set here override it.
            Restart=always
            RestartSec=5
            TimeoutStopSec=30
            KillSignal=SIGINT
            NoNewPrivileges=true
            PrivateTmp=true
            ProtectSystem=strict
            ProtectHome=read-only
            ReadWritePaths={folder}

            [Install]
            WantedBy=multi-user.target
            """;
    }
}
