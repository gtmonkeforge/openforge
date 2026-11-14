using System.Collections.Generic;
using System.Linq;
using OpenForge.Infrastructure;
using OpenForge.Models;

namespace OpenForge.Services;
public sealed class Channels
{
    private readonly Db _db;
    private readonly ForgeContext _context;
    private readonly Access _access;

    public Channels(Db db, ForgeContext context, Access access)
    {
        _db = db;
        _context = context;
        _access = access;
    }

    public ChannelDto? Get(string guid, string name)
    {
        ChannelRow? row = _db.One<ChannelRow>("SELECT * FROM release_channels WHERE guid=@guid AND name=@name", new { guid, name });

        if (row == null)
            return null;

        string? latest = _db.One<string>("SELECT version FROM releases WHERE guid=@guid AND channel=@name ORDER BY id DESC LIMIT 1", new { guid, name });

        return new ChannelDto
        {
            Guid = row.Guid,
            Name = row.Name,
            Notes = row.Notes,
            Public = row.Public != 0,
            Enabled = row.Enabled != 0,
            Members = JsonBody.StringList(row.Members),
            CurrentVersion = latest,
        };
    }

    public bool Allowed(ModRow row, ChannelDto? channel)
    {
        if (channel == null)
            return false;

        CurrentUser? current = _context.User;

        if (_access.CanManage(current, row))
            return true;

        if (row.Visibility == "private")
            return false;

        bool member = current != null && (channel.Members ?? []).Contains(current.Id);
        return channel.Enabled && ((row.Visibility == "public" && channel.Public) || member);
    }

    public List<ChannelDto> List(ModRow row)
    {
        CurrentUser? current = _context.User;
        bool manage = _access.CanManage(current, row);
        var result = new List<ChannelDto>();

        foreach (string name in _db.Query<string>("SELECT name FROM release_channels WHERE guid=@guid ORDER BY name!='stable',name", new { guid = row.Guid }).ToList())
        {
            ChannelDto? channel = Get(row.Guid, name);

            if (!Allowed(row, channel))
                continue;

            if (!manage)
                channel!.Members = null;

            result.Add(channel!);
        }

        return result;
    }

    public ChannelDto Select(ModRow row, string name)
    {
        ChannelDto? channel = Get(row.Guid, name);

        if (!Allowed(row, channel))
            throw ApiException.NotFound("Release channel not found.");

        return channel!;
    }
}
