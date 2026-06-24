namespace TravelwAI.Business.Interfaces;

public interface IFriendService
{
    Task<Dictionary<string, object?>> CreateFriendRequestAsync(string requesterId, string recipientEmail);
    Task<(List<Dictionary<string, object?>> friends, List<Dictionary<string, object?>> pending)> GetFriendsAsync(string userId);
    Task<Dictionary<string, object?>> UpdateFriendStatusAsync(string requestEmail, string recipientId, string action);
    Task<Dictionary<string, object?>> RemoveFriendAsync(string userId, string friendUserId);
}
