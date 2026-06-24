using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Business.Services;

public sealed class ChatService : IChatService
{
    private readonly IDataRepository _repo;

    public ChatService(IDataRepository repo)
    {
        _repo = repo;
    }

    public async Task<string?> CreateConversationAsync(string currentUserId, string otherUserId)
    {
        if (string.IsNullOrWhiteSpace(currentUserId) || string.IsNullOrWhiteSpace(otherUserId)) return null;
        if (string.Equals(currentUserId, otherUserId, StringComparison.Ordinal)) return null;

        var existing = await FindDirectConversationAsync(currentUserId, otherUserId);
        if (existing is not null) return existing;

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

        foreach (var conv in all)
        {
            await HydrateConversationAsync(conv, userId);
        }

        return all.OrderByDescending(x => x.GetValueOrDefault("last_message_time")?.ToString() ?? string.Empty).ToList();
    }

    public async Task<string?> SendMessageAsync(string conversationId, string senderId, string content)
    {
        var conversation = await _repo.GetByIdAsync("conversations", conversationId);
        if (conversation is null || !IsUserInConversation(conversation, senderId)) return null;

        var id = await _repo.AddAsync("messages", new Dictionary<string, object?>
        {
            ["sender_id"] = senderId,
            ["conversation_id"] = conversationId,
            ["content"] = content,
            ["time_sent"] = DateTime.UtcNow
        });

        await _repo.UpdateAsync("conversations", conversationId, new Dictionary<string, object?>
        {
            ["last_message"] = content,
            ["last_message_time"] = DateTime.UtcNow,
            ["last_sender_id"] = senderId
        });

        return id;
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

    public async Task<bool> DeleteConversationAsync(string conversationId, string userId)
    {
        var conversation = await _repo.GetByIdAsync("conversations", conversationId);
        if (conversation is null) return false;

        if (!IsUserInConversation(conversation, userId))
        {
            return false;
        }

        await _repo.DeleteWhereEqualAsync("messages", "conversation_id", conversationId);
        await _repo.DeleteAsync("conversations", conversationId);
        return true;
    }

    public async Task<List<Dictionary<string, object?>>> GetAllUsersExceptAsync(string userId)
    {
        var users = await _repo.GetAllAsync("users", limit: 200);
        return users.Where(u => u.GetValueOrDefault("id")?.ToString() != userId)
            .Select(u => new Dictionary<string, object?>
            {
                ["email"] = u.GetValueOrDefault("email"),
                ["username"] = u.GetValueOrDefault("username"),
                ["name"] = u.GetValueOrDefault("name"),
                ["profilePic"] = u.GetValueOrDefault("profilePic"),
                ["role"] = u.GetValueOrDefault("role"),
                ["id"] = u.GetValueOrDefault("id")
            }).ToList();
    }

    public async Task<List<Dictionary<string, object?>>> GetAdminUsersAsync(string currentUserId)
    {
        var users = await _repo.GetAllAsync("users", limit: 300);
        return users
            .Where(u => u.GetValueOrDefault("id")?.ToString() != currentUserId)
            .Where(u => string.Equals(u.GetValueOrDefault("role")?.ToString(), "Admin", StringComparison.OrdinalIgnoreCase))
            .Select(u => new Dictionary<string, object?>
            {
                ["email"] = u.GetValueOrDefault("email"),
                ["username"] = u.GetValueOrDefault("username"),
                ["name"] = u.GetValueOrDefault("name"),
                ["displayName"] = u.GetValueOrDefault("displayName"),
                ["profilePic"] = u.GetValueOrDefault("profilePic"),
                ["role"] = u.GetValueOrDefault("role"),
                ["id"] = u.GetValueOrDefault("id")
            })
            .ToList();
    }

    public Task<Dictionary<string, object?>?> GetUserByIdAsync(string userId) => _repo.GetByIdAsync("users", userId);

    public async Task<Dictionary<string, object?>?> GetUserByEmailAsync(string email)
    {
        var users = await _repo.WhereEqualAsync("users", "email", email.ToLowerInvariant(), 1);
        return users.FirstOrDefault();
    }

    public Task<bool> CreateOrUpdateUserAsync(string userId, Dictionary<string, object?> userData)
    {
        if (userData.TryGetValue("email", out var email) && email is string e) userData["email"] = e.ToLowerInvariant();
        userData["updated_at"] = DateTime.UtcNow;
        return _repo.SetAsync("users", userId, userData, merge: true);
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
        if (participantIds.Count > 0 && participantIds.Contains(userId)) return true;

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
