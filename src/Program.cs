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

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<Shell>();
builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddSingleton<Diagnostics>();
builder.Services.AddSingleton<MonkeShield>();
builder.Services.AddSingleton<GitHubReleases>();
builder.Services.AddScoped<Db>();
builder.Services.AddScoped<ForgeContext>();
builder.Services.AddScoped<Access>();
builder.Services.AddScoped<Channels>();
builder.Services.AddScoped<ModPresenter>();
builder.Services.AddScoped<Storage>();
builder.Services.AddScoped<Activity>();
builder.Services.AddScoped<Webhooks>();
builder.Services.AddHostedService<MonkeShieldBackfill>();

WebApplication app = builder.Build();

app.UseForgeErrors();
app.UseStaticFiles();
app.UseForgeSession();
app.UseRouting();
app.UseForgeAuth();

SessionEndpoints.Map(app);
AuthEndpoints.Map(app);
UserEndpoints.Map(app);
ModEndpoints.Map(app);
PublishEndpoints.Map(app);
ChannelEndpoints.Map(app);
CommunityEndpoints.Map(app);
ManagementEndpoints.Map(app);
AdminEndpoints.Map(app);
SocialEndpoints.Map(app);
ToolEndpoints.Map(app);
PageEndpoints.Map(app);

app.Run();
