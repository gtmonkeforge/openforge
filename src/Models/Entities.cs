using System.Linq;
using OpenForge.Infrastructure;

namespace OpenForge.Models;

public class UserRow
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Links { get; set; } = "[]";
    public int IsAdmin { get; set; }
    public int IsDeveloper { get; set; }
    public int IsVerified { get; set; }
    public int IsBanned { get; set; }
    public string? PublicUsername { get; set; }
    public string? Avatar { get; set; }
    public string? CustomSlug { get; set; }
    public string AccentColor { get; set; } = "#dc5c3b";
    public int AccentCustomized { get; set; }
    public int IsGroup { get; set; }
    public string? StripeAccountId { get; set; }
    public int StripeChargesEnabled { get; set; }
    public int StripeTransfersEnabled { get; set; }
    public int StripeDetailsSubmitted { get; set; }
    public string? StripeProfileIssues { get; set; }
    public string? StripeProfileFlaggedAt { get; set; }
    public int StripeProfileGrace { get; set; }
    public string? Email { get; set; }
    public int EmailVerified { get; set; }
    public string? EmailSource { get; set; }
    public string? PasswordHash { get; set; }
    public long SessionEpoch { get; set; }
    public int EmailReleaseNotices { get; set; } = 1;
    public int EmailAnnouncements { get; set; } = 1;
    public string? CreatedAt { get; set; }
    public string? DeletedAt { get; set; }

    public bool IsEmailAccount => Id.StartsWith(Text.EmailAccountPrefix, System.StringComparison.Ordinal);
    public string Provider => IsEmailAccount ? "email" : "discord";

    // Email accounts have no Discord name; Discord accounts' username column is their Discord username.
    public string DiscordName => IsEmailAccount || Group ? "" : Username;
    public bool Verified => IsVerified != 0;
    public bool Banned => IsBanned != 0;
    public bool Group => IsGroup != 0;
    public string PublicName => PublicUsername ?? DisplayName;
}

public class ModRow
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string AuthorId { get; set; } = "";
    public string Category { get; set; } = "Other";
    public string? SourceUrl { get; set; }
    public string? Logo { get; set; }
    public string Screenshots { get; set; } = "[]";
    public string Capabilities { get; set; } = "{}";
    public string CurrentVersion { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public long Downloads { get; set; }
    public int Demo { get; set; }
    public string Status { get; set; } = "pending";
    public string Visibility { get; set; } = "public";
    public string? Banner { get; set; }
    public int IsChoice { get; set; }
    public string? ChoiceNote { get; set; }
    public string? CustomSlug { get; set; }
    public string Links { get; set; } = "[]";
    public long PriceCents { get; set; }
    public int UsesInjector { get; set; }
    public int ExtensionsEnabled { get; set; }
    public int DocsEnabled { get; set; }

    public bool Paid => PriceCents > 0;
    public bool Injected => UsesInjector != 0;
    public bool AcceptsExtensions => ExtensionsEnabled != 0;
    public bool HasDocs => DocsEnabled != 0;
}

public sealed class ExtensionRow
{
    public string Guid { get; set; } = "";
    public string ParentGuid { get; set; } = "";
    public string State { get; set; } = "pending";
    public int Featured { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }

    public static readonly string[] States = ["pending", "approved", "unapproved"];
}

public sealed class ModDocRow
{
    public long Id { get; set; }
    public string Guid { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int Position { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? UpdatedBy { get; set; }
}

public sealed class ModListRow : ModRow
{
    public const string BaseQuery =
        "SELECT m.*,u.username,COALESCE(u.public_username,u.display_name) display_name,u.is_admin,u.is_developer,u.is_verified,u.avatar author_avatar,u.custom_slug author_slug,u.stripe_charges_enabled author_charges,u.stripe_transfers_enabled author_transfers FROM mods m JOIN users u ON u.id=m.author_id";

    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int IsAdmin { get; set; }
    public int IsDeveloper { get; set; }
    public int IsVerified { get; set; }
    public string? AuthorAvatar { get; set; }
    public string? AuthorSlug { get; set; }
    public int AuthorCharges { get; set; }
    public int AuthorTransfers { get; set; }
}

public sealed class PaymentRow
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? BuyerId { get; set; }
    public string? RecipientId { get; set; }
    public string? Guid { get; set; }
    public long AmountCents { get; set; }
    public long FeeCents { get; set; }
    public string Currency { get; set; } = "";
    public string? StripeAccount { get; set; }
    public string? PaymentIntent { get; set; }
    public string Status { get; set; } = "open";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? SubscriptionId { get; set; }
}

public sealed class TierRow
{
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string? Guid { get; set; }
    public string Name { get; set; } = "";
    public string Benefits { get; set; } = "[]";
    public long PriceCents { get; set; }
    public int Position { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? ArchivedAt { get; set; }
}

public sealed class SubscriptionRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string TierId { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string StripeAccount { get; set; } = "";
    public string? CheckoutSession { get; set; }
    public string? StripeSubscription { get; set; }
    public long PriceCents { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int CancelAtPeriodEnd { get; set; }
    public string? CurrentPeriodEnd { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public int CollectionPaused { get; set; }

    // past_due keeps access while Stripe retries the card; Stripe ends the subscription if retries run out.
    public static readonly string[] GrantingStatuses = ["active", "trialing", "past_due"];
    public bool Granting => GrantingStatuses.Contains(Status);
}

public sealed class DisputeRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string? PaymentId { get; set; }
    public string? SubscriptionId { get; set; }
    public string? SellerId { get; set; }
    public string? Guid { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "open";
    public string? StaffId { get; set; }
    public string? StaffNote { get; set; }
    public long RefundedCents { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? ResolvedAt { get; set; }
}

public sealed class InjectorDeviceRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string? LastUsedAt { get; set; }
    public string? RevokedAt { get; set; }
}

public sealed class ReleaseRow
{
    public long Id { get; set; }
    public string Guid { get; set; } = "";
    public string Version { get; set; } = "";
    public string ReleasedAt { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Channel { get; set; } = "stable";
}

public sealed class FileRow
{
    public string Id { get; set; } = "";
    public long ReleaseId { get; set; }
    public string StoredName { get; set; } = "";
    public string Filename { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Loader { get; set; } = "bepinex5";
}

public sealed class ChannelRow
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public int Public { get; set; }
    public int Enabled { get; set; }
    public string Members { get; set; } = "[]";
}

public sealed class ApiKeyRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string? LastUsedAt { get; set; }
    public string? RevokedAt { get; set; }
}

public sealed class TransferRow
{
    public string Id { get; set; } = "";
    public string Guid { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public string Status { get; set; } = "pending";
    public double Created { get; set; }
}

public sealed class ConversationRow
{
    public string Id { get; set; } = "";
    public string? GroupId { get; set; }
    public string? Sender { get; set; }
    public string? Recipient { get; set; }
    public string Status { get; set; } = "pending";
}

public sealed class MessageRow
{
    public long Id { get; set; }
    public string ConversationId { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Body { get; set; } = "";
    public double Created { get; set; }
}

public sealed class NotificationRow
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Body { get; set; } = "";
    public string Target { get; set; } = "";
    public double Created { get; set; }
    public int Seen { get; set; }
}

public sealed class PresenceRow
{
    public string UserId { get; set; } = "";
    public string? RoomCode { get; set; }
    public string Visibility { get; set; } = "none";
    public double Updated { get; set; }
}
