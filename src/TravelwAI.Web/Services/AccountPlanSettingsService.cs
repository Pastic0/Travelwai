using System.Globalization;
using System.Text.RegularExpressions;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

public sealed class AccountPlanSettingsService
{
    public const string Collection = "account_plan_settings";
    public const string DocumentId = "default";

    private readonly IDataRepository _repo;

    public AccountPlanSettingsService(IDataRepository repo)
    {
        _repo = repo;
    }

    public async Task<IReadOnlyList<AccountPlanSetting>> GetAsync()
    {
        Dictionary<string, object?>? doc = null;
        try { doc = await _repo.GetByIdAsync(Collection, DocumentId); } catch { }

        if (doc?.GetValueOrDefault("plans") is IEnumerable<object?> rawPlans)
        {
            var parsed = rawPlans
                .OfType<Dictionary<string, object?>>()
                .Select(item => new AccountPlanSetting(
                    NormalizeRole(Text(item, "role")),
                    Clean(Text(item, "name")),
                    Clean(Text(item, "price")),
                    ReadAmount(item),
                    Clean(Text(item, "subtitle")),
                    Clean(Text(item, "note")),
                    Clean(Text(item, "cta")),
                    Truthy(item.GetValueOrDefault("requires_payment")) || Truthy(item.GetValueOrDefault("requiresPayment")),
                    NormalizeBenefits(item.GetValueOrDefault("benefits"))))
                .ToList();
            if (parsed.Count > 0) return Normalize(parsed);
        }

        return Defaults();
    }

    public async Task<IReadOnlyList<AccountPlanSetting>> SaveAsync(IEnumerable<AccountPlanSetting> source, string? updatedBy = null)
    {
        var plans = Normalize(source);
        var now = DateTime.UtcNow;
        await _repo.SetAsync(Collection, DocumentId, new Dictionary<string, object?>
        {
            ["plans"] = plans.Select(ToDictionary).ToList(),
            ["updated_by"] = updatedBy,
            ["updated_at"] = now,
            ["updatedAt"] = now,
            ["version"] = now.ToString("O", CultureInfo.InvariantCulture)
        }, merge: false);
        return plans;
    }

    public async Task<decimal> GetMonthlyAmountAsync(string? role)
    {
        var normalized = NormalizeRole(role);
        var plans = await GetAsync();
        return plans.FirstOrDefault(item => string.Equals(item.Role, normalized, StringComparison.OrdinalIgnoreCase))?.MonthlyPriceAmount ?? 0m;
    }

    public static AccountPlanSetting FromRequest(string? role, string? name, string? price, decimal? monthlyPriceAmount, string? subtitle, string? note, string? cta, bool requiresPayment, IEnumerable<string>? benefits)
    {
        var normalizedRole = NormalizeRole(role);
        var cleanPrice = Clean(price);
        var isMoneyRole = normalizedRole is "Free" or "VIP" or "Premium";
        var hasNumericPrice = HasNumericPrice(cleanPrice);
        var amount = monthlyPriceAmount.HasValue
            ? Math.Max(0m, decimal.Truncate(monthlyPriceAmount.Value))
            : ParseAmount(cleanPrice);
        var displayPrice = isMoneyRole || hasNumericPrice || amount > 0m
            ? FormatAmount(amount)
            : cleanPrice;

        return new AccountPlanSetting(
            normalizedRole,
            Clean(name),
            displayPrice,
            amount,
            Clean(subtitle),
            Clean(note),
            Clean(cta),
            requiresPayment,
            NormalizeBenefits(benefits));
    }

    public static object ToResponse(AccountPlanSetting plan) => new
    {
        role = plan.Role,
        name = plan.Name,
        price = plan.Price,
        monthly_price_amount = plan.MonthlyPriceAmount,
        monthlyPriceAmount = plan.MonthlyPriceAmount,
        subtitle = plan.Subtitle,
        note = plan.Note,
        cta = plan.Cta,
        requires_payment = plan.RequiresPayment,
        requiresPayment = plan.RequiresPayment,
        benefits = plan.Benefits
    };

    private static Dictionary<string, object?> ToDictionary(AccountPlanSetting plan) => new()
    {
        ["role"] = plan.Role,
        ["name"] = plan.Name,
        ["price"] = plan.Price,
        ["monthly_price_amount"] = plan.MonthlyPriceAmount,
        ["monthlyPriceAmount"] = plan.MonthlyPriceAmount,
        ["subtitle"] = plan.Subtitle,
        ["note"] = plan.Note,
        ["cta"] = plan.Cta,
        ["requires_payment"] = plan.RequiresPayment,
        ["requiresPayment"] = plan.RequiresPayment,
        ["benefits"] = plan.Benefits
    };

    private static List<AccountPlanSetting> Normalize(IEnumerable<AccountPlanSetting> source)
    {
        var map = source
            .Where(item => !string.IsNullOrWhiteSpace(item.Role))
            .GroupBy(item => item.Role, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        return Defaults().Select(fallback =>
        {
            if (!map.TryGetValue(fallback.Role, out var item)) return fallback;
            var hasExplicitPrice = !string.IsNullOrWhiteSpace(item.Price);
            var rawDisplayPrice = hasExplicitPrice ? item.Price : fallback.Price;
            var hasNumericPrice = HasNumericPrice(rawDisplayPrice);
            decimal amount;
            if (!hasExplicitPrice)
            {
                amount = fallback.MonthlyPriceAmount;
            }
            else if (hasNumericPrice)
            {
                amount = item.MonthlyPriceAmount > 0m
                    ? item.MonthlyPriceAmount
                    : ParseAmount(rawDisplayPrice);
            }
            else
            {
                amount = Math.Max(0m, item.MonthlyPriceAmount);
            }

            var displayPrice = hasNumericPrice ? FormatAmount(amount) : rawDisplayPrice;
            return new AccountPlanSetting(
                fallback.Role,
                string.IsNullOrWhiteSpace(item.Name) ? fallback.Name : item.Name,
                displayPrice,
                amount,
                item.Subtitle,
                string.IsNullOrWhiteSpace(item.Note) ? fallback.Note : item.Note,
                string.IsNullOrWhiteSpace(item.Cta) ? fallback.Cta : item.Cta,
                item.RequiresPayment,
                new List<string>(item.Benefits));
        }).ToList();
    }

    private static List<AccountPlanSetting> Defaults() => new()
    {
        new("Free", "Free", "0Đ", 0m, "Dùng thử cơ bản", "Miễn phí", "Bắt đầu miễn phí", false, new() { "AI tạo bài viết 2 lần / 10 phút", "Chatbot 3 câu / 10 phút", "Không dùng AI lập lịch trình", "Không đổi phong cách chatbot", "Không dùng ưu đãi bài viết" }),
        new("VIP", "VIP", "59.000Đ", 59000m, "Có lịch trình", "Theo tháng", "Nâng cấp VIP", true, new() { "AI tạo bài viết 5 lần / 10 phút", "Chatbot 7 câu / 10 phút", "Đổi phong cách miễn phí hoặc đã mua", "Không dùng AI lập lịch trình", "Không dùng ưu đãi bài viết" }),
        new("Premium", "Premium", "129.000Đ", 129000m, "Không giới hạn", "Đầy đủ", "Nâng cấp Premium", true, new() { "Đầy đủ tính năng của VIP", "Ưu đãi bài viết", "Không giới hạn lập lịch trình" }),
        new("Sales", "Sales", "Đăng ký", 0m, "Bán tour và nhận hoa hồng", "Thu phí đăng ký", "Đăng ký Sales", true, new() { "Tài khoản kinh doanh Sales", "Quản lý tour đã tạo", "Xem đơn bán tour", "Nhận hoa hồng theo cấp" }),
        new("Company", "Company", "Đăng ký", 0m, "Đối tác tour và dịch vụ", "Thu phí đăng ký", "Đăng ký Company", true, new() { "Tài khoản kinh doanh Company", "Quản lý tour của doanh nghiệp", "Xem doanh thu Company", "Tính phí dịch vụ theo cấp" })
    };

    public static string NormalizeRole(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        normalized = string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized switch
        {
            "free" or "user" => "Free",
            "vip" => "VIP",
            "premium" => "Premium",
            "sales" or "sale" or "tour sales" or "toursales" => "Sales",
            "business" or "company" => "Company",
            _ => string.Empty
        };
    }


    public static string FormatAmount(decimal value)
    {
        var safe = Math.Max(0m, decimal.Truncate(value));
        return string.Format(CultureInfo.GetCultureInfo("vi-VN"), "{0:N0}Đ", safe);
    }

    private static bool HasNumericPrice(string? value)
        => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "[0-9]");

    public static decimal ParseAmount(string? value)
    {
        var text = Clean(value);
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        var digits = Regex.Replace(text, "[^0-9]", string.Empty);
        return decimal.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ? amount : 0m;
    }

    private static decimal ReadAmount(Dictionary<string, object?> item)
    {
        foreach (var key in new[] { "monthly_price_amount", "monthlyPriceAmount", "price_amount", "priceAmount" })
        {
            if (item.TryGetValue(key, out var raw) && decimal.TryParse(raw?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount)) return amount;
        }
        return ParseAmount(Text(item, "price"));
    }

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Text(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    private static bool Truthy(object? value) => value is bool b ? b : bool.TryParse(value?.ToString(), out var parsed) && parsed;
    private static List<string> NormalizeBenefits(object? value)
    {
        if (value is IEnumerable<string> strings) return NormalizeBenefits(strings);
        if (value is IEnumerable<object?> objects) return objects.Select(item => Clean(item?.ToString())).Where(item => !string.IsNullOrWhiteSpace(item)).Take(12).ToList();
        return Clean(value?.ToString()).Split(new[] { '\n', ';', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(Clean).Where(item => !string.IsNullOrWhiteSpace(item)).Take(12).ToList();
    }
    private static List<string> NormalizeBenefits(IEnumerable<string>? values) => (values ?? Array.Empty<string>()).Select(Clean).Where(item => !string.IsNullOrWhiteSpace(item)).Take(12).ToList();
}

public sealed record AccountPlanSetting(
    string Role,
    string Name,
    string Price,
    decimal MonthlyPriceAmount,
    string Subtitle,
    string Note,
    string Cta,
    bool RequiresPayment,
    List<string> Benefits);
