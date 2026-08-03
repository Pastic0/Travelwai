namespace TravelwAI.Models.Storage;

public sealed record UserImageStorageUsage(
    long UsedBytes,
    long LimitBytes,
    int ImageCount)
{
    public long RemainingBytes => Math.Max(0, LimitBytes - UsedBytes);
    public double UsedPercent => LimitBytes <= 0
        ? 0
        : Math.Round(Math.Clamp((double)UsedBytes / LimitBytes * 100d, 0d, 100d), 2);
}

public sealed record UserImageStorageCategoryUsage(
    string Category,
    long UsedBytes,
    int ImageCount);

public sealed record UserImageStorageItem(
    string Id,
    string UserId,
    string StorageProvider,
    string StoragePath,
    string PublicUrl,
    string Folder,
    string Category,
    string ContentType,
    long FileSize,
    DateTime CreatedAt);

public sealed record UserImageStorageDetails(
    UserImageStorageUsage Usage,
    IReadOnlyList<UserImageStorageCategoryUsage> Categories,
    IReadOnlyList<UserImageStorageItem> Items)
{
    public long MessageImageBytes => Categories
        .Where(item => item.Category is "chat" or "ai-chat")
        .Sum(item => item.UsedBytes);

    public int MessageImageCount => Categories
        .Where(item => item.Category is "chat" or "ai-chat")
        .Sum(item => item.ImageCount);
}

public sealed record UserImageStorageAccountUsage(
    string UserId,
    UserImageStorageUsage Usage,
    IReadOnlyList<UserImageStorageCategoryUsage> Categories);

public sealed record UserImageDeleteResult(
    int DeletedCount,
    long DeletedBytes,
    UserImageStorageUsage Usage);
