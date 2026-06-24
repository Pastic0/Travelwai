using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Business.Services;

public sealed class FriendService : IFriendService
{
    private readonly IDataRepository _repo;
    private readonly IChatService _chatService;

    public FriendService(IDataRepository repo, IChatService chatService)
    {
        _repo = repo;
        _chatService = chatService;
    }

    public async Task<Dictionary<string, object?>> CreateFriendRequestAsync(string requesterId, string recipientEmail)
    {
        var recipient = await _chatService.GetUserByEmailAsync(recipientEmail);
        if (recipient is null || recipient.GetValueOrDefault("id") is not string recipientId)
            return new() { ["success"] = false, ["message"] = "Không tìm thấy người dùng" };
        if (recipientId == requesterId)
            return new() { ["success"] = false, ["message"] = "Bạn không thể gửi yêu cầu kết bạn cho chính mình." };

        var between = await GetRelationsBetweenAsync(requesterId, recipientId);
        var duplicate = between.Any(f =>
            IsSamePair(f, requesterId, recipientId) &&
            (f.GetValueOrDefault("status")?.ToString() == "pending" || f.GetValueOrDefault("status")?.ToString() == "friends"));

        if (duplicate) return new() { ["success"] = false, ["message"] = "Yêu cầu kết bạn đã tồn tại hoặc hai người đã là bạn bè." };

        var id = await _repo.AddAsync("friends", new Dictionary<string, object?>
        {
            ["user1_id"] = requesterId,
            ["user2_id"] = recipientId,
            ["status"] = "pending",
            ["created_at"] = DateTime.UtcNow
        });

        return new() { ["success"] = true, ["request_id"] = id, ["message"] = "Yêu cầu kết bạn đã được gửi thành công." };
    }

    public async Task<(List<Dictionary<string, object?>> friends, List<Dictionary<string, object?>> pending)> GetFriendsAsync(string userId)
    {
        var relations = await GetRelationsForUserAsync(userId);
        var friendList = new List<Dictionary<string, object?>>();
        var pendingList = new List<Dictionary<string, object?>>();

        foreach (var rel in relations)
        {
            var u1 = rel.GetValueOrDefault("user1_id")?.ToString();
            var u2 = rel.GetValueOrDefault("user2_id")?.ToString();
            if (u1 != userId && u2 != userId) continue;
            var otherId = u1 == userId ? u2 : u1;
            if (otherId is null || otherId == userId) continue;
            var user = await _chatService.GetUserByIdAsync(otherId);
            if (user is null) continue;
            var status = rel.GetValueOrDefault("status")?.ToString();
            var item = new Dictionary<string, object?>
            {
                ["id"] = user.GetValueOrDefault("id"),
                ["email"] = user.GetValueOrDefault("email"),
                ["username"] = user.GetValueOrDefault("username"),
                ["name"] = user.GetValueOrDefault("name") ?? user.GetValueOrDefault("username"),
                ["profilePic"] = user.GetValueOrDefault("profilePic"),
                ["status"] = status,
                ["direction"] = u1 == userId ? "outgoing" : "incoming"
            };

            if (status == "pending")
            {

                if (u2 == userId) pendingList.Add(item);
            }
            else if (status == "friends")
            {
                friendList.Add(item);
            }
        }
        return (friendList, pendingList);
    }

    public async Task<Dictionary<string, object?>> UpdateFriendStatusAsync(string requestEmail, string recipientId, string action)
    {
        var requester = await _chatService.GetUserByEmailAsync(requestEmail);
        if (requester?.GetValueOrDefault("id") is not string requesterId)
            return new() { ["success"] = false, ["message"] = "Không tìm thấy người gửi lời mời." };

        var request = (await _repo.WhereEqualAsync("friends", "user1_id", requesterId, limit: 50))
            .FirstOrDefault(f =>
                f.GetValueOrDefault("user2_id")?.ToString() == recipientId &&
                f.GetValueOrDefault("status")?.ToString() == "pending");

        if (request?.GetValueOrDefault("id") is not string requestId)
            return new() { ["success"] = false, ["message"] = "Không tìm thấy yêu cầu kết bạn phù hợp." };

        if (action == "accepted")
        {
            var ids = new[] { requesterId, recipientId }.OrderBy(x => x).ToArray();
            var friendshipId = $"{ids[0]}_{ids[1]}";
            await _repo.SetAsync("friends", friendshipId, new Dictionary<string, object?>
            {
                ["user1_id"] = ids[0],
                ["user2_id"] = ids[1],
                ["created_at"] = DateTime.UtcNow,
                ["status"] = "friends"
            });
            await _repo.DeleteAsync("friends", requestId);
            return new() { ["success"] = true, ["message"] = "Yêu cầu kết bạn đã được chấp nhận." };
        }

        await _repo.DeleteAsync("friends", requestId);
        return new() { ["success"] = true, ["message"] = "Yêu cầu kết bạn đã được từ chối." };
    }

    public async Task<Dictionary<string, object?>> RemoveFriendAsync(string userId, string friendUserId)
    {
        if (string.IsNullOrWhiteSpace(friendUserId) || friendUserId == userId)
            return new() { ["success"] = false, ["message"] = "Không xác định được bạn bè cần xóa." };

        var friendships = (await GetRelationsBetweenAsync(userId, friendUserId))
            .Where(f => f.GetValueOrDefault("status")?.ToString() == "friends" && IsSamePair(f, userId, friendUserId))
            .ToList();

        if (friendships.Count == 0)
            return new() { ["success"] = false, ["message"] = "Không tìm thấy quan hệ bạn bè phù hợp." };

        foreach (var friendship in friendships)
        {
            if (friendship.GetValueOrDefault("id") is string friendshipId && !string.IsNullOrWhiteSpace(friendshipId))
            {
                await _repo.DeleteAsync("friends", friendshipId);
            }
        }

        return new() { ["success"] = true, ["message"] = "Đã xóa khỏi danh sách bạn bè." };
    }

    private async Task<List<Dictionary<string, object?>>> GetRelationsForUserAsync(string userId)
    {
        var asUser1 = await _repo.WhereEqualAsync("friends", "user1_id", userId, limit: 200);
        var asUser2 = await _repo.WhereEqualAsync("friends", "user2_id", userId, limit: 200);
        return asUser1
            .Concat(asUser2)
            .GroupBy(x => x.GetValueOrDefault("id")?.ToString())
            .Select(g => g.First())
            .ToList();
    }

    private async Task<List<Dictionary<string, object?>>> GetRelationsBetweenAsync(string userA, string userB)
    {
        var firstSide = await _repo.WhereEqualAsync("friends", "user1_id", userA, limit: 100);
        var secondSide = await _repo.WhereEqualAsync("friends", "user1_id", userB, limit: 100);
        return firstSide
            .Concat(secondSide)
            .Where(f => IsSamePair(f, userA, userB))
            .GroupBy(x => x.GetValueOrDefault("id")?.ToString())
            .Select(g => g.First())
            .ToList();
    }

    private static bool IsSamePair(Dictionary<string, object?> relation, string userA, string userB)
    {
        var u1 = relation.GetValueOrDefault("user1_id")?.ToString();
        var u2 = relation.GetValueOrDefault("user2_id")?.ToString();
        return (u1 == userA && u2 == userB) || (u1 == userB && u2 == userA);
    }
}
