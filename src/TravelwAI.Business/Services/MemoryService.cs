using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Business.Services;

public sealed class MemoryService : IMemoryService
{
    private readonly IDataRepository _repo;
    private readonly IChatService _chatService;

    public MemoryService(IDataRepository repo, IChatService chatService)
    {
        _repo = repo;
        _chatService = chatService;
    }

    public async Task<string?> CreateMemoryAsync(string userId, string memoryName, string description, string province, string createdAt, List<string> sharedEmails, string? photoUrl, List<string>? photoUrls = null)
    {
        var cleanSharedEmails = NormalizeSharedEmails(sharedEmails);
        var cleanPhotoUrls = (photoUrls ?? new List<string>())
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(photoUrl) && cleanPhotoUrls.Count == 0) cleanPhotoUrls.Add(photoUrl);
        var firstPhotoUrl = cleanPhotoUrls.FirstOrDefault();

        if (cleanSharedEmails.Count > 0)
        {
            var sharedUsers = new List<Dictionary<string, object?>>();
            foreach (var email in cleanSharedEmails)
            {
                var user = await _chatService.GetUserByEmailAsync(email);
                if (user is not null) sharedUsers.Add(user);
            }

            return await _repo.AddAsync("shared_memories", new Dictionary<string, object?>
            {
                ["created_by_user_id"] = userId,
                ["memory_name"] = memoryName,
                ["description"] = description,
                ["province"] = province,
                ["shared_users"] = sharedUsers,
                ["photo_url"] = firstPhotoUrl,
                ["photo_urls"] = cleanPhotoUrls,
                ["created_at"] = DateTime.UtcNow,
                ["memory_collection"] = "shared_memories"
            });
        }

        return await _repo.AddAsync("memories", new Dictionary<string, object?>
        {
            ["created_by_user_id"] = userId,
            ["memory_name"] = memoryName,
            ["description"] = description,
            ["province"] = province,
            ["photo_url"] = firstPhotoUrl,
            ["photo_urls"] = cleanPhotoUrls,
            ["created_at"] = DateTime.UtcNow,
            ["memory_collection"] = "memories"
        });
    }

    public async Task<List<Dictionary<string, object?>>> GetUserMemoriesAsync(string userId)
    {
        var personalMemories = await GetOwnedMemoriesFromCollectionAsync("memories", userId);
        var sharedMemories = await GetOwnedMemoriesFromCollectionAsync("shared_memories", userId);

        return personalMemories
            .Concat(sharedMemories)
            .OrderByDescending(GetMemoryCreatedDate)
            .ToList();
    }

    public async Task<Dictionary<string, object?>?> GetMemoryByIdAsync(string memoryId, string userId)
    {
        foreach (var collection in MemoryCollections)
        {
            var documentId = await ResolveOwnedMemoryDocumentIdAsync(collection, memoryId, userId);
            if (documentId is null) continue;

            var memory = await _repo.GetByIdAsync(collection, documentId);
            if (!IsOwnedByUser(memory, userId)) continue;

            memory!["memory_collection"] = collection;
            memory.Remove("created_by_user_id");
            return memory;
        }

        return null;
    }

    public async Task<bool> DeleteMemoryAsync(string memoryId, string userId)
    {
        foreach (var collection in MemoryCollections)
        {
            var documentId = await ResolveOwnedMemoryDocumentIdAsync(collection, memoryId, userId);
            if (documentId is not null && await _repo.DeleteAsync(collection, documentId)) return true;
        }

        return false;
    }

    private async Task<List<Dictionary<string, object?>>> GetOwnedMemoriesFromCollectionAsync(string collection, string userId)
    {
        var list = await _repo.WhereEqualAsync(collection, "created_by_user_id", userId);
        foreach (var item in list)
        {
            item["memory_collection"] = collection;
            item.Remove("created_by_user_id");
        }
        return list;
    }

    private async Task<string?> ResolveOwnedMemoryDocumentIdAsync(string collection, string memoryId, string userId)
    {
        if (string.IsNullOrWhiteSpace(memoryId)) return null;

        var normalizedMemoryId = memoryId.Trim();

        var direct = await _repo.GetByIdAsync(collection, normalizedMemoryId);
        if (IsOwnedByUser(direct, userId)) return normalizedMemoryId;

        foreach (var field in new[] { "id", "memory_id", "memoryId", "Id", "document_id", "documentId" })
        {
            var matches = await _repo.WhereEqualAsync(collection, field, normalizedMemoryId, limit: 10);
            var owned = matches.FirstOrDefault(row => IsOwnedByUser(row, userId));
            var id = owned?.GetValueOrDefault("id")?.ToString();
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }

        return null;
    }

    private static bool IsOwnedByUser(Dictionary<string, object?>? memory, string userId)
    {
        return memory is not null && memory.GetValueOrDefault("created_by_user_id")?.ToString() == userId;
    }

    public async Task<List<Dictionary<string, object?>>> GetMemoriesByProvinceAsync(string userId, string provinceName)
    {
        var list = await GetUserMemoriesAsync(userId);
        return list.Where(x => string.Equals(x.GetValueOrDefault("province")?.ToString(), provinceName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static List<string> NormalizeSharedEmails(List<string>? sharedEmails)
    {
        if (sharedEmails is null || sharedEmails.Count == 0) return new List<string>();

        return sharedEmails
            .SelectMany(email => (email ?? string.Empty)
                .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(email => email.Trim().ToLowerInvariant())
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DateTime GetMemoryCreatedDate(Dictionary<string, object?> memory)
    {
        var raw = memory.GetValueOrDefault("created_at") ?? memory.GetValueOrDefault("createdAt");
        return DateTime.TryParse(raw?.ToString(), out var date) ? date : DateTime.MinValue;
    }

    private static readonly string[] MemoryCollections = { "memories", "shared_memories" };
}
