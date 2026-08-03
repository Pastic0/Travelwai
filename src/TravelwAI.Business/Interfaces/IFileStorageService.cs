using Microsoft.AspNetCore.Http;
using TravelwAI.Models.Storage;

namespace TravelwAI.Business.Interfaces;

public interface IFileStorageService
{
    Task<string?> SaveImageAsync(IFormFile file, string userId, string folderName);
    Task<string?> SaveImageToSupabaseAsync(IFormFile file, string userId, string folderName);
    Task<string?> SaveFileAsync(IFormFile file, string userId, string folderName);
    Task<bool> DeleteStoredFileByUrlAsync(string publicUrl);
    Task<bool> IsStoredFileOwnedByUserInFolderAsync(string publicUrl, string userId, string folderName);
    Task<int> DeleteStoredFilesInFolderAsync(string folderName);
    Task<UserImageStorageUsage> GetUserImageStorageUsageAsync(string userId);
    Task<UserImageStorageDetails> GetUserImageStorageDetailsAsync(string userId);
    Task<IReadOnlyList<UserImageStorageAccountUsage>> GetUsersImageStorageUsageAsync(IEnumerable<string> userIds);
    Task<UserImageStorageUsage> GetTotalImageStorageUsageAsync();
    Task<UserImageStorageUsage> SetTotalImageStorageLimitAsync(long limitBytes, string updatedBy);
    Task<UserImageDeleteResult> DeleteAllUserImagesAsync(string userId);
    Task<UserImageDeleteResult> DeleteUserMessageImagesAsync(string userId);
    Task<UserImageDeleteResult> DeleteUserImageAsync(string userId, string uploadId);
}
