using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/post-content-ai")]
[Route("api/admin/post-content-ai")]
public sealed class PostContentAiController : ApiControllerBase
{
    private const string GenerationCollection = "post_ai_generations";
    private static readonly JsonSerializerOptions WikipediaJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> GenerationLocks = new(StringComparer.Ordinal);
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonTitleCharacterRegex = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly string[] UniqueAngles =
    {
        "Một góc nhìn văn hóa mới",
        "Hành trình khám phá đáng nhớ",
        "Những câu chuyện ít người biết",
        "Dấu ấn bản địa qua thời gian",
        "Trải nghiệm di sản đầy cảm hứng",
        "Sắc màu văn hóa và con người",
        "Điểm hẹn cho người yêu khám phá",
        "Chuyện kể từ miền đất lễ hội"
    };
    private static readonly string[] UniqueAnglesEn =
    {
        "A fresh cultural perspective",
        "A memorable journey of discovery",
        "Stories few people know",
        "Local heritage through time",
        "An inspiring heritage experience",
        "The colors of culture and community",
        "A destination for curious travelers",
        "Stories from the land of festivals"
    };

    private readonly IDataRepository _repo;
    private readonly OllamaAiService _ollama;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly RoleFeaturePolicyService _rolePolicies;
    private readonly AiUsageLimitService _usageLimits;
    private readonly ILogger<PostContentAiController> _logger;

    public PostContentAiController(
        IAuthService authService,
        IDataRepository repo,
        OllamaAiService ollama,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        RoleFeaturePolicyService rolePolicies,
        AiUsageLimitService usageLimits,
        ILogger<PostContentAiController> logger) : base(authService)
    {
        _repo = repo;
        _ollama = ollama;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _rolePolicies = rolePolicies;
        _usageLimits = usageLimits;
        _logger = logger;
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GeneratePostContentRequest? request, CancellationToken cancellationToken)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var keyword = CleanValue(request?.Keyword, 180);
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return BadRequest(new { success = false, message = "Vui lòng nhập lễ hội hoặc ngày lễ trước khi tạo nội dung." });
        }

        var policy = _rolePolicies.GetPolicy(current.authUser?.GetValueOrDefault("role"));
        var usage = await _usageLimits.TryConsumeAsync(
            current.userId!,
            AiUsageLimitService.PostFeature,
            policy.AiPostLimitPerWindow,
            policy.WindowMinutes,
            cancellationToken);
        if (!usage.Allowed)
        {
            Response.Headers["Retry-After"] = usage.RetryAfterSeconds.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                success = false,
                code = "AI_POST_RATE_LIMIT",
                message = $"Gói {policy.Role} được dùng AI tạo bài viết tối đa {policy.AiPostLimitPerWindow} lần trong {policy.WindowMinutes} phút.",
                limit = usage.Limit,
                remaining = usage.Remaining,
                retryAfterSeconds = usage.RetryAfterSeconds,
                resetAt = usage.ResetAt
            });
        }

        var completed = false;
        var generationLock = GenerationLocks.GetOrAdd(current.userId!, _ => new SemaphoreSlim(1, 1));
        var lockAcquired = false;

        try
        {
            var useEnglish = string.Equals(request?.Language, "en", StringComparison.OrdinalIgnoreCase);


            var wikipedia = await LoadWikipediaReferenceAsync(keyword, cancellationToken);

            await generationLock.WaitAsync(cancellationToken);
            lockAcquired = true;

            var sessionId = NormalizeSessionId(request?.SessionId);
            var usedTitles = await LoadUsedTitlesAsync(sessionId, current.userId!);
            var creativeSeed = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..22];
            var draft = await GenerateDraftAsync(keyword, usedTitles, creativeSeed, wikipedia, useEnglish, cancellationToken);

            if (draft is null || !HasRequiredContent(draft))
            {
                draft = await GenerateDraftAsync(
                    keyword,
                    usedTitles,
                    creativeSeed + "-retry",
                    wikipedia,
                    useEnglish,
                    cancellationToken,
                    strictRetry: true);
            }

            if (draft is null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { success = false, message = "AI chưa tạo được nội dung hợp lệ. Vui lòng thử lại." });
            }

            NormalizeDraft(draft, keyword, useEnglish);
            var rawTitle = draft.Title;
            var safeTitle = string.IsNullOrWhiteSpace(rawTitle)
                ? (useEnglish ? $"Discover {keyword}" : $"Khám phá {keyword}")
                : rawTitle;
            draft.Title = EnsureUniqueTitle(safeTitle, usedTitles, creativeSeed, useEnglish);

            var generationId = await _repo.AddAsync(GenerationCollection, new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["keyword"] = keyword,
                ["title"] = draft.Title,
                ["province"] = draft.Province,
                ["tour_keywords"] = draft.TourKeywords,
                ["created_by"] = current.userId ?? string.Empty,
                ["creative_seed"] = creativeSeed,
                ["language"] = useEnglish ? "en" : "vi",
                ["wikipedia_sources"] = wikipedia.Sources
                    .Select(source => new Dictionary<string, object?>
                    {
                        ["title"] = source.Title,
                        ["url"] = source.Url
                    })
                    .ToList(),
                ["status"] = "temporary",
                ["created_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });

            completed = true;
            return Ok(new
            {
                success = true,
                data = new
                {
                    title = draft.Title,
                    province = draft.Province,
                    tourKeywords = draft.TourKeywords,
                    summary = draft.Summary,
                    content = draft.Content,
                    aiGenerationSessionId = sessionId,
                    aiGenerationId = generationId ?? string.Empty,
                    wikipediaSources = wikipedia.Sources.Select(source => new
                    {
                        title = source.Title,
                        url = source.Url
                    })
                },
                message = "Đã tạo nội dung."
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { success = false, message = "AI phản hồi quá lâu. Vui lòng thử lại." });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "AI không thể tạo bài viết cho từ khóa {Keyword}", keyword);
            return StatusCode(StatusCodes.Status502BadGateway, new { success = false, message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Không thể kết nối dịch vụ AI khi tạo bài viết cho từ khóa {Keyword}", keyword);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, message = "Không thể kết nối dịch vụ AI." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi tạo nội dung bài viết cho từ khóa {Keyword}", keyword);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, message = "Dịch vụ tạo nội dung tạm thời không khả dụng." });
        }
        finally
        {
            if (lockAcquired)
            {
                generationLock.Release();
            }

            if (!completed && usage.UsageEventId.HasValue)
            {
                try
                {
                    await _usageLimits.ReleaseAsync(
                        usage.UsageEventId,
                        current.userId!,
                        AiUsageLimitService.PostFeature,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Không thể hoàn lại lượt tạo bài viết AI {UsageEventId} cho người dùng {UserId}",
                        usage.UsageEventId,
                        current.userId);
                }
            }
        }
    }

    [HttpPost("generate-stream")]
    public async Task<IActionResult> GenerateStream([FromBody] GeneratePostContentRequest? request, CancellationToken cancellationToken)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var keyword = CleanValue(request?.Keyword, 180);
        if (string.IsNullOrWhiteSpace(keyword))
            return BadRequest(new { success = false, message = "Vui lòng nhập lễ hội hoặc ngày lễ trước khi tạo nội dung." });

        var policy = _rolePolicies.GetPolicy(current.authUser?.GetValueOrDefault("role"));
        var usage = await _usageLimits.TryConsumeAsync(
            current.userId!,
            AiUsageLimitService.PostFeature,
            policy.AiPostLimitPerWindow,
            policy.WindowMinutes,
            cancellationToken);
        if (!usage.Allowed)
        {
            Response.Headers["Retry-After"] = usage.RetryAfterSeconds.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                success = false,
                code = "AI_POST_RATE_LIMIT",
                message = $"Gói {policy.Role} được dùng AI tạo bài viết tối đa {policy.AiPostLimitPerWindow} lần trong {policy.WindowMinutes} phút.",
                limit = usage.Limit,
                remaining = usage.Remaining,
                retryAfterSeconds = usage.RetryAfterSeconds,
                resetAt = usage.ResetAt
            });
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache, no-store";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.StartAsync(cancellationToken);

        var completed = false;
        var generationLock = GenerationLocks.GetOrAdd(current.userId!, _ => new SemaphoreSlim(1, 1));
        var lockAcquired = false;

        try
        {
            var useEnglish = string.Equals(request?.Language, "en", StringComparison.OrdinalIgnoreCase);
            await WriteGenerationStreamEventAsync(new { type = "status", message = "Đang đối chiếu dữ liệu Wikipedia..." }, cancellationToken);
            var wikipedia = await LoadWikipediaReferenceAsync(keyword, cancellationToken);

            await WriteGenerationStreamEventAsync(new { type = "status", message = "AI đang bắt đầu viết nội dung..." }, cancellationToken);
            await generationLock.WaitAsync(cancellationToken);
            lockAcquired = true;

            var sessionId = NormalizeSessionId(request?.SessionId);
            var usedTitles = await LoadUsedTitlesAsync(sessionId, current.userId!);
            var creativeSeed = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..22];

            Task StreamDelta(string delta, CancellationToken token) =>
                WriteGenerationStreamEventAsync(new { type = "delta", delta }, token);

            var draft = await GenerateDraftAsync(
                keyword,
                usedTitles,
                creativeSeed,
                wikipedia,
                useEnglish,
                cancellationToken,
                onDelta: StreamDelta);

            if (draft is null || !HasRequiredContent(draft))
            {
                await WriteGenerationStreamEventAsync(new
                {
                    type = "reset",
                    message = "Kết quả đầu chưa đúng cấu trúc, AI đang tạo lại..."
                }, cancellationToken);

                draft = await GenerateDraftAsync(
                    keyword,
                    usedTitles,
                    creativeSeed + "-retry",
                    wikipedia,
                    useEnglish,
                    cancellationToken,
                    strictRetry: true,
                    onDelta: StreamDelta);
            }

            if (draft is null)
                throw new InvalidOperationException("AI chưa tạo được nội dung hợp lệ. Vui lòng thử lại.");

            NormalizeDraft(draft, keyword, useEnglish);
            var safeTitle = string.IsNullOrWhiteSpace(draft.Title)
                ? (useEnglish ? $"Discover {keyword}" : $"Khám phá {keyword}")
                : draft.Title;
            draft.Title = EnsureUniqueTitle(safeTitle, usedTitles, creativeSeed, useEnglish);

            var generationId = await _repo.AddAsync(GenerationCollection, new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["keyword"] = keyword,
                ["title"] = draft.Title,
                ["province"] = draft.Province,
                ["tour_keywords"] = draft.TourKeywords,
                ["created_by"] = current.userId ?? string.Empty,
                ["creative_seed"] = creativeSeed,
                ["language"] = useEnglish ? "en" : "vi",
                ["wikipedia_sources"] = wikipedia.Sources
                    .Select(source => new Dictionary<string, object?>
                    {
                        ["title"] = source.Title,
                        ["url"] = source.Url
                    })
                    .ToList(),
                ["status"] = "temporary",
                ["created_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });

            completed = true;
            await WriteGenerationStreamEventAsync(new
            {
                type = "completed",
                success = true,
                data = new
                {
                    title = draft.Title,
                    province = draft.Province,
                    tourKeywords = draft.TourKeywords,
                    summary = draft.Summary,
                    content = draft.Content,
                    aiGenerationSessionId = sessionId,
                    aiGenerationId = generationId ?? string.Empty,
                    wikipediaSources = wikipedia.Sources.Select(source => new { title = source.Title, url = source.Url })
                },
                message = "Đã tạo nội dung."
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "AI tạo bài viết phản hồi quá lâu cho từ khóa {Keyword}", keyword);
            try
            {
                await WriteGenerationStreamEventAsync(new
                {
                    type = "error",
                    success = false,
                    message = "AI phản hồi quá lâu. Vui lòng thử lại."
                }, CancellationToken.None);
            }
            catch (Exception writeException)
            {
                _logger.LogDebug(writeException, "Client đã ngắt kết nối trước khi nhận lỗi timeout tạo bài viết AI.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi streaming tạo nội dung bài viết cho từ khóa {Keyword}", keyword);
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await WriteGenerationStreamEventAsync(new
                    {
                        type = "error",
                        success = false,
                        message = ex is HttpRequestException
                            ? "Không thể kết nối dịch vụ AI."
                            : ex.Message
                    }, CancellationToken.None);
                }
                catch (Exception writeException)
                {
                    _logger.LogDebug(writeException, "Client đã ngắt kết nối trước khi nhận lỗi tạo bài viết AI.");
                }
            }
        }
        finally
        {
            if (lockAcquired) generationLock.Release();
            if (!completed && usage.UsageEventId.HasValue)
            {
                try
                {
                    await _usageLimits.ReleaseAsync(
                        usage.UsageEventId,
                        current.userId!,
                        AiUsageLimitService.PostFeature,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không thể hoàn lại lượt tạo bài viết AI {UsageEventId} cho người dùng {UserId}", usage.UsageEventId, current.userId);
                }
            }
        }

        return new EmptyResult();
    }

    private async Task WriteGenerationStreamEventAsync(object payload, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(payload) + "\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private async Task<GeneratedPostDraft?> GenerateDraftAsync(
        string keyword,
        IReadOnlyCollection<string> usedTitles,
        string creativeSeed,
        WikipediaReference wikipedia,
        bool useEnglish,
        CancellationToken cancellationToken,
        bool strictRetry = false,
        Func<string, CancellationToken, Task>? onDelta = null)
    {
        var forbiddenTitles = usedTitles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .TakeLast(60)
            .Select((title, index) => $"{index + 1}. {title}")
            .ToList();

        var forbiddenBlock = forbiddenTitles.Count == 0
            ? "Chưa có tiêu đề cần tránh."
            : string.Join("\n", forbiddenTitles);
        var outputLanguageInstruction = useEnglish
            ? "Write title, province, tourKeywords, summary and content entirely in natural English."
            : "Mọi nội dung phải viết bằng tiếng Việt có dấu.";
        var paraphraseInstruction = useEnglish
            ? "Do not copy long passages from Wikipedia; paraphrase the facts naturally in English."
            : "Không sao chép nguyên văn dài từ Wikipedia; phải diễn đạt lại tự nhiên bằng tiếng Việt.";
        var nationwideValue = useEnglish ? "Nationwide" : "Toàn quốc";

        var systemContext = $$"""
            Bạn là biên tập viên du lịch Việt Nam chuyên viết bài về lễ hội và ngày lễ.
            Nhiệm vụ là tạo một bản nháp bài viết hoàn chỉnh từ đúng từ khóa được cung cấp.
            Wikipedia tiếng Việt trong prompt là nguồn dữ kiện ưu tiên cao nhất.
            Khi Wikipedia có dữ liệu phù hợp, phải dùng dữ kiện đó làm nền tảng chính, không được viết trái với nguồn.
            Chỉ bổ sung kiến thức chung khi Wikipedia thiếu thông tin; phần bổ sung phải thận trọng và không được bịa chi tiết.
            {{paraphraseInstruction}}
            Nếu Wikipedia không có kết quả phù hợp, mới dựa vào kiến thức của model và phải tránh khẳng định chi tiết chưa chắc chắn.
            Chỉ trả về một đối tượng JSON hợp lệ, không dùng Markdown, không thêm lời dẫn hoặc giải thích.
            JSON phải có đúng 5 khóa dạng camelCase: title, province, tourKeywords, summary, content.
            title phải tự nhiên, hấp dẫn, độc đáo, khác rõ ràng với mọi tiêu đề bị cấm.
            province là tên tỉnh/thành phù hợp nhất; nếu sự kiện diễn ra toàn quốc thì ghi "{{nationwideValue}}".
            tourKeywords là chuỗi từ khóa phân tách bằng dấu phẩy, gồm 5 đến 10 từ khóa có ích cho tìm kiếm tour.
            summary dài khoảng 2 đến 3 câu.
            content dài khoảng 600 đến 900 từ, chia thành các đoạn văn rõ ràng bằng ký tự xuống dòng, không dùng tiêu đề Markdown.
            Không bịa ngày tháng, địa điểm hoặc nghi thức quá cụ thể khi không chắc chắn; diễn đạt thận trọng và hữu ích.
            {{outputLanguageInstruction}}
            """;

        var retryInstruction = strictRetry
            ? "Đây là lần tạo lại vì kết quả trước chưa hợp lệ. Hãy tuân thủ tuyệt đối cấu trúc JSON và tạo tiêu đề khác hoàn toàn."
            : "Tạo bản nháp mới, không lặp lại các tiêu đề đã sinh trong phiên tạo nội dung hiện tại.";

        var wikipediaBlock = wikipedia.HasResults
            ? wikipedia.Json
            : "Không tìm thấy dữ liệu Wikipedia phù hợp. Chỉ khi đó mới dùng kiến thức chung một cách thận trọng.";

        var prompt = $$"""
            Từ khóa lễ hội/ngày lễ: {{keyword}}
            Mã sáng tạo bắt buộc dùng để thay đổi góc tiếp cận: {{creativeSeed}}

            DỮ LIỆU WIKIPEDIA TIẾNG VIỆT — NGUỒN ƯU TIÊN:
            {{wikipediaBlock}}

            Danh sách tiêu đề tuyệt đối không được trùng hoặc chỉ đổi vài từ:
            {{forbiddenBlock}}

            {{retryInstruction}}
            Chỉ xuất JSON theo mẫu:
            {"title":"...","province":"...","tourKeywords":"...","summary":"...","content":"..."}
            """;

        var answer = await _ollama.GenerateJsonStreamingAsync(
            systemPrompt: systemContext,
            userPrompt: prompt,
            maxOutputWords: 1200,
            onDelta: onDelta,
            cancellationToken: cancellationToken);

        return ParseDraft(answer);
    }

    private async Task<WikipediaReference> LoadWikipediaReferenceAsync(string keyword, CancellationToken cancellationToken)
    {
        var normalized = MultiSpaceRegex.Replace(keyword ?? string.Empty, " ").Trim();
        if (normalized.Length > 180) normalized = normalized[..180];
        if (string.IsNullOrWhiteSpace(normalized)) return WikipediaReference.Empty;

        var cacheKey = "post-ai-wikipedia:" + NormalizeTitle(normalized);
        if (_cache.TryGetValue(cacheKey, out WikipediaReference? cached) && cached is not null)
            return cached;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TravelwAI/1.0 (post-content-generator)");


            var searchQueries = new[]
            {
                $"intitle:\"{normalized}\"",
                $"\"{normalized}\"",
                normalized
            };

            List<WikipediaSource> sources = new();
            foreach (var searchQuery in searchQueries)
            {
                sources = await SearchWikipediaAsync(client, searchQuery, cancellationToken);
                if (sources.Count > 0) break;
            }

            var reference = sources.Count == 0
                ? WikipediaReference.Empty
                : new WikipediaReference(
                    JsonSerializer.Serialize(
                        sources.Select(source => new
                        {
                            title = source.Title,
                            extract = source.Extract,
                            source = source.Url
                        }),
                        WikipediaJsonOptions),
                    sources);

            _cache.Set(cacheKey, reference, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6),
                Size = 1
            });
            return reference;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Không thể tải Wikipedia cho chức năng tạo bài viết AI với từ khóa {Keyword}", keyword);
            return WikipediaReference.Empty;
        }
    }

    private static async Task<List<WikipediaSource>> SearchWikipediaAsync(
        HttpClient client,
        string searchQuery,
        CancellationToken cancellationToken)
    {
        var url = "https://vi.wikipedia.org/w/api.php?action=query&generator=search" +
                  $"&gsrsearch={Uri.EscapeDataString(searchQuery)}&gsrlimit=5&gsrnamespace=0" +
                  "&prop=extracts|info&exintro=1&explaintext=1&inprop=url&format=json&origin=*";

        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return new List<WikipediaSource>();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages))
            return new List<WikipediaSource>();

        return pages.EnumerateObject()
            .Select(property => property.Value)
            .Select(page =>
            {
                var title = page.TryGetProperty("title", out var titleElement)
                    ? titleElement.GetString() ?? string.Empty
                    : string.Empty;
                var extract = page.TryGetProperty("extract", out var extractElement)
                    ? extractElement.GetString() ?? string.Empty
                    : string.Empty;
                var url = page.TryGetProperty("fullurl", out var urlElement)
                    ? urlElement.GetString() ?? string.Empty
                    : string.Empty;
                var index = page.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var value)
                    ? value
                    : int.MaxValue;

                extract = MultiSpaceRegex.Replace(extract, " ").Trim();
                if (extract.Length > 2600) extract = extract[..2600].TrimEnd() + "…";
                return new { Source = new WikipediaSource(title, extract, url), Index = index };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Source.Title) && !string.IsNullOrWhiteSpace(item.Source.Extract))
            .OrderBy(item => item.Index)
            .Take(4)
            .Select(item => item.Source)
            .ToList();
    }

    private async Task<List<string>> LoadUsedTitlesAsync(string sessionId, string userId)
    {
        var generated = await _repo.WhereEqualAsync(GenerationCollection, "session_id", sessionId, limit: 100);
        return generated
            .Where(row => string.Equals(Text(row, "created_by"), userId, StringComparison.Ordinal))
            .Select(row => Text(row, "title"))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeSessionId(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        return Guid.TryParse(raw, out var parsed)
            ? parsed.ToString("N")
            : Guid.NewGuid().ToString("N");
    }

    private static GeneratedPostDraft? ParseDraft(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            return JsonSerializer.Deserialize<GeneratedPostDraft>(
                value[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasRequiredContent(GeneratedPostDraft draft)
        => !string.IsNullOrWhiteSpace(draft.Title)
            && !string.IsNullOrWhiteSpace(draft.Summary)
            && !string.IsNullOrWhiteSpace(draft.Content);

    private static void NormalizeDraft(GeneratedPostDraft draft, string keyword, bool useEnglish)
    {
        draft.Title = CleanValue(draft.Title, 180);
        draft.Province = CleanValue(draft.Province, 100);
        draft.TourKeywords = CleanValue(draft.TourKeywords, 320);
        draft.Summary = CleanMultilineValue(draft.Summary, 1200);
        draft.Content = CleanMultilineValue(draft.Content, 12000);

        if (string.IsNullOrWhiteSpace(draft.Title)) draft.Title = useEnglish ? $"Discover {keyword}" : $"Khám phá {keyword}";
        if (string.IsNullOrWhiteSpace(draft.Province)) draft.Province = useEnglish ? "Vietnam" : "Việt Nam";
        if (string.IsNullOrWhiteSpace(draft.TourKeywords))
            draft.TourKeywords = useEnglish
                ? $"{keyword}, cultural travel, festival, local experiences, discover Vietnam"
                : $"{keyword}, du lịch văn hóa, lễ hội, trải nghiệm địa phương, khám phá Việt Nam";
        if (string.IsNullOrWhiteSpace(draft.Summary))
            draft.Summary = useEnglish
                ? $"Discover the cultural significance, distinctive features and notable experiences connected to {keyword}."
                : $"Khám phá những nét đặc sắc, ý nghĩa văn hóa và trải nghiệm đáng chú ý liên quan đến {keyword}.";
        if (string.IsNullOrWhiteSpace(draft.Content))
            draft.Content = useEnglish
                ? $"{keyword} is a culturally rich subject with meaningful travel experiences. This article introduces its context, notable features and practical suggestions for travelers."
                : $"{keyword} là một chủ đề giàu giá trị văn hóa và trải nghiệm. Bài viết giới thiệu bối cảnh, những điểm đáng chú ý và gợi ý phù hợp cho du khách khi tìm hiểu sự kiện này.";
    }

    private static string EnsureUniqueTitle(string title, IReadOnlyCollection<string> usedTitles, string creativeSeed, bool useEnglish)
    {
        var normalizedUsed = usedTitles
            .Select(NormalizeTitle)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);

        var cleanTitle = CleanValue(title, 180).Trim(' ', '.', ',', '-', '–', ':');
        if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = useEnglish ? "A journey through Vietnamese festivals" : "Hành trình khám phá lễ hội Việt Nam";
        if (!normalizedUsed.Contains(NormalizeTitle(cleanTitle))) return cleanTitle;

        var hash = unchecked((uint)StringComparer.Ordinal.GetHashCode(creativeSeed));
        var angles = useEnglish ? UniqueAnglesEn : UniqueAngles;
        for (var index = 0; index < angles.Length; index++)
        {
            var angle = angles[(int)((hash + (uint)index) % (uint)angles.Length)];
            var candidate = BuildTitleWithSuffix(cleanTitle, $": {angle}");
            if (!normalizedUsed.Contains(NormalizeTitle(candidate))) return candidate;
        }

        var counter = 2;
        while (counter < 10000)
        {
            var candidate = BuildTitleWithSuffix(cleanTitle, useEnglish ? $" - New perspective {counter}" : $" - Góc nhìn mới {counter}");
            if (!normalizedUsed.Contains(NormalizeTitle(candidate))) return candidate;
            counter++;
        }

        return BuildTitleWithSuffix(cleanTitle, $" - {creativeSeed[^6..].ToUpperInvariant()}");
    }

    private static string BuildTitleWithSuffix(string title, string suffix)
    {
        const int maxLength = 180;
        var cleanSuffix = MultiSpaceRegex.Replace((suffix ?? string.Empty).Replace('\0', ' '), " ");
        if (cleanSuffix.Length > 60) cleanSuffix = cleanSuffix[..60].TrimEnd();
        var maxTitleLength = Math.Max(1, maxLength - cleanSuffix.Length);
        var titlePart = title.Length <= maxTitleLength ? title : title[..maxTitleLength].TrimEnd();
        return (titlePart + cleanSuffix).Trim();
    }

    private static string NormalizeTitle(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(character));
        }
        return MultiSpaceRegex.Replace(NonTitleCharacterRegex.Replace(builder.ToString(), " "), " ").Trim();
    }

    private static string CleanValue(string? value, int maxLength)
    {
        var cleaned = MultiSpaceRegex.Replace((value ?? string.Empty).Replace('\0', ' ').Trim(), " ");
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].Trim();
    }

    private static string CleanMultilineValue(string? value, int maxLength)
    {
        var cleaned = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\0', ' ')
            .Trim();
        cleaned = Regex.Replace(cleaned, @"[ \t]{2,}", " ");
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].Trim();
    }

    private static string Text(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    public sealed class GeneratePostContentRequest
    {
        public string? Keyword { get; set; }
        public string? Language { get; set; }
        public string? SessionId { get; set; }
    }

    private sealed record WikipediaSource(string Title, string Extract, string Url);

    private sealed record WikipediaReference(string Json, IReadOnlyList<WikipediaSource> Sources)
    {
        public static WikipediaReference Empty { get; } = new("[]", Array.Empty<WikipediaSource>());
        public bool HasResults => Sources.Count > 0;
    }

    private sealed class GeneratedPostDraft
    {
        public string? Title { get; set; }
        public string? Province { get; set; }
        public string? TourKeywords { get; set; }
        public string? Summary { get; set; }
        public string? Content { get; set; }
    }
}
