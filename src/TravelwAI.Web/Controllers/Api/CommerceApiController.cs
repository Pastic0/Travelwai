using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/commerce")]
public sealed class CommerceApiController : ApiControllerBase
{
    private const string CartCollection = "commerce_cart";
    private const string PlanOrdersCollection = "plan_orders";
    private const string BusinessApplicationsCollection = "business_applications";
    private const string PlanPaymentBankCode = "BIDV";
    private const string PlanPaymentAccountNumber = "0343513147";
    private const string PlanPaymentAccountName = "TravelwAI";
    private const int PlanPaymentExpireMinutes = 3;
    private readonly IDataRepository _repo;
    private readonly TourOfferService _offerService;
    private readonly EmailNotificationService _emailNotificationService;
    private readonly PlanQueueService _planQueueService;

    public CommerceApiController(IAuthService authService, IDataRepository repo, TourOfferService offerService, EmailNotificationService emailNotificationService, PlanQueueService planQueueService) : base(authService)
    {
        _repo = repo;
        _offerService = offerService;
        _emailNotificationService = emailNotificationService;
        _planQueueService = planQueueService;
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
        var state = await _planQueueService.SyncUserAsync(current.userId!, NormalizeAccountRole(current.authUser?.GetValueOrDefault("role")));
        var validation = await ValidatePlanOrderAsync(current.userId!, current.authUser, role, state.CurrentRole);
        var monthlyPrice = PlanMonthlyPriceAmount(role);
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
            yearDiscountPercent = PlanYearDiscountPercent
        });
    }

    [HttpPost("plan-orders")]
    public async Task<IActionResult> CreatePlanOrder([FromBody] PlanOrderRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var role = NormalizePlanRole(request.PlanRole ?? request.Role);
        await DeleteExpiredPendingPlanOrdersAsync(current.userId!);
        if (role is not ("VIP" or "Premium"))
        {
            return BadRequest(new { success = false, message = "Gói Sales và Business phải gửi biểu mẫu đăng ký cho Admin." });
        }

        var state = await _planQueueService.SyncUserAsync(current.userId!, NormalizeAccountRole(current.authUser?.GetValueOrDefault("role")));
        var validation = await ValidatePlanOrderAsync(current.userId!, current.authUser, role, state.CurrentRole);
        if (!validation.ok) return BadRequest(new { success = false, message = validation.message });

        var months = NormalizePlanMonths(request.Months ?? request.DurationMonths);
        var pricing = CalculatePlanPricing(role, months);
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
        var paymentContent = PlanPaymentContent(safeOrderId);
        var qrUrl = PlanQrUrl(pricing.finalAmount, safeOrderId);
        await _repo.UpdateAsync(PlanOrdersCollection, safeOrderId, new Dictionary<string, object?>
        {
            ["payment_bank"] = PlanPaymentBankCode,
            ["paymentBank"] = PlanPaymentBankCode,
            ["payment_account"] = PlanPaymentAccountNumber,
            ["paymentAccount"] = PlanPaymentAccountNumber,
            ["payment_account_name"] = PlanPaymentAccountName,
            ["paymentAccountName"] = PlanPaymentAccountName,
            ["payment_content"] = paymentContent,
            ["paymentContent"] = paymentContent,
            ["payment_qr_url"] = qrUrl,
            ["paymentQrUrl"] = qrUrl,
            ["updated_at"] = now
        });

        return Ok(new
        {
            success = true,
            message = "Đã tạo thanh toán. Quét QR rồi bấm Xác nhận thanh toán.",
            order_id = safeOrderId,
            orderId = safeOrderId,
            expires_at = expiresAt,
            expiresAt = expiresAt,
            payment_bank = PlanPaymentBankCode,
            paymentBank = PlanPaymentBankCode,
            payment_account = PlanPaymentAccountNumber,
            paymentAccount = PlanPaymentAccountNumber,
            payment_account_name = PlanPaymentAccountName,
            paymentAccountName = PlanPaymentAccountName,
            payment_content = paymentContent,
            paymentContent = paymentContent,
            payment_qr_url = qrUrl,
            paymentQrUrl = qrUrl,
            amount = pricing.finalAmount
        });
    }

    [HttpPost("plan-orders/{id}/confirm")]
    public async Task<IActionResult> ConfirmPlanOrder(string id)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var order = await _repo.GetByIdAsync(PlanOrdersCollection, id);
        if (order is null) return NotFound(new { success = false, message = "Thanh toán thất bại. Đơn đã hết hạn hoặc đã bị xoá." });
        if (!IsOwner(order, current.userId)) return StatusCode(403, new { success = false, message = "Bạn không có quyền xác nhận đơn này." });

        var status = Text(order, "status");
        if (string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { success = true, message = "Xác nhận thanh toán thành công." });
        }

        var expiresAt = ParseDate(FirstText(order, "expires_at", "expiresAt"));
        if (expiresAt != DateTime.MinValue && expiresAt <= DateTime.UtcNow)
        {
            await _repo.DeleteAsync(PlanOrdersCollection, id);
            return BadRequest(new { success = false, expired = true, message = "Thanh toán thất bại. Đơn đã hết hạn." });
        }

        return BadRequest(new { success = false, message = "Thanh toán thất bại. Admin chưa xác nhận bán." });
    }

    [HttpPost("business-application")]
    public async Task<IActionResult> SubmitBusinessApplication([FromBody] BusinessApplicationRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var role = NormalizePlanRole(request.PlanRole ?? request.Role);
        if (role is not ("Sales" or "Business")) return BadRequest(new { success = false, message = "Loại đăng ký không hợp lệ." });
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
            message = string.IsNullOrWhiteSpace(emailError) ? "Đã gửi biểu mẫu cho Admin." : "Đã lưu biểu mẫu. Email gửi Admin chưa gửi được, Admin vẫn có thể xem trong Manage.",
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
        return (true, "Thanh toán thành công. Đơn tour đang chờ Sales xác nhận bán.", safeOrderId);
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
            var expiresAt = ParseDate(FirstText(order, "expires_at", "expiresAt"));
            if (!string.IsNullOrWhiteSpace(id)
                && !string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase)
                && expiresAt != DateTime.MinValue
                && expiresAt <= now)
            {
                await _repo.DeleteAsync(PlanOrdersCollection, id);
            }
        }
    }

    private async Task<(bool ok, string message)> ValidatePlanOrderAsync(string userId, Dictionary<string, object?>? authUser, string role, string? syncedRole = null)
    {
        var currentRole = NormalizeAccountRole(syncedRole) is { Length: > 0 } synced ? synced : NormalizeAccountRole(authUser?.GetValueOrDefault("role"));
        if (string.Equals(currentRole, "Admin", StringComparison.OrdinalIgnoreCase)) return (false, "Admin không cần mua gói.");
        if (currentRole is "Sales" or "Business") return (false, "Gói Sales và Business không mua trực tiếp trong thanh toán.");

        var orders = await _repo.WhereEqualAsync(PlanOrdersCollection, "buyer_id", userId, limit: 100);
        var activeOrders = orders.Where(IsActivePlanRecord).ToList();
        var activeSoldOrders = activeOrders.Where(order => string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase)).ToList();
        if (string.Equals(currentRole, role, StringComparison.OrdinalIgnoreCase) || activeOrders.Count > 0 || activeSoldOrders.Count > 0)
        {
            return (true, string.Empty);
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
            "business" or "company" => "Business",
            "free" or "user" => "Free",
            _ => string.Empty
        };
    }
    private static string PlanPaymentContent(string orderId) => $"TWAI {orderId}";
    private static string PlanQrUrl(decimal amount, string orderId) => $"https://img.vietqr.io/image/{PlanPaymentBankCode}-{PlanPaymentAccountNumber}-compact2.png?amount={(long)Math.Round(amount, 0, MidpointRounding.AwayFromZero)}&addInfo={Uri.EscapeDataString(PlanPaymentContent(orderId))}&accountName={Uri.EscapeDataString(PlanPaymentAccountName)}";
    private const decimal PlanYearDiscountPercent = 10m;
    private static int NormalizePlanMonths(int? value) => Math.Clamp(value.GetValueOrDefault(1), 1, 12);
    private static (decimal monthlyPrice, decimal originalAmount, decimal discountPercent, decimal discountAmount, decimal finalAmount, string priceText) CalculatePlanPricing(string role, int months)
    {
        var monthlyPrice = PlanMonthlyPriceAmount(role);
        var originalAmount = monthlyPrice * months;
        var discountPercent = months >= 12 ? PlanYearDiscountPercent : 0m;
        var discountAmount = Math.Round(originalAmount * discountPercent / 100m, 0, MidpointRounding.AwayFromZero);
        var finalAmount = Math.Max(0m, originalAmount - discountAmount);
        var priceText = $"{Money(finalAmount)} / {months} tháng" + (discountPercent > 0 ? " (-10%)" : string.Empty);
        return (monthlyPrice, originalAmount, discountPercent, discountAmount, finalAmount, priceText);
    }
    private static string PlanPriceText(string role) => Money(PlanMonthlyPriceAmount(role));
    private static decimal PlanPriceAmount(string role) => PlanMonthlyPriceAmount(role);
    private static decimal PlanMonthlyPriceAmount(string role) => role.Equals("Premium", StringComparison.OrdinalIgnoreCase) ? 129000m : role.Equals("VIP", StringComparison.OrdinalIgnoreCase) ? 59000m : 0m;
    private static string Money(decimal value) => string.Format(System.Globalization.CultureInfo.GetCultureInfo("vi-VN"), "{0:N0}đ", value);
    private static string Text(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
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
