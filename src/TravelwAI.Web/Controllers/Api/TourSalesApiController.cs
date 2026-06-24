using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/tour-sales")]
public sealed class TourSalesApiController : ApiControllerBase
{
    private readonly IDataRepository _repo;
    private readonly TourOrderAutomation _tourAutomation;
    private readonly IFileStorageService _fileStorage;
    private readonly IChatService _chatService;
    private readonly EmailNotificationService _emailNotificationService;

    public TourSalesApiController(IAuthService authService, IDataRepository repo, TourOrderAutomation tourAutomation, IFileStorageService fileStorage, IChatService chatService, EmailNotificationService emailNotificationService) : base(authService)
    {
        _repo = repo;
        _tourAutomation = tourAutomation;
        _fileStorage = fileStorage;
        _chatService = chatService;
        _emailNotificationService = emailNotificationService;
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
        var activeTours = tours.Count(t => string.Equals(Text(t, "status"), "Đang bán", StringComparison.OrdinalIgnoreCase) && !(GetInt(t, "slots") > 0 && GetInt(t, "sold") >= GetInt(t, "slots")));
        var sold = tours.Sum(t => GetInt(t, "sold"));
        var slots = tours.Sum(t => GetInt(t, "slots"));
        var revenue = orders.Where(o => string.Equals(Text(o, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase)).Sum(GetOrderTotal);

        return Ok(new
        {
            success = true,
            role = access.role,
            data = new
            {
                tours = tours.Count,
                activeTours,
                sold,
                slots,
                available = Math.Max(0, slots - sold),
                orders = orders.Count,
                revenue
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

        if (!string.Equals(access.role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            tours = tours.Where(t => !IsTourSoldOut(t)).ToList();
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
            salesName = request.TourSalesName?.Trim() ?? "Tour Sales";
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
            return StatusCode(403, new { success = false, message = "Tour Sales chỉ được sửa tour của họ." });
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
        if (string.IsNullOrWhiteSpace(automaticSalesName)) automaticSalesName = request.TourSalesName?.Trim() ?? "Tour Sales";

        var salesName = manualSalesName && !string.IsNullOrWhiteSpace(storedSalesName)
            ? storedSalesName
            : automaticSalesName;

        if (string.IsNullOrWhiteSpace(salesName)) salesName = "Tour Sales";

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
            return StatusCode(403, new { success = false, message = "Tour Sales chỉ được xóa tour của họ." });
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
        var tourMap = tours
            .Where(t => !string.IsNullOrWhiteSpace(Text(t, "id")))
            .ToDictionary(t => Text(t, "id"), t => t, StringComparer.OrdinalIgnoreCase);
        var isAdmin = string.Equals(access.role, "Admin", StringComparison.OrdinalIgnoreCase);

        foreach (var order in orders)
        {
            var tourId = TextAny(order, "tour_id", "tourId");
            if (!string.IsNullOrWhiteSpace(tourId) && tourMap.TryGetValue(tourId, out var tour))
            {
                var ownerId = GetTourOwnerId(tour);
                var salesName = string.IsNullOrWhiteSpace(ownerId)
                    ? TextAny(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName")
                    : await GetUserDisplayNameAsync(ownerId);
                if (string.IsNullOrWhiteSpace(salesName)) salesName = "Tour Sales";

                order["tour_sales_id"] = ownerId;
                order["tourSalesId"] = ownerId;
                order["tour_sales_name"] = salesName;
                order["tourSalesName"] = salesName;
                order["can_sell"] = isAdmin || (!string.IsNullOrWhiteSpace(ownerId) && string.Equals(ownerId, access.userId, StringComparison.Ordinal));
                order["canSell"] = order["can_sell"];
            }
            else
            {
                var ownerId = TextAny(order, "tour_sales_id", "tourSalesId", "seller_id", "sellerId");
                order["can_sell"] = isAdmin || (!string.IsNullOrWhiteSpace(ownerId) && string.Equals(ownerId, access.userId, StringComparison.Ordinal));
                order["canSell"] = order["can_sell"];
            }
        }

        return Ok(new { success = true, data = orders });
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
            return BadRequest(new { success = false, message = "Đơn này đã bị hủy do quá 3 phút chưa bán." });
        }
        if (string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Đơn này đã bán rồi." });
        }
        if (_tourAutomation.IsExpiredPendingOrder(order))
        {
            await _tourAutomation.ExpireOrderAsync(id, order);
            await CleanupCanceledToursAndOrdersAsync();
            return BadRequest(new { success = false, message = "Đơn này đã quá 3 phút nên đã bị hủy và tự động xóa khỏi trang Sales." });
        }

        var tourId = Text(order, "tour_id");
        if (string.IsNullOrWhiteSpace(tourId)) tourId = Text(order, "tourId");
        if (string.IsNullOrWhiteSpace(tourId)) return BadRequest(new { success = false, message = "Đơn không có mã tour." });

        var tour = await _repo.GetByIdAsync("tours", tourId);
        if (tour is null) return NotFound(new { success = false, message = "Không tìm thấy tour của đơn này" });
        if (!string.Equals(access.role, "Admin", StringComparison.OrdinalIgnoreCase) && !CanEditTour(access.role, access.userId, tour))
        {
            return StatusCode(403, new { success = false, message = "Tour này thuộc Tour Sales khác, bạn không thể bán." });
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
            return StatusCode(500, new { success = false, message = "Không thể tạo lịch trình cho khách đặt tour." });
        }

        var now = DateTime.UtcNow;
        await _repo.UpdateAsync("tour_orders", id, new Dictionary<string, object?>
        {
            ["status"] = "Đã bán",
            ["seller_id"] = access.userId,
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
        var total = GetOrderTotal(order);
        if (total <= 0) total = GetDecimal(tour, "price") * quantity;

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
                ? "Đã bán tour, tạo lịch trình cho khách và gửi email xác nhận"
                : "Đã bán tour và tạo lịch trình cho khách",
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
        if (order is null) return NotFound(new { success = false, message = "Không tìm thấy đơn bán tour" });

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
        return ok ? Ok(new { success = true, message = "Đã xóa đơn bán tour" }) : NotFound(new { success = false, message = "Không tìm thấy đơn bán tour" });
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

        var role = current.authUser?.GetValueOrDefault("role")?.ToString() ?? "User";
        if (!IsTourRole(role))
        {
            return (false, StatusCode(403, new { success = false, message = "Chỉ Admin hoặc Tour Sales mới được truy cập trang bán tour." }), role, current.userId, current.authUser);
        }

        return (true, null, role, current.userId, current.authUser);
    }

    private async Task HydrateTourSalesAsync(List<Dictionary<string, object?>> tours, string? role, string? currentUserId)
    {
        var ownerIds = tours
            .Select(GetTourOwnerId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var userNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ownerId in ownerIds)
        {
            userNames[ownerId] = await GetUserDisplayNameAsync(ownerId);
        }

        foreach (var tour in tours)
        {
            var ownerId = GetTourOwnerId(tour);
            var oldName = TextAny(tour, "tour_sales_name", "tourSalesName");
            var manualSalesName = GetBool(tour, "tour_sales_manual_name", "tourSalesManualName");
            var storedSalesName = TextAny(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName");
            var salesName = manualSalesName && !string.IsNullOrWhiteSpace(storedSalesName)
                ? storedSalesName
                : !string.IsNullOrWhiteSpace(ownerId) && userNames.TryGetValue(ownerId, out var name)
                    ? name
                    : storedSalesName;
            if (string.IsNullOrWhiteSpace(salesName)) salesName = "Tour Sales";

            tour["tour_sales_id"] = ownerId;
            tour["tourSalesId"] = ownerId;
            tour["tour_sales_name"] = salesName;
            tour["tourSalesName"] = salesName;
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
                    ["tourSalesName"] = salesName
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
        var ownerId = GetTourOwnerId(tour);
        return !string.IsNullOrWhiteSpace(userId)
            && !string.IsNullOrWhiteSpace(ownerId)
            && string.Equals(ownerId, userId, StringComparison.Ordinal);
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

    private static bool IsTourRole(string? role) => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "Tour Sales", StringComparison.OrdinalIgnoreCase);

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
    private static decimal GetDecimal(Dictionary<string, object?> row, string key) => decimal.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value) ? value : 0;
    private static decimal GetOrderTotal(Dictionary<string, object?> order) => decimal.TryParse(order.GetValueOrDefault("total_price")?.ToString(), out var value) ? value : 0;
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
