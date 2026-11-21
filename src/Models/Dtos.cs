using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenForge.Models;

public sealed record LinkDto(string Label, string Url);

// A staff-named award. On a mod it's that mod's own medal; on a person it also names the mod that won it.
public sealed class MedalDto
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModName { get; init; }
}

// Flags stay 0/1 and links stays a JSON string: the v1 API returned them that way and clients rely on it.
public sealed class SessionUserDto
{
    public string Id { get; init; } = "";
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Links { get; init; } = "[]";
    public bool IsAdmin { get; init; }
    public bool IsDeveloper { get; init; }
    public int IsVerified { get; init; }
    public int IsBanned { get; init; }
    public string? PublicUsername { get; init; }
    public string? Avatar { get; init; }
    public string? CustomSlug { get; init; }
    public string AccentColor { get; init; } = "";
    public int AccentCustomized { get; init; }
    public int IsGroup { get; init; }
    public bool IsRoot { get; init; }
    public string DiscordUsername { get; init; } = "";
    public string ProfileUrl { get; init; } = "";
    public string AuthProvider { get; init; } = "discord";
    public bool HasEmail { get; init; }
}

public sealed record SessionDto(SessionUserDto? User, string CsrfToken, bool OauthConfigured, bool EmailSignup, bool MailConfigured);

public sealed class GroupMemberDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Avatar { get; init; }
    public string Role { get; init; } = "";
}

public sealed class ProfileDto
{
    public string Id { get; init; } = "";
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public List<LinkDto> Links { get; init; } = [];
    public bool IsAdmin { get; init; }
    public bool IsDeveloper { get; init; }
    public bool IsVerified { get; init; }
    public List<MedalDto> Medals { get; init; } = [];
    public string? PublicUsername { get; init; }
    public string? Avatar { get; init; }
    public string? CustomSlug { get; init; }
    public bool IsGroup { get; init; }
    public string DiscordUsername { get; init; } = "";
    public string ProfileUrl { get; init; } = "";
    public long ModCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GroupMemberDto>? Members { get; init; }
}

public sealed class AuthorDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string DiscordUsername { get; init; } = "";
    public bool IsAdmin { get; init; }
    public bool IsDeveloper { get; init; }
    public bool IsVerified { get; init; }
    public List<MedalDto> Medals { get; init; } = [];
    public string? Avatar { get; init; }
    public string ProfileUrl { get; init; } = "";
}

public sealed class DownloadDto
{
    public string Id { get; init; } = "";
    public string Filename { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Loader { get; init; } = "";
    public string Url { get; init; } = "";
}

public sealed class ReleaseDto
{
    public long Id { get; init; }
    public string Guid { get; init; } = "";
    public string Version { get; init; } = "";
    public string ReleasedAt { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Channel { get; init; } = "";
    public List<DownloadDto> Downloads { get; init; } = [];
    public List<string> Loaders { get; init; } = [];
    public Dictionary<string, List<DownloadDto>> DownloadsByLoader { get; init; } = [];
}

public sealed class ChannelDto
{
    public string Guid { get; init; } = "";
    public string Name { get; init; } = "";
    public string Notes { get; init; } = "";
    public bool Public { get; init; }
    public bool Enabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Members { get; set; }
    public string? CurrentVersion { get; init; }
}

public sealed class ModDto
{
    public string Guid { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    public string? SourceUrl { get; init; }
    public string? Logo { get; init; }
    public List<string> Screenshots { get; init; } = [];
    public JsonObject Capabilities { get; init; } = [];
    public string? CurrentVersion { get; set; }
    public string CreatedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
    public long Downloads { get; init; }
    public int Demo { get; init; }
    public string Status { get; init; } = "";
    public string Visibility { get; init; } = "";
    public string? Banner { get; init; }
    public bool ModdersChoice { get; init; }
    public List<MedalDto> Medals { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChoiceNote { get; init; }
    public string? CustomSlug { get; init; }
    public JsonNode Links { get; init; } = new JsonArray();
    public AuthorDto Author { get; init; } = new();
    public string Id { get; init; } = "";
    public string Url { get; set; } = "";
    public List<string> Loaders { get; init; } = [];
    public long PriceCents { get; init; }
    public string Currency { get; init; } = "usd";
    public bool UsesInjector { get; init; }
    public bool Owned { get; init; }
    public bool DonationsEnabled { get; init; }
    public bool Gated { get; init; }
    public long? MinTierCents { get; init; }
    public bool ExtensionsEnabled { get; init; }
    public bool DocsEnabled { get; init; }

    // Set when this mod is an extension of another mod.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExtendsDto? Extends { get; init; }

    // The newest stable release, included in extension listings so marketplaces can install without a second request.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReleaseDto? LatestRelease { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExtensionCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DocCount { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TierDto>? Tiers { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChannelDto? Channel { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChannelDto>? Channels { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ReleaseDto>? Releases { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DescriptionHtml { get; set; }
}

public sealed record PageDto<T>(List<T> Items, long Total, int Page, int Limit);

public sealed class ExtendsDto
{
    public string Guid { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";

    // The parent developers' decision: pending, approved or unapproved.
    public string State { get; init; } = "pending";
    public bool Featured { get; init; }
}

public sealed class ModDocSummaryDto
{
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public int Position { get; init; }
    public string UpdatedAt { get; init; } = "";
}

public sealed class ModDocDto
{
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Html { get; init; } = "";
    public int Position { get; init; }
    public string CreatedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
}

public sealed class SocialSummaryDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Username { get; init; } = "";
    public string? Avatar { get; init; }
    public string ProfileUrl { get; init; } = "";
}

public sealed class FriendDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Username { get; init; } = "";
    public string? Avatar { get; init; }
    public string ProfileUrl { get; init; } = "";
    public string Status { get; init; } = "offline";
    public string? RoomCode { get; init; }
    public bool Joinable { get; init; }
}

public sealed class TierDto
{
    public string Id { get; init; } = "";
    public string OwnerId { get; init; } = "";
    public string OwnerName { get; init; } = "";
    public string? Guid { get; init; }
    public string Scope { get; init; } = "mod";
    public string Name { get; init; } = "";
    public List<string> Benefits { get; init; } = [];
    public long PriceCents { get; init; }
    public string Currency { get; init; } = "usd";
    public bool Subscribed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TierModDto>? Mods { get; init; }
}

public sealed record TierModDto(string Guid, string Name);
