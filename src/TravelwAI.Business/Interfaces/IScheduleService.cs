using TravelwAI.Models.Travel;

namespace TravelwAI.Business.Interfaces;

public interface IScheduleService
{
    Task<string?> CreateScheduleAsync(string userId, ScheduleRequest schedule);
    Task<(List<Dictionary<string, object?>> owned, List<Dictionary<string, object?>> shared)> GetSchedulesForUserAsync(string userId);
    Task<Dictionary<string, object?>?> GetScheduleByIdAsync(string scheduleId, string userId);
    Task<bool> DeleteScheduleAsync(string scheduleId, string userId);
}
