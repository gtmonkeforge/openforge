using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenForge.Infrastructure;
using OpenForge.Models;

namespace OpenForge.Endpoints;

public static class SocialEndpoints
{
    private const double PresenceSeconds = 90;

    private sealed class FriendshipRow
    {
        public string Sender { get; set; } = "";
        public string Recipient { get; set; } = "";
        public int Accepted { get; set; }
    }

    private sealed class GroupRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public string ConversationId { get; set; } = "";
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/friends", Friends);
        app.MapPost("/api/v1/friends", FriendRequestAsync);
        app.MapPost("/api/v1/friends/requests", FriendRequestAsync);
        app.MapPut("/api/v1/friends/{other}", AcceptFriend);
        app.MapDelete("/api/v1/friends/{other}", RemoveFriend);
        app.MapPost("/api/v1/friends/{other}/join", Join);
        app.MapPut("/api/v1/presence", SetPresenceAsync);
        app.MapDelete("/api/v1/presence", ClearPresence);
        app.MapGet("/api/v1/groups", Groups);
        app.MapPost("/api/v1/groups", CreateGroupAsync);
        app.MapPost("/api/v1/groups/{groupId}/members", AddMemberAsync);
        app.MapDelete("/api/v1/groups/{groupId}/members/{other}", RemoveMember);
        app.MapGet("/api/v1/chat", Conversations);
        app.MapPost("/api/v1/chat", StartChatAsync);
        app.MapPost("/api/v1/chat/{conversationId}/accept", (string conversationId, ForgeContext context, Db db) => AnswerChat(conversationId, "accepted", context, db));
        app.MapPost("/api/v1/chat/{conversationId}/decline", (string conversationId, ForgeContext context, Db db) => AnswerChat(conversationId, "declined", context, db));
        app.MapGet("/api/v1/chat/{conversationId}/messages", Messages);
        app.MapPost("/api/v1/chat/{conversationId}/messages", SendMessageAsync);
        app.MapGet("/api/v1/notifications", Notifications);
        app.MapPost("/api/v1/notifications/read", ReadNotificationsAsync);
    }

    public static List<UserRow> MatchUsers(Db db, string value)
    {
        return db.Query<UserRow>(
            "SELECT * FROM users WHERE id=@value OR (custom_slug=@value AND is_verified=1) OR public_username=@value COLLATE NOCASE OR (public_username IS NULL AND username=@value COLLATE NOCASE)",
            new { value }).ToList();
    }

    private static string Current(ForgeContext context)
    {
        CurrentUser user = context.RequireUser("Sign in to use this feature.");

        if (user.IsGroup)
            throw ApiException.Forbidden("Groups cannot sign in or use personal account features.");

        return user.Id;
    }

    private static Task<JsonObject> PayloadAsync(HttpRequest request)
    {
        return JsonBody.RequireObjectAsync(request, "Supply a JSON object.");
    }

    private static string Person(Db db, JsonNode? node)
    {
        if (!node.IsString(out string value))
            throw ApiException.BadRequest("Supply a username or ID.");

        value = value.Trim().TrimStart('@');
        List<UserRow> rows = MatchUsers(db, value);
        UserRow? exact = rows.FirstOrDefault(r => r.Id == value);

        if (exact == null && rows.Count != 1)
            throw ApiException.NotFound("User not found or username is ambiguous. Use their ID.");

        UserRow row = exact ?? rows[0];

        if (row.Group || row.Banned)
            throw ApiException.BadRequest("Choose an active personal account.");

        return row.Id;
    }

    private static SocialSummaryDto Summary(Db db, string uid)
    {
        UserRow row = db.One<UserRow>("SELECT * FROM users WHERE id=@uid", new { uid })!;

        return new SocialSummaryDto
        {
            Id = uid,
            Name = row.Group ? row.DisplayName : row.PublicName,
            Username = row.PublicUsername ?? row.Username,
            Avatar = row.Avatar,
            ProfileUrl = "/profile/" + uid,
        };
    }

    private static bool Connected(Db db, string a, string b)
    {
        return db.Exists("SELECT 1 FROM friendships WHERE accepted=1 AND ((sender=@a AND recipient=@b) OR (sender=@b AND recipient=@a))", new { a, b });
    }

    private static bool Member(Db db, string? group, string uid, bool owner = false)
    {
        return db.Exists("SELECT 1 FROM group_members WHERE group_id=@group AND user_id=@uid" + (owner ? " AND role='owner'" : ""), new { group, uid });
    }

    private static IResult Friends(ForgeContext context, Db db)
    {
        string uid = Current(context);
        var friends = new List<FriendDto>();
        var incoming = new List<FriendDto>();
        var sent = new List<FriendDto>();

        foreach (FriendshipRow r in db.Query<FriendshipRow>("SELECT * FROM friendships WHERE sender=@uid OR recipient=@uid", new { uid }).ToList())
        {
            string other = r.Sender == uid ? r.Recipient : r.Sender;
            SocialSummaryDto summary = Summary(db, other);
            PresenceRow? presence = db.One<PresenceRow>("SELECT * FROM presence WHERE user_id=@other", new { other });
            bool fresh = presence != null && presence.Updated > Text.NowEpoch() - PresenceSeconds;
            string? room = fresh && presence!.Visibility == "friends" && r.Accepted != 0 ? presence.RoomCode : null;

            var item = new FriendDto
            {
                Id = summary.Id,
                Name = summary.Name,
                Username = summary.Username,
                Avatar = summary.Avatar,
                ProfileUrl = summary.ProfileUrl,
                Status = fresh && presence!.RoomCode != null ? "in_game" : fresh ? "online" : "offline",
                RoomCode = room,
                Joinable = !string.IsNullOrEmpty(room),
            };

            if (r.Accepted != 0)
                friends.Add(item);
            else if (r.Sender == uid)
                sent.Add(item);
            else
                incoming.Add(item);
        }

        string visibility = db.One<string>("SELECT visibility FROM presence WHERE user_id=@uid", new { uid }) ?? "none";
        return Api.Json(new { friends, requests = incoming, sent, visibility });
    }

    private static async Task<IResult> FriendRequestAsync(HttpRequest request, ForgeContext context, Db db)
    {
        string uid = Current(context);
        string other = Person(db, (await PayloadAsync(request))["user_id"]);

        if (uid == other)
            throw ApiException.BadRequest("You cannot add yourself.");

        db.BeginImmediate();

        if (db.Exists("SELECT 1 FROM friendships WHERE (sender=@uid AND recipient=@other) OR (sender=@other AND recipient=@uid)", new { uid, other }))
            throw ApiException.Conflict("A friendship or request already exists.");

        db.Exec("INSERT INTO friendships(sender,recipient) VALUES(@uid,@other)", new { uid, other });
        ManagementEndpoints.Notify(db, other, "friend_request", Summary(db, uid).Name + " sent a friend request.", uid);
        db.Commit();

        return Api.Json(new { ok = true }, 201);
    }

    private static IResult AcceptFriend(string other, ForgeContext context, Db db)
    {
        string uid = Current(context);

        if (db.Exec("UPDATE friendships SET accepted=1 WHERE sender=@other AND recipient=@uid AND accepted=0", new { other, uid }) == 0)
            throw ApiException.NotFound("Incoming request not found.");

        ManagementEndpoints.Notify(db, other, "friend_accepted", Summary(db, uid).Name + " accepted your friend request.", uid);
        return Api.Ok();
    }

    private static IResult RemoveFriend(string other, ForgeContext context, Db db)
    {
        string uid = Current(context);
        db.Exec("DELETE FROM friendships WHERE (sender=@uid AND recipient=@other) OR (sender=@other AND recipient=@uid)", new { uid, other });
        return Api.Ok();
    }

    private static IResult Join(string other, ForgeContext context, Db db)
    {
        string uid = Current(context);

        if (!Connected(db, uid, other))
            throw ApiException.Forbidden("Only accepted friends can see this room.");

        string? room = db.One<string>("SELECT room_code FROM presence WHERE user_id=@other AND visibility='friends' AND updated>@since", new { other, since = Text.NowEpoch() - PresenceSeconds });

        if (string.IsNullOrEmpty(room))
            throw ApiException.NotFound("This friend is not sharing a room.");

        return Api.Json(new { room_code = room });
    }

    private static async Task<IResult> SetPresenceAsync(HttpRequest request, ForgeContext context, Db db)
    {
        string uid = Current(context);
        JsonObject payload = await PayloadAsync(request);
        PresenceRow? old = db.One<PresenceRow>("SELECT * FROM presence WHERE user_id=@uid", new { uid });

        string? visibility;

        if (payload.ContainsKey("visibility"))
            visibility = payload["visibility"].IsString(out string chosen) ? chosen : null;
        else if (payload.ContainsKey("joinable"))
            visibility = payload["joinable"]?.GetValueKind() is System.Text.Json.JsonValueKind.True ? "friends" : "none";
        else
            visibility = old?.Visibility ?? "none";

        string? code = old?.RoomCode;
        bool codeValid = true;

        if (payload.ContainsKey("room_code"))
        {
            JsonNode? node = payload["room_code"];
            code = node == null ? null : node.IsString(out string text) ? text : null;
            codeValid = node == null || node.IsString(out _);
        }

        double updated = payload.ContainsKey("room_code") ? Text.NowEpoch() : old?.Updated ?? 0;

        if (visibility is not ("friends" or "none"))
            throw ApiException.BadRequest("Choose Friends or None visibility.");

        if (!codeValid || (code != null && (code == "" || code.Length > 12 || !code.All(char.IsAsciiLetterOrDigit))))
            throw ApiException.BadRequest("Use a room code of 1–12 letters or numbers.");

        db.Exec("INSERT INTO presence VALUES(@uid,@code,@visibility,@updated) ON CONFLICT(user_id) DO UPDATE SET room_code=excluded.room_code,visibility=excluded.visibility,updated=excluded.updated",
            new { uid, code = string.IsNullOrEmpty(code) ? null : code.ToUpperInvariant(), visibility, updated });

        return Api.Ok();
    }

    private static IResult ClearPresence(ForgeContext context, Db db)
    {
        string uid = Current(context);
        db.Exec("DELETE FROM presence WHERE user_id=@uid", new { uid });
        return Api.Ok();
    }

    private static IResult Groups(ForgeContext context, Db db)
    {
        string uid = Current(context);
        List<GroupRow> items = db.Query<GroupRow>(
            "SELECT u.id,u.display_name name,g.role,c.id conversation_id FROM group_members g JOIN users u ON u.id=g.group_id JOIN conversations c ON c.group_id=u.id WHERE g.user_id=@uid",
            new { uid }).ToList();

        return Api.Json(new { items });
    }

    private static async Task<IResult> CreateGroupAsync(HttpRequest request, ForgeContext context, Db db)
    {
        string uid = Current(context);
        JsonObject payload = await PayloadAsync(request);
        string name = "";

        if (payload.ContainsKey("name") && !payload["name"].IsString(out name) || Text.Len(name.Trim()) is < 3 or > 60)
            throw ApiException.BadRequest("Use a group name of 3–60 characters.");

        string groupId = "group-" + Text.Hex(12);
        string conversationId = Text.Hex(16);

        db.BeginImmediate();
        db.Exec("INSERT INTO users(id,username,display_name,is_group) VALUES(@groupId,@groupId,@name,1)", new { groupId, name = name.Trim() });
        db.Exec("INSERT INTO group_members VALUES(@groupId,@uid,'owner')", new { groupId, uid });
        db.Exec("INSERT INTO conversations(id,group_id,status) VALUES(@conversationId,@groupId,'accepted')", new { conversationId, groupId });
        db.Commit();

        return Api.Json(new { id = groupId, conversation_id = conversationId }, 201);
    }

    private static async Task<IResult> AddMemberAsync(string groupId, HttpRequest request, ForgeContext context, Db db)
    {
        string uid = Current(context);

        if (!Member(db, groupId, uid, true))
            throw ApiException.Forbidden("Only the group owner can manage members.");

        JsonObject payload = await PayloadAsync(request);
        db.BeginImmediate();
        string other = Person(db, payload["user_id"]);

        if (Member(db, groupId, other))
            throw ApiException.Conflict("Already a group member.");

        if (db.Scalar<long>("SELECT COUNT(*) FROM group_members WHERE group_id=@groupId", new { groupId }) >= 20)
            throw ApiException.Conflict("Groups support at most 20 members.");

        db.Exec("INSERT INTO group_members(group_id,user_id) VALUES(@groupId,@other)", new { groupId, other });
        ManagementEndpoints.Notify(db, other, "group_member", "You were added to " + Summary(db, groupId).Name + ".", groupId);
        db.Commit();

        return Api.Ok();
    }

    private static IResult RemoveMember(string groupId, string other, ForgeContext context, Db db)
    {
        string uid = Current(context);

        if (!Member(db, groupId, uid, true))
            throw ApiException.Forbidden("Only the group owner can manage members.");

        if (other == uid)
            throw ApiException.BadRequest("The owner must remain a member.");

        db.Exec("DELETE FROM group_members WHERE group_id=@groupId AND user_id=@other", new { groupId, other });
        return Api.Ok();
    }

    private static ConversationRow Conversation(Db db, string conversationId, string uid)
    {
        ConversationRow? c = db.One<ConversationRow>("SELECT * FROM conversations WHERE id=@conversationId", new { conversationId });
        bool allowed = c != null && (c.GroupId != null ? Member(db, c.GroupId, uid) : uid == c.Sender || uid == c.Recipient);

        if (!allowed)
            throw ApiException.NotFound("Conversation not found.");

        return c!;
    }

    private static IResult Conversations(ForgeContext context, Db db)
    {
        string uid = Current(context);
        var items = new List<object>();

        foreach (ConversationRow r in db.Query<ConversationRow>("SELECT DISTINCT c.* FROM conversations c LEFT JOIN group_members m ON m.group_id=c.group_id WHERE c.sender=@uid OR c.recipient=@uid OR m.user_id=@uid", new { uid }).ToList())
        {
            string status = r.Status;

            if (r.GroupId == null && status != "accepted" && Connected(db, r.Sender!, r.Recipient!))
                status = "accepted";

            string nameOf = r.GroupId ?? (r.Recipient == uid ? r.Sender! : r.Recipient!);
            items.Add(new { id = r.Id, group_id = r.GroupId, sender = r.Sender, recipient = r.Recipient, status, name = Summary(db, nameOf).Name, incoming = r.Recipient == uid });
        }

        return Api.Json(new { items });
    }

    private static async Task<IResult> StartChatAsync(HttpRequest request, ForgeContext context, Db db)
    {
        string uid = Current(context);
        string other = Person(db, (await PayloadAsync(request))["user_id"]);

        if (other == uid)
            throw ApiException.BadRequest("Choose another user.");

        db.BeginImmediate();
        ConversationRow? existing = db.One<ConversationRow>("SELECT * FROM conversations WHERE (sender=@uid AND recipient=@other) OR (sender=@other AND recipient=@uid)", new { uid, other });
        string conversationId;

        if (existing == null)
        {
            conversationId = Text.Hex(16);
            string status = Connected(db, uid, other) ? "accepted" : "pending";
            db.Exec("INSERT INTO conversations(id,sender,recipient,status) VALUES(@conversationId,@uid,@other,@status)", new { conversationId, uid, other, status });

            if (status == "pending")
                ManagementEndpoints.Notify(db, other, "message_request", Summary(db, uid).Name + " wants to chat.", conversationId);
        }
        else
        {
            conversationId = existing.Id;

            if (Connected(db, uid, other) && existing.Status != "accepted")
                db.Exec("UPDATE conversations SET status='accepted' WHERE id=@conversationId", new { conversationId });
        }

        db.Commit();
        return Api.Json(new { id = conversationId });
    }

    private static IResult AnswerChat(string conversationId, string answer, ForgeContext context, Db db)
    {
        string uid = Current(context);
        ConversationRow c = Conversation(db, conversationId, uid);

        if (c.GroupId != null || c.Recipient != uid || c.Status != "pending")
            throw ApiException.Forbidden("Only the recipient can answer a message request.");

        if (db.Exec("UPDATE conversations SET status=@answer WHERE id=@conversationId AND status='pending'", new { answer, conversationId }) == 0)
            throw ApiException.Conflict("This request has already been answered.");

        return Api.Ok();
    }

    private static IResult Messages(string conversationId, HttpRequest request, ForgeContext context, Db db)
    {
        string uid = Current(context);
        Conversation(db, conversationId, uid);

        if (!long.TryParse(request.Query["before"].FirstOrDefault() ?? long.MaxValue.ToString(), out long before) || before < 1)
            throw ApiException.BadRequest("Invalid message cursor.");

        List<MessageRow> rows = db.Query<MessageRow>("SELECT * FROM messages WHERE conversation_id=@conversationId AND id<@before ORDER BY id DESC LIMIT 100", new { conversationId, before }).ToList();
        var authors = new Dictionary<string, SocialSummaryDto>();

        var items = Enumerable.Reverse(rows).Select(m =>
        {
            if (!authors.TryGetValue(m.Sender, out SocialSummaryDto? author))
                authors[m.Sender] = author = Summary(db, m.Sender);

            return new { id = m.Id, conversation_id = m.ConversationId, sender = m.Sender, body = m.Body, created = m.Created, author };
        }).ToList();

        return Api.Json(new { items, next_before = rows.Count == 100 ? rows[^1].Id : (long?)null });
    }

    private static async Task<IResult> SendMessageAsync(string conversationId, HttpRequest request, ForgeContext context, Db db)
    {
        string uid = Current(context);
        Conversation(db, conversationId, uid);
        JsonObject payload = await PayloadAsync(request);

        db.BeginImmediate();
        ConversationRow c = Conversation(db, conversationId, uid);
        string body = "";

        if (payload.ContainsKey("body") && !payload["body"].IsString(out body) || Text.Len(body.Trim()) is < 1 or > 4000)
            throw ApiException.BadRequest("Write a message of 1–4000 characters.");

        bool friends = c.GroupId == null && Connected(db, c.Sender!, c.Recipient!);
        bool hasMessages = db.Exists("SELECT 1 FROM messages WHERE conversation_id=@conversationId", new { conversationId });
        bool blocked = (c.Status == "declined" && !friends) || (c.Status == "pending" && !friends && (uid != c.Sender || hasMessages));

        if (blocked)
            throw ApiException.Forbidden("Wait for this message request to be accepted.");

        body = body.Trim();
        db.Exec("INSERT INTO messages(conversation_id,sender,body,created) VALUES(@conversationId,@uid,@body,@now)", new { conversationId, uid, body, now = Text.NowEpoch() });

        List<string> recipients = c.GroupId != null
            ? db.Query<string>("SELECT user_id FROM group_members WHERE group_id=@group", new { group = c.GroupId }).ToList()
            : [uid == c.Sender ? c.Recipient! : c.Sender!];

        string preview = Summary(db, uid).Name + ": " + new string(body.EnumerateRunes().Take(180).SelectMany(r => r.ToString()).ToArray());

        foreach (string other in recipients.Where(other => other != uid))
            ManagementEndpoints.Notify(db, other, "chat_message", preview, conversationId);

        db.Commit();
        return Api.Json(new { ok = true }, 201);
    }

    private static IResult Notifications(ForgeContext context, Db db)
    {
        string uid = Current(context);
        List<NotificationRow> items = db.Query<NotificationRow>("SELECT * FROM notifications WHERE user_id=@uid AND seen=0 ORDER BY id LIMIT 100", new { uid }).ToList();
        return Api.Json(new { items });
    }

    private static async Task<IResult> ReadNotificationsAsync(HttpRequest request, ForgeContext context, Db db)
    {
        string uid = Current(context);
        JsonObject payload = await PayloadAsync(request);

        if (!payload["through"].IsInt(out long through) || through < 0)
            throw ApiException.BadRequest("Supply a notification ID.");

        db.Exec("UPDATE notifications SET seen=1 WHERE user_id=@uid AND id<=@through", new { uid, through });
        return Api.Ok();
    }
}
