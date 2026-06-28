namespace TravelwAI.Business.Interfaces;

public interface IChatService
{
    Task<string?> CreateConversationAsync(string currentUserId, string otherUserId);
    Task<string?> CreateGroupConversationAsync(string currentUserId, IEnumerable<string> participantIds, string? groupName = null);
    Task<string?> CreateOrGetSupportAdminConversationAsync(string currentUserId);
    Task<int> DeleteSupportAdminConversationsForUserAsync(string userId);
    Task<List<Dictionary<string, object?>>> GetConversationsAsync(string userId);
    Task<string?> SendMessageAsync(string conversationId, string senderId, string content);
    Task<List<Dictionary<string, object?>>> GetMessagesAsync(string conversationId, int limit, int offset);
    Task<Dictionary<string, object?>?> UpdateConversationDisplayNameAsync(string conversationId, string userId, string displayName);
    Task<bool> DeleteConversationAsync(string conversationId, string userId);
    Task<List<Dictionary<string, object?>>> GetAllUsersExceptAsync(string userId);
    Task<List<Dictionary<string, object?>>> GetAdminUsersAsync(string currentUserId);
    Task<Dictionary<string, object?>?> GetUserByIdAsync(string userId);
    Task<Dictionary<string, object?>?> GetUserByEmailAsync(string email);
    Task<bool> CreateOrUpdateUserAsync(string userId, Dictionary<string, object?> userData);
}
