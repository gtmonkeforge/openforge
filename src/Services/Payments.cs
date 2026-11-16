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

    public Payments(Db db, ForgeContext context, Access access)
    {
        _db = db;
        _context = context;
        _access = access;
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

    // Called from the webhook inside its transaction once Stripe confirms the money moved.
    public void MarkPaid(PaymentRow payment, string? paymentIntent)
    {
        string now = Text.NowIso();
        _db.Exec("UPDATE payments SET status='paid',payment_intent=COALESCE(@paymentIntent,payment_intent),updated_at=@now WHERE id=@id AND status IN ('open','failed')",
            new { paymentIntent, now, id = payment.Id });

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
        _db.Exec("UPDATE payments SET status=@status,updated_at=@now WHERE id=@id", new { status, now, id = payment.Id });

        if (payment.Kind == "purchase")
            _db.Exec("UPDATE entitlements SET revoked_at=@now WHERE payment_id=@id AND revoked_at IS NULL", new { now, id = payment.Id });
    }

    public void Restore(PaymentRow payment)
    {
        _db.Exec("UPDATE payments SET status='paid',updated_at=@now WHERE id=@id", new { now = Text.NowIso(), id = payment.Id });

        if (payment.Kind == "purchase")
            _db.Exec("UPDATE entitlements SET revoked_at=NULL WHERE payment_id=@id", new { id = payment.Id });
    }
}
