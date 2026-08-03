using TravelwAI.Models.Storage;

namespace TravelwAI.Business.Exceptions;

public sealed class ImageStorageQuotaExceededException : InvalidOperationException
{
    public ImageStorageQuotaExceededException(UserImageStorageUsage usage, long requestedBytes)
        : base("Tài khoản đã vượt giới hạn 200 MB ảnh tải lên.")
    {
        Usage = usage;
        RequestedBytes = requestedBytes;
    }

    public UserImageStorageUsage Usage { get; }
    public long RequestedBytes { get; }
}
