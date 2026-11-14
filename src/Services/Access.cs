using OpenForge.Infrastructure;
using OpenForge.Models;

namespace OpenForge.Services;
public sealed class Access
{
    private readonly Db _db;
    private readonly ForgeContext _context;

    public Access(Db db, ForgeContext context)
    {
        _db = db;
        _context = context;
    }

    public UserRow? ResolveUser(string value)
    {
        return _db.One<UserRow>("SELECT * FROM users WHERE id=@value OR (custom_slug=@value AND is_verified=1)", new { value });
    }

    public ModRow? ResolveMod(string value)
    {
        return _db.One<ModRow>("SELECT m.* FROM mods m JOIN users u ON u.id=m.author_id WHERE m.guid=@value OR (m.custom_slug=@value AND u.is_verified=1)", new { value });
    }

    public ModListRow? ModWithAuthor(string guid)
    {
        return _db.One<ModListRow>(ModListRow.BaseQuery + " WHERE m.guid=@guid", new { guid });
    }

    public bool CanViewMod(ModRow? row)
    {
        if (row == null || row.Status == "deleted")
            return false;

        CurrentUser? current = _context.User;

        if (current != null && (current.IsAdmin || CanPublish(current.Id, row.AuthorId)))
            return true;

        if (row.Status != "approved")
            return false;

        if (row.Visibility == "public")
            return true;

        return row.Visibility == "code" && current != null && _db.Exists(
            "SELECT 1 FROM release_channels c, json_each(c.members) member WHERE c.guid=@guid AND c.enabled=1 AND member.value=@uid",
            new { guid = row.Guid, uid = current.Id });
    }

    public bool CanManage(CurrentUser? user, ModRow row)
    {
        return user != null && (user.IsAdmin || CanPublish(user.Id, row.AuthorId));
    }

    public bool CanPublish(string uid, string author)
    {
        return uid == author || _db.Exists(
            "SELECT 1 FROM group_members g JOIN users u ON u.id=g.group_id WHERE g.group_id=@author AND g.user_id=@uid AND u.is_group=1 AND u.is_banned=0",
            new { author, uid });
    }

    public bool CanTransfer(string uid, string author)
    {
        return uid == author || _db.Exists("SELECT 1 FROM group_members WHERE group_id=@author AND user_id=@uid AND role='owner'", new { author, uid });
    }
}
