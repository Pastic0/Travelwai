using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/itinerary-ai")]
public sealed class ItineraryAiController : ApiControllerBase
{
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly OllamaAiService _ollama;
    private readonly RoleFeaturePolicyService _rolePolicies;
    private readonly ILogger<ItineraryAiController> _logger;

    public ItineraryAiController(
        IAuthService authService,
        OllamaAiService ollama,
        RoleFeaturePolicyService rolePolicies,
        ILogger<ItineraryAiController> logger) : base(authService)
    {
        _ollama = ollama;
        _rolePolicies = rolePolicies;
        _logger = logger;
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate(
        [FromBody] ItineraryAiRequest? request,
        CancellationToken cancellationToken)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var policy = _rolePolicies.GetPolicy(current.authUser?.GetValueOrDefault("role"));
        if (!policy.CanUseAiItinerary)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                success = false,
                code = "AI_ITINERARY_ROLE_LOCKED",
                message = $"Gói {policy.Role} không được dùng AI lập lịch trình."
            });
        }

        var instruction = Clean(request?.Instruction, 1600);
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return BadRequest(new { success = false, message = "Vui lòng nhập yêu cầu cho AI." });
        }

        var useEnglish = string.Equals(request?.Language, "en", StringComparison.OrdinalIgnoreCase);
        var currentDraft = NormalizeInputDraft(request?.CurrentDraft);
        var currentDraftJson = JsonSerializer.Serialize(currentDraft, PromptJsonOptions);
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        var outputLanguageInstruction = useEnglish
            ? "Write every user-facing text field in natural English."
            : "Viết toàn bộ trường văn bản hiển thị cho người dùng bằng tiếng Việt có dấu.";

        var systemContext = $$"""
            Bạn là bộ máy tạo và chỉnh sửa lịch trình du lịch Việt Nam trong một biểu mẫu.
            Bạn phải trả về đúng một đối tượng JSON hợp lệ, không Markdown, không lời dẫn, không giải thích.
            Hãy thực hiện đúng yêu cầu mới nhất của người dùng trên bản nháp hiện tại.
            Những trường người dùng không yêu cầu thay đổi phải được giữ nguyên hợp lý.
            Nếu bản nháp đang trống, hãy tạo lịch trình hoàn chỉnh, thực tế và có thể chỉnh sửa tiếp.
            Không tự thêm thông tin chia sẻ, email, quyền riêng tư hoặc tự lưu lịch trình.
            Ngày dùng định dạng yyyy-MM-dd. endDate không được trước startDate và lịch trình tối đa 30 ngày.
            budget là số không âm. currency chỉ được là VND, USD hoặc EUR.
            Mỗi ngày dùng timePhases; mỗi timePhase có name, timeRange và activities.
            Mỗi activity chỉ có name và notes. Nội dung ngắn gọn, cụ thể, không dùng Markdown.
            {{outputLanguageInstruction}}
            JSON phải có đúng cấu trúc:
            {
              "title":"...",
              "description":"...",
              "startDate":"yyyy-MM-dd",
              "endDate":"yyyy-MM-dd",
              "budget":0,
              "currency":"VND",
              "tags":["..."],
              "days":[
                {
                  "date":"yyyy-MM-dd",
                  "timePhases":[
                    {
                      "name":"Buổi sáng",
                      "timeRange":"08:00 - 11:30",
                      "activities":[{"name":"...","notes":"..."}]
                    }
                  ]
                }
              ]
            }
            """;

        var prompt = $$"""
            Ngày hiện tại: {{today}}
            Yêu cầu của người dùng: {{instruction}}

            Bản nháp hiện tại:
            {{currentDraftJson}}

            Hãy trả về toàn bộ bản nháp sau khi đã tạo hoặc chỉnh sửa. Chỉ xuất JSON.
            """;

        try
        {
            ItineraryDraft? generated = null;
            for (var attempt = 0; attempt < 2 && generated is null; attempt++)
            {
                var attemptPrompt = attempt == 0
                    ? prompt
                    : prompt + "\nKết quả trước không đọc được. Hãy trả lại đúng một JSON hợp lệ theo cấu trúc đã yêu cầu.";

                var answer = await _ollama.ChatAsync(
                    message: attemptPrompt,
                    history: null,
                    referenceContext: null,
                    systemContext: systemContext,
                    images: null,
                    cancellationToken: cancellationToken);

                generated = ParseDraft(answer);
            }

            if (generated is null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    success = false,
                    message = "AI chưa tạo được lịch trình hợp lệ."
                });
            }

            var normalized = NormalizeOutputDraft(generated, currentDraft, useEnglish);
            return Ok(new
            {
                success = true,
                data = normalized,
                message = "Đã cập nhật lịch trình bằng AI."
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                success = false,
                message = "AI phản hồi quá lâu."
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "AI không thể tạo lịch trình cho người dùng {UserId}", current.userId);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                success = false,
                message = "AI chưa tạo được lịch trình."
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Không thể kết nối Ollama khi tạo lịch trình cho người dùng {UserId}", current.userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                message = "Không thể kết nối dịch vụ AI."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi tạo lịch trình AI cho người dùng {UserId}", current.userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                message = "Dịch vụ AI tạm thời không khả dụng."
            });
        }
    }

    private static ItineraryDraft NormalizeInputDraft(ItineraryDraft? draft)
    {
        draft ??= new ItineraryDraft();
        return new ItineraryDraft
        {
            Title = Clean(draft.Title, 180),
            Description = CleanMultiline(draft.Description, 1800),
            StartDate = Clean(draft.StartDate, 10),
            EndDate = Clean(draft.EndDate, 10),
            Budget = draft.Budget is >= 0 ? draft.Budget : null,
            Currency = NormalizeCurrency(draft.Currency),
            Tags = NormalizeTags(draft.Tags),
            Days = NormalizeDays(draft.Days)
        };
    }

    private static ItineraryDraft NormalizeOutputDraft(ItineraryDraft generated, ItineraryDraft current, bool useEnglish)
    {
        var title = Clean(generated.Title, 180);
        if (string.IsNullOrWhiteSpace(title)) title = current.Title;
        if (string.IsNullOrWhiteSpace(title)) title = useEnglish ? "Travel itinerary" : "Lịch trình du lịch";

        var description = CleanMultiline(generated.Description, 1800);
        if (string.IsNullOrWhiteSpace(description)) description = current.Description;

        var start = ParseDate(generated.StartDate)
            ?? ParseDate(current.StartDate)
            ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(7));

        var generatedDays = NormalizeDays(generated.Days);
        var requestedDayCount = generatedDays.Count;
        var end = ParseDate(generated.EndDate)
            ?? ParseDate(current.EndDate)
            ?? (requestedDayCount > 1 ? start.AddDays(requestedDayCount - 1) : start.AddDays(2));

        if (end < start) end = start;
        if (end.DayNumber - start.DayNumber > 29) end = start.AddDays(29);

        var currency = NormalizeCurrency(
            string.IsNullOrWhiteSpace(generated.Currency) ? current.Currency : generated.Currency);
        var budget = generated.Budget is >= 0 ? generated.Budget : current.Budget;
        var tags = NormalizeTags(generated.Tags);
        if (tags.Count == 0) tags = NormalizeTags(current.Tags);

        var sourceByDate = NormalizeDays(current.Days)
            .Where(day => ParseDate(day.Date) is not null)
            .GroupBy(day => day.Date!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);


        foreach (var generatedDay in generatedDays)
            sourceByDate[generatedDay.Date!] = generatedDay;

        var days = new List<ItineraryDay>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var key = date.ToString("yyyy-MM-dd");
            sourceByDate.TryGetValue(key, out var sourceDay);
            days.Add(new ItineraryDay
            {
                Date = key,
                TimePhases = NormalizeTimePhases(sourceDay?.TimePhases)
            });
        }

        return new ItineraryDraft
        {
            Title = title,
            Description = description,
            StartDate = start.ToString("yyyy-MM-dd"),
            EndDate = end.ToString("yyyy-MM-dd"),
            Budget = budget,
            Currency = currency,
            Tags = tags,
            Days = days
        };
    }

    private static List<ItineraryDay> NormalizeDays(IEnumerable<ItineraryDay>? days)
        => (days ?? Enumerable.Empty<ItineraryDay>())
            .Take(30)
            .Select(day => new ItineraryDay
            {
                Date = ParseDate(day.Date)?.ToString("yyyy-MM-dd") ?? string.Empty,
                TimePhases = NormalizeTimePhases(day.TimePhases)
            })
            .Where(day => !string.IsNullOrWhiteSpace(day.Date))
            .ToList();

    private static List<ItineraryTimePhase> NormalizeTimePhases(IEnumerable<ItineraryTimePhase>? phases)
        => (phases ?? Enumerable.Empty<ItineraryTimePhase>())
            .Take(8)
            .Select(phase => new ItineraryTimePhase
            {
                Name = Clean(phase.Name, 100),
                TimeRange = Clean(phase.TimeRange, 50),
                Activities = (phase.Activities ?? new List<ItineraryActivity>())
                    .Take(12)
                    .Select(activity => new ItineraryActivity
                    {
                        Name = Clean(activity.Name, 180),
                        Notes = CleanMultiline(activity.Notes, 700)
                    })
                    .Where(activity => !string.IsNullOrWhiteSpace(activity.Name))
                    .ToList()
            })
            .Where(phase => !string.IsNullOrWhiteSpace(phase.Name) || phase.Activities.Count > 0)
            .ToList();

    private static List<string> NormalizeTags(IEnumerable<string>? tags)
        => (tags ?? Enumerable.Empty<string>())
            .Select(tag => Clean(tag, 40))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

    private static string NormalizeCurrency(string? currency)
    {
        var value = Clean(currency, 3).ToUpperInvariant();
        return value is "USD" or "EUR" ? value : "VND";
    }

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParse(Clean(value, 10), out var parsed) ? parsed : null;

    private static ItineraryDraft? ParseDraft(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            return JsonSerializer.Deserialize<ItineraryDraft>(
                answer[start..(end + 1)],
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString
                });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clean(string? value, int maxLength)
    {
        var clean = string.Join(" ", (value ?? string.Empty)
            .Replace('\0', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }

    private static string CleanMultiline(string? value, int maxLength)
    {
        var clean = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\0', ' ')
            .Trim();
        while (clean.Contains("\n\n\n", StringComparison.Ordinal))
            clean = clean.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }

    public sealed class ItineraryAiRequest
    {
        public string? Instruction { get; set; }
        public string? Language { get; set; }
        public ItineraryDraft? CurrentDraft { get; set; }
    }

    public sealed class ItineraryDraft
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? StartDate { get; set; }
        public string? EndDate { get; set; }
        public double? Budget { get; set; }
        public string? Currency { get; set; }
        public List<string> Tags { get; set; } = new();
        public List<ItineraryDay> Days { get; set; } = new();
    }

    public sealed class ItineraryDay
    {
        public string? Date { get; set; }
        public List<ItineraryTimePhase> TimePhases { get; set; } = new();
    }

    public sealed class ItineraryTimePhase
    {
        public string? Name { get; set; }
        public string? TimeRange { get; set; }
        public List<ItineraryActivity> Activities { get; set; } = new();
    }

    public sealed class ItineraryActivity
    {
        public string? Name { get; set; }
        public string? Notes { get; set; }
    }
}
