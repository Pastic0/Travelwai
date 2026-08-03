using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Models.Requests;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[ApiController]
[Route("api")]
public sealed class AuthController : ControllerBase
{
    private const int MinimumPasswordLength = 8;
    private readonly IAuthService _authService;
    private readonly TourOfferService _tourOfferService;
    private readonly EmailNotificationService _emailNotificationService;
    private readonly PlanQueueService _planQueueService;
    private readonly AccountPresenceService _presenceService;

    public AuthController(IAuthService authService, TourOfferService tourOfferService, EmailNotificationService emailNotificationService, PlanQueueService planQueueService, AccountPresenceService presenceService)
    {
        _authService = authService;
        _tourOfferService = tourOfferService;
        _emailNotificationService = emailNotificationService;
        _planQueueService = planQueueService;
        _presenceService = presenceService;
    }

    [HttpPost("signup")]
    public async Task<IActionResult> SignUp([FromBody] UserAccountRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { success = false, message = "Vui lòng nhập email và mật khẩu" });

        if (request.Password.Length < MinimumPasswordLength)
        {
            return BadRequest(new { success = false, message = $"Mật khẩu phải có ít nhất {MinimumPasswordLength} ký tự" });
        }

        var normalizedEmail = request.Email.Trim();
        var result = await _authService.SignUpAsync(
            normalizedEmail,
            request.Password,
            request.Username?.Trim() ?? string.Empty);

        if (result.GetValueOrDefault("success") is not true
            && !string.IsNullOrWhiteSpace(request.OfferInvite)
            && (result.GetValueOrDefault("message")?.ToString() ?? string.Empty).Contains("đã được đăng ký", StringComparison.OrdinalIgnoreCase))
        {
            await _tourOfferService.DeletePendingInvitesForEmailAsync(normalizedEmail);
        }

        if (result.TryGetValue("success", out var success) && success is bool ok && ok
            && result.TryGetValue("localId", out var uidObj) && uidObj is string uid)
        {
            await _tourOfferService.ConfirmSignupAsync(normalizedEmail, uid, request.OfferInvite?.Trim());

            var emailError = await _emailNotificationService.SendSignupSuccessAsync(
                normalizedEmail,
                result.GetValueOrDefault("displayName")?.ToString()
                    ?? result.GetValueOrDefault("username")?.ToString()
                    ?? request.Username?.Trim());

            result["signupEmailSent"] = string.IsNullOrWhiteSpace(emailError);
            if (string.IsNullOrWhiteSpace(emailError))
            {
                result["message"] = "Tạo tài khoản thành công.";
            }
            else
            {
                result["message"] = "Tạo tài khoản thành công.";
                result["emailWarning"] = emailError;
            }
        }

        return Ok(result);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] UserAccountRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { success = false, message = "Vui lòng nhập email và mật khẩu" });

        var result = await _authService.LoginAsync(
            request.Email.Trim(),
            request.Password,
            request.Username?.Trim() ?? string.Empty);

        await SyncPlanResultAsync(result);
        WriteAuthCookiesIfSuccess(result);
        await MarkOnlineIfSuccessAsync(result, updateLoginTime: true);
        return Ok(result);
    }

    [HttpPost("verify-token")]
    public async Task<IActionResult> VerifyToken([FromBody] TokenRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.IdToken))
            return Ok(new { message = "Chưa cung cấp ID token", success = false });

        var result = await _authService.VerifyTokenAsync(request.IdToken.Trim());
        await SyncPlanResultAsync(result);
        return Ok(result);
    }

    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RefreshToken))
            return Ok(new { message = "Chưa cung cấp refresh token", success = false });

        var result = await _authService.RefreshTokenAsync(request.RefreshToken.Trim());
        await SyncPlanResultAsync(result);
        WriteAuthCookiesIfSuccess(result);
        await MarkOnlineIfSuccessAsync(result, updateLoginTime: false);
        return Ok(result);
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { success = false, message = "Vui lòng nhập email" });

        return Ok(await _authService.SendPasswordResetEmailAsync(request.Email.Trim()));
    }

    [HttpPost("password-reset/verify-otp")]
    public async Task<IActionResult> VerifyPasswordResetOtp([FromBody] VerifyPasswordResetOtpRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Otp))
            return BadRequest(new { success = false, message = "Vui lòng nhập email và mã OTP" });

        return Ok(await _authService.VerifyPasswordResetOtpAsync(request.Email.Trim(), request.Otp.Trim()));
    }

    [HttpPost("password-reset/confirm")]
    public async Task<IActionResult> ConfirmPasswordReset([FromBody] ResetPasswordRequest request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.ResetToken)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { success = false, message = "Vui lòng nhập đầy đủ thông tin đổi mật khẩu" });
        }

        if (request.Password.Length < MinimumPasswordLength)
        {
            return BadRequest(new { success = false, message = $"Mật khẩu mới phải có ít nhất {MinimumPasswordLength} ký tự" });
        }

        var result = await _authService.ResetPasswordWithTokenAsync(request.Email.Trim(), request.ResetToken.Trim(), request.Password);
        if (result.TryGetValue("success", out var success) && success is bool ok && ok)
        {
            var emailError = await _emailNotificationService.SendPasswordChangedSuccessAsync(request.Email.Trim());
            result["passwordChangedEmailSent"] = string.IsNullOrWhiteSpace(emailError);
            if (string.IsNullOrWhiteSpace(emailError))
            {
                result["message"] = "Đổi mật khẩu thành công.";
            }
            else
            {
                result["emailWarning"] = emailError;
            }
        }

        return Ok(result);
    }

    [HttpPost("google-login")]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.IdToken))
            return Ok(new { message = "Chưa cung cấp ID token", success = false });

        var result = await _authService.VerifyTokenAsync(request.IdToken.Trim());
        await SyncPlanResultAsync(result);
        if (result.TryGetValue("success", out var success) && success is bool ok && ok)
        {
            Response.Cookies.Append("TravelwAIAuth", request.IdToken.Trim(), BuildAuthCookieOptions());
            await MarkOnlineIfSuccessAsync(result, updateLoginTime: true);
        }
        return Ok(result);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var userId = await TryResolveCurrentUserIdAsync();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            try
            {
                await _presenceService.MarkOfflineAsync(userId, revokeRefreshToken: true);
            }
            catch
            {
                // Vẫn phải xóa phiên phía trình duyệt nếu cập nhật trạng thái tạm thời thất bại.
            }
        }

        ClearAuthCookies();
        return Ok(new { success = true, message = "Đã đăng xuất" });
    }

    private async Task SyncPlanResultAsync(Dictionary<string, object?> result)
    {
        if (!result.TryGetValue("success", out var success) || success is not bool ok || !ok) return;
        var userId = result.GetValueOrDefault("localId")?.ToString()
            ?? result.GetValueOrDefault("uid")?.ToString()
            ?? result.GetValueOrDefault("userId")?.ToString();
        if (string.IsNullOrWhiteSpace(userId)) return;
        var state = await _planQueueService.SyncUserAsync(userId, result.GetValueOrDefault("role")?.ToString());
        result["role"] = state.CurrentRole;
        result["planRole"] = state.CurrentRole;
        result["plan_role"] = state.CurrentRole;
        result["planStartedAt"] = state.CurrentStartedAt;
        result["plan_started_at"] = state.CurrentStartedAt;
        result["planExpiresAt"] = state.CurrentExpiresAt;
        result["plan_expires_at"] = state.CurrentExpiresAt;
        result["nextPlanRole"] = state.NextRole;
        result["next_plan_role"] = state.NextRole;
        result["nextPlanStartedAt"] = state.NextStartedAt;
        result["next_plan_started_at"] = state.NextStartedAt;
        result["nextPlanExpiresAt"] = state.NextExpiresAt;
        result["next_plan_expires_at"] = state.NextExpiresAt;
        result["planCountdownSeconds"] = state.CountdownSeconds;
        result["plan_countdown_seconds"] = state.CountdownSeconds;
    }

    private void WriteAuthCookiesIfSuccess(Dictionary<string, object?> result)
    {
        if (!result.TryGetValue("success", out var success) || success is not bool ok || !ok)
        {
            ClearAuthCookies();
            return;
        }

        if (result.GetValueOrDefault("idToken") is string idToken && !string.IsNullOrWhiteSpace(idToken))
        {

            Response.Cookies.Append("TravelwAIAuth", idToken, BuildAuthCookieOptions());
        }

        if (result.GetValueOrDefault("refreshToken") is string refreshToken && !string.IsNullOrWhiteSpace(refreshToken))
        {
            Response.Cookies.Append("TravelwAIRefresh", refreshToken, BuildAuthCookieOptions());
        }
    }

    private CookieOptions BuildAuthCookieOptions() => new()
    {
        Path = "/",
        SameSite = SameSiteMode.Lax,
        Secure = Request.IsHttps,
        HttpOnly = false,
        IsEssential = true
    };


    private async Task MarkOnlineIfSuccessAsync(Dictionary<string, object?> result, bool updateLoginTime)
    {
        if (result.GetValueOrDefault("success") is not bool ok || !ok) return;
        var userId = result.GetValueOrDefault("localId")?.ToString()
            ?? result.GetValueOrDefault("uid")?.ToString()
            ?? result.GetValueOrDefault("userId")?.ToString();
        if (string.IsNullOrWhiteSpace(userId)
            && result.GetValueOrDefault("user") is Dictionary<string, object?> user)
        {
            userId = _authService.GetUserId(user);
        }
        if (string.IsNullOrWhiteSpace(userId)) return;

        try
        {
            await _presenceService.MarkOnlineAsync(userId, updateLoginTime);
            result["is_online"] = true;
            result["isOnline"] = true;
            result["presence_status"] = "online";
        }
        catch
        {
            result["presenceWarning"] = "Đăng nhập thành công nhưng chưa cập nhật được trạng thái online.";
        }
    }

    private async Task<string?> TryResolveCurrentUserIdAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        var token = string.Empty;
        if (!string.IsNullOrWhiteSpace(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = header["Bearer ".Length..].Trim();
        }
        if (string.IsNullOrWhiteSpace(token) && Request.Cookies.TryGetValue("TravelwAIAuth", out var cookieToken))
        {
            token = cookieToken?.Trim() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(token)) return null;

        var verify = await _authService.VerifyTokenAsync(token);
        if (verify.GetValueOrDefault("success") is not bool ok || !ok) return null;
        return verify.GetValueOrDefault("user") is Dictionary<string, object?> user
            ? _authService.GetUserId(user)
            : null;
    }

    private void ClearAuthCookies()
    {
        Response.Cookies.Delete("TravelwAIAuth", new CookieOptions { Path = "/" });
        Response.Cookies.Delete("TravelwAIRefresh", new CookieOptions { Path = "/" });
    }
}
