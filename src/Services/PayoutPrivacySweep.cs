using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenForge.Infrastructure;

namespace OpenForge.Services;

// Re-checks every connected Stripe account's public details once a day. The first run covers accounts created before
// the privacy check existed; later runs catch webhooks that never arrived and grace periods that ran out.
public sealed class PayoutPrivacySweep : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    // Stripe allows far more, but there's no hurry and this leaves room for live traffic.
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(250);

    private readonly IServiceProvider _services;
    private readonly ForgeOptions _options;
    private readonly ILogger<PayoutPrivacySweep> _logger;

    public PayoutPrivacySweep(IServiceProvider services, ForgeOptions options, ILogger<PayoutPrivacySweep> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.StripeConfigured)
            return;

        if (!_options.PrivacyShieldConfigured)
        {
            _logger.LogWarning("SUPPORT_EMAIL, SUPPORT_PHONE and SUPPORT_ADDRESS_* are not all set, so creators can't connect Stripe and existing creators' public details aren't checked.");
            return;
        }

        try
        {
            await Task.Delay(StartDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        List<string> accounts;

        using (IServiceScope scope = _services.CreateScope())
            accounts = scope.ServiceProvider.GetRequiredService<Db>().Query<string>("SELECT stripe_account_id FROM users WHERE stripe_account_id IS NOT NULL").ToList();

        int flagged = 0, failed = 0;

        foreach (string accountId in accounts)
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                using IServiceScope scope = _services.CreateScope();
                IServiceProvider provider = scope.ServiceProvider;
                JsonObject account = await provider.GetRequiredService<StripeClient>().GetAsync("/v1/accounts/" + Uri.EscapeDataString(accountId));

                if (Payments.ProfileIssues(account, _options).Count > 0)
                    flagged++;

                if (provider.GetRequiredService<Payments>().SaveAccountState(accountId, account) is string ownerId)
                    await provider.GetRequiredService<Subscriptions>().SyncCollectionAsync(ownerId);
            }
            catch (ApiException)
            {
                // StripeClient has already reported the details to the admin Errors tab.
                failed++;
            }

            await Task.Delay(Pace, stoppingToken);
        }

        _logger.LogInformation("Payout privacy check: {Accounts} accounts, {Flagged} showing personal details, {Failed} couldn't be read", accounts.Count, flagged, failed);
    }
}
