using Microsoft.AspNetCore.Http;

namespace TravelwAI.Business.Interfaces;

public interface IFileStorageService
{
    Task<string?> SaveImageAsync(IFormFile file, string userId, string folderName);
    Task<string?> SaveFileAsync(IFormFile file, string userId, string folderName);
}
