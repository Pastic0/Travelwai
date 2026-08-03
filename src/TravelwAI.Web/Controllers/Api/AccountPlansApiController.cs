using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class AccountPlansApiController : ApiControllerBase
{
    private readonly AccountPlanSettingsService _settings;

    public AccountPlansApiController(IAuthService authService, AccountPlanSettingsService settings) : base(authService)
    {
        _settings = settings;
    }

    [HttpGet("account-plans")]
    public async Task<IActionResult> GetAccountPlans()
    {
        var plans = await _settings.GetAsync();
        return Ok(new
        {
            success = true,
            data = plans.Select(AccountPlanSettingsService.ToResponse).ToList()
        });
    }

    [HttpPut("admin/account-plans")]
    public async Task<IActionResult> UpdateAccountPlans([FromBody] AccountPlanSettingsRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (!string.Equals(NormalizeAccountRole(current.authUser?.GetValueOrDefault("role")), "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(403, new { success = false, message = "Chỉ Admin được sửa bảng giá." });
        }

        var incoming = request?.Plans ?? new List<AccountPlanRequest>();
        var plans = incoming.Select(item => AccountPlanSettingsService.FromRequest(
            item.Role,
            item.Name,
            item.Price,
            item.MonthlyPriceAmount,
            item.Subtitle,
            item.Note,
            item.Cta,
            item.RequiresPayment ?? false,
            item.Benefits));

        var saved = await _settings.SaveAsync(plans, current.userId);
        return Ok(new
        {
            success = true,
            message = "Đã lưu bảng giá và cập nhật giá thanh toán thực tế.",
            data = saved.Select(AccountPlanSettingsService.ToResponse).ToList()
        });
    }
}

public sealed class AccountPlanSettingsRequest
{
    public List<AccountPlanRequest>? Plans { get; set; }
}

public sealed class AccountPlanRequest
{
    public string? Role { get; set; }
    public string? Name { get; set; }
    public string? Price { get; set; }
    public decimal? MonthlyPriceAmount { get; set; }
    public string? Subtitle { get; set; }
    public string? Note { get; set; }
    public string? Cta { get; set; }
    public bool? RequiresPayment { get; set; }
    public List<string>? Benefits { get; set; }
}
