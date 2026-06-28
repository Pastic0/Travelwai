using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/tour-sales")]
public sealed class TourSalesApiController : ApiControllerBase
{
    private const string SalesLevelSettingsCollection = "sales_level_settings";
    private const string SalesLevelSettingsDocumentId = "default";
    private readonly IDataRepository _repo;
    private readonly TourOrderAutomation _tourAutomation;
    private readonly IFileStorageService _fileStorage;
    private readonly IChatService _chatService;
    private readonly EmailNotificationService _emailNotificationService;
    private readonly NpgsqlDataSource _dataSource;

    public TourSalesApiController(IAuthService authService, IDataRepository repo, TourOrderAutomation tourAutomation, IFileStorageService fileStorage, IChatService chatService, EmailNotificationService emailNotificationService, NpgsqlDataSource dataSource) : base(authService)
    {
        _repo = repo;
        _tourAutomation = tourAutomation;
        _fileStorage = fileStorage;
        _chatService = chatService;
        _emailNotificationService = emailNotificationService;
        _dataSource = dataSource;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        var access = await RequireTourAccessAsync();
        if (!access.ok) return access.error!;

        await _tourAutomation.ExpirePendingOrdersAsync();
        await CleanupCanceledToursAndOrdersAsync();

        var tours = await _repo.GetAllAsync("tours", limit: 200);
        var orders = await _repo.GetAllAsync("tour_orders", limit: 500);
        await AttachTourSalesToOrdersAsync(orders, tours, access.role, access.userId);

        var isAdmin = IsAdminRole(access.role);
        if (isAdmin && RequestIsCompanyPage())
        {
            tours = await FilterToursByOwnerRoleAsync(tours, "Business");
            orders = orders.Where(o => IsCompanyRole(TextAny(o, "owner_role", "ownerRole"))).ToList();
        }
        else if (!isAdmin)
        {
            tours = tours.Where(t => IsOwnedByCurrentSales(t, access.userId)).ToList();
            var ownedTourIds = tours
                .Select(t => Text(t, "id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            orders = orders.Where(o => IsOrderOwnedByCurrentSales(o, access.userId, ownedTourIds)).ToList();
        }

        var activeTours = tours.Count(t => string.Equals(Text(t, "status"), "Đang bán", StringComparison.OrdinalIgnoreCase) && !(GetInt(t, "slots") > 0 && GetInt(t, "sold") >= GetInt(t, "slots")));
        var sold = tours.Sum(t => GetInt(t, "sold"));
        var slots = tours.Sum(t => GetInt(t, "slots"));
        var soldOrders = orders.Where(o => string.Equals(Text(o, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase)).ToList();
        var grossRevenue = soldOrders.Sum(GetOrderOriginalTotal);
        var discountDeducted = soldOrders.Sum(GetOrderDiscountAmount);
        var commission = soldOrders.Sum(GetOrderCommissionAmount);
        var serviceFee = soldOrders.Sum(GetOrderServiceAmount);
        var companyOriginalDeducted = soldOrders.Where(IsCompanyOrder).Sum(GetOrderOriginalTotal);
        var companyRevenue = Math.Max(0, companyOriginalDeducted - serviceFee);
        var isSales = IsSalesRole(access.role);
        var isCompany = IsCompanyRole(access.role);
        var revenue = isSales
            ? commission
            : isCompany
                ? Math.Max(0, grossRevenue - serviceFee)
                : grossRevenue - discountDeducted - commission + serviceFee - companyOriginalDeducted;
        var currentCommissionPercent = await GetTourSalesCommissionPercentAsync(access.userId);
        var currentServicePercent = await GetUserServicePercentAsync(access.userId);
        var currentSoldCount = await GetOwnerSoldQuantityAsync(access.userId);
        var currentLevel = await GetTourSalesLevelAsync(access.userId, currentSoldCount);

        return Ok(new
        {
            success = true,
            role = access.role,
            current_user_id = access.userId,
            current_user_name = await GetCurrentSalesNameAsync(access.userId!, access.authUser),
            data = new
            {
                tours = tours.Count,
                activeTours,
                sold,
                slots,
                available = Math.Max(0, slots - sold),
                orders = orders.Count,
                revenue,
                grossRevenue,
                gross_revenue = grossRevenue,
                discountDeducted,
                discount_deducted = discountDeducted,
                commission,
                commissionDeducted = commission,
                commission_deducted = commission,
                serviceFee,
                service_fee = serviceFee,
                serviceDeducted = serviceFee,
                service_deducted = serviceFee,
                companyOriginalDeducted,
                company_original_deducted = companyOriginalDeducted,
                companyGrossDeducted = companyOriginalDeducted,
                company_gross_deducted = companyOriginalDeducted,
                companyRevenue,
                company_revenue = companyRevenue,
                commission_percent = currentCommissionPercent,
                commissionPercent = currentCommissionPercent,
                service_percent = currentServicePercent,
                servicePercent = currentServicePercent,
                sales_level = currentLevel.Level,
                salesLevel = currentLevel.Level,
                sales_sold_count = currentSoldCount,
                salesSoldCount = currentSoldCount
            }
        });
    }

    [HttpGet("tours")]
    public async Task<IActionResult> GetTours()
    {
        var access = await RequireTourAccessAsync();
        if (!access.ok) return access.error!;

        await _tourAutomation.ExpirePendingOrdersAsync();
        await CleanupCanceledToursAndOrdersAsync();

        var tours = await _repo.GetAllAsync("tours", limit: 200);
        foreach (var tour in tours)
        {
            var id = Text(tour, "id");
            var slots = GetInt(tour, "slots");
            var sold = GetInt(tour, "sold");
            var status = NormalizeTourStatus(Text(tour, "status"), slots, sold);
            if (!string.Equals(status, Text(tour, "status"), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(id))
            {
                tour["status"] = status;
                await _repo.UpdateAsync("tours", id, new Dictionary<string, object?>
                {
                    ["status"] = status,
                    ["updated_at"] = DateTime.UtcNow
                });
            }
        }

        var isAdmin = IsAdminRole(access.role);
        if (isAdmin && RequestIsCompanyPage())
        {
            tours = await FilterToursByOwnerRoleAsync(tours, "Business");
        }
        else if (!isAdmin)
        {
            tours = tours
                .Where(t => IsOwnedByCurrentSales(t, access.userId))
                .Where(t => !IsTourSoldOut(t))
                .ToList();
        }

        await HydrateTourSalesAsync(tours, access.role, access.userId);

        return Ok(new { success = true, role = access.role, current_user_id = access.userId, current_user_name = await GetCurrentSalesNameAsync(access.userId!, access.authUser), data = tours });
    }

    [HttpPost("tour-image")]
    public async Task<IActionResult> UploadTourImage([FromForm] IFormFile? image)
    {
        var access = await RequireTourAccessAsync();
        if (!access.ok) return access.error!;
        if (image is null || image.Length == 0)
        {
            return BadRequest(new { success = false, message = "Vui lòng chọn tệp ảnh tour." });
        }

        var url = await _fileStorage.SaveImageAsync(image, access.userId!, "tours");
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { success = false, message = "Ảnh không hợp lệ. Chỉ hỗ trợ JPG, PNG, GIF hoặc WEBP, tối đa 10MB." });
        }

        return Ok(new { success = true, url, image = url, message = "Đã tải ảnh tour" });
    }

    [HttpPost("tours")]
    public async Task<IActionResult> CreateTour([FromBody] TourUpsertRequest request)
    {
        var access = await RequireTourAccessAsync();
        if (!access.ok) return access.error!;
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { success = false, message = "Vui lòng nhập tên tour." });
        if (!TourDatesAreValid(request.StartDate, request.EndDate)) return BadRequest(new { success = false, message = "Ngày kết thúc phải sau hoặc bằng ngày bắt đầu." });

        var now = DateTime.UtcNow;
        var slots = Math.Max(0, request.Slots ?? 0);
        var sold = Math.Max(0, request.Sold ?? 0);
        if (slots > 0 && sold > slots) sold = slots;
        var status = NormalizeTourStatus(request.Status, slots, sold);
        var isAdmin = string.Equals(access.role, "Admin", StringComparison.OrdinalIgnoreCase);
        var requestedSalesId = request.TourSalesId?.Trim() ?? string.Empty;
        var ownerId = access.userId!;
        if (isAdmin && !string.IsNullOrWhiteSpace(requestedSalesId))
        {
            ownerId = requestedSalesId;
        }

        var salesName = await GetCurrentSalesNameAsync(ownerId, string.Equals(ownerId, access.userId, StringComparison.Ordinal) ? access.authUser : null);
        if (string.IsNullOrWhiteSpace(salesName))
        {
            salesName = request.TourSalesName?.Trim() ?? "Sales";
        }

        var data = new Dictionary<string, object?>
        {
            ["name"] = request.Name.Trim(),
            ["destination"] = request.Destination?.Trim() ?? string.Empty,
            ["description"] = request.Description?.Trim() ?? string.Empty,
            ["duration"] = request.Duration?.Trim() ?? string.Empty,
            ["start_date"] = request.StartDate?.Trim() ?? string.Empty,
            ["end_date"] = request.EndDate?.Trim() ?? string.Empty,
            ["price"] = request.Price ?? 0,
            ["slots"] = slots,
            ["sold"] = sold,
            ["status"] = status,
            ["image"] = request.Image?.Trim() ?? string.Empty,
            ["tour_sales_id"] = ownerId,
            ["tourSalesId"] = ownerId,
            ["tour_sales_name"] = salesName,
            ["tourSalesName"] = salesName,
            ["tour_sales_manual_name"] = false,
            ["tourSalesManualName"] = false,
            ["created_by"] = ownerId,
            ["createdBy"] = ownerId,
            ["created_at"] = now,
            ["updated_at"] = now
        };

        var id = await _repo.AddAsync("tours", data);
        return Ok(new { success = true, id, message = "Đã tạo tour" });
    }

    [HttpPut("tours/{id}")]
    public async Task<IActionResult> UpdateTour(string id, [FromBody] TourUpsertRequest request)
    {
        var access = await RequireTourAccessAsync();
        if (!access.ok) return access.error!;

        var current = await _repo.GetByIdAsync("tours", id);
        if (current is null) return NotFound(new { success = false, message = "Không tìm thấy tour" });
        if (!CanEditTour(access.role, access.userId, current))
        {
            return StatusCode(403, new { success = false, message = "Sales chỉ được sửa tour của họ." });
        }

        var nextStartDate = request.StartDate is null ? Text(current, "start_date") : request.StartDate.Trim();
        var nextEndDate = request.EndDate is null ? Text(current, "end_date") : request.EndDate.Trim();
        if (!TourDatesAreValid(nextStartDate, nextEndDate)) return BadRequest(new { success = false, message = "Ngày kết thúc phải sau hoặc bằng ngày bắt đầu." });

        var nextSlots = Math.Max(0, request.Slots ?? GetInt(current, "slots"));
        var nextSold = Math.Max(0, request.Sold ?? GetInt(current, "sold"));
        if (nextSlots > 0 && nextSold > nextSlots) nextSold = nextSlots;
        var nextStatus = NormalizeTourStatus(request.Status ?? Text(current, "status"), nextSlots, nextSold);
        var ownerId = GetTourOwnerId(current);
        var isAdmin = string.Equals(access.role, "Admin", StringComparison.OrdinalIgnoreCase);
        var requestedSalesId = request.TourSalesId?.Trim() ?? string.Empty;
        var manualSalesName = GetBool(current, "tour_sales_manual_name", "tourSalesManualName");
        var storedSalesName = TextAny(current, "tour_sales_name", "tourSalesName", "sales_name", "salesName");

        if (isAdmin && request.TourSalesId is not null && !string.IsNullOrWhiteSpace(requestedSalesId))
        {
            ownerId = requestedSalesId;
            manualSalesName = false;
        }

        var automaticSalesName = string.IsNullOrWhiteSpace(ownerId)
            ? storedSalesName
            : await GetUserDisplayNameAsync(ownerId);
        if (string.IsNullOrWhiteSpace(automaticSalesName)) automaticSalesName = request.TourSalesName?.Trim() ?? "Sales";

        var salesName = manualSalesName && !string.IsNullOrWhiteSpace(storedSalesName)
            ? storedSalesName
            : automaticSalesName;

        if (string.IsNullOrWhiteSpace(salesName)) salesName = "Sales";

        var data = new Dictionary<string, object?>
        {
            ["name"] = string.IsNullOrWhiteSpace(request.Name) ? Text(current, "name") : request.Name.Trim(),
            ["destination"] = request.Destination is null ? Text(current, "destination") : request.Destination.Trim(),
            ["description"] = request.Description is null ? Text(current, "description") : request.Description.Trim(),
            ["duration"] = request.Duration is null ? Text(current, "duration") : request.Duration.Trim(),
            ["start_date"] = nextStartDate,
            ["end_date"] = nextEndDate,
            ["price"] = request.Price ?? GetDecimal(current, "price"),
            ["slots"] = nextSlots,
            ["sold"] = nextSold,
            ["status"] = nextStatus,
            ["image"] = request.Image is null ? Text(current, "image") : request.Image.Trim(),
            ["tour_sales_id"] = ownerId,
            ["tourSalesId"] = ownerId,
            ["created_by"] = ownerId,
            ["createdBy"] = ownerId,
            ["tour_sales_name"] = salesName,
            ["tourSalesName"] = salesName,
            ["tour_sales_manual_name"] = manualSalesName,
            ["tourSalesManualName"] = manualSalesName,
            ["updated_by"] = access.userId,
            ["updated_at"] = DateTime.UtcNow
        };

        var ok = await _repo.UpdateAsync("tours", id, data);
        return ok ? Ok(new { success = true, message = "Đã cập nhật tour" }) : NotFound(new { success = false, message = "Không tìm thấy tour" });
    }

    [HttpDelete("tours/{id}")]
    public async Task<IActionResult> DeleteTour(string id)
    {
        var access = await RequireTourAccessAsync();
        if (!access.ok) return access.error!;
        var current = await _repo.GetByIdAsync("tours", id);
        if (current is null) return NotFound(new { success = false, message = "Không tìm thấy tour" });
        if (!CanEditTour(access.role, access.userId, current))
        {
            return StatusCode(403, new { success = false, message = "Sales chỉ được xóa tour của họ." });
        }

        var ok = await _repo.DeleteAsync("tours", id);
        return ok ? Ok(new { success = true, message = "Đã xóa tour" }) : NotFound(new { success = false, message = "Không tìm thấy tour" });
    }

    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders()
    {
        var access = await RequireTourAccessAsync();
        if (!access.ok) return access.error!;

        await _tourAutomation.ExpirePendingOrdersAsync();
        await CleanupCanceledToursAndOrdersAsync();

        var orders = await _repo.GetAllAsync("tour_orders", limit: 500);
        var tours = await _repo.GetAllAsync("tours", limit: 500);
        var isAdmin = string.Equals(access.role, "Admin", StringComparison.OrdinalIgnoreCase);
        await AttachTourSalesToOrdersAsync(orders, tours, access.role, access.userId);

        if (isAdmin && RequestIsCompanyPage())
        {
            orders = orders.Where(o => IsCompanyRole(TextAny(o, "owner_role", "ownerRole"))).ToList();
        }
        else if (!isAdmin)
        {
            var ownedTourIds = tours
                .Where(t => IsOwnedByCurrentSales(t, access.userId))
                .Select(t => Text(t, "id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            orders = orders.Where(o => IsOrderOwnedByCurrentSales(o, access.userId, ownedTourIds)).ToList();
        }

        return Ok(new { success = true, role = access.role, current_user_id = access.userId, data = orders });
    }

    [HttpGet("commission")]
    public async Task<IActionResult> GetCommissionStatus()
    {
        var access = await RequireTourAccessAsync();
        if (!access.ok) return access.error!;

        await _tourAutomation.ExpirePendingOrdersAsync();
        await CleanupCanceledToursAndOrdersAsync();

        var tours = await _repo.GetAllAsync("tours", limit: 500);
        var orders = await _repo.GetAllAsync("tour_orders", limit: 500);
        await AttachTourSalesToOrdersAsync(orders, tours, access.role, access.userId);

        if (!string.Equals(access.role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            tours = tours.Where(t => IsOwnedByCurrentSales(t, access.userId)).ToList();
            var ownedTourIds = tours
                .Select(t => Text(t, "id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            orders = orders.Where(o => IsOrderOwnedByCurrentSales(o, access.userId, ownedTourIds)).ToList();
        }

        var soldOrders = orders.Where(o => string.Equals(Text(o, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase)).ToList();
        var percent = await GetTourSalesCommissionPercentAsync(access.userId);
        var soldCount = await GetOwnerSoldQuantityAsync(access.userId);
        var level = await GetTourSalesLevelAsync(access.userId, soldCount);
        var servicePercent = await GetUserServicePercentAsync(access.userId);
        var grossRevenue = soldOrders.Sum(GetOrderOriginalTotal);
        var discountDeducted = soldOrders.Sum(GetOrderDiscountAmount);
        var commission = soldOrders.Sum(GetOrderCommissionAmount);
        var serviceFee = soldOrders.Sum(GetOrderServiceAmount);
        var companyOriginalDeducted = soldOrders.Where(IsCompanyOrder).Sum(GetOrderOriginalTotal);
        var companyRevenue = Math.Max(0, companyOriginalDeducted - serviceFee);

        return Ok(new
        {
            success = true,
            role = access.role,
            current_user_id = access.userId,
            current_user_name = await GetCurrentSalesNameAsync(access.userId!, access.authUser),
            commission_percent = percent,
            commissionPercent = percent,
            service_percent = servicePercent,
            servicePercent = servicePercent,
            sales_level = level.Level,
            salesLevel = level.Level,
            sales_sold_count = soldCount,
            salesSoldCount = soldCount,
            data = new
            {
                soldOrders = soldOrders.Count,
                sold_orders = soldOrders.Count,
                grossRevenue,
                gross_revenue = grossRevenue,
                discountDeducted,
                discount_deducted = discountDeducted,
                commission,
                commissionDeducted = commission,
                commission_deducted = commission,
                serviceFee,
                service_fee = serviceFee,
                serviceDeducted = serviceFee,
                service_deducted = serviceFee,
                companyOriginalDeducted,
                company_original_deducted = companyOriginalDeducted,
                companyGrossDeducted = companyOriginalDeducted,
                company_gross_deducted = companyOriginalDeducted,
                companyRevenue,
                company_revenue = companyRevenue
            }
        });
    }

    [HttpPost("tours/{id}/sell")]
    public async Task<IActionResult> SellTour(string id, [FromBody] TourSellRequest request)
    {
        var access = await RequireTourAccessAsync();
        if (!access.ok) return access.error!;
        return BadRequest(new { success = false, message = "Chỉ bán được tour từ đơn khách đã đặt." });
    }

    [HttpPost("orders/{id}/sell")]
    public async Task<IActionResult> SellOrder(string id)
    {
        var access = await RequireTourAccessAsync();
        if (!access.ok) return access.error!;

        await _tourAutomation.ExpirePendingOrdersAsync();

        var order = await _repo.GetByIdAsync("tour_orders", id);
        if (order is null) return NotFound(new { success = false, message = "Không tìm thấy đơn đặt tour" });

        var status = Text(order, "status");
        if (string.Equals(status, "Đã hủy", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Đơn đã bị hủy vì quá 3 phút chưa bán." });
        }
        if (string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Đơn đã được bán." });
        }
        if (_tourAutomation.IsExpiredPendingOrder(order))
        {
            await _tourAutomation.ExpireOrderAsync(id, order);
            await CleanupCanceledToursAndOrdersAsync();
            return BadRequest(new { success = false, message = "Đơn quá 3 phút nên đã bị hủy và xoá khỏi Sales." });
        }

        var tourId = Text(order, "tour_id");
        if (string.IsNullOrWhiteSpace(tourId)) tourId = Text(order, "tourId");
        if (string.IsNullOrWhiteSpace(tourId)) return BadRequest(new { success = false, message = "Đơn không có mã tour." });

        var tour = await _repo.GetByIdAsync("tours", tourId);
        if (tour is null) return NotFound(new { success = false, message = "Không tìm thấy tour của đơn này" });
        if (!string.Equals(access.role, "Admin", StringComparison.OrdinalIgnoreCase) && !CanEditTour(access.role, access.userId, tour))
        {
            return StatusCode(403, new { success = false, message = "Tour thuộc Sales khác." });
        }

        var quantity = Math.Max(1, GetInt(order, "quantity"));
        var sold = GetInt(tour, "sold");
        var slots = GetInt(tour, "slots");
        if (slots > 0 && sold + quantity > slots)
        {
            return BadRequest(new { success = false, message = "Số chỗ còn lại không đủ." });
        }

        var scheduleId = await _tourAutomation.EnsureScheduleForSoldOrderAsync(order, tour, id);
        if (string.IsNullOrWhiteSpace(scheduleId))
        {
            return StatusCode(500, new { success = false, message = "Không tạo được lịch trình tour." });
        }

        var total = GetOrderTotal(order);
        if (total <= 0) total = GetDecimal(tour, "price") * quantity;
        var originalTotal = GetOrderOriginalTotal(order);
        if (originalTotal <= 0) originalTotal = GetDecimal(tour, "price") * quantity;

        var now = DateTime.UtcNow;
        var tourOwnerId = GetTourOwnerId(tour);
        var tourOwnerRole = await GetUserRoleAsync(tourOwnerId);
        var ownerSoldCountAfter = await GetOwnerSoldQuantityAsync(tourOwnerId) + quantity;
        var commissionPercent = IsSalesRole(tourOwnerRole) ? await GetTourSalesCommissionPercentAsync(tourOwnerId, ownerSoldCountAfter) : 0m;
        var servicePercent = IsCompanyRole(tourOwnerRole) ? await GetUserServicePercentAsync(tourOwnerId) : 0m;
        var commissionAmount = CalculatePercentAmount(originalTotal, commissionPercent);
        var serviceAmount = CalculatePercentAmount(originalTotal, servicePercent);
        var discountAmount = Math.Max(0, originalTotal - total);
        var tourSalesName = string.IsNullOrWhiteSpace(tourOwnerId)
            ? TextAny(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName")
            : await GetUserDisplayNameAsync(tourOwnerId);
        if (string.IsNullOrWhiteSpace(tourSalesName)) tourSalesName = "Sales";

        await _repo.UpdateAsync("tour_orders", id, new Dictionary<string, object?>
        {
            ["status"] = "Đã bán",
            ["seller_id"] = access.userId,
            ["sellerId"] = access.userId,
            ["tour_sales_id"] = tourOwnerId,
            ["tourSalesId"] = tourOwnerId,
            ["tour_sales_name"] = tourSalesName,
            ["tourSalesName"] = tourSalesName,
            ["owner_role"] = tourOwnerRole,
            ["ownerRole"] = tourOwnerRole,
            ["commission_percent"] = commissionPercent,
            ["commissionPercent"] = commissionPercent,
            ["commission_amount"] = commissionAmount,
            ["commissionAmount"] = commissionAmount,
            ["commission_base_total"] = originalTotal,
            ["commissionBaseTotal"] = originalTotal,
            ["service_fee_percent"] = servicePercent,
            ["serviceFeePercent"] = servicePercent,
            ["service_percent"] = servicePercent,
            ["servicePercent"] = servicePercent,
            ["service_fee_amount"] = serviceAmount,
            ["serviceFeeAmount"] = serviceAmount,
            ["service_amount"] = serviceAmount,
            ["serviceAmount"] = serviceAmount,
            ["original_total_price"] = originalTotal,
            ["originalTotalPrice"] = originalTotal,
            ["discount_amount"] = discountAmount,
            ["discountAmount"] = discountAmount,
            ["sold_at"] = now,
            ["schedule_id"] = scheduleId,
            ["auto_schedule_created"] = true,
            ["schedule_created_at"] = now,
            ["updated_at"] = now
        });

        var newSold = sold + quantity;
        await _repo.UpdateAsync("tours", tourId, new Dictionary<string, object?>
        {
            ["sold"] = newSold,
            ["status"] = NormalizeTourStatus(Text(tour, "status"), slots, newSold),
            ["updated_at"] = now
        });

        var customerEmail = FirstText(order, "customer_email", "customerEmail");
        var customerName = FirstText(order, "customer_name", "customerName");
        var tourName = FirstText(order, "tour_name", "tourName");
        if (string.IsNullOrWhiteSpace(tourName)) tourName = Text(tour, "name");
        var emailError = await _emailNotificationService.SendTourSoldSuccessAsync(
            customerEmail,
            customerName,
            tourName,
            quantity,
            total,
            id,
            scheduleId);

        return Ok(new
        {
            success = true,
            message = string.IsNullOrWhiteSpace(emailError)
                ? "Đã bán tour, tạo lịch trình và gửi email"
                : "Đã bán tour và tạo lịch trình",
            emailSent = string.IsNullOrWhiteSpace(emailError),
            emailWarning = emailError
        });
    }

    [HttpDelete("orders/{id}")]
    public async Task<IActionResult> DeleteOrder(string id)
    {
        var access = await RequireTourAccessAsync();
        if (!access.ok) return access.error!;
        if (!string.Equals(access.role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(403, new { success = false, message = "Chỉ Admin mới được xóa đơn bán tour." });
        }

        var order = await _repo.GetByIdAsync("tour_orders", id);
        if (order is null) return NotFound(new { success = false, message = "Không tìm thấy đơn tour" });

        if (string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
        {
            var tourId = Text(order, "tour_id");
            if (string.IsNullOrWhiteSpace(tourId)) tourId = Text(order, "tourId");
            var tour = string.IsNullOrWhiteSpace(tourId) ? null : await _repo.GetByIdAsync("tours", tourId);
            if (tour is not null)
            {
                var sold = GetInt(tour, "sold");
                var slots = GetInt(tour, "slots");
                var quantity = Math.Max(1, GetInt(order, "quantity"));
                var nextSold = Math.Max(0, sold - quantity);
                var currentStatus = Text(tour, "status");
                var nextStatus = slots > 0 && nextSold >= slots
                    ? "Đã bán"
                    : (string.Equals(currentStatus, "Đã bán", StringComparison.OrdinalIgnoreCase) || string.Equals(currentStatus, "Hết chỗ", StringComparison.OrdinalIgnoreCase)
                        ? "Đang bán"
                        : NormalizeTourStatus(currentStatus, slots, nextSold));
                await _repo.UpdateAsync("tours", tourId, new Dictionary<string, object?>
                {
                    ["sold"] = nextSold,
                    ["status"] = nextStatus,
                    ["updated_at"] = DateTime.UtcNow
                });
            }
        }

        var ok = await _repo.DeleteAsync("tour_orders", id);
        return ok ? Ok(new { success = true, message = "Đã xoá đơn tour" }) : NotFound(new { success = false, message = "Không tìm thấy đơn tour" });
    }

    private async Task CleanupCanceledToursAndOrdersAsync()
    {
        var tours = await _repo.GetAllAsync("tours", limit: 200);
        foreach (var tour in tours.Where(IsCanceledRow).ToList())
        {
            var id = Text(tour, "id");
            if (!string.IsNullOrWhiteSpace(id)) await _repo.DeleteAsync("tours", id);
        }

        var orders = await _repo.GetAllAsync("tour_orders", limit: 500);
        foreach (var order in orders.Where(IsCanceledRow).ToList())
        {
            var scheduleId = Text(order, "schedule_id");
            if (string.IsNullOrWhiteSpace(scheduleId)) scheduleId = Text(order, "scheduleId");
            if (!string.IsNullOrWhiteSpace(scheduleId)) await _repo.DeleteAsync("schedules", scheduleId);

            var id = Text(order, "id");
            if (!string.IsNullOrWhiteSpace(id)) await _repo.DeleteAsync("tour_orders", id);
        }
    }

    private async Task<(bool ok, IActionResult? error, string? role, string? userId, Dictionary<string, object?>? authUser)> RequireTourAccessAsync()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return (false, current.error, null, null, null);

        var role = current.authUser?.GetValueOrDefault("role")?.ToString() ?? "Free";
        if (!IsTourRole(role))
        {
            return (false, StatusCode(403, new { success = false, message = "Chỉ Admin, Sales hoặc Business mới được truy cập trang bán tour." }), role, current.userId, current.authUser);
        }

        return (true, null, role, current.userId, current.authUser);
    }

    private bool RequestIsCompanyPage()
    {
        var page = Request.Query["page"].ToString();
        return string.Equals(page, "business", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<Dictionary<string, object?>>> FilterToursByOwnerRoleAsync(List<Dictionary<string, object?>> tours, string requiredRole)
    {
        var filtered = new List<Dictionary<string, object?>>();
        foreach (var tour in tours)
        {
            var ownerId = GetTourOwnerId(tour);
            if (string.IsNullOrWhiteSpace(ownerId)) continue;
            var ownerRole = await GetUserRoleAsync(ownerId);
            if (string.Equals(ownerRole, requiredRole, StringComparison.OrdinalIgnoreCase)) filtered.Add(tour);
        }
        return filtered;
    }

    private async Task HydrateTourSalesAsync(List<Dictionary<string, object?>> tours, string? role, string? currentUserId)
    {
        var ownerIds = tours
            .Select(GetTourOwnerId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var userNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var userRoles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ownerId in ownerIds)
        {
            userNames[ownerId] = await GetUserDisplayNameAsync(ownerId);
            userRoles[ownerId] = await GetUserRoleAsync(ownerId);
        }

        foreach (var tour in tours)
        {
            var ownerId = GetTourOwnerId(tour);
            var ownerRole = !string.IsNullOrWhiteSpace(ownerId) && userRoles.TryGetValue(ownerId, out var roleName)
                ? roleName
                : string.Empty;
            var oldName = TextAny(tour, "tour_sales_name", "tourSalesName");
            var manualSalesName = GetBool(tour, "tour_sales_manual_name", "tourSalesManualName");
            var storedSalesName = TextAny(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName");
            var salesName = manualSalesName && !string.IsNullOrWhiteSpace(storedSalesName)
                ? storedSalesName
                : !string.IsNullOrWhiteSpace(ownerId) && userNames.TryGetValue(ownerId, out var name)
                    ? name
                    : storedSalesName;
            if (string.IsNullOrWhiteSpace(salesName)) salesName = "Sales";

            tour["tour_sales_id"] = ownerId;
            tour["tourSalesId"] = ownerId;
            tour["tour_sales_name"] = salesName;
            tour["tourSalesName"] = salesName;
            tour["owner_role"] = ownerRole;
            tour["ownerRole"] = ownerRole;
            tour["can_edit"] = CanEditTour(role, currentUserId, tour);
            tour["canEdit"] = tour["can_edit"];

            var id = Text(tour, "id");
            if (!manualSalesName && !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(ownerId) && !string.Equals(oldName, salesName, StringComparison.Ordinal))
            {
                await _repo.UpdateAsync("tours", id, new Dictionary<string, object?>
                {
                    ["tour_sales_id"] = ownerId,
                    ["tourSalesId"] = ownerId,
                    ["tour_sales_name"] = salesName,
                    ["tourSalesName"] = salesName,
                    ["owner_role"] = ownerRole,
                    ["ownerRole"] = ownerRole
                });
            }
        }
    }

    private async Task<string> GetCurrentSalesNameAsync(string userId, Dictionary<string, object?>? authUser)
    {
        var profileName = await GetUserDisplayNameAsync(userId);
        if (!string.Equals(profileName, userId, StringComparison.Ordinal)) return profileName;
        var authName = TextAny(authUser, "displayName", "username", "name", "email");
        return string.IsNullOrWhiteSpace(authName) ? userId : authName;
    }

    private async Task<string> GetUserDisplayNameAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return string.Empty;
        var user = await _chatService.GetUserByIdAsync(userId);
        var name = TextAny(user, "displayName", "username", "name", "email");
        return string.IsNullOrWhiteSpace(name) ? userId : name;
    }

    private static bool CanEditTour(string? role, string? userId, Dictionary<string, object?> tour)
    {
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)) return true;
        return IsOwnedByCurrentSales(tour, userId);
    }

    private static bool IsOwnedByCurrentSales(Dictionary<string, object?> tour, string? userId)
    {
        var ownerId = GetTourOwnerId(tour);
        return !string.IsNullOrWhiteSpace(userId)
            && !string.IsNullOrWhiteSpace(ownerId)
            && string.Equals(ownerId, userId, StringComparison.Ordinal);
    }

    private static bool IsOrderOwnedByCurrentSales(Dictionary<string, object?> order, string? userId, ISet<string> ownedTourIds)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;

        var tourId = TextAny(order, "tour_id", "tourId");
        if (!string.IsNullOrWhiteSpace(tourId) && ownedTourIds.Contains(tourId)) return true;

        var orderOwnerId = TextAny(order, "tour_sales_id", "tourSalesId", "created_by", "createdBy");
        return !string.IsNullOrWhiteSpace(orderOwnerId) && string.Equals(orderOwnerId, userId, StringComparison.Ordinal);
    }

    private static string GetTourOwnerId(Dictionary<string, object?> tour)
    {
        var owner = TextAny(tour, "created_by", "createdBy", "tour_sales_id", "tourSalesId", "seller_id", "sellerId");
        return owner.Trim();
    }

    private static string TextAny(Dictionary<string, object?>? row, params string[] keys)
    {
        if (row is null) return string.Empty;
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

    private static bool IsTourRole(string? role) => IsAdminRole(role) || IsSalesRole(role) || IsCompanyRole(role);
    private static bool IsAdminRole(string? role) => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
    private static bool IsSalesRole(string? role) => string.Equals(role, "Sales", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "Tour Sales", StringComparison.OrdinalIgnoreCase);
    private static bool IsCompanyRole(string? role) => string.Equals(role, "Business", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "Company", StringComparison.OrdinalIgnoreCase);

    private static bool IsTourSoldOut(Dictionary<string, object?> tour)
    {
        var slots = GetInt(tour, "slots");
        var sold = GetInt(tour, "sold");
        var status = NormalizeTourStatus(Text(tour, "status"), slots, sold);
        return (slots > 0 && sold >= slots) || string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Hết chỗ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCanceledRow(Dictionary<string, object?> row)
    {
        var status = NormalizeStatusText(Text(row, "status"));
        return status.StartsWith("da huy", StringComparison.Ordinal)
            || status is "huy" or "da huy tour" or "tour da huy" or "cancelled" or "canceled";
    }

    private static string NormalizeStatusText(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('đ', 'd');
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) builder.Append(ch);
        }
        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool GetBool(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetValue(key, out var value) || value is null) continue;
            if (value is bool boolValue) return boolValue;
            if (bool.TryParse(value.ToString(), out var parsed)) return parsed;
            if (int.TryParse(value.ToString(), out var number)) return number != 0;
        }
        return false;
    }

    private static string NormalizeTourStatus(string? status, int slots, int sold)
    {
        if (slots > 0 && sold >= slots) return "Đã bán";
        var value = (status ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) return "Đang bán";
        if (string.Equals(value, "Hết chỗ", StringComparison.OrdinalIgnoreCase)) return "Đã bán";
        return value;
    }

    private static bool TourDatesAreValid(string? startDate, string? endDate)
    {
        if (string.IsNullOrWhiteSpace(startDate) || string.IsNullOrWhiteSpace(endDate)) return true;
        return !DateOnly.TryParse(startDate, out var start) || !DateOnly.TryParse(endDate, out var end) || end >= start;
    }
    private static string Text(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    private static string FirstText(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Text(row, key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }
    private static int GetInt(Dictionary<string, object?> row, string key) => int.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value) ? value : 0;
    private static int GetIntAny(Dictionary<string, object?>? row, params string[] keys)
    {
        if (row is null) return 0;
        foreach (var key in keys)
        {
            if (int.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value)) return value;
        }
        return 0;
    }
    private static decimal GetDecimal(Dictionary<string, object?> row, string key) => decimal.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value) ? value : 0;
    private static decimal GetDecimalAny(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (decimal.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value)) return value;
        }
        return 0;
    }

    private static decimal GetOrderTotal(Dictionary<string, object?> order) => GetDecimalAny(order, "total_price", "totalPrice");

    private static bool IsCompanyOrder(Dictionary<string, object?> order)
    {
        return IsCompanyRole(TextAny(order, "owner_role", "ownerRole"));
    }

    private static decimal GetCompanyOriginalDeducted(Dictionary<string, object?> order)
    {
        return IsCompanyOrder(order) ? GetOrderOriginalTotal(order) : 0m;
    }

    private static decimal GetOrderOriginalTotal(Dictionary<string, object?> order)
    {
        var original = GetDecimalAny(order, "original_total_price", "originalTotalPrice", "commission_base_total", "commissionBaseTotal");
        if (original > 0) return original;
        var total = GetOrderTotal(order);
        var discount = GetDecimalAny(order, "discount_amount", "discountAmount");
        if (discount > 0) return total + discount;
        return total;
    }

    private static decimal GetOrderDiscountAmount(Dictionary<string, object?> order)
    {
        var stored = GetDecimalAny(order, "discount_amount", "discountAmount");
        if (stored > 0) return stored;
        var original = GetOrderOriginalTotal(order);
        var total = GetOrderTotal(order);
        return Math.Max(0, original - total);
    }

    private static decimal NormalizePercent(decimal value, decimal fallback = 0m)
    {
        if (value < 0) return fallback;
        if (value > 100) return 100m;
        return value;
    }

    private static decimal CalculatePercentAmount(decimal originalTotal, decimal percent)
    {
        var safeTotal = Math.Max(0, originalTotal);
        var safePercent = NormalizePercent(percent);
        return Math.Round(safeTotal * safePercent / 100m, 0, MidpointRounding.AwayFromZero);
    }

    private static decimal GetOrderCommissionPercent(Dictionary<string, object?> order)
    {
        foreach (var key in new[] { "commission_percent", "commissionPercent" })
        {
            if (order.TryGetValue(key, out var raw) && decimal.TryParse(raw?.ToString(), out var value))
            {
                return NormalizePercent(value, 8m);
            }
        }
        return 8m;
    }

    private static decimal GetOrderCommissionAmount(Dictionary<string, object?> order)
    {
        if (!string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase)) return 0;
        var stored = GetDecimalAny(order, "commission_amount", "commissionAmount");
        if (stored > 0) return stored;
        return CalculatePercentAmount(GetOrderOriginalTotal(order), GetOrderCommissionPercent(order));
    }

    private static decimal GetOrderServicePercent(Dictionary<string, object?> order)
    {
        return NormalizePercent(GetDecimalAny(order, "service_fee_percent", "serviceFeePercent", "service_percent", "servicePercent"));
    }

    private static decimal GetOrderServiceAmount(Dictionary<string, object?> order)
    {
        if (!string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase)) return 0;
        var stored = GetDecimalAny(order, "service_fee_amount", "serviceFeeAmount", "service_amount", "serviceAmount");
        if (stored > 0) return stored;
        return CalculatePercentAmount(GetOrderOriginalTotal(order), GetOrderServicePercent(order));
    }

    private sealed record SalesLevelSetting(int Level, decimal CommissionPercent, decimal OfferDiscountPercent);

    private async Task<(int Level, decimal Percent, int NextTarget)> GetTourSalesLevelAsync(string? userId, int soldCount)
    {
        var automatic = GetSalesLevel(soldCount);
        var user = await GetUserDocumentAsync(userId);
        var storedLevel = GetIntAny(user, "commission_level", "commissionLevel", "sales_level", "salesLevel");
        var level = storedLevel > 0 ? ClampSalesLevel(storedLevel) : automatic.Level;
        var setting = GetSalesLevelSetting(await GetSalesLevelSettingsAsync(), level);
        return (level, setting.CommissionPercent, automatic.NextTarget);
    }

    private async Task<List<SalesLevelSetting>> GetSalesLevelSettingsAsync()
    {
        Dictionary<string, object?>? doc = null;
        try
        {
            doc = await _repo.GetByIdAsync(SalesLevelSettingsCollection, SalesLevelSettingsDocumentId);
        }
        catch
        {
            doc = null;
        }

        if (doc?.GetValueOrDefault("levels") is IEnumerable<object?> rawLevels)
        {
            var parsed = rawLevels
                .OfType<Dictionary<string, object?>>()
                .Select(item => new SalesLevelSetting(
                    ClampSalesLevel(GetIntAny(item, "level")),
                    NormalizePercent(GetDecimalAny(item, "commission_percent", "commissionPercent"), DefaultSalesLevelSetting(ClampSalesLevel(GetIntAny(item, "level"))).CommissionPercent),
                    NormalizePercent(GetDecimalAny(item, "offer_discount_percent", "offerDiscountPercent"))
                ))
                .ToList();
            if (parsed.Count > 0) return NormalizeSalesLevelSettings(parsed);
        }

        return NormalizeSalesLevelSettings(Array.Empty<SalesLevelSetting>());
    }

    private static List<SalesLevelSetting> NormalizeSalesLevelSettings(IEnumerable<SalesLevelSetting> settings)
    {
        var map = settings
            .GroupBy(item => ClampSalesLevel(item.Level))
            .ToDictionary(group => group.Key, group => group.Last());

        return Enumerable.Range(1, 5)
            .Select(level => map.TryGetValue(level, out var item)
                ? new SalesLevelSetting(level, NormalizePercent(item.CommissionPercent, DefaultSalesLevelSetting(level).CommissionPercent), NormalizePercent(item.OfferDiscountPercent))
                : DefaultSalesLevelSetting(level))
            .ToList();
    }

    private static SalesLevelSetting DefaultSalesLevelSetting(int level) => ClampSalesLevel(level) switch
    {
        2 => new SalesLevelSetting(2, 12m, 0m),
        3 => new SalesLevelSetting(3, 15m, 0m),
        4 => new SalesLevelSetting(4, 18m, 0m),
        5 => new SalesLevelSetting(5, 20m, 0m),
        _ => new SalesLevelSetting(1, 8m, 0m)
    };

    private static SalesLevelSetting GetSalesLevelSetting(IReadOnlyCollection<SalesLevelSetting> settings, int level)
    {
        var safeLevel = ClampSalesLevel(level);
        return settings.FirstOrDefault(item => item.Level == safeLevel) ?? DefaultSalesLevelSetting(safeLevel);
    }

    private static int ClampSalesLevel(int level)
    {
        if (level < 1) return 1;
        if (level > 5) return 5;
        return level;
    }

    private async Task<decimal> GetTourSalesCommissionPercentAsync(string? userId, int? soldCountOverride = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) return 0m;
        var role = await GetUserRoleAsync(userId);
        if (!IsSalesRole(role)) return 0m;
        var soldCount = soldCountOverride ?? await GetOwnerSoldQuantityAsync(userId);
        var level = await GetTourSalesLevelAsync(userId, soldCount);
        var settings = await GetSalesLevelSettingsAsync();
        var fallback = GetSalesLevelSetting(settings, level.Level).CommissionPercent;
        var user = await GetUserDocumentAsync(userId);
        if (user is null) return fallback;
        foreach (var key in new[] { "commission_percent", "commissionPercent" })
        {
            if (user.TryGetValue(key, out var raw) && decimal.TryParse(raw?.ToString(), out var manual))
            {
                return NormalizePercent(manual, fallback);
            }
        }
        return fallback;
    }

    private async Task<decimal> GetUserServicePercentAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return 0m;
        var role = await GetUserRoleAsync(userId);
        if (!IsCompanyRole(role)) return 0m;
        var user = await GetUserDocumentAsync(userId);
        if (user is null) return 0m;
        return NormalizePercent(GetDecimalAny(user, "service_fee_percent", "serviceFeePercent", "service_percent", "servicePercent"));
    }

    private async Task<Dictionary<string, object?>?> GetUserDocumentAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        Dictionary<string, object?>? user = null;
        try
        {
            user = await _repo.GetByIdAsync("users", userId);
        }
        catch
        {
            user = null;
        }
        user ??= await _chatService.GetUserByIdAsync(userId);
        return user;
    }

    private async Task<string> GetUserRoleAsync(string? userId)
    {
        var user = await GetUserDocumentAsync(userId);
        var role = TextAny(user, "role", "userRole");
        if (string.Equals(role, "Tour Sales", StringComparison.OrdinalIgnoreCase)) return "Sales";
        if (string.Equals(role, "User", StringComparison.OrdinalIgnoreCase)) return "Free";
        if (string.Equals(role, "Company", StringComparison.OrdinalIgnoreCase)) return "Business";
        return string.IsNullOrWhiteSpace(role) ? "Free" : role;
    }

    private static (int Level, decimal Percent, int NextTarget) GetSalesLevel(int soldCount)
    {
        if (soldCount >= 300) return (5, 20m, 300);
        if (soldCount >= 200) return (4, 18m, 300);
        if (soldCount >= 120) return (3, 15m, 200);
        if (soldCount >= 50) return (2, 12m, 120);
        return (1, 8m, 50);
    }

    private async Task<int> GetOwnerSoldQuantityAsync(string? ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return 0;
        var orders = await _repo.GetAllAsync("tour_orders", limit: 1000);
        return orders
            .Where(o => string.Equals(Text(o, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
            .Where(o => string.Equals(TextAny(o, "tour_sales_id", "tourSalesId", "created_by", "createdBy", "seller_id", "sellerId"), ownerId, StringComparison.Ordinal))
            .Sum(o => Math.Max(1, GetInt(o, "quantity")));
    }

    private async Task AttachBusinessAmountsAsync(Dictionary<string, object?> order, string? ownerId, string? ownerRole)
    {
        var originalTotal = GetOrderOriginalTotal(order);
        var isSold = string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase);
        var soldCount = await GetOwnerSoldQuantityAsync(ownerId);
        var level = await GetTourSalesLevelAsync(ownerId, soldCount);
        var commissionPercent = IsSalesRole(ownerRole) ? await GetTourSalesCommissionPercentAsync(ownerId, soldCount) : 0m;
        var servicePercent = IsCompanyRole(ownerRole) ? await GetUserServicePercentAsync(ownerId) : 0m;
        var commissionAmount = isSold ? CalculatePercentAmount(originalTotal, commissionPercent) : 0;
        var serviceAmount = isSold ? CalculatePercentAmount(originalTotal, servicePercent) : 0;
        order["owner_role"] = ownerRole ?? string.Empty;
        order["ownerRole"] = ownerRole ?? string.Empty;
        order["sales_level"] = level.Level;
        order["salesLevel"] = level.Level;
        order["sales_sold_count"] = soldCount;
        order["salesSoldCount"] = soldCount;
        order["commission_percent"] = commissionPercent;
        order["commissionPercent"] = commissionPercent;
        order["commission_amount"] = commissionAmount;
        order["commissionAmount"] = commissionAmount;
        order["commission_base_total"] = originalTotal;
        order["commissionBaseTotal"] = originalTotal;
        order["service_fee_percent"] = servicePercent;
        order["serviceFeePercent"] = servicePercent;
        order["service_percent"] = servicePercent;
        order["servicePercent"] = servicePercent;
        order["service_fee_amount"] = serviceAmount;
        order["serviceFeeAmount"] = serviceAmount;
        order["service_amount"] = serviceAmount;
        order["serviceAmount"] = serviceAmount;
        order["discount_amount"] = GetOrderDiscountAmount(order);
        order["discountAmount"] = GetOrderDiscountAmount(order);
    }

    private async Task AttachTourSalesToOrdersAsync(List<Dictionary<string, object?>> orders, List<Dictionary<string, object?>> tours, string? role, string? currentUserId)
    {
        var tourMap = tours
            .Where(t => !string.IsNullOrWhiteSpace(Text(t, "id")))
            .ToDictionary(t => Text(t, "id"), t => t, StringComparer.OrdinalIgnoreCase);
        var isAdmin = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);

        foreach (var order in orders)
        {
            var ownerId = string.Empty;
            var tourId = TextAny(order, "tour_id", "tourId");
            if (!string.IsNullOrWhiteSpace(tourId) && tourMap.TryGetValue(tourId, out var tour))
            {
                ownerId = GetTourOwnerId(tour);
                var salesName = string.IsNullOrWhiteSpace(ownerId)
                    ? TextAny(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName")
                    : await GetUserDisplayNameAsync(ownerId);
                if (string.IsNullOrWhiteSpace(salesName)) salesName = "Sales";
                var ownerRole = await GetUserRoleAsync(ownerId);

                order["tour_sales_id"] = ownerId;
                order["tourSalesId"] = ownerId;
                order["tour_sales_name"] = salesName;
                order["tourSalesName"] = salesName;
                order["owner_role"] = ownerRole;
                order["ownerRole"] = ownerRole;
            }
            else
            {
                ownerId = TextAny(order, "tour_sales_id", "tourSalesId", "seller_id", "sellerId");
            }

            var resolvedOwnerRole = TextAny(order, "owner_role", "ownerRole");
            if (string.IsNullOrWhiteSpace(resolvedOwnerRole)) resolvedOwnerRole = await GetUserRoleAsync(ownerId);
            order["can_sell"] = isAdmin || (!string.IsNullOrWhiteSpace(ownerId) && string.Equals(ownerId, currentUserId, StringComparison.Ordinal));
            order["canSell"] = order["can_sell"];
            await AttachBusinessAmountsAsync(order, ownerId, resolvedOwnerRole);
        }
    }
}

public sealed class TourUpsertRequest
{
    public string? Name { get; set; }
    public string? Destination { get; set; }
    public string? Description { get; set; }
    public string? Duration { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public decimal? Price { get; set; }
    public int? Slots { get; set; }
    public int? Sold { get; set; }
    public string? Status { get; set; }
    public string? Image { get; set; }
    public string? TourSalesId { get; set; }
    public string? TourSalesName { get; set; }
}

public sealed class TourSellRequest
{
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerPhone { get; set; }
    public int? Quantity { get; set; }
    public string? Status { get; set; }
}
