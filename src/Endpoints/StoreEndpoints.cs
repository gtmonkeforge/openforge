using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// Money flows, chosen so each party pays for what they earn from:
//   Paid mods        direct charge on the creator's Stripe account. Stripe's fee comes out of the creator's side,
//                    and MonkeForge collects 10% as an application fee.
//   Creator donation destination charge on MonkeForge's account. MonkeForge pays Stripe's fee and the creator gets 100%.
//   Platform donation plain charge on MonkeForge's account.
public static class StoreEndpoints
{
    private const int MaxWebhookBytes = 512 * 1024;
    private const int MaxOpenCheckoutsPerHour = 10;

    // Event types the webhook acts on. Anything else is acknowledged without touching the database,
    // so the Stripe endpoints can safely be set to send all events.
    private static readonly HashSet<string> HandledEvents = new(StringComparer.Ordinal)
    {
        "checkout.session.completed",
        "checkout.session.async_payment_succeeded",
        "checkout.session.async_payment_failed",
        "checkout.session.expired",
        "charge.refunded",
        "charge.dispute.created",
        "charge.dispute.closed",
        "account.updated",
        "customer.subscription.created",
        "customer.subscription.updated",
        "customer.subscription.deleted",
        "invoice.paid",
    };

    // Stripe stops retrying a delivery after about three days, so older dedupe rows are no longer needed.
    private static readonly TimeSpan StripeEventRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan StripeEventPruneInterval = TimeSpan.FromHours(1);
    private static long _nextStripeEventPruneTicks;

    private sealed class LibraryRow
    {
        public string Guid { get; set; } = "";
        public string GrantedAt { get; set; } = "";
        public string? Name { get; set; }
        public string? Logo { get; set; }
        public string? CustomSlug { get; set; }
        public int UsesInjector { get; set; }
        public string? Status { get; set; }
        public long AmountCents { get; set; }
        public string Currency { get; set; } = "";
        public string? PaymentId { get; set; }
        public string? PaymentStatus { get; set; }
        public string? DisputeStatus { get; set; }
    }

    private sealed class SaleRow
    {
        public string Kind { get; set; } = "";
        public string? Guid { get; set; }
        public string? Name { get; set; }
        public long AmountCents { get; set; }
        public long FeeCents { get; set; }
        public string Currency { get; set; } = "";
        public string Status { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/store", Config);
        app.MapGet("/api/v1/payouts", PayoutStatusAsync);
        app.MapPost("/api/v1/payouts/onboard", OnboardAsync);
        app.MapPut("/api/v1/mods/{value}/pricing", SetPricingAsync);
        app.MapPost("/api/v1/mods/{value}/checkout", CheckoutAsync);
        app.MapPost("/api/v1/donations", DonateAsync);
        app.MapGet("/api/v1/purchases", Library);
        app.MapPost("/api/v1/stripe/webhook", WebhookAsync).WithMetadata(new CsrfExemptAttribute());
    }

    private static IResult Config(ForgeOptions options)
    {
        return Api.Json(new
        {
            enabled = options.StripeConfigured,
            currency = options.Currency,
            fee_percent = ForgeOptions.PlatformFeePercent,
            min_price_cents = Payments.MinPriceCents,
            max_price_cents = Payments.MaxPriceCents,
            min_donation_cents = Payments.MinDonationCents,
            max_donation_cents = Payments.MaxDonationCents,
        });
    }

    // Payouts belong to whoever publishes: a personal account, or a publishing group managed by its owners.
    private static UserRow PayoutAccount(HttpRequest request, CurrentUser user, Db db, Access access)
    {
        string target = request.Query["account"].FirstOrDefault() ?? user.Id;

        if (target == user.Id)
            return user.Row;

        UserRow? group = db.One<UserRow>("SELECT * FROM users WHERE id=@target AND is_group=1", new { target });

        if (group == null || !access.CanTransfer(user.Id, group.Id))
            throw ApiException.Forbidden("Only a group owner can manage that group's payouts.");

        return group;
    }

    private static async Task<IResult> PayoutStatusAsync(HttpRequest request, ForgeContext context, Db db, Access access, StripeClient stripe, Payments payments, ForgeOptions options)
    {
        CurrentUser user = context.RequireUser("Sign in to manage payouts.");
        UserRow account = PayoutAccount(request, user, db, access);

        if (options.StripeConfigured && account.StripeAccountId != null)
        {
            // Refreshing here means returning from onboarding shows the right state even before the webhook lands.
            // If Stripe is unreachable, the last saved state is shown instead of failing the page.
            try
            {
                JsonObject remote = await stripe.GetAsync("/v1/accounts/" + Uri.EscapeDataString(account.StripeAccountId));
                payments.SaveAccountState(account.StripeAccountId, remote);
                account = db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = account.Id })!;
            }
            catch (ApiException)
            {
            }
        }

        var totals = db.One<(long Sales, long Fees, long Donations)>("""
            SELECT COALESCE(SUM(CASE WHEN kind IN ('purchase','subscription') THEN amount_cents END),0),
                   COALESCE(SUM(CASE WHEN kind IN ('purchase','subscription') THEN fee_cents END),0),
                   COALESCE(SUM(CASE WHEN kind='creator_donation' THEN amount_cents END),0)
            FROM payments WHERE recipient_id=@id AND status='paid'
            """, new { id = account.Id });

        List<SaleRow> recent = db.Query<SaleRow>("""
            SELECT p.kind,p.guid,m.name,p.amount_cents,p.fee_cents,p.currency,p.status,p.created_at FROM payments p
            LEFT JOIN mods m ON m.guid=p.guid WHERE p.recipient_id=@id AND p.status!='open' AND p.status!='expired'
            ORDER BY p.created_at DESC LIMIT 25
            """, new { id = account.Id }).ToList();

        return Api.Json(new
        {
            enabled = options.StripeConfigured,
            account_id = account.Id,
            connected = account.StripeAccountId != null,
            details_submitted = account.StripeDetailsSubmitted != 0,
            charges_enabled = account.StripeChargesEnabled != 0,
            donations_enabled = account.StripeTransfersEnabled != 0,
            fee_percent = ForgeOptions.PlatformFeePercent,
            currency = options.Currency,
            totals = new { sales_cents = totals.Sales, fees_cents = totals.Fees, earnings_cents = totals.Sales - totals.Fees, donations_cents = totals.Donations },
            recent = recent.Select(r => new { kind = r.Kind, guid = r.Guid, name = r.Name, amount_cents = r.AmountCents, fee_cents = r.FeeCents, currency = r.Currency, status = r.Status, created_at = r.CreatedAt }),
        });
    }

    private static async Task<IResult> OnboardAsync(HttpRequest request, ForgeContext context, Db db, Access access, StripeClient stripe, ForgeOptions options)
    {
        CurrentUser user = context.RequireUser("Sign in to set up payouts.");
        stripe.RequireConfigured();
        UserRow account = PayoutAccount(request, user, db, access);
        string? stripeId = account.StripeAccountId;

        if (stripeId == null)
        {
            // A full-dashboard account that pays its own Stripe fees: the creator owns the relationship with Stripe,
            // handles refunds and disputes on their own sales, and MonkeForge never sees their bank or identity details.
            JsonObject created = await stripe.PostAsync("/v1/accounts",
            [
                new("controller[stripe_dashboard][type]", "full"),
                new("controller[fees][payer]", "account"),
                new("controller[losses][payments]", "stripe"),
                new("controller[requirement_collection]", "stripe"),
                new("capabilities[card_payments][requested]", "true"),
                new("capabilities[transfers][requested]", "true"),
                new("metadata[monkeforge_account]", account.Id),
            ], idempotencyKey: "monkeforge-account-" + account.Id);

            stripeId = StripeClient.Str(created["id"]) ?? throw new ApiException(502, "The payment provider returned an unexpected response.");

            int updated = db.Exec("UPDATE users SET stripe_account_id=@stripeId WHERE id=@id AND stripe_account_id IS NULL", new { stripeId, id = account.Id });

            if (updated == 0)
                stripeId = db.One<string>("SELECT stripe_account_id FROM users WHERE id=@id", new { id = account.Id })!;
        }

        string back = options.PublicUrl + "/settings/payouts" + (account.Id == user.Id ? "" : "?account=" + Uri.EscapeDataString(account.Id));
        string separator = back.Contains('?') ? "&" : "?";

        JsonObject link = await stripe.PostAsync("/v1/account_links",
        [
            new("account", stripeId),
            new("type", "account_onboarding"),
            new("refresh_url", back + separator + "onboarding=retry"),
            new("return_url", back + separator + "onboarding=done"),
        ]);

        return Api.Json(new { url = StripeClient.Str(link["url"]) });
    }

    private static async Task<IResult> SetPricingAsync(string value, HttpRequest request, ForgeContext context, Access access, Db db, Payments payments, Storage storage, PayloadVault vault)
    {
        CurrentUser user = context.RequireUser("Sign in to manage pricing.");
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send price_cents and/or uses_injector as JSON.");

        if (payload.Count == 0 || !payload.HasOnly("price_cents", "uses_injector"))
            throw ApiException.BadRequest("Send price_cents and/or uses_injector as JSON.");

        db.BeginImmediate();
        ModRow row = access.ResolveMod(value) ?? throw ApiException.NotFound("Mod not found.");

        if (row.Status == "deleted")
            throw ApiException.NotFound("Mod not found.");

        if (!access.CanPublish(user.Id, row.AuthorId))
            throw ApiException.Forbidden("Only the publisher can change pricing.");

        long price = row.PriceCents;
        bool injector = row.Injected;

        if (payload.ContainsKey("price_cents"))
        {
            if (!payload["price_cents"].IsInt(out price) || (price != 0 && (price < Payments.MinPriceCents || price > Payments.MaxPriceCents)))
                throw ApiException.BadRequest($"Set the price to 0 for a free mod, or between {Payments.MinPriceCents} and {Payments.MaxPriceCents} cents.");

            if (price > 0 && row.Visibility != "public")
                throw ApiException.Conflict("Make this mod public before setting a price.");

            if (price > 0 && !Payments.CanSell(payments.Seller(row)))
                throw ApiException.Conflict("Finish setting up payouts before selling this mod.");
        }

        if (payload.ContainsKey("uses_injector"))
        {
            if (!payload["uses_injector"].IsBool(out injector))
                throw ApiException.BadRequest("uses_injector must be true or false.");

            // Turning it off would make sealed binaries downloadable, which buyers and creators were promised won't happen.
            if (row.Injected && !injector)
                throw ApiException.BadRequest("Injector delivery can't be turned off once enabled. Contact an administrator if this was a mistake.");
        }

        bool sealing = injector && !row.Injected;
        var written = new List<string>();
        var replaced = new List<string>();

        try
        {
            if (sealing)
                SealExisting(row.Guid, db, storage, vault, written, replaced);

            // Obfuscated binaries defeat static scanning, so a public injector mod always goes back to a person for review.
            string status = sealing && row.Visibility == "public" && row.Status == "approved" ? "pending" : row.Status;

            db.Exec("UPDATE mods SET price_cents=@price,uses_injector=@injector,status=@status,updated_at=@now WHERE guid=@guid",
                new { price, injector = injector ? 1 : 0, status, now = Text.NowIso(), guid = row.Guid });
            db.Commit();

            Storage.DeleteAll(replaced);
            return Api.Json(new { price_cents = price, uses_injector = injector, status });
        }
        catch
        {
            db.Rollback();
            Storage.DeleteAll(written);
            throw;
        }
    }

    private static void SealExisting(string guid, Db db, Storage storage, PayloadVault vault, List<string> written, List<string> replaced)
    {
        var files = db.Query<(string Id, string StoredName)>(
            "SELECT f.id,f.stored_name FROM files f JOIN releases r ON r.id=f.release_id WHERE r.guid=@guid", new { guid }).ToList();

        foreach ((string id, string stored) in files)
        {
            if (PayloadVault.IsSealed(stored))
                continue;

            string sealedName = stored + PayloadVault.SealedSuffix;
            string source = storage.FilePath(stored);
            string target = storage.FilePath(sealedName);
            byte[] plain = File.ReadAllBytes(source);

            try
            {
                File.WriteAllBytes(target, vault.Seal(plain, sealedName));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
            }

            written.Add(target);
            replaced.Add(source);
            db.Exec("UPDATE files SET stored_name=@sealedName WHERE id=@id", new { sealedName, id });
        }
    }

    private static void LimitCheckouts(Db db, string userId)
    {
        // Card-testing bots love donation forms, so each account gets a handful of checkout attempts per hour.
        string since = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        long recent = db.Scalar<long>("SELECT COUNT(*) FROM payments WHERE buyer_id=@userId AND created_at>=@since", new { userId, since });

        if (recent >= MaxOpenCheckoutsPerHour)
            throw new ApiException(429, "Too many checkout attempts. Please wait a little while and try again.");
    }

    private static List<KeyValuePair<string, string>> CheckoutForm(ForgeOptions options, string name, long amount, string successUrl, string cancelUrl, string buyer, Dictionary<string, string> metadata)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", options.Currency),
            new("line_items[0][price_data][unit_amount]", amount.ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][price_data][product_data][name]", name),
            new("success_url", successUrl),
            new("cancel_url", cancelUrl),
            new("client_reference_id", buyer),
            // Stripe's minimum is 30 minutes after it creates the session, so exactly 30 from here lands just short of it.
            new("expires_at", DateTimeOffset.UtcNow.AddMinutes(35).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            // Newer Stripe accounts turn on Managed Payments by default, which rejects line items without a product tax code.
            // OpenForge sells through Connect as the platform, not with Stripe as merchant of record, so it opts out per session.
            new("managed_payments[enabled]", "false"),
        };

        // Metadata goes on the session and on the PaymentIntent, so refund and dispute events carry it too.
        foreach ((string key, string val) in metadata)
        {
            form.Add(new($"metadata[{key}]", val));
            form.Add(new($"payment_intent_data[metadata][{key}]", val));
        }

        return form;
    }

    private static void RecordCheckout(Db db, JsonObject session, string kind, string buyer, string? recipient, string? guid, long amount, long fee, string currency, string? stripeAccount)
    {
        string id = StripeClient.Str(session["id"]) ?? throw new ApiException(502, "The payment provider returned an unexpected response.");
        string now = Text.NowIso();

        db.Exec("""
            INSERT INTO payments(id,kind,buyer_id,recipient_id,guid,amount_cents,fee_cents,currency,stripe_account,status,created_at,updated_at)
            VALUES(@id,@kind,@buyer,@recipient,@guid,@amount,@fee,@currency,@stripeAccount,'open',@now,@now)
            """, new { id, kind, buyer, recipient, guid, amount, fee, currency, stripeAccount, now });
    }

    private static async Task<IResult> CheckoutAsync(string value, ForgeContext context, Access access, Db db, Payments payments, StripeClient stripe, ForgeOptions options)
    {
        CurrentUser user = context.RequireUser("Sign in to buy this mod.");
        stripe.RequireConfigured();

        ModRow row = access.ResolveMod(value) ?? throw ApiException.NotFound("Mod not found.");

        if (!access.CanViewMod(row) || row.Status != "approved")
            throw ApiException.NotFound("This mod isn't available to buy right now.");

        if (!row.Paid)
            throw ApiException.BadRequest("This mod is free.");

        if (payments.CanUse(row, user))
            throw ApiException.Conflict("You already own this mod.");

        UserRow? seller = payments.Seller(row);

        if (!Payments.CanSell(seller))
            throw ApiException.Conflict("This creator can't accept payments right now. Please try again later.");

        LimitCheckouts(db, user.Id);

        long amount = row.PriceCents;
        long fee = Payments.PlatformFee(amount);
        string page = options.PublicUrl + "/view/" + Uri.EscapeDataString(row.Guid);

        List<KeyValuePair<string, string>> form = CheckoutForm(options, row.Name, amount, page + "?checkout=success", page + "?checkout=cancelled", user.Id,
            new() { ["kind"] = "purchase", ["guid"] = row.Guid, ["buyer"] = user.Id });
        form.Add(new("payment_intent_data[application_fee_amount]", fee.ToString(CultureInfo.InvariantCulture)));
        StripeClient.AddBranding(form, options, seller!.PublicName);

        JsonObject session = await stripe.PostAsync("/v1/checkout/sessions", form, seller!.StripeAccountId);
        RecordCheckout(db, session, "purchase", user.Id, seller.Id, row.Guid, amount, fee, options.Currency, seller.StripeAccountId);

        return Api.Json(new { url = StripeClient.Str(session["url"]) });
    }

    private static async Task<IResult> DonateAsync(HttpRequest request, ForgeContext context, Db db, StripeClient stripe, ForgeOptions options)
    {
        CurrentUser user = context.RequireUser("Sign in to donate.");
        stripe.RequireConfigured();
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send recipient and amount_cents as JSON.");

        if (!payload.HasOnly("recipient", "amount_cents", "guid") || !payload["recipient"].IsString(out string recipient) || !payload["amount_cents"].IsInt(out long amount))
            throw ApiException.BadRequest("Send recipient and amount_cents as JSON.");

        if (amount < Payments.MinDonationCents || amount > Payments.MaxDonationCents)
            throw ApiException.BadRequest($"Donations must be between {Payments.MinDonationCents} and {Payments.MaxDonationCents} cents.");

        string? guid = payload["guid"].IsString(out string g) && g != "" ? g : null;
        LimitCheckouts(db, user.Id);

        string back = options.PublicUrl + (guid != null ? "/view/" + Uri.EscapeDataString(guid) : "/browse");
        string separator = back.Contains('?') ? "&" : "?";

        if (recipient == "platform")
        {
            List<KeyValuePair<string, string>> form = CheckoutForm(options, "Donation to " + options.BrandName, amount, back + separator + "donation=success", back + separator + "donation=cancelled", user.Id,
                new() { ["kind"] = "platform_donation", ["buyer"] = user.Id });
            StripeClient.AddBranding(form, options);

            JsonObject session = await stripe.PostAsync("/v1/checkout/sessions", form);
            RecordCheckout(db, session, "platform_donation", user.Id, null, null, amount, 0, options.Currency, null);

            return Api.Json(new { url = StripeClient.Str(session["url"]) });
        }

        UserRow? creator = db.One<UserRow>("SELECT * FROM users WHERE id=@recipient", new { recipient });

        if (creator == null || creator.Id == user.Id)
            throw ApiException.BadRequest("Choose a creator to donate to.");

        if (!Payments.CanReceiveDonations(creator))
            throw ApiException.Conflict("This creator can't receive donations right now.");

        if (guid != null && !db.Exists("SELECT 1 FROM mods WHERE guid=@guid AND author_id=@id", new { guid, id = creator.Id }))
            guid = null;

        List<KeyValuePair<string, string>> donation = CheckoutForm(options, "Donation to " + creator.PublicName, amount, back + separator + "donation=success", back + separator + "donation=cancelled", user.Id,
            new() { ["kind"] = "creator_donation", ["buyer"] = user.Id, ["recipient"] = creator.Id });

        // Destination charge with no application fee: the full amount reaches the creator and MonkeForge covers Stripe's fee.
        donation.Add(new("payment_intent_data[transfer_data][destination]", creator.StripeAccountId!));
        StripeClient.AddBranding(donation, options);

        JsonObject created = await stripe.PostAsync("/v1/checkout/sessions", donation);
        RecordCheckout(db, created, "creator_donation", user.Id, creator.Id, guid, amount, 0, options.Currency, null);

        return Api.Json(new { url = StripeClient.Str(created["url"]) });
    }

    private static IResult Library(ForgeContext context, Db db)
    {
        CurrentUser user = context.RequireUser("Sign in to see your purchases.");

        List<LibraryRow> owned = db.Query<LibraryRow>("""
            SELECT e.guid,e.granted_at,m.name,m.logo,m.custom_slug,m.uses_injector,m.status,COALESCE(p.amount_cents,0) amount_cents,COALESCE(p.currency,'') currency,
                   p.id payment_id,p.status payment_status,(SELECT d.status FROM staff_disputes d WHERE d.payment_id=p.id ORDER BY d.created_at DESC LIMIT 1) dispute_status
            FROM entitlements e LEFT JOIN mods m ON m.guid=e.guid LEFT JOIN payments p ON p.id=e.payment_id
            WHERE e.user_id=@id AND e.revoked_at IS NULL ORDER BY e.granted_at DESC
            """, new { id = user.Id }).ToList();

        return Api.Json(new
        {
            items = owned.Select(o => new
            {
                guid = o.Guid,
                name = o.Name ?? o.Guid,
                logo = o.Logo,
                url = o.Status is null or "deleted" ? null : "/view/" + o.Guid,
                uses_injector = o.UsesInjector != 0,
                available = o.Status is not (null or "deleted"),
                amount_cents = o.AmountCents,
                currency = o.Currency,
                purchased_at = o.GrantedAt,
                payment_id = o.PaymentId,
                dispute_status = o.DisputeStatus,
                can_dispute = o.PaymentId != null && o.PaymentStatus == "paid" && o.DisputeStatus != "open",
            }),
        });
    }

    private static async Task<IResult> WebhookAsync(HttpRequest request, Db db, Payments payments, Subscriptions subscriptions, StripeClient stripe, ILoggerFactory loggers, Diagnostics diagnostics)
    {
        stripe.RequireConfigured();
        ILogger logger = loggers.CreateLogger("StripeWebhook");

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16384];
        int read;

        while ((read = await request.Body.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > MaxWebhookBytes)
                throw new ApiException(413);
        }

        string payload = Encoding.UTF8.GetString(buffer.ToArray());

        if (!stripe.VerifySignature(payload, request.Headers["Stripe-Signature"].ToString()))
            throw ApiException.BadRequest("Invalid signature.");

        JsonObject evt = JsonBody.ParseObject(payload) ?? throw ApiException.BadRequest("Invalid event.");
        string id = StripeClient.Str(evt["id"]) ?? throw ApiException.BadRequest("Invalid event.");
        string type = StripeClient.Str(evt["type"]) ?? "";
        string? account = StripeClient.Str(evt["account"]);
        JsonObject obj = evt["data"]?["object"] as JsonObject ?? throw ApiException.BadRequest("Invalid event.");

        if (!HandledEvents.Contains(type))
            return Api.Ok();

        db.BeginImmediate();

        PruneStripeEvents(db);

        // Stripe retries deliveries, so each event is applied at most once.
        if (db.Exec("INSERT OR IGNORE INTO stripe_events(id,type,received_at) VALUES(@id,@type,@now)", new { id, type, now = Text.NowIso() }) == 0)
        {
            db.Commit();
            return Api.Ok();
        }

        string? stopAfterCommit = null;

        PaymentRow? ByIntent(string? intent)
        {
            return intent == null ? null : db.One<PaymentRow>("SELECT * FROM payments WHERE payment_intent=@intent", new { intent });
        }

        switch (type)
        {
            case "checkout.session.completed" when StripeClient.Str(obj["mode"]) == "subscription":
                subscriptions.CheckoutCompleted(obj, account);
                break;
            case "customer.subscription.created":
            case "customer.subscription.updated":
            case "customer.subscription.deleted":
                subscriptions.SubscriptionChanged(obj, account, type == "customer.subscription.deleted");
                break;
            case "invoice.paid":
                subscriptions.InvoicePaid(obj, account);
                break;
            case "checkout.session.completed":
            case "checkout.session.async_payment_succeeded":
            {
                PaymentRow? payment = db.One<PaymentRow>("SELECT * FROM payments WHERE id=@sid", new { sid = StripeClient.Str(obj["id"]) });

                if (payment == null || StripeClient.Str(obj["payment_status"]) != "paid")
                    break;

                // The session must have come from the account we created it on, for the amount we asked for.
                bool consistent = payment.StripeAccount == account
                    && StripeClient.Long(obj["amount_total"]) == payment.AmountCents
                    && StripeClient.Str(obj["currency"]) == payment.Currency;

                if (!consistent)
                {
                    logger.LogError("Stripe session {Session} does not match its recorded payment; access was not granted", payment.Id);
                    diagnostics.Report("Stripe webhook", $"Checkout session {payment.Id} does not match its recorded payment, so access was not granted.",
                        $"Expected account {payment.StripeAccount ?? "platform"}, {payment.AmountCents} {payment.Currency}.\nGot account {account ?? "platform"}, {StripeClient.Long(obj["amount_total"])} {StripeClient.Str(obj["currency"])}.");
                    break;
                }

                payments.MarkPaid(payment, StripeClient.Str(obj["payment_intent"]));
                break;
            }
            case "checkout.session.async_payment_failed":
                db.Exec("UPDATE payments SET status='failed',updated_at=@now WHERE id=@sid AND status='open'", new { now = Text.NowIso(), sid = StripeClient.Str(obj["id"]) });
                break;
            case "checkout.session.expired":
                db.Exec("UPDATE payments SET status='expired',updated_at=@now WHERE id=@sid AND status='open'", new { now = Text.NowIso(), sid = StripeClient.Str(obj["id"]) });
                break;
            case "charge.refunded":
            {
                // Partial refunds keep access; only a full refund takes the mod away.
                PaymentRow? payment = ByIntent(StripeClient.Str(obj["payment_intent"]));

                if (payment != null && StripeClient.Flag(obj["refunded"]))
                    payments.SetReversed(payment, "refunded");

                break;
            }
            case "charge.dispute.created":
            {
                PaymentRow? payment = ByIntent(StripeClient.Str(obj["payment_intent"]));

                if (payment != null)
                    payments.SetReversed(payment, "disputed");

                // Someone charging back a membership payment shouldn't keep being billed for it.
                if (payment?.SubscriptionId != null)
                    stopAfterCommit = payment.SubscriptionId;

                break;
            }
            case "charge.dispute.closed":
            {
                PaymentRow? payment = ByIntent(StripeClient.Str(obj["payment_intent"]));

                if (payment != null && StripeClient.Str(obj["status"]) == "won")
                    payments.Restore(payment);

                break;
            }
            case "account.updated":
            {
                string? accountId = StripeClient.Str(obj["id"]);

                if (accountId != null)
                    payments.SaveAccountState(accountId, obj);

                break;
            }
        }

        db.Commit();

        if (stopAfterCommit != null && subscriptions.Get(stopAfterCommit) is { } disputed)
        {
            try
            {
                await subscriptions.CancelAsync(disputed, true);
            }
            catch (ApiException)
            {
                logger.LogError("Could not cancel disputed subscription {Subscription}; cancel it in Stripe", disputed.Id);
                diagnostics.Report("Stripe webhook", $"Could not cancel disputed subscription {disputed.Id}. Cancel it in the Stripe dashboard.");
            }
        }

        return Api.Ok();
    }

    // Runs inside the webhook's transaction, at most once an hour per process.
    private static void PruneStripeEvents(Db db)
    {
        long now = DateTime.UtcNow.Ticks;
        long next = Interlocked.Read(ref _nextStripeEventPruneTicks);

        if (now < next || Interlocked.CompareExchange(ref _nextStripeEventPruneTicks, now + StripeEventPruneInterval.Ticks, next) != next)
            return;

        // Same fixed-width format as Text.NowIso, so the string comparison orders correctly.
        string cutoff = DateTime.UtcNow.Subtract(StripeEventRetention).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
        db.Exec("DELETE FROM stripe_events WHERE received_at < @cutoff", new { cutoff });
    }
}
