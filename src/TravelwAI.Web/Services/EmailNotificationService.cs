using System.Globalization;
using Microsoft.Extensions.Options;
using TravelwAI.Data.Options;
using TravelwAI.Data.Services;

namespace TravelwAI.Web.Services;

public sealed class EmailNotificationService
{
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<EmailNotificationService> _logger;

    public EmailNotificationService(IOptions<EmailOptions> emailOptions, ILogger<EmailNotificationService> logger)
    {
        _emailOptions = emailOptions.Value;
        _logger = logger;
    }

    public Task<string?> SendSignupSuccessAsync(string toEmail, string? username)
    {
        var displayName = string.IsNullOrWhiteSpace(username) ? toEmail : username.Trim();
        return TrySendPlainEmailAsync(
            toEmail,
            "Đăng ký TravelwAI thành công",
            $"""
            Xin chào {displayName},

            Tài khoản TravelwAI của bạn đã được đăng ký thành công.

            Email đăng ký: {toEmail}
            Thời gian: {DateTime.Now:HH:mm dd/MM/yyyy}

            Bạn có thể đăng nhập và bắt đầu khám phá TravelwAI.

            TravelwAI
            """);
    }

    public Task<string?> SendPasswordChangedSuccessAsync(string toEmail)
    {
        return TrySendPlainEmailAsync(
            toEmail,
            "Đổi mật khẩu TravelwAI thành công",
            $"""
            Xin chào,

            Mật khẩu tài khoản TravelwAI của bạn đã được đổi thành công.

            Email tài khoản: {toEmail}
            Thời gian: {DateTime.Now:HH:mm dd/MM/yyyy}

            Nếu bạn không thực hiện thao tác này, vui lòng đổi mật khẩu lại ngay và kiểm tra bảo mật tài khoản của bạn.

            TravelwAI
            """);
    }

    public Task<string?> SendTourBookingCreatedAsync(
        string toEmail,
        string? customerName,
        string? tourName,
        int quantity,
        decimal originalTotal,
        decimal discountPercent,
        decimal discountAmount,
        decimal total,
        string orderId,
        DateTime expiresAtUtc)
    {
        var name = string.IsNullOrWhiteSpace(customerName) ? toEmail : customerName.Trim();
        var tour = string.IsNullOrWhiteSpace(tourName) ? "Tour du lịch" : tourName.Trim();
        return TrySendPlainEmailAsync(
            toEmail,
            "Đặt tour TravelwAI thành công",
            $"""
            Xin chào {name},

            Bạn đã đặt tour thành công trên TravelwAI.

            Mã đơn: {orderId}
            Tour: {tour}
            Số người: {Math.Max(1, quantity)}
            Tạm tính: {FormatVnd(originalTotal)}
            Ưu đãi: {FormatPercent(discountPercent)} (-{FormatVnd(discountAmount)})
            Tổng tiền: {FormatVnd(total)}
            Trạng thái: Chờ Sales xác nhận
            Hạn xác nhận: {expiresAtUtc.ToLocalTime():HH:mm dd/MM/yyyy}

            Sau khi Sales xác nhận, hệ thống sẽ tạo lịch trình tour.

            TravelwAI
            """);
    }

    public Task<string?> SendTourSoldSuccessAsync(
        string toEmail,
        string? customerName,
        string? tourName,
        int quantity,
        decimal total,
        string orderId,
        string? scheduleId)
    {
        var name = string.IsNullOrWhiteSpace(customerName) ? toEmail : customerName.Trim();
        var tour = string.IsNullOrWhiteSpace(tourName) ? "Tour du lịch" : tourName.Trim();
        var scheduleText = string.IsNullOrWhiteSpace(scheduleId) ? string.Empty : $"\nMã lịch trình: {scheduleId}";

        return TrySendPlainEmailAsync(
            toEmail,
            "Mua tour TravelwAI thành công",
            $"""
            Xin chào {name},

            Tour của bạn đã được xác nhận.

            Mã đơn: {orderId}
            Tour: {tour}
            Số người: {Math.Max(1, quantity)}
            Tổng tiền: {FormatVnd(total)}{scheduleText}
            Thời gian xác nhận: {DateTime.Now:HH:mm dd/MM/yyyy}

            Lịch trình tour đã được tạo trong tài khoản TravelwAI.

            TravelwAI
            """);
    }

    public Task<string?> SendNewsletterSubscribedAsync(string toEmail)
    {
        var email = (toEmail ?? string.Empty).Trim();
        return TrySendPlainEmailAsync(
            email,
            "Đăng ký nhận tin TravelwAI thành công",
            $"""
            Xin chào,

            Bạn đã đăng ký nhận tin TravelwAI thành công.

            Email nhận tin: {email}
            Thời gian: {DateTime.Now:HH:mm dd/MM/yyyy}

            TravelwAI sẽ gửi thông tin du lịch, văn hoá, lịch sử, tour và cập nhật mới.

            TravelwAI
            """);
    }



    public Task<string?> SendBusinessApplicationToAdminAsync(Dictionary<string, object?> application)
    {
        const string adminEmail = "2324802010387@student.tdmu.edu.vn";
        string Text(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (application.TryGetValue(key, out var value) && value is not null)
                {
                    var text = value.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            return string.Empty;
        }

        var role = Text("plan_role", "planRole");
        var subject = $"Biểu mẫu đăng ký {role} - TravelwAI";
        var body = $"""
        Admin TravelwAI nhận được biểu mẫu đăng ký {role}.

        Thông tin doanh nghiệp
        Tên công ty / cá nhân kinh doanh: {Text("company_name", "companyName")}
        Loại hình: {Text("business_type", "businessType")}
        Mã số thuế / CMND: {Text("tax_code", "taxCode")}
        Địa chỉ văn phòng: {Text("office_address", "officeAddress")}
        Tỉnh / Thành phố: {Text("province")}
        Website / Fanpage: {Text("website")}

        Người phụ trách
        Họ và tên: {Text("contact_name", "contactName")}
        Chức vụ: {Text("position")}
        Số điện thoại: {Text("phone")}
        Email: {Text("email")}

        Tài khoản gửi biểu mẫu: {Text("user_email", "userEmail")}
        Thời gian: {DateTime.Now:HH:mm dd/MM/yyyy}

        Xem và duyệt trong Manage.
        """;
        return TrySendPlainEmailAsync(adminEmail, subject, body);
    }

    private async Task<string?> TrySendPlainEmailAsync(string toEmail, string subject, string body)
    {
        var error = await ResendEmailSender.SendPlainEmailAsync(_emailOptions, toEmail, subject, body);
        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogWarning("Không gửi được email TravelwAI đến {Email}. Lỗi: {Error}", toEmail, error);
        }

        return error;
    }

    private static string FormatVnd(decimal value) => value.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + " ₫";

    private static string FormatPercent(decimal value)
    {
        if (value <= 0) return "0%";
        return value % 1 == 0
            ? value.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + "%"
            : value.ToString("N2", CultureInfo.GetCultureInfo("vi-VN")) + "%";
    }
}
