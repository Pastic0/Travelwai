using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/feedback")]
public sealed class FeedbackController : ApiControllerBase
{
    private const string Collection = "feedbacks";
    private const int MaxMessageLength = 4000;
    private const int MaxAttachments = 3;
    private const long MaxAttachmentBytes = 10L * 1024 * 1024;
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "processing", "resolved", "closed"
    };

    private readonly IDataRepository _repo;
    private readonly IFileStorageService _fileStorage;
    private readonly InAppNotificationService _notifications;

    public FeedbackController(
        IAuthService authService,
        IDataRepository repo,
        IFileStorageService fileStorage,
        InAppNotificationService notifications) : base(authService)
    {
        _repo = repo;
        _fileStorage = fileStorage;
        _notifications = notifications;
    }

    [HttpPost]
    [RequestSizeLimit(35L * 1024 * 1024)]
    public async Task<IActionResult> Create()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var form = await Request.ReadFormAsync();
        var cleanMessage = form["message"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(cleanMessage))
        {
            return BadRequest(new { success = false, message = "Vui lòng nhập nội dung phản hồi." });
        }
        if (cleanMessage.Length > MaxMessageLength)
        {
            return BadRequest(new { success = false, message = $"Nội dung phản hồi tối đa {MaxMessageLength} ký tự." });
        }

        var files = form.Files.Take(MaxAttachments + 1).ToList();
        if (files.Count > MaxAttachments)
        {
            return BadRequest(new { success = false, message = $"Mỗi phản hồi tối đa {MaxAttachments} tệp." });
        }
        if (files.Any(file => file.Length <= 0 || file.Length > MaxAttachmentBytes))
        {
            return BadRequest(new { success = false, message = "Mỗi tệp đính kèm phải nhỏ hơn hoặc bằng 10 MB." });
        }

        var id = Guid.NewGuid().ToString("N");
        var attachments = new List<Dictionary<string, object?>>();
        try
        {
            foreach (var file in files)
            {
                var url = await _fileStorage.SaveFileAsync(file, current.userId!, $"feedback/{id}");
                if (string.IsNullOrWhiteSpace(url))
                {
                    throw new InvalidOperationException($"Tệp {file.FileName} không được hỗ trợ.");
                }
                attachments.Add(new Dictionary<string, object?>
                {
                    ["url"] = url,
                    ["name"] = Path.GetFileName(file.FileName),
                    ["content_type"] = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    ["size"] = file.Length
                });
            }

            var now = DateTime.UtcNow;
            var authUser = current.authUser ?? new Dictionary<string, object?>();
            var email = ReadText(authUser, "email");
            var userName = FirstText(authUser, "username", "displayName", "display_name", "name", "email");
            var data = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["user_id"] = current.userId,
                ["user_email"] = email,
                ["user_name"] = userName,
                ["message"] = cleanMessage,
                ["attachments"] = attachments,
                ["status"] = "new",
                ["admin_note"] = string.Empty,
                ["created_at"] = now,
                ["updated_at"] = now
            };
            await _repo.SetAsync(Collection, id, data, merge: false);

            try
            {
                await Task.WhenAll(
                    _notifications.CreateForUserAsync(
                        current.userId!,
                        "feedback",
                        "system",
                        "Đã gửi phản hồi",
                        "Phản hồi của bạn đã được ghi nhận và chuyển đến quản trị viên.",
                        "/notifications",
                        "feedback",
                        id,
                        "created"),
                    _notifications.CreateForRoleAsync(
                        "Admin",
                        "feedback",
                        "system",
                        "Có phản hồi mới",
                        $"{(string.IsNullOrWhiteSpace(userName) ? email : userName)} vừa gửi một phản hồi mới.",
                        "/admin",
                        "feedback",
                        id,
                        "created"));
            }
            catch
            {
                // Phản hồi đã được lưu; lỗi thông báo không được làm mất phản hồi hoặc tệp đính kèm.
            }

            return Ok(new { success = true, message = "Đã gửi phản hồi.", data = ToFeedbackPayload(data) });
        }
        catch
        {
            foreach (var attachment in attachments)
            {
                var url = ReadText(attachment, "url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    try { await _fileStorage.DeleteStoredFileByUrlAsync(url); } catch { }
                }
            }
            throw;
        }
    }

    [HttpGet("mine")]
    public async Task<IActionResult> Mine([FromQuery] int limit = 20)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var rows = await _repo.WhereEqualAsync(Collection, "user_id", current.userId!, Math.Clamp(limit, 1, 50));
        var data = rows
            .OrderByDescending(row => ReadText(row, "created_at"), StringComparer.Ordinal)
            .Select(ToFeedbackPayload)
            .ToList();
        return Ok(new { success = true, data });
    }

    [HttpGet("admin")]
    public async Task<IActionResult> AdminList([FromQuery] string? status, [FromQuery] string? search)
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;

        var cleanStatus = (status ?? string.Empty).Trim().ToLowerInvariant();
        var cleanSearch = (search ?? string.Empty).Trim();
        var rows = await _repo.GetAllAsync(Collection, limit: 1000);
        var filtered = rows.Where(row =>
        {
            if (!string.IsNullOrWhiteSpace(cleanStatus)
                && !string.Equals(ReadText(row, "status"), cleanStatus, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(cleanSearch)) return true;
            var source = string.Join(" ", new[]
            {
                ReadText(row, "user_name"), ReadText(row, "user_email"), ReadText(row, "message"), ReadText(row, "admin_note")
            });
            return source.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase);
        });

        var data = filtered
            .OrderByDescending(row => ReadText(row, "created_at"), StringComparer.Ordinal)
            .Select(ToFeedbackPayload)
            .ToList();
        return Ok(new { success = true, data });
    }

    [HttpPut("admin/{id}")]
    public async Task<IActionResult> AdminUpdate(string id, [FromBody] FeedbackUpdateRequest request)
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;
        var row = await _repo.GetByIdAsync(Collection, id);
        if (row is null) return NotFound(new { success = false, message = "Không tìm thấy phản hồi." });

        var status = (request.Status ?? ReadText(row, "status") ?? "new").Trim().ToLowerInvariant();
        if (!AllowedStatuses.Contains(status))
        {
            return BadRequest(new { success = false, message = "Trạng thái phản hồi không hợp lệ." });
        }
        var note = (request.AdminNote ?? string.Empty).Trim();
        if (note.Length > MaxMessageLength)
        {
            return BadRequest(new { success = false, message = $"Nội dung xử lý tối đa {MaxMessageLength} ký tự." });
        }

        await _repo.UpdateAsync(Collection, id, new Dictionary<string, object?>
        {
            ["status"] = status,
            ["admin_note"] = note,
            ["handled_by"] = admin.userId,
            ["updated_at"] = DateTime.UtcNow
        });
        var userId = ReadText(row, "user_id");
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var statusLabel = status switch
            {
                "processing" => "đang được xử lý",
                "resolved" => "đã được xử lý",
                "closed" => "đã đóng",
                _ => "đã được tiếp nhận"
            };
            var detail = string.IsNullOrWhiteSpace(note)
                ? $"Phản hồi của bạn {statusLabel}."
                : $"Phản hồi của bạn {statusLabel}. Phản hồi từ Admin: {note}";
            try
            {
                await _notifications.CreateForUserAsync(
                    userId,
                    "feedback",
                    "system",
                    "Cập nhật phản hồi",
                    detail,
                    "/notifications",
                    "feedback",
                    id,
                    $"status-{status}");
            }
            catch
            {
                // Trạng thái phản hồi đã được cập nhật; thông báo là tác vụ bổ sung.
            }
        }

        var updated = await _repo.GetByIdAsync(Collection, id);
        return Ok(new { success = true, message = "Đã cập nhật phản hồi.", data = ToFeedbackPayload(updated ?? row) });
    }

    [HttpDelete("admin/{id}")]
    public async Task<IActionResult> AdminDelete(string id)
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;
        var row = await _repo.GetByIdAsync(Collection, id);
        if (row is null) return NotFound(new { success = false, message = "Không tìm thấy phản hồi." });

        foreach (var attachment in ReadAttachments(row))
        {
            var url = ReadText(attachment, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            await _fileStorage.DeleteStoredFileByUrlAsync(url);
        }
        await _repo.DeleteAsync(Collection, id);
        return Ok(new { success = true, message = "Đã xóa phản hồi." });
    }

    private async Task<(bool ok, string? userId, IActionResult? error)> RequireAdminAsync()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return (false, null, current.error);
        var role = NormalizeAccountRole(current.authUser?.GetValueOrDefault("role"));
        if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, StatusCode(403, new { success = false, message = "Chỉ Admin mới được truy cập." }));
        }
        return (true, current.userId, null);
    }

    private static object ToFeedbackPayload(Dictionary<string, object?> row)
    {
        return new
        {
            id = ReadText(row, "id"),
            userId = ReadText(row, "user_id"),
            userName = ReadText(row, "user_name"),
            userEmail = ReadText(row, "user_email"),
            message = ReadText(row, "message"),
            attachments = ReadAttachments(row).Select(item => new
            {
                url = ReadText(item, "url"),
                name = ReadText(item, "name"),
                contentType = ReadText(item, "content_type"),
                size = ReadLong(item, "size")
            }),
            status = ReadText(row, "status", "new"),
            adminNote = ReadText(row, "admin_note"),
            createdAt = ReadText(row, "created_at"),
            updatedAt = ReadText(row, "updated_at")
        };
    }

    private static List<Dictionary<string, object?>> ReadAttachments(Dictionary<string, object?> row)
    {
        if (!row.TryGetValue("attachments", out var raw) || raw is not IEnumerable<object?> list) return new();
        return list.OfType<Dictionary<string, object?>>().ToList();
    }

    private static string FirstText(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = ReadText(row, key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }

    private static string ReadText(Dictionary<string, object?> row, string key, string fallback = "")
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O"),
            _ => value.ToString()?.Trim() ?? fallback
        };
    }

    private static long ReadLong(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) && long.TryParse(value?.ToString(), out var result) ? result : 0;

    public sealed class FeedbackUpdateRequest
    {
        public string? Status { get; set; }
        public string? AdminNote { get; set; }
    }
}
