using System;
using System.Globalization;
using System.Text.Json.Nodes;
using OpenForge.Endpoints;
using OpenForge.Infrastructure;
using OpenForge.Models;

namespace OpenForge.Services;

public sealed class Payments
{
    // Stripe's smallest USD card charge is $0.50. The ceilings keep typos from turning into $5,000 mods.
    public const long MinPriceCents = 50;
    public const long MaxPriceCents = 50_000;
    public const long MinDonationCents = 100;
    public const long MaxDonationCents = 50_000;

    private readonly Db _db;
    private readonly ForgeContext _context;
    private readonly Access _access;
    private readonly Mailer _mailer;
    private readonly ForgeOptions _options;

    public Payments(Db db, ForgeContext context, Access access, Mailer mailer, ForgeOptions options)
    {
        _db = db;
        _context = context;
        _access = access;
        _mailer = mailer;
        _options = options;
    }

    public static long PlatformFee(long amountCents)
    {
        // Rounded half up so a $0.55 sale yields a 6¢ fee rather than 5¢.
        return (amountCents * ForgeOptions.PlatformFeePercent + 50) / 100;
    }

    // A mod is gated when it has a one-time price or any live tier, its own or a creator membership, unlocks it.
    public bool IsGated(ModRow mod)
    {
        return mod.Paid || _db.Exists("""
            SELECT 1 FROM subscription_tiers t WHERE t.archived_at IS NULL
            AND (t.guid=@guid OR EXISTS(SELECT 1 FROM tier_mods tm WHERE tm.tier_id=t.id AND tm.guid=@guid))
            """, new { guid = mod.Guid });
    }

    // Whether the viewer may install this mod's files: ungated mods, its own publishers, admins, buyers and subscribers.
    public bool CanUse(ModRow mod, CurrentUser? user = null)
    {
        user ??= _context.User;

        if (!IsGated(mod))
            return true;

        if (user == null)
            return false;

        if (user.IsAdmin || _access.CanPublish(user.Id, mod.AuthorId))
            return true;

        return HasEntitlement(user.Id, mod.Guid) || HasSubscriptionFor(user.Id, mod.Guid);
    }

    // Archived tiers still count here: people who subscribed before a tier was retired keep access until their subscription ends.
    public bool HasSubscriptionFor(string userId, string guid)
    {
        return _db.Exists("""
            SELECT 1 FROM subscriptions s JOIN subscription_tiers t ON t.id=s.tier_id
            WHERE s.user_id=@userId AND s.status IN ('active','trialing','past_due')
            AND (t.guid=@guid OR EXISTS(SELECT 1 FROM tier_mods tm WHERE tm.tier_id=t.id AND tm.guid=@guid))
            """, new { userId, guid });
    }

    public bool HasEntitlement(string userId, string guid)
    {
        return _db.Exists("SELECT 1 FROM entitlements WHERE user_id=@userId AND guid=@guid AND revoked_at IS NULL", new { userId, guid });
    }

    public UserRow? Seller(ModRow mod)
    {
        return _db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = mod.AuthorId });
    }

    public static bool CanSell(UserRow? seller)
    {
        return seller != null && !seller.Banned && seller.StripeAccountId != null && seller.StripeChargesEnabled != 0;
    }

    public static bool CanReceiveDonations(UserRow? seller)
    {
        return seller != null && !seller.Banned && seller.StripeAccountId != null && seller.StripeTransfersEnabled != 0;
    }

    public void SaveAccountState(string accountId, JsonObject account)
    {
        bool charges = StripeClient.Flag(account["charges_enabled"]);
        bool details = StripeClient.Flag(account["details_submitted"]);
        bool transfers = StripeClient.Str(account["capabilities"]?["transfers"]) == "active";

        _db.Exec("UPDATE users SET stripe_charges_enabled=@charges,stripe_transfers_enabled=@transfers,stripe_details_submitted=@details WHERE stripe_account_id=@accountId",
            new { charges = charges ? 1 : 0, transfers = transfers ? 1 : 0, details = details ? 1 : 0, accountId });
    }

    public PaymentRow? Get(string id)
    {
        return _db.One<PaymentRow>("SELECT * FROM payments WHERE id=@id", new { id });
    }

    // Called from the webhook inside its transaction once Stripe confirms the money moved.
    public void MarkPaid(PaymentRow payment, string? paymentIntent)
    {
        string now = Text.NowIso();
        int changed = _db.Exec("UPDATE payments SET status='paid',payment_intent=COALESCE(@paymentIntent,payment_intent),updated_at=@now WHERE id=@id AND status IN ('open','failed')",
            new { paymentIntent, now, id = payment.Id });

        // Stripe retries and the async-payment event can both arrive; only the first one sends a receipt.
        if (changed == 0)
            return;

        Receipt(Get(payment.Id)!);

        if (payment.Kind != "purchase" || payment.BuyerId == null || payment.Guid == null)
            return;

        _db.Exec("""
            INSERT INTO entitlements(user_id,guid,payment_id,granted_at) VALUES(@buyer,@guid,@id,@now)
            ON CONFLICT(user_id,guid) DO UPDATE SET payment_id=excluded.payment_id,granted_at=excluded.granted_at,revoked_at=NULL
            """, new { buyer = payment.BuyerId, guid = payment.Guid, id = payment.Id, now });

        ManagementEndpoints.Notify(_db, payment.BuyerId, "purchase", "Your purchase is ready to install.", payment.Guid);
    }

    // Full refunds and disputes take access away; a later dispute win restores it.
    public void SetReversed(PaymentRow payment, string status)
    {
        string now = Text.NowIso();
        int changed = _db.Exec("UPDATE payments SET status=@status,updated_at=@now WHERE id=@id AND status!=@status", new { status, now, id = payment.Id });

        if (payment.Kind == "purchase")
            _db.Exec("UPDATE entitlements SET revoked_at=@now WHERE payment_id=@id AND revoked_at IS NULL", new { now, id = payment.Id });

        if (changed > 0 && status == "refunded")
            RefundNotice(payment);
    }

    public void Restore(PaymentRow payment)
    {
        _db.Exec("UPDATE payments SET status='paid',updated_at=@now WHERE id=@id", new { now = Text.NowIso(), id = payment.Id });

        if (payment.Kind == "purchase")
            _db.Exec("UPDATE entitlements SET revoked_at=NULL WHERE payment_id=@id", new { id = payment.Id });
    }

    // ---- Receipts ------------------------------------------------------------------------------------------------

    private sealed record ReceiptItem(string Title, string Line, string? Seller, string? Url);

    private ReceiptItem Describe(PaymentRow payment)
    {
        string? Name(string? id)
        {
            return id == null ? null : _db.One<string>("SELECT COALESCE(public_username,display_name) FROM users WHERE id=@id", new { id });
        }

        string? modName = payment.Guid == null ? null : _db.One<string>("SELECT name FROM mods WHERE guid=@guid", new { guid = payment.Guid });
        string? seller = Name(payment.RecipientId);
        string purchases = _options.PublicUrl + "/profile#purchases";

        switch (payment.Kind)
        {
            case "purchase":
                return new ReceiptItem($"Receipt for {modName ?? payment.Guid}", modName ?? payment.Guid ?? "Mod", seller,
                    payment.Guid == null ? purchases : _options.PublicUrl + "/view/" + Uri.EscapeDataString(payment.Guid));
            case "subscription":
            {
                string? tier = payment.SubscriptionId == null ? null : _db.One<string>(
                    "SELECT t.name FROM subscriptions s JOIN subscription_tiers t ON t.id=s.tier_id WHERE s.id=@id", new { id = payment.SubscriptionId });
                string line = $"{tier ?? "Membership"} ({(modName != null ? modName : seller ?? "creator")}), monthly";
                return new ReceiptItem($"Receipt for your {tier ?? "membership"} membership", line, seller, purchases);
            }
            case "creator_donation":
                return new ReceiptItem($"Thanks for supporting {seller ?? "a creator"}", $"Donation to {seller ?? "a creator"}", seller, null);
            default:
                return new ReceiptItem($"Thanks for supporting {_options.BrandName}", $"Donation to {_options.BrandName}", null, null);
        }
    }

    public static string Money(long minor, string currency)
    {
        // Mirrors the site's zero-decimal list: those currencies are already in whole units.
        bool whole = currency is "bif" or "clp" or "djf" or "gnf" or "jpy" or "kmf" or "krw" or "mga" or "pyg" or "rwf" or "ugx" or "vnd" or "vuv" or "xaf" or "xof" or "xpf";
        decimal amount = whole ? minor : minor / 100m;
        return amount.ToString(whole ? "N0" : "N2", CultureInfo.InvariantCulture) + " " + currency.ToUpperInvariant();
    }

    private string? BuyerEmail(PaymentRow payment)
    {
        return payment.BuyerId == null ? null : _db.One<string>("SELECT email FROM users WHERE id=@id AND email_verified=1 AND email IS NOT NULL", new { id = payment.BuyerId });
    }

    // Queued inside the webhook's transaction, so a receipt exists exactly when the payment is recorded as paid.
    public void Receipt(PaymentRow payment)
    {
        string? to = BuyerEmail(payment);

        if (to == null)
            return;

        ReceiptItem item = Describe(payment);
        string total = Money(payment.AmountCents, payment.Currency);
        string date = DateTime.UtcNow.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        string seller = item.Seller == null ? "" : $"<tr><td style=\"color:#6b7280;padding:4px 16px 4px 0\">Sold by</td><td>{Mailer.Encode(item.Seller)} via {Mailer.Encode(_options.BrandName)}</td></tr>";
        string renewal = payment.Kind == "subscription"
            ? "<p style=\"margin:12px 0 0;color:#6b7280;font-size:14px\">This membership renews every month until you cancel it from your purchases page.</p>" : "";
        string html = $"""
            <p style="margin:0 0 16px">Thanks for your payment. Here's your receipt.</p>
            <table role="presentation" cellpadding="0" cellspacing="0" style="width:100%;font-size:14px;border-collapse:collapse">
            <tr><td style="color:#6b7280;padding:4px 16px 4px 0">Item</td><td>{Mailer.Encode(item.Line)}</td></tr>
            {seller}
            <tr><td style="color:#6b7280;padding:4px 16px 4px 0">Date</td><td>{date}</td></tr>
            <tr><td style="color:#6b7280;padding:4px 16px 4px 0">Reference</td><td style="font-family:monospace;font-size:12px;word-break:break-all">{Mailer.Encode(payment.Id)}</td></tr>
            <tr><td style="padding:12px 16px 0 0;border-top:1px solid #e5e7eb"><strong>Total paid</strong></td><td style="padding-top:12px;border-top:1px solid #e5e7eb"><strong>{Mailer.Encode(total)}</strong></td></tr>
            </table>
            {renewal}
            <p style="margin:16px 0 0;color:#6b7280;font-size:14px">Something wrong with this purchase? Open a dispute from your purchases page within 90 days.</p>
            """;
        string text = $"""
            Thanks for your payment. Here's your receipt.

            Item: {item.Line}
            {(item.Seller == null ? "" : $"Sold by: {item.Seller} via {_options.BrandName}\n")}Date: {date}
            Reference: {payment.Id}
            Total paid: {total}
            {(payment.Kind == "subscription" ? "\nThis membership renews every month until you cancel it from your purchases page.\n" : "")}
            Your purchases: {_options.PublicUrl}/profile#purchases
            Something wrong? Open a dispute from your purchases page within 90 days.
            """;

        _mailer.Enqueue(to, item.Title, text, _mailer.Layout(item.Title, html, item.Url == null ? "Your purchases" : "View", item.Url ?? _options.PublicUrl + "/profile#purchases",
            $"This receipt was sent to the email address on your {Mailer.Encode(_options.BrandName)} account."), MailKinds.Receipt, payment.BuyerId);
    }

    private void RefundNotice(PaymentRow payment)
    {
        string? to = BuyerEmail(payment);

        if (to == null)
            return;

        ReceiptItem item = Describe(payment);
        string total = Money(payment.AmountCents, payment.Currency);
        string subject = $"Refund for {item.Line}";
        string html = $"""
            <p style="margin:0 0 12px">We've refunded <strong>{Mailer.Encode(total)}</strong> for {Mailer.Encode(item.Line)}.</p>
            <p style="margin:0 0 12px">Refunds usually reach your card within 5–10 business days, depending on your bank.</p>
            <p style="margin:0;color:#6b7280;font-size:14px">Reference: <span style="font-family:monospace">{Mailer.Encode(payment.Id)}</span></p>
            """;
        string text = $"We've refunded {total} for {item.Line}.\n\nRefunds usually reach your card within 5–10 business days, depending on your bank.\n\nReference: {payment.Id}";
        _mailer.Enqueue(to, subject, text, _mailer.Layout(subject, html), MailKinds.Receipt, payment.BuyerId);
    }
}
