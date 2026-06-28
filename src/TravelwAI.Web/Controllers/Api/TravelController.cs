using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class TravelController : ApiControllerBase
{
    private const string ProvinceTravelTagsCollection = "province_travel_tags";
    private const string ProvinceSearchEventsCollection = "province_search_events";
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
        await TrackProvinceSearchAsync(provinceName, current.userId, "province-api");
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

    [HttpPost("analytics/province-view")]
    public async Task<IActionResult> TrackProvinceView([FromBody] ProvinceViewTrackRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (request is null) return BadRequest(new { success = false, message = "Thiếu tên tỉnh/thành" });

        var provinceName = request.ProvinceName?.Trim();
        if (string.IsNullOrWhiteSpace(provinceName))
        {
            provinceName = request.Province?.Trim() ?? request.Name?.Trim();
        }

        if (string.IsNullOrWhiteSpace(provinceName))
        {
            return BadRequest(new { success = false, message = "Thiếu tên tỉnh/thành" });
        }

        var source = string.IsNullOrWhiteSpace(request.Source)
            ? "province-detail"
            : request.Source!.Trim();

        await TrackProvinceSearchAsync(provinceName, current.userId, source);
        return Ok(new { success = true, message = "Đã ghi nhận lượt xem tỉnh/thành" });
    }

    [HttpGet("provinces/{provinceId}/with-destinations")]
    public async Task<IActionResult> GetProvinceWithDestinations(string provinceId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        await TrackProvinceSearchAsync(provinceId, current.userId, "province-detail-api");
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

    private async Task TrackProvinceSearchAsync(string provinceName, string? userId, string source = "province-map")
    {
        var name = (provinceName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await _repo.AddAsync(ProvinceSearchEventsCollection, new Dictionary<string, object?>
            {
                ["province_name"] = name,
                ["provinceName"] = name,
                ["user_id"] = userId ?? string.Empty,
                ["userId"] = userId ?? string.Empty,
                ["source"] = source,
                ["created_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });
        }
        catch
        {
        }
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

public sealed class ProvinceViewTrackRequest
{
    public string? ProvinceName { get; set; }
    public string? Province { get; set; }
    public string? Name { get; set; }
    public string? Source { get; set; }
}
