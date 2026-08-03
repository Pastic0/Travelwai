using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TravelwAI.Web.Options;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/payment-webhooks/sepay")]
public sealed class SePayWebhookController : ControllerBase
{
    private readonly AutomaticPaymentService _automaticPaymentService;
    private readonly SePayOptions _options;
    private readonly ILogger<SePayWebhookController> _logger;

    public SePayWebhookController(
        AutomaticPaymentService automaticPaymentService,
        IOptions<SePayOptions> options,
        ILogger<SePayWebhookController> logger)
    {
        _automaticPaymentService = automaticPaymentService;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            success = true,
            enabled = _options.Enabled,
            apiKeyConfigured = !string.IsNullOrWhiteSpace(_options.WebhookApiKey),
            bankCode = _options.BankCode,
            accountConfigured = !string.IsNullOrWhiteSpace(_options.BankAccountNumber),
            paymentCodePrefix = _options.PaymentCodePrefix
        });
    }

    [HttpPost]
    public async Task<IActionResult> Receive([FromBody] SePayWebhookPayload payload, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("Webhook SePay bị từ chối vì SePay:Enabled đang là false.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                error = "sepay_disabled"
            });
        }

        if (string.IsNullOrWhiteSpace(_options.WebhookApiKey))
        {
            _logger.LogError("Webhook SePay chưa được cấu hình API key.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                error = "sepay_api_key_missing"
            });
        }

        if (!IsAuthorized(Request.Headers["Authorization"].ToString(), _options.WebhookApiKey))
        {
            return Unauthorized(new
            {
                success = false,
                error = "invalid_webhook_api_key"
            });
        }

        try
        {
            var result = await _automaticPaymentService.ProcessSePayAsync(payload, cancellationToken);
            if (result.Ignored)
            {
                _logger.LogInformation(
                    "Đã nhận giao dịch SePay {TransactionId} nhưng không xác nhận đơn: {Reason}",
                    payload.Id,
                    result.Message);
            }
            else
            {
                _logger.LogInformation(
                    "Đã tự động xác nhận giao dịch SePay {TransactionId} cho đơn {OrderId}. Duplicate={Duplicate}",
                    payload.Id,
                    result.OrderId,
                    result.Duplicate);
            }

            // SePay yêu cầu HTTP 200/201 và JSON chính xác có success=true.
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Xử lý webhook SePay {TransactionId} thất bại.", payload.Id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { success = false });
        }
    }

    private static bool IsAuthorized(string authorizationHeader, string configuredKey)
    {
        var parts = (authorizationHeader ?? string.Empty).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !string.Equals(parts[0], "Apikey", StringComparison.OrdinalIgnoreCase)) return false;

        var providedBytes = Encoding.UTF8.GetBytes(parts[1]);
        var expectedBytes = Encoding.UTF8.GetBytes(configuredKey.Trim());
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
