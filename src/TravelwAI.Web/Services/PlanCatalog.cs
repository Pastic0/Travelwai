using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TravelwAI.Web.Services;

public static class PlanCatalog
{
    public const string SeaTag = "biển";
    public const string MountainTag = "núi";
    public const string HistoricalTag = "di tích lịch sử";
    public const string RomanticTag = "thơ mộng";
    public const string EntertainmentTag = "khu vui chơi";

    public const string NormalColor = "#e5e7eb";
    public const string SeaColor = "#0ea5e9";
    public const string MountainColor = "#22c55e";
    public const string HistoricalColor = "#f97316";
    public const string ResortColor = "#a855f7";
    public const string HoneymoonColor = "#ec4899";
    public const string TeamBuildingColor = "#14b8a6";
    public const string EntertainmentColor = "#eab308";

    public static readonly IReadOnlyList<string> AllowedTags = new[]
    {
        SeaTag,
        MountainTag,
        HistoricalTag,
        RomanticTag,
        EntertainmentTag
    };

    public static List<Dictionary<string, object?>> DefaultStatusOptions() => new()
    {
        Status("binh_thuong", "Bình thường", "", Array.Empty<string>(), false, 0, NormalColor),
        Status("di_bien", "Đi biển", "", new[] { SeaTag }, false, 1, SeaColor),
        Status("len_nui", "Lên núi", "", new[] { MountainTag }, false, 2, MountainColor),
        Status("di_tich_lich_su", "Tham quan di tích lịch sử", "", new[] { HistoricalTag }, false, 3, HistoricalColor),
        Status("nghi_duong", "Nghỉ dưỡng", "", new[] { RomanticTag }, false, 4, ResortColor),
        Status("tuan_trang_mat", "Tuần trăng mật", "", new[] { RomanticTag }, false, 5, HoneymoonColor),
        Status("team_building", "Team building", "", new[] { MountainTag, SeaTag }, true, 6, TeamBuildingColor),
        Status("giai_tri", "Giải trí", "", new[] { EntertainmentTag }, false, 7, EntertainmentColor)
    };

    public static List<Dictionary<string, object?>> DefaultProvinceTags() => new()
    {
        Province(1, "Cao Bằng", "Bắc Bộ", "Trung du và Miền núi phía Bắc", new[] { MountainTag, HistoricalTag, RomanticTag }, "Thác Bản Giốc, Pác Bó, núi rừng Đông Bắc."),
        Province(2, "Điện Biên", "Bắc Bộ", "Trung du và Miền núi phía Bắc", new[] { MountainTag, HistoricalTag }, "Di tích chiến trường Điện Biên Phủ và cảnh quan Tây Bắc."),
        Province(3, "Lai Châu", "Bắc Bộ", "Trung du và Miền núi phía Bắc", new[] { MountainTag, RomanticTag }, "Đèo Ô Quy Hồ, cao nguyên Sìn Hồ, bản làng vùng cao."),
        Province(4, "Lạng Sơn", "Bắc Bộ", "Trung du và Miền núi phía Bắc", new[] { MountainTag, HistoricalTag }, "Mẫu Sơn, động Tam Thanh, thành nhà Mạc."),
        Province(5, "Lào Cai", "Bắc Bộ", "Trung du và Miền núi phía Bắc", new[] { MountainTag, RomanticTag, HistoricalTag }, "Sa Pa, Y Tý, ruộng bậc thang, văn hóa vùng cao."),
        Province(6, "Phú Thọ", "Bắc Bộ", "Trung du và Miền núi phía Bắc", new[] { MountainTag, HistoricalTag, RomanticTag }, "Đền Hùng, Hòa Bình, Tam Đảo và không gian trung du."),
        Province(7, "Sơn La", "Bắc Bộ", "Trung du và Miền núi phía Bắc", new[] { MountainTag, RomanticTag }, "Mộc Châu, Tà Xùa, đồi chè và mùa hoa."),
        Province(8, "Thái Nguyên", "Bắc Bộ", "Trung du và Miền núi phía Bắc", new[] { MountainTag, HistoricalTag }, "ATK Định Hóa, hồ Núi Cốc, vùng chè."),
        Province(9, "Tuyên Quang", "Bắc Bộ", "Trung du và Miền núi phía Bắc", new[] { MountainTag, HistoricalTag, RomanticTag }, "Hà Giang, Na Hang, Tân Trào, cao nguyên đá."),
        Province(10, "Thành phố Hà Nội", "Bắc Bộ", "Đồng bằng sông Hồng", new[] { HistoricalTag, RomanticTag, EntertainmentTag }, "Phố cổ, Hồ Gươm, di sản văn hóa và khu vui chơi đô thị."),
        Province(11, "Thành phố Hải Phòng", "Bắc Bộ", "Đồng bằng sông Hồng", new[] { SeaTag, MountainTag, HistoricalTag, EntertainmentTag }, "Cát Bà, Đồ Sơn, di tích Bạch Đằng, vui chơi ven biển."),
        Province(12, "Bắc Ninh", "Bắc Bộ", "Đồng bằng sông Hồng", new[] { HistoricalTag, RomanticTag, EntertainmentTag }, "Quan họ, chùa cổ, làng nghề và không gian văn hóa Bắc Bộ."),
        Province(13, "Hưng Yên", "Bắc Bộ", "Đồng bằng sông Hồng", new[] { HistoricalTag, RomanticTag }, "Phố Hiến, làng nghề, vùng đồng bằng yên bình."),
        Province(14, "Ninh Bình", "Bắc Bộ", "Đồng bằng sông Hồng", new[] { MountainTag, HistoricalTag, RomanticTag }, "Tràng An, Tam Cốc, Hoa Lư, cảnh quan non nước."),
        Province(15, "Quảng Ninh", "Bắc Bộ", "Đồng bằng sông Hồng", new[] { SeaTag, MountainTag, HistoricalTag, RomanticTag, EntertainmentTag }, "Hạ Long, Cô Tô, Yên Tử, Bình Liêu và vui chơi nghỉ dưỡng."),
        Province(16, "Thành phố Huế", "Trung Bộ", "Bắc Trung Bộ", new[] { SeaTag, HistoricalTag, RomanticTag }, "Cố đô Huế, sông Hương, Lăng Cô và văn hóa cung đình."),
        Province(17, "Hà Tĩnh", "Trung Bộ", "Bắc Trung Bộ", new[] { SeaTag, MountainTag, HistoricalTag, RomanticTag }, "Thiên Cầm, Ngã ba Đồng Lộc, núi Hồng Lĩnh."),
        Province(18, "Nghệ An", "Trung Bộ", "Bắc Trung Bộ", new[] { SeaTag, MountainTag, HistoricalTag, RomanticTag }, "Cửa Lò, quê Bác, Pù Mát, đảo chè Thanh Chương."),
        Province(19, "Quảng Trị", "Trung Bộ", "Bắc Trung Bộ", new[] { SeaTag, MountainTag, HistoricalTag, RomanticTag }, "Phong Nha - Kẻ Bàng, Thành cổ Quảng Trị, Cửa Tùng."),
        Province(20, "Thanh Hóa", "Trung Bộ", "Bắc Trung Bộ", new[] { SeaTag, MountainTag, HistoricalTag, RomanticTag, EntertainmentTag }, "Sầm Sơn, Pù Luông, Lam Kinh và khu nghỉ dưỡng biển."),
        Province(21, "Thành phố Đà Nẵng", "Trung Bộ", "Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { SeaTag, MountainTag, HistoricalTag, RomanticTag, EntertainmentTag }, "Biển Mỹ Khê, Bà Nà, Hội An, Ngũ Hành Sơn."),
        Province(22, "Đắk Lắk", "Trung Bộ", "Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { SeaTag, MountainTag, RomanticTag }, "Buôn Ma Thuột, hồ Lắk, cao nguyên và biển Phú Yên."),
        Province(23, "Gia Lai", "Trung Bộ", "Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { SeaTag, MountainTag, HistoricalTag, RomanticTag }, "Biển Quy Nhơn, Pleiku, Biển Hồ, văn hóa Tây Nguyên."),
        Province(24, "Khánh Hòa", "Trung Bộ", "Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { SeaTag, MountainTag, HistoricalTag, RomanticTag, EntertainmentTag }, "Nha Trang, Cam Ranh, tháp Chăm, đảo và khu vui chơi biển."),
        Province(25, "Lâm Đồng", "Trung Bộ", "Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { SeaTag, MountainTag, RomanticTag, EntertainmentTag }, "Đà Lạt, đồi thông, thác nước và biển Bình Thuận."),
        Province(26, "Quảng Ngãi", "Trung Bộ", "Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { SeaTag, MountainTag, HistoricalTag, RomanticTag }, "Lý Sơn, Sa Huỳnh, Kon Tum, Măng Đen."),
        Province(27, "Thành phố Hồ Chí Minh", "Nam Bộ", "Đông Nam Bộ", new[] { SeaTag, HistoricalTag, RomanticTag, EntertainmentTag }, "Đô thị năng động, Cần Giờ, Vũng Tàu, khu vui chơi lớn."),
        Province(28, "Đồng Nai", "Nam Bộ", "Đông Nam Bộ", new[] { MountainTag, RomanticTag, EntertainmentTag }, "Vườn quốc gia Cát Tiên, hồ Trị An, khu du lịch sinh thái."),
        Province(29, "Tây Ninh", "Nam Bộ", "Đông Nam Bộ", new[] { MountainTag, HistoricalTag, RomanticTag, EntertainmentTag }, "Núi Bà Đen, Tòa Thánh Cao Đài, hồ Dầu Tiếng."),
        Province(30, "Thành phố Cần Thơ", "Nam Bộ", "Đồng bằng sông Cửu Long", new[] { RomanticTag, HistoricalTag, EntertainmentTag }, "Chợ nổi, bến Ninh Kiều, văn hóa sông nước và vui chơi đô thị."),
        Province(31, "An Giang", "Nam Bộ", "Đồng bằng sông Cửu Long", new[] { SeaTag, MountainTag, HistoricalTag, RomanticTag, EntertainmentTag }, "Phú Quốc, Hà Tiên, Nam Du, núi Sam, Trà Sư."),
        Province(32, "Cà Mau", "Nam Bộ", "Đồng bằng sông Cửu Long", new[] { SeaTag, RomanticTag, HistoricalTag }, "Đất Mũi, rừng ngập mặn, biển và văn hóa phương Nam."),
        Province(33, "Đồng Tháp", "Nam Bộ", "Đồng bằng sông Cửu Long", new[] { HistoricalTag, RomanticTag, EntertainmentTag }, "Tràm Chim, làng hoa Sa Đéc, Gò Tháp, mùa sen."),
        Province(34, "Vĩnh Long", "Nam Bộ", "Đồng bằng sông Cửu Long", new[] { SeaTag, HistoricalTag, RomanticTag }, "Cù lao, miệt vườn, Bến Tre, Trà Vinh và biển duyên hải."),
    };

    public static Dictionary<string, object?> Status(string key, string label, string description, IEnumerable<string> tags, bool matchAll, int order, string? color = null) => new()
    {
        ["id"] = key,
        ["key"] = key,
        ["label"] = label,
        ["description"] = description,
        ["tags"] = CleanTags(tags),
        ["match_all"] = matchAll,
        ["enabled"] = true,
        ["order"] = order,
        ["color"] = NormalizeColor(color) ?? GetDefaultStatusColor(key, tags),
        ["updated_at"] = DateTime.UtcNow
    };

    public static Dictionary<string, object?> Province(int id, string name, string area, string region, IEnumerable<string> tags, string description) => new()
    {
        ["id"] = id.ToString(CultureInfo.InvariantCulture),
        ["province_id"] = id,
        ["name"] = name,
        ["province_name"] = name,
        ["area"] = area,
        ["region"] = region,
        ["tags"] = CleanTags(tags),
        ["description"] = description,
        ["updated_at"] = DateTime.UtcNow
    };

    public static string ResolveStatusColor(string? key, string? color, IEnumerable<string>? tags = null)
    {
        var normalizedKey = NormalizeKey(key);
        var normalizedColor = NormalizeColor(color);
        if (normalizedKey == "binh_thuong" && IsWhiteColor(normalizedColor)) return NormalColor;
        return normalizedColor ?? GetDefaultStatusColor(normalizedKey, tags);
    }

    public static bool IsWhiteColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return false;
        var value = color.Trim().ToLowerInvariant();
        return value is "#fff" or "#ffffff" or "white";
    }

    public static string GetDefaultStatusColor(string? key, IEnumerable<string>? tags = null)
    {
        return NormalizeKey(key) switch
        {
            "binh_thuong" => NormalColor,
            "di_bien" => SeaColor,
            "len_nui" => MountainColor,
            "di_tich_lich_su" => HistoricalColor,
            "nghi_duong" => ResortColor,
            "tuan_trang_mat" => HoneymoonColor,
            "team_building" => TeamBuildingColor,
            "giai_tri" => EntertainmentColor,
            _ => GetDefaultTagColor(CleanTags(tags).FirstOrDefault())
        };
    }

    public static string GetDefaultTagColor(string? tag)
    {
        return NormalizeKey(tag) switch
        {
            "bien" => SeaColor,
            "nui" => MountainColor,
            "di_tich_lich_su" => HistoricalColor,
            "tho_mong" => HoneymoonColor,
            "khu_vui_choi" => EntertainmentColor,
            _ => "#6366f1"
        };
    }

    public static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var value = color.Trim();
        return Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value.ToLowerInvariant() : null;
    }

    public static List<string> CleanTags(IEnumerable<string>? tags)
    {
        return (tags ?? Array.Empty<string>())
            .Select(NormalizeTag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizeTag(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        text = Regex.Replace(text, "\\s+", " ");
        foreach (var tag in AllowedTags)
        {
            if (NormalizeKey(tag) == NormalizeKey(text)) return tag;
        }
        return text;
    }

    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim().ToLowerInvariant().Replace('đ', 'd');
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) builder.Append(ch);
        }
        text = builder.ToString().Normalize(NormalizationForm.FormC);
        text = Regex.Replace(text, "[^a-z0-9]+", "_").Trim('_');
        return Regex.Replace(text, "_+", "_");
    }

    public static string ResolveStatusKey(string? keyOrLabel, IEnumerable<Dictionary<string, object?>> options)
    {
        var key = NormalizeKey(keyOrLabel);
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        foreach (var option in options)
        {
            var optionKey = option.GetValueOrDefault("key")?.ToString() ?? option.GetValueOrDefault("id")?.ToString() ?? string.Empty;
            var label = option.GetValueOrDefault("label")?.ToString() ?? string.Empty;
            if (NormalizeKey(optionKey) == key || NormalizeKey(label) == key) return optionKey;
        }
        return key;
    }

    public static bool MatchesRequiredTags(IEnumerable<string>? provinceTags, IEnumerable<string>? requiredTags, bool matchAll)
    {
        var provinceSet = CleanTags(provinceTags).Select(NormalizeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var required = CleanTags(requiredTags).Select(NormalizeKey).Where(tag => !string.IsNullOrWhiteSpace(tag)).ToList();
        if (required.Count == 0) return true;
        return matchAll ? required.All(provinceSet.Contains) : required.Any(provinceSet.Contains);
    }

    public static int GetInt(Dictionary<string, object?> value, string key, int fallback = 0)
    {
        var raw = value.GetValueOrDefault(key);
        return raw switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)Math.Round(d),
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => fallback
        };
    }

    public static bool IsEnabled(Dictionary<string, object?> value)
    {
        if (!value.TryGetValue("enabled", out var enabled) || enabled is null) return true;
        return enabled is bool b ? b : bool.TryParse(enabled.ToString(), out var parsed) ? parsed : true;
    }

    public static bool GetBool(Dictionary<string, object?> value, string key, bool fallback = false)
    {
        if (!value.TryGetValue(key, out var raw) || raw is null) return fallback;
        return raw is bool b ? b : bool.TryParse(raw.ToString(), out var parsed) ? parsed : fallback;
    }

    public static List<string> ToStringList(object? value)
    {
        if (value is null) return new List<string>();
        if (value is IEnumerable<string> strings) return strings.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (value is IEnumerable<object> objects) return objects.Select(o => o?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
        return new List<string>();
    }
}
