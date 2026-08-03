using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TravelwAI.Business.Exceptions;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Data.Options;
using TravelwAI.Models.Storage;

namespace TravelwAI.Business.Services;

public sealed class FileStorageService : IFileStorageService
{
    public const long UserImageStorageLimitBytes = 200L * 1024 * 1024;
    public const long DefaultTotalImageStorageLimitBytes = 1L * 1024 * 1024 * 1024;
    public const long MinTotalImageStorageLimitBytes = 1L * 1024 * 1024;
    public const long MaxTotalImageStorageLimitBytes = 10L * 1024 * 1024 * 1024 * 1024;

    private const string StorageSettingsCollection = "site_settings";
    private const string StorageSettingsDocumentId = "storage";

    private const long MaxAttachmentBytes = 10 * 1024 * 1024;
    private const string UploadRootFolder = "uploads";
    private const string QuotaFolderRegex = "^(profiles|memories|ai-chat|chat|tours|posts|feedback)/";

    private readonly IWebHostEnvironment _env;
    private readonly SupabaseOptions _supabaseOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataRepository _repository;
    private readonly ILogger<FileStorageService> _logger;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp"
    };

    private static readonly HashSet<string> AllowedAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
        ".mp4", ".webm", ".mov",
        ".mp3", ".wav", ".ogg", ".m4a",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".txt", ".csv", ".zip"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        WriteIndented = false
    };

    public FileStorageService(
        IWebHostEnvironment env,
        IOptions<SupabaseOptions> supabaseOptions,
        IHttpClientFactory httpClientFactory,
        NpgsqlDataSource dataSource,
        IDataRepository repository,
        ILogger<FileStorageService> logger)
    {
        _env = env;
        _supabaseOptions = supabaseOptions.Value;
        _httpClientFactory = httpClientFactory;
        _dataSource = dataSource;
        _repository = repository;
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

        return await SaveWithImageQuotaAsync(
            file,
            userId,
            safeUserId,
            safeFolder,
            fileName,
            () => SaveToBestStorageAsync(file, safeUserId, safeFolder, fileName));
    }

    public async Task<string?> SaveImageToSupabaseAsync(IFormFile file, string userId, string folderName)
    {
        if (file.Length == 0 || file.Length > MaxAttachmentBytes) return null;

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) return null;
        if (!CanUseSupabaseStorage())
        {
            throw new InvalidOperationException(
                "Supabase Storage chưa được cấu hình. Cần SUPABASE_URL, SUPABASE_STORAGE_BUCKET và SUPABASE_SERVICE_ROLE_KEY (hoặc SUPABASE_STORAGE_API_KEY).");
        }

        var safeUserId = SanitizeFileName(userId);
        var safeFolder = NormalizeFolder(folderName);
        var fileName = $"{safeUserId}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}{ext}";

        return await SaveWithImageQuotaAsync(
            file,
            userId,
            safeUserId,
            safeFolder,
            fileName,
            async () =>
            {
                var stored = await SaveToSupabaseStorageAsync(file, safeUserId, safeFolder, fileName);
                await EnsurePublicUrlAccessibleAsync(stored.Url);
                return stored;
            });
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

        var isImage = AllowedExtensions.Contains(ext);
        if (!isImage || !IsQuotaManagedFolder(safeFolder))
        {
            return await SaveTrackedFileWithoutQuotaAsync(
                file,
                userId,
                safeUserId,
                safeFolder,
                fileName,
                isImage);
        }

        return await SaveWithImageQuotaAsync(
            file,
            userId,
            safeUserId,
            safeFolder,
            fileName,
            () => SaveToBestStorageAsync(file, safeUserId, safeFolder, fileName));
    }


    public async Task<bool> DeleteStoredFileByUrlAsync(string publicUrl)
    {
        var cleanUrl = (publicUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanUrl)) return false;

        string? uploadId = null;
        string? provider = null;
        string? storagePath = null;

        await using (var conn = await _dataSource.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                select id, storage_provider, storage_path
                from app_user_uploads
                where public_url = @url and deleted_at is null
                order by created_at desc
                limit 1;
                """;
            cmd.Parameters.AddWithValue("url", cleanUrl);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                uploadId = reader.GetString(0);
                provider = reader.GetString(1);
                storagePath = reader.GetString(2);
            }
        }

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(storagePath))
        {
            (provider, storagePath) = ResolveStoredFileLocation(cleanUrl);
        }

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(storagePath)) return false;

        if (string.Equals(provider, "supabase", StringComparison.OrdinalIgnoreCase))
        {
            await DeleteSupabaseObjectsAsync(new[] { storagePath });
        }
        else if (!TryDeleteLocalObject(storagePath))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uploadId))
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await MarkDeletedAsync(conn, new[] { uploadId });
        }

        return true;
    }

    public async Task<bool> IsStoredFileOwnedByUserInFolderAsync(string publicUrl, string userId, string folderName)
    {
        var cleanUrl = (publicUrl ?? string.Empty).Trim();
        var cleanUserId = (userId ?? string.Empty).Trim();
        var cleanFolder = NormalizeFolder(folderName).Replace(Path.DirectorySeparatorChar, '/');
        if (cleanUrl.Length == 0 || cleanUserId.Length == 0 || cleanFolder.Length == 0) return false;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select 1
            from app_user_uploads
            where public_url = @url
              and user_id = @userId
              and folder = @folder
              and deleted_at is null
            limit 1;
            """;
        cmd.Parameters.AddWithValue("url", cleanUrl);
        cmd.Parameters.AddWithValue("userId", cleanUserId);
        cmd.Parameters.AddWithValue("folder", cleanFolder);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    public async Task<int> DeleteStoredFilesInFolderAsync(string folderName)
    {
        var cleanFolder = NormalizeFolder(folderName).Replace(Path.DirectorySeparatorChar, '/');
        if (cleanFolder.Length == 0) return 0;

        var urls = new List<string>();
        await using (var conn = await _dataSource.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                select public_url
                from app_user_uploads
                where folder = @folder
                  and deleted_at is null
                order by created_at asc;
                """;
            cmd.Parameters.AddWithValue("folder", cleanFolder);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0)) urls.Add(reader.GetString(0));
            }
        }

        var deleted = 0;
        foreach (var url in urls.Distinct(StringComparer.Ordinal))
        {
            if (await DeleteStoredFileByUrlAsync(url)) deleted += 1;
        }
        return deleted;
    }

    private (string? Provider, string? StoragePath) ResolveStoredFileLocation(string publicUrl)
    {
        if (publicUrl.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
        {
            return ("local", publicUrl.TrimStart('/'));
        }

        var baseUrl = _supabaseOptions.Url?.Trim().TrimEnd('/') ?? string.Empty;
        var bucket = _supabaseOptions.StorageBucket?.Trim().Trim('/') ?? string.Empty;
        var publicBase = string.IsNullOrWhiteSpace(_supabaseOptions.StoragePublicUrl)
            ? (!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(bucket)
                ? $"{baseUrl}/storage/v1/object/public/{Uri.EscapeDataString(bucket)}"
                : string.Empty)
            : _supabaseOptions.StoragePublicUrl.Trim().TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(publicBase)
            && publicUrl.StartsWith(publicBase + "/", StringComparison.OrdinalIgnoreCase))
        {
            return ("supabase", DecodeStoragePath(publicUrl[(publicBase.Length + 1)..]));
        }

        var marker = "/storage/v1/object/public/";
        var markerIndex = publicUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var remainder = publicUrl[(markerIndex + marker.Length)..];
            var slash = remainder.IndexOf('/');
            if (slash >= 0 && slash + 1 < remainder.Length)
            {
                return ("supabase", DecodeStoragePath(remainder[(slash + 1)..]));
            }
        }

        return (null, null);
    }

    private static string DecodeStoragePath(string value)
    {
        return string.Join('/', value
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString));
    }

    public async Task<UserImageStorageUsage> GetUserImageStorageUsageAsync(string userId)
    {
        return (await GetUserImageStorageDetailsAsync(userId)).Usage;
    }

    public async Task<UserImageStorageDetails> GetUserImageStorageDetailsAsync(string userId)
    {
        var safeUserId = SanitizeFileName(userId);
        await using var conn = await _dataSource.OpenConnectionAsync();
        await AcquireUserLockAsync(conn, safeUserId);
        try
        {
            await EnsureLegacyUploadsBackfilledAsync(conn, userId, safeUserId);
            var items = await ReadActiveImagesAsync(conn, userId);
            return BuildStorageDetails(userId, items);
        }
        finally
        {
            await ReleaseUserLockAsync(conn, safeUserId);
        }
    }

    public async Task<IReadOnlyList<UserImageStorageAccountUsage>> GetUsersImageStorageUsageAsync(IEnumerable<string> userIds)
    {
        var ids = userIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = ids.Select(async userId =>
        {
            await gate.WaitAsync();
            try
            {
                var details = await GetUserImageStorageDetailsAsync(userId);
                return new UserImageStorageAccountUsage(userId, details.Usage, details.Categories);
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    public async Task<UserImageStorageUsage> GetTotalImageStorageUsageAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        var limitBytes = await ReadTotalImageStorageLimitAsync();
        return await ReadTotalUsageAsync(conn, limitBytes);
    }

    public async Task<UserImageStorageUsage> SetTotalImageStorageLimitAsync(long limitBytes, string updatedBy)
    {
        var safeLimit = Math.Clamp(limitBytes, MinTotalImageStorageLimitBytes, MaxTotalImageStorageLimitBytes);
        await using var conn = await _dataSource.OpenConnectionAsync();
        await AcquireGlobalStorageLockAsync(conn);
        try
        {
            var now = DateTime.UtcNow;
            await _repository.SetAsync(StorageSettingsCollection, StorageSettingsDocumentId, new Dictionary<string, object?>
            {
                ["total_limit_bytes"] = safeLimit,
                ["totalLimitBytes"] = safeLimit,
                ["updated_by"] = updatedBy,
                ["updatedBy"] = updatedBy,
                ["updated_at"] = now,
                ["updatedAt"] = now
            }, merge: true);
            return await ReadTotalUsageAsync(conn, safeLimit);
        }
        finally
        {
            await ReleaseGlobalStorageLockAsync(conn);
        }
    }

    public Task<UserImageDeleteResult> DeleteAllUserImagesAsync(string userId)
    {
        return DeleteUserImagesAsync(userId, uploadId: null, allowedCategories: null);
    }

    public Task<UserImageDeleteResult> DeleteUserMessageImagesAsync(string userId)
    {
        return DeleteUserImagesAsync(
            userId,
            uploadId: null,
            allowedCategories: new HashSet<string>(new[] { "chat", "ai-chat" }, StringComparer.OrdinalIgnoreCase));
    }

    public Task<UserImageDeleteResult> DeleteUserImageAsync(string userId, string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            throw new ArgumentException("Mã ảnh không hợp lệ.", nameof(uploadId));
        }
        return DeleteUserImagesAsync(userId, uploadId.Trim(), allowedCategories: null);
    }

    private async Task<UserImageDeleteResult> DeleteUserImagesAsync(
        string userId,
        string? uploadId,
        HashSet<string>? allowedCategories)
    {
        var safeUserId = SanitizeFileName(userId);
        await using var conn = await _dataSource.OpenConnectionAsync();
        await AcquireUserLockAsync(conn, safeUserId);
        try
        {
            await EnsureLegacyUploadsBackfilledAsync(conn, userId, safeUserId);
            var uploads = await ReadActiveImagesAsync(conn, userId);
            if (!string.IsNullOrWhiteSpace(uploadId))
            {
                uploads = uploads
                    .Where(item => string.Equals(item.Id, uploadId, StringComparison.Ordinal))
                    .ToList();
            }
            if (allowedCategories is not null)
            {
                uploads = uploads
                    .Where(item => allowedCategories.Contains(item.Category))
                    .ToList();
            }
            if (uploads.Count == 0)
            {
                return new UserImageDeleteResult(0, 0, await ReadUsageAsync(conn, userId));
            }

            var deleted = new List<TrackedUpload>();
            var supabaseUploads = uploads.Where(item => item.Provider == "supabase").ToList();
            foreach (var batch in supabaseUploads.Chunk(100))
            {
                await DeleteSupabaseObjectsAsync(batch.Select(item => item.StoragePath).ToArray());
                deleted.AddRange(batch);
            }

            foreach (var local in uploads.Where(item => item.Provider == "local"))
            {
                if (TryDeleteLocalObject(local.StoragePath)) deleted.Add(local);
            }

            if (deleted.Count > 0)
            {
                await MarkDeletedAsync(conn, deleted.Select(item => item.Id));
                try
                {
                    await RemoveDeletedImageReferencesAsync(deleted.Select(item => item.PublicUrl));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ảnh đã được xóa nhưng không thể dọn hết URL cũ trong dữ liệu tài khoản {UserId}.", userId);
                }
            }

            var usage = await ReadUsageAsync(conn, userId);
            return new UserImageDeleteResult(
                deleted.Count,
                deleted.Sum(item => item.FileSize),
                usage);
        }
        finally
        {
            await ReleaseUserLockAsync(conn, safeUserId);
        }
    }

    private async Task<string?> SaveTrackedFileWithoutQuotaAsync(
        IFormFile file,
        string userId,
        string safeUserId,
        string safeFolder,
        string fileName,
        bool isImage)
    {
        StoredFile? stored = null;
        try
        {
            stored = await SaveToBestStorageAsync(file, safeUserId, safeFolder, fileName);
            if (stored is null) return null;

            await using var conn = await _dataSource.OpenConnectionAsync();
            await RegisterUploadAsync(conn, userId, safeFolder, file, stored, isImage);
            return stored.Url;
        }
        catch
        {
            if (stored is not null) await TryDeleteStoredFileAsync(stored);
            throw;
        }
    }

    private async Task<string?> SaveWithImageQuotaAsync(
        IFormFile file,
        string userId,
        string safeUserId,
        string safeFolder,
        string fileName,
        Func<Task<StoredFile?>> save)
    {
        if (!IsQuotaManagedFolder(safeFolder)) return (await save())?.Url;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await AcquireGlobalStorageLockAsync(conn);
        await AcquireUserLockAsync(conn, safeUserId);
        try
        {
            await EnsureLegacyUploadsBackfilledAsync(conn, userId, safeUserId);

            var totalLimitBytes = await ReadTotalImageStorageLimitAsync();
            var totalUsage = await ReadTotalUsageAsync(conn, totalLimitBytes);
            if (file.Length > totalUsage.RemainingBytes)
            {
                throw new TotalImageStorageQuotaExceededException(totalUsage, file.Length);
            }

            var usage = await ReadUsageAsync(conn, userId);
            if (file.Length > usage.RemainingBytes)
            {
                throw new ImageStorageQuotaExceededException(usage, file.Length);
            }

            StoredFile? stored = null;
            try
            {
                stored = await save();
                if (stored is null) return null;
                await RegisterUploadAsync(conn, userId, safeFolder, file, stored, isImage: true);
                return stored.Url;
            }
            catch
            {
                if (stored is not null)
                {
                    await TryDeleteStoredFileAsync(stored);
                }
                throw;
            }
        }
        finally
        {
            await ReleaseUserLockAsync(conn, safeUserId);
            await ReleaseGlobalStorageLockAsync(conn);
        }
    }

    private async Task<StoredFile?> SaveToBestStorageAsync(IFormFile file, string safeUserId, string safeFolder, string fileName)
    {
        if (!CanUseSupabaseStorage())
        {
            if (!_supabaseOptions.StorageFallbackToLocal)
            {
                throw new InvalidOperationException(
                    "Supabase Storage chưa được cấu hình và lưu local đã bị tắt. Cần SUPABASE_URL, SUPABASE_STORAGE_BUCKET và SUPABASE_SERVICE_ROLE_KEY.");
            }
            return await SaveToLocalAsync(file, safeFolder, fileName);
        }

        try
        {
            return await SaveToSupabaseStorageAsync(file, safeUserId, safeFolder, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể tải tệp lên Supabase Storage.");
            if (!_supabaseOptions.StorageFallbackToLocal) throw;
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

    private async Task<StoredFile> SaveToSupabaseStorageAsync(IFormFile file, string safeUserId, string safeFolder, string fileName)
    {
        var baseUrl = _supabaseOptions.Url.Trim().TrimEnd('/');
        var bucket = _supabaseOptions.StorageBucket.Trim().Trim('/');
        var apiKey = _supabaseOptions.StorageApiKey.Trim();

        var storagePath = BuildStorageObjectPath(safeUserId, safeFolder, fileName);
        var uploadUrl = $"{baseUrl}/storage/v1/object/{Uri.EscapeDataString(bucket)}/{EscapeStoragePath(storagePath)}";

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        AddStorageAuthHeaders(request, apiKey);
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

        return new StoredFile(
            BuildSupabasePublicUrl(baseUrl, bucket, storagePath),
            storagePath,
            "supabase");
    }

    private string BuildSupabasePublicUrl(string baseUrl, string bucket, string storagePath)
    {
        var publicBase = string.IsNullOrWhiteSpace(_supabaseOptions.StoragePublicUrl)
            ? $"{baseUrl}/storage/v1/object/public/{Uri.EscapeDataString(bucket)}"
            : _supabaseOptions.StoragePublicUrl.Trim().TrimEnd('/');

        return $"{publicBase}/{EscapeStoragePath(storagePath)}";
    }

    private async Task EnsurePublicUrlAccessibleAsync(string publicUrl)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, publicUrl);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.IsSuccessStatusCode) return;

        var detail = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            $"Ảnh đã upload nhưng URL công khai không truy cập được ({(int)response.StatusCode}). " +
            "Hãy bật Public bucket cho Supabase Storage hoặc cấu hình Supabase:StoragePublicUrl đúng. " + detail);
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

    private async Task<StoredFile> SaveToLocalAsync(IFormFile file, string safeFolder, string fileName)
    {
        var (uploadDir, urlFolder) = PrepareUploadFolder(safeFolder);
        var filePath = Path.Combine(uploadDir, fileName);

        await using var fs = File.Create(filePath);
        await file.CopyToAsync(fs);

        var relativePath = $"{UploadRootFolder}/{urlFolder}/{fileName}".Replace('\\', '/');
        return new StoredFile($"/{relativePath}", relativePath, "local");
    }

    private async Task RegisterUploadAsync(
        NpgsqlConnection conn,
        string userId,
        string safeFolder,
        IFormFile file,
        StoredFile stored,
        bool isImage)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            insert into app_user_uploads(
                id, user_id, storage_provider, storage_path, public_url, folder,
                content_type, file_size, is_image, created_at, deleted_at)
            values (
                @id, @userId, @provider, @path, @url, @folder,
                @contentType, @fileSize, @isImage, now(), null)
            on conflict (storage_provider, storage_path) do update
            set user_id = excluded.user_id,
                public_url = excluded.public_url,
                folder = excluded.folder,
                content_type = excluded.content_type,
                file_size = excluded.file_size,
                is_image = excluded.is_image,
                deleted_at = null;
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("provider", stored.Provider);
        cmd.Parameters.AddWithValue("path", stored.StoragePath);
        cmd.Parameters.AddWithValue("url", stored.Url);
        cmd.Parameters.AddWithValue("folder", safeFolder.Replace(Path.DirectorySeparatorChar, '/'));
        cmd.Parameters.AddWithValue("contentType", GetSafeContentType(file));
        cmd.Parameters.AddWithValue("fileSize", file.Length);
        cmd.Parameters.AddWithValue("isImage", isImage);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<UserImageStorageUsage> ReadUsageAsync(NpgsqlConnection conn, string userId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select coalesce(sum(file_size), 0)::bigint, count(*)::int
            from app_user_uploads
            where user_id = @userId
              and is_image = true
              and deleted_at is null;
            """;
        cmd.Parameters.AddWithValue("userId", userId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new UserImageStorageUsage(0, UserImageStorageLimitBytes, 0);
        return new UserImageStorageUsage(reader.GetInt64(0), UserImageStorageLimitBytes, reader.GetInt32(1));
    }

    private async Task<List<TrackedUpload>> ReadActiveImagesAsync(NpgsqlConnection conn, string userId)
    {
        var items = new List<TrackedUpload>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select id, storage_provider, storage_path, public_url, folder,
                   content_type, file_size, created_at
            from app_user_uploads
            where user_id = @userId
              and is_image = true
              and deleted_at is null
            order by created_at desc;
            """;
        cmd.Parameters.AddWithValue("userId", userId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var folder = reader.GetString(4);
            items.Add(new TrackedUpload(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                folder,
                GetStorageCategory(folder),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.GetDateTime(7).ToUniversalTime()));
        }
        return items;
    }

    private static UserImageStorageDetails BuildStorageDetails(string userId, IReadOnlyList<TrackedUpload> uploads)
    {
        var categories = uploads
            .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => new UserImageStorageCategoryUsage(
                group.Key,
                group.Sum(item => item.FileSize),
                group.Count()))
            .OrderByDescending(item => item.UsedBytes)
            .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var usedBytes = uploads.Sum(item => item.FileSize);
        var usage = new UserImageStorageUsage(usedBytes, UserImageStorageLimitBytes, uploads.Count);
        var items = uploads.Select(item => new UserImageStorageItem(
            item.Id,
            userId,
            item.Provider,
            item.StoragePath,
            item.PublicUrl,
            item.Folder,
            item.Category,
            item.ContentType,
            item.FileSize,
            item.CreatedAt)).ToList();
        return new UserImageStorageDetails(usage, categories, items);
    }

    private static string GetStorageCategory(string folderName)
    {
        return folderName
            .Replace(Path.DirectorySeparatorChar, '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "other";
    }

    private async Task EnsureLegacyUploadsBackfilledAsync(NpgsqlConnection conn, string userId, string safeUserId)
    {
        await using (var scanCmd = conn.CreateCommand())
        {
            scanCmd.CommandText = "select exists(select 1 from app_user_upload_scans where user_id = @userId);";
            scanCmd.Parameters.AddWithValue("userId", userId);
            if (Convert.ToBoolean(await scanCmd.ExecuteScalarAsync())) return;
        }

        try
        {
            await BackfillSupabaseUploadsAsync(conn, userId, safeUserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể quét ảnh Supabase cũ cho tài khoản {UserId}.", userId);
        }

        try
        {
            await BackfillLocalUploadsAsync(conn, userId, safeUserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể quét ảnh local cũ cho tài khoản {UserId}.", userId);
        }

        await using var markCmd = conn.CreateCommand();
        markCmd.CommandText = """
            insert into app_user_upload_scans(user_id, scanned_at)
            values (@userId, now())
            on conflict (user_id) do update set scanned_at = excluded.scanned_at;
            """;
        markCmd.Parameters.AddWithValue("userId", userId);
        await markCmd.ExecuteNonQueryAsync();
    }

    private async Task BackfillSupabaseUploadsAsync(NpgsqlConnection conn, string userId, string safeUserId)
    {
        if (string.IsNullOrWhiteSpace(_supabaseOptions.StorageBucket)) return;

        var rows = new List<(string Path, string ContentType, long Size)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                select name,
                       coalesce(metadata ->> 'mimetype', 'image/*') as content_type,
                       case
                           when coalesce(metadata ->> 'size', '') ~ '^[0-9]+$'
                           then (metadata ->> 'size')::bigint
                           else 0
                       end as file_size
                from storage.objects
                where bucket_id = @bucket
                  and name like @userPath
                  and name ~* @folderRegex
                  and lower(name) ~ '[.](jpg|jpeg|png|gif|webp)$';
                """;
            cmd.Parameters.AddWithValue("bucket", _supabaseOptions.StorageBucket.Trim());
            cmd.Parameters.AddWithValue("userPath", $"%/{safeUserId}/%");
            cmd.Parameters.AddWithValue("folderRegex", QuotaFolderRegex);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        if (rows.Count == 0) return;
        var baseUrl = _supabaseOptions.Url.Trim().TrimEnd('/');
        var bucket = _supabaseOptions.StorageBucket.Trim().Trim('/');
        foreach (var row in rows)
        {
            await InsertBackfillRowAsync(
                conn,
                userId,
                "supabase",
                row.Path,
                BuildSupabasePublicUrl(baseUrl, bucket, row.Path),
                row.Path.Split('/')[0],
                row.ContentType,
                row.Size);
        }
    }

    private async Task BackfillLocalUploadsAsync(NpgsqlConnection conn, string userId, string safeUserId)
    {
        var webRoot = ResolveWebRoot();
        var uploadsRoot = Path.Combine(webRoot, UploadRootFolder);
        if (!Directory.Exists(uploadsRoot)) return;

        foreach (var filePath in Directory.EnumerateFiles(uploadsRoot, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(filePath);
            if (!AllowedExtensions.Contains(ext)) continue;
            if (!Path.GetFileName(filePath).StartsWith($"{safeUserId}_", StringComparison.OrdinalIgnoreCase)) continue;

            var relative = Path.GetRelativePath(webRoot, filePath).Replace('\\', '/');
            var folder = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? "misc";
            if (!IsQuotaManagedFolder(folder)) continue;
            var info = new FileInfo(filePath);
            await InsertBackfillRowAsync(
                conn,
                userId,
                "local",
                relative,
                $"/{relative}",
                folder,
                GetContentTypeFromExtension(ext),
                info.Exists ? info.Length : 0);
        }
    }

    private static async Task InsertBackfillRowAsync(
        NpgsqlConnection conn,
        string userId,
        string provider,
        string path,
        string url,
        string folder,
        string contentType,
        long fileSize)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            insert into app_user_uploads(
                id, user_id, storage_provider, storage_path, public_url, folder,
                content_type, file_size, is_image, created_at, deleted_at)
            values (
                @id, @userId, @provider, @path, @url, @folder,
                @contentType, @fileSize, true, now(), null)
            on conflict (storage_provider, storage_path) do nothing;
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("url", url);
        cmd.Parameters.AddWithValue("folder", folder);
        cmd.Parameters.AddWithValue("contentType", contentType);
        cmd.Parameters.AddWithValue("fileSize", Math.Max(0, fileSize));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DeleteSupabaseObjectsAsync(string[] paths)
    {
        if (paths.Length == 0) return;
        if (!CanUseSupabaseStorage())
        {
            throw new InvalidOperationException("Không thể xóa ảnh vì Supabase Storage chưa được cấu hình.");
        }

        var baseUrl = _supabaseOptions.Url.Trim().TrimEnd('/');
        var bucket = _supabaseOptions.StorageBucket.Trim().Trim('/');
        var apiKey = _supabaseOptions.StorageApiKey.Trim();
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{baseUrl}/storage/v1/object/{Uri.EscapeDataString(bucket)}");
        AddStorageAuthHeaders(request, apiKey);
        request.Content = JsonContent.Create(new { prefixes = paths });
        using var response = await client.SendAsync(request);
        if (response.IsSuccessStatusCode) return;

        var detail = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"Supabase Storage xóa ảnh lỗi {(int)response.StatusCode}: {detail}");
    }

    private bool TryDeleteLocalObject(string relativePath)
    {
        try
        {
            var webRoot = ResolveWebRoot();
            var fullPath = Path.GetFullPath(Path.Combine(webRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var safeRoot = Path.GetFullPath(Path.Combine(webRoot, UploadRootFolder));
            if (!fullPath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) return false;
            if (File.Exists(fullPath)) File.Delete(fullPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể xóa ảnh local {Path}.", relativePath);
            return false;
        }
    }

    private async Task TryDeleteStoredFileAsync(StoredFile stored)
    {
        try
        {
            if (stored.Provider == "supabase")
            {
                await DeleteSupabaseObjectsAsync(new[] { stored.StoragePath });
            }
            else
            {
                TryDeleteLocalObject(stored.StoragePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể hoàn tác tệp upload {Path}.", stored.StoragePath);
        }
    }

    private static async Task MarkDeletedAsync(NpgsqlConnection conn, IEnumerable<string> ids)
    {
        var cleanIds = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (cleanIds.Length == 0) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "update app_user_uploads set deleted_at = now() where id = any(@ids);";
        cmd.Parameters.AddWithValue("ids", cleanIds);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RemoveDeletedImageReferencesAsync(IEnumerable<string> urls)
    {
        var deletedUrls = urls
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (deletedUrls.Count == 0) return;

        var affected = new Dictionary<(string Collection, string Id), string>();
        await using (var conn = await _dataSource.OpenConnectionAsync())
        {
            foreach (var batch in deletedUrls.Chunk(40))
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    select collection, id, data::text
                    from app_documents
                    where data::text like any(@patterns);
                    """;
                cmd.Parameters.AddWithValue("patterns", batch.Select(url => $"%{url}%").ToArray());
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    affected[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
                }
            }
        }

        foreach (var entry in affected)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(entry.Value);
            }
            catch
            {
                continue;
            }

            if (root is null || !RemoveDeletedUrls(root, deletedUrls)) continue;
            var clean = root.Deserialize<Dictionary<string, object?>>(JsonOptions);
            if (clean is null) continue;
            await _repository.SetAsync(entry.Key.Collection, entry.Key.Id, clean, merge: false);
        }
    }

    private static bool RemoveDeletedUrls(JsonNode node, HashSet<string> deletedUrls)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && deletedUrls.Contains(text.Trim()))
                {
                    obj.Remove(property.Key);
                    changed = true;
                    continue;
                }

                if (property.Value is JsonObject childObject
                    && childObject["url"] is JsonValue urlValue
                    && urlValue.TryGetValue<string>(out var childUrl)
                    && deletedUrls.Contains(childUrl.Trim()))
                {
                    obj.Remove(property.Key);
                    changed = true;
                    continue;
                }

                if (property.Value is not null && RemoveDeletedUrls(property.Value, deletedUrls)) changed = true;
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = array.Count - 1; index >= 0; index--)
            {
                var item = array[index];
                if (item is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && deletedUrls.Contains(text.Trim()))
                {
                    array.RemoveAt(index);
                    changed = true;
                    continue;
                }

                if (item is JsonObject itemObject
                    && itemObject["url"] is JsonValue urlValue
                    && urlValue.TryGetValue<string>(out var itemUrl)
                    && deletedUrls.Contains(itemUrl.Trim()))
                {
                    array.RemoveAt(index);
                    changed = true;
                    continue;
                }

                if (item is not null && RemoveDeletedUrls(item, deletedUrls)) changed = true;
            }
        }
        return changed;
    }

    private async Task<long> ReadTotalImageStorageLimitAsync()
    {
        try
        {
            var settings = await _repository.GetByIdAsync(StorageSettingsCollection, StorageSettingsDocumentId);
            foreach (var key in new[] { "total_limit_bytes", "totalLimitBytes" })
            {
                if (settings is not null
                    && settings.TryGetValue(key, out var raw)
                    && long.TryParse(raw?.ToString(), out var value)
                    && value > 0)
                {
                    return Math.Clamp(value, MinTotalImageStorageLimitBytes, MaxTotalImageStorageLimitBytes);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể đọc tổng hạn mức ảnh; sử dụng mặc định 1 GB.");
        }
        return DefaultTotalImageStorageLimitBytes;
    }

    private static async Task<UserImageStorageUsage> ReadTotalUsageAsync(NpgsqlConnection conn, long limitBytes)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select coalesce(sum(file_size), 0)::bigint, count(*)::int
            from app_user_uploads
            where deleted_at is null;
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new UserImageStorageUsage(0, limitBytes, 0);
        return new UserImageStorageUsage(reader.GetInt64(0), limitBytes, reader.GetInt32(1));
    }

    private static async Task AcquireGlobalStorageLockAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select pg_advisory_lock(hashtextextended(@lockKey, 0));";
        cmd.Parameters.AddWithValue("lockKey", "travelwai-image-storage:global");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ReleaseGlobalStorageLockAsync(NpgsqlConnection conn)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "select pg_advisory_unlock(hashtextextended(@lockKey, 0));";
            cmd.Parameters.AddWithValue("lockKey", "travelwai-image-storage:global");
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {

        }
    }

    private static async Task AcquireUserLockAsync(NpgsqlConnection conn, string safeUserId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select pg_advisory_lock(hashtextextended(@lockKey, 0));";
        cmd.Parameters.AddWithValue("lockKey", $"travelwai-image-storage:{safeUserId}");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ReleaseUserLockAsync(NpgsqlConnection conn, string safeUserId)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "select pg_advisory_unlock(hashtextextended(@lockKey, 0));";
            cmd.Parameters.AddWithValue("lockKey", $"travelwai-image-storage:{safeUserId}");
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {

        }
    }

    private static void AddStorageAuthHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("apikey", apiKey);
    }

    private (string UploadDir, string UrlFolder) PrepareUploadFolder(string folderName)
    {
        var webRoot = ResolveWebRoot();
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

    private string ResolveWebRoot()
    {
        var webRoot = _env.WebRootPath;
        if (!string.IsNullOrWhiteSpace(webRoot)) return webRoot;
        var contentRoot = string.IsNullOrWhiteSpace(_env.ContentRootPath)
            ? Directory.GetCurrentDirectory()
            : _env.ContentRootPath;
        return Path.Combine(contentRoot, "wwwroot");
    }

    private static bool IsQuotaManagedFolder(string folderName)
    {
        var root = folderName
            .Replace(Path.DirectorySeparatorChar, '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        return root is "profiles" or "memories" or "ai-chat" or "chat" or "tours" or "posts" or "feedback";
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
        return GetContentTypeFromExtension(Path.GetExtension(file.FileName));
    }

    private static string GetContentTypeFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
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
        if (!File.Exists(gitKeep)) File.WriteAllText(gitKeep, string.Empty);
    }

    private sealed record StoredFile(string Url, string StoragePath, string Provider);
    private sealed record TrackedUpload(
        string Id,
        string Provider,
        string StoragePath,
        string PublicUrl,
        string Folder,
        string Category,
        string ContentType,
        long FileSize,
        DateTime CreatedAt);
}
