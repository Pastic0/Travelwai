using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Models.Travel;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class SchedulesController : ApiControllerBase
{
    private readonly IScheduleService _scheduleService;

    public SchedulesController(IAuthService authService, IScheduleService scheduleService) : base(authService)
    {
        _scheduleService = scheduleService;
    }

    [HttpPost("schedules")]
    public async Task<IActionResult> CreateSchedule(ScheduleRequest schedule)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var id = await _scheduleService.CreateScheduleAsync(current.userId!, schedule);
        return id is null
            ? StatusCode(500, new { success = false, detail = "Không thể lưu lịch trình" })
            : Ok(new { success = true, schedule_id = id, message = "Đã lưu lịch trình" });
    }

    [HttpGet("get_schedules")]
    public async Task<IActionResult> GetSchedules()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var (owned, shared) = await _scheduleService.GetSchedulesForUserAsync(current.userId!);
        return Ok(new { success = true, shared_data = shared, owned_data = owned, message = "Đã tải lịch trình của bạn và lịch trình được chia sẻ" });
    }

    [HttpGet("schedules/{scheduleId}")]
    public async Task<IActionResult> GetSchedule(string scheduleId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var schedule = await _scheduleService.GetScheduleByIdAsync(scheduleId, current.userId!);
        return schedule is null
            ? NotFound(new { success = false, detail = "Không tìm thấy lịch trình hoặc bạn không có quyền truy cập" })
            : Ok(new { success = true, data = schedule, message = "Đã tải lịch trình" });
    }

    [HttpDelete("schedules/{scheduleId}")]
    public async Task<IActionResult> DeleteSchedule(string scheduleId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var ok = await _scheduleService.DeleteScheduleAsync(scheduleId, current.userId!);
        return ok ? Ok(new { success = true, message = "Đã xóa lịch trình" }) : NotFound(new { success = false, detail = "Không tìm thấy lịch trình hoặc bạn không có quyền xóa" });
    }
}
