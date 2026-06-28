using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class AccountPlansApiController : ApiControllerBase
{
    private const string AccountPlanSettingsCollection = "account_plan_settings";
    private const string AccountPlanSettingsDocumentId = "default";
    private readonly IDataRepository _repo;

    public AccountPlansApiController(IAuthService authService, IDataRepository repo) : base(authService)
    {
        _repo = repo;
    }

    [HttpGet("account-plans")]
    public async Task<IActionResult> GetAccountPlans()
    {
        var plans = await GetAccountPlanSettingsAsync();
        return Ok(new
        {
            success = true,
            data = plans.Select(ToAccountPlanResponse).ToList()
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

        var incoming = request.Plans ?? new List<AccountPlanRequest>();
        var plans = NormalizeAccountPlanSettings(incoming.Select(item => new AccountPlanSetting(
            NormalizePlanRole(item.Role),
            CleanText(item.Name),
            CleanText(item.Price),
            CleanText(item.Subtitle),
            CleanText(item.Note),
            CleanText(item.Cta),
            item.RequiresPayment ?? false,
            NormalizeBenefits(item.Benefits)
        )));

        await _repo.SetAsync(AccountPlanSettingsCollection, AccountPlanSettingsDocumentId, new Dictionary<string, object?>
        {
            ["plans"] = plans.Select(ToAccountPlanDictionary).ToList(),
            ["updated_at"] = DateTime.UtcNow
        }, merge: false);

        return Ok(new
        {
            success = true,
            message = "Đã lưu bảng giá",
            data = plans.Select(ToAccountPlanResponse).ToList()
        });
    }

    private async Task<List<AccountPlanSetting>> GetAccountPlanSettingsAsync()
    {
        Dictionary<string, object?>? doc = null;
        try
        {
            doc = await _repo.GetByIdAsync(AccountPlanSettingsCollection, AccountPlanSettingsDocumentId);
        }
        catch
        {
            doc = null;
        }

        if (doc?.GetValueOrDefault("plans") is IEnumerable<object?> rawPlans)
        {
            var parsed = rawPlans
                .OfType<Dictionary<string, object?>>()
                .Select(item => new AccountPlanSetting(
                    NormalizePlanRole(GetText(item, "role")),
                    CleanText(GetText(item, "name")),
                    CleanText(GetText(item, "price")),
                    CleanText(GetText(item, "subtitle")),
                    CleanText(GetText(item, "note")),
                    CleanText(GetText(item, "cta")),
                    IsTruthy(item.GetValueOrDefault("requires_payment")) || IsTruthy(item.GetValueOrDefault("requiresPayment")),
                    NormalizeBenefits(item.GetValueOrDefault("benefits"))
                ))
                .ToList();
            if (parsed.Count > 0) return NormalizeAccountPlanSettings(parsed);
        }

        return DefaultAccountPlanSettings();
    }

    private static List<AccountPlanSetting> NormalizeAccountPlanSettings(IEnumerable<AccountPlanSetting> source)
    {
        var map = source
            .Where(item => !string.IsNullOrWhiteSpace(item.Role))
            .GroupBy(item => item.Role, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        return DefaultAccountPlanSettings()
            .Select(defaultPlan => map.TryGetValue(defaultPlan.Role, out var item)
                ? new AccountPlanSetting(
                    defaultPlan.Role,
                    string.IsNullOrWhiteSpace(item.Name) ? defaultPlan.Name : item.Name,
                    string.IsNullOrWhiteSpace(item.Price) ? defaultPlan.Price : item.Price,
                    string.IsNullOrWhiteSpace(item.Subtitle) ? defaultPlan.Subtitle : item.Subtitle,
                    string.IsNullOrWhiteSpace(item.Note) ? defaultPlan.Note : item.Note,
                    string.IsNullOrWhiteSpace(item.Cta) ? defaultPlan.Cta : item.Cta,
                    item.RequiresPayment || defaultPlan.RequiresPayment,
                    NormalizeMandatoryBenefits(defaultPlan.Role, item.Benefits.Count > 0 ? item.Benefits : defaultPlan.Benefits))
                : defaultPlan with { Benefits = NormalizeMandatoryBenefits(defaultPlan.Role, defaultPlan.Benefits) })
            .ToList();
    }

    private static List<string> NormalizeMandatoryBenefits(string role, IEnumerable<string> benefits)
    {
        var list = benefits
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Where(item => !item.Equals("Không dùng chatbot AI", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (role.Equals("Free", StringComparison.OrdinalIgnoreCase)
            && !list.Any(item => item.Contains("Chatbot AI", StringComparison.OrdinalIgnoreCase)))
        {
            list.Add("Chatbot AI 3 câu hỏi trong 5 phút");
        }

        return list;
    }

    private static List<AccountPlanSetting> DefaultAccountPlanSettings() => new()
    {
        new AccountPlanSetting("Free", "Free", "0đ", "Dùng thử cơ bản", "Miễn phí", "Bắt đầu miễn phí", false, new List<string>
        {
            "Xem bản đồ Việt Nam, bài viết và tour du lịch",
            "Nhắn tin thường và xem thông báo",
            "Không dùng AI tạo bài viết",
            "Không lập lịch trình",
            "Không dùng ưu đãi bài viết",
            "Chatbot AI 3 câu hỏi trong 5 phút"
        }),
        new AccountPlanSetting("VIP", "VIP", "59.000đ", "Có AI và lịch trình", "Theo tháng", "Nâng cấp VIP", true, new List<string>
        {
            "Xem bản đồ, bài viết và tour",
            "AI tạo bài viết",
            "Lập lịch trình",
            "Không dùng ưu đãi bài viết",
            "Chatbot AI 10 câu hỏi trong 5 phút"
        }),
        new AccountPlanSetting("Premium", "Premium", "129.000đ", "Không giới hạn", "Đầy đủ", "Nâng cấp Premium", true, new List<string>
        {
            "Đầy đủ tính năng của VIP",
            "Ưu đãi bài viết",
            "Chatbot AI không giới hạn",
            "Không giới hạn AI tạo bài viết và lập lịch trình"
        }),
        new AccountPlanSetting("Sales", "Sales", "Đăng ký", "Bán tour và nhận hoa hồng", "Thu phí đăng ký", "Đăng ký Sales", true, new List<string>
        {
            "Tài khoản kinh doanh Sales",
            "Quản lý tour đã tạo",
            "Xem đơn bán tour",
            "Nhận hoa hồng theo cấp"
        }),
        new AccountPlanSetting("Business", "Business", "Đăng ký", "Đối tác tour và dịch vụ", "Thu phí đăng ký", "Đăng ký Business", true, new List<string>
        {
            "Tài khoản kinh doanh Business",
            "Quản lý tour của doanh nghiệp",
            "Xem doanh thu Business",
            "Tính phí dịch vụ theo cấp"
        })
    };

    private static object ToAccountPlanResponse(AccountPlanSetting plan) => new
    {
        role = plan.Role,
        name = plan.Name,
        price = plan.Price,
        subtitle = plan.Subtitle,
        note = plan.Note,
        cta = plan.Cta,
        requires_payment = plan.RequiresPayment,
        requiresPayment = plan.RequiresPayment,
        benefits = plan.Benefits
    };

    private static Dictionary<string, object?> ToAccountPlanDictionary(AccountPlanSetting plan) => new()
    {
        ["role"] = plan.Role,
        ["name"] = plan.Name,
        ["price"] = plan.Price,
        ["subtitle"] = plan.Subtitle,
        ["note"] = plan.Note,
        ["cta"] = plan.Cta,
        ["requires_payment"] = plan.RequiresPayment,
        ["requiresPayment"] = plan.RequiresPayment,
        ["benefits"] = plan.Benefits
    };

    private static string NormalizePlanRole(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        normalized = string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized switch
        {
            "free" or "user" => "Free",
            "vip" => "VIP",
            "premium" => "Premium",
            "sales" or "sale" or "tour sales" or "toursales" => "Sales",
            "business" or "company" => "Business",
            _ => string.Empty
        };
    }

    private static string CleanText(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static List<string> NormalizeBenefits(object? value)
    {
        if (value is IEnumerable<string> strings)
        {
            return strings.Select(CleanText).Where(item => !string.IsNullOrWhiteSpace(item)).Take(12).ToList();
        }
        if (value is IEnumerable<object?> objects)
        {
            return objects.Select(item => CleanText(item?.ToString())).Where(item => !string.IsNullOrWhiteSpace(item)).Take(12).ToList();
        }
        return CleanText(value?.ToString())
            .Split(new[] { '\n', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(CleanText)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(12)
            .ToList();
    }

    private static string GetText(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    private static bool IsTruthy(object? value) => value is bool b ? b : bool.TryParse(value?.ToString(), out var parsed) && parsed;
    private sealed record AccountPlanSetting(string Role, string Name, string Price, string Subtitle, string Note, string Cta, bool RequiresPayment, List<string> Benefits);
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
    public string? Subtitle { get; set; }
    public string? Note { get; set; }
    public string? Cta { get; set; }
    public bool? RequiresPayment { get; set; }
    public List<string>? Benefits { get; set; }
}
