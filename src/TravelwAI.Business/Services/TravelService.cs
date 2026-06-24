using System.Globalization;
using System.Text;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Business.Services;

public sealed class TravelService : ITravelService
{
    private readonly IDataRepository _repo;

    private static readonly Dictionary<string, (string Area, string Region)> ProvinceClassifications = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Cao Bằng"] = (@"Bắc Bộ", @"Trung du và Miền núi phía Bắc"),
        [@"Điện Biên"] = (@"Bắc Bộ", @"Trung du và Miền núi phía Bắc"),
        [@"Lai Châu"] = (@"Bắc Bộ", @"Trung du và Miền núi phía Bắc"),
        [@"Lạng Sơn"] = (@"Bắc Bộ", @"Trung du và Miền núi phía Bắc"),
        [@"Lào Cai"] = (@"Bắc Bộ", @"Trung du và Miền núi phía Bắc"),
        [@"Phú Thọ"] = (@"Bắc Bộ", @"Trung du và Miền núi phía Bắc"),
        [@"Sơn La"] = (@"Bắc Bộ", @"Trung du và Miền núi phía Bắc"),
        [@"Thái Nguyên"] = (@"Bắc Bộ", @"Trung du và Miền núi phía Bắc"),
        [@"Tuyên Quang"] = (@"Bắc Bộ", @"Trung du và Miền núi phía Bắc"),
        [@"Thành phố Hà Nội"] = (@"Bắc Bộ", @"Đồng bằng sông Hồng"),
        [@"Thành phố Hải Phòng"] = (@"Bắc Bộ", @"Đồng bằng sông Hồng"),
        [@"Bắc Ninh"] = (@"Bắc Bộ", @"Đồng bằng sông Hồng"),
        [@"Hưng Yên"] = (@"Bắc Bộ", @"Đồng bằng sông Hồng"),
        [@"Ninh Bình"] = (@"Bắc Bộ", @"Đồng bằng sông Hồng"),
        [@"Quảng Ninh"] = (@"Bắc Bộ", @"Đồng bằng sông Hồng"),
        [@"Thành phố Huế"] = (@"Trung Bộ", @"Bắc Trung Bộ"),
        [@"Hà Tĩnh"] = (@"Trung Bộ", @"Bắc Trung Bộ"),
        [@"Nghệ An"] = (@"Trung Bộ", @"Bắc Trung Bộ"),
        [@"Quảng Trị"] = (@"Trung Bộ", @"Bắc Trung Bộ"),
        [@"Thanh Hóa"] = (@"Trung Bộ", @"Bắc Trung Bộ"),
        [@"Thành phố Đà Nẵng"] = (@"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên"),
        [@"Đắk Lắk"] = (@"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên"),
        [@"Gia Lai"] = (@"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên"),
        [@"Khánh Hòa"] = (@"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên"),
        [@"Lâm Đồng"] = (@"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên"),
        [@"Quảng Ngãi"] = (@"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên"),
        [@"Thành phố Hồ Chí Minh"] = (@"Nam Bộ", @"Đông Nam Bộ"),
        [@"Đồng Nai"] = (@"Nam Bộ", @"Đông Nam Bộ"),
        [@"Tây Ninh"] = (@"Nam Bộ", @"Đông Nam Bộ"),
        [@"Thành phố Cần Thơ"] = (@"Nam Bộ", @"Đồng bằng sông Cửu Long"),
        [@"An Giang"] = (@"Nam Bộ", @"Đồng bằng sông Cửu Long"),
        [@"Cà Mau"] = (@"Nam Bộ", @"Đồng bằng sông Cửu Long"),
        [@"Đồng Tháp"] = (@"Nam Bộ", @"Đồng bằng sông Cửu Long"),
        [@"Vĩnh Long"] = (@"Nam Bộ", @"Đồng bằng sông Cửu Long"),
        [@"Quần đảo Hoàng Sa"] = (@"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên"),
        [@"Quần đảo Trường Sa"] = (@"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên"),
    };

    private static readonly Dictionary<string, List<string>> ProvinceMergedFrom = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Cao Bằng"] = new() { @"Cao Bằng" },
        [@"Điện Biên"] = new() { @"Điện Biên" },
        [@"Lai Châu"] = new() { @"Lai Châu" },
        [@"Lạng Sơn"] = new() { @"Lạng Sơn" },
        [@"Lào Cai"] = new() { @"Lào Cai", @"Yên Bái" },
        [@"Phú Thọ"] = new() { @"Hòa Bình", @"Vĩnh Phúc", @"Phú Thọ" },
        [@"Sơn La"] = new() { @"Sơn La" },
        [@"Thái Nguyên"] = new() { @"Bắc Kạn", @"Thái Nguyên" },
        [@"Tuyên Quang"] = new() { @"Hà Giang", @"Tuyên Quang" },
        [@"Thành phố Hà Nội"] = new() { @"Hà Nội" },
        [@"Thành phố Hải Phòng"] = new() { @"Hải Dương", @"Hải Phòng" },
        [@"Bắc Ninh"] = new() { @"Bắc Giang", @"Bắc Ninh" },
        [@"Hưng Yên"] = new() { @"Thái Bình", @"Hưng Yên" },
        [@"Ninh Bình"] = new() { @"Hà Nam", @"Nam Định", @"Ninh Bình" },
        [@"Quảng Ninh"] = new() { @"Quảng Ninh" },
        [@"Thành phố Huế"] = new() { @"Thừa Thiên Huế" },
        [@"Hà Tĩnh"] = new() { @"Hà Tĩnh" },
        [@"Nghệ An"] = new() { @"Nghệ An" },
        [@"Quảng Trị"] = new() { @"Quảng Bình", @"Quảng Trị" },
        [@"Thanh Hóa"] = new() { @"Thanh Hóa" },
        [@"Thành phố Đà Nẵng"] = new() { @"Quảng Nam", @"Đà Nẵng" },
        [@"Đắk Lắk"] = new() { @"Phú Yên", @"Đắk Lắk" },
        [@"Gia Lai"] = new() { @"Gia Lai", @"Bình Định" },
        [@"Khánh Hòa"] = new() { @"Khánh Hòa", @"Ninh Thuận" },
        [@"Lâm Đồng"] = new() { @"Đắk Nông", @"Lâm Đồng", @"Bình Thuận" },
        [@"Quảng Ngãi"] = new() { @"Quảng Ngãi", @"Kon Tum" },
        [@"Thành phố Hồ Chí Minh"] = new() { @"Bình Dương", @"Thành phố Hồ Chí Minh", @"Bà Rịa - Vũng Tàu" },
        [@"Đồng Nai"] = new() { @"Bình Phước", @"Đồng Nai" },
        [@"Tây Ninh"] = new() { @"Long An", @"Tây Ninh" },
        [@"Thành phố Cần Thơ"] = new() { @"Sóc Trăng", @"Hậu Giang", @"Cần Thơ" },
        [@"An Giang"] = new() { @"Kiên Giang", @"An Giang" },
        [@"Cà Mau"] = new() { @"Bạc Liêu", @"Cà Mau" },
        [@"Đồng Tháp"] = new() { @"Tiền Giang", @"Đồng Tháp" },
        [@"Vĩnh Long"] = new() { @"Bến Tre", @"Vĩnh Long", @"Trà Vinh" },
        [@"Quần đảo Hoàng Sa"] = new(),
        [@"Quần đảo Trường Sa"] = new(),
    };

    private static readonly Dictionary<string, (string NaturalAreaKm2, string Population)> ProvinceStats = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Cao Bằng"] = (@"6.700,39", @"573.119"),
        [@"Điện Biên"] = (@"9.539,93", @"673.091"),
        [@"Lai Châu"] = (@"9.068,73", @"512.601"),
        [@"Lạng Sơn"] = (@"8.310,18", @"881.384"),
        [@"Lào Cai"] = (@"13.257", @"1.778.785"),
        [@"Phú Thọ"] = (@"9.361,4", @"4.022.638"),
        [@"Sơn La"] = (@"14.109,83", @"1.404.587"),
        [@"Thái Nguyên"] = (@"8.375,3", @"1.799.489"),
        [@"Tuyên Quang"] = (@"13.795,6", @"1.865.270"),
        [@"Thành phố Hà Nội"] = (@"3.359,84", @"8.807.523"),
        [@"Thành phố Hải Phòng"] = (@"3.194,7", @"4.664.124"),
        [@"Bắc Ninh"] = (@"4.718,6", @"3.619.433"),
        [@"Hưng Yên"] = (@"2.514,8", @"3.567.943"),
        [@"Ninh Bình"] = (@"3.942,6", @"4.412.264"),
        [@"Quảng Ninh"] = (@"6.207,93", @"1.497.447"),
        [@"Thành phố Huế"] = (@"4.947,11", @"1.432.986"),
        [@"Hà Tĩnh"] = (@"5.994,45", @"1.622.901"),
        [@"Nghệ An"] = (@"16.486,49", @"3.831.694"),
        [@"Quảng Trị"] = (@"12.700", @"1.870.845"),
        [@"Thanh Hóa"] = (@"11.114,71", @"4.324.783"),
        [@"Thành phố Đà Nẵng"] = (@"11.859,6", @"3.065.628"),
        [@"Đắk Lắk"] = (@"18.096,4", @"3.346.853"),
        [@"Gia Lai"] = (@"21.576,5", @"3.583.693"),
        [@"Khánh Hòa"] = (@"8.555,9", @"2.243.554"),
        [@"Lâm Đồng"] = (@"24.233,1", @"3.872.999"),
        [@"Quảng Ngãi"] = (@"14.832,6", @"2.161.755"),
        [@"Thành phố Hồ Chí Minh"] = (@"6.772,6", @"14.002.598"),
        [@"Đồng Nai"] = (@"12.737,2", @"4.491.408"),
        [@"Tây Ninh"] = (@"8.536,5", @"3.254.170"),
        [@"Thành phố Cần Thơ"] = (@"6.360,8", @"4.199.824"),
        [@"An Giang"] = (@"9.888,9", @"4.952.238"),
        [@"Cà Mau"] = (@"7.942,4", @"2.606.672"),
        [@"Đồng Tháp"] = (@"5.938,7", @"4.370.046"),
        [@"Vĩnh Long"] = (@"6.296,2", @"4.257.581"),
        [@"Quần đảo Hoàng Sa"] = (string.Empty, string.Empty),
        [@"Quần đảo Trường Sa"] = (string.Empty, string.Empty),
    };

    private static readonly Dictionary<string, string> ArchipelagoBelongsTo = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Quần đảo Hoàng Sa"] = @"Thành phố Đà Nẵng",
        [@"Quần đảo Trường Sa"] = @"Khánh Hòa",
    };

    private static readonly Dictionary<string, string> ArchipelagoNotes = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> ProvinceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Cao Bằng"] = @"Cao Bằng",
        [@"Điện Biên"] = @"Điện Biên",
        [@"Lai Châu"] = @"Lai Châu",
        [@"Lạng Sơn"] = @"Lạng Sơn",
        [@"Lào Cai"] = @"Lào Cai",
        [@"Yên Bái"] = @"Lào Cai",
        [@"Phú Thọ"] = @"Phú Thọ",
        [@"Hòa Bình"] = @"Phú Thọ",
        [@"Vĩnh Phúc"] = @"Phú Thọ",
        [@"Sơn La"] = @"Sơn La",
        [@"Thái Nguyên"] = @"Thái Nguyên",
        [@"Bắc Kạn"] = @"Thái Nguyên",
        [@"Tuyên Quang"] = @"Tuyên Quang",
        [@"Hà Giang"] = @"Tuyên Quang",
        [@"Thành phố Hà Nội"] = @"Thành phố Hà Nội",
        [@"Hà Nội"] = @"Thành phố Hà Nội",
        [@"TP. Hà Nội"] = @"Thành phố Hà Nội",
        [@"Thủ đô"] = @"Thành phố Hà Nội",
        [@"Thủ đô Hà Nội"] = @"Thành phố Hà Nội",
        [@"Thành phố Hải Phòng"] = @"Thành phố Hải Phòng",
        [@"Hải Dương"] = @"Thành phố Hải Phòng",
        [@"Hải Phòng"] = @"Thành phố Hải Phòng",
        [@"TP. Hải Phòng"] = @"Thành phố Hải Phòng",
        [@"Bắc Ninh"] = @"Bắc Ninh",
        [@"Bắc Giang"] = @"Bắc Ninh",
        [@"Hưng Yên"] = @"Hưng Yên",
        [@"Thái Bình"] = @"Hưng Yên",
        [@"Ninh Bình"] = @"Ninh Bình",
        [@"Hà Nam"] = @"Ninh Bình",
        [@"Nam Định"] = @"Ninh Bình",
        [@"Quảng Ninh"] = @"Quảng Ninh",
        [@"Thành phố Huế"] = @"Thành phố Huế",
        [@"Thừa Thiên Huế"] = @"Thành phố Huế",
        [@"Thừa Thiên-Huế"] = @"Thành phố Huế",
        [@"Thừa Thiên–Huế"] = @"Thành phố Huế",
        [@"Huế"] = @"Thành phố Huế",
        [@"TP. Huế"] = @"Thành phố Huế",
        [@"Hà Tĩnh"] = @"Hà Tĩnh",
        [@"Nghệ An"] = @"Nghệ An",
        [@"Quảng Trị"] = @"Quảng Trị",
        [@"Quảng Bình"] = @"Quảng Trị",
        [@"Thanh Hóa"] = @"Thanh Hóa",
        [@"Thành phố Đà Nẵng"] = @"Thành phố Đà Nẵng",
        [@"Quảng Nam"] = @"Thành phố Đà Nẵng",
        [@"Đà Nẵng"] = @"Thành phố Đà Nẵng",
        [@"TP. Đà Nẵng"] = @"Thành phố Đà Nẵng",
        [@"Hoàng Sa"] = @"Quần đảo Hoàng Sa",
        [@"Quần đảo Hoàng Sa"] = @"Quần đảo Hoàng Sa",
        [@"Đắk Lắk"] = @"Đắk Lắk",
        [@"Phú Yên"] = @"Đắk Lắk",
        [@"Gia Lai"] = @"Gia Lai",
        [@"Bình Định"] = @"Gia Lai",
        [@"Khánh Hòa"] = @"Khánh Hòa",
        [@"Ninh Thuận"] = @"Khánh Hòa",
        [@"Trường Sa"] = @"Quần đảo Trường Sa",
        [@"Quần đảo Trường Sa"] = @"Quần đảo Trường Sa",
        [@"Lâm Đồng"] = @"Lâm Đồng",
        [@"Đắk Nông"] = @"Lâm Đồng",
        [@"Bình Thuận"] = @"Lâm Đồng",
        [@"Quảng Ngãi"] = @"Quảng Ngãi",
        [@"Kon Tum"] = @"Quảng Ngãi",
        [@"Thành phố Hồ Chí Minh"] = @"Thành phố Hồ Chí Minh",
        [@"Bình Dương"] = @"Thành phố Hồ Chí Minh",
        [@"Bà Rịa - Vũng Tàu"] = @"Thành phố Hồ Chí Minh",
        [@"Hồ Chí Minh"] = @"Thành phố Hồ Chí Minh",
        [@"TP. Hồ Chí Minh"] = @"Thành phố Hồ Chí Minh",
        [@"TP HCM"] = @"Thành phố Hồ Chí Minh",
        [@"TPHCM"] = @"Thành phố Hồ Chí Minh",
        [@"Sài Gòn"] = @"Thành phố Hồ Chí Minh",
        [@"Bà Rịa–Vũng Tàu"] = @"Thành phố Hồ Chí Minh",
        [@"Đồng Nai"] = @"Đồng Nai",
        [@"Bình Phước"] = @"Đồng Nai",
        [@"Tây Ninh"] = @"Tây Ninh",
        [@"Long An"] = @"Tây Ninh",
        [@"Thành phố Cần Thơ"] = @"Thành phố Cần Thơ",
        [@"Sóc Trăng"] = @"Thành phố Cần Thơ",
        [@"Hậu Giang"] = @"Thành phố Cần Thơ",
        [@"Cần Thơ"] = @"Thành phố Cần Thơ",
        [@"TP. Cần Thơ"] = @"Thành phố Cần Thơ",
        [@"An Giang"] = @"An Giang",
        [@"Kiên Giang"] = @"An Giang",
        [@"Cà Mau"] = @"Cà Mau",
        [@"Bạc Liêu"] = @"Cà Mau",
        [@"Đồng Tháp"] = @"Đồng Tháp",
        [@"Tiền Giang"] = @"Đồng Tháp",
        [@"Vĩnh Long"] = @"Vĩnh Long",
        [@"Bến Tre"] = @"Vĩnh Long",
        [@"Trà Vinh"] = @"Vĩnh Long",
        [@"TP Hồ Chí Minh"] = @"Thành phố Hồ Chí Minh",
        [@"TP. HCM"] = @"Thành phố Hồ Chí Minh",
        [@"Thành phố HCM"] = @"Thành phố Hồ Chí Minh",
    };

    public TravelService(IDataRepository repo)
    {
        _repo = repo;
    }

    public async Task<Dictionary<string, object?>?> GetProvinceByNameAsync(string provinceName)
    {
        var canonicalName = FindCanonicalProvinceName(provinceName) ?? provinceName;
        var province = await _repo.GetByIdAsync("provinces", canonicalName, includeId: false)
                       ?? await _repo.GetByIdAsync("provinces", CreateKey(canonicalName), includeId: false)
                       ?? await _repo.GetByIdAsync("provinces", provinceName, includeId: false)
                       ?? await _repo.GetByIdAsync("provinces", CreateKey(provinceName), includeId: false);

        if (province is not null) return NormalizeProvince(canonicalName, province);
        return CreateProvinceFallback(canonicalName);
    }

    public async Task<Dictionary<string, object?>?> GetProvinceWithDestinationsAsync(string provinceId)
    {
        var canonicalName = FindCanonicalProvinceName(provinceId) ?? provinceId;
        var province = await _repo.GetByIdAsync("provinces", canonicalName, includeId: true)
                       ?? await _repo.GetByIdAsync("provinces", CreateKey(canonicalName), includeId: true)
                       ?? await _repo.GetByIdAsync("provinces", provinceId, includeId: true)
                       ?? await _repo.GetByIdAsync("provinces", CreateKey(provinceId), includeId: true);
        if (province is null) return CreateProvinceWithDestinationsFallback(canonicalName);
        var normalized = NormalizeProvince(canonicalName, province);
        var destinations = ToDestinationList(province.GetValueOrDefault("destinations"));
        if (destinations.Count == 0) destinations = CreateFallbackDestinations(normalized.GetValueOrDefault("province_name")?.ToString() ?? canonicalName);
        return new Dictionary<string, object?>
        {
            ["province"] = normalized,
            ["destinations"] = destinations,
            ["total_destinations"] = destinations.Count
        };
    }

    private static Dictionary<string, object?> NormalizeProvince(string requestedName, Dictionary<string, object?> province)
    {
        var rawName = province.GetValueOrDefault("province_name")?.ToString()
                   ?? province.GetValueOrDefault("name")?.ToString()
                   ?? requestedName;
        var name = FindCanonicalProvinceName(rawName) ?? FindCanonicalProvinceName(requestedName) ?? rawName;
        var area = GetArea(name);
        var region = GetRegion(name);
        var mergedFrom = GetMergedFrom(name);
        var stats = GetStats(name);

        province["name"] = name;
        province["province_name"] = name;
        province["area"] = area;
        province["region"] = region;
        province["subregion"] = region;
        province["classification"] = $"{area} - {region}";
        province["merged_from"] = mergedFrom;
        province["natural_area_km2"] = stats.NaturalAreaKm2;
        province["population"] = stats.Population;
        var isArchipelago = IsArchipelago(name);
        province["is_archipelago"] = isArchipelago;
        if (isArchipelago)
        {
            province["belongs_to"] = GetBelongsTo(name);
            province["administrative_note"] = GetAdministrativeNote(name);
        }
        province["description"] = BuildProvinceDescription(name, area, region, mergedFrom, stats.NaturalAreaKm2, stats.Population);
        if (!province.ContainsKey("famous_for")) province["famous_for"] = GetExperiences(area, region);
        return province;
    }

    private static Dictionary<string, object?>? CreateProvinceFallback(string provinceName)
    {
        var canonical = FindCanonicalProvinceName(provinceName);
        if (canonical is null) return null;
        var area = GetArea(canonical);
        var region = GetRegion(canonical);
        var mergedFrom = GetMergedFrom(canonical);
        var stats = GetStats(canonical);
        return new Dictionary<string, object?>
        {
            ["name"] = canonical,
            ["province_name"] = canonical,
            ["area"] = area,
            ["region"] = region,
            ["subregion"] = region,
            ["classification"] = $"{area} - {region}",
            ["merged_from"] = mergedFrom,
            ["natural_area_km2"] = stats.NaturalAreaKm2,
            ["population"] = stats.Population,
            ["is_archipelago"] = IsArchipelago(canonical),
            ["belongs_to"] = GetBelongsTo(canonical),
            ["administrative_note"] = GetAdministrativeNote(canonical),
            ["description"] = BuildProvinceDescription(canonical, area, region, mergedFrom, stats.NaturalAreaKm2, stats.Population),
            ["famous_for"] = GetExperiences(area, region),
            ["destinations"] = CreateFallbackDestinations(canonical)
        };
    }

    private static Dictionary<string, object?>? CreateProvinceWithDestinationsFallback(string provinceName)
    {
        var province = CreateProvinceFallback(provinceName);
        if (province is null) return null;
        var destinations = CreateFallbackDestinations(province.GetValueOrDefault("province_name")?.ToString() ?? provinceName);
        return new Dictionary<string, object?>
        {
            ["province"] = province,
            ["destinations"] = destinations,
            ["total_destinations"] = destinations.Count
        };
    }

    private static readonly Dictionary<string, string> ProvinceDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Thành phố Hà Nội"] = @"Hà Nội là điểm đến kết hợp giữa lịch sử, văn hóa và nhịp sống hiện đại. Du khách có thể khám phá phố cổ, Hồ Gươm, Văn Miếu, Hoàng thành Thăng Long, làng nghề truyền thống, ẩm thực đường phố và những không gian vui chơi, mua sắm sôi động.",
        [@"Thành phố Huế"] = @"Huế mang vẻ đẹp trầm mặc của cố đô với kinh thành, lăng tẩm triều Nguyễn, chùa Thiên Mụ và sông Hương thơ mộng. Du khách còn có thể thưởng thức nhã nhạc cung đình, ẩm thực Huế tinh tế, biển Lăng Cô và không gian văn hóa rất riêng.",
        [@"Lai Châu"] = @"Lai Châu hấp dẫn với cảnh núi rừng Tây Bắc hùng vĩ, đèo Ô Quy Hồ, cao nguyên Sìn Hồ, bản làng dân tộc và những thửa ruộng bậc thang xanh mướt. Đây là điểm đến phù hợp cho du khách thích khám phá thiên nhiên, văn hóa vùng cao.",
        [@"Điện Biên"] = @"Điện Biên nổi bật với dấu ấn lịch sử hào hùng qua chiến trường Điện Biên Phủ, hầm Đờ Cát, đồi A1 và bảo tàng chiến thắng. Ngoài ra, nơi đây còn có cánh đồng Mường Thanh, A Pa Chải và cảnh quan núi rừng Tây Bắc hoang sơ.",
        [@"Sơn La"] = @"Sơn La cuốn hút bởi Mộc Châu xanh mát, Tà Xùa mây phủ, đồi chè, thác Dải Yếm và những bản làng yên bình. Du khách có thể trải nghiệm khí hậu trong lành, văn hóa dân tộc đặc sắc và các mùa hoa rực rỡ quanh năm.",
        [@"Lạng Sơn"] = @"Lạng Sơn là vùng đất biên cương có nhiều danh thắng như động Tam Thanh, núi Tô Thị, thành nhà Mạc và khu du lịch Mẫu Sơn. Du khách còn có thể khám phá chợ vùng biên, thưởng thức vịt quay, khâu nhục và không khí núi rừng mát lành.",
        [@"Quảng Ninh"] = @"Quảng Ninh nổi tiếng với vịnh Hạ Long, Yên Tử, Cô Tô, Quan Lạn và Bình Liêu. Nơi đây kết hợp hài hòa giữa biển đảo, núi non, du lịch tâm linh, nghỉ dưỡng cao cấp và những trải nghiệm khám phá thiên nhiên kỳ vĩ bậc nhất miền Bắc.",
        [@"Thanh Hóa"] = @"Thanh Hóa có nhiều điểm đến hấp dẫn như biển Sầm Sơn, Pù Luông, thành nhà Hồ, Lam Kinh và suối cá Cẩm Lương. Du khách có thể trải nghiệm biển, núi, rừng, di sản lịch sử và văn hóa xứ Thanh trong cùng một hành trình.",
        [@"Nghệ An"] = @"Nghệ An là vùng đất giàu truyền thống với quê Bác ở Nam Đàn, biển Cửa Lò, đảo Lan Châu, vườn quốc gia Pù Mát và đồi chè Thanh Chương. Du khách đến đây có thể cảm nhận vẻ đẹp mộc mạc, chân tình và đậm bản sắc xứ Nghệ.",
        [@"Hà Tĩnh"] = @"Hà Tĩnh mang vẻ đẹp yên bình với biển Thiên Cầm, chùa Hương Tích, hồ Kẻ Gỗ và Ngã ba Đồng Lộc. Nơi đây phù hợp cho những hành trình kết hợp nghỉ dưỡng, tìm hiểu lịch sử, thưởng thức dân ca ví giặm và khám phá thiên nhiên miền Trung.",
        [@"Cao Bằng"] = @"Cao Bằng nổi bật với thác Bản Giốc, suối Lê Nin, hang Pác Bó, hồ Thang Hen và công viên địa chất Non Nước. Du khách sẽ được chiêm ngưỡng cảnh quan núi đá vôi hùng vĩ, làng bản thanh bình và văn hóa dân tộc đặc sắc.",
        [@"Tuyên Quang"] = @"Tuyên Quang hấp dẫn với Tân Trào, Na Hang, Lâm Bình, cao nguyên đá Đồng Văn, đèo Mã Pì Lèng và sông Nho Quế. Đây là điểm đến lý tưởng để khám phá thiên nhiên Đông Bắc, lịch sử cách mạng, văn hóa bản địa và cảnh quan núi non kỳ vĩ.",
        [@"Lào Cai"] = @"Lào Cai cuốn hút với Sa Pa, Fansipan, Bắc Hà, Mù Cang Chải, hồ Thác Bà và những thửa ruộng bậc thang tuyệt đẹp. Du khách có thể săn mây, đi chợ phiên, khám phá bản làng vùng cao và tận hưởng khí hậu mát mẻ quanh năm.",
        [@"Thái Nguyên"] = @"Thái Nguyên nổi bật với đồi chè Tân Cương, hồ Núi Cốc, ATK Định Hóa, hồ Ba Bể và những khu sinh thái xanh mát. Nơi đây thích hợp cho du lịch nghỉ dưỡng ngắn ngày, khám phá thiên nhiên, văn hóa trà và lịch sử cách mạng.",
        [@"Phú Thọ"] = @"Phú Thọ là vùng đất cội nguồn với Đền Hùng, hát xoan, Tam Đảo, Mai Châu, hồ Hòa Bình và suối khoáng thư giãn. Du khách có thể kết hợp du lịch tâm linh, nghỉ dưỡng, khám phá bản làng dân tộc và cảnh quan trung du, miền núi.",
        [@"Bắc Ninh"] = @"Bắc Ninh hấp dẫn với dân ca quan họ, chùa Dâu, chùa Bút Tháp, làng tranh Đông Hồ, Tây Yên Tử và mùa vải thiều. Đây là điểm đến giàu bản sắc văn hóa Kinh Bắc, phù hợp cho hành trình tìm hiểu lễ hội, làng nghề và di sản.",
        [@"Hưng Yên"] = @"Hưng Yên mang vẻ đẹp cổ kính và yên bình với phố Hiến, làng Nôm, chùa Chuông, chùa Keo, biển Đồng Châu và vườn nhãn. Du khách có thể trải nghiệm không gian làng quê Bắc Bộ, thưởng thức đặc sản địa phương và tìm hiểu văn hóa truyền thống.",
        [@"Thành phố Hải Phòng"] = @"Hải Phòng là thành phố cảng sôi động với Cát Bà, vịnh Lan Hạ, Đồ Sơn, Côn Sơn, Kiếp Bạc và ẩm thực đường phố hấp dẫn. Du khách có thể tắm biển, đi đảo, leo núi, khám phá di tích lịch sử và thưởng thức bánh đa cua nổi tiếng.",
        [@"Ninh Bình"] = @"Ninh Bình quyến rũ với Tràng An, Tam Cốc, cố đô Hoa Lư, chùa Bái Đính, Tam Chúc, Phủ Dầy và biển Thịnh Long. Nơi đây nổi bật bởi cảnh quan núi đá vôi, sông nước thơ mộng, di sản văn hóa và không gian tâm linh thanh tịnh.",
        [@"Quảng Trị"] = @"Quảng Trị là điểm đến giàu cảm xúc với thành cổ Quảng Trị, cầu Hiền Lương, địa đạo Vịnh Mốc, Phong Nha, Sơn Đoòng và biển Nhật Lệ. Du khách có thể kết hợp hành trình tri ân lịch sử, khám phá hang động kỳ vĩ và nghỉ dưỡng ven biển.",
        [@"Thành phố Đà Nẵng"] = @"Đà Nẵng thu hút với biển Mỹ Khê, bán đảo Sơn Trà, Bà Nà Hills, phố cổ Hội An, thánh địa Mỹ Sơn và Cù Lao Chàm. Thành phố mang đến trải nghiệm đa dạng từ nghỉ dưỡng biển, khám phá di sản, ẩm thực xứ Quảng đến vui chơi hiện đại.",
        [@"Quảng Ngãi"] = @"Quảng Ngãi có vẻ đẹp giao hòa giữa biển đảo và núi rừng với đảo Lý Sơn, Sa Huỳnh, Măng Đen, thác Pa Sỹ và văn hóa cồng chiêng. Du khách có thể khám phá cảnh quan hoang sơ, dấu tích lịch sử và đời sống bản địa giàu bản sắc.",
        [@"Gia Lai"] = @"Gia Lai hấp dẫn với Biển Hồ, núi lửa Chư Đăng Ya, không gian cồng chiêng, Quy Nhơn, Kỳ Co, Eo Gió và tháp Chăm. Đây là điểm đến độc đáo khi kết hợp vẻ đẹp đại ngàn Tây Nguyên với biển xanh miền Trung.",
        [@"Khánh Hòa"] = @"Khánh Hòa nổi tiếng với vịnh Nha Trang, Cam Ranh, đảo Bình Ba, Vĩnh Hy, Ninh Chữ và tháp Chăm cổ kính. Du khách có thể tận hưởng biển xanh, cát trắng, khu nghỉ dưỡng hiện đại, hoạt động lặn ngắm san hô và ẩm thực hải sản phong phú.",
        [@"Lâm Đồng"] = @"Lâm Đồng cuốn hút với Đà Lạt mộng mơ, hồ Tà Đùng, thác Pongour, Mũi Né, Bàu Trắng và đồi cát ven biển. Nơi đây kết hợp khí hậu cao nguyên mát mẻ, cảnh quan rừng thông, cà phê, hoa và những trải nghiệm nghỉ dưỡng lãng mạn.",
        [@"Đắk Lắk"] = @"Đắk Lắk nổi bật với Buôn Đôn, hồ Lắk, thác Dray Nur, văn hóa Ê Đê, Gành Đá Đĩa, Mũi Điện và biển Phú Yên. Du khách có thể khám phá đại ngàn Tây Nguyên, thưởng thức cà phê, trải nghiệm cồng chiêng và tận hưởng biển xanh.",
        [@"Thành phố Hồ Chí Minh"] = @"Thành phố Hồ Chí Minh là trung tâm du lịch sôi động với phố thị hiện đại, chợ Bến Thành, địa đạo Củ Chi, Vũng Tàu, Hồ Tràm và Côn Đảo. Du khách có thể trải nghiệm lịch sử, mua sắm, ẩm thực đa vùng và nghỉ dưỡng biển đảo.",
        [@"Đồng Nai"] = @"Đồng Nai thu hút với rừng Nam Cát Tiên, hồ Trị An, thác Giang Điền, núi Bà Rá và các khu du lịch sinh thái. Đây là điểm đến phù hợp cho dã ngoại cuối tuần, cắm trại, khám phá thiên nhiên và nghỉ dưỡng gần đô thị.",
        [@"Tây Ninh"] = @"Tây Ninh nổi bật với núi Bà Đen, Tòa Thánh Cao Đài, hồ Dầu Tiếng, làng nổi Tân Lập và cảnh quan Đồng Tháp Mười. Du khách có thể kết hợp du lịch tâm linh, leo núi, sinh thái, khám phá vùng biên và thưởng thức đặc sản địa phương.",
        [@"Thành phố Cần Thơ"] = @"Cần Thơ quyến rũ với chợ nổi Cái Răng, bến Ninh Kiều, vườn trái cây, chùa Dơi, chùa Som Rong và văn hóa Khmer Nam Bộ. Đây là điểm đến lý tưởng để trải nghiệm sông nước miền Tây, ẩm thực dân dã và cuộc sống miệt vườn.",
        [@"Vĩnh Long"] = @"Vĩnh Long mang vẻ đẹp miệt vườn với cù lao An Bình, làng gốm, chùa Khmer, cồn Phụng, vườn dừa và những homestay ven sông. Du khách có thể đi thuyền, thưởng thức trái cây, nghe đờn ca tài tử và cảm nhận nhịp sống miền Tây.",
        [@"Đồng Tháp"] = @"Đồng Tháp hấp dẫn với Tràm Chim, làng hoa Sa Đéc, Gò Tháp, chợ nổi Cái Bè và những cánh đồng sen bát ngát. Du khách sẽ được tận hưởng không gian sông nước thanh bình, ẩm thực dân dã và vẻ đẹp mộc mạc của miền Tây.",
        [@"Cà Mau"] = @"Cà Mau là điểm cuối Tổ quốc với Đất Mũi, rừng U Minh Hạ, sân chim, nhà Công tử Bạc Liêu, điện gió và hệ sinh thái ngập mặn. Du khách có thể khám phá rừng, biển, văn hóa phương Nam và thưởng thức hải sản tươi ngon.",
        [@"An Giang"] = @"An Giang cuốn hút với núi Sam, miếu Bà Chúa Xứ, rừng tràm Trà Sư, Châu Đốc, Phú Quốc, Hà Tiên và Nam Du. Du khách có thể trải nghiệm du lịch tâm linh, sinh thái, biển đảo, văn hóa biên giới và ẩm thực miền Tây đặc sắc."
    };

    private static string BuildProvinceDescription(string name, string area, string region, List<string> mergedFrom, string naturalAreaKm2, string population)
    {
        if (ProvinceDescriptions.TryGetValue(name, out var customDescription))
        {
            return customDescription;
        }

        if (IsArchipelago(name))
        {
            return $"{name} là quần đảo của Việt Nam. Khu vực này phù hợp để tìm hiểu địa lý, lịch sử và chủ quyền biển đảo Việt Nam.";
        }

        var capitalText = name == "Thành phố Hà Nội" ? " Đây là Thủ đô của Việt Nam." : string.Empty;
        var statsText = !string.IsNullOrWhiteSpace(naturalAreaKm2) && !string.IsNullOrWhiteSpace(population)
            ? $" Diện tích khoảng {naturalAreaKm2} km², dân số khoảng {population} người."
            : string.Empty;
        return $"{name} thuộc khu vực {area}, vùng {region}.{capitalText}{statsText}";
    }

    private static List<string> GetExperiences(string area, string region) => region switch
    {
        "Trung du và Miền núi phía Bắc" => new() { "núi rừng và ruộng bậc thang", "di tích lịch sử - văn hóa", "ẩm thực địa phương" },
        "Đồng bằng sông Hồng" => new() { "đô thị lịch sử", "di sản văn hóa", "ẩm thực Bắc Bộ" },
        "Bắc Trung Bộ" => new() { "di sản cố đô", "biển Trung Bộ", "ẩm thực Trung Bộ" },
        "Duyên hải Nam Trung Bộ và Tây Nguyên" => new() { "biển đảo - cao nguyên", "thác nước và vịnh biển", "văn hóa bản địa" },
        "Đông Nam Bộ" => new() { "đô thị năng động", "khu vui chơi - sinh thái", "ẩm thực Nam Bộ" },
        "Đồng bằng sông Cửu Long" => new() { "sông nước miệt vườn", "chợ nổi và làng nghề", "ẩm thực miền Tây" },
        _ => area switch
        {
            "Bắc Bộ" => new() { "núi rừng - đô thị lịch sử", "di tích lịch sử - văn hóa", "ẩm thực Bắc Bộ" },
            "Trung Bộ" => new() { "biển đảo và cung đường ven biển", "di sản - đền đài - phố cổ", "ẩm thực Trung Bộ" },
            "Nam Bộ" => new() { "sông nước miệt vườn", "chợ nổi và làng nghề", "ẩm thực Nam Bộ" },
            _ => new() { "thiên nhiên", "văn hóa", "ẩm thực" }
        }
    };

    private static List<Dictionary<string, object?>> CreateFallbackDestinations(string provinceName) => new()
    {
        new Dictionary<string, object?>
        {
            ["location_name"] = $"Trung tâm {provinceName}",
            ["location_description"] = "Khu vực thuận tiện để bắt đầu hành trình, ăn uống, nghỉ ngơi và tìm hiểu nhịp sống địa phương.",
            ["location_type"] = "Trung tâm",
            ["location_open_time"] = "Cả ngày",
            ["location_address"] = provinceName,
            ["location_images"] = new List<string>()
        },
        new Dictionary<string, object?>
        {
            ["location_name"] = $"Điểm check-in tại {provinceName}",
            ["location_description"] = "Gợi ý tham quan các địa điểm nổi tiếng, cảnh quan thiên nhiên, di tích hoặc khu vui chơi phù hợp với du khách.",
            ["location_type"] = "Tham quan",
            ["location_open_time"] = "08:00 - 17:00",
            ["location_address"] = provinceName,
            ["location_images"] = new List<string>()
        },
        new Dictionary<string, object?>
        {
            ["location_name"] = "Khu ẩm thực và trải nghiệm địa phương",
            ["location_description"] = "Khám phá món ăn đặc sản, chợ địa phương, quán ăn và các hoạt động văn hóa đặc trưng.",
            ["location_type"] = "Ẩm thực",
            ["location_open_time"] = "09:00 - 22:00",
            ["location_address"] = provinceName,
            ["location_images"] = new List<string>()
        }
    };

    private static string? FindCanonicalProvinceName(string provinceName)
    {
        if (string.IsNullOrWhiteSpace(provinceName)) return null;
        if (ProvinceAliases.TryGetValue(provinceName, out var directAlias)) return directAlias;

        var key = NormalizeText(provinceName);
        foreach (var alias in ProvinceAliases)
        {
            var aliasKey = NormalizeText(alias.Key);
            if (aliasKey == key || aliasKey.Contains(key) || key.Contains(aliasKey))
            {
                return alias.Value;
            }
        }

        return ProvinceClassifications.Keys.FirstOrDefault(name => NormalizeText(name) == key)
            ?? ProvinceClassifications.Keys.FirstOrDefault(name => NormalizeText(name).Contains(key) || key.Contains(NormalizeText(name)));
    }

    private static string GetArea(string provinceName)
    {
        var canonical = FindCanonicalProvinceName(provinceName) ?? provinceName;
        return ProvinceClassifications.TryGetValue(canonical, out var value) ? value.Area : "Việt Nam";
    }

    private static string GetRegion(string provinceName)
    {
        var canonical = FindCanonicalProvinceName(provinceName) ?? provinceName;
        return ProvinceClassifications.TryGetValue(canonical, out var value) ? value.Region : "Chưa phân loại";
    }

    private static List<string> GetMergedFrom(string provinceName)
    {
        var canonical = FindCanonicalProvinceName(provinceName) ?? provinceName;
        return ProvinceMergedFrom.TryGetValue(canonical, out var value) ? value : new List<string> { canonical };
    }

    private static (string NaturalAreaKm2, string Population) GetStats(string provinceName)
    {
        var canonical = FindCanonicalProvinceName(provinceName) ?? provinceName;
        return ProvinceStats.TryGetValue(canonical, out var value) ? value : (string.Empty, string.Empty);
    }

    private static bool IsArchipelago(string provinceName)
    {
        var canonical = FindCanonicalProvinceName(provinceName) ?? provinceName;
        return ArchipelagoBelongsTo.ContainsKey(canonical);
    }

    private static string GetBelongsTo(string provinceName)
    {
        var canonical = FindCanonicalProvinceName(provinceName) ?? provinceName;
        return ArchipelagoBelongsTo.TryGetValue(canonical, out var value) ? value : string.Empty;
    }

    private static string GetAdministrativeNote(string provinceName)
    {
        var canonical = FindCanonicalProvinceName(provinceName) ?? provinceName;
        return ArchipelagoNotes.TryGetValue(canonical, out var value) ? value : string.Empty;
    }

    private static List<Dictionary<string, object?>> ToDestinationList(object? destinationsObj)
    {
        var list = new List<Dictionary<string, object?>>();
        if (destinationsObj is Dictionary<string, object?> dict)
        {
            foreach (var (key, value) in dict)
            {
                if (value is Dictionary<string, object?> inner)
                {
                    var item = new Dictionary<string, object?>(inner) { ["id"] = key };
                    list.Add(item);
                }
            }
        }
        else if (destinationsObj is IEnumerable<object> rawList)
        {
            foreach (var value in rawList)
            {
                if (value is Dictionary<string, object?> inner) list.Add(new Dictionary<string, object?>(inner));
            }
        }
        return list;
    }

    private static string CreateKey(string name) => NormalizeText(name).Replace(" ", "-").Replace("&", "and");

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(ch switch
            {
                'đ' => 'd',
                '–' or '—' => '-',
                _ => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch == '.' || ch == '-' ? ch : ' '
            });
        }
        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
