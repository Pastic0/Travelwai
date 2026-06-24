namespace TravelwAI.Web.Models;

public sealed class DestinationSummaryViewModel
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string OpenTime { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public IReadOnlyList<string> Images { get; init; } = Array.Empty<string>();
}

public sealed class ProvinceDetailPageViewModel
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Area { get; init; } = "Việt Nam";
    public string Region { get; init; } = "Chưa phân loại";
    public string Classification => string.IsNullOrWhiteSpace(Region) ? Area : $"{Area} - {Region}";
    public IReadOnlyList<string> FamousFor { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MergedFrom { get; init; } = Array.Empty<string>();
    public string NaturalAreaKm2 { get; init; } = string.Empty;
    public string Population { get; init; } = string.Empty;
    public string BelongsTo { get; init; } = string.Empty;
    public string AdministrativeNote { get; init; } = string.Empty;
    public bool IsArchipelago { get; init; }
    public IReadOnlyList<DestinationSummaryViewModel> Destinations { get; init; } = Array.Empty<DestinationSummaryViewModel>();

    public bool HasData => !string.IsNullOrWhiteSpace(Name);
}
