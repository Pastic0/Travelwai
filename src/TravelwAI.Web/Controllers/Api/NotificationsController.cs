using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class NotificationsController : ApiControllerBase
{
    private readonly InAppNotificationService _notifications;

    public NotificationsController(
        IAuthService authService,
        InAppNotificationService notifications) : base(authService)
    {
        _notifications = notifications;
    }

    [HttpGet("notifications")]
    public async Task<IActionResult> GetNotifications()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var all = await _notifications.GetForUserAsync(current.userId!);
        var friends = FilterByCategory(all, InAppNotificationService.FriendRequestCategory);
        var messages = FilterByCategory(all, InAppNotificationService.MessageNewCategory);
        var tours = FilterByCategory(all, InAppNotificationService.TourBookedCategory);
        var schedules = FilterByCategory(all, InAppNotificationService.ScheduleCreatedCategory);
        var payments = FilterByCategory(all, InAppNotificationService.PaymentSuccessCategory);
        var unreadCount = all.Count(item => !ReadBool(item, "is_read", "isRead"));

        return Ok(new
        {
            success = true,
            local_only = false,
            data = new
            {
                friends,
                messages,
                tours,
                orders = tours,
                schedules,
                payments,
                all
            },
            count = all.Count,
            unread_count = unreadCount,
            message = "Đã tải thông báo theo tài khoản."
        });
    }

    [HttpPost("notifications/read")]
    public async Task<IActionResult> MarkNotificationsRead([FromBody] MarkNotificationsReadRequest? request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var ids = NormalizeIdList(request?.Ids);
        var updated = ids.Count == 0
            ? await _notifications.MarkAllReadForUserAsync(current.userId!)
            : await _notifications.MarkReadForUserAsync(current.userId!, ids);

        return Ok(new
        {
            success = true,
            read_count = updated,
            message = "Đã lưu trạng thái đã đọc theo tài khoản."
        });
    }

    [HttpPost("notifications/clear")]
    public async Task<IActionResult> ClearNotifications([FromBody] ClearNotificationsRequest? request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var ids = NormalizeIdList(request?.Ids);
        var deleted = ids.Count == 0
            ? await _notifications.DeleteAllForUserAsync(current.userId!)
            : await _notifications.DeleteForUserAsync(current.userId!, ids);

        return Ok(new
        {
            success = true,
            deleted_count = deleted,
            physically_deleted_count = deleted,
            message = "Đã dọn thông báo của tài khoản."
        });
    }

    private static List<Dictionary<string, object?>> FilterByCategory(
        IEnumerable<Dictionary<string, object?>> items,
        string category)
    {
        return items
            .Where(item => string.Equals(Text(item, "category"), category, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<string> NormalizeIdList(IEnumerable<string>? ids)
    {
        return (ids ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(2000)
            .ToList();
    }

    private static bool ReadBool(Dictionary<string, object?> item, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!item.TryGetValue(key, out var raw) || raw is null) continue;
            if (raw is bool boolean) return boolean;
            if (bool.TryParse(raw.ToString(), out var parsed)) return parsed;
        }
        return false;
    }

    private static string Text(Dictionary<string, object?> item, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!item.TryGetValue(key, out var raw) || raw is null) continue;
            var text = raw.ToString()?.Trim() ?? string.Empty;
            if (text.Length > 0) return text;
        }
        return string.Empty;
    }
}

public sealed class ClearNotificationsRequest
{
    public List<string>? Ids { get; set; }
}

public sealed class MarkNotificationsReadRequest
{
    public List<string>? Ids { get; set; }
}
