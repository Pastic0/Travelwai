using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Models.Travel;

namespace TravelwAI.Business.Services;

public sealed class ScheduleService : IScheduleService
{
    private readonly IDataRepository _repo;
    private readonly IChatService _chatService;

    public ScheduleService(IDataRepository repo, IChatService chatService)
    {
        _repo = repo;
        _chatService = chatService;
    }

    public async Task<string?> CreateScheduleAsync(string userId, ScheduleRequest schedule)
    {
        var data = await BuildScheduleDataAsync(userId, schedule);

        if (!string.IsNullOrWhiteSpace(schedule.OldScheduleId))
        {
            var oldId = schedule.OldScheduleId.Trim();
            var existing = await _repo.GetByIdAsync("schedules", oldId);
            if (existing is null) return null;
            if (existing.GetValueOrDefault("user_id")?.ToString() != userId) return null;

            data["updated_at"] = DateTime.UtcNow;
            var updated = await _repo.UpdateAsync("schedules", oldId, data);
            return updated ? oldId : null;
        }

        data["created_at"] = DateTime.UtcNow;
        data["updated_at"] = DateTime.UtcNow;
        return await _repo.AddAsync("schedules", data);
    }

    private async Task<Dictionary<string, object?>> BuildScheduleDataAsync(string userId, ScheduleRequest schedule)
    {
        var sharedEmails = schedule.SharedEmails
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sharedIds = new List<string>();
        foreach (var email in sharedEmails)
        {
            var user = await _chatService.GetUserByEmailAsync(email);
            if (user?.GetValueOrDefault("id") is string id && !sharedIds.Contains(id)) sharedIds.Add(id);
        }

        return new Dictionary<string, object?>
        {
            ["title"] = schedule.Title,
            ["description"] = schedule.Description,
            ["start_date"] = schedule.StartDate,
            ["end_date"] = schedule.EndDate,
            ["budget"] = schedule.Budget,
            ["currency"] = schedule.Currency,
            ["shared_emails"] = sharedEmails,
            ["shared_with_user_ids"] = sharedIds,
            ["tags"] = schedule.Tags,
            ["days"] = schedule.Days.Select(day => new Dictionary<string, object?>
            {
                ["day_number"] = day.DayNumber,
                ["date"] = day.Date,
                ["destinations"] = day.Destinations.Select(d => new Dictionary<string, object?>
                {
                    ["name"] = d.Name,
                    ["description"] = d.Description,
                    ["estimated_duration"] = d.EstimatedDuration,
                    ["time_phase"] = d.TimePhase,
                    ["time_range"] = d.TimeRange
                }).ToList()
            }).ToList(),
            ["user_id"] = userId
        };
    }

    public async Task<(List<Dictionary<string, object?>> owned, List<Dictionary<string, object?>> shared)> GetSchedulesForUserAsync(string userId)
    {
        var owned = await _repo.WhereEqualAsync("schedules", "user_id", userId);
        var shared = await _repo.WhereArrayContainsAsync("schedules", "shared_with_user_ids", userId);
        owned.ForEach(RemoveInternalFields);
        shared.ForEach(RemoveInternalFields);
        return (owned, shared);
    }

    public async Task<Dictionary<string, object?>?> GetScheduleByIdAsync(string scheduleId, string userId)
    {
        var schedule = await _repo.GetByIdAsync("schedules", scheduleId);
        if (schedule is null) return null;

        var isOwner = schedule.GetValueOrDefault("user_id")?.ToString() == userId;
        var isShared = ContainsUserId(schedule.GetValueOrDefault("shared_with_user_ids"), userId);
        if (!isOwner && !isShared) return null;

        RemoveInternalFields(schedule);
        return schedule;
    }

    private static bool ContainsUserId(object? value, string userId)
    {
        if (value is IEnumerable<string> stringIds) return stringIds.Any(id => id == userId);
        if (value is IEnumerable<object> objectIds) return objectIds.Any(id => id?.ToString() == userId);
        return false;
    }

    public async Task<bool> DeleteScheduleAsync(string scheduleId, string userId)
    {
        var schedule = await _repo.GetByIdAsync("schedules", scheduleId);
        if (schedule is null) return false;
        if (schedule.GetValueOrDefault("user_id")?.ToString() != userId) return false;
        return await _repo.DeleteAsync("schedules", scheduleId);
    }

    private static void RemoveInternalFields(Dictionary<string, object?> data)
    {
        data.Remove("user_id");
        data.Remove("shared_with_user_ids");
    }
}
