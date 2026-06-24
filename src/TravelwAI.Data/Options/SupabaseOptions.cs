namespace TravelwAI.Data.Options;

public sealed class SupabaseOptions
{
    public string Url { get; set; } = string.Empty;
    public string ProjectRef { get; set; } = string.Empty;
    public string DatabasePassword { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
    public string JwtSecret { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 30;

    public string StorageBucket { get; set; } = "travelwai-uploads";
    public string StorageApiKey { get; set; } = string.Empty;
    public string StoragePublicUrl { get; set; } = string.Empty;
    public bool StorageEnabled { get; set; } = true;
    public bool StorageFallbackToLocal { get; set; } = true;
}
