using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[ApiController]
[Route("api/newsletter")]
public sealed class NewsletterApiController : ControllerBase
{
    private readonly EmailNotificationService _emailNotificationService;

    public NewsletterApiController(EmailNotificationService emailNotificationService)
    {
        _emailNotificationService = emailNotificationService;
    }

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe([FromBody] NewsletterSubscribeRequest request)
    {
        var email = request?.Email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest(new { success = false, message = "Vui lòng nhập email nhận tin." });
        }

        if (!new EmailAddressAttribute().IsValid(email))
        {
            return BadRequest(new { success = false, message = "Email không hợp lệ." });
        }

        var emailError = await _emailNotificationService.SendNewsletterSubscribedAsync(email);
        if (!string.IsNullOrWhiteSpace(emailError))
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                message = emailError
            });
        }

        return Ok(new
        {
            success = true,
            message = "Đã gửi email xác nhận đăng ký nhận tin."
        });
    }
}

public sealed record NewsletterSubscribeRequest(string? Email);
