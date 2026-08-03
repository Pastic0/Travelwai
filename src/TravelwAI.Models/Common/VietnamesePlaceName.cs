using System.Globalization;
using System.Text;

namespace TravelwAI.Models.Common;


public static class VietnamesePlaceName
{
    private static readonly string[] KnownNameValues =
    {

        "Cao Bằng", "Điện Biên", "Lai Châu", "Lạng Sơn", "Lào Cai", "Phú Thọ",
        "Sơn La", "Thái Nguyên", "Tuyên Quang", "Thành phố Hà Nội",
        "Thành phố Hải Phòng", "Bắc Ninh", "Hưng Yên", "Ninh Bình", "Quảng Ninh",
        "Thành phố Huế", "Hà Tĩnh", "Nghệ An", "Quảng Trị", "Thanh Hóa",
        "Thành phố Đà Nẵng", "Đắk Lắk", "Gia Lai", "Khánh Hòa", "Lâm Đồng",
        "Quảng Ngãi", "Thành phố Hồ Chí Minh", "Đồng Nai", "Tây Ninh",
        "Thành phố Cần Thơ", "An Giang", "Cà Mau", "Đồng Tháp", "Vĩnh Long",


        "Quần đảo Hoàng Sa", "Quần đảo Trường Sa",


        "Hà Nội", "Hải Phòng", "Huế", "Đà Nẵng", "Hồ Chí Minh", "Cần Thơ",
        "Yên Bái", "Hòa Bình", "Vĩnh Phúc", "Bắc Kạn", "Hà Giang", "Hải Dương",
        "Bắc Giang", "Thái Bình", "Hà Nam", "Nam Định", "Thừa Thiên Huế",
        "Quảng Bình", "Quảng Nam", "Phú Yên", "Bình Định", "Ninh Thuận",
        "Đắk Nông", "Bình Thuận", "Kon Tum", "Bình Dương", "Bà Rịa - Vũng Tàu",
        "Bình Phước", "Long An", "Sóc Trăng", "Hậu Giang", "Kiên Giang",
        "Bạc Liêu", "Tiền Giang", "Bến Tre", "Trà Vinh",


        "Thành phố Hà Nội (Thủ đô)", "TP. Hà Nội", "TP Hà Nội", "TP. Hải Phòng",
        "TP Hải Phòng", "TP. Huế", "TP Huế", "TP. Đà Nẵng", "TP Đà Nẵng",
        "TP. Hồ Chí Minh", "TP Hồ Chí Minh", "TP. HCM", "TP HCM",
        "TP. Cần Thơ", "TP Cần Thơ"
    };

    private static readonly IReadOnlyDictionary<string, string> CanonicalByName = KnownNameValues
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToDictionary(value => value, value => value, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> AllKnownNames => CanonicalByName.Values.ToArray();

    public static bool TryGetEnglishName(string? source, out string englishName)
    {
        var value = (source ?? string.Empty).Trim();
        if (value.Length == 0 || !CanonicalByName.TryGetValue(value, out var canonicalName))
        {
            englishName = string.Empty;
            return false;
        }

        englishName = ToAscii(canonicalName);
        return true;
    }

    public static string ToAscii(string? source)
    {
        var value = source ?? string.Empty;
        if (value.Length == 0) return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(character switch
            {
                'Đ' => 'D',
                'đ' => 'd',
                _ => character
            });
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
