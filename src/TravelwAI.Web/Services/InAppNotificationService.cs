using System.Security.Cryptography;
using System.Text;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

public sealed class InAppNotificationService
{
    public const string Collection = "notifications";
    public const string TourBookedCategory = "tour-booked";
    public const string ScheduleCreatedCategory = "schedule-created";
    public const string PaymentSuccessCategory = "payment-success";
    public const string FriendRequestCategory = "friend-request";
    public const string MessageNewCategory = "message-new";


    private readonly IDataRepository _repo;

    public InAppNotificationService(IDataRepository repo)
    {
        _repo = repo;
    }

    public Task<string?> CreateForUserAsync(
        string userId,
        string type,
        string category,
        string title,
        string content,
        string? url = null,
        string? sourceType = null,
        string? sourceId = null,
        string? eventKey = null,
        string severity = "info",
        DateTime? expiresAt = null,
        Dictionary<string, object?>? metadata = null)
    {
        if (!IsSupportedCategory(category)) return Task.FromResult<string?>(null);
        return CreateUserNotificationAsync(userId, type, category, title, content, url, sourceType, sourceId, eventKey, severity, expiresAt, metadata);
    }

    // Thông báo chỉ còn được phân phối theo tài khoản. Giữ hai hàm này để
    // các đoạn mã cũ vẫn biên dịch, nhưng không tạo thêm thông báo role/all.
    public Task<string?> CreateForRoleAsync(
        string role,
        string type,
        string category,
        string title,
        string content,
        string? url = null,
        string? sourceType = null,
        string? sourceId = null,
        string? eventKey = null,
        string severity = "info",
        DateTime? expiresAt = null)
        => Task.FromResult<string?>(null);

    public Task<string?> CreateBroadcastAsync(
        string type,
        string category,
        string title,
        string content,
        string? url = null,
        string? sourceType = null,
        string? sourceId = null,
        string? eventKey = null,
        string severity = "info",
        DateTime? expiresAt = null)
        => Task.FromResult<string?>(null);

    public Task<bool> DeactivateForUserAsync(string userId, string sourceType, string sourceId, string eventKey)
    {
        return DeactivateAsync(userId, sourceType, sourceId, eventKey);
    }

    public Task<bool> DeactivateForRoleAsync(string role, string sourceType, string sourceId, string eventKey)
        => Task.FromResult(false);

    public async Task<List<Dictionary<string, object?>>> GetForUserAsync(string userId)
    {
        var cleanUserId = (userId ?? string.Empty).Trim();
        if (cleanUserId.Length == 0) return new List<Dictionary<string, object?>>();

        // Chỉ một truy vấn theo recipient_id, không truy vấn role, broadcast,
        // bảng trạng thái hoặc các bảng nghiệp vụ khác khi mở thông báo.
        var rows = await _repo.WhereEqualAsync(Collection, "recipient_id", cleanUserId, limit: 300);
        var now = DateTime.UtcNow;

        var items = rows
            .Where(IsVisible)
            .Where(IsSupportedNotification)
            .Where(item =>
            {
                var expiresAt = ParseDate(FirstText(item, "expires_at", "expiresAt"));
                return expiresAt == DateTime.MinValue || expiresAt > now;
            })
            .ToList();

        foreach (var item in items)
        {
            NormalizeNotificationKind(item);
            var storageId = FirstText(item, "id", "Id");
            if (storageId.Length == 0) storageId = FirstText(item, "notification_key", "notificationKey");
            item["storage_id"] = storageId;
            item["id"] = storageId;
            item["is_read"] = ReadBool(item, "is_read", "isRead");
        }

        return items
            .GroupBy(item => FirstText(item, "id", "Id"), StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(item => ParseDate(FirstText(item, "created_at", "createdAt")))
            .Take(300)
            .ToList();
    }

    // Tương thích chữ ký cũ; role không còn được dùng để tải thông báo.
    public Task<List<Dictionary<string, object?>>> GetForUserAsync(string userId, string? role)
        => GetForUserAsync(userId);

    public async Task PersistForUserAsync(string userId, IEnumerable<Dictionary<string, object?>> notifications)
    {
        var cleanUserId = (userId ?? string.Empty).Trim();
        if (cleanUserId.Length == 0 || notifications is null) return;

        // Ghi tuần tự để không chiếm nhiều session cùng lúc.
        foreach (var item in notifications)
        {
            if (item is null || !IsSupportedNotification(item)) continue;
            var title = FirstText(item, "title");
            var content = FirstText(item, "content", "message");
            if (title.Length == 0 || content.Length == 0) continue;

            var notificationKey = FirstText(item, "notification_key", "notificationKey", "id", "Id");
            if (notificationKey.Length == 0)
            {
                notificationKey = SafeHash($"{FirstText(item, "type")}|{title}|{content}|{FirstText(item, "url")}|{FirstText(item, "created_at", "createdAt")}");
            }

            var documentId = SafeHash($"user|{cleanUserId}|materialized|{notificationKey}");
            var data = new Dictionary<string, object?>(item, StringComparer.Ordinal)
            {
                ["notification_key"] = notificationKey,
                ["recipient_id"] = cleanUserId,
                ["audience"] = "user",
                ["is_active"] = true,
                ["updated_at"] = DateTime.UtcNow
            };
            data.Remove("id");
            data.Remove("Id");
            data.Remove("storage_id");
            if (FirstText(data, "created_at", "createdAt").Length == 0) data["created_at"] = DateTime.UtcNow;
            await _repo.SetAsync(Collection, documentId, data, merge: true);
        }
    }

    public async Task<int> MarkReadForUserAsync(string userId, IEnumerable<string> notificationIds)
    {
        var cleanUserId = (userId ?? string.Empty).Trim();
        var ids = NormalizeIds(notificationIds);
        if (cleanUserId.Length == 0 || ids.Count == 0) return 0;

        var now = DateTime.UtcNow;
        return await _repo.UpdateWhereEqualAndInAsync(
            Collection,
            "recipient_id",
            cleanUserId,
            "id",
            ids,
            new Dictionary<string, object?>
            {
                ["is_read"] = true,
                ["read_at"] = now,
                ["updated_at"] = now
            });
    }

    public async Task<int> MarkAllReadForUserAsync(string userId)
    {
        var cleanUserId = (userId ?? string.Empty).Trim();
        if (cleanUserId.Length == 0) return 0;

        var now = DateTime.UtcNow;
        return await _repo.UpdateWhereEqualAsync(
            Collection,
            "recipient_id",
            cleanUserId,
            new Dictionary<string, object?>
            {
                ["is_read"] = true,
                ["read_at"] = now,
                ["updated_at"] = now
            });
    }

    public async Task<int> DeleteForUserAsync(string userId, IEnumerable<string> notificationIds)
    {
        var cleanUserId = (userId ?? string.Empty).Trim();
        var ids = NormalizeIds(notificationIds);
        if (cleanUserId.Length == 0 || ids.Count == 0) return 0;

        return await _repo.DeleteWhereEqualAndInAsync(
            Collection,
            "recipient_id",
            cleanUserId,
            "id",
            ids);
    }

    public async Task<int> DeleteAllForUserAsync(string userId)
    {
        var cleanUserId = (userId ?? string.Empty).Trim();
        if (cleanUserId.Length == 0) return 0;
        return await _repo.DeleteWhereEqualAsync(Collection, "recipient_id", cleanUserId);
    }

    private async Task<bool> DeactivateAsync(string userId, string sourceType, string sourceId, string eventKey)
    {
        var documentId = SafeHash($"user|{userId.Trim()}|{Normalize(sourceType, "manual")}|{sourceId.Trim()}|{Normalize(eventKey, "created")}");
        return await _repo.UpdateAsync(Collection, documentId, new Dictionary<string, object?>
        {
            ["is_active"] = false,
            ["resolved_at"] = DateTime.UtcNow,
            ["updated_at"] = DateTime.UtcNow
        });
    }

    private async Task<string?> CreateUserNotificationAsync(
        string userId,
        string type,
        string category,
        string title,
        string content,
        string? url,
        string? sourceType,
        string? sourceId,
        string? eventKey,
        string severity,
        DateTime? expiresAt,
        Dictionary<string, object?>? metadata)
    {
        userId = (userId ?? string.Empty).Trim();
        title = (title ?? string.Empty).Trim();
        content = (content ?? string.Empty).Trim();
        if (userId.Length == 0 || title.Length == 0 || content.Length == 0) return null;

        type = Normalize(type, TypeForCategory(category));
        category = Normalize(category, string.Empty);
        severity = Normalize(severity, "info");
        sourceType = Normalize(sourceType, "manual");
        sourceId = string.IsNullOrWhiteSpace(sourceId) ? Guid.NewGuid().ToString("N") : sourceId.Trim();
        eventKey = Normalize(eventKey, category);

        var documentId = SafeHash($"user|{userId}|{sourceType}|{sourceId}|{eventKey}");
        var now = DateTime.UtcNow;
        var data = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["category"] = category,
            ["title"] = title,
            ["content"] = content,
            ["url"] = string.IsNullOrWhiteSpace(url) ? "/notifications" : url.Trim(),
            ["severity"] = severity,
            ["source_type"] = sourceType,
            ["source_id"] = sourceId,
            ["event_key"] = eventKey,
            ["notification_key"] = SafeHash($"{sourceType}|{sourceId}|{eventKey}|{type}|{category}"),
            ["recipient_id"] = userId,
            ["audience"] = "user",
            ["is_active"] = true,
            ["is_read"] = false,
            ["created_at"] = now,
            ["updated_at"] = now
        };

        if (metadata is not null)
        {
            var reservedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "id",
                "recipient_id",
                "audience",
                "notification_key",
                "is_active",
                "is_read",
                "created_at",
                "updated_at"
            };

            foreach (var pair in metadata)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key)
                    && pair.Value is not null
                    && !reservedKeys.Contains(pair.Key))
                {
                    data[pair.Key] = pair.Value;
                }
            }
        }

        if (expiresAt.HasValue) data["expires_at"] = expiresAt.Value.ToUniversalTime();
        await _repo.SetAsync(Collection, documentId, data, merge: true);
        return documentId;
    }

    private static bool IsSupportedNotification(Dictionary<string, object?> item)
    {
        var category = Normalize(FirstText(item, "category"), string.Empty);
        if (IsSupportedCategory(category)) return true;

        // Chỉ giữ tương thích với đúng năm thông báo cũ đã được lưu trước đây.
        var title = FirstText(item, "title");
        return string.Equals(title, "Đã đặt tour", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, "Đã gửi yêu cầu đặt tour", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, "Đã lập lịch trình", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, "Đã lưu lịch trình", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, "Thanh toán thành công", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, "Lời mời kết bạn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, "Tin nhắn mới", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedCategory(string? category)
    {
        var normalized = Normalize(category, string.Empty);
        return normalized is TourBookedCategory
            or ScheduleCreatedCategory
            or PaymentSuccessCategory
            or FriendRequestCategory
            or MessageNewCategory;
    }

    private static void NormalizeNotificationKind(Dictionary<string, object?> item)
    {
        var category = Normalize(FirstText(item, "category"), string.Empty);
        var title = FirstText(item, "title");

        if (!IsSupportedCategory(category))
        {
            category = title switch
            {
                "Đã đặt tour" or "Đã gửi yêu cầu đặt tour" => TourBookedCategory,
                "Đã lập lịch trình" or "Đã lưu lịch trình" => ScheduleCreatedCategory,
                "Thanh toán thành công" => PaymentSuccessCategory,
                "Lời mời kết bạn" => FriendRequestCategory,
                "Tin nhắn mới" => MessageNewCategory,
                _ => category
            };
        }

        item["category"] = category;
        item["type"] = TypeForCategory(category);
    }

    private static string TypeForCategory(string? category)
    {
        return Normalize(category, string.Empty) switch
        {
            TourBookedCategory => "tour",
            ScheduleCreatedCategory => "schedule",
            PaymentSuccessCategory => "payment",
            FriendRequestCategory => "friend",
            MessageNewCategory => "message",
            _ => "system"
        };
    }

    private static List<string> NormalizeIds(IEnumerable<string>? notificationIds)
    {
        return (notificationIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(2000)
            .ToList();
    }

    private static bool IsVisible(Dictionary<string, object?> item)
    {
        if (ReadBool(item, "is_deleted", "isDeleted")) return false;
        if (!item.TryGetValue("is_active", out var raw) || raw is null) return true;
        if (raw is bool boolean) return boolean;
        return !bool.TryParse(raw.ToString(), out var parsed) || parsed;
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

    private static string Normalize(string? value, string fallback)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text.Length == 0 ? fallback : text;
    }

    private static string SafeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string FirstText(Dictionary<string, object?>? row, params string[] keys)
    {
        if (row is null) return string.Empty;
        foreach (var key in keys)
        {
            if (!row.TryGetValue(key, out var raw) || raw is null) continue;
            var text = raw.ToString()?.Trim() ?? string.Empty;
            if (text.Length > 0) return text;
        }
        return string.Empty;
    }

    private static DateTime ParseDate(string? value)
    {
        if (!DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return DateTime.MinValue;
        }
        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
    }
}
