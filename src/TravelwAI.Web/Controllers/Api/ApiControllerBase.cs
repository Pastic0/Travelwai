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
}
