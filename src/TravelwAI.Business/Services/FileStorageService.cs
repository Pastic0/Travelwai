using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Options;

namespace TravelwAI.Business.Services;

public sealed class FileStorageService : IFileStorageService
{
    private readonly IWebHostEnvironment _env;
    private readonly SupabaseOptions _supabaseOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FileStorageService> _logger;
    private const long MaxAttachmentBytes = 10 * 1024 * 1024;
    private const string UploadRootFolder = "uploads";

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webp"
    };

    private static readonly HashSet<string> AllowedAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
        ".mp4", ".webm", ".mov",
        ".mp3", ".wav", ".ogg", ".m4a",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".txt", ".csv", ".zip"
    };

    public FileStorageService(
        IWebHostEnvironment env,
        IOptions<SupabaseOptions> supabaseOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<FileStorageService> logger)
    {
        _env = env;
        _supabaseOptions = supabaseOptions.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> SaveImageAsync(IFormFile file, string userId, string folderName)
    {
        if (file.Length == 0 || file.Length > MaxAttachmentBytes) return null;

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) return null;

        var safeUserId = SanitizeFileName(userId);
        var safeFolder = NormalizeFolder(folderName);
        var fileName = $"{safeUserId}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}{ext}";

        return await SaveToBestStorageAsync(file, safeUserId, safeFolder, fileName);
    }

    public async Task<string?> SaveFileAsync(IFormFile file, string userId, string folderName)
    {
        if (file.Length == 0 || file.Length > MaxAttachmentBytes) return null;

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedAttachmentExtensions.Contains(ext)) return null;

        var safeUserId = SanitizeFileName(userId);
        var safeFolder = NormalizeFolder(folderName);
        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(file.FileName));
        var fileName = $"{safeUserId}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}_{baseName}{ext}";

        return await SaveToBestStorageAsync(file, safeUserId, safeFolder, fileName);
    }

    private async Task<string?> SaveToBestStorageAsync(IFormFile file, string safeUserId, string safeFolder, string fileName)
    {
        if (CanUseSupabaseStorage())
        {
            try
            {
                var supabaseUrl = await SaveToSupabaseStorageAsync(file, safeUserId, safeFolder, fileName);
                if (!string.IsNullOrWhiteSpace(supabaseUrl)) return supabaseUrl;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể tải tệp lên Supabase Storage. Sẽ dùng lưu local nếu được bật dự phòng.");
                if (!_supabaseOptions.StorageFallbackToLocal) throw;
            }
        }

        return await SaveToLocalAsync(file, safeFolder, fileName);
    }

    private bool CanUseSupabaseStorage()
    {
        return _supabaseOptions.StorageEnabled
            && !string.IsNullOrWhiteSpace(_supabaseOptions.Url)
            && !string.IsNullOrWhiteSpace(_supabaseOptions.StorageBucket)
            && !string.IsNullOrWhiteSpace(_supabaseOptions.StorageApiKey);
    }

    private async Task<string?> SaveToSupabaseStorageAsync(IFormFile file, string safeUserId, string safeFolder, string fileName)
    {
        var baseUrl = _supabaseOptions.Url.Trim().TrimEnd('/');
        var bucket = _supabaseOptions.StorageBucket.Trim().Trim('/');
        var apiKey = _supabaseOptions.StorageApiKey.Trim();

        var storagePath = BuildStorageObjectPath(safeUserId, safeFolder, fileName);
        var uploadUrl = $"{baseUrl}/storage/v1/object/{Uri.EscapeDataString(bucket)}/{EscapeStoragePath(storagePath)}";

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("apikey", apiKey);
        request.Headers.TryAddWithoutValidation("x-upsert", "true");

        await using var stream = file.OpenReadStream();
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(GetSafeContentType(file));
        request.Content.Headers.ContentLength = file.Length;

        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Supabase Storage upload lỗi {(int)response.StatusCode}: {detail}");
        }

        return BuildSupabasePublicUrl(baseUrl, bucket, storagePath);
    }

    private string BuildSupabasePublicUrl(string baseUrl, string bucket, string storagePath)
    {
        var publicBase = string.IsNullOrWhiteSpace(_supabaseOptions.StoragePublicUrl)
            ? $"{baseUrl}/storage/v1/object/public/{Uri.EscapeDataString(bucket)}"
            : _supabaseOptions.StoragePublicUrl.Trim().TrimEnd('/');

        return $"{publicBase}/{EscapeStoragePath(storagePath)}";
    }

    private static string BuildStorageObjectPath(string safeUserId, string safeFolder, string fileName)
    {
        var datePath = DateTime.UtcNow.ToString("yyyy/MM");
        var normalizedFolder = safeFolder.Replace(Path.DirectorySeparatorChar, '/').Trim('/');
        var normalizedUserId = SanitizeFileName(safeUserId);
        return $"{normalizedFolder}/{datePath}/{normalizedUserId}/{fileName}";
    }

    private static string EscapeStoragePath(string storagePath)
    {
        return string.Join('/', storagePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
    }

    private async Task<string?> SaveToLocalAsync(IFormFile file, string safeFolder, string fileName)
    {
        var (uploadDir, urlFolder) = PrepareUploadFolder(safeFolder);
        var filePath = Path.Combine(uploadDir, fileName);

        await using var fs = File.Create(filePath);
        await file.CopyToAsync(fs);

        return $"/{UploadRootFolder}/{urlFolder}/{fileName}";
    }

    private (string UploadDir, string UrlFolder) PrepareUploadFolder(string folderName)
    {
        var webRoot = _env.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            var contentRoot = string.IsNullOrWhiteSpace(_env.ContentRootPath)
                ? Directory.GetCurrentDirectory()
                : _env.ContentRootPath;
            webRoot = Path.Combine(contentRoot, "wwwroot");
        }

        var safeFolder = NormalizeFolder(folderName);
        var uploadsRoot = Path.Combine(webRoot, UploadRootFolder);
        var uploadDir = Path.GetFullPath(Path.Combine(uploadsRoot, safeFolder));
        var uploadsRootFullPath = Path.GetFullPath(uploadsRoot);

        if (!uploadDir.StartsWith(uploadsRootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Thư mục upload không hợp lệ.");
        }

        Directory.CreateDirectory(uploadDir);
        EnsureGitKeep(uploadDir);

        var urlFolder = safeFolder.Replace(Path.DirectorySeparatorChar, '/').Trim('/');
        return (uploadDir, urlFolder);
    }

    private static string NormalizeFolder(string folderName)
    {
        var normalized = (folderName ?? string.Empty)
            .Trim('/', '\\')
            .Replace("..", string.Empty)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        var parts = normalized
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeFileName)
            .Where(part => !string.IsNullOrWhiteSpace(part));

        var safe = Path.Combine(parts.ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "misc" : safe;
    }

    private static string SanitizeFileName(string fileName)
    {
        var safe = new string((fileName ?? string.Empty).Select(ch =>
            ch is >= 'a' and <= 'z' ||
            ch is >= 'A' and <= 'Z' ||
            ch is >= '0' and <= '9' ||
            ch is '-' or '_'
                ? ch
                : '_').ToArray()).Trim('_');
        if (string.IsNullOrWhiteSpace(safe)) safe = "file";
        return safe.Length > 48 ? safe[..48] : safe;
    }

    private static string GetSafeContentType(IFormFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.ContentType)) return file.ContentType;

        return Path.GetExtension(file.FileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    private static void EnsureGitKeep(string directory)
    {
        var gitKeep = Path.Combine(directory, ".gitkeep");
        if (!File.Exists(gitKeep))
        {
            File.WriteAllText(gitKeep, string.Empty);
        }
    }
}
