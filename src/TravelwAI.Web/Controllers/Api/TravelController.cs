using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class TravelController : ApiControllerBase
{
    private const string ProvinceTravelTagsCollection = "province_travel_tags";
    private readonly ITravelService _travelService;
    private readonly IDataRepository _repo;

    public TravelController(IAuthService authService, ITravelService travelService, IDataRepository repo) : base(authService)
    {
        _travelService = travelService;
        _repo = repo;
    }

    [HttpGet("get_province/{provinceName}")]
    public async Task<IActionResult> GetProvince(string provinceName)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var province = await _travelService.GetProvinceByNameAsync(provinceName);
        if (province is null)
        {
            var fallbackTags = await GetProvincePlanTagsAsync(provinceName, null);
            return Ok(new
            {
                success = false,
                data = new { name = provinceName, province_name = provinceName, description = "Thong tin dang duoc cap nhat.", attractions = new[] { "Dang phat trien" }, famous_for = Array.Empty<string>(), travel_tags = fallbackTags, plan_tags = fallbackTags },
                message = "Chua tim thay du lieu, dang hien thi noi dung mac dinh"
            });
        }

        var tags = await GetProvincePlanTagsAsync(provinceName, province);
        province["travel_tags"] = tags;
        province["plan_tags"] = tags;
        return Ok(new { success = true, data = province, message = "Da tai thong tin tinh/thanh" });
    }

    [HttpGet("provinces/{provinceId}/with-destinations")]
    public async Task<IActionResult> GetProvinceWithDestinations(string provinceId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var data = await _travelService.GetProvinceWithDestinationsAsync(provinceId);
        if (data is null) return NotFound(new { success = false, detail = "Khong tim thay tinh/thanh" });

        if (data.TryGetValue("province", out var provinceObj) && provinceObj is Dictionary<string, object?> province)
        {
            var tags = await GetProvincePlanTagsAsync(provinceId, province);
            province["travel_tags"] = tags;
            province["plan_tags"] = tags;
        }

        return Ok(new { success = true, data, message = "Da tai tinh/thanh cung diem den" });
    }

    private async Task<List<string>> GetProvincePlanTagsAsync(string requestedName, Dictionary<string, object?>? province)
    {
        var candidateNames = new[]
        {
            province?.GetValueOrDefault("province_name")?.ToString(),
            province?.GetValueOrDefault("name")?.ToString(),
            requestedName
        }
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        var saved = await _repo.GetAllAsync(ProvinceTravelTagsCollection, limit: 100);
        var match = saved.FirstOrDefault(item => candidateNames.Any(name =>
            string.Equals(item.GetValueOrDefault("name")?.ToString(), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.GetValueOrDefault("province_name")?.ToString(), name, StringComparison.OrdinalIgnoreCase)));

        if (match is not null)
        {
            return PlanCatalog.CleanTags(PlanCatalog.ToStringList(match.GetValueOrDefault("tags")));
        }

        match = PlanCatalog.DefaultProvinceTags().FirstOrDefault(item => candidateNames.Any(name =>
            string.Equals(item.GetValueOrDefault("name")?.ToString(), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.GetValueOrDefault("province_name")?.ToString(), name, StringComparison.OrdinalIgnoreCase)));

        return match is null
            ? new List<string>()
            : PlanCatalog.CleanTags(PlanCatalog.ToStringList(match.GetValueOrDefault("tags")));
    }
}
