using System.Text.Json;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Business.Services;

public sealed class ChatService : IChatService
{
    private const string PrimaryAdminId = "admin2324802010387";
    private const string PrimaryAdminEmail = "2324802010387@student.tdmu.edu.vn";
    private const int MaxMessageTextLength = 100;
    private const int MaxMessageAttachments = 5;
    private const string ChatMessagePayloadType = "travelwai-chat-message";

    private readonly IDataRepository _repo;
    private readonly IFileStorageService _fileStorage;

    public ChatService(IDataRepository repo, IFileStorageService fileStorage)
    {
        _repo = repo;
        _fileStorage = fileStorage;
    }

    public async Task<string?> CreateConversationAsync(string currentUserId, string otherUserId)
    {
        if (string.IsNullOrWhiteSpace(currentUserId) || string.IsNullOrWhiteSpace(otherUserId)) return null;
        if (string.Equals(currentUserId, otherUserId, StringComparison.Ordinal)) return null;

        var existing = await FindDirectConversationAsync(currentUserId, otherUserId);
        if (existing is not null)
        {
            await UnhideConversationForUserAsync(existing, currentUserId);
            return existing;
        }

        var participantIds = new List<string> { currentUserId, otherUserId };
        var id = await _repo.AddAsync("conversations", new Dictionary<string, object?>
        {
            ["conversation_type"] = "direct",
            ["is_group"] = false,
            ["created_user"] = currentUserId,
            ["created_by"] = currentUserId,
            ["other_user"] = otherUserId,
            ["participant_ids"] = participantIds,
            ["created_at"] = DateTime.UtcNow,
            ["last_message_time"] = null,
            ["last_message"] = null
        });

        if (id is not null)
        {
            await _repo.UpdateAsync("conversations", id, new Dictionary<string, object?> { ["conversation_id"] = id });
        }

        return id;
    }

    public async Task<string?> CreateGroupConversationAsync(string currentUserId, IEnumerable<string> participantIds, string? groupName = null)
    {
        if (string.IsNullOrWhiteSpace(currentUserId)) return null;

        var cleanParticipantIds = participantIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Append(currentUserId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (cleanParticipantIds.Count < 2) return null;

        var cleanGroupName = string.IsNullOrWhiteSpace(groupName)
            ? "Nhóm trò chuyện"
            : groupName.Trim();

        var id = await _repo.AddAsync("conversations", new Dictionary<string, object?>
        {
            ["conversation_type"] = "group",
            ["is_group"] = true,
            ["group_name"] = cleanGroupName,
            ["created_user"] = currentUserId,
            ["created_by"] = currentUserId,
            ["participant_ids"] = cleanParticipantIds,
            ["created_at"] = DateTime.UtcNow,
            ["last_message_time"] = null,
            ["last_message"] = null
        });

        if (id is not null)
        {
            await _repo.UpdateAsync("conversations", id, new Dictionary<string, object?> { ["conversation_id"] = id });
        }

        return id;
    }

    public async Task<string?> CreateOrGetSupportAdminConversationAsync(string currentUserId)
    {
        if (string.IsNullOrWhiteSpace(currentUserId)) return null;

        var currentUser = await GetUserByIdAsync(currentUserId);
        if (IsAdminUser(currentUser)) return null;

        var existing = await _repo.WhereEqualAsync("conversations", "support_user_id", currentUserId, limit: 20);
        var supportConversation = existing
            .Where(c => IsTruthy(c.GetValueOrDefault("support_admin")))
            .OrderByDescending(c => c.GetValueOrDefault("created_at")?.ToString() ?? string.Empty)
            .FirstOrDefault();

        string? assignedAdminId = null;
        if (supportConversation is not null)
        {
            var candidates = new List<string?>
            {
                supportConversation.GetValueOrDefault("primary_admin_id")?.ToString(),
                supportConversation.GetValueOrDefault("other_user")?.ToString()
            };
            candidates.AddRange(ToStringList(supportConversation.GetValueOrDefault("participant_ids")));

            foreach (var candidate in candidates
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Select(id => id!.Trim())
                         .Where(id => !string.Equals(id, currentUserId, StringComparison.Ordinal))
                         .Distinct(StringComparer.Ordinal))
            {
                var admin = await GetUserByIdAsync(candidate);
                if (!IsAdminUser(admin)) continue;
                assignedAdminId = candidate;
                break;
            }
        }

        // Hội thoại hỗ trợ đã gán cho Admin nào thì giữ nguyên Admin đó.
        // Chỉ chọn Admin mặc định khi tạo mới hoặc Admin cũ không còn quyền Admin.
        if (string.IsNullOrWhiteSpace(assignedAdminId))
        {
            var primaryAdmin = await GetPrimaryAdminUserInternalAsync();
            assignedAdminId = primaryAdmin?.GetValueOrDefault("id")?.ToString();
        }

        if (string.IsNullOrWhiteSpace(assignedAdminId)
            || string.Equals(assignedAdminId, currentUserId, StringComparison.Ordinal))
        {
            return null;
        }

        var participantIds = new List<string> { assignedAdminId, currentUserId }
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (supportConversation is not null)
        {
            var conversationId = supportConversation.GetValueOrDefault("id")?.ToString();
            if (!string.IsNullOrWhiteSpace(conversationId))
            {
                var hiddenForUserIds = ToStringList(supportConversation.GetValueOrDefault("hidden_for_user_ids"));
                hiddenForUserIds.RemoveAll(id => string.Equals(id, currentUserId, StringComparison.Ordinal));
                await _repo.UpdateAsync("conversations", conversationId, new Dictionary<string, object?>
                {
                    ["participant_ids"] = participantIds,
                    ["hidden_for_user_ids"] = hiddenForUserIds,
                    ["conversation_type"] = "direct",
                    ["is_group"] = false,
                    ["support_admin"] = true,
                    ["support_user_id"] = currentUserId,
                    ["primary_admin_id"] = assignedAdminId,
                    ["group_name"] = BuildSupportAdminGroupName(currentUser),
                    ["created_user"] = currentUserId,
                    ["other_user"] = assignedAdminId
                });
                return conversationId;
            }
        }

        var id = await _repo.AddAsync("conversations", new Dictionary<string, object?>
        {
            ["conversation_type"] = "direct",
            ["is_group"] = false,
            ["support_admin"] = true,
            ["support_user_id"] = currentUserId,
            ["primary_admin_id"] = assignedAdminId,
            ["group_name"] = BuildSupportAdminGroupName(currentUser),
            ["created_user"] = currentUserId,
            ["created_by"] = currentUserId,
            ["other_user"] = assignedAdminId,
            ["participant_ids"] = participantIds,
            ["created_at"] = DateTime.UtcNow,
            ["last_message_time"] = null,
            ["last_message"] = null
        });

        if (id is not null)
        {
            await _repo.UpdateAsync("conversations", id, new Dictionary<string, object?> { ["conversation_id"] = id });
        }

        return id;
    }

    public async Task<int> DeleteSupportAdminConversationsForUserAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return 0;

        var currentUser = await GetUserByIdAsync(userId);
        if (IsAdminUser(currentUser)) return 0;

        var supportConversations = await _repo.WhereEqualAsync("conversations", "support_user_id", userId, limit: 50);
        var deleted = 0;
        foreach (var conversation in supportConversations.Where(c => IsTruthy(c.GetValueOrDefault("support_admin"))))
        {
            var conversationId = conversation.GetValueOrDefault("id")?.ToString();
            if (string.IsNullOrWhiteSpace(conversationId)) continue;
            var result = await DeleteConversationAsync(conversationId, userId);
            if (result.Success) deleted += 1;
        }

        return deleted;
    }

    public async Task<List<Dictionary<string, object?>>> GetConversationsAsync(string userId)
    {
        var all = await _repo.WhereArrayContainsAsync("conversations", "participant_ids", userId, limit: 100);

        if (all.Count == 0)
        {
            all = (await _repo.WhereEqualAsync("conversations", "created_user", userId, limit: 50))
                .Concat(await _repo.WhereEqualAsync("conversations", "other_user", userId, limit: 50))
                .GroupBy(c => c.GetValueOrDefault("id")?.ToString())
                .Select(g => g.First())
                .ToList();
        }

        var currentUser = await GetUserByIdAsync(userId);
        if (IsAdminUser(currentUser))
        {
            // Chỉ nạp hội thoại hỗ trợ được gán chính xác cho tài khoản Admin hiện tại.
            // Không quét hoặc chuyển hội thoại của Admin khác sang tài khoản đang đăng nhập.
            var assignedSupportConversations = await _repo.WhereEqualAsync(
                "conversations",
                "primary_admin_id",
                userId,
                limit: 200);

            foreach (var supportConversation in assignedSupportConversations
                         .Where(c => IsTruthy(c.GetValueOrDefault("support_admin"))))
            {
                var conversationId = supportConversation.GetValueOrDefault("id")?.ToString();
                var supportUserId = supportConversation.GetValueOrDefault("support_user_id")?.ToString();
                if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(supportUserId)) continue;
                if (string.Equals(supportUserId, userId, StringComparison.Ordinal)) continue;

                var participantIds = new List<string> { userId, supportUserId }
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (!GetParticipantIds(supportConversation).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(participantIds))
                {
                    await _repo.UpdateAsync("conversations", conversationId, new Dictionary<string, object?>
                    {
                        ["participant_ids"] = participantIds,
                        ["conversation_type"] = "direct",
                        ["is_group"] = false,
                        ["support_admin"] = true,
                        ["primary_admin_id"] = userId,
                        ["created_user"] = supportUserId,
                        ["other_user"] = userId
                    });
                }

                supportConversation["participant_ids"] = participantIds;
                supportConversation["conversation_type"] = "direct";
                supportConversation["is_group"] = false;
                supportConversation["created_user"] = supportUserId;
                supportConversation["other_user"] = userId;
                all.Add(supportConversation);
            }
        }

        all = all
            .GroupBy(c => c.GetValueOrDefault("id")?.ToString())
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .Select(g => g.First())
            .Where(c => IsUserInConversation(c, userId))
            .Where(c => !IsConversationHiddenForUser(c, userId))
            .ToList();

        foreach (var conv in all)
        {
            await HydrateConversationAsync(conv, userId);
        }

        return all.OrderByDescending(x => x.GetValueOrDefault("last_message_time")?.ToString() ?? string.Empty).ToList();
    }

    public async Task<ChatMessageSendResult> SendMessageAsync(string conversationId, string senderId, string content)
    {
        var conversation = await _repo.GetByIdAsync("conversations", conversationId);
        if (conversation is null || !IsUserInConversation(conversation, senderId))
        {
            return new ChatMessageSendResult(false, null, string.Empty, "CHAT_FORBIDDEN", "Bạn không có quyền gửi tin nhắn trong hội thoại này.");
        }

        var normalized = await NormalizeMessageContentAsync(conversationId, senderId, content);
        if (!normalized.Success)
        {
            return new ChatMessageSendResult(false, null, string.Empty, normalized.ErrorCode, normalized.ErrorMessage);
        }

        var now = DateTime.UtcNow;
        var id = await _repo.AddAsync("messages", new Dictionary<string, object?>
        {
            ["sender_id"] = senderId,
            ["conversation_id"] = conversationId,
            ["content"] = normalized.Content,
            ["time_sent"] = now
        });

        if (id is null)
        {
            return new ChatMessageSendResult(false, null, string.Empty, "CHAT_SAVE_FAILED", "Không thể lưu tin nhắn.");
        }

        await _repo.UpdateAsync("conversations", conversationId, new Dictionary<string, object?>
        {
            ["last_message"] = normalized.Content,
            ["last_message_time"] = now,
            ["last_sender_id"] = senderId,

            ["hidden_for_user_ids"] = new List<string>()
        });

        return new ChatMessageSendResult(true, id, normalized.Content);
    }

    public async Task<List<Dictionary<string, object?>>> GetMessagesAsync(string conversationId, int limit, int offset)
    {
        var safeLimit = Math.Clamp(limit, 1, 100);
        var safeOffset = Math.Max(0, offset);
        var ordered = await _repo.WhereEqualPagedAsync(
            "messages",
            "conversation_id",
            conversationId,
            "time_sent",
            descending: false,
            limit: safeLimit,
            offset: safeOffset);

        foreach (var msg in ordered)
        {
            var senderId = msg.GetValueOrDefault("sender_id")?.ToString();
            msg["sender_info"] = senderId is null ? null : await GetUserByIdAsync(senderId);
        }
        return ordered;
    }

    public async Task<Dictionary<string, object?>?> UpdateConversationDisplayNameAsync(string conversationId, string userId, string displayName)
    {
        var conversation = await _repo.GetByIdAsync("conversations", conversationId);
        if (conversation is null) return null;

        if (!IsUserInConversation(conversation, userId))
        {
            return null;
        }

        var cleanName = (displayName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanName)) return null;
        if (cleanName.Length > 60) cleanName = cleanName[..60];

        var participantIds = GetParticipantIds(conversation);
        var isGroup = IsGroupConversation(conversation) || participantIds.Count > 2;

        if (isGroup)
        {
            await _repo.UpdateAsync("conversations", conversationId, new Dictionary<string, object?>
            {
                ["group_name"] = cleanName,
                ["conversation_type"] = "group",
                ["is_group"] = true
            });
        }
        else
        {
            var nicknames = ToObjectDictionary(conversation.GetValueOrDefault("nicknames"));
            nicknames[userId] = cleanName;
            await _repo.UpdateAsync("conversations", conversationId, new Dictionary<string, object?>
            {
                ["nicknames"] = nicknames
            });
        }

        var updated = await _repo.GetByIdAsync("conversations", conversationId);
        if (updated is null) return null;

        await HydrateConversationAsync(updated, userId);
        return updated;
    }

    public async Task<ChatConversationDeleteResult> DeleteConversationAsync(string conversationId, string userId)
    {
        var conversation = await _repo.GetByIdAsync("conversations", conversationId);
        if (conversation is null || !IsUserInConversation(conversation, userId))
        {
            return new ChatConversationDeleteResult(false, "not_found");
        }

        var deletedAttachments = await HardDeleteConversationAsync(conversationId);
        return new ChatConversationDeleteResult(true, "deleted", deletedAttachments);
    }

    public async Task<List<Dictionary<string, object?>>> GetAllUsersExceptAsync(string userId)
    {
        var users = await _repo.GetAllAsync("users", limit: 200);
        foreach (var user in users) ApplyComputedPresence(user);
        return users.Where(u => u.GetValueOrDefault("id")?.ToString() != userId)
            .Select(u => new Dictionary<string, object?>
            {
                ["email"] = u.GetValueOrDefault("email"),
                ["username"] = u.GetValueOrDefault("username"),
                ["name"] = u.GetValueOrDefault("name"),
                ["profilePic"] = u.GetValueOrDefault("profilePic"),
                ["role"] = u.GetValueOrDefault("role"),
                ["id"] = u.GetValueOrDefault("id"),
                ["is_online"] = u.GetValueOrDefault("is_online") ?? false,
                ["isOnline"] = u.GetValueOrDefault("isOnline") ?? false,
                ["presence_status"] = u.GetValueOrDefault("presence_status") ?? "offline",
                ["last_seen_at"] = u.GetValueOrDefault("last_seen_at"),
                ["lastSeenAt"] = u.GetValueOrDefault("lastSeenAt")
            }).ToList();
    }

    private async Task<List<Dictionary<string, object?>>> GetAllAdminUsersInternalAsync()
    {
        var users = await _repo.GetAllAsync("users", limit: 300);
        foreach (var user in users) ApplyComputedPresence(user);
        return users
            .Where(IsAdminUser)
            .Select(u => new Dictionary<string, object?>
            {
                ["email"] = u.GetValueOrDefault("email"),
                ["username"] = u.GetValueOrDefault("username"),
                ["name"] = u.GetValueOrDefault("name"),
                ["displayName"] = u.GetValueOrDefault("displayName"),
                ["profilePic"] = u.GetValueOrDefault("profilePic"),
                ["role"] = u.GetValueOrDefault("role"),
                ["id"] = u.GetValueOrDefault("id"),
                ["is_online"] = u.GetValueOrDefault("is_online") ?? false,
                ["isOnline"] = u.GetValueOrDefault("isOnline") ?? false,
                ["presence_status"] = u.GetValueOrDefault("presence_status") ?? "offline",
                ["last_seen_at"] = u.GetValueOrDefault("last_seen_at"),
                ["lastSeenAt"] = u.GetValueOrDefault("lastSeenAt"),
                ["last_login_at"] = u.GetValueOrDefault("last_login_at"),
                ["updated_at"] = u.GetValueOrDefault("updated_at")
            })
            .ToList();
    }

    private async Task<Dictionary<string, object?>?> GetPrimaryAdminUserInternalAsync()
    {
        var admins = await GetAllAdminUsersInternalAsync();

        // Admin mặc định cho nút hỗ trợ được chọn cố định, không phụ thuộc
        // trạng thái online hoặc lần đăng nhập gần nhất của các Admin khác.
        return admins.FirstOrDefault(user => string.Equals(
                   user.GetValueOrDefault("id")?.ToString(),
                   PrimaryAdminId,
                   StringComparison.OrdinalIgnoreCase))
            ?? admins.FirstOrDefault(user => string.Equals(
                user.GetValueOrDefault("email")?.ToString(),
                PrimaryAdminEmail,
                StringComparison.OrdinalIgnoreCase))
            ?? admins
                .OrderBy(user => user.GetValueOrDefault("id")?.ToString() ?? string.Empty, StringComparer.Ordinal)
                .FirstOrDefault();
    }

    public async Task<List<Dictionary<string, object?>>> GetAdminUsersAsync(string currentUserId)
    {
        return (await GetAllAdminUsersInternalAsync())
            .Where(u => u.GetValueOrDefault("id")?.ToString() != currentUserId)
            .ToList();
    }

    public async Task<Dictionary<string, object?>?> GetUserByIdAsync(string userId)
    {
        var user = await _repo.GetByIdAsync("users", userId);
        ApplyComputedPresence(user);
        return user;
    }

    public async Task<Dictionary<string, object?>?> GetUserByEmailAsync(string email)
    {
        var users = await _repo.WhereEqualAsync("users", "email", email.ToLowerInvariant(), 1);
        var user = users.FirstOrDefault();
        ApplyComputedPresence(user);
        return user;
    }

    public Task<bool> CreateOrUpdateUserAsync(string userId, Dictionary<string, object?> userData)
    {
        if (userData.TryGetValue("email", out var email) && email is string e) userData["email"] = e.ToLowerInvariant();
        userData["updated_at"] = DateTime.UtcNow;
        return _repo.SetAsync("users", userId, userData, merge: true);
    }

    private async Task<NormalizedChatMessage> NormalizeMessageContentAsync(string conversationId, string senderId, string content)
    {
        var raw = (content ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return NormalizedChatMessage.Fail("CHAT_EMPTY", "Tin nhắn không được để trống.");
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var typeElement)
                && string.Equals(typeElement.GetString(), ChatMessagePayloadType, StringComparison.Ordinal))
            {
                var text = root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                    ? (textElement.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (text.Length > MaxMessageTextLength)
                {
                    return NormalizedChatMessage.Fail("CHAT_MESSAGE_TOO_LONG", $"Tin nhắn tối đa {MaxMessageTextLength} ký tự.");
                }

                var sourceAttachments = new List<JsonElement>();
                if (root.TryGetProperty("attachments", out var attachmentsElement) && attachmentsElement.ValueKind == JsonValueKind.Array)
                {
                    sourceAttachments.AddRange(attachmentsElement.EnumerateArray());
                }
                else if (root.TryGetProperty("attachment", out var attachmentElement) && attachmentElement.ValueKind == JsonValueKind.Object)
                {
                    sourceAttachments.Add(attachmentElement);
                }

                if (sourceAttachments.Count > MaxMessageAttachments)
                {
                    return NormalizedChatMessage.Fail("CHAT_TOO_MANY_ATTACHMENTS", $"Mỗi tin nhắn tối đa {MaxMessageAttachments} tệp đính kèm.");
                }

                var attachments = new List<Dictionary<string, object?>>();
                foreach (var item in sourceAttachments)
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var url = ReadJsonString(item, "url").Trim();
                    if (url.Length == 0) continue;

                    var isOwned = await _fileStorage.IsStoredFileOwnedByUserInFolderAsync(
                        url,
                        senderId,
                        $"chat/{conversationId}");
                    if (!isOwned)
                    {
                        return NormalizedChatMessage.Fail(
                            "CHAT_ATTACHMENT_FORBIDDEN",
                            "Tệp đính kèm không thuộc người gửi hoặc không thuộc hội thoại này.");
                    }

                    var name = Path.GetFileName(ReadJsonString(item, "name", "fileName", "filename"));
                    if (string.IsNullOrWhiteSpace(name)) name = "Tệp đính kèm";
                    if (name.Length > 255) name = name[..255];
                    var contentType = ReadJsonString(item, "contentType", "content_type", "mimeType");
                    if (string.IsNullOrWhiteSpace(contentType)) contentType = "application/octet-stream";
                    if (contentType.Length > 100) contentType = contentType[..100];
                    var size = ReadJsonLong(item, "size");

                    attachments.Add(new Dictionary<string, object?>
                    {
                        ["url"] = url,
                        ["name"] = name,
                        ["contentType"] = contentType,
                        ["size"] = Math.Max(0, size),
                        ["type"] = contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                            ? "video"
                            : contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image" : "file"
                    });
                }

                if (text.Length == 0 && attachments.Count == 0)
                {
                    return NormalizedChatMessage.Fail("CHAT_EMPTY", "Tin nhắn không được để trống.");
                }

                if (attachments.Count == 0) return NormalizedChatMessage.Ok(text);

                var payload = new Dictionary<string, object?>
                {
                    ["type"] = ChatMessagePayloadType,
                    ["version"] = 2,
                    ["text"] = text,
                    ["attachments"] = attachments,
                    ["attachment"] = attachments[0]
                };
                return NormalizedChatMessage.Ok(JsonSerializer.Serialize(payload));
            }
        }
        catch (JsonException)
        {

        }

        if (raw.Length > MaxMessageTextLength)
        {
            return NormalizedChatMessage.Fail("CHAT_MESSAGE_TOO_LONG", $"Tin nhắn tối đa {MaxMessageTextLength} ký tự.");
        }
        return NormalizedChatMessage.Ok(raw);
    }

    private async Task<int> HardDeleteConversationAsync(string conversationId)
    {
        var deletedAttachments = await _fileStorage.DeleteStoredFilesInFolderAsync($"chat/{conversationId}");
        await _repo.DeleteWhereEqualAsync("messages", "conversation_id", conversationId);
        await _repo.DeleteAsync("conversations", conversationId);
        return deletedAttachments;
    }

    private async Task UnhideConversationForUserAsync(string conversationId, string userId)
    {
        var conversation = await _repo.GetByIdAsync("conversations", conversationId);
        if (conversation is null) return;
        var hiddenForUserIds = ToStringList(conversation.GetValueOrDefault("hidden_for_user_ids"));
        var removed = hiddenForUserIds.RemoveAll(id => string.Equals(id, userId, StringComparison.Ordinal)) > 0;
        if (!removed) return;
        await _repo.UpdateAsync("conversations", conversationId, new Dictionary<string, object?>
        {
            ["hidden_for_user_ids"] = hiddenForUserIds,
            ["updated_at"] = DateTime.UtcNow
        });
    }

    private static bool IsConversationHiddenForUser(Dictionary<string, object?> conversation, string userId)
    {
        return ToStringList(conversation.GetValueOrDefault("hidden_for_user_ids"))
            .Contains(userId, StringComparer.Ordinal);
    }

    private static string ReadJsonString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static long ReadJsonLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : 0;
    }

    private sealed record NormalizedChatMessage(bool Success, string Content, string? ErrorCode, string? ErrorMessage)
    {
        public static NormalizedChatMessage Ok(string content) => new(true, content, null, null);
        public static NormalizedChatMessage Fail(string code, string message) => new(false, string.Empty, code, message);
    }

    private async Task HydrateConversationAsync(Dictionary<string, object?> conv, string currentUserId)
    {
        var participantIds = GetParticipantIds(conv);
        var isGroup = IsGroupConversation(conv) || participantIds.Count > 2;

        if (participantIds.Count == 0)
        {
            var createdUser = conv.GetValueOrDefault("created_user")?.ToString();
            var otherUser = conv.GetValueOrDefault("other_user")?.ToString();
            participantIds = new[] { createdUser, otherUser }
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var participants = await BuildParticipantsAsync(participantIds);
        conv["participants"] = participants;
        conv["participant_ids"] = participantIds;
        conv["member_count"] = participants.Count;
        conv["is_group"] = isGroup;
        conv["conversation_type"] = isGroup ? "group" : "direct";

        if (isGroup)
        {
            var groupName = conv.GetValueOrDefault("group_name")?.ToString();
            if (string.IsNullOrWhiteSpace(groupName))
            {
                groupName = BuildGroupName(participants, currentUserId);
                conv["group_name"] = groupName;
            }

            conv["other_user_info"] = new Dictionary<string, object?>
            {
                ["id"] = conv.GetValueOrDefault("id"),
                ["username"] = groupName,
                ["name"] = groupName,
                ["email"] = $"{participants.Count} thành viên",
                ["profilePic"] = null
            };
            return;
        }

        var otherUserId = conv.GetValueOrDefault("created_user")?.ToString() == currentUserId
            ? conv.GetValueOrDefault("other_user")?.ToString()
            : conv.GetValueOrDefault("created_user")?.ToString();

        if (string.IsNullOrWhiteSpace(otherUserId))
        {
            otherUserId = participantIds.FirstOrDefault(id => !string.Equals(id, currentUserId, StringComparison.Ordinal));
        }

        conv["other_user_info"] = otherUserId is null ? null : await GetUserByIdAsync(otherUserId);
    }

    private async Task<List<Dictionary<string, object?>>> BuildParticipantsAsync(IEnumerable<string> participantIds)
    {
        var participants = new List<Dictionary<string, object?>>();
        foreach (var participantId in participantIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var user = await GetUserByIdAsync(participantId);
            participants.Add(user ?? new Dictionary<string, object?> { ["id"] = participantId, ["username"] = "Người dùng" });
        }

        return participants;
    }

    private static string BuildSupportAdminGroupName(Dictionary<string, object?>? user)
    {
        var name = user?.GetValueOrDefault("username")?.ToString()
            ?? user?.GetValueOrDefault("displayName")?.ToString()
            ?? user?.GetValueOrDefault("name")?.ToString()
            ?? user?.GetValueOrDefault("email")?.ToString()?.Split('@').FirstOrDefault()
            ?? "Người dùng";

        return $"Nhắn tin Admin chính - {name}";
    }

    private static bool IsAdminUser(Dictionary<string, object?>? user)
    {
        return string.Equals(user?.GetValueOrDefault("role")?.ToString(), "Admin", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyComputedPresence(Dictionary<string, object?>? user)
    {
        if (user is null) return;

        var storedOnline = IsTruthy(user.GetValueOrDefault("is_online"))
            || IsTruthy(user.GetValueOrDefault("isOnline"));
        var lastSeen = ReadPresenceDate(
            user.GetValueOrDefault("last_seen_at")
            ?? user.GetValueOrDefault("lastSeenAt"));
        var isOnline = storedOnline
            && lastSeen.HasValue
            && lastSeen.Value >= DateTime.UtcNow.AddMinutes(-3);

        user["is_online"] = isOnline;
        user["isOnline"] = isOnline;
        user["presence_status"] = isOnline ? "online" : "offline";
        if (lastSeen.HasValue)
        {
            var iso = lastSeen.Value.ToString("O");
            user["last_seen_at"] = iso;
            user["lastSeenAt"] = iso;
        }
    }

    private static DateTime? ReadPresenceDate(object? value)
    {
        if (value is null) return null;
        if (value is DateTime dateTime)
        {
            return dateTime.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                : dateTime.ToUniversalTime();
        }
        if (value is DateTimeOffset offset) return offset.UtcDateTime;
        return DateTimeOffset.TryParse(value.ToString(), out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            bool typed => typed,
            string text => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string BuildGroupName(List<Dictionary<string, object?>> participants, string currentUserId)
    {
        var names = participants
            .Where(user => !string.Equals(user.GetValueOrDefault("id")?.ToString(), currentUserId, StringComparison.Ordinal))
            .Select(user => user.GetValueOrDefault("username")?.ToString()
                ?? user.GetValueOrDefault("name")?.ToString()
                ?? user.GetValueOrDefault("email")?.ToString()?.Split('@').FirstOrDefault()
                ?? "Người dùng")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(3)
            .ToList();

        return names.Count == 0 ? "Nhóm trò chuyện" : "Nhóm " + string.Join(", ", names);
    }

    private async Task<string?> FindDirectConversationAsync(string currentUserId, string otherUserId)
    {
        var conversations = await _repo.WhereArrayContainsAsync("conversations", "participant_ids", currentUserId, limit: 100);

        if (conversations.Count == 0)
        {
            conversations = (await _repo.WhereEqualAsync("conversations", "created_user", currentUserId, limit: 50))
                .Concat(await _repo.WhereEqualAsync("conversations", "other_user", currentUserId, limit: 50))
                .GroupBy(c => c.GetValueOrDefault("id")?.ToString())
                .Select(g => g.First())
                .ToList();
        }

        foreach (var conversation in conversations)
        {
            if (IsGroupConversation(conversation)) continue;

            var createdUser = conversation.GetValueOrDefault("created_user")?.ToString();
            var otherUser = conversation.GetValueOrDefault("other_user")?.ToString();
            if ((string.Equals(createdUser, currentUserId, StringComparison.Ordinal) && string.Equals(otherUser, otherUserId, StringComparison.Ordinal)) ||
                (string.Equals(createdUser, otherUserId, StringComparison.Ordinal) && string.Equals(otherUser, currentUserId, StringComparison.Ordinal)))
            {
                return conversation.GetValueOrDefault("id")?.ToString();
            }

            var participantIds = GetParticipantIds(conversation);
            if (participantIds.Count == 2 && participantIds.Contains(currentUserId) && participantIds.Contains(otherUserId))
            {
                return conversation.GetValueOrDefault("id")?.ToString();
            }
        }

        return null;
    }

    private static bool IsUserInConversation(Dictionary<string, object?> conversation, string userId)
    {
        var participantIds = GetParticipantIds(conversation);
        if (participantIds.Count > 0) return participantIds.Contains(userId, StringComparer.Ordinal);

        var createdUser = conversation.GetValueOrDefault("created_user")?.ToString();
        var otherUser = conversation.GetValueOrDefault("other_user")?.ToString();
        return string.Equals(createdUser, userId, StringComparison.Ordinal) ||
               string.Equals(otherUser, userId, StringComparison.Ordinal);
    }

    private static bool IsGroupConversation(Dictionary<string, object?> conversation)
    {
        var type = conversation.GetValueOrDefault("conversation_type")?.ToString();
        if (string.Equals(type, "group", StringComparison.OrdinalIgnoreCase)) return true;

        var isGroup = conversation.GetValueOrDefault("is_group");
        return isGroup switch
        {
            bool value => value,
            string text => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static List<string> GetParticipantIds(Dictionary<string, object?> conversation)
    {
        var participantValue = conversation.GetValueOrDefault("participant_ids");
        var ids = ToStringList(participantValue)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return ids;
    }

    private static Dictionary<string, object?> ToObjectDictionary(object? value)
    {
        if (value is null) return new Dictionary<string, object?>();

        if (value is Dictionary<string, object?> typedDictionary)
        {
            return typedDictionary
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        if (value is System.Collections.IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>();
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    result[key] = entry.Value;
                }
            }
            return result;
        }

        return new Dictionary<string, object?>();
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
                result.Add(item?.ToString() ?? string.Empty);
            }
            return result;
        }

        return new List<string>();
    }
}
