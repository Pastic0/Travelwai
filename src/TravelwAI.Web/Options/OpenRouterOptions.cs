namespace TravelwAI.Web.Options;

public sealed class OpenRouterOptions
{
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string Model { get; set; } = "google/gemma-4-31b-it:free";
    public string ApiKey { get; set; } = string.Empty;
    public string SiteUrl { get; set; } = "https://travelwai.id.vn";
    public string AppTitle { get; set; } = "TravelwAI";
    public int TimeoutSeconds { get; set; } = 180;
}
