namespace TravelwAI.Web.Models;

public sealed record ProvinceSummary(
    int Id,
    string Name,
    string Area,
    string Region,
    IReadOnlyList<string> FamousFor,
    IReadOnlyList<string> MergedFrom,
    string NaturalAreaKm2,
    string Population);

public sealed class ProvincesPageViewModel
{
    public IReadOnlyList<ProvinceSummary> Provinces { get; init; } = Array.Empty<ProvinceSummary>();
}

public static class ProvinceCatalog
{
    public static IReadOnlyList<ProvinceSummary> All { get; } = new List<ProvinceSummary>
    {
        new ProvinceSummary(1, @"Cao Bằng", @"Bắc Bộ", @"Trung du và Miền núi phía Bắc", new[] { @"núi rừng và ruộng bậc thang", @"di tích lịch sử - văn hóa", @"ẩm thực địa phương" }, new[] { @"Cao Bằng" }, @"6.700,39", @"573.119"),
        new ProvinceSummary(2, @"Điện Biên", @"Bắc Bộ", @"Trung du và Miền núi phía Bắc", new[] { @"núi rừng và ruộng bậc thang", @"di tích lịch sử - văn hóa", @"ẩm thực địa phương" }, new[] { @"Điện Biên" }, @"9.539,93", @"673.091"),
        new ProvinceSummary(3, @"Lai Châu", @"Bắc Bộ", @"Trung du và Miền núi phía Bắc", new[] { @"núi rừng và ruộng bậc thang", @"di tích lịch sử - văn hóa", @"ẩm thực địa phương" }, new[] { @"Lai Châu" }, @"9.068,73", @"512.601"),
        new ProvinceSummary(4, @"Lạng Sơn", @"Bắc Bộ", @"Trung du và Miền núi phía Bắc", new[] { @"núi rừng và ruộng bậc thang", @"di tích lịch sử - văn hóa", @"ẩm thực địa phương" }, new[] { @"Lạng Sơn" }, @"8.310,18", @"881.384"),
        new ProvinceSummary(5, @"Lào Cai", @"Bắc Bộ", @"Trung du và Miền núi phía Bắc", new[] { @"núi rừng và ruộng bậc thang", @"di tích lịch sử - văn hóa", @"ẩm thực địa phương" }, new[] { @"Lào Cai", @"Yên Bái" }, @"13.257", @"1.778.785"),
        new ProvinceSummary(6, @"Phú Thọ", @"Bắc Bộ", @"Trung du và Miền núi phía Bắc", new[] { @"núi rừng và ruộng bậc thang", @"di tích lịch sử - văn hóa", @"ẩm thực địa phương" }, new[] { @"Hòa Bình", @"Vĩnh Phúc", @"Phú Thọ" }, @"9.361,4", @"4.022.638"),
        new ProvinceSummary(7, @"Sơn La", @"Bắc Bộ", @"Trung du và Miền núi phía Bắc", new[] { @"núi rừng và ruộng bậc thang", @"di tích lịch sử - văn hóa", @"ẩm thực địa phương" }, new[] { @"Sơn La" }, @"14.109,83", @"1.404.587"),
        new ProvinceSummary(8, @"Thái Nguyên", @"Bắc Bộ", @"Trung du và Miền núi phía Bắc", new[] { @"núi rừng và ruộng bậc thang", @"di tích lịch sử - văn hóa", @"ẩm thực địa phương" }, new[] { @"Bắc Kạn", @"Thái Nguyên" }, @"8.375,3", @"1.799.489"),
        new ProvinceSummary(9, @"Tuyên Quang", @"Bắc Bộ", @"Trung du và Miền núi phía Bắc", new[] { @"núi rừng và ruộng bậc thang", @"di tích lịch sử - văn hóa", @"ẩm thực địa phương" }, new[] { @"Hà Giang", @"Tuyên Quang" }, @"13.795,6", @"1.865.270"),
        new ProvinceSummary(10, @"Thành phố Hà Nội", @"Bắc Bộ", @"Đồng bằng sông Hồng", new[] { @"đô thị lịch sử", @"di sản văn hóa", @"ẩm thực Bắc Bộ" }, new[] { @"Hà Nội" }, @"3.359,84", @"8.807.523"),
        new ProvinceSummary(11, @"Thành phố Hải Phòng", @"Bắc Bộ", @"Đồng bằng sông Hồng", new[] { @"đô thị lịch sử", @"di sản văn hóa", @"ẩm thực Bắc Bộ" }, new[] { @"Hải Dương", @"Hải Phòng" }, @"3.194,7", @"4.664.124"),
        new ProvinceSummary(12, @"Bắc Ninh", @"Bắc Bộ", @"Đồng bằng sông Hồng", new[] { @"đô thị lịch sử", @"di sản văn hóa", @"ẩm thực Bắc Bộ" }, new[] { @"Bắc Giang", @"Bắc Ninh" }, @"4.718,6", @"3.619.433"),
        new ProvinceSummary(13, @"Hưng Yên", @"Bắc Bộ", @"Đồng bằng sông Hồng", new[] { @"đô thị lịch sử", @"di sản văn hóa", @"ẩm thực Bắc Bộ" }, new[] { @"Thái Bình", @"Hưng Yên" }, @"2.514,8", @"3.567.943"),
        new ProvinceSummary(14, @"Ninh Bình", @"Bắc Bộ", @"Đồng bằng sông Hồng", new[] { @"đô thị lịch sử", @"di sản văn hóa", @"ẩm thực Bắc Bộ" }, new[] { @"Hà Nam", @"Nam Định", @"Ninh Bình" }, @"3.942,6", @"4.412.264"),
        new ProvinceSummary(15, @"Quảng Ninh", @"Bắc Bộ", @"Đồng bằng sông Hồng", new[] { @"đô thị lịch sử", @"di sản văn hóa", @"ẩm thực Bắc Bộ" }, new[] { @"Quảng Ninh" }, @"6.207,93", @"1.497.447"),
        new ProvinceSummary(16, @"Thành phố Huế", @"Trung Bộ", @"Bắc Trung Bộ", new[] { @"di sản cố đô", @"biển Trung Bộ", @"ẩm thực Trung Bộ" }, new[] { @"Thừa Thiên Huế" }, @"4.947,11", @"1.432.986"),
        new ProvinceSummary(17, @"Hà Tĩnh", @"Trung Bộ", @"Bắc Trung Bộ", new[] { @"di sản cố đô", @"biển Trung Bộ", @"ẩm thực Trung Bộ" }, new[] { @"Hà Tĩnh" }, @"5.994,45", @"1.622.901"),
        new ProvinceSummary(18, @"Nghệ An", @"Trung Bộ", @"Bắc Trung Bộ", new[] { @"di sản cố đô", @"biển Trung Bộ", @"ẩm thực Trung Bộ" }, new[] { @"Nghệ An" }, @"16.486,49", @"3.831.694"),
        new ProvinceSummary(19, @"Quảng Trị", @"Trung Bộ", @"Bắc Trung Bộ", new[] { @"di sản cố đô", @"biển Trung Bộ", @"ẩm thực Trung Bộ" }, new[] { @"Quảng Bình", @"Quảng Trị" }, @"12.700", @"1.870.845"),
        new ProvinceSummary(20, @"Thanh Hóa", @"Trung Bộ", @"Bắc Trung Bộ", new[] { @"di sản cố đô", @"biển Trung Bộ", @"ẩm thực Trung Bộ" }, new[] { @"Thanh Hóa" }, @"11.114,71", @"4.324.783"),
        new ProvinceSummary(21, @"Thành phố Đà Nẵng", @"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { @"biển đảo - cao nguyên", @"thác nước và vịnh biển", @"văn hóa bản địa" }, new[] { @"Quảng Nam", @"Đà Nẵng" }, @"11.859,6", @"3.065.628"),
        new ProvinceSummary(22, @"Đắk Lắk", @"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { @"biển đảo - cao nguyên", @"thác nước và vịnh biển", @"văn hóa bản địa" }, new[] { @"Phú Yên", @"Đắk Lắk" }, @"18.096,4", @"3.346.853"),
        new ProvinceSummary(23, @"Gia Lai", @"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { @"biển đảo - cao nguyên", @"thác nước và vịnh biển", @"văn hóa bản địa" }, new[] { @"Gia Lai", @"Bình Định" }, @"21.576,5", @"3.583.693"),
        new ProvinceSummary(24, @"Khánh Hòa", @"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { @"biển đảo - cao nguyên", @"thác nước và vịnh biển", @"văn hóa bản địa" }, new[] { @"Khánh Hòa", @"Ninh Thuận" }, @"8.555,9", @"2.243.554"),
        new ProvinceSummary(25, @"Lâm Đồng", @"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { @"biển đảo - cao nguyên", @"thác nước và vịnh biển", @"văn hóa bản địa" }, new[] { @"Đắk Nông", @"Lâm Đồng", @"Bình Thuận" }, @"24.233,1", @"3.872.999"),
        new ProvinceSummary(26, @"Quảng Ngãi", @"Trung Bộ", @"Duyên hải Nam Trung Bộ và Tây Nguyên", new[] { @"biển đảo - cao nguyên", @"thác nước và vịnh biển", @"văn hóa bản địa" }, new[] { @"Quảng Ngãi", @"Kon Tum" }, @"14.832,6", @"2.161.755"),
        new ProvinceSummary(27, @"Thành phố Hồ Chí Minh", @"Nam Bộ", @"Đông Nam Bộ", new[] { @"đô thị năng động", @"khu vui chơi - sinh thái", @"ẩm thực Nam Bộ" }, new[] { @"Bình Dương", @"Thành phố Hồ Chí Minh", @"Bà Rịa - Vũng Tàu" }, @"6.772,6", @"14.002.598"),
        new ProvinceSummary(28, @"Đồng Nai", @"Nam Bộ", @"Đông Nam Bộ", new[] { @"đô thị năng động", @"khu vui chơi - sinh thái", @"ẩm thực Nam Bộ" }, new[] { @"Bình Phước", @"Đồng Nai" }, @"12.737,2", @"4.491.408"),
        new ProvinceSummary(29, @"Tây Ninh", @"Nam Bộ", @"Đông Nam Bộ", new[] { @"đô thị năng động", @"khu vui chơi - sinh thái", @"ẩm thực Nam Bộ" }, new[] { @"Long An", @"Tây Ninh" }, @"8.536,5", @"3.254.170"),
        new ProvinceSummary(30, @"Thành phố Cần Thơ", @"Nam Bộ", @"Đồng bằng sông Cửu Long", new[] { @"sông nước miệt vườn", @"chợ nổi và làng nghề", @"ẩm thực miền Tây" }, new[] { @"Sóc Trăng", @"Hậu Giang", @"Cần Thơ" }, @"6.360,8", @"4.199.824"),
        new ProvinceSummary(31, @"An Giang", @"Nam Bộ", @"Đồng bằng sông Cửu Long", new[] { @"sông nước miệt vườn", @"chợ nổi và làng nghề", @"ẩm thực miền Tây" }, new[] { @"Kiên Giang", @"An Giang" }, @"9.888,9", @"4.952.238"),
        new ProvinceSummary(32, @"Cà Mau", @"Nam Bộ", @"Đồng bằng sông Cửu Long", new[] { @"sông nước miệt vườn", @"chợ nổi và làng nghề", @"ẩm thực miền Tây" }, new[] { @"Bạc Liêu", @"Cà Mau" }, @"7.942,4", @"2.606.672"),
        new ProvinceSummary(33, @"Đồng Tháp", @"Nam Bộ", @"Đồng bằng sông Cửu Long", new[] { @"sông nước miệt vườn", @"chợ nổi và làng nghề", @"ẩm thực miền Tây" }, new[] { @"Tiền Giang", @"Đồng Tháp" }, @"5.938,7", @"4.370.046"),
        new ProvinceSummary(34, @"Vĩnh Long", @"Nam Bộ", @"Đồng bằng sông Cửu Long", new[] { @"sông nước miệt vườn", @"chợ nổi và làng nghề", @"ẩm thực miền Tây" }, new[] { @"Bến Tre", @"Vĩnh Long", @"Trà Vinh" }, @"6.296,2", @"4.257.581"),
    };
}
