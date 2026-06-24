using Microsoft.AspNetCore.Mvc;
using TravelwAI.Web.Models;

namespace TravelwAI.Web.Controllers;

public sealed class PageController : Controller
{
    [HttpGet("/")]
    public IActionResult Index() => View("Landing");

    [HttpGet("/landing")]
    public IActionResult Landing() => View("Landing");

    [HttpGet("/home")]
    public IActionResult Home() => View("MainSite");

    [HttpGet("/login")]
    public IActionResult Login() => View("Login");

    [HttpGet("/signup")]
    public IActionResult Signup() => View("Signup");

    [HttpGet("/forgot-password")]
    public IActionResult ForgotPassword() => View("ForgotPassword");

    [HttpGet("/reset-password")]
    public IActionResult ResetPassword() => View("ResetPassword");

    [HttpGet("/provinces")]
    public IActionResult Provinces()
    {
        var model = new ProvincesPageViewModel
        {
            Provinces = ProvinceCatalog.All
        };
        return View("Provinces", model);
    }

    [HttpGet("/detail")]
    public IActionResult Detail([FromQuery] string? province, [FromQuery] string? id)
    {
        var provinceName = string.IsNullOrWhiteSpace(province) ? id : province;
        if (string.IsNullOrWhiteSpace(provinceName))
        {
            return View("Detail", new ProvinceDetailPageViewModel());
        }

        return View("Detail", ToLocalProvinceDetailPageViewModel(provinceName));
    }

    [HttpGet("/schedule")]
    public IActionResult Schedule() => View("Schedule");

    [HttpGet("/plans")]
    public IActionResult Plans() => View("Plans");

    [HttpGet("/profile")]
    public IActionResult Profile() => View("Profile");

    [HttpGet("/messaging")]
    public IActionResult Messaging() => View("Messaging");

    [HttpGet("/contact")]
    public IActionResult Contact() => View("Contact");

    [HttpGet("/notifications")]
    public IActionResult Notifications() => View("Notifications");

    [HttpGet("/posts")]
    public IActionResult Posts() => View("Posts");

    [HttpGet("/tours")]
    public IActionResult Tours() => View("Tours");

    [HttpGet("/tour-sales")]
    public IActionResult TourSales() => View("TourSales");

    [HttpGet("/admin")]
    public IActionResult AdminPanel() => View("AdminPanel");

    private static ProvinceDetailPageViewModel ToLocalProvinceDetailPageViewModel(string requestedName)
    {
        var match = ProvinceCatalog.All.FirstOrDefault(item =>
            string.Equals(item.Name, requestedName, StringComparison.OrdinalIgnoreCase)
            || item.MergedFrom.Any(oldName => string.Equals(oldName, requestedName, StringComparison.OrdinalIgnoreCase)));

        if (match is null)
        {
            return new ProvinceDetailPageViewModel
            {
                Name = requestedName,
                Description = $"Khám phá văn hoá, lịch sử, lễ hội, làng nghề và địa danh nổi bật ở {requestedName}.",
                Area = "Việt Nam",
                Region = "Chưa phân loại",
                Destinations = BuildLocalDestinationSummaries(requestedName)
            };
        }

        return new ProvinceDetailPageViewModel
        {
            Name = match.Name,
            Description = $"{match.Name} là điểm đến phù hợp để khám phá văn hoá, lịch sử, lễ hội, làng nghề truyền thống, nhân vật lịch sử và địa danh nổi tiếng của địa phương.",
            Area = match.Area,
            Region = match.Region,
            FamousFor = match.FamousFor,
            MergedFrom = match.MergedFrom,
            NaturalAreaKm2 = match.NaturalAreaKm2,
            Population = match.Population,
            Destinations = BuildLocalDestinationSummaries(match.Name)
        };
    }

    private static IReadOnlyList<DestinationSummaryViewModel> BuildLocalDestinationSummaries(string provinceName) => new[]
    {
        new DestinationSummaryViewModel
        {
            Name = $"Trung tâm {provinceName}",
            Description = "Khu vực thuận tiện để bắt đầu hành trình, ăn uống, nghỉ ngơi và tìm hiểu nhịp sống địa phương.",
            Type = "Trung tâm",
            OpenTime = "Cả ngày",
            Address = provinceName
        },
        new DestinationSummaryViewModel
        {
            Name = $"Điểm văn hoá - lịch sử tại {provinceName}",
            Description = "Gợi ý tham quan địa danh nổi tiếng, di tích, lễ hội, làng nghề và không gian văn hoá bản địa.",
            Type = "Văn hoá - lịch sử",
            OpenTime = "08:00 - 17:00",
            Address = provinceName
        }
    };

    private static ProvinceDetailPageViewModel ToProvinceDetailPageViewModel(string requestedName, Dictionary<string, object?>? data)
    {
        if (data is null) return new ProvinceDetailPageViewModel { Name = requestedName, Description = "Thông tin đang được cập nhật." };

        var province = data.GetValueOrDefault("province") as Dictionary<string, object?> ?? data;
        var destinations = data.GetValueOrDefault("destinations") as IEnumerable<Dictionary<string, object?>> ?? Array.Empty<Dictionary<string, object?>>();

        return new ProvinceDetailPageViewModel
        {
            Name = GetText(province, "province_name", "name") ?? requestedName,
            Description = GetText(province, "description") ?? "Thông tin đang được cập nhật.",
            Area = GetText(province, "area") ?? "Việt Nam",
            Region = GetText(province, "subregion", "region") ?? "Chưa phân loại",
            FamousFor = ToStringList(province.GetValueOrDefault("famous_for")),
            MergedFrom = ToStringList(province.GetValueOrDefault("merged_from")),
            NaturalAreaKm2 = GetText(province, "natural_area_km2") ?? string.Empty,
            Population = GetText(province, "population") ?? string.Empty,
            BelongsTo = GetText(province, "belongs_to") ?? string.Empty,
            AdministrativeNote = GetText(province, "administrative_note") ?? string.Empty,
            IsArchipelago = IsTruthy(province.GetValueOrDefault("is_archipelago")),
            Destinations = destinations.Select(ToDestinationSummary).ToList()
        };
    }

    private static DestinationSummaryViewModel ToDestinationSummary(Dictionary<string, object?> value) => new()
    {
        Name = GetText(value, "location_name", "name") ?? "Điểm đến",
        Description = GetText(value, "location_description", "description") ?? string.Empty,
        Type = GetText(value, "location_type", "type") ?? "Tham quan",
        OpenTime = GetText(value, "location_open_time", "open_time") ?? "Đang cập nhật",
        Address = GetText(value, "location_address", "address") ?? string.Empty,
        Images = ToStringList(value.GetValueOrDefault("location_images"))
    };

    private static string? GetText(Dictionary<string, object?> value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (value.TryGetValue(key, out var raw) && raw is not null)
            {
                var text = raw.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return null;
    }

    private static bool IsTruthy(object? value)
    {
        if (value is bool b) return b;
        return bool.TryParse(value?.ToString(), out var parsed) && parsed;
    }

    private static IReadOnlyList<string> ToStringList(object? value)
    {
        if (value is IEnumerable<string> strings) return strings.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (value is IEnumerable<object> objects) return objects.Select(x => x?.ToString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        return Array.Empty<string>();
    }
}
