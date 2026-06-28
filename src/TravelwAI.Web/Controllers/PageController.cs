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

    [HttpGet("/pricing")]
    public IActionResult Pricing() => View("Pricing");

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

    [HttpGet("/cart")]
    public IActionResult Cart() => View("Cart");

    [HttpGet("/checkout")]
    public IActionResult Checkout() => View("Checkout");

    [HttpGet("/manage")]
    public IActionResult Manage() => View("Manage");
    [HttpGet("/business")]
    public IActionResult Business() => View("Business");

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
            Description = $"{match.Name} là điểm đến để khám phá văn hoá, lịch sử, lễ hội, làng nghề truyền thống, nhân vật lịch sử và địa danh nổi tiếng của địa phương.",
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
}
