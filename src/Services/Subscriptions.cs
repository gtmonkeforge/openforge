using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenForge.Endpoints;
using OpenForge.Infrastructure;
using OpenForge.Models;

namespace OpenForge.Services;

// Monthly memberships. Like one-time purchases, they are direct charges on the seller's Stripe account:
// Stripe's fee comes out of the seller's side and MonkeForge takes 10% of every invoice as an application fee.
public sealed class Subscriptions
{
    private readonly Db _db;
    private readonly StripeClient _stripe;
    private readonly ForgeOptions _options;
    private readonly ILogger<Subscriptions> _logger;
    private readonly Diagnostics _diagnostics;
    private readonly Payments _payments;

    public Subscriptions(Db db, StripeClient stripe, ForgeOptions options, ILogger<Subscriptions> logger, Diagnostics diagnostics, Payments payments)
    {
        _payments = payments;
        _diagnostics = diagnostics;
        _db = db;
        _stripe = stripe;
        _options = options;
        _logger = logger;
    }

    public SubscriptionRow? Get(string id)
    {
        return _db.One<SubscriptionRow>("SELECT * FROM subscriptions WHERE id=@id", new { id });
    }

    public async Task<string> CheckoutAsync(TierRow tier, UserRow seller, CurrentUser buyer, string productName, string returnPath)
    {
        string id = Text.Hex(16);
        string back = _options.PublicUrl + returnPath;
        string separator = back.Contains('?') ? "&" : "?";

        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "subscription"),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", _options.Currency),
            new("line_items[0][price_data][unit_amount]", tier.PriceCents.ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][price_data][recurring][interval]", "month"),
            new("line_items[0][price_data][product_data][name]", productName),
            new("success_url", back + separator + "checkout=success"),
            new("cancel_url", back + separator + "checkout=cancelled"),
            new("client_reference_id", buyer.Id),
            // Stripe's minimum is 30 minutes after it creates the session, so exactly 30 from here lands just short of it.
            new("expires_at", DateTimeOffset.UtcNow.AddMinutes(35).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            // Managed Payments is on by default for newer accounts and rejects line items without a tax code; see StoreEndpoints.CheckoutForm.
            new("managed_payments[enabled]", "false"),
            new("subscription_data[application_fee_percent]", ForgeOptions.PlatformFeePercent.ToString(CultureInfo.InvariantCulture)),
            new("subscription_data[metadata][monkeforge_subscription]", id),
            new("metadata[kind]", "subscription"),
            new("metadata[monkeforge_subscription]", id),
        };

        StripeClient.AddBranding(form, _options, seller.PublicName);
        JsonObject session = await _stripe.PostAsync("/v1/checkout/sessions", form, seller.StripeAccountId);
        string now = Text.NowIso();

        _db.Exec("""
            INSERT INTO subscriptions(id,user_id,tier_id,owner_id,stripe_account,checkout_session,price_cents,currency,status,created_at,updated_at)
            VALUES(@id,@user,@tier,@owner,@account,@session,@price,@currency,'pending',@now,@now)
            """, new { id, user = buyer.Id, tier = tier.Id, owner = seller.Id, account = seller.StripeAccountId, session = StripeClient.Str(session["id"]), price = tier.PriceCents, currency = _options.Currency, now });

        return StripeClient.Str(session["url"]) ?? throw new ApiException(502, "The payment provider returned an unexpected response.");
    }

    // At period end the subscriber keeps what they already paid for; immediately is for refunds, takedowns and bans.
    public async Task CancelAsync(SubscriptionRow sub, bool immediately)
    {
        if (sub.StripeSubscription == null || !sub.Granting)
            return;

        string path = "/v1/subscriptions/" + Uri.EscapeDataString(sub.StripeSubscription);

        if (immediately)
            await _stripe.DeleteAsync(path, sub.StripeAccount);
        else
            await _stripe.PostAsync(path, [new("cancel_at_period_end", "true")], sub.StripeAccount);

        _db.Exec("UPDATE subscriptions SET status=CASE WHEN @immediately THEN 'canceled' ELSE status END,cancel_at_period_end=1,updated_at=@now WHERE id=@id",
            new { immediately, now = Text.NowIso(), id = sub.Id });
    }

    // Cancels many subscriptions without stopping at the first Stripe failure, and reports what happened.
    public async Task<(int Cancelled, int Failed)> CancelManyAsync(IEnumerable<SubscriptionRow> subs, bool immediately)
    {
        int cancelled = 0, failed = 0;

        foreach (SubscriptionRow sub in subs)
        {
            try
            {
                await CancelAsync(sub, immediately);
                cancelled++;
            }
            catch (ApiException)
            {
                failed++;
                _logger.LogError("Could not cancel subscription {Subscription}; cancel it in Stripe", sub.Id);
                _diagnostics.Report("Stripe", $"Could not cancel subscription {sub.Id}. Cancel it in the Stripe dashboard.");
            }
        }

        return (cancelled, failed);
    }

    public List<SubscriptionRow> LiveForMod(string guid)
    {
        return _db.Query<SubscriptionRow>("""
            SELECT s.* FROM subscriptions s JOIN subscription_tiers t ON t.id=s.tier_id
            WHERE t.guid=@guid AND s.status IN ('active','trialing','past_due')
            """, new { guid }).ToList();
    }

    public List<SubscriptionRow> LiveForOwner(string ownerId)
    {
        return _db.Query<SubscriptionRow>("SELECT * FROM subscriptions WHERE owner_id=@ownerId AND status IN ('active','trialing','past_due')", new { ownerId }).ToList();
    }

    public List<SubscriptionRow> LiveForTier(string tierId)
    {
        return _db.Query<SubscriptionRow>("SELECT * FROM subscriptions WHERE tier_id=@tierId AND status IN ('active','trialing','past_due')", new { tierId }).ToList();
    }

    // A taken-down mod stops billing at once: its own tiers end, and creator memberships stop listing it.
    public async Task<(int Cancelled, int Failed)> TakeDownModAsync(string guid)
    {
        _db.Exec("UPDATE subscription_tiers SET archived_at=@now WHERE guid=@guid AND archived_at IS NULL", new { now = Text.NowIso(), guid });
        _db.Exec("DELETE FROM tier_mods WHERE guid=@guid", new { guid });
        return await CancelManyAsync(LiveForMod(guid), true);
    }

    public async Task<(int Cancelled, int Failed)> StopSellerAsync(string ownerId)
    {
        _db.Exec("UPDATE subscription_tiers SET archived_at=@now WHERE owner_id=@ownerId AND archived_at IS NULL", new { now = Text.NowIso(), ownerId });
        return await CancelManyAsync(LiveForOwner(ownerId), true);
    }

    // Refunds a payment on the seller's account. The 10% fee is returned too, so the buyer gets everything back.
    public async Task RefundAsync(PaymentRow payment)
    {
        string? intent = payment.PaymentIntent ?? await FindInvoiceIntentAsync(payment);

        if (intent == null || payment.StripeAccount == null)
            throw ApiException.Conflict("This payment can't be refunded automatically. Refund it from the Stripe dashboard.");

        await _stripe.PostAsync("/v1/refunds",
        [
            new("payment_intent", intent),
            new("refund_application_fee", "true"),
            new("metadata[monkeforge_payment]", payment.Id),
        ], payment.StripeAccount, "monkeforge-refund-" + payment.Id);
    }

    // Invoices moved their PaymentIntent between Stripe API versions, so both places are checked.
    private async Task<string?> FindInvoiceIntentAsync(PaymentRow payment)
    {
        if (payment.Kind != "subscription" || payment.StripeAccount == null)
            return null;

        JsonObject invoice = await _stripe.GetAsync("/v1/invoices/" + Uri.EscapeDataString(payment.Id), payment.StripeAccount);
        string? intent = StripeClient.Str(invoice["payment_intent"]);

        if (intent != null)
            return intent;

        JsonObject payments = await _stripe.GetAsync("/v1/invoice_payments?invoice=" + Uri.EscapeDataString(payment.Id), payment.StripeAccount);
        return (payments["data"] as JsonArray)?.Select(p => StripeClient.Str(p?["payment"]?["payment_intent"])).FirstOrDefault(p => p != null);
    }

    // ---- Webhook handlers. They run inside the webhook's transaction. ----

    private SubscriptionRow? FromStripe(string? stripeId, JsonNode? metadata)
    {
        SubscriptionRow? row = stripeId == null ? null : _db.One<SubscriptionRow>("SELECT * FROM subscriptions WHERE stripe_subscription=@stripeId", new { stripeId });
        string? ours = StripeClient.Str(metadata?["monkeforge_subscription"]);
        return row ?? (ours == null ? null : Get(ours));
    }

    public void CheckoutCompleted(JsonObject session, string? account)
    {
        SubscriptionRow? sub = _db.One<SubscriptionRow>("SELECT * FROM subscriptions WHERE checkout_session=@id", new { id = StripeClient.Str(session["id"]) });
        string? stripeId = StripeClient.Str(session["subscription"]);

        if (sub == null || stripeId == null || sub.StripeAccount != account)
            return;

        _db.Exec("UPDATE subscriptions SET stripe_subscription=@stripeId,status=CASE WHEN status='pending' THEN 'active' ELSE status END,updated_at=@now WHERE id=@id",
            new { stripeId, now = Text.NowIso(), id = sub.Id });
        ManagementEndpoints.Notify(_db, sub.UserId, "subscription", "Your membership is active.", sub.Id);
    }

    public void SubscriptionChanged(JsonObject obj, string? account, bool deleted)
    {
        string? stripeId = StripeClient.Str(obj["id"]);
        SubscriptionRow? sub = FromStripe(stripeId, obj["metadata"]);

        if (sub == null || sub.StripeAccount != account)
            return;

        string status = deleted ? "canceled" : StripeClient.Str(obj["status"]) ?? sub.Status;

        // Newer API versions report the billing period on each subscription item rather than the subscription.
        long periodEnd = StripeClient.Long(obj["current_period_end"]);

        if (periodEnd == 0 && obj["items"]?["data"] is JsonArray items && items.Count > 0)
            periodEnd = StripeClient.Long(items[0]?["current_period_end"]);

        string? end = periodEnd == 0 ? sub.CurrentPeriodEnd : DateTimeOffset.FromUnixTimeSeconds(periodEnd).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);

        _db.Exec("""
            UPDATE subscriptions SET stripe_subscription=COALESCE(stripe_subscription,@stripeId),status=@status,
            cancel_at_period_end=@cancelling,current_period_end=@end,updated_at=@now WHERE id=@id
            """, new { stripeId, status, cancelling = StripeClient.Flag(obj["cancel_at_period_end"]) ? 1 : 0, end, now = Text.NowIso(), id = sub.Id });
    }

    public void InvoicePaid(JsonObject invoice, string? account)
    {
        // Older API versions put the subscription on the invoice; newer ones nest it under parent.subscription_details.
        string? stripeId = StripeClient.Str(invoice["subscription"]) ?? StripeClient.Str(invoice["parent"]?["subscription_details"]?["subscription"]);
        JsonNode? metadata = invoice["subscription_details"]?["metadata"] ?? invoice["parent"]?["subscription_details"]?["metadata"];
        SubscriptionRow? sub = FromStripe(stripeId, metadata);
        long amount = StripeClient.Long(invoice["amount_paid"]);
        string? id = StripeClient.Str(invoice["id"]);

        if (sub == null || id == null || amount <= 0 || sub.StripeAccount != account)
            return;

        TierRow? tier = _db.One<TierRow>("SELECT * FROM subscription_tiers WHERE id=@id", new { id = sub.TierId });
        string? intent = StripeClient.Str(invoice["payment_intent"])
            ?? (invoice["payments"]?["data"] as JsonArray)?.Select(p => StripeClient.Str(p?["payment"]?["payment_intent"])).FirstOrDefault(p => p != null);
        string now = Text.NowIso();

        int inserted = _db.Exec("""
            INSERT OR IGNORE INTO payments(id,kind,buyer_id,recipient_id,guid,amount_cents,fee_cents,currency,stripe_account,payment_intent,status,created_at,updated_at,subscription_id)
            VALUES(@id,'subscription',@buyer,@owner,@guid,@amount,@fee,@currency,@account,@intent,'paid',@now,@now,@sub)
            """, new { id, buyer = sub.UserId, owner = sub.OwnerId, guid = tier?.Guid, amount, fee = Payments.PlatformFee(amount), currency = StripeClient.Str(invoice["currency"]) ?? sub.Currency, account, intent, now, sub = sub.Id });

        // Stripe redelivers events; the receipt goes out once, for the invoice's first delivery.
        if (inserted > 0 && _payments.Get(id) is PaymentRow paid)
            _payments.Receipt(paid);

        if (stripeId != null)
            _db.Exec("UPDATE subscriptions SET stripe_subscription=COALESCE(stripe_subscription,@stripeId),status='active',updated_at=@now WHERE id=@sid AND status IN ('pending','past_due','incomplete')",
                new { stripeId, now, sid = sub.Id });
    }
}
