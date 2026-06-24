using System.Globalization;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Models.Travel;

namespace TravelwAI.Web.Services;

public sealed class TourOrderAutomation
{
    public const int BookingHoldMinutes = 3;

    private readonly IDataRepository _repo;
    private readonly IScheduleService _scheduleService;

    public TourOrderAutomation(IDataRepository repo, IScheduleService scheduleService)
    {
        _repo = repo;
        _scheduleService = scheduleService;
    }

    public async Task<int> ExpirePendingOrdersAsync()
    {
        var orders = await _repo.WhereEqualAsync("tour_orders", "status", "Khách đặt", limit: 500);
        var expiredCount = 0;

        foreach (var order in orders)
        {
            var orderId = Text(order, "id");
            if (string.IsNullOrWhiteSpace(orderId)) continue;
            if (!IsExpiredPendingOrder(order)) continue;

            if (await ExpireOrderAsync(orderId, order))
            {
                expiredCount++;
            }
        }

        return expiredCount;
    }

    public async Task<bool> ExpireOrderAsync(string orderId, Dictionary<string, object?> order)
    {
        if (string.IsNullOrWhiteSpace(orderId) || !IsPendingOrder(order)) return false;

        var scheduleId = Text(order, "schedule_id");
        if (string.IsNullOrWhiteSpace(scheduleId)) scheduleId = Text(order, "scheduleId");
        if (!string.IsNullOrWhiteSpace(scheduleId))
        {
            await _repo.DeleteAsync("schedules", scheduleId);
        }

        var now = DateTime.UtcNow;
        return await _repo.UpdateAsync("tour_orders", orderId, new Dictionary<string, object?>
        {
            ["status"] = "Đã hủy",
            ["schedule_id"] = string.Empty,
            ["auto_schedule_created"] = false,
            ["cancel_reason"] = $"Quá {BookingHoldMinutes} phút chưa bán",
            ["expired_at"] = now,
            ["updated_at"] = now
        });
    }

    public bool IsExpiredPendingOrder(Dictionary<string, object?> order)
    {
        if (!IsPendingOrder(order)) return false;

        var now = DateTime.UtcNow;
        if (TryGetDateTime(order, "expires_at", out var expiresAt))
        {
            return now >= expiresAt;
        }

        if (TryGetDateTime(order, "created_at", out var createdAt))
        {
            return now >= createdAt.AddMinutes(BookingHoldMinutes);
        }

        return false;
    }

    public async Task<string?> EnsureScheduleForSoldOrderAsync(Dictionary<string, object?> order, Dictionary<string, object?> tour, string orderId)
    {
        var existingScheduleId = Text(order, "schedule_id");
        if (string.IsNullOrWhiteSpace(existingScheduleId)) existingScheduleId = Text(order, "scheduleId");
        if (!string.IsNullOrWhiteSpace(existingScheduleId)) return existingScheduleId;

        var buyerId = Text(order, "buyer_id");
        if (string.IsNullOrWhiteSpace(buyerId)) buyerId = Text(order, "buyerId");
        if (string.IsNullOrWhiteSpace(buyerId)) return null;

        var quantity = Math.Max(1, GetInt(order, "quantity"));
        var total = GetDecimal(order, "total_price");
        if (total <= 0) total = GetDecimal(tour, "price") * quantity;

        var scheduleRequest = BuildTourScheduleRequest(tour, order, quantity, total);
        var scheduleId = await _scheduleService.CreateScheduleAsync(buyerId, scheduleRequest);
        if (string.IsNullOrWhiteSpace(scheduleId)) return null;

        return scheduleId;
    }

    public static bool IsPendingOrder(Dictionary<string, object?> order)
    {
        var status = Text(order, "status");
        return !string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "Đã hủy", StringComparison.OrdinalIgnoreCase);
    }

    private static ScheduleRequest BuildTourScheduleRequest(Dictionary<string, object?> tour, Dictionary<string, object?> order, int quantity, decimal total)
    {
        var name = Text(tour, "name");
        var destination = Text(tour, "destination");
        var description = Text(tour, "description");
        var startDate = NormalizeDateOrToday(FirstText(order, "tour_start_date", "start_date"), Text(tour, "start_date"));
        var endDate = NormalizeDateOrToday(FirstText(order, "tour_end_date", "end_date"), startDate);

        return new ScheduleRequest
        {
            Title = string.IsNullOrWhiteSpace(name) ? "Lịch trình tour du lịch" : $"Lịch trình tour: {name}",
            Description = BuildScheduleDescription(name, destination, description, quantity),
            StartDate = startDate,
            EndDate = endDate,
            Budget = (double)total,
            Currency = "VND",
            Tags = new List<string> { "Tour du lịch", "Đã bán" },
            Days = BuildTourDays(name, destination, description, startDate, endDate)
        };
    }

    private static string BuildScheduleDescription(string tourName, string destination, string description, int quantity)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(tourName)) parts.Add(tourName);
        if (!string.IsNullOrWhiteSpace(destination)) parts.Add($"Điểm đến: {destination}");
        if (!string.IsNullOrWhiteSpace(description)) parts.Add(description);
        parts.Add($"Số khách: {Math.Max(1, quantity)}");
        parts.Add("Lịch trình được tạo tự động sau khi bán tour thành công");
        return string.Join(". ", parts);
    }

    private static List<DaySchedule> BuildTourDays(string tourName, string destination, string description, string startDate, string endDate)
    {
        var start = ParseDateOnly(startDate) ?? DateOnly.FromDateTime(DateTime.Today);
        var end = ParseDateOnly(endDate) ?? start;
        if (end < start) end = start;

        var days = new List<DaySchedule>();
        var totalDays = Math.Min(30, end.DayNumber - start.DayNumber + 1);
        var place = string.IsNullOrWhiteSpace(destination) ? (string.IsNullOrWhiteSpace(tourName) ? "Điểm đến trong tour" : tourName) : destination;
        var desc = string.IsNullOrWhiteSpace(description) ? "Theo chương trình tour đã đặt." : description;

        for (var i = 0; i < totalDays; i++)
        {
            var date = start.AddDays(i);
            var isFirst = i == 0;
            var isLast = i == totalDays - 1;

            days.Add(new DaySchedule
            {
                DayNumber = i + 1,
                Date = date.ToString("yyyy-MM-dd"),
                Destinations = new List<DayDetail>
                {
                    new()
                    {
                        Name = isFirst ? "Khởi hành và nhận tour" : $"Khám phá {place}",
                        Description = isFirst ? $"Chuẩn bị, di chuyển và bắt đầu hành trình {tourName}." : desc,
                        EstimatedDuration = "3 giờ",
                        TimePhase = "Sáng",
                        TimeRange = "08:00 - 11:30"
                    },
                    new()
                    {
                        Name = $"Tham quan {place}",
                        Description = desc,
                        EstimatedDuration = "4 giờ",
                        TimePhase = "Chiều",
                        TimeRange = "13:30 - 17:30"
                    },
                    new()
                    {
                        Name = isLast ? "Kết thúc tour" : "Ăn tối và tự do trải nghiệm",
                        Description = isLast ? "Kiểm tra hành lý, mua quà và kết thúc lịch trình tour." : "Tự do ăn uống, nghỉ ngơi và trải nghiệm địa phương.",
                        EstimatedDuration = "2 giờ",
                        TimePhase = "Tối",
                        TimeRange = "19:00 - 21:00"
                    }
                }
            });
        }

        return days;
    }

    private static string NormalizeDateOrToday(string value, string? fallback = null)
    {
        if (ParseDateOnly(value) is { } parsed) return parsed.ToString("yyyy-MM-dd");
        if (!string.IsNullOrWhiteSpace(fallback) && ParseDateOnly(fallback) is { } fallbackDate) return fallbackDate.ToString("yyyy-MM-dd");
        return DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
    }

    private static DateOnly? ParseDateOnly(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParse(value, out var date)) return date;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dateTime)) return DateOnly.FromDateTime(dateTime);
        return null;
    }

    private static bool TryGetDateTime(Dictionary<string, object?> row, string key, out DateTime value)
    {
        value = default;
        if (!row.TryGetValue(key, out var raw) || raw is null) return false;

        if (raw is DateTime dt)
        {
            value = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return true;
        }

        if (raw is DateTimeOffset dto)
        {
            value = dto.UtcDateTime;
            return true;
        }

        var text = raw.ToString();
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedOffset))
        {
            value = parsedOffset.UtcDateTime;
            return true;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            value = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static string FirstText(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Text(row, key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }

    private static string Text(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    private static int GetInt(Dictionary<string, object?> row, string key) => int.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value) ? value : 0;
    private static decimal GetDecimal(Dictionary<string, object?> row, string key) => decimal.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value) ? value : 0;
}
