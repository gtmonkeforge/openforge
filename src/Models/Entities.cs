namespace OpenForge.Models;

public class UserRow
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Links { get; set; } = "[]";
    public int IsAdmin { get; set; }
    public int IsVerified { get; set; }
    public int IsBanned { get; set; }
    public string? PublicUsername { get; set; }
    public string? Avatar { get; set; }
    public string? CustomSlug { get; set; }
    public string AccentColor { get; set; } = "#dc5c3b";
    public int AccentCustomized { get; set; }
    public int IsGroup { get; set; }

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
    public string? ChoiceCategory { get; set; }
    public string? CustomSlug { get; set; }
    public string Links { get; set; } = "[]";
}

public sealed class ModListRow : ModRow
{
    public const string BaseQuery =
        "SELECT m.*,u.username,COALESCE(u.public_username,u.display_name) display_name,u.is_admin,u.is_verified,u.avatar author_avatar,u.custom_slug author_slug FROM mods m JOIN users u ON u.id=m.author_id";

    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int IsAdmin { get; set; }
    public int IsVerified { get; set; }
    public string? AuthorAvatar { get; set; }
    public string? AuthorSlug { get; set; }
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
