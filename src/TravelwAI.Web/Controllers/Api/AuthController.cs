using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Models.Requests;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[ApiController]
[Route("api")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly TourOfferService _tourOfferService;
    private readonly EmailNotificationService _emailNotificationService;

    public AuthController(IAuthService authService, TourOfferService tourOfferService, EmailNotificationService emailNotificationService)
    {
        _authService = authService;
        _tourOfferService = tourOfferService;
        _emailNotificationService = emailNotificationService;
    }

    [HttpPost("signup")]
    public async Task<IActionResult> SignUp([FromBody] UserAccountRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { success = false, message = "Vui lòng nhập email và mật khẩu" });

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
                result["message"] = "Tạo tài khoản thành công. Email xác nhận đã được gửi.";
            }
            else
            {
                result["message"] = "Tạo tài khoản thành công. Vui lòng đăng nhập.";
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

        WriteAuthCookiesIfSuccess(result);
        return Ok(result);
    }

    [HttpPost("verify-token")]
    public async Task<IActionResult> VerifyToken([FromBody] TokenRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.IdToken))
            return Ok(new { message = "Chưa cung cấp ID token", success = false });

        return Ok(await _authService.VerifyTokenAsync(request.IdToken.Trim()));
    }

    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RefreshToken))
            return Ok(new { message = "Chưa cung cấp refresh token", success = false });

        var result = await _authService.RefreshTokenAsync(request.RefreshToken.Trim());
        WriteAuthCookiesIfSuccess(result);
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

        if (request.Password.Length < 6)
        {
            return BadRequest(new { success = false, message = "Mật khẩu mới phải có ít nhất 6 ký tự" });
        }

        var result = await _authService.ResetPasswordWithTokenAsync(request.Email.Trim(), request.ResetToken.Trim(), request.Password);
        if (result.TryGetValue("success", out var success) && success is bool ok && ok)
        {
            var emailError = await _emailNotificationService.SendPasswordChangedSuccessAsync(request.Email.Trim());
            result["passwordChangedEmailSent"] = string.IsNullOrWhiteSpace(emailError);
            if (string.IsNullOrWhiteSpace(emailError))
            {
                result["message"] = "Đổi mật khẩu thành công. Email xác nhận đã được gửi.";
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
        if (result.TryGetValue("success", out var success) && success is bool ok && ok)
        {
            Response.Cookies.Append("TravelwAIAuth", request.IdToken.Trim(), BuildAuthCookieOptions(TimeSpan.FromHours(1)));
        }
        return Ok(result);
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        ClearAuthCookies();
        return Ok(new { success = true, message = "Đã đăng xuất" });
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

            Response.Cookies.Append("TravelwAIAuth", idToken, BuildAuthCookieOptions(TimeSpan.FromDays(7)));
        }

        if (result.GetValueOrDefault("refreshToken") is string refreshToken && !string.IsNullOrWhiteSpace(refreshToken))
        {
            Response.Cookies.Append("TravelwAIRefresh", refreshToken, BuildAuthCookieOptions(TimeSpan.FromDays(30)));
        }
    }

    private CookieOptions BuildAuthCookieOptions(TimeSpan maxAge) => new()
    {
        Path = "/",
        MaxAge = maxAge,
        SameSite = SameSiteMode.Lax,
        Secure = Request.IsHttps,
        HttpOnly = false
    };

    private void ClearAuthCookies()
    {
        Response.Cookies.Delete("TravelwAIAuth", new CookieOptions { Path = "/" });
        Response.Cookies.Delete("TravelwAIRefresh", new CookieOptions { Path = "/" });
    }
}
