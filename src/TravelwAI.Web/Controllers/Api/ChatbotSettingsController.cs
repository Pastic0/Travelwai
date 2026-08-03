using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;
using TravelwAI.Web.Options;

namespace TravelwAI.Web.Controllers.Api;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/chatbot")]
public sealed class ChatbotSettingsController : ApiControllerBase
{
    private const string PlanOrdersCollection = "plan_orders";
    private const int PaymentExpireMinutes = 5;

    private readonly ChatbotSettingsService _settings;
    private readonly RoleFeaturePolicyService _rolePolicies;
    private readonly IDataRepository _repo;
    private readonly AutomaticPaymentService _automaticPaymentService;
    private readonly SePayOptions _sePay;

    public ChatbotSettingsController(
        IAuthService authService,
        ChatbotSettingsService settings,
        RoleFeaturePolicyService rolePolicies,
        IDataRepository repo,
        AutomaticPaymentService automaticPaymentService,
        IOptions<SePayOptions> sePayOptions) : base(authService)
    {
        _settings = settings;
        _rolePolicies = rolePolicies;
        _repo = repo;
        _automaticPaymentService = automaticPaymentService;
        _sePay = sePayOptions.Value;
    }

    [HttpGet("public-settings")]
    public async Task<IActionResult> GetPublicSettings()
    {
        var configuration = await _settings.GetConfigurationAsync();
        return Ok(new
        {
            success = true,
            data = new
            {
                chatbotName = configuration.ChatbotName,
                selectedStyleId = "default",
                defaultStyleId = configuration.DefaultStyleId,
                role = "Free",
                canChangeStyle = false,
                hasAllStyles = false,
                styles = configuration.Styles.Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    price = item.Price,
                    isFree = item.IsFree,
                    maxResponseWords = item.MaxResponseWords,
                    owned = item.IsFree,
                    locked = !item.IsFree,
                    canSelect = false
                })
            }
        });
    }

    [HttpGet("settings")]
    [HttpGet("store")]
    public async Task<IActionResult> GetSettings()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var userSettings = await _settings.GetForUserAsync(current.userId!, current.authUser?.GetValueOrDefault("role"));
        return Ok(new { success = true, data = ToUserSettingsResponse(userSettings) });
    }

    [HttpPut("style")]
    public async Task<IActionResult> UpdateSelectedStyle([FromBody] ChatbotStyleSelectionRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var styleId = (request?.StyleId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(styleId))
        {
            return BadRequest(new { success = false, message = "Vui lòng chọn phong cách." });
        }

        var result = await _settings.SetUserStyleAsync(current.userId!, styleId, current.authUser?.GetValueOrDefault("role"));
        if (!result.Success)
        {
            return StatusCode(result.Style is null ? StatusCodes.Status400BadRequest : StatusCodes.Status403Forbidden,
                new { success = false, message = result.Message, code = result.Style is null ? "STYLE_NOT_FOUND" : "STYLE_LOCKED" });
        }

        var userSettings = await _settings.GetForUserAsync(current.userId!, current.authUser?.GetValueOrDefault("role"));
        return Ok(new
        {
            success = true,
            message = $"Đã đổi phong cách của {userSettings.Configuration.ChatbotName} sang {result.Style!.Name}.",
            data = ToUserSettingsResponse(userSettings)
        });
    }

    [HttpPost("styles/{styleId}/purchase")]
    public async Task<IActionResult> PurchaseStyle(string styleId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (!_sePay.Enabled
            || string.IsNullOrWhiteSpace(_sePay.WebhookApiKey)
            || string.IsNullOrWhiteSpace(_sePay.BankCode)
            || string.IsNullOrWhiteSpace(_sePay.BankAccountNumber))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                message = "Thanh toán tự động chưa được cấu hình. Vui lòng liên hệ quản trị viên."
            });
        }

        var userSettings = await _settings.GetForUserAsync(current.userId!, current.authUser?.GetValueOrDefault("role"));
        var policy = _rolePolicies.GetPolicy(userSettings.Role);
        if (!policy.CanChangeChatbotStyle)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { success = false, code = "VIP_REQUIRED", message = "Cần gói VIP trở lên để mua và đổi phong cách." });
        }

        var style = userSettings.Configuration.Styles.FirstOrDefault(item => string.Equals(item.Id, styleId, StringComparison.OrdinalIgnoreCase));
        if (style is null) return NotFound(new { success = false, message = "Không tìm thấy phong cách." });
        if (style.IsFree || policy.HasAllChatbotStyles || userSettings.OwnedStyleIds.Contains(style.Id))
        {
            return BadRequest(new { success = false, message = "Tài khoản đã có quyền dùng phong cách này." });
        }

        var now = DateTime.UtcNow;
        var existing = await _repo.WhereEqualAsync(PlanOrdersCollection, "buyer_id", current.userId!, limit: 100);
        foreach (var order in existing.Where(item => IsStyleOrder(item) && string.Equals(FirstText(item, "style_id", "styleId"), style.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var id = FirstText(order, "id", "Id");
            var status = FirstText(order, "status");
            var expiresAt = GetEffectiveStylePaymentExpiry(order);
            if (string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase))
            {
                var benefit = await _automaticPaymentService.EnsureOrderBenefitsAsync(id, order);
                if (benefit.Success && await _settings.UserOwnsStyleAsync(current.userId!, style.Id))
                    return Ok(new { success = true, purchased = true, message = "Phong cách đã được mở khóa." });
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    success = false,
                    purchased = false,
                    message = benefit.Message
                });
            }
            if (expiresAt > now)
            {
                order["expires_at"] = expiresAt;
                order["expiresAt"] = expiresAt;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    await _repo.UpdateAsync(PlanOrdersCollection, id, new Dictionary<string, object?>
                    {
                        ["expires_at"] = expiresAt,
                        ["expiresAt"] = expiresAt,
                        ["updated_at"] = now
                    });
                }
                return Ok(ToPaymentResponse(order, style, id));
            }
            if (!string.IsNullOrWhiteSpace(id) && expiresAt != DateTime.MinValue && expiresAt <= now)
            {
                await _repo.UpdateAsync(PlanOrdersCollection, id, new Dictionary<string, object?>
                {
                    ["status"] = "Hết hạn",
                    ["payment_status"] = "Hết hạn",
                    ["paymentStatus"] = "Hết hạn",
                    ["expired_at"] = now,
                    ["expiredAt"] = now,
                    ["updated_at"] = now
                });
            }
        }

        var expires = now.AddMinutes(PaymentExpireMinutes);
        var orderId = await _repo.AddAsync(PlanOrdersCollection, new Dictionary<string, object?>
        {
            ["order_type"] = "chatbot_style",
            ["orderType"] = "chatbot_style",
            ["buyer_id"] = current.userId,
            ["buyerId"] = current.userId,
            ["buyer_name"] = FirstText(current.authUser!, "displayName", "display_name", "username", "email"),
            ["buyerName"] = FirstText(current.authUser!, "displayName", "display_name", "username", "email"),
            ["buyer_email"] = FirstText(current.authUser!, "email"),
            ["buyerEmail"] = FirstText(current.authUser!, "email"),
            ["style_id"] = style.Id,
            ["styleId"] = style.Id,
            ["style_name"] = style.Name,
            ["styleName"] = style.Name,
            ["price_amount"] = style.Price,
            ["priceAmount"] = style.Price,
            ["status"] = "Khách đặt",
            ["created_at"] = now,
            ["expires_at"] = expires,
            ["expiresAt"] = expires,
            ["updated_at"] = now
        });

        var safeOrderId = string.IsNullOrWhiteSpace(orderId) ? $"STYLE-{DateTime.UtcNow:yyyyMMddHHmmssfff}" : orderId;
        var paymentCode = AutomaticPaymentService.CreatePaymentCode(
            safeOrderId,
            _sePay.PaymentCodePrefix,
            _sePay.PaymentCodeSuffixLength);
        var paymentContent = paymentCode;
        var qrUrl = BuildQrUrl(style.Price, paymentContent);
        await _repo.UpdateAsync(PlanOrdersCollection, safeOrderId, new Dictionary<string, object?>
        {
            ["payment_bank"] = _sePay.BankCode,
            ["paymentBank"] = _sePay.BankCode,
            ["payment_account"] = _sePay.BankAccountNumber,
            ["paymentAccount"] = _sePay.BankAccountNumber,
            ["payment_account_name"] = _sePay.BankAccountName,
            ["paymentAccountName"] = _sePay.BankAccountName,
            ["payment_code"] = paymentCode,
            ["paymentCode"] = paymentCode,
            ["payment_content"] = paymentContent,
            ["paymentContent"] = paymentContent,
            ["payment_qr_url"] = qrUrl,
            ["paymentQrUrl"] = qrUrl,
            ["updated_at"] = now
        });

        var saved = await _repo.GetByIdAsync(PlanOrdersCollection, safeOrderId) ?? new Dictionary<string, object?>();
        return Ok(ToPaymentResponse(saved, style, safeOrderId));
    }

    [HttpGet("style-orders/{orderId}")]
    public async Task<IActionResult> GetStyleOrder(string orderId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var order = await _repo.GetByIdAsync(PlanOrdersCollection, orderId);
        if (order is null || !IsStyleOrder(order)) return NotFound(new { success = false, message = "Không tìm thấy đơn mua phong cách." });
        if (!string.Equals(FirstText(order, "buyer_id", "buyerId"), current.userId, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status403Forbidden, new { success = false, message = "Bạn không có quyền xem đơn này." });

        var status = FirstText(order, "status");
        if (!string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase))
        {
            await _automaticPaymentService.TryReconcileOrderAsync(orderId, order, HttpContext.RequestAborted);
            order = await _repo.GetByIdAsync(PlanOrdersCollection, orderId) ?? order;
            status = FirstText(order, "status");
        }
        var styleId = FirstText(order, "style_id", "styleId");
        var sold = string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase);
        PaymentBenefitResult? benefit = null;
        var purchased = false;
        if (sold)
        {
            benefit = await _automaticPaymentService.EnsureOrderBenefitsAsync(orderId, order);
            purchased = benefit.Success && await _settings.UserOwnsStyleAsync(current.userId!, styleId);
            if (purchased) order = await _repo.GetByIdAsync(PlanOrdersCollection, orderId) ?? order;
        }
        var expiresAtText = FirstText(order, "expires_at", "expiresAt");
        var effectiveExpiresAt = GetEffectiveStylePaymentExpiry(order);
        var expired = !purchased && effectiveExpiresAt != DateTime.MinValue && effectiveExpiresAt <= DateTime.UtcNow;
        if (expired && string.Equals(status, "Khách đặt", StringComparison.OrdinalIgnoreCase))
        {
            await _repo.UpdateAsync(PlanOrdersCollection, orderId, new Dictionary<string, object?>
            {
                ["status"] = "Hết hạn",
                ["payment_status"] = "Hết hạn",
                ["paymentStatus"] = "Hết hạn",
                ["expired_at"] = DateTime.UtcNow,
                ["expiredAt"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });
            status = "Hết hạn";
        }

        object expiresAtResponse = effectiveExpiresAt == DateTime.MinValue
            ? expiresAtText
            : effectiveExpiresAt;

        return Ok(new
        {
            success = true,
            message = purchased
                ? "Thanh toán thành công. Phong cách đã mở khóa."
                : sold
                    ? benefit?.Message ?? "Đã nhận thanh toán. Đang mở khóa phong cách."
                : expired
                    ? "Mã đã hết hạn. Hãy tạo mã mới."
                    : "Đang chờ thanh toán.",
            data = new
            {
                orderId,
                styleId,
                status = purchased ? status : sold ? "Đang kích hoạt" : status,
                purchased,
                benefitsApplied = purchased,
                expired,
                paymentStatus = FirstText(order, "payment_status", "paymentStatus"),
                expiresAt = expiresAtResponse,
                expiresAtUnixMs = ToUnixMilliseconds(effectiveExpiresAt)
            }
        });
    }

    private object ToUserSettingsResponse(ChatbotUserConfiguration settings)
    {
        var policy = _rolePolicies.GetPolicy(settings.Role);
        return new
        {
            chatbotName = settings.Configuration.ChatbotName,
            selectedStyleId = settings.SelectedStyleId,
            defaultStyleId = settings.Configuration.DefaultStyleId,
            role = settings.Role,
            canChangeStyle = settings.CanChangeStyle,
            hasAllStyles = settings.HasAllStyles,
            styles = settings.Configuration.Styles.Select(item =>
            {
                var owned = item.IsFree || settings.OwnedStyleIds.Contains(item.Id) || policy.HasAllChatbotStyles;
                return new
                {
                    id = item.Id,
                    name = item.Name,
                    price = item.Price,
                    isFree = item.IsFree,
                    maxResponseWords = item.MaxResponseWords,
                    owned,
                    locked = !owned,
                    canSelect = settings.CanChangeStyle && owned,
                    canPurchase = policy.CanChangeChatbotStyle && !policy.HasAllChatbotStyles && !item.IsFree && !owned
                };
            })
        };
    }

    private object ToPaymentResponse(Dictionary<string, object?> order, ChatbotConversationStyle style, string orderId)
    {
        var paymentContent = FirstText(order, "payment_content", "paymentContent", "payment_code", "paymentCode");
        return new
        {
            success = true,
            purchased = false,
            message = "Đã tạo mã thanh toán. Quét QR để thanh toán.",
            data = new
            {
                orderId,
                styleId = style.Id,
                styleName = style.Name,
                amount = style.Price,
                status = FirstText(order, "status"),
                expiresAt = GetEffectiveStylePaymentExpiry(order),
                expiresAtUnixMs = ToUnixMilliseconds(GetEffectiveStylePaymentExpiry(order)),
                paymentBank = _sePay.BankCode,
                paymentAccount = _sePay.BankAccountNumber,
                paymentAccountName = _sePay.BankAccountName,
                paymentContent,
                paymentQrUrl = BuildQrUrl(style.Price, paymentContent)
            }
        };
    }

    private static DateTime GetEffectiveStylePaymentExpiry(Dictionary<string, object?> order)
    {
        // Use the same deadline for display, polling and deletion.
        var configuredExpiry = ParseDate(FirstText(order, "expires_at", "expiresAt"));
        if (configuredExpiry != DateTime.MinValue) return configuredExpiry;

        var createdAt = ParseDate(FirstText(order, "created_at", "createdAt"));
        return createdAt == DateTime.MinValue
            ? DateTime.MinValue
            : createdAt.AddMinutes(PaymentExpireMinutes);
    }

    private static bool IsStyleOrder(Dictionary<string, object?> order)
        => string.Equals(FirstText(order, "order_type", "orderType"), "chatbot_style", StringComparison.OrdinalIgnoreCase);

    private static long ToUnixMilliseconds(DateTime value)
    {
        if (value == DateTime.MinValue) return 0L;
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    private string BuildQrUrl(decimal amount, string content)
        => $"https://img.vietqr.io/image/{_sePay.BankCode}-{_sePay.BankAccountNumber}-compact2.png?amount={(long)Math.Round(amount, 0, MidpointRounding.AwayFromZero)}&addInfo={Uri.EscapeDataString(content)}&accountName={Uri.EscapeDataString(_sePay.BankAccountName)}";

    private static string FirstText(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetValue(key, out var value)) continue;
            var text = value?.ToString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return string.Empty;
    }

    private static DateTime ParseDate(string value) => DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : DateTime.MinValue;
}

public sealed class ChatbotStyleSelectionRequest
{
    public string? StyleId { get; set; }
}
