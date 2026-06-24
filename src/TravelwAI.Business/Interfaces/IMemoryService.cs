namespace TravelwAI.Business.Interfaces;

public interface IMemoryService
{
    Task<string?> CreateMemoryAsync(string userId, string memoryName, string description, string province, string createdAt, List<string> sharedEmails, string? photoUrl, List<string>? photoUrls = null);
    Task<List<Dictionary<string, object?>>> GetUserMemoriesAsync(string userId);
    Task<Dictionary<string, object?>?> GetMemoryByIdAsync(string memoryId, string userId);
    Task<bool> DeleteMemoryAsync(string memoryId, string userId);
    Task<List<Dictionary<string, object?>>> GetMemoriesByProvinceAsync(string userId, string provinceName);
}
