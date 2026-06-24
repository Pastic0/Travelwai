using System.Text.Json.Serialization;

namespace TravelwAI.Models.Travel;

public sealed class DayDetail
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("estimated_duration")]
    public string? EstimatedDuration { get; set; }

    [JsonPropertyName("time_phase")]
    public string TimePhase { get; set; } = string.Empty;

    [JsonPropertyName("time_range")]
    public string TimeRange { get; set; } = string.Empty;
}

public sealed class DaySchedule
{
    [JsonPropertyName("day_number")]
    public int DayNumber { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("destinations")]
    public List<DayDetail> Destinations { get; set; } = new();
}

public sealed class ScheduleRequest
{
    [JsonPropertyName("old_schedule_id")]
    public string? OldScheduleId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("start_date")]
    public string StartDate { get; set; } = string.Empty;

    [JsonPropertyName("end_date")]
    public string EndDate { get; set; } = string.Empty;

    [JsonPropertyName("budget")]
    public double? Budget { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "VND";

    [JsonPropertyName("shared_emails")]
    public List<string> SharedEmails { get; set; } = new();

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("days")]
    public List<DaySchedule> Days { get; set; } = new();
}
