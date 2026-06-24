using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/tours")]
public sealed class PublicToursApiController : ApiControllerBase
{
    private readonly IDataRepository _repo;
    private readonly TourOrderAutomation _tourAutomation;
    private readonly TourOfferService _offerService;
    private readonly EmailNotificationService _emailNotificationService;

    public PublicToursApiController(IAuthService authService, IDataRepository repo, TourOrderAutomation tourAutomation, TourOfferService offerService, EmailNotificationService emailNotificationService) : base(authService)
    {
        _repo = repo;
        _tourAutomation = tourAutomation;
        _offerService = offerService;
        _emailNotificationService = emailNotificationService;
    }

    [HttpGet]
    public async Task<IActionResult> GetTours()
    {
        await _tourAutomation.ExpirePendingOrdersAsync();

        var tours = await _repo.GetAllAsync("tours", limit: 200);
        var orders = await _repo.WhereEqualAsync("tour_orders", "status", "Khách đặt", limit: 500);
        var selling = new List<Dictionary<string, object?>>();

        foreach (var tour in tours)
        {
            var status = Text(tour, "status");
            var slots = GetInt(tour, "slots");
            var sold = GetInt(tour, "sold");
            if (slots > 0 && sold >= slots) continue;
            if (!string.Equals(status, "Đang bán", StringComparison.OrdinalIgnoreCase)) continue;

            var id = Text(tour, "id");
            var pending = orders
                .Where(o => string.Equals(o.GetValueOrDefault("tour_id")?.ToString(), id, StringComparison.OrdinalIgnoreCase))
                .Where(TourOrderAutomation.IsPendingOrder)
                .Sum(o => GetInt(o, "quantity"));

            tour["pending"] = pending;
            tour["available"] = slots > 0 ? Math.Max(0, slots - sold - pending) : 0;
            selling.Add(tour);
        }

        return Ok(new { success = true, data = selling });
    }

    [HttpPost("{id}/book")]
    public async Task<IActionResult> BookTour(string id, [FromBody] PublicTourBookingRequest request)
    {
        await _tourAutomation.ExpirePendingOrdersAsync();

        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var tour = await _repo.GetByIdAsync("tours", id);
        if (tour is null) return NotFound(new { success = false, message = "Không tìm thấy tour" });

        var tourStatus = Text(tour, "status");
        if (!string.Equals(tourStatus, "Đang bán", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Tour này hiện không nhận đặt chỗ." });
        }

        var quantity = Math.Max(1, request.Quantity ?? 1);
        var slots = GetInt(tour, "slots");
        var sold = GetInt(tour, "sold");
        var pendingQuantity = await GetPendingQuantityAsync(id);
        if (slots > 0 && sold + pendingQuantity + quantity > slots)
        {
            return BadRequest(new { success = false, message = "Tour không còn đủ chỗ." });
        }

        var price = GetDecimal(tour, "price");
        var originalTotal = price * quantity;
        var bookingDiscount = await _offerService.GetBookingDiscountAsync(current.userId!);
        var discountPercent = bookingDiscount.DiscountPercent;
        var discountAmount = Math.Round(originalTotal * discountPercent / 100m, 0, MidpointRounding.AwayFromZero);
        var total = Math.Max(0m, originalTotal - discountAmount);
        var email = current.authUser?.GetValueOrDefault("email")?.ToString() ?? string.Empty;
        var name = string.IsNullOrWhiteSpace(request.CustomerName)
            ? current.authUser?.GetValueOrDefault("displayName")?.ToString() ?? current.authUser?.GetValueOrDefault("username")?.ToString() ?? email
            : request.CustomerName.Trim();

        var now = DateTime.UtcNow;
        var orderId = await _repo.AddAsync("tour_orders", new Dictionary<string, object?>
        {
            ["tour_id"] = id,
            ["tour_name"] = tour.GetValueOrDefault("name")?.ToString() ?? string.Empty,
            ["tour_start_date"] = Text(tour, "start_date"),
            ["tour_end_date"] = Text(tour, "end_date"),
            ["tour_duration"] = Text(tour, "duration"),
            ["tour_sales_id"] = TextAny(tour, "tour_sales_id", "tourSalesId", "created_by", "createdBy", "seller_id", "sellerId"),
            ["tourSalesId"] = TextAny(tour, "tour_sales_id", "tourSalesId", "created_by", "createdBy", "seller_id", "sellerId"),
            ["tour_sales_name"] = TextAny(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName"),
            ["tourSalesName"] = TextAny(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName"),
            ["schedule_id"] = string.Empty,
            ["auto_schedule_created"] = false,
            ["customer_name"] = name,
            ["customer_email"] = string.IsNullOrWhiteSpace(request.CustomerEmail) ? email : request.CustomerEmail.Trim(),
            ["customer_phone"] = request.CustomerPhone?.Trim() ?? string.Empty,
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
            ["buyer_id"] = current.userId,
            ["created_at"] = now,
            ["expires_at"] = now.AddMinutes(TourOrderAutomation.BookingHoldMinutes),
            ["updated_at"] = now
        });

        var safeOrderId = string.IsNullOrWhiteSpace(orderId)
            ? $"TW-{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : orderId;

        if (bookingDiscount.PostOfferDiscountPercent > 0)
        {
            await _offerService.ConsumePostOfferAsync(current.userId!, safeOrderId);
        }

        var customerEmail = string.IsNullOrWhiteSpace(request.CustomerEmail) ? email : request.CustomerEmail.Trim();
        var emailError = await _emailNotificationService.SendTourBookingCreatedAsync(
            customerEmail,
            name,
            tour.GetValueOrDefault("name")?.ToString() ?? string.Empty,
            quantity,
            originalTotal,
            discountPercent,
            discountAmount,
            total,
            safeOrderId,
            now.AddMinutes(TourOrderAutomation.BookingHoldMinutes));

        return Ok(new
        {
            success = true,
            message = string.IsNullOrWhiteSpace(emailError)
                ? "Đặt tour thành công. Email xác nhận đã được gửi. Tour Sales cần xác nhận bán trong 3 phút, sau đó lịch trình mới được tạo."
                : "Đặt tour thành công. Tour Sales cần xác nhận bán trong 3 phút, sau đó lịch trình mới được tạo.",
            order_id = safeOrderId,
            emailSent = string.IsNullOrWhiteSpace(emailError),
            emailWarning = emailError
        });
    }

    private async Task<int> GetPendingQuantityAsync(string tourId)
    {
        var orders = await _repo.WhereEqualAsync("tour_orders", "tour_id", tourId, limit: 200);
        return orders
            .Where(o => string.Equals(o.GetValueOrDefault("tour_id")?.ToString(), tourId, StringComparison.OrdinalIgnoreCase))
            .Where(TourOrderAutomation.IsPendingOrder)
            .Sum(o => GetInt(o, "quantity"));
    }

    private static string Text(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    private static string TextAny(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetValue(key, out var value) && value is not null)
            {
                var text = value.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return string.Empty;
    }
    private static int GetInt(Dictionary<string, object?> row, string key) => int.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value) ? value : 0;
    private static decimal GetDecimal(Dictionary<string, object?> row, string key) => decimal.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value) ? value : 0;
}

public sealed class PublicTourBookingRequest
{
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerPhone { get; set; }
    public int? Quantity { get; set; }
}
