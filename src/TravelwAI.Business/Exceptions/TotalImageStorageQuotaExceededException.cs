using TravelwAI.Models.Storage;

namespace TravelwAI.Business.Exceptions;

public sealed class TotalImageStorageQuotaExceededException : InvalidOperationException
{
    public TotalImageStorageQuotaExceededException(UserImageStorageUsage usage, long requestedBytes)
        : base("Tổng dung lượng ảnh của hệ thống đã đạt hạn mức.")
    {
        Usage = usage;
        RequestedBytes = requestedBytes;
    }

    public UserImageStorageUsage Usage { get; }
    public long RequestedBytes { get; }
}
