using System.IO;
using System.Linq;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using OpenForge.Endpoints;
using OpenForge.Infrastructure;
using OpenForge.Services;

ForgeOptions.LoadDotEnv(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

var builderOptions = new WebApplicationOptions { Args = args };
bool contentRootGiven = args.Any(arg => arg.StartsWith("--contentRoot", System.StringComparison.OrdinalIgnoreCase));

if (!contentRootGiven && !Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")) && Directory.Exists(Path.Combine(System.AppContext.BaseDirectory, "wwwroot")))
    builderOptions = new WebApplicationOptions { Args = args, ContentRootPath = System.AppContext.BaseDirectory };

WebApplicationBuilder builder = WebApplication.CreateBuilder(builderOptions);
ForgeOptions options = ForgeOptions.Load(builder.Configuration, builder.Environment.ContentRootPath);
Schema.Migrate(options);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = ForgeOptions.MaxRequestBytes);

// The admin panel gets a listener of its own next to the site's usual URLs (ASPNETCORE_URLS, --urls or launchSettings).
if (options.AdminPortEnabled)
{
    string urls = builder.Configuration["urls"] ?? builder.Configuration["http_ports"]?.Split(';').Select(port => "http://*:" + port).Aggregate((a, b) => a + ";" + b) ?? "http://localhost:5000";
    string bind = options.AdminBind is "*" or "0.0.0.0" or "+" ? "*" : options.AdminBind.Contains(':') && !options.AdminBind.StartsWith('[') ? "[" + options.AdminBind + "]" : options.AdminBind;
    builder.WebHost.UseUrls(urls + ";http://" + bind + ":" + options.AdminPort);
}
builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = ForgeOptions.MaxRequestBytes;
    form.ValueLengthLimit = 1024 * 1024;
});

builder.Services.AddDataProtection()
    .SetApplicationName("OpenForge")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDir, "keys")));

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("discord", client => client.Timeout = System.TimeSpan.FromSeconds(15))
    .ConfigureHttpClient(client => client.DefaultRequestHeaders.UserAgent.ParseAdd("MonkeForge/1.0"));
builder.Services.AddHttpClient("github", client => client.Timeout = System.TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("monkeshield", client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("webhooks");
builder.Services.AddHttpClient("stripe", client => client.Timeout = System.TimeSpan.FromSeconds(30));

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<Shell>();
builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddSingleton<DocsStore>();
builder.Services.AddSingleton<Diagnostics>();
builder.Services.AddSingleton<MonkeShield>();
builder.Services.AddSingleton<GitHubReleases>();
builder.Services.AddSingleton<PayloadVault>();
builder.Services.AddSingleton<StripeClient>();
builder.Services.AddSingleton<MailSignal>();
builder.Services.AddSingleton<AuthThrottle>();
builder.Services.AddSingleton<Passkeys>();
builder.Services.AddSingleton<SupportSignal>();
builder.Services.AddSingleton<EmailPreviews>();
builder.Services.AddSingleton<AdminShell>();
builder.Services.AddScoped<Db>();
builder.Services.AddScoped<ForgeContext>();
builder.Services.AddScoped<Access>();
builder.Services.AddScoped<Channels>();
builder.Services.AddScoped<ModPresenter>();
builder.Services.AddScoped<Storage>();
builder.Services.AddScoped<Activity>();
builder.Services.AddScoped<Webhooks>();
builder.Services.AddScoped<Payments>();
builder.Services.AddScoped<Subscriptions>();
builder.Services.AddScoped<Seo>();
builder.Services.AddScoped<Mailer>();
builder.Services.AddScoped<SupportDesk>();
builder.Services.AddHostedService<MonkeShieldBackfill>();
builder.Services.AddHostedService<MailWorker>();
builder.Services.AddHostedService<PayoutPrivacySweep>();
builder.Services.AddHostedService<SupportInbox>();

WebApplication app = builder.Build();

// Load the payload key at startup so a missing or damaged key fails loudly instead of on the first install.
app.Services.GetRequiredService<PayloadVault>();

app.UseForgeErrors();
app.UseAdminPort(options);
// Scripts and styles aren't fingerprinted, so browsers must check for a newer copy instead of guessing how long theirs is good for.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseForgeSession();
app.UseRouting();
app.UseForgeAuth();

SessionEndpoints.Map(app);
AuthEndpoints.Map(app);
AccountEndpoints.Map(app);
EmailEndpoints.Map(app);
UserEndpoints.Map(app);
ModEndpoints.Map(app);
PublishEndpoints.Map(app);
ChannelEndpoints.Map(app);
CommunityEndpoints.Map(app);
ManagementEndpoints.Map(app);
AdminEndpoints.Map(app);
AdminPortalEndpoints.Map(app);
AdminUserEndpoints.Map(app);
ReviewEndpoints.Map(app);
TicketEndpoints.Map(app);
SocialEndpoints.Map(app);
ToolEndpoints.Map(app);
StoreEndpoints.Map(app);
InjectorEndpoints.Map(app);
SubscriptionEndpoints.Map(app);
DocsEndpoints.Map(app);
PageEndpoints.Map(app);

app.Run();
