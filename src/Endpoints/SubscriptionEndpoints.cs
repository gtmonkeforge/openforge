using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using OpenForge.Infrastructure;
using OpenForge.Models;
using OpenForge.Services;

namespace OpenForge.Endpoints;

// Monthly tiers, the buyer's view of what they pay for, and disputes reviewed by staff.
// A mod tier unlocks one mod. A creator tier (no guid) unlocks whichever of the creator's mods they attach to it.
public static class SubscriptionEndpoints
{
    public const long MinTierCents = 100;
    public const long MaxTierCents = 50_000;
    private const int MaxTiersPerMod = 5;
    private const int MaxTiersPerCreator = 10;
    private const int MaxBenefits = 10;
    private const int DisputeWindowDays = 90;

    private sealed class MySubscriptionRow
    {
        public string Id { get; set; } = "";
        public string TierId { get; set; } = "";
        public string Status { get; set; } = "";
        public int CancelAtPeriodEnd { get; set; }
        public string? CurrentPeriodEnd { get; set; }
        public long PriceCents { get; set; }
        public string Currency { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string TierName { get; set; } = "";
        public string? Guid { get; set; }
        public string? ModName { get; set; }
        public string? ModStatus { get; set; }
        public string OwnerId { get; set; } = "";
        public string OwnerName { get; set; } = "";
        public string? DisputeStatus { get; set; }
        public long PaidCount { get; set; }
    }

    private sealed class AdminDisputeRow
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "";
        public string Reason { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string? ResolvedAt { get; set; }
        public string? StaffNote { get; set; }
        public long RefundedCents { get; set; }
        public string? PaymentId { get; set; }
        public string? SubscriptionId { get; set; }
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        public string? SellerId { get; set; }
        public string? SellerName { get; set; }
        public string? Guid { get; set; }
        public string? ModName { get; set; }
        public string? TierName { get; set; }
        public long? AmountCents { get; set; }
        public string? Currency { get; set; }
        public string? PaidAt { get; set; }
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/users/{userId}/tiers", CreatorTiers);
        app.MapPost("/api/v1/tiers", CreateTierAsync);
        app.MapPatch("/api/v1/tiers/{tierId}", UpdateTierAsync);
        app.MapDelete("/api/v1/tiers/{tierId}", ArchiveTierAsync);
        app.MapPost("/api/v1/tiers/{tierId}/subscribe", SubscribeAsync);
        app.MapGet("/api/v1/subscriptions", MySubscriptions);
        app.MapPost("/api/v1/subscriptions/{subscriptionId}/cancel", CancelAsync);
        app.MapGet("/api/v1/disputes", MyDisputes);
        app.MapPost("/api/v1/disputes", OpenDisputeAsync);
        app.MapGet("/api/v1/admin/disputes", AdminDisputes);
        app.MapPost("/api/v1/admin/disputes/{disputeId}/resolve", ResolveDisputeAsync);
    }

    // ---- Presenting tiers ----

    public static TierDto Present(TierRow tier, Db db, ForgeOptions options, string? viewerId, bool withMods)
    {
        string ownerName = db.One<string>("SELECT COALESCE(public_username,display_name) FROM users WHERE id=@id", new { id = tier.OwnerId }) ?? "";

        return new TierDto
        {
            Id = tier.Id,
            OwnerId = tier.OwnerId,
            OwnerName = ownerName,
            Guid = tier.Guid,
            Scope = tier.Guid == null ? "creator" : "mod",
            Name = tier.Name,
            Benefits = JsonBody.StringList(tier.Benefits),
            PriceCents = tier.PriceCents,
            Currency = options.Currency,
            Subscribed = viewerId != null && db.Exists("SELECT 1 FROM subscriptions WHERE tier_id=@id AND user_id=@viewerId AND status IN ('active','trialing','past_due')", new { id = tier.Id, viewerId }),
            Mods = withMods
                ? db.Query<TierModDto>("SELECT m.guid,m.name FROM tier_mods tm JOIN mods m ON m.guid=tm.guid WHERE tm.tier_id=@id AND m.status='approved' AND m.visibility='public' ORDER BY m.name COLLATE NOCASE", new { id = tier.Id }).ToList()
                : null,
        };
    }

    // Every live tier that unlocks a mod: its own first, then creator memberships that include it.
    public static List<TierRow> TiersForMod(Db db, string guid)
    {
        return db.Query<TierRow>("""
            SELECT t.* FROM subscription_tiers t WHERE t.archived_at IS NULL
            AND (t.guid=@guid OR EXISTS(SELECT 1 FROM tier_mods tm WHERE tm.tier_id=t.id AND tm.guid=@guid))
            ORDER BY t.guid IS NULL, t.price_cents, t.position
            """, new { guid }).ToList();
    }

    private static IResult CreatorTiers(string userId, ForgeContext context, Access access, Db db, ForgeOptions options)
    {
        UserRow owner = access.ResolveUser(userId) ?? throw ApiException.NotFound("This profile could not be found.");
        CurrentUser? viewer = context.User;
        bool manage = viewer != null && CanManageCreator(viewer, owner.Id, access);

        List<TierRow> tiers = db.Query<TierRow>("SELECT * FROM subscription_tiers WHERE owner_id=@id AND guid IS NULL AND archived_at IS NULL ORDER BY price_cents,position", new { id = owner.Id }).ToList();
        object? mods = manage
            ? db.Query<TierModDto>("SELECT guid,name FROM mods WHERE author_id=@id AND status!='deleted' ORDER BY name COLLATE NOCASE", new { id = owner.Id }).ToList()
            : null;

        return Api.Json(new
        {
            can_manage = manage,
            can_sell = Payments.CanSell(owner),
            items = tiers.Select(t => Present(t, db, options, viewer?.Id, true)),
            linked = manage ? tiers.ToDictionary(t => t.Id, t => db.Query<string>("SELECT guid FROM tier_mods WHERE tier_id=@id", new { id = t.Id }).ToList()) : null,
            mods,
        });
    }

    // ---- Managing tiers ----

    private static bool CanManageCreator(CurrentUser user, string ownerId, Access access)
    {
        return user.Id == ownerId || access.CanTransfer(user.Id, ownerId);
    }

    private sealed record TierInput(string? Name, long? Price, List<string>? Benefits, List<string>? Mods);

    private static TierInput ReadTier(JsonObject payload, bool creating)
    {
        string? name = null;
        long? price = null;
        List<string>? benefits = null;
        List<string>? mods = null;

        if (payload.ContainsKey("name") || creating)
        {
            if (!payload["name"].IsString(out string text) || Text.Len(text.Trim()) is < 1 or > 60)
                throw ApiException.BadRequest("Tier names must be 1–60 characters.");

            name = text.Trim();
        }

        if (payload.ContainsKey("price_cents") || creating)
        {
            if (!payload["price_cents"].IsInt(out long value) || value < MinTierCents || value > MaxTierCents)
                throw ApiException.BadRequest($"Monthly prices must be between {MinTierCents} and {MaxTierCents} cents.");

            price = value;
        }

        if (payload.ContainsKey("benefits"))
        {
            if (payload["benefits"] is not JsonArray list || list.Count > MaxBenefits)
                throw ApiException.BadRequest($"List up to {MaxBenefits} benefits.");

            benefits = [];

            foreach (JsonNode? item in list)
            {
                if (!item.IsString(out string benefit) || Text.Len(benefit.Trim()) is < 1 or > 200)
                    throw ApiException.BadRequest("Each benefit must be 1–200 characters.");

                benefits.Add(benefit.Trim());
            }
        }

        if (payload.ContainsKey("mods"))
        {
            if (payload["mods"] is not JsonArray list || list.Count > 100 || list.Any(m => !m.IsString(out _)))
                throw ApiException.BadRequest("mods must be a list of mod GUIDs.");

            mods = list.Select(m => m!.ToString()).Distinct().ToList();
        }

        return new TierInput(name, price, benefits, mods);
    }

    private static void LinkMods(Db db, string tierId, string ownerId, List<string> mods)
    {
        foreach (string guid in mods)
        {
            if (!db.Exists("SELECT 1 FROM mods WHERE guid=@guid AND author_id=@ownerId AND status!='deleted' AND visibility='public'", new { guid, ownerId }))
                throw ApiException.BadRequest("Creator tiers can only include your own public mods.");

            if (ExtensionEndpoints.IsExtension(db, guid))
                throw ApiException.BadRequest("Extensions are always free, so creator tiers can't include them.");
        }

        db.Exec("DELETE FROM tier_mods WHERE tier_id=@tierId", new { tierId });

        foreach (string guid in mods)
            db.Exec("INSERT INTO tier_mods(tier_id,guid) VALUES(@tierId,@guid)", new { tierId, guid });
    }

    private static async Task<IResult> CreateTierAsync(HttpRequest request, ForgeContext context, Access access, Db db, ForgeOptions options)
    {
        CurrentUser user = context.RequireUser("Sign in to create tiers.");
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send the tier as JSON.");

        if (!payload.HasOnly("guid", "owner_id", "name", "price_cents", "benefits", "mods"))
            throw ApiException.BadRequest("Send guid or owner_id, plus name, price_cents, benefits and mods.");

        TierInput input = ReadTier(payload, true);
        string? guid = payload["guid"].IsString(out string g) && g != "" ? g : null;
        string ownerId;

        db.BeginImmediate();

        if (guid != null)
        {
            ModRow mod = access.ResolveMod(guid) ?? throw ApiException.NotFound("Mod not found.");

            if (mod.Status == "deleted" || !access.CanPublish(user.Id, mod.AuthorId))
                throw ApiException.Forbidden("Only the publisher can add tiers to this mod.");

            if (input.Mods is { Count: > 0 })
                throw ApiException.BadRequest("Mod tiers always unlock their own mod. Use a creator tier to bundle several mods.");

            if (ExtensionEndpoints.IsExtension(db, mod.Guid))
                throw ApiException.Conflict("Extensions are always free, so they can't have paid tiers.");

            if (mod.Visibility != "public")
                throw ApiException.Conflict("Make this mod public before adding paid tiers.");

            guid = mod.Guid;
            ownerId = mod.AuthorId;

            if (db.Scalar<long>("SELECT COUNT(*) FROM subscription_tiers WHERE guid=@guid AND archived_at IS NULL", new { guid }) >= MaxTiersPerMod)
                throw ApiException.Conflict($"A mod can have up to {MaxTiersPerMod} tiers.");
        }
        else
        {
            ownerId = payload["owner_id"].IsString(out string o) && o != "" ? o : user.Id;

            if (!CanManageCreator(user, ownerId, access))
                throw ApiException.Forbidden("Only the creator or a group owner can add membership tiers.");

            if (db.Scalar<long>("SELECT COUNT(*) FROM subscription_tiers WHERE owner_id=@ownerId AND guid IS NULL AND archived_at IS NULL", new { ownerId }) >= MaxTiersPerCreator)
                throw ApiException.Conflict($"You can have up to {MaxTiersPerCreator} membership tiers.");
        }

        if (!Payments.CanSell(db.One<UserRow>("SELECT * FROM users WHERE id=@ownerId", new { ownerId })))
            throw ApiException.Conflict("Finish setting up payouts before adding paid tiers.");

        string id = Text.Hex(12);
        long position = db.Scalar<long>("SELECT COALESCE(MAX(position),0)+1 FROM subscription_tiers WHERE owner_id=@ownerId", new { ownerId });

        db.Exec("INSERT INTO subscription_tiers(id,owner_id,guid,name,benefits,price_cents,position,created_at) VALUES(@id,@ownerId,@guid,@name,@benefits,@price,@position,@now)",
            new { id, ownerId, guid, name = input.Name, benefits = JsonBody.Serialize(input.Benefits ?? []), price = input.Price, position, now = Text.NowIso() });

        if (guid == null && input.Mods != null)
            LinkMods(db, id, ownerId, input.Mods);

        db.Commit();

        TierRow created = db.One<TierRow>("SELECT * FROM subscription_tiers WHERE id=@id", new { id })!;
        return Api.Json(Present(created, db, options, user.Id, true), 201);
    }

    private static TierRow ManageableTier(string tierId, CurrentUser user, Access access, Db db)
    {
        TierRow tier = db.One<TierRow>("SELECT * FROM subscription_tiers WHERE id=@tierId AND archived_at IS NULL", new { tierId })
            ?? throw ApiException.NotFound("Tier not found.");

        bool allowed = tier.Guid != null
            ? db.One<string>("SELECT author_id FROM mods WHERE guid=@guid", new { guid = tier.Guid }) is string author && access.CanPublish(user.Id, author)
            : CanManageCreator(user, tier.OwnerId, access);

        if (!allowed)
            throw ApiException.Forbidden("You can't manage this tier.");

        return tier;
    }

    // Price changes apply to new subscribers; people already subscribed keep the price they signed up at.
    private static async Task<IResult> UpdateTierAsync(string tierId, HttpRequest request, ForgeContext context, Access access, Db db, ForgeOptions options)
    {
        CurrentUser user = context.RequireUser("Sign in to manage tiers.");
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send the changes as JSON.");

        if (payload.Count == 0 || !payload.HasOnly("name", "price_cents", "benefits", "mods"))
            throw ApiException.BadRequest("Only name, price_cents, benefits and mods can change.");

        TierInput input = ReadTier(payload, false);
        db.BeginImmediate();
        TierRow tier = ManageableTier(tierId, user, access, db);

        if (input.Mods != null && tier.Guid != null)
            throw ApiException.BadRequest("Mod tiers always unlock their own mod.");

        db.Exec("UPDATE subscription_tiers SET name=COALESCE(@name,name),price_cents=COALESCE(@price,price_cents),benefits=COALESCE(@benefits,benefits) WHERE id=@id",
            new { name = input.Name, price = input.Price, benefits = input.Benefits == null ? null : JsonBody.Serialize(input.Benefits), id = tier.Id });

        if (input.Mods != null)
            LinkMods(db, tier.Id, tier.OwnerId, input.Mods);

        db.Commit();
        return Api.Json(Present(db.One<TierRow>("SELECT * FROM subscription_tiers WHERE id=@id", new { id = tier.Id })!, db, options, user.Id, true));
    }

    // Retiring a tier stops new sign-ups and ends current subscriptions at the end of what each person already paid for.
    private static async Task<IResult> ArchiveTierAsync(string tierId, ForgeContext context, Access access, Db db, Subscriptions subscriptions)
    {
        CurrentUser user = context.RequireUser("Sign in to manage tiers.");
        TierRow tier = ManageableTier(tierId, user, access, db);

        db.Exec("UPDATE subscription_tiers SET archived_at=@now WHERE id=@id", new { now = Text.NowIso(), id = tier.Id });
        (int cancelled, int failed) = await subscriptions.CancelManyAsync(subscriptions.LiveForTier(tier.Id), false);

        return Api.Json(new { ok = true, subscriptions_ending = cancelled, subscriptions_failed = failed });
    }

    // ---- Subscribing ----

    private static async Task<IResult> SubscribeAsync(string tierId, ForgeContext context, Access access, Db db, StripeClient stripe, Subscriptions subscriptions)
    {
        CurrentUser user = context.RequireUser("Sign in to subscribe.");
        stripe.RequireConfigured();

        TierRow tier = db.One<TierRow>("SELECT * FROM subscription_tiers WHERE id=@tierId AND archived_at IS NULL", new { tierId })
            ?? throw ApiException.NotFound("This tier isn't available anymore.");
        UserRow? seller = db.One<UserRow>("SELECT * FROM users WHERE id=@id", new { id = tier.OwnerId });

        if (!Payments.CanSell(seller))
            throw ApiException.Conflict("This creator can't accept payments right now. Please try again later.");

        if (user.Id == tier.OwnerId || access.CanPublish(user.Id, tier.OwnerId))
            throw ApiException.BadRequest("You can't subscribe to your own tier.");

        string productName;
        string returnPath;

        if (tier.Guid != null)
        {
            ModRow? mod = access.ResolveMod(tier.Guid);

            if (mod == null || !access.CanViewMod(mod) || mod.Status != "approved")
                throw ApiException.NotFound("This mod isn't available right now.");

            productName = tier.Name + " · " + mod.Name;
            returnPath = "/view/" + Uri.EscapeDataString(mod.Guid);
        }
        else
        {
            productName = tier.Name + " · " + seller!.PublicName;
            returnPath = Text.ProfileUrl(seller.Id, seller.CustomSlug, seller.Verified);
        }

        if (db.Exists("SELECT 1 FROM subscriptions WHERE tier_id=@tierId AND user_id=@uid AND status IN ('active','trialing','past_due')", new { tierId, uid = user.Id }))
            throw ApiException.Conflict("You're already subscribed to this tier.");

        string since = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        if (db.Scalar<long>("SELECT COUNT(*) FROM subscriptions WHERE user_id=@uid AND created_at>=@since", new { uid = user.Id, since }) >= 10)
            throw new ApiException(429, "Too many checkout attempts. Please wait a little while and try again.");

        string url = await subscriptions.CheckoutAsync(tier, seller!, user, productName, returnPath);
        return Api.Json(new { url });
    }

    private static IResult MySubscriptions(ForgeContext context, Db db)
    {
        CurrentUser user = context.RequireUser("Sign in to see your subscriptions.");

        List<MySubscriptionRow> rows = db.Query<MySubscriptionRow>("""
            SELECT s.id,s.tier_id,s.status,s.cancel_at_period_end,s.current_period_end,s.price_cents,s.currency,s.created_at,
                   t.name tier_name,t.guid,m.name mod_name,m.status mod_status,s.owner_id,COALESCE(u.public_username,u.display_name) owner_name,
                   (SELECT d.status FROM staff_disputes d WHERE d.subscription_id=s.id ORDER BY d.created_at DESC LIMIT 1) dispute_status,
                   (SELECT COUNT(*) FROM payments p WHERE p.subscription_id=s.id AND p.status='paid') paid_count
            FROM subscriptions s JOIN subscription_tiers t ON t.id=s.tier_id LEFT JOIN mods m ON m.guid=t.guid JOIN users u ON u.id=s.owner_id
            WHERE s.user_id=@id AND s.status!='pending' ORDER BY s.status IN ('active','trialing','past_due') DESC, s.created_at DESC
            """, new { id = user.Id }).ToList();

        return Api.Json(new
        {
            items = rows.Select(r => new
            {
                id = r.Id,
                tier_name = r.TierName,
                scope = r.Guid == null ? "creator" : "mod",
                guid = r.Guid,
                mod_name = r.ModName,
                url = r.Guid == null ? "/profile/" + r.OwnerId : r.ModStatus is null or "deleted" ? null : "/view/" + r.Guid,
                owner_id = r.OwnerId,
                owner_name = r.OwnerName,
                status = r.Status,
                live = SubscriptionRow.GrantingStatuses.Contains(r.Status),
                cancel_at_period_end = r.CancelAtPeriodEnd != 0,
                current_period_end = r.CurrentPeriodEnd,
                price_cents = r.PriceCents,
                currency = r.Currency,
                created_at = r.CreatedAt,
                dispute_status = r.DisputeStatus,
                can_dispute = r.PaidCount > 0 && r.DisputeStatus != "open",
            }),
        });
    }

    private static async Task<IResult> CancelAsync(string subscriptionId, ForgeContext context, Subscriptions subscriptions)
    {
        CurrentUser user = context.RequireUser("Sign in to manage subscriptions.");
        SubscriptionRow sub = subscriptions.Get(subscriptionId) is { } found && found.UserId == user.Id ? found : throw ApiException.NotFound("Subscription not found.");

        if (!sub.Granting)
            throw ApiException.Conflict("This subscription has already ended.");

        if (sub.CancelAtPeriodEnd != 0)
            return Api.Json(new { ok = true, ends_at = sub.CurrentPeriodEnd });

        await subscriptions.CancelAsync(sub, false);
        return Api.Json(new { ok = true, ends_at = sub.CurrentPeriodEnd });
    }

    // ---- Disputes reviewed by staff ----

    private static IResult MyDisputes(ForgeContext context, Db db)
    {
        CurrentUser user = context.RequireUser("Sign in to see your disputes.");
        List<DisputeRow> rows = db.Query<DisputeRow>("SELECT * FROM staff_disputes WHERE user_id=@id ORDER BY created_at DESC", new { id = user.Id }).ToList();

        return Api.Json(new
        {
            items = rows.Select(d => new { id = d.Id, payment_id = d.PaymentId, subscription_id = d.SubscriptionId, status = d.Status, reason = d.Reason, staff_note = d.StaffNote, refunded_cents = d.RefundedCents, created_at = d.CreatedAt, resolved_at = d.ResolvedAt }),
        });
    }

    private static async Task<IResult> OpenDisputeAsync(HttpRequest request, ForgeContext context, Db db, Subscriptions subscriptions)
    {
        CurrentUser user = context.RequireUser("Sign in to open a dispute.");
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send payment_id or subscription_id, and a reason.");

        if (!payload.HasOnly("payment_id", "subscription_id", "reason") || !payload["reason"].IsString(out string reason))
            throw ApiException.BadRequest("Send payment_id or subscription_id, and a reason.");

        reason = reason.Trim();

        if (Text.Len(reason) is < 10 or > 2000)
            throw ApiException.BadRequest("Tell us what went wrong in 10–2,000 characters.");

        string? paymentId = payload["payment_id"].IsString(out string p) ? p : null;
        string? subscriptionId = payload["subscription_id"].IsString(out string s) ? s : null;

        if ((paymentId == null) == (subscriptionId == null))
            throw ApiException.BadRequest("Choose one purchase or one subscription.");

        string cutoff = DateTime.UtcNow.AddDays(-DisputeWindowDays).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        string? sellerId;
        string? guid;

        if (paymentId != null)
        {
            PaymentRow payment = db.One<PaymentRow>("SELECT * FROM payments WHERE id=@paymentId AND buyer_id=@uid AND kind='purchase'", new { paymentId, uid = user.Id })
                ?? throw ApiException.NotFound("Purchase not found.");

            if (payment.Status != "paid")
                throw ApiException.Conflict("Only completed purchases can be disputed.");

            if (string.CompareOrdinal(payment.CreatedAt, cutoff) < 0)
                throw ApiException.Conflict($"Purchases can be disputed within {DisputeWindowDays} days.");

            (sellerId, guid) = (payment.RecipientId, payment.Guid);
        }
        else
        {
            SubscriptionRow sub = subscriptions.Get(subscriptionId!) is { } found && found.UserId == user.Id ? found : throw ApiException.NotFound("Subscription not found.");

            if (!db.Exists("SELECT 1 FROM payments WHERE subscription_id=@id AND status='paid' AND created_at>=@cutoff", new { id = sub.Id, cutoff }))
                throw ApiException.Conflict($"Subscriptions can be disputed within {DisputeWindowDays} days of a payment.");

            sellerId = sub.OwnerId;
            guid = db.One<string>("SELECT guid FROM subscription_tiers WHERE id=@id", new { id = sub.TierId });
        }

        string id = Text.Hex(12);

        try
        {
            db.BeginImmediate();
            db.Exec("INSERT INTO staff_disputes(id,user_id,payment_id,subscription_id,seller_id,guid,reason,created_at) VALUES(@id,@uid,@paymentId,@subscriptionId,@sellerId,@guid,@reason,@now)",
                new { id, uid = user.Id, paymentId, subscriptionId, sellerId, guid, reason, now = Text.NowIso() });

            foreach (string admin in db.Query<string>("SELECT id FROM users WHERE is_admin=1 AND is_banned=0"))
                ManagementEndpoints.Notify(db, admin, "dispute_staff", "A buyer opened a payment dispute.", id);

            db.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw ApiException.Conflict("You already have an open dispute for this. Staff will get back to you.");
        }

        return Api.Json(new { id, status = "open" }, 201);
    }

    private static IResult AdminDisputes(HttpRequest request, ForgeContext context, Db db)
    {
        AdminEndpoints.RequireAdmin(context);
        string status = request.Query["status"].FirstOrDefault() ?? "open";

        if (status is not ("open" or "accepted" or "rejected" or "all"))
            throw ApiException.BadRequest("Choose open, accepted, rejected or all.");

        List<AdminDisputeRow> rows = db.Query<AdminDisputeRow>("""
            SELECT d.id,d.status,d.reason,d.created_at,d.resolved_at,d.staff_note,d.refunded_cents,d.payment_id,d.subscription_id,
                   d.user_id,COALESCE(u.public_username,u.display_name) user_name,d.seller_id,COALESCE(s.public_username,s.display_name) seller_name,
                   d.guid,m.name mod_name,t.name tier_name,
                   COALESCE(p.amount_cents,lp.amount_cents) amount_cents,COALESCE(p.currency,lp.currency) currency,COALESCE(p.created_at,lp.created_at) paid_at
            FROM staff_disputes d JOIN users u ON u.id=d.user_id LEFT JOIN users s ON s.id=d.seller_id LEFT JOIN mods m ON m.guid=d.guid
            LEFT JOIN payments p ON p.id=d.payment_id
            LEFT JOIN subscriptions sub ON sub.id=d.subscription_id LEFT JOIN subscription_tiers t ON t.id=sub.tier_id
            LEFT JOIN payments lp ON lp.id=(SELECT id FROM payments WHERE subscription_id=d.subscription_id AND status='paid' ORDER BY created_at DESC LIMIT 1)
            WHERE @status='all' OR d.status=@status ORDER BY d.created_at DESC LIMIT 200
            """, new { status }).ToList();

        return Api.Json(new
        {
            items = rows.Select(r => new
            {
                id = r.Id, status = r.Status, reason = r.Reason, created_at = r.CreatedAt, resolved_at = r.ResolvedAt, staff_note = r.StaffNote,
                refunded_cents = r.RefundedCents, kind = r.PaymentId != null ? "purchase" : "subscription",
                user = new { id = r.UserId, name = r.UserName }, seller = r.SellerId == null ? null : new { id = r.SellerId, name = r.SellerName },
                guid = r.Guid, mod_name = r.ModName, tier_name = r.TierName, amount_cents = r.AmountCents, currency = r.Currency, paid_at = r.PaidAt,
            }),
        });
    }

    // Accepting refunds the purchase, or the latest payment of a subscription and ends it. Rejecting just closes it with a note.
    private static async Task<IResult> ResolveDisputeAsync(string disputeId, HttpRequest request, ForgeContext context, Db db, Payments payments, Subscriptions subscriptions)
    {
        CurrentUser admin = AdminEndpoints.RequireAdmin(context);
        JsonObject payload = await JsonBody.RequireObjectAsync(request, "Send action and an optional note.");

        if (!payload.HasOnly("action", "note") || !payload["action"].IsString(out string action) || action is not ("accept" or "reject"))
            throw ApiException.BadRequest("Choose accept or reject.");

        string note = payload["note"].IsString(out string n) ? n.Trim() : "";

        if (Text.Len(note) > 2000)
            throw ApiException.BadRequest("Notes can be up to 2,000 characters.");

        DisputeRow dispute = db.One<DisputeRow>("SELECT * FROM staff_disputes WHERE id=@disputeId AND status='open'", new { disputeId })
            ?? throw ApiException.NotFound("Open dispute not found.");

        long refunded = 0;
        PaymentRow? payment = null;

        if (action == "accept")
        {
            payment = dispute.PaymentId != null
                ? db.One<PaymentRow>("SELECT * FROM payments WHERE id=@id", new { id = dispute.PaymentId })
                : db.One<PaymentRow>("SELECT * FROM payments WHERE subscription_id=@id AND status='paid' ORDER BY created_at DESC LIMIT 1", new { id = dispute.SubscriptionId });

            if (payment == null || payment.Status != "paid")
                throw ApiException.Conflict("There's no completed payment left to refund. Reject the dispute with a note instead.");

            await subscriptions.RefundAsync(payment);
            refunded = payment.AmountCents;

            if (dispute.SubscriptionId != null && subscriptions.Get(dispute.SubscriptionId) is { } sub)
                await subscriptions.CancelAsync(sub, true);
        }

        db.BeginImmediate();

        int changed = db.Exec("UPDATE staff_disputes SET status=@status,staff_id=@admin,staff_note=@note,refunded_cents=@refunded,resolved_at=@now WHERE id=@id AND status='open'",
            new { status = action == "accept" ? "accepted" : "rejected", admin = admin.Id, note = note == "" ? null : note, refunded, now = Text.NowIso(), id = dispute.Id });

        if (changed == 0)
            throw ApiException.Conflict("Another staff member already resolved this dispute.");

        if (payment != null)
            payments.SetReversed(payment, "refunded");

        ManagementEndpoints.Notify(db, dispute.UserId, "dispute", action == "accept" ? "Your dispute was accepted and a refund is on its way." : "Your dispute was reviewed. Open your profile to see the reply.", dispute.Id);

        if (action == "accept" && dispute.SellerId != null)
            ManagementEndpoints.Notify(db, dispute.SellerId, "sale_refund", "Staff refunded one of your sales after a buyer dispute.", dispute.Id);

        db.Exec("INSERT INTO admin_audit(actor_id,action,target,created_at) VALUES(@actor,@action,@target,datetime('now'))",
            new { actor = admin.Id, action = "dispute." + action, target = dispute.Id });
        db.Commit();

        return Api.Json(new { id = dispute.Id, status = action == "accept" ? "accepted" : "rejected", refunded_cents = refunded });
    }
}
