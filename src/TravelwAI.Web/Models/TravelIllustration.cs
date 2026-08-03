namespace TravelwAI.Web.Models;

public sealed class TravelIllustration
{
    public string Url { get; set; } = string.Empty;
    public string OriginalUrl { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/jpeg";
    public string Title { get; set; } = string.Empty;
    public string Alt { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string LicenseName { get; set; } = string.Empty;
    public string LicenseUrl { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
}
