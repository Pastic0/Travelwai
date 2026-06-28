using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;

namespace TravelwAI.Web.Controllers.Api;

[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected readonly IAuthService AuthService;

    protected ApiControllerBase(IAuthService authService)
    {
        AuthService = authService;
    }

    protected async Task<(bool ok, string? userId, Dictionary<string, object?>? authUser, IActionResult? error)> CurrentUserAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return (false, null, null, Unauthorized(new { success = false, message = "Thiếu token đăng nhập" }));

        var token = header["Bearer ".Length..].Trim();
        var result = await AuthService.VerifyTokenAsync(token);
        if (!result.TryGetValue("success", out var success) || success is not bool ok || !ok)
            return (false, null, null, Unauthorized(new { success = false, message = "Token đăng nhập không hợp lệ" }));

        var authUser = result.GetValueOrDefault("user") as Dictionary<string, object?>;
        if (authUser is null)
            return (false, null, null, Unauthorized(new { success = false, message = "Token đăng nhập không hợp lệ" }));

        var userId = AuthService.GetUserId(authUser);
        if (string.IsNullOrWhiteSpace(userId))
            return (false, null, null, Unauthorized(new { success = false, message = "Không đọc được mã người dùng từ token" }));

        return (true, userId, authUser, null);
    }

    protected static string NormalizeAccountRole(object? role)
    {
        var value = role?.ToString()?.Trim() ?? string.Empty;
        if (value.Equals("User", StringComparison.OrdinalIgnoreCase)) return "Free";
        if (value.Equals("Company", StringComparison.OrdinalIgnoreCase)) return "Business";
        if (value.Equals("Tour Sales", StringComparison.OrdinalIgnoreCase) || value.Equals("TourSales", StringComparison.OrdinalIgnoreCase)) return "Sales";
        if (value.Equals("VIP", StringComparison.OrdinalIgnoreCase)) return "VIP";
        if (value.Equals("Premium", StringComparison.OrdinalIgnoreCase)) return "Premium";
        if (value.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return "Admin";
        if (value.Equals("Sales", StringComparison.OrdinalIgnoreCase)) return "Sales";
        if (value.Equals("Business", StringComparison.OrdinalIgnoreCase)) return "Business";
        return string.IsNullOrWhiteSpace(value) ? "Free" : value;
    }

    protected static bool IsFreeAccount(Dictionary<string, object?>? authUser)
        => string.Equals(NormalizeAccountRole(authUser?.GetValueOrDefault("role")), "Free", StringComparison.OrdinalIgnoreCase);

    protected static bool IsVipAccount(Dictionary<string, object?>? authUser)
        => string.Equals(NormalizeAccountRole(authUser?.GetValueOrDefault("role")), "VIP", StringComparison.OrdinalIgnoreCase);

    protected static bool CanUsePostOffer(Dictionary<string, object?>? authUser)
    {
        var role = NormalizeAccountRole(authUser?.GetValueOrDefault("role"));
        return !string.Equals(role, "Free", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase);
    }

    protected static bool CanUseAiPost(Dictionary<string, object?>? authUser)
        => !IsFreeAccount(authUser);

    protected static bool CanCreateSchedule(Dictionary<string, object?>? authUser)
        => !IsFreeAccount(authUser);

    protected static bool CanUseAiChat(Dictionary<string, object?>? authUser)
        => true;

    protected static int GetAiChatLimitPerFiveMinutes(Dictionary<string, object?>? authUser)
    {
        var role = NormalizeAccountRole(authUser?.GetValueOrDefault("role"));
        if (string.Equals(role, "Free", StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase)) return 10;
        return 0;
    }

}
