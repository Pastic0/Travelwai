using System.Globalization;
using System.Text.Encodings.Web;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

public sealed class AiKnowledgeContextService
{
    private static readonly JsonSerializerOptions AiJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static readonly string[] PostFields =
    {
        "id", "title", "summary", "content", "month", "festival", "province", "holiday_type", "holidayType",
        "tour_keywords", "tourKeywords", "author_id", "authorId", "author_name", "authorName",
        "status", "source", "is_deleted", "isDeleted", "created_at", "updated_at"
    };

    private static readonly string[] TourFields =
    {
        "id", "name", "title", "description", "destination", "province", "start_date", "end_date",
        "duration", "price", "slots", "sold", "status", "itinerary", "included", "excluded",
        "tour_sales_name", "tourSalesName", "owner_role", "ownerRole", "created_at", "updated_at"
    };

    private static readonly string[] HiddenPostStatuses =
    {
        "an", "da xoa", "deleted", "inactive", "draft", "nhap"
    };

    private static readonly string[] ScheduleSearchFields =
    {
        "id", "name", "title", "destination", "province", "start_date", "end_date", "startDate", "endDate",
        "days", "items", "activities", "notes", "status", "created_at", "updated_at",
        "user_id", "userId", "shared_with_user_ids", "sharedWithUserIds"
    };

    private readonly IDataRepository _repo;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AiKnowledgeContextService> _logger;

    public AiKnowledgeContextService(
        IDataRepository repo,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<AiKnowledgeContextService> logger)
    {
        _repo = repo;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public Task<string> BuildForChatAsync(
        string userId,
        string question,
        bool hasImages,
        CancellationToken cancellationToken)
    {
        if (hasImages && !RequiresInternalKnowledgeForImageQuestion(question))
        {
            var imageContext =
                $"THỜI ĐIỂM HỆ THỐNG: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC. " +
                "CHẾ ĐỘ PHÂN TÍCH ẢNH NHANH: Hãy phân tích trực tiếp hình ảnh người dùng gửi. " +
                "Không cần truy vấn tour, bài viết, lịch trình hoặc Wikipedia nếu câu hỏi không yêu cầu đối chiếu dữ liệu TravelwAI. " +
                "Mô tả những gì thực sự nhìn thấy, đọc chữ trong ảnh khi có thể và nêu rõ mức độ chắc chắn nếu nhận diện địa điểm hoặc đối tượng chưa chắc chắn.";
            return Task.FromResult(imageContext);
        }

        return BuildAsync(userId, question, cancellationToken);
    }

    public async Task<string> BuildAsync(string userId, string question, CancellationToken cancellationToken)
    {
        var sections = new List<string>();

        var toursTask = LoadToursAsync(question);
        var postsTask = LoadPostsAsync(question);
        var schedulesTask = LoadSchedulesAsync(userId, question);
        var wikiTask = LoadWikipediaAsync(question, cancellationToken);


        await Task.WhenAll(toursTask, postsTask, schedulesTask, wikiTask);

        AddSection(sections, "TOUR MỚI NHẤT TRONG HỆ THỐNG", await toursTask);
        AddSection(sections, "BÀI VIẾT PHÙ HỢP VÀ MỚI NHẤT, NGÀY LỄ VÀ LỄ HỘI", await postsTask);
        AddSection(sections, "LỊCH TRÌNH CỦA NGƯỜI DÙNG", await schedulesTask);
        AddSection(sections, "THÔNG TIN ĐỐI CHIẾU TỪ WIKIPEDIA TIẾNG VIỆT", await wikiTask);

        sections.Insert(0, $"THỜI ĐIỂM HỆ THỐNG: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC. Khi nói về ngày hiện tại, hãy dựa trên thời điểm này.");
        return string.Join("\n\n", sections);
    }

    private static bool RequiresInternalKnowledgeForImageQuestion(string question)
    {
        var normalized = NormalizeSearchText(question);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        var internalKnowledgeTerms = new[]
        {
            "tour", "dat tour", "gia tour", "booking",
            "bai viet", "article", "post", "le hoi", "ngay le",
            "lich trinh", "itinerary", "schedule",
            "travelwai", "he thong", "website", "wikipedia",
            "so sanh", "goi y tour", "tim tour"
        };
        return internalKnowledgeTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal));
    }

    private async Task<string> LoadToursAsync(string question)
    {
        try
        {
            var query = NormalizeSearchText(question);
            var terms = SearchTerms(query);
            var hasSearchTerms = terms.Count > 0;
            var selected = await _repo.SearchRankedDocumentsAsync(
                "tours",
                TourFields,
                hasSearchTerms ? string.Join(' ', terms) : null,
                limit: 30,
                excludeDeleted: true);

            return JsonSerializer.Serialize(selected
                .Select((item, index) =>
                {
                    var compact = Compact(item.Document, TourFields);
                    compact["ai_match_score"] = Math.Round(item.Rank, 4);
                    compact["ai_is_recent"] = !hasSearchTerms && index < 10;
                    return compact;
                })
                .Where(x => x.Count > 0)
                .ToList(), AiJsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể nạp tour cho ngữ cảnh AI");
            return "Không tải được dữ liệu tour ở thời điểm hiện tại.";
        }
    }

    private async Task<string> LoadPostsAsync(string question)
    {
        try
        {
            var query = NormalizeSearchText(question);
            var terms = SearchTerms(query);
            var requestedMonth = ExtractRequestedMonth(question);
            var hasSearchFilter = terms.Count > 0 || requestedMonth.HasValue;
            var selected = await _repo.SearchRankedDocumentsAsync(
                "travel_posts",
                PostFields,
                terms.Count > 0 ? string.Join(' ', terms) : null,
                limit: 30,
                month: requestedMonth,
                excludedNormalizedStatuses: HiddenPostStatuses,
                excludeDeleted: true);

            return JsonSerializer.Serialize(selected
                .Select((item, index) =>
                {
                    var compact = Compact(item.Document, PostFields);
                    compact["ai_match_score"] = Math.Round(item.Rank, 4);
                    compact["ai_is_recent"] = !hasSearchFilter && index < 10;
                    return compact;
                })
                .Where(x => x.Count > 0)
                .ToList(), AiJsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể nạp bài viết/lễ hội cho ngữ cảnh AI");
            return "Không tải được bài viết và dữ liệu lễ hội ở thời điểm hiện tại.";
        }
    }


    private async Task<string> LoadSchedulesAsync(string userId, string question)
    {
        try
        {
            var terms = SearchTerms(NormalizeSearchText(question));
            var schedules = await _repo.SearchRankedDocumentsAsync(
                "schedules",
                ScheduleSearchFields,
                terms.Count > 0 ? string.Join(' ', terms) : null,
                limit: 80,
                authorizedUserId: userId);
            var data = new
            {
                owned = schedules
                    .Where(item => IsOwnedSchedule(item.Document, userId))
                    .Take(40)
                    .Select(CompactSchedule),
                shared = schedules
                    .Where(item => !IsOwnedSchedule(item.Document, userId))
                    .Take(40)
                    .Select(CompactSchedule)
            };
            return JsonSerializer.Serialize(data, AiJsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể nạp lịch trình cho ngữ cảnh AI của {UserId}", userId);
            return "Không tải được lịch trình của người dùng ở thời điểm hiện tại.";
        }
    }

    private async Task<string> LoadWikipediaAsync(string question, CancellationToken cancellationToken)
    {
        var normalized = Regex.Replace(question ?? string.Empty, @"\s+", " ").Trim();
        if (normalized.Length > 180) normalized = normalized[..180];
        var cacheKey = "ai-wiki:" + normalized.ToLowerInvariant();
        if (_cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached)) return cached;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TravelwAI/1.0 (knowledge-context)");

            var search = string.IsNullOrWhiteSpace(normalized)
                ? "ngày lễ lễ hội Việt Nam"
                : $"{normalized} ngày lễ lễ hội Việt Nam";
            var url = "https://vi.wikipedia.org/w/api.php?action=query&generator=search" +
                      $"&gsrsearch={Uri.EscapeDataString(search)}&gsrlimit=4" +
                      "&prop=extracts|info&exintro=1&explaintext=1&inprop=url&format=json&origin=*";

            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return "Wikipedia hiện không phản hồi.";
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            if (!document.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pages))
                return "Không tìm thấy bài Wikipedia phù hợp với câu hỏi.";

            var results = new List<object>();
            foreach (var page in pages.EnumerateObject().Select(p => p.Value).Take(4))
            {
                var title = page.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var extract = page.TryGetProperty("extract", out var e) ? e.GetString() ?? "" : "";
                var fullUrl = page.TryGetProperty("fullurl", out var u) ? u.GetString() ?? "" : "";
                extract = Regex.Replace(extract, @"\s+", " ").Trim();
                if (extract.Length > 1200) extract = extract[..1200] + "…";
                results.Add(new { title, extract, source = fullUrl });
            }

            var result = JsonSerializer.Serialize(results, AiJsonOptions);
            _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6),
                Size = 1
            });
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể truy vấn Wikipedia cho ngữ cảnh AI");
            return "Không thể đối chiếu Wikipedia ở thời điểm hiện tại.";
        }
    }

    private static int? ExtractRequestedMonth(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        var match = Regex.Match(
            NormalizeSearchText(question),
            @"(?:^|\s)thang\s+(?<month>1[0-2]|0?[1-9])(?:\s|$)",
            RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["month"].Value, out var month) ? month : null;
    }

    private static IReadOnlyCollection<string> SearchTerms(string normalizedQuestion)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuestion)) return Array.Empty<string>();

        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            "tour", "du", "lich", "trinh", "du lich", "bai", "viet", "tin", "tuc", "noi", "dung",
            "moi", "nhat", "hien", "thi", "cho", "toi", "minh", "co", "nao", "cac", "danh",
            "sach", "tim", "kiem", "xem", "gia", "gioi", "thieu", "ve", "o", "tai"
        };

        return normalizedQuestion
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 2 && !ignored.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
    }

    private static bool IsOwnedSchedule(Dictionary<string, object?> item, string userId)
    {
        var ownerId = item.TryGetValue("user_id", out var snakeCase)
            ? snakeCase?.ToString()?.Trim()
            : item.TryGetValue("userId", out var camelCase)
                ? camelCase?.ToString()?.Trim()
                : string.Empty;
        return string.Equals(ownerId, userId, StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> CompactSchedule(RankedDocument item)
    {
        var compact = Compact(item.Document, new[]
        {
            "id", "name", "title", "destination", "province", "start_date", "end_date", "startDate", "endDate",
            "days", "items", "activities", "notes", "status", "created_at", "updated_at"
        });
        compact["ai_match_score"] = Math.Round(item.Rank, 4);
        return compact;
    }

    private static Dictionary<string, object?> Compact(Dictionary<string, object?> source, IEnumerable<string> fields)
    {
        var output = new Dictionary<string, object?>();
        foreach (var field in fields.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!source.TryGetValue(field, out var value) || value is null) continue;
            var text = value.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Length > 1800) text = text[..1800] + "…";
            output[field] = text;
        }
        return output;
    }

    private static void AddSection(List<string> sections, string title, string content)
    {
        if (!string.IsNullOrWhiteSpace(content)) sections.Add($"{title}:\n{content}");
    }

}
