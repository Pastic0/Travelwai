using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;
using TravelwAI.Web.Options;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/commerce")]
public sealed class CommerceApiController : ApiControllerBase
{
    private const string CartCollection = "commerce_cart";
    private const string PlanOrdersCollection = "plan_orders";
    private const string BusinessApplicationsCollection = "business_applications";
    private const int PlanPaymentExpireMinutes = 5;
    private readonly IDataRepository _repo;
    private readonly TourOfferService _offerService;
    private readonly EmailNotificationService _emailNotificationService;
    private readonly PlanQueueService _planQueueService;
    private readonly AccountPlanSettingsService _accountPlanSettings;
    private readonly AutomaticPaymentService _automaticPaymentService;
    private readonly InAppNotificationService _notifications;
    private readonly NpgsqlDataSource _dataSource;
    private readonly SePayOptions _sePay;

    public CommerceApiController(
        IAuthService authService,
        IDataRepository repo,
        TourOfferService offerService,
        EmailNotificationService emailNotificationService,
        PlanQueueService planQueueService,
        AccountPlanSettingsService accountPlanSettings,
        AutomaticPaymentService automaticPaymentService,
        InAppNotificationService notifications,
        NpgsqlDataSource dataSource,
        IOptions<SePayOptions> sePayOptions) : base(authService)
    {
        _repo = repo;
        _offerService = offerService;
        _emailNotificationService = emailNotificationService;
        _planQueueService = planQueueService;
        _accountPlanSettings = accountPlanSettings;
        _automaticPaymentService = automaticPaymentService;
        _notifications = notifications;
        _dataSource = dataSource;
        _sePay = sePayOptions.Value;
    }

    [HttpGet("cart")]
    public async Task<IActionResult> GetCart()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var rows = await _repo.WhereEqualAsync(CartCollection, "buyer_id", current.userId!, limit: 200);
        foreach (var row in rows.Where(row => string.Equals(Text(row, "status"), "Trong giỏ", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            await MarkCartItemExpiredIfTourSoldOutAsync(row);
        }
        rows = rows
            .Where(IsVisibleCartStatus)
            .OrderByDescending(row => ParseDate(row.GetValueOrDefault("created_at")))
            .ToList();
        return Ok(new { success = true, data = rows });
    }

    [HttpGet("cart/{id}")]
    public async Task<IActionResult> GetCartItem(string id)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var item = await _repo.GetByIdAsync(CartCollection, id);
        if (item is null) return NotFound(new { success = false, message = "Không tìm thấy sản phẩm trong giỏ hàng." });
        if (!IsOwner(item, current.userId)) return StatusCode(403, new { success = false, message = "Bạn không có quyền xem sản phẩm này." });
        await MarkCartItemExpiredIfTourSoldOutAsync(item);
        return Ok(new { success = true, data = item });
    }

    [HttpPost("cart/tour")]
    public async Task<IActionResult> AddTourToCart([FromBody] TourCartRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var tourId = (request.TourId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(tourId)) return BadRequest(new { success = false, message = "Thiếu mã tour." });

        var tour = await _repo.GetByIdAsync("tours", tourId);
        if (tour is null) return NotFound(new { success = false, message = "Không tìm thấy tour." });
        if (IsTourSoldOut(tour))
        {
            return BadRequest(new { success = false, message = "Tour đã bán hết." });
        }
        if (!string.Equals(Text(tour, "status"), "Đang bán", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Tour này hiện không nhận đặt chỗ." });
        }

        var quantity = Math.Max(1, request.Quantity ?? 1);
        var slots = Int(tour, "slots");
        var sold = Int(tour, "sold");
        var pendingQuantity = await GetPendingTourQuantityAsync(tourId);
        if (slots > 0 && sold + pendingQuantity + quantity > slots)
        {
            return BadRequest(new { success = false, message = "Tour không còn đủ chỗ." });
        }

        var buyerEmail = Text(current.authUser!, "email");
        var buyerName = FirstText(current.authUser!, "displayName", "display_name", "username", "email");
        if (!string.IsNullOrWhiteSpace(request.CustomerName)) buyerName = request.CustomerName.Trim();
        if (!string.IsNullOrWhiteSpace(request.CustomerEmail)) buyerEmail = request.CustomerEmail.Trim();

        var price = Decimal(tour, "price");
        var now = DateTime.UtcNow;
        var id = await _repo.AddAsync(CartCollection, new Dictionary<string, object?>
        {
            ["item_type"] = "tour",
            ["itemType"] = "tour",
            ["tour_id"] = tourId,
            ["tourId"] = tourId,
            ["tour_name"] = Text(tour, "name"),
            ["tourName"] = Text(tour, "name"),
            ["tour_start_date"] = Text(tour, "start_date"),
            ["tour_end_date"] = Text(tour, "end_date"),
            ["tour_duration"] = Text(tour, "duration"),
            ["tour_sales_id"] = FirstText(tour, "tour_sales_id", "tourSalesId", "created_by", "createdBy", "seller_id", "sellerId"),
            ["tour_sales_name"] = FirstText(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName"),
            ["buyer_id"] = current.userId,
            ["buyerId"] = current.userId,
            ["buyer_name"] = buyerName,
            ["buyerName"] = buyerName,
            ["buyer_email"] = buyerEmail,
            ["buyerEmail"] = buyerEmail,
            ["customer_name"] = buyerName,
            ["customer_email"] = buyerEmail,
            ["quantity"] = quantity,
            ["unit_price"] = price,
            ["unitPrice"] = price,
            ["total_price"] = price * quantity,
            ["totalPrice"] = price * quantity,
            ["status"] = "Trong giỏ",
            ["created_at"] = now,
            ["updated_at"] = now
        });

        return Ok(new { success = true, message = "Đã thêm tour vào giỏ hàng.", cart_id = id, cartId = id, checkout_url = $"/checkout?cartId={Uri.EscapeDataString(id ?? string.Empty)}" });
    }

    [HttpDelete("cart/{id}")]
    public async Task<IActionResult> DeleteCartItem(string id)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var item = await _repo.GetByIdAsync(CartCollection, id);
        if (item is null) return NotFound(new { success = false, message = "Không tìm thấy sản phẩm trong giỏ hàng." });
        if (!IsOwner(item, current.userId)) return StatusCode(403, new { success = false, message = "Bạn không có quyền xoá sản phẩm này." });
        await _repo.DeleteAsync(CartCollection, id);
        return Ok(new { success = true, message = "Đã xoá khỏi giỏ hàng." });
    }

    [HttpPost("checkout/cart/{id}/pay")]
    public async Task<IActionResult> PayCartItem(string id)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var item = await _repo.GetByIdAsync(CartCollection, id);
        if (item is null) return NotFound(new { success = false, message = "Không tìm thấy sản phẩm thanh toán." });
        if (!IsOwner(item, current.userId)) return StatusCode(403, new { success = false, message = "Bạn không có quyền thanh toán sản phẩm này." });
        if (await MarkCartItemExpiredIfTourSoldOutAsync(item))
        {
            return BadRequest(new { success = false, expired = true, message = "Tour đã bán hết. Đơn trong giỏ đã hết hạn." });
        }
        if (!string.Equals(Text(item, "status"), "Trong giỏ", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Sản phẩm này đã được xử lý." });
        }

        var type = FirstText(item, "item_type", "itemType").ToLowerInvariant();
        if (type != "tour") return BadRequest(new { success = false, message = "Loại sản phẩm không hợp lệ." });

        var orderResult = await CreateTourOrderFromCartAsync(item, current.userId!, current.authUser);
        if (!orderResult.success) return BadRequest(new { success = false, message = orderResult.message });

        await _repo.UpdateAsync(CartCollection, id, new Dictionary<string, object?>
        {
            ["status"] = "Đã thanh toán",
            ["order_id"] = orderResult.orderId,
            ["orderId"] = orderResult.orderId,
            ["updated_at"] = DateTime.UtcNow
        });

        var tourName = FirstText(item, "tour_name", "tourName");
        if (string.IsNullOrWhiteSpace(tourName)) tourName = "tour du lịch";
        await _notifications.CreateForUserAsync(
            current.userId!,
            "tour",
            InAppNotificationService.TourBookedCategory,
            "Đã đặt tour",
            $"Bạn đã đặt tour {tourName}. Mã đơn: {orderResult.orderId}.",
            "/tours",
            "tour-order",
            orderResult.orderId,
            "tour-booked");
        await _notifications.CreateForUserAsync(
            current.userId!,
            "payment",
            InAppNotificationService.PaymentSuccessCategory,
            "Thanh toán thành công",
            $"Thanh toán cho tour {tourName} đã thành công. Mã đơn: {orderResult.orderId}.",
            "/tours",
            "tour-payment",
            orderResult.orderId,
            "payment-success");

        return Ok(new { success = true, message = orderResult.message, order_id = orderResult.orderId, orderId = orderResult.orderId });
    }

    [HttpGet("plan-eligibility")]
    public async Task<IActionResult> CheckPlanEligibility([FromQuery] string? plan)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var role = NormalizePlanRole(plan);
        if (string.IsNullOrWhiteSpace(role)) return BadRequest(new { success = false, message = "Gói tài khoản không hợp lệ." });
        await DeleteExpiredPendingPlanOrdersAsync(current.userId!);
        var pendingOrder = await GetPendingPlanOrderAsync(current.userId!);
        if (pendingOrder is not null)
        {
            pendingOrder = await RefreshPendingPlanPaymentDestinationAsync(pendingOrder);
        }
        var state = await _planQueueService.SyncUserAsync(current.userId!, NormalizeAccountRole(current.authUser?.GetValueOrDefault("role")));
        var validation = await ValidatePlanOrderAsync(current.userId!, current.authUser, role, state.CurrentRole, pendingOrder);
        var monthlyPrice = await _accountPlanSettings.GetMonthlyAmountAsync(role);
        return Ok(new
        {
            success = true,
            can_buy = validation.ok,
            canBuy = validation.ok,
            message = validation.message,
            current_role = state.CurrentRole,
            currentRole = state.CurrentRole,
            current_plan_started_at = state.CurrentStartedAt,
            currentPlanStartedAt = state.CurrentStartedAt,
            current_plan_expires_at = state.CurrentExpiresAt,
            currentPlanExpiresAt = state.CurrentExpiresAt,
            next_plan_role = state.NextRole,
            nextPlanRole = state.NextRole,
            next_plan_started_at = state.NextStartedAt,
            nextPlanStartedAt = state.NextStartedAt,
            next_plan_expires_at = state.NextExpiresAt,
            nextPlanExpiresAt = state.NextExpiresAt,
            plan_countdown_seconds = state.CountdownSeconds,
            planCountdownSeconds = state.CountdownSeconds,
            plan_role = role,
            planRole = role,
            monthly_price_amount = monthlyPrice,
            monthlyPriceAmount = monthlyPrice,
            year_discount_percent = PlanYearDiscountPercent,
            yearDiscountPercent = PlanYearDiscountPercent,
            has_pending_order = pendingOrder is not null,
            hasPendingOrder = pendingOrder is not null,
            pending_order_id = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "id", "Id"),
            pendingOrderId = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "id", "Id"),
            pending_plan_role = pendingOrder is null ? string.Empty : NormalizePlanRole(FirstText(pendingOrder, "plan_role", "planRole", "role")),
            pendingPlanRole = pendingOrder is null ? string.Empty : NormalizePlanRole(FirstText(pendingOrder, "plan_role", "planRole", "role")),
            pending_duration_months = pendingOrder is null ? 0 : FirstInt(pendingOrder, "duration_months", "durationMonths"),
            pendingDurationMonths = pendingOrder is null ? 0 : FirstInt(pendingOrder, "duration_months", "durationMonths"),
            pending_order_expires_at = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "expires_at", "expiresAt"),
            pendingOrderExpiresAt = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "expires_at", "expiresAt"),
            pending_order_expires_at_unix_ms = pendingOrder is null ? 0L : ToUnixMilliseconds(GetEffectivePlanPaymentExpiry(pendingOrder)),
            pendingOrderExpiresAtUnixMs = pendingOrder is null ? 0L : ToUnixMilliseconds(GetEffectivePlanPaymentExpiry(pendingOrder)),
            pending_payment_bank = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "payment_bank", "paymentBank"),
            pendingPaymentBank = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "payment_bank", "paymentBank"),
            pending_payment_account = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "payment_account", "paymentAccount"),
            pendingPaymentAccount = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "payment_account", "paymentAccount"),
            pending_payment_account_name = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "payment_account_name", "paymentAccountName"),
            pendingPaymentAccountName = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "payment_account_name", "paymentAccountName"),
            pending_payment_content = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "payment_content", "paymentContent"),
            pendingPaymentContent = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "payment_content", "paymentContent"),
            pending_payment_qr_url = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "payment_qr_url", "paymentQrUrl"),
            pendingPaymentQrUrl = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "payment_qr_url", "paymentQrUrl"),
            pending_monthly_price = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "unit_price", "unitPrice"),
            pendingMonthlyPrice = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "unit_price", "unitPrice"),
            pending_original_amount = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "original_price_amount", "originalPriceAmount"),
            pendingOriginalAmount = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "original_price_amount", "originalPriceAmount"),
            pending_discount_percent = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "discount_percent", "discountPercent"),
            pendingDiscountPercent = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "discount_percent", "discountPercent"),
            pending_discount_amount = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "discount_amount", "discountAmount"),
            pendingDiscountAmount = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "discount_amount", "discountAmount"),
            pending_amount = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "price_amount", "priceAmount"),
            pendingAmount = pendingOrder is null ? string.Empty : FirstText(pendingOrder, "price_amount", "priceAmount")
        });
    }

    [HttpPost("plan-orders")]
    public async Task<IActionResult> CreatePlanOrder([FromBody] PlanOrderRequest request)
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
        var role = NormalizePlanRole(request.PlanRole ?? request.Role);
        if (role is not ("VIP" or "Premium"))
        {
            return BadRequest(new { success = false, message = "Gói Sales và Company phải gửi biểu mẫu đăng ký cho Admin." });
        }

        await using var planOrderLockConnection = await _dataSource.OpenConnectionAsync(HttpContext.RequestAborted);
        var planOrderLockKey = $"travelwai-plan-order:{current.userId}";
        await using (var planOrderLockCommand = planOrderLockConnection.CreateCommand())
        {
            planOrderLockCommand.CommandText = "select pg_advisory_lock(hashtextextended(@lock_key, 0));";
            planOrderLockCommand.Parameters.AddWithValue("lock_key", planOrderLockKey);
            await planOrderLockCommand.ExecuteScalarAsync(HttpContext.RequestAborted);
        }

        try
        {
            await DeleteExpiredPendingPlanOrdersAsync(current.userId!);
            var pendingOrder = await GetPendingPlanOrderAsync(current.userId!);
            var state = await _planQueueService.SyncUserAsync(current.userId!, NormalizeAccountRole(current.authUser?.GetValueOrDefault("role")));
            var validation = await ValidatePlanOrderAsync(current.userId!, current.authUser, role, state.CurrentRole, pendingOrder);
            if (!validation.ok) return BadRequest(new { success = false, message = validation.message });

            var months = NormalizePlanMonths(request.Months ?? request.DurationMonths);
            var pricing = CalculatePlanPricing(await _accountPlanSettings.GetMonthlyAmountAsync(role), months);
            var now = DateTime.UtcNow;
            var expiresAt = now.AddMinutes(PlanPaymentExpireMinutes);
            var email = Text(current.authUser!, "email");
            var name = FirstText(current.authUser!, "displayName", "display_name", "username", "email");
            var currentRole = state.CurrentRole;
            var orderId = await _repo.AddAsync(PlanOrdersCollection, new Dictionary<string, object?>
            {
                ["buyer_id"] = current.userId,
                ["buyerId"] = current.userId,
                ["buyer_name"] = name,
                ["buyerName"] = name,
                ["buyer_email"] = email,
                ["buyerEmail"] = email,
                ["plan_role"] = role,
                ["planRole"] = role,
                ["plan_name"] = role,
                ["planName"] = role,
                ["current_role"] = currentRole,
                ["currentRole"] = currentRole,
                ["duration_months"] = months,
                ["durationMonths"] = months,
                ["unit_price"] = pricing.monthlyPrice,
                ["unitPrice"] = pricing.monthlyPrice,
                ["original_price_amount"] = pricing.originalAmount,
                ["originalPriceAmount"] = pricing.originalAmount,
                ["discount_percent"] = pricing.discountPercent,
                ["discountPercent"] = pricing.discountPercent,
                ["discount_amount"] = pricing.discountAmount,
                ["discountAmount"] = pricing.discountAmount,
                ["price_text"] = pricing.priceText,
                ["priceText"] = pricing.priceText,
                ["price_amount"] = pricing.finalAmount,
                ["priceAmount"] = pricing.finalAmount,
                ["status"] = "Khách đặt",
                ["created_at"] = now,
                ["expires_at"] = expiresAt,
                ["expiresAt"] = expiresAt,
                ["updated_at"] = now
            });

            var safeOrderId = string.IsNullOrWhiteSpace(orderId) ? $"TWAI-{DateTime.UtcNow:yyyyMMddHHmmssfff}" : orderId;
            var paymentCode = AutomaticPaymentService.CreatePaymentCode(
                safeOrderId,
                _sePay.PaymentCodePrefix,
                _sePay.PaymentCodeSuffixLength);
            var paymentContent = paymentCode;
            var qrUrl = PlanQrUrl(pricing.finalAmount, paymentContent);
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

            return Ok(new
            {
                success = true,
                message = "Đã tạo mã thanh toán. Quét QR để thanh toán.",
                order_id = safeOrderId,
                orderId = safeOrderId,
                expires_at = expiresAt,
                expiresAt = expiresAt,
                expires_at_unix_ms = new DateTimeOffset(expiresAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                expiresAtUnixMs = new DateTimeOffset(expiresAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                payment_bank = _sePay.BankCode,
                paymentBank = _sePay.BankCode,
                payment_account = _sePay.BankAccountNumber,
                paymentAccount = _sePay.BankAccountNumber,
                payment_account_name = _sePay.BankAccountName,
                paymentAccountName = _sePay.BankAccountName,
                payment_code = paymentCode,
                paymentCode = paymentCode,
                payment_content = paymentContent,
                paymentContent = paymentContent,
                payment_qr_url = qrUrl,
                paymentQrUrl = qrUrl,
                amount = pricing.finalAmount
            });
        }
        finally
        {
            try
            {
                await using var planOrderUnlockCommand = planOrderLockConnection.CreateCommand();
                planOrderUnlockCommand.CommandText = "select pg_advisory_unlock(hashtextextended(@lock_key, 0));";
                planOrderUnlockCommand.Parameters.AddWithValue("lock_key", planOrderLockKey);
                await planOrderUnlockCommand.ExecuteScalarAsync();
            }
            catch
            {

            }
        }
    }

    [HttpGet("plan-orders/{id}/status")]
    public async Task<IActionResult> GetPlanOrderStatus(string id)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var order = await _repo.GetByIdAsync(PlanOrdersCollection, id);
        if (order is null) return NotFound(new { success = false, message = "Không tìm thấy đơn thanh toán." });
        if (!IsOwner(order, current.userId)) return StatusCode(403, new { success = false, message = "Bạn không có quyền xem đơn này." });

        var status = Text(order, "status");
        if (!string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase))
        {
            await _automaticPaymentService.TryReconcileOrderAsync(id, order, HttpContext.RequestAborted);
            order = await _repo.GetByIdAsync(PlanOrdersCollection, id) ?? order;
            status = Text(order, "status");
        }
        var sold = string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase);
        PaymentBenefitResult? benefit = null;
        PlanQueueState? planState = null;
        if (sold)
        {
            benefit = await _automaticPaymentService.EnsureOrderBenefitsAsync(id, order);
            if (benefit.Success)
            {
                order = await _repo.GetByIdAsync(PlanOrdersCollection, id) ?? order;
                planState = await _planQueueService.SyncUserAsync(current.userId!);
            }
        }
        var paid = sold && benefit?.Success == true;
        var expiresAt = GetEffectivePlanPaymentExpiry(order);
        var expired = !paid && expiresAt != DateTime.MinValue && expiresAt <= DateTime.UtcNow;
        if (expired && string.Equals(status, "Khách đặt", StringComparison.OrdinalIgnoreCase))
        {
            await _repo.UpdateAsync(PlanOrdersCollection, id, new Dictionary<string, object?>
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
        object expiresAtResponse = expiresAt == DateTime.MinValue
            ? FirstText(order, "expires_at", "expiresAt")
            : expiresAt;
        return Ok(new
        {
            success = true,
            paid,
            expired,
            status = paid ? status : sold ? "Đang kích hoạt" : status,
            message = paid
                ? "Thanh toán thành công. Gói đã kích hoạt."
                : sold
                    ? benefit?.Message ?? "Đã nhận thanh toán. Đang kích hoạt gói."
                : expired
                    ? "Mã đã hết hạn. Hãy tạo mã mới."
                    : "Đang chờ thanh toán.",
            order_id = id,
            orderId = id,
            payment_status = FirstText(order, "payment_status", "paymentStatus"),
            paymentStatus = FirstText(order, "payment_status", "paymentStatus"),
            benefits_applied = paid,
            benefitsApplied = paid,
            role = planState?.CurrentRole ?? benefit?.CurrentRole ?? string.Empty,
            expires_at = expiresAtResponse,
            expiresAt = expiresAtResponse,
            expires_at_unix_ms = ToUnixMilliseconds(expiresAt),
            expiresAtUnixMs = ToUnixMilliseconds(expiresAt)
        });
    }

    [HttpPost("business-application")]
    public async Task<IActionResult> SubmitBusinessApplication([FromBody] BusinessApplicationRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var role = NormalizePlanRole(request.PlanRole ?? request.Role);
        if (role is not ("Sales" or "Company")) return BadRequest(new { success = false, message = "Loại đăng ký không hợp lệ." });
        if (string.IsNullOrWhiteSpace(request.CompanyName)) return BadRequest(new { success = false, message = "Vui lòng nhập tên công ty / cá nhân kinh doanh." });
        if (string.IsNullOrWhiteSpace(request.BusinessType)) return BadRequest(new { success = false, message = "Vui lòng nhập loại hình." });
        if (string.IsNullOrWhiteSpace(request.Province)) return BadRequest(new { success = false, message = "Vui lòng nhập tỉnh / thành phố." });
        if (string.IsNullOrWhiteSpace(request.ContactName)) return BadRequest(new { success = false, message = "Vui lòng nhập họ và tên người phụ trách." });
        if (string.IsNullOrWhiteSpace(request.Phone)) return BadRequest(new { success = false, message = "Vui lòng nhập số điện thoại." });
        if (string.IsNullOrWhiteSpace(request.Email)) return BadRequest(new { success = false, message = "Vui lòng nhập email." });

        var currentRole = NormalizeAccountRole(current.authUser?.GetValueOrDefault("role"));
        await DeleteExpiredPendingPlanOrdersAsync(current.userId!);
        if (string.Equals(currentRole, "Admin", StringComparison.OrdinalIgnoreCase)) return BadRequest(new { success = false, message = "Admin không cần đăng ký gói." });
        var existing = await _repo.WhereEqualAsync(BusinessApplicationsCollection, "user_id", current.userId!, limit: 50);
        var hasPendingSameRole = existing.Any(item => IsActivePlanRecord(item) && string.Equals(NormalizePlanRole(FirstText(item, "plan_role", "planRole", "role")), role, StringComparison.OrdinalIgnoreCase));
        if (hasPendingSameRole) return BadRequest(new { success = false, message = "Bạn đã gửi biểu mẫu đăng ký gói này rồi." });

        var now = DateTime.UtcNow;
        var data = new Dictionary<string, object?>
        {
            ["user_id"] = current.userId,
            ["userId"] = current.userId,
            ["user_email"] = Text(current.authUser!, "email"),
            ["userEmail"] = Text(current.authUser!, "email"),
            ["plan_role"] = role,
            ["planRole"] = role,
            ["company_name"] = request.CompanyName.Trim(),
            ["companyName"] = request.CompanyName.Trim(),
            ["business_type"] = request.BusinessType.Trim(),
            ["businessType"] = request.BusinessType.Trim(),
            ["tax_code"] = request.TaxCode?.Trim() ?? string.Empty,
            ["taxCode"] = request.TaxCode?.Trim() ?? string.Empty,
            ["office_address"] = request.OfficeAddress?.Trim() ?? string.Empty,
            ["officeAddress"] = request.OfficeAddress?.Trim() ?? string.Empty,
            ["province"] = request.Province.Trim(),
            ["website"] = request.Website?.Trim() ?? string.Empty,
            ["contact_name"] = request.ContactName.Trim(),
            ["contactName"] = request.ContactName.Trim(),
            ["position"] = request.Position?.Trim() ?? string.Empty,
            ["phone"] = request.Phone.Trim(),
            ["email"] = request.Email.Trim(),
            ["status"] = "Chờ xử lý",
            ["created_at"] = now,
            ["updated_at"] = now
        };
        var id = await _repo.AddAsync(BusinessApplicationsCollection, data);
        var emailError = await _emailNotificationService.SendBusinessApplicationToAdminAsync(data);
        return Ok(new
        {
            success = true,
            message = "Đã gửi biểu mẫu.",
            application_id = id,
            applicationId = id,
            emailSent = string.IsNullOrWhiteSpace(emailError),
            emailWarning = emailError
        });
    }

    private async Task<(bool success, string message, string orderId)> CreateTourOrderFromCartAsync(Dictionary<string, object?> item, string userId, Dictionary<string, object?>? authUser)
    {
        var tourId = FirstText(item, "tour_id", "tourId");
        var tour = await _repo.GetByIdAsync("tours", tourId);
        if (tour is null) return (false, "Không tìm thấy tour.", string.Empty);
        if (IsTourSoldOut(tour)) return (false, "Tour đã bán hết.", string.Empty);
        if (!string.Equals(Text(tour, "status"), "Đang bán", StringComparison.OrdinalIgnoreCase)) return (false, "Tour này hiện không nhận đặt chỗ.", string.Empty);

        var quantity = Math.Max(1, Int(item, "quantity"));
        var slots = Int(tour, "slots");
        var sold = Int(tour, "sold");
        var currentCartId = FirstText(item, "id", "Id");
        var pendingQuantity = await GetPendingTourQuantityAsync(tourId, currentCartId);
        if (slots > 0 && sold + pendingQuantity + quantity > slots) return (false, "Tour không còn đủ chỗ.", string.Empty);

        var price = Decimal(tour, "price");
        var originalTotal = price * quantity;
        var bookingDiscount = await _offerService.GetBookingDiscountAsync(userId);
        var discountPercent = bookingDiscount.DiscountPercent;
        var discountAmount = Math.Round(originalTotal * discountPercent / 100m, 0, MidpointRounding.AwayFromZero);
        var total = Math.Max(0m, originalTotal - discountAmount);
        var now = DateTime.UtcNow;
        var buyerEmail = FirstText(item, "buyer_email", "buyerEmail", "customer_email", "customerEmail");
        if (string.IsNullOrWhiteSpace(buyerEmail)) buyerEmail = Text(authUser ?? new Dictionary<string, object?>(), "email");
        var buyerName = FirstText(item, "buyer_name", "buyerName", "customer_name", "customerName");
        if (string.IsNullOrWhiteSpace(buyerName)) buyerName = FirstText(authUser ?? new Dictionary<string, object?>(), "displayName", "display_name", "username", "email");

        var orderId = await _repo.AddAsync("tour_orders", new Dictionary<string, object?>
        {
            ["tour_id"] = tourId,
            ["tour_name"] = Text(tour, "name"),
            ["tour_start_date"] = Text(tour, "start_date"),
            ["tour_end_date"] = Text(tour, "end_date"),
            ["tour_duration"] = Text(tour, "duration"),
            ["tour_sales_id"] = FirstText(tour, "tour_sales_id", "tourSalesId", "created_by", "createdBy", "seller_id", "sellerId"),
            ["tourSalesId"] = FirstText(tour, "tour_sales_id", "tourSalesId", "created_by", "createdBy", "seller_id", "sellerId"),
            ["tour_sales_name"] = FirstText(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName"),
            ["tourSalesName"] = FirstText(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName"),
            ["schedule_id"] = string.Empty,
            ["auto_schedule_created"] = false,
            ["customer_name"] = buyerName,
            ["customer_email"] = buyerEmail,
            ["customer_phone"] = string.Empty,
            ["quantity"] = quantity,
            ["unit_price"] = price,
            ["original_total_price"] = originalTotal,
            ["discount_percent"] = discountPercent,
            ["discount_amount"] = discountAmount,
            ["invite_discount_percent"] = bookingDiscount.InviteDiscountPercent,
            ["post_offer_discount_percent"] = bookingDiscount.PostOfferDiscountPercent,
            ["discount_source"] = bookingDiscount.Source,
            ["post_offer_id"] = bookingDiscount.PostOfferId,
            ["total_price"] = total,
            ["status"] = "Khách đặt",
            ["buyer_id"] = userId,
            ["created_at"] = now,
            ["expires_at"] = now.AddMinutes(TourOrderAutomation.BookingHoldMinutes),
            ["updated_at"] = now
        });

        var safeOrderId = string.IsNullOrWhiteSpace(orderId) ? $"TW-{DateTime.UtcNow:yyyyMMddHHmmssfff}" : orderId;
        if (bookingDiscount.PostOfferDiscountPercent > 0) await _offerService.ConsumePostOfferAsync(userId, safeOrderId);
        await _emailNotificationService.SendTourBookingCreatedAsync(buyerEmail, buyerName, Text(tour, "name"), quantity, originalTotal, discountPercent, discountAmount, total, safeOrderId, now.AddMinutes(TourOrderAutomation.BookingHoldMinutes));
        return (true, "Thanh toán thành công.", safeOrderId);
    }

    private async Task<bool> MarkCartItemExpiredIfTourSoldOutAsync(Dictionary<string, object?> item)
    {
        if (!string.Equals(FirstText(item, "item_type", "itemType"), "tour", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(Text(item, "status"), "Trong giỏ", StringComparison.OrdinalIgnoreCase)) return false;
        var tourId = FirstText(item, "tour_id", "tourId");
        if (string.IsNullOrWhiteSpace(tourId)) return false;
        var tour = await _repo.GetByIdAsync("tours", tourId);
        if (tour is not null && !IsTourSoldOut(tour) && string.Equals(Text(tour, "status"), "Đang bán", StringComparison.OrdinalIgnoreCase)) return false;

        var id = FirstText(item, "id", "Id");
        var now = DateTime.UtcNow;
        item["status"] = "Hết hạn";
        item["expired_at"] = now;
        item["expiredAt"] = now;
        item["expires_reason"] = "Tour đã bán hết";
        item["expiresReason"] = "Tour đã bán hết";
        item["updated_at"] = now;
        if (!string.IsNullOrWhiteSpace(id))
        {
            await _repo.UpdateAsync(CartCollection, id, new Dictionary<string, object?>
            {
                ["status"] = "Hết hạn",
                ["expired_at"] = now,
                ["expiredAt"] = now,
                ["expires_reason"] = "Tour đã bán hết",
                ["expiresReason"] = "Tour đã bán hết",
                ["updated_at"] = now
            });
        }
        return true;
    }

    private static bool IsVisibleCartStatus(Dictionary<string, object?> row)
    {
        var status = Text(row, "status");
        return string.Equals(status, "Trong giỏ", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Hết hạn", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTourSoldOut(Dictionary<string, object?> tour)
    {
        var status = Text(tour, "status");
        var slots = Int(tour, "slots");
        var sold = Int(tour, "sold");
        return (slots > 0 && sold >= slots)
            || string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Hết chỗ", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> GetPendingTourQuantityAsync(string tourId, string? excludeCartId = null)
    {
        var orders = await _repo.WhereEqualAsync("tour_orders", "tour_id", tourId, limit: 500);
        var cart = await _repo.WhereEqualAsync(CartCollection, "tour_id", tourId, limit: 500);
        return orders.Where(TourOrderAutomation.IsPendingOrder).Sum(o => Int(o, "quantity"))
            + cart.Where(c => string.Equals(Text(c, "status"), "Trong giỏ", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(excludeCartId) || !string.Equals(FirstText(c, "id", "Id"), excludeCartId, StringComparison.Ordinal)))
                .Sum(c => Int(c, "quantity"));
    }

    private async Task DeleteExpiredPendingPlanOrdersAsync(string userId)
    {
        var now = DateTime.UtcNow;
        var orders = await _repo.WhereEqualAsync(PlanOrdersCollection, "buyer_id", userId, limit: 100);
        foreach (var order in orders)
        {
            var id = FirstText(order, "id", "Id");
            var status = Text(order, "status");
            var expiresAt = GetEffectivePlanPaymentExpiry(order);
            if (!string.IsNullOrWhiteSpace(id)
                && string.Equals(status, "Khách đặt", StringComparison.OrdinalIgnoreCase)
                && expiresAt != DateTime.MinValue
                && expiresAt <= now)
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
    }

    private async Task<Dictionary<string, object?>> RefreshPendingPlanPaymentDestinationAsync(Dictionary<string, object?> order)
    {
        var orderId = FirstText(order, "id", "Id");
        var paymentContent = FirstText(order, "payment_content", "paymentContent", "payment_code", "paymentCode");
        var amount = FirstDecimal(order, "price_amount", "priceAmount");
        var qrUrl = PlanQrUrl(amount, paymentContent);
        var effectiveExpiresAt = GetEffectivePlanPaymentExpiry(order);

        order["payment_bank"] = _sePay.BankCode;
        order["paymentBank"] = _sePay.BankCode;
        order["payment_account"] = _sePay.BankAccountNumber;
        order["paymentAccount"] = _sePay.BankAccountNumber;
        order["payment_account_name"] = _sePay.BankAccountName;
        order["paymentAccountName"] = _sePay.BankAccountName;
        order["payment_qr_url"] = qrUrl;
        order["paymentQrUrl"] = qrUrl;
        if (effectiveExpiresAt != DateTime.MinValue)
        {
            order["expires_at"] = effectiveExpiresAt;
            order["expiresAt"] = effectiveExpiresAt;
        }

        if (!string.IsNullOrWhiteSpace(orderId))
        {
            await _repo.UpdateAsync(PlanOrdersCollection, orderId, new Dictionary<string, object?>
            {
                ["payment_bank"] = _sePay.BankCode,
                ["paymentBank"] = _sePay.BankCode,
                ["payment_account"] = _sePay.BankAccountNumber,
                ["paymentAccount"] = _sePay.BankAccountNumber,
                ["payment_account_name"] = _sePay.BankAccountName,
                ["paymentAccountName"] = _sePay.BankAccountName,
                ["payment_qr_url"] = qrUrl,
                ["paymentQrUrl"] = qrUrl,
                ["expires_at"] = effectiveExpiresAt == DateTime.MinValue ? FirstText(order, "expires_at", "expiresAt") : effectiveExpiresAt,
                ["expiresAt"] = effectiveExpiresAt == DateTime.MinValue ? FirstText(order, "expires_at", "expiresAt") : effectiveExpiresAt,
                ["updated_at"] = DateTime.UtcNow
            });
        }

        return order;
    }

    private async Task<Dictionary<string, object?>?> GetPendingPlanOrderAsync(string userId)
    {
        var now = DateTime.UtcNow;
        var orders = await _repo.WhereEqualAsync(PlanOrdersCollection, "buyer_id", userId, limit: 100);
        return orders
            .Where(IsPendingAccountPlanOrder)
            .Where(order =>
            {
                var expiresAt = GetEffectivePlanPaymentExpiry(order);
                return expiresAt == DateTime.MinValue || expiresAt > now;
            })
            .OrderByDescending(order => ParseDate(FirstText(order, "created_at", "createdAt", "updated_at", "updatedAt")))
            .FirstOrDefault();
    }

    private static bool IsPendingAccountPlanOrder(Dictionary<string, object?> order)
    {
        if (string.Equals(FirstText(order, "order_type", "orderType"), "chatbot_style", StringComparison.OrdinalIgnoreCase)) return false;
        var role = NormalizePlanRole(FirstText(order, "plan_role", "planRole", "role"));
        if (role is not ("VIP" or "Premium")) return false;

        var status = Text(order, "status");
        return !string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "Đã hủy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "Đã huỷ", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "Từ chối", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "Hết hạn", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(bool ok, string message)> ValidatePlanOrderAsync(string userId, Dictionary<string, object?>? authUser, string role, string? syncedRole = null, Dictionary<string, object?>? pendingOrder = null)
    {
        var currentRole = NormalizeAccountRole(syncedRole) is { Length: > 0 } synced ? synced : NormalizeAccountRole(authUser?.GetValueOrDefault("role"));
        if (string.Equals(currentRole, "Admin", StringComparison.OrdinalIgnoreCase)) return (false, "Admin không cần mua gói.");
        if (currentRole is "Sales" or "Company") return (false, "Gói Sales và Company không hỗ trợ thanh toán trực tiếp.");

        pendingOrder ??= await GetPendingPlanOrderAsync(userId);
        if (pendingOrder is not null)
        {
            var pendingRole = NormalizePlanRole(FirstText(pendingOrder, "plan_role", "planRole", "role"));
            return (false, $"Bạn đang có đơn gói {pendingRole} chờ thanh toán. Hãy hoàn tất đơn này trước.");
        }

        return (true, string.Empty);
    }

    private static bool IsActivePlanRecord(Dictionary<string, object?> item)
    {
        var status = Text(item, "status");
        return !string.Equals(status, "Đã hủy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "Từ chối", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOwner(Dictionary<string, object?> item, string? userId) => string.Equals(FirstText(item, "buyer_id", "buyerId", "user_id", "userId"), userId, StringComparison.Ordinal);
    private static string NormalizePlanRole(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text switch
        {
            "vip" => "VIP",
            "premium" => "Premium",
            "sales" or "sale" or "tour sales" or "toursales" => "Sales",
            "business" or "company" => "Company",
            "free" or "user" => "Free",
            _ => string.Empty
        };
    }
    private static DateTime GetEffectivePlanPaymentExpiry(Dictionary<string, object?> order)
    {
        // expires_at is the single authoritative deadline shown to the buyer.
        // Only derive created_at + 5 minutes for legacy rows that do not have it.
        var configuredExpiry = ParseDate(FirstText(order, "expires_at", "expiresAt"));
        if (configuredExpiry != DateTime.MinValue) return configuredExpiry;

        var createdAt = ParseDate(FirstText(order, "created_at", "createdAt"));
        return createdAt == DateTime.MinValue
            ? DateTime.MinValue
            : createdAt.AddMinutes(PlanPaymentExpireMinutes);
    }

    private static long ToUnixMilliseconds(DateTime value)
    {
        if (value == DateTime.MinValue) return 0L;
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    private string PlanQrUrl(decimal amount, string paymentContent)
        => $"https://img.vietqr.io/image/{_sePay.BankCode}-{_sePay.BankAccountNumber}-compact2.png?amount={(long)Math.Round(amount, 0, MidpointRounding.AwayFromZero)}&addInfo={Uri.EscapeDataString(paymentContent)}&accountName={Uri.EscapeDataString(_sePay.BankAccountName)}";
    private const decimal PlanYearDiscountPercent = 10m;
    private static int NormalizePlanMonths(int? value) => Math.Clamp(value.GetValueOrDefault(1), 1, 12);
    private static (decimal monthlyPrice, decimal originalAmount, decimal discountPercent, decimal discountAmount, decimal finalAmount, string priceText) CalculatePlanPricing(decimal monthlyPrice, int months)
    {
        var originalAmount = monthlyPrice * months;
        var discountPercent = months >= 12 ? PlanYearDiscountPercent : 0m;
        var discountAmount = Math.Round(originalAmount * discountPercent / 100m, 0, MidpointRounding.AwayFromZero);
        var finalAmount = Math.Max(0m, originalAmount - discountAmount);
        var priceText = $"{Money(finalAmount)} / {months} tháng" + (discountPercent > 0 ? " (-10%)" : string.Empty);
        return (monthlyPrice, originalAmount, discountPercent, discountAmount, finalAmount, priceText);
    }
    private static string Money(decimal value) => string.Format(System.Globalization.CultureInfo.GetCultureInfo("vi-VN"), "{0:N0}đ", value);
    private static string Text(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    private static decimal FirstDecimal(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetValue(key, out var value) || value is null) continue;
            if (value is decimal decimalValue) return decimalValue;
            if (decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
            if (decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.GetCultureInfo("vi-VN"), out parsed)) return parsed;
        }
        return 0m;
    }

    private static string FirstText(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            var text = Text(row, key);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return string.Empty;
    }
    private static int Int(Dictionary<string, object?> row, string key) => int.TryParse(Text(row, key), out var value) ? value : 0;
    private static int FirstInt(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (int.TryParse(Text(row, key), out var value)) return value;
        }
        return 0;
    }
    private static decimal Decimal(Dictionary<string, object?> row, string key) => decimal.TryParse(Text(row, key), out var value) ? value : 0;
    private static DateTime ParseDate(object? value) => DateTime.TryParse(value?.ToString(), out var date) ? date : DateTime.MinValue;
}

public sealed class TourCartRequest
{
    public string? TourId { get; set; }
    public int? Quantity { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
}

public sealed class PlanOrderRequest
{
    public string? PlanRole { get; set; }
    public string? Role { get; set; }
    public int? Months { get; set; }
    public int? DurationMonths { get; set; }
}

public sealed class BusinessApplicationRequest
{
    public string? PlanRole { get; set; }
    public string? Role { get; set; }
    public string? CompanyName { get; set; }
    public string? BusinessType { get; set; }
    public string? TaxCode { get; set; }
    public string? OfficeAddress { get; set; }
    public string? Province { get; set; }
    public string? Website { get; set; }
    public string? ContactName { get; set; }
    public string? Position { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
}
