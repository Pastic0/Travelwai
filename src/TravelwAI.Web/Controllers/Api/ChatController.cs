using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Models.Requests;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class ChatController : ApiControllerBase
{
    private readonly IChatService _chatService;
    private readonly IFriendService _friendService;
    private readonly IFileStorageService _fileStorage;
    private readonly InAppNotificationService _notifications;

    public ChatController(
        IAuthService authService,
        IChatService chatService,
        IFriendService friendService,
        IFileStorageService fileStorage,
        InAppNotificationService notifications) : base(authService)
    {
        _chatService = chatService;
        _friendService = friendService;
        _fileStorage = fileStorage;
        _notifications = notifications;
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> GetConversations()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var conversations = await _chatService.GetConversationsAsync(current.userId!);
        return Ok(new { success = true, data = conversations, message = "Đã tải danh sách cuộc trò chuyện" });
    }

    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation(CreateConversationRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var participantIds = (request.ParticipantIds ?? new List<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Where(id => !string.Equals(id, current.userId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (participantIds.Count >= 2)
        {
            var groupId = await _chatService.CreateGroupConversationAsync(current.userId!, participantIds, request.GroupName);
            return groupId is null
                ? StatusCode(500, new { success = false, detail = "Không thể tạo nhóm trò chuyện" })
                : Ok(new { success = true, conversation_id = groupId, is_group = true, message = "Đã tạo nhóm trò chuyện" });
        }

        var otherUserId = !string.IsNullOrWhiteSpace(request.OtherUserId)
            ? request.OtherUserId.Trim()
            : participantIds.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(otherUserId))
        {
            return BadRequest(new { success = false, detail = "Thiếu mã người dùng cần trò chuyện" });
        }

        var id = await _chatService.CreateConversationAsync(current.userId!, otherUserId);
        return id is null
            ? StatusCode(500, new { success = false, detail = "Không thể tạo cuộc trò chuyện" })
            : Ok(new { success = true, conversation_id = id, is_group = false, message = "Đã tạo cuộc trò chuyện" });
    }

    [HttpPost("support/admin-conversation")]
    public async Task<IActionResult> CreateSupportAdminConversation()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var conversationId = await _chatService.CreateOrGetSupportAdminConversationAsync(current.userId!);
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return NotFound(new { success = false, detail = "Chưa tìm thấy tài khoản Admin chính để nhận hỗ trợ." });
        }

        return Ok(new { success = true, conversation_id = conversationId, message = "Đã mở hội thoại Admin." });
    }

    [HttpDelete("support/admin-conversation")]
    public async Task<IActionResult> DeleteSupportAdminConversation()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var deleted = await _chatService.DeleteSupportAdminConversationsForUserAsync(current.userId!);
        return Ok(new { success = true, deleted, message = "Đã xoá toàn bộ lịch sử hội thoại Admin." });
    }

    [HttpGet("conversations/{conversationId}/messages")]
    public async Task<IActionResult> GetMessages(string conversationId, [FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var conversations = await _chatService.GetConversationsAsync(current.userId!);
        if (!conversations.Any(c => c.GetValueOrDefault("id")?.ToString() == conversationId)) return StatusCode(403, new { success = false, detail = "Bạn không có quyền truy cập cuộc trò chuyện này" });
        var messages = await _chatService.GetMessagesAsync(conversationId, limit, offset);
        return Ok(new { success = true, data = messages, message = "Đã tải tin nhắn" });
    }

    [HttpPut("conversations/{conversationId}/name")]
    public async Task<IActionResult> UpdateConversationName(string conversationId, [FromBody] UpdateConversationNameRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return BadRequest(new { success = false, detail = "Tên không được để trống." });
        }

        var updated = await _chatService.UpdateConversationDisplayNameAsync(conversationId, current.userId!, request.DisplayName);
        if (updated is null)
        {
            return NotFound(new { success = false, detail = "Không tìm thấy cuộc trò chuyện hoặc bạn không có quyền đổi tên." });
        }

        return Ok(new { success = true, data = updated, message = "Đã lưu tên cuộc trò chuyện." });
    }

    [HttpPost("conversations/{conversationId}/attachments")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> UploadConversationAttachment(string conversationId, IFormFile? file)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var conversations = await _chatService.GetConversationsAsync(current.userId!);
        if (!conversations.Any(c => c.GetValueOrDefault("id")?.ToString() == conversationId))
        {
            return StatusCode(403, new { success = false, detail = "Bạn không có quyền truy cập cuộc trò chuyện này" });
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest(new { success = false, detail = "Chưa chọn tệp đính kèm" });
        }

        var url = await _fileStorage.SaveFileAsync(file, current.userId!, $"chat/{conversationId}");
        if (url is null)
        {
            return BadRequest(new { success = false, detail = "Tệp không hợp lệ, quá lớn hoặc chưa được hỗ trợ" });
        }

        return Ok(new
        {
            success = true,
            data = new
            {
                name = Path.GetFileName(file.FileName),
                url,
                contentType = file.ContentType,
                size = file.Length
            },
            message = "Đã tải tệp đính kèm"
        });
    }

    [HttpDelete("conversations/{conversationId}")]
    public async Task<IActionResult> DeleteConversation(string conversationId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var result = await _chatService.DeleteConversationAsync(conversationId, current.userId!);
        if (!result.Success) return NotFound(new { success = false, detail = "Không tìm thấy cuộc trò chuyện hoặc bạn không có quyền xóa." });

        var message = "Đã xoá toàn bộ lịch sử cuộc trò chuyện.";
        return Ok(new { success = true, action = result.Action, deleted_attachments = result.DeletedAttachments, message });
    }

    [HttpPost("friends/request")]
    public async Task<IActionResult> SendFriendRequest(FriendRequestPayload payload)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var result = await _friendService.CreateFriendRequestAsync(current.userId!, payload.TargetUserEmail);
        if (result.GetValueOrDefault("success") is bool ok && ok)
        {
            var recipientId = result.GetValueOrDefault("recipient_id")?.ToString() ?? string.Empty;
            var requestId = result.GetValueOrDefault("request_id")?.ToString() ?? Guid.NewGuid().ToString("N");
            var senderEmail = current.authUser?.GetValueOrDefault("email")?.ToString() ?? string.Empty;
            var senderName = current.authUser?.GetValueOrDefault("displayName")?.ToString()
                ?? current.authUser?.GetValueOrDefault("display_name")?.ToString()
                ?? current.authUser?.GetValueOrDefault("username")?.ToString()
                ?? senderEmail;
            if (string.IsNullOrWhiteSpace(senderName)) senderName = "Một người dùng";

            await _notifications.CreateForUserAsync(
                recipientId,
                "friend",
                InAppNotificationService.FriendRequestCategory,
                "Lời mời kết bạn",
                $"{senderName} đã gửi lời mời kết bạn.",
                "/messaging",
                "friend-request",
                requestId,
                "friend-request",
                metadata: new Dictionary<string, object?>
                {
                    ["request_email"] = senderEmail,
                    ["requester_id"] = current.userId,
                    ["requester_name"] = senderName
                });

            return Ok(result);
        }
        var message = result.GetValueOrDefault("message")?.ToString() ?? "Đã xảy ra lỗi";
        return Conflict(new { success = false, detail = message, message });
    }

    [HttpGet("friend_requests")]
    public async Task<IActionResult> GetFriendRequests()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var (friends, pending) = await _friendService.GetFriendsAsync(current.userId!);
        return Ok(new { success = true, data = friends, friends, pending, message = "Đã tải trạng thái bạn bè" });
    }

    [HttpGet("get_friends")]
    public async Task<IActionResult> GetFriendsLegacy()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var (friends, pending) = await _friendService.GetFriendsAsync(current.userId!);
        return Ok(new { success = true, data = friends, friends, pending, message = "Đã tải danh sách bạn bè" });
    }

    [HttpPost("friend_requests")]
    public async Task<IActionResult> UpdateFriendStatus([FromForm] string request_email, [FromForm] string action)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var result = await _friendService.UpdateFriendStatusAsync(request_email, current.userId!, action);
        if (result.GetValueOrDefault("success") is bool ok && ok)
        {
            var requestId = result.GetValueOrDefault("request_id")?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                await _notifications.DeactivateForUserAsync(
                    current.userId!,
                    "friend-request",
                    requestId,
                    "friend-request");
            }

            return Ok(new { success = true, data = result, message = result.GetValueOrDefault("message") });
        }
        var message = result.GetValueOrDefault("message")?.ToString() ?? "Không thể xử lý yêu cầu kết bạn.";
        return BadRequest(new { success = false, data = result, message });
    }

    [HttpDelete("friends/{friendUserId}")]
    public async Task<IActionResult> RemoveFriend(string friendUserId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var result = await _friendService.RemoveFriendAsync(current.userId!, friendUserId);
        if (result.GetValueOrDefault("success") is bool ok && ok) return Ok(result);
        var message = result.GetValueOrDefault("message")?.ToString() ?? "Không thể xóa bạn bè.";
        return NotFound(new { success = false, data = result, message, detail = message });
    }

    [HttpPost("support/admin-message")]
    public async Task<IActionResult> SendSupportMessageToAdmin([FromBody] SupportAdminMessageRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var message = (request?.Message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return BadRequest(new { success = false, detail = "Bạn chưa nhập nội dung cần hỗ trợ." });
        }

        if (message.Length > 100)
        {
            return BadRequest(new { success = false, detail = "Tin nhắn hỗ trợ tối đa 100 ký tự." });
        }

        var conversationId = await _chatService.CreateOrGetSupportAdminConversationAsync(current.userId!);
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return NotFound(new { success = false, detail = "Chưa tìm thấy tài khoản Admin chính để nhận hỗ trợ." });
        }

        var sendResult = await _chatService.SendMessageAsync(conversationId, current.userId!, message);
        if (!sendResult.Success)
        {
            return BadRequest(new { success = false, code = sendResult.ErrorCode, detail = sendResult.ErrorMessage ?? "Không thể gửi tin nhắn hỗ trợ." });
        }

        var senderName = current.authUser?.GetValueOrDefault("displayName")?.ToString()
            ?? current.authUser?.GetValueOrDefault("display_name")?.ToString()
            ?? current.authUser?.GetValueOrDefault("username")?.ToString()
            ?? current.authUser?.GetValueOrDefault("email")?.ToString()
            ?? "Người dùng";
        var conversation = (await _chatService.GetConversationsAsync(current.userId!))
            .FirstOrDefault(item => string.Equals(item.GetValueOrDefault("id")?.ToString(), conversationId, StringComparison.Ordinal));
        if (conversation is not null && !string.IsNullOrWhiteSpace(sendResult.MessageId))
        {
            var participantIds = ToStringList(conversation.GetValueOrDefault("participant_ids"));
            foreach (var recipientId in participantIds
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Select(id => id.Trim())
                         .Where(id => !string.Equals(id, current.userId, StringComparison.Ordinal))
                         .Distinct(StringComparer.Ordinal))
            {
                await _notifications.CreateForUserAsync(
                    recipientId,
                    "message",
                    InAppNotificationService.MessageNewCategory,
                    "Tin nhắn mới",
                    $"Bạn có tin nhắn mới từ {senderName}.",
                    $"/messaging?conversationId={Uri.EscapeDataString(conversationId)}",
                    "message",
                    sendResult.MessageId,
                    "message-new",
                    metadata: new Dictionary<string, object?>
                    {
                        ["conversation_id"] = conversationId,
                        ["sender_id"] = current.userId,
                        ["sender_name"] = senderName
                    });
            }
        }

        return Ok(new
        {
            success = true,
            conversation_id = conversationId,
            message = "Đã gửi tin nhắn cho Admin."
        });
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var users = await _chatService.GetAllUsersExceptAsync(current.userId!);
        return Ok(new { success = true, data = users, message = "Danh sách người dùng để tìm kiếm" });
    }
    private static List<string> ToStringList(object? value)
    {
        if (value is null) return new List<string>();
        if (value is string text) return string.IsNullOrWhiteSpace(text) ? new List<string>() : new List<string> { text };
        if (value is System.Collections.IEnumerable items)
        {
            var result = new List<string>();
            foreach (var item in items)
            {
                var itemText = item?.ToString();
                if (!string.IsNullOrWhiteSpace(itemText)) result.Add(itemText);
            }
            return result;
        }
        return new List<string>();
    }
}

public sealed class SupportAdminMessageRequest
{
    public string? Message { get; set; }
}
