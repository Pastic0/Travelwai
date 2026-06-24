using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class NotificationsController : ApiControllerBase
{
    private const string NotificationDismissalsCollection = "notification_dismissals";

    private readonly IScheduleService _scheduleService;
    private readonly IFriendService _friendService;
    private readonly IChatService _chatService;
    private readonly IDataRepository _repo;

    public NotificationsController(
        IAuthService authService,
        IScheduleService scheduleService,
        IFriendService friendService,
        IChatService chatService,
        IDataRepository repo) : base(authService)
    {
        _scheduleService = scheduleService;
        _friendService = friendService;
        _chatService = chatService;
        _repo = repo;
    }

    [HttpGet("notifications")]
    public async Task<IActionResult> GetNotifications()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var userId = current.userId!;
        var dismissedIds = await GetDismissedNotificationIdsAsync(userId);
        var scheduleNotifications = FilterDismissed(await GetScheduleNotificationsAsync(userId), dismissedIds);
        var friendNotifications = FilterDismissed(await GetFriendNotificationsAsync(userId), dismissedIds);
        var messageNotifications = FilterDismissed(await GetMessageNotificationsAsync(userId), dismissedIds);
        var all = scheduleNotifications
            .Concat(friendNotifications)
            .Concat(messageNotifications)
            .OrderByDescending(x => x.GetValueOrDefault("created_at")?.ToString() ?? string.Empty)
            .ToList();

        ApplyDefaultReadState(scheduleNotifications);
        ApplyDefaultReadState(friendNotifications);
        ApplyDefaultReadState(messageNotifications);
        ApplyDefaultReadState(all);

        var unreadCount = all.Count(x => !IsRead(x));

        return Ok(new
        {
            success = true,
            local_only = false,
            data = new
            {
                schedules = scheduleNotifications,
                friends = friendNotifications,
                messages = messageNotifications,
                systems = Array.Empty<Dictionary<string, object?>>(),
                all
            },
            count = all.Count,
            unread_count = unreadCount,
            message = "Đã tải thông báo và áp dụng danh sách đã xoá trong database."
        });
    }

    [HttpPost("notifications/clear")]
    public async Task<IActionResult> ClearNotifications([FromBody] ClearNotificationsRequest? request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var userId = current.userId!;
        var incomingIds = NormalizeIdList(request?.Ids);

        if (incomingIds.Count == 0)
        {
            var schedules = await GetScheduleNotificationsAsync(userId);
            var friends = await GetFriendNotificationsAsync(userId);
            var messages = await GetMessageNotificationsAsync(userId);
            incomingIds = schedules.Concat(friends).Concat(messages)
                .Select(item => Text(item, "id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var oldIds = await GetDismissedNotificationIdsAsync(userId);
        var mergedIds = oldIds
            .Concat(incomingIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .TakeLast(2000)
            .ToList();

        await _repo.SetAsync(NotificationDismissalsCollection, userId, new Dictionary<string, object?>
        {
            ["user_id"] = userId,
            ["ids"] = mergedIds,
            ["updated_at"] = DateTime.UtcNow
        }, merge: false);

        return Ok(new
        {
            success = true,
            deleted_count = incomingIds.Count,
            stored_count = mergedIds.Count,
            message = "Đã xoá thông báo trong database."
        });
    }

    private async Task<List<Dictionary<string, object?>>> GetScheduleNotificationsAsync(string userId)
    {
        var (owned, shared) = await _scheduleService.GetSchedulesForUserAsync(userId);
        var result = new List<Dictionary<string, object?>>();

        foreach (var item in owned.Take(10))
        {
            result.Add(new Dictionary<string, object?>
            {
                ["type"] = "schedule",
                ["title"] = "Tạo lịch trình mới",
                ["content"] = $"Lịch trình \"{Text(item, "title", "name") ?? "Chưa đặt tên"}\" đã được tạo/cập nhật.",
                ["url"] = "/schedule",
                ["created_at"] = Text(item, "updated_at", "created_at", "start_date")
            });
        }

        foreach (var item in shared.Take(10))
        {
            result.Add(new Dictionary<string, object?>
            {
                ["type"] = "schedule",
                ["title"] = "Lịch trình được chia sẻ",
                ["content"] = $"Bạn được chia sẻ lịch trình \"{Text(item, "title", "name") ?? "Chưa đặt tên"}\".",
                ["url"] = "/schedule",
                ["created_at"] = Text(item, "updated_at", "created_at", "start_date")
            });
        }

        EnsureNotificationIds(result);
        return result
            .OrderByDescending(x => x.GetValueOrDefault("created_at")?.ToString() ?? string.Empty)
            .Take(20)
            .ToList();
    }

    private async Task<List<Dictionary<string, object?>>> GetFriendNotificationsAsync(string userId)
    {
        var (_, pending) = await _friendService.GetFriendsAsync(userId);
        var result = pending.Select(item => new Dictionary<string, object?>
        {
            ["type"] = "friend",
            ["title"] = "Lời mời kết bạn",
            ["content"] = $"{Text(item, "name", "username", "email") ?? "Một người dùng"} đã gửi lời mời kết bạn.",
            ["url"] = "/messaging",
            ["created_at"] = Text(item, "created_at"),
            ["request_email"] = Text(item, "email"),
            ["requester_name"] = Text(item, "name", "username", "email")
        }).ToList();
        EnsureNotificationIds(result);
        return result;
    }

    private async Task<List<Dictionary<string, object?>>> GetMessageNotificationsAsync(string userId)
    {
        var conversations = await _chatService.GetConversationsAsync(userId);
        var result = new List<Dictionary<string, object?>>();

        foreach (var c in conversations.Take(30))
        {
            var conversationId = Text(c, "id", "conversation_id");
            var lastMessage = Text(c, "last_message");
            var lastMessageTime = Text(c, "last_message_time", "created_at");
            var lastSenderId = Text(c, "last_sender_id", "sender_id", "last_message_sender_id");

            if (string.IsNullOrWhiteSpace(lastSenderId) && !string.IsNullOrWhiteSpace(conversationId))
            {
                var latestMessage = (await _repo.WhereEqualPagedAsync(
                        "messages",
                        "conversation_id",
                        conversationId,
                        "time_sent",
                        descending: true,
                        limit: 1,
                        offset: 0,
                        includeId: true))
                    .FirstOrDefault();

                if (latestMessage is not null)
                {
                    lastSenderId = Text(latestMessage, "sender_id", "user_id");
                    lastMessage = Text(latestMessage, "content", "message", "text") ?? lastMessage;
                    lastMessageTime = Text(latestMessage, "time_sent", "created_at") ?? lastMessageTime;
                }
            }

            if (string.IsNullOrWhiteSpace(lastMessage)) continue;

            if (!string.IsNullOrWhiteSpace(lastSenderId) && string.Equals(lastSenderId, userId, StringComparison.Ordinal))
            {
                continue;
            }

            var senderInfo = !string.IsNullOrWhiteSpace(lastSenderId)
                ? await _repo.GetByIdAsync("users", lastSenderId)
                : c.GetValueOrDefault("other_user_info") as Dictionary<string, object?>;
            var senderName = senderInfo is null ? "Bạn bè" : Text(senderInfo, "name", "username", "email") ?? "Bạn bè";

            result.Add(new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["title"] = "Tin nhắn mới",
                ["content"] = $"{senderName}: {lastMessage}",
                ["url"] = string.IsNullOrWhiteSpace(conversationId) ? "/messaging" : $"/messaging?conversationId={Uri.EscapeDataString(conversationId)}",
                ["created_at"] = lastMessageTime,
                ["source_id"] = conversationId,
                ["sender_id"] = lastSenderId
            });
        }

        EnsureNotificationIds(result);
        return result
            .OrderByDescending(x => x.GetValueOrDefault("created_at")?.ToString() ?? string.Empty)
            .Take(20)
            .ToList();
    }

    private async Task<List<string>> GetDismissedNotificationIdsAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return new List<string>();

        var saved = await _repo.GetByIdAsync(NotificationDismissalsCollection, userId);
        return NormalizeIdList(saved?.GetValueOrDefault("ids"));
    }

    private static List<Dictionary<string, object?>> FilterDismissed(List<Dictionary<string, object?>> items, IReadOnlyCollection<string> dismissedIds)
    {
        if (dismissedIds.Count == 0) return items;
        var dismissed = new HashSet<string>(dismissedIds, StringComparer.Ordinal);
        return items
            .Where(item => !dismissed.Contains(Text(item, "id") ?? string.Empty))
            .ToList();
    }

    private static List<string> NormalizeIdList(object? raw)
    {
        if (raw is null) return new List<string>();

        if (raw is IEnumerable<string> stringList)
        {
            return stringList
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (raw is System.Text.Json.JsonElement element && element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Select(item => item.ValueKind == System.Text.Json.JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (raw is System.Collections.IEnumerable items && raw is not string)
        {
            var ids = new List<string>();
            foreach (var item in items)
            {
                var id = item?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
            }
            return ids.Distinct(StringComparer.Ordinal).ToList();
        }

        var text = raw.ToString();
        return string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
    }

    private static void ApplyDefaultReadState(List<Dictionary<string, object?>> items)
    {
        EnsureNotificationIds(items);
        foreach (var item in items)
        {
            item["is_read"] = false;
        }
    }

    private static bool IsRead(Dictionary<string, object?> item)
    {
        return item.TryGetValue("is_read", out var raw) && raw is bool b && b;
    }

    private static void EnsureNotificationIds(IEnumerable<Dictionary<string, object?>> items)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(Text(item, "id"))) continue;
            var raw = $"{Text(item, "type")}|{Text(item, "title")}|{Text(item, "content")}|{Text(item, "url")}|{Text(item, "created_at")}";
            item["id"] = SafeHash(raw);
        }
    }

    private static string SafeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? Text(Dictionary<string, object?> value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (value.TryGetValue(key, out var raw) && raw is not null)
            {
                var text = raw.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return null;
    }
}

public sealed class ClearNotificationsRequest
{
    public List<string>? Ids { get; set; }
}
