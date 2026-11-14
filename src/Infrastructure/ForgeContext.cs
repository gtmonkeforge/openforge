using OpenForge.Models;

namespace OpenForge.Infrastructure;

public sealed class CurrentUser
{
    public CurrentUser(UserRow row, bool isRoot)
    {
        Row = row;
        IsRoot = isRoot;
    }

    public UserRow Row { get; }
    public bool IsRoot { get; }

    public string Id => Row.Id;
    public bool IsAdmin => Row.IsAdmin != 0 || IsRoot;
    public bool IsVerified => Row.Verified;
    public bool IsBanned => Row.Banned;
    public bool IsGroup => Row.Group;
    public string Username => Row.PublicUsername ?? Row.Username;
    public string DisplayName => Row.PublicUsername ?? Row.DisplayName;
    public string AccentColor => Row.AccentCustomized != 0 ? Row.AccentColor : Text.DefaultAccent;
    public string ProfileUrl => Text.ProfileUrl(Row.Id, Row.CustomSlug, Row.Verified);

    public SessionUserDto ToDto()
    {
        return new SessionUserDto
        {
            Id = Row.Id,
            Username = Username,
            DisplayName = DisplayName,
            Description = Row.Description,
            Links = Row.Links,
            IsAdmin = IsAdmin,
            IsVerified = Row.IsVerified,
            IsBanned = Row.IsBanned,
            PublicUsername = Row.PublicUsername,
            Avatar = Row.Avatar,
            CustomSlug = Row.CustomSlug,
            AccentColor = AccentColor,
            AccentCustomized = Row.AccentCustomized,
            IsGroup = Row.IsGroup,
            IsRoot = IsRoot,
            DiscordUsername = Row.Username,
            ProfileUrl = ProfileUrl,
        };
    }
}

public sealed class ForgeContext
{
    private readonly Db _db;
    private readonly ForgeOptions _options;
    private string? _loadedFor;
    private CurrentUser? _user;

    public ForgeContext(Db db, ForgeOptions options)
    {
        _db = db;
        _options = options;
    }

    public SessionData Session { get; set; } = new SessionData();
    public string? ApiUserId { get; set; }

    public CurrentUser? User
    {
        get
        {
            string? uid = ApiUserId ?? Session.UserId;

            if (uid == null)
                return null;

            if (_loadedFor == uid)
                return _user;

            UserRow? row = _db.One<UserRow>("SELECT * FROM users WHERE id=@uid", new { uid });
            _loadedFor = uid;
            _user = row == null ? null : new CurrentUser(row, _options.RootAdminId != "" && uid == _options.RootAdminId);

            return _user;
        }
    }

    public void Refresh()
    {
        _loadedFor = null;
    }

    public CurrentUser RequireUser(string message)
    {
        return User ?? throw ApiException.Unauthorized(message);
    }

    public string Csrf()
    {
        Session.Csrf ??= Text.UrlToken(32);
        return Session.Csrf;
    }

    public void SignIn(string userId)
    {
        Session = new SessionData { UserId = userId, Permanent = true };
        Csrf();
        Refresh();
    }

    public void Clear()
    {
        Session = new SessionData();
        Refresh();
    }
}
