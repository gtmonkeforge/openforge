using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenForge.Models;

public sealed record LinkDto(string Label, string Url);

// Flags stay 0/1 and links stays a JSON string: the v1 API returned them that way and clients rely on it.
public sealed class SessionUserDto
{
    public string Id { get; init; } = "";
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Links { get; init; } = "[]";
    public bool IsAdmin { get; init; }
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
}

public sealed record SessionDto(SessionUserDto? User, string CsrfToken, bool OauthConfigured);

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
    public bool IsVerified { get; init; }
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
    public bool IsVerified { get; init; }
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
    public string? ChoiceCategory { get; init; }
    public string? CustomSlug { get; init; }
    public JsonNode Links { get; init; } = new JsonArray();
    public AuthorDto Author { get; init; } = new();
    public List<string> Badges { get; init; } = [];
    public string Id { get; init; } = "";
    public string Url { get; set; } = "";
    public List<string> Loaders { get; init; } = [];

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
