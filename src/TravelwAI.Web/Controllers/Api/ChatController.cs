using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using TravelwAI.Business.Interfaces;
using TravelwAI.Models.Requests;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class ChatController : ApiControllerBase
{
    private sealed class AiChatQuotaWindow
    {
        public DateTime WindowStartUtc { get; set; } = DateTime.UtcNow;
        public int Count { get; set; }
    }

    private static readonly ConcurrentDictionary<string, AiChatQuotaWindow> AiChatQuota = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTime> OpenRouterModelCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly IChatService _chatService;
    private readonly IFriendService _friendService;
    private readonly IFileStorageService _fileStorage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly TravelGuideRagService _travelGuideRagService;
    private readonly IMemoryCache _memoryCache;
    private readonly NpgsqlDataSource _dataSource;

    public ChatController(
        IAuthService authService,
        IChatService chatService,
        IFriendService friendService,
        IFileStorageService fileStorage,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        TravelGuideRagService travelGuideRagService,
        IMemoryCache memoryCache,
        NpgsqlDataSource dataSource) : base(authService)
    {
        _chatService = chatService;
        _friendService = friendService;
        _fileStorage = fileStorage;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _travelGuideRagService = travelGuideRagService;
        _memoryCache = memoryCache;
        _dataSource = dataSource;
    }

    [HttpPost("ai/chat")]
    public async Task<IActionResult> AskAi([FromBody] AiChatRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { success = false, detail = "Bạn chưa nhập nội dung để hỏi AI." });
        }

        var assistantMode = NormalizeAiAssistantMode(request.Assistant);

        var current = await CurrentUserAsync();
        if (!current.ok)
        {
            return Unauthorized(new
            {
                success = false,
                code = "ai_login_required",
                detail = "Bạn cần đăng nhập để dùng Chatbot AI.",
                message = "Bạn cần đăng nhập để dùng Chatbot AI."
            });
        }

        var quotaError = await TryConsumeAiChatQuotaAsync(current.userId!, current.authUser, HttpContext.RequestAborted);
        if (quotaError is not null) return quotaError;

        if (assistantMode == "travelwai")
        {
            var quickReply = TryBuildManagerQuickReply(request.Message);
            if (!string.IsNullOrWhiteSpace(quickReply))
            {
                return Ok(new { success = true, data = new { reply = quickReply }, message = "Quản lý TravelwAI đã xử lý nội bộ" });
            }
        }

        var apiKey = GetOpenRouterApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return StatusCode(500, new { success = false, detail = "Chưa cấu hình API key AI. Vui lòng kiểm tra cấu hình trên Render." });
        }

        var model = assistantMode == "guide-rag"
            ? GetOpenRouterConfigValue("RagModel", "OPENROUTER_RAG_MODEL", GetOpenRouterConfigValue("Model", "OPENROUTER_MODEL", "openrouter/auto"))
            : GetOpenRouterConfigValue("Model", "OPENROUTER_MODEL", "openrouter/auto");
        var siteUrl = GetOpenRouterConfigValue("SiteUrl", "OPENROUTER_SITE_URL", "https://travelwai.onrender.com");
        var appName = GetOpenRouterConfigValue("AppName", "OPENROUTER_APP_NAME", "TravelwAI");
        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = assistantMode == "guide-rag" ? BuildTravelGuideSystemPrompt() : "Bạn là Quản lý TravelwAI, trợ lý hỗ trợ người dùng sử dụng website TravelwAI. Không dùng markdown, không bảng, không gạch đầu dòng, không emoji, không dùng các ký tự trang trí như |, >, #, *, ---. Trả lời tối đa khoảng 150 chữ bằng tiếng Việt. Hỗ trợ điều hướng trang, lịch trình, kế hoạch, nhắn tin, tour du lịch, hồ sơ, thông báo, phản hồi và tài khoản. Khi người dùng muốn mở trang, hướng dẫn dùng cú pháp: tới trang [tên trang]. Khi không chắc, hỏi lại một câu ngắn."
            }
        };

        var contextBlock = assistantMode == "guide-rag"
            ? await _travelGuideRagService.BuildRagContextAsync(request.Message, request.Context, HttpContext.RequestAborted)
            : BuildAiContextBlock(request.Context);
        if (!string.IsNullOrWhiteSpace(contextBlock))
        {
            messages.Add(new { role = "system", content = contextBlock });
        }

        if (request.History is not null)
        {
            foreach (var item in request.History.Where(item => !string.IsNullOrWhiteSpace(item.Content)).TakeLast(12))
            {
                var role = string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                messages.Add(new { role, content = item.Content!.Trim() });
            }
        }

        messages.Add(new { role = "user", content = request.Message.Trim() });

        var responseCacheKey = BuildAiResponseCacheKey(assistantMode, request.Message, contextBlock, request.History);
        if (TryGetCachedAiReply(responseCacheKey, out var cachedReply))
        {
            return Ok(new { success = true, data = new { reply = cachedReply, cached = true }, message = "AI đã trả lời từ cache" });
        }

        OpenRouterChatResult aiResponse;
        try
        {
            aiResponse = await SendOpenRouterChatAsync(
                GetOpenRouterModelCandidates(assistantMode, model),
                messages,
                assistantMode == "guide-rag" ? 0.45 : 0.35,
                assistantMode == "guide-rag" ? 360 : 280,
                siteUrl,
                appName,
                apiKey,
                HttpContext.RequestAborted);
        }
        catch (OpenRouterChatException ex)
        {
            return StatusCode(ex.StatusCode, new { success = false, detail = ex.Message, raw = ex.Raw });
        }

        var cleaned = CleanSimpleChatbotReply(aiResponse.Content, 150, IsOpenRouterAnswerCutOff(aiResponse.FinishReason));
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Mình chưa nhận được câu trả lời hoàn chỉnh từ AI. Bạn hỏi lại giúp mình nhé.";
        CacheAiReply(responseCacheKey, cleaned, assistantMode);
        return Ok(new { success = true, data = new { reply = cleaned, model = aiResponse.Model }, message = "AI đã trả lời" });
    }

    private sealed class OpenRouterChatResult
    {
        public string Content { get; init; } = string.Empty;
        public string? FinishReason { get; init; }
        public string Model { get; init; } = string.Empty;
    }

    private sealed class OpenRouterChatException : Exception
    {
        public int StatusCode { get; }
        public string Raw { get; }

        public OpenRouterChatException(int statusCode, string message, string raw = "") : base(message)
        {
            StatusCode = statusCode;
            Raw = raw;
        }
    }

    private async Task<OpenRouterChatResult> SendOpenRouterChatAsync(
        IReadOnlyList<string> modelCandidates,
        List<object> messages,
        double temperature,
        int maxTokens,
        string siteUrl,
        string appName,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var models = modelCandidates.Count > 0 ? modelCandidates : new[] { "openrouter/auto" };
        var timeoutSeconds = Math.Clamp(GetOpenRouterConfigInt("TimeoutSeconds", "OPENROUTER_TIMEOUT_SECONDS", 28), 8, 60);

        foreach (var candidateModel in models.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryGetOpenRouterModelCooldown(candidateModel, out var waitSeconds))
            {
                errors.Add($"{candidateModel}: đang tạm nghỉ {waitSeconds}s do lần gọi trước bị quá tải.");
                continue;
            }

            var payload = new Dictionary<string, object?>
            {
                ["model"] = candidateModel,
                ["messages"] = messages,
                ["temperature"] = temperature,
                ["max_tokens"] = maxTokens,
                ["provider"] = new { allow_fallbacks = true }
            };

            if (ShouldSendOpenRouterReasoningOptions())
            {
                payload["reasoning"] = BuildOpenRouterMinimalReasoningOptions();
            }

            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildOpenRouterChatCompletionsUri());
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            httpRequest.Headers.TryAddWithoutValidation("HTTP-Referer", siteUrl);
            httpRequest.Headers.TryAddWithoutValidation("X-Title", appName);
            httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            string responseText;
            try
            {
                response = await http.SendAsync(httpRequest, cancellationToken);
                responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                MarkOpenRouterModelCooldown(candidateModel, 90);
                errors.Add($"{candidateModel}: quá thời gian chờ, đã chuyển model khác.");
                continue;
            }
            catch (HttpRequestException ex)
            {
                MarkOpenRouterModelCooldown(candidateModel, 90);
                errors.Add($"{candidateModel}: lỗi kết nối OpenRouter {ex.Message}.");
                continue;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    var detail = BuildOpenRouterErrorMessage(statusCode, responseText);
                    errors.Add($"{candidateModel}: {detail}");

                    if (statusCode == 401 || statusCode == 402 || statusCode == 403)
                    {
                        throw new OpenRouterChatException(statusCode, detail, responseText);
                    }

                    if (ShouldCooldownOpenRouterModel(statusCode, responseText))
                    {
                        var retryAfter = GetRetryAfterSeconds(response) ?? GetOpenRouterModelCooldownSeconds(statusCode, responseText);
                        MarkOpenRouterModelCooldown(candidateModel, retryAfter);
                    }

                    continue;
                }
            }

            JsonNode? json;
            try
            {
                json = JsonNode.Parse(responseText);
            }
            catch (JsonException)
            {
                MarkOpenRouterModelCooldown(candidateModel, 45);
                errors.Add($"{candidateModel}: AI trả về dữ liệu không hợp lệ.");
                continue;
            }

            var answer = json?["choices"]?[0]?["message"]?["content"]?.ToString();
            var finishReason = json?["choices"]?[0]?["finish_reason"]?.ToString();
            if (string.IsNullOrWhiteSpace(answer))
            {
                MarkOpenRouterModelCooldown(candidateModel, 45);
                errors.Add($"{candidateModel}: AI chưa trả về nội dung hợp lệ.");
                continue;
            }

            return new OpenRouterChatResult
            {
                Content = answer,
                FinishReason = finishReason,
                Model = candidateModel
            };
        }

        var joined = string.Join("; ", errors.Where(item => !string.IsNullOrWhiteSpace(item)).Take(4));
        if (string.IsNullOrWhiteSpace(joined)) joined = "Không có model khả dụng.";
        throw new OpenRouterChatException(502, "OpenRouter chưa trả lời được: " + joined);
    }

    private IReadOnlyList<string> GetOpenRouterModelCandidates(string assistantMode, string primaryModel)
    {
        var values = new List<string>();
        void AddMany(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var item in raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var clean = item.Trim();
                if (!string.IsNullOrWhiteSpace(clean)) values.Add(clean);
            }
        }

        AddMany(primaryModel);
        AddMany(assistantMode == "guide-rag"
            ? GetOpenRouterConfigValue("RagFallbackModels", "OPENROUTER_RAG_FALLBACK_MODELS", string.Empty)
            : GetOpenRouterConfigValue("FallbackModels", "OPENROUTER_FALLBACK_MODELS", string.Empty));
        AddMany(GetOpenRouterConfigValue("FallbackModels", "OPENROUTER_FALLBACK_MODELS", string.Empty));
        AddMany(GetDefaultOpenRouterFreeFallbackModels());
        AddMany("openrouter/auto");

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string GetDefaultOpenRouterFreeFallbackModels()
    {
        return string.Join(',', new[]
        {
            "qwen/qwen3-14b:free",
            "google/gemma-3-27b-it:free",
            "deepseek/deepseek-chat-v3-0324:free",
            "deepseek/deepseek-r1-0528:free",
            "mistralai/mistral-small-3.2-24b-instruct:free",
            "openai/gpt-oss-120b:free"
        });
    }

    private async Task<IActionResult?> TryConsumeAiChatQuotaAsync(string userId, Dictionary<string, object?>? authUser, CancellationToken cancellationToken)
    {
        var limit = GetAiChatLimitPerFiveMinutes(authUser);
        if (limit <= 0) return null;

        try
        {
            return await TryConsumeAiChatQuotaFromDatabaseAsync(userId, authUser, limit, cancellationToken);
        }
        catch
        {
            return TryConsumeAiChatQuotaFromMemory(userId, authUser, limit);
        }
    }

    private async Task<IActionResult?> TryConsumeAiChatQuotaFromDatabaseAsync(string userId, Dictionary<string, object?>? authUser, int limit, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        DateTime? windowStart = null;
        var count = 0;
        await using (var select = conn.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "select window_start_utc, count from ai_chat_quota_windows where user_id = @user_id for update;";
            select.Parameters.AddWithValue("user_id", userId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                windowStart = reader.GetDateTime(0);
                count = reader.GetInt32(1);
            }
        }

        if (windowStart is null || (now - windowStart.Value.ToUniversalTime()).TotalMinutes >= 5)
        {
            await using var upsert = conn.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = """
                insert into ai_chat_quota_windows(user_id, window_start_utc, count, updated_at)
                values (@user_id, @window_start_utc, 1, now())
                on conflict (user_id) do update
                set window_start_utc = excluded.window_start_utc,
                    count = 1,
                    updated_at = now();
                """;
            upsert.Parameters.AddWithValue("user_id", userId);
            upsert.Parameters.AddWithValue("window_start_utc", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return null;
        }

        if (count >= limit)
        {
            await tx.RollbackAsync(cancellationToken);
            return BuildAiQuotaExceededResponse(authUser, limit, count, windowStart.Value, now);
        }

        await using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "update ai_chat_quota_windows set count = count + 1, updated_at = now() where user_id = @user_id;";
            update.Parameters.AddWithValue("user_id", userId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return null;
    }

    private IActionResult? TryConsumeAiChatQuotaFromMemory(string userId, Dictionary<string, object?>? authUser, int limit)
    {
        var now = DateTime.UtcNow;
        var window = AiChatQuota.GetOrAdd(userId, _ => new AiChatQuotaWindow { WindowStartUtc = now, Count = 0 });
        lock (window)
        {
            if ((now - window.WindowStartUtc).TotalMinutes >= 5)
            {
                window.WindowStartUtc = now;
                window.Count = 0;
            }

            if (window.Count >= limit)
            {
                return BuildAiQuotaExceededResponse(authUser, limit, window.Count, window.WindowStartUtc, now);
            }

            window.Count++;
        }

        return null;
    }

    private IActionResult BuildAiQuotaExceededResponse(Dictionary<string, object?>? authUser, int limit, int used, DateTime windowStartUtc, DateTime nowUtc)
    {
        var role = GetAccountRole(authUser);
        var isFree = string.Equals(role, "Free", StringComparison.OrdinalIgnoreCase);
        var resetSeconds = Math.Max(1, (int)Math.Ceiling((windowStartUtc.ToUniversalTime().AddMinutes(5) - nowUtc).TotalSeconds));
        Response.Headers["Retry-After"] = resetSeconds.ToString(CultureInfo.InvariantCulture);
        return StatusCode(429, new
        {
            success = false,
            code = isFree ? "free_ai_quota_exceeded" : "ai_quota_exceeded",
            role,
            limit,
            used,
            windowSeconds = 300,
            retryAfterSeconds = resetSeconds,
            detail = isFree
                ? "Tài khoản Free đã dùng hết 3 câu hỏi trong 5 phút. Vui lòng nâng cấp gói hoặc thử lại sau."
                : $"Tài khoản {role} đã dùng hết {limit} câu hỏi trong 5 phút. Vui lòng thử lại sau.",
            message = isFree
                ? "Tài khoản Free đã dùng hết 3 câu hỏi trong 5 phút."
                : $"Tài khoản {role} đã dùng hết {limit} câu hỏi trong 5 phút."
        });
    }

    [HttpPost("ai/schedule-plan")]
    public async Task<IActionResult> AskAiSchedulePlan([FromBody] AiSchedulePlanRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        if (request is null || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { success = false, detail = "Bạn chưa nhập yêu cầu lập lịch trình cho AI." });
        }

        if (!CanCreateSchedule(current.authUser))
        {
            return StatusCode(403, new { success = false, detail = "Tài khoản Free chưa dùng được lịch trình. Vui lòng nâng cấp VIP hoặc Premium.", message = "Tài khoản Free chưa dùng được lịch trình." });
        }

        var apiKey = GetOpenRouterApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return StatusCode(500, new { success = false, detail = "Chưa cấu hình API key AI. Vui lòng kiểm tra cấu hình trên Render." });
        }

        var model = GetOpenRouterConfigValue("Model", "OPENROUTER_MODEL", "openrouter/auto");
        var siteUrl = GetOpenRouterConfigValue("SiteUrl", "OPENROUTER_SITE_URL", "https://travelwai.onrender.com");
        var appName = GetOpenRouterConfigValue("AppName", "OPENROUTER_APP_NAME", "TravelwAI");
        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = "Bạn là AI lập lịch trình du lịch cho TravelwAI. Trả lời DUY NHẤT bằng JSON hợp lệ, không markdown. Nếu thiếu thông tin quan trọng, trả status='needs_more_info' và question bằng tiếng Việt. Nếu đủ thông tin, trả status='ready', reply ngắn gọn và schedule theo schema: {title:string, description:string, start_date:'yyyy-MM-dd', end_date:'yyyy-MM-dd', budget:number|null, currency:'VND'|'USD'|'EUR', tags:string[], days:[{day_number:number, date:'yyyy-MM-dd', destinations:[{name:string, description:string, estimated_duration:string|null, time_phase:string, time_range:string}]}]}."
            }
        };

        if (request.History is not null)
        {
            foreach (var item in request.History.Where(item => !string.IsNullOrWhiteSpace(item.Content)).TakeLast(8))
            {
                var role = string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                messages.Add(new { role, content = item.Content!.Trim() });
            }
        }

        messages.Add(new { role = "user", content = BuildSchedulePlannerPrompt(request) });
        string answer;
        try
        {
            var scheduleAi = await SendOpenRouterChatAsync(
                GetOpenRouterModelCandidates("travelwai", model),
                messages,
                0.35,
                1800,
                siteUrl,
                appName,
                apiKey,
                HttpContext.RequestAborted);
            answer = scheduleAi.Content;
        }
        catch (OpenRouterChatException ex)
        {
            return StatusCode(ex.StatusCode, new { success = false, detail = ex.Message, raw = ex.Raw });
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            return StatusCode(502, new { success = false, detail = "AI chưa trả về nội dung lập lịch trình hợp lệ." });
        }

        var aiResult = TryParseAiJsonObject(answer);
        if (aiResult is null)
        {
            return StatusCode(502, new { success = false, detail = "AI chưa trả về JSON hợp lệ để lập lịch trình. Vui lòng hỏi lại ngắn gọn hơn.", raw = answer });
        }

        if (aiResult["status"] is null)
        {
            aiResult["status"] = JsonValue.Create(aiResult["schedule"] is null ? "needs_more_info" : "ready");
        }
        if (aiResult["reply"] is null && aiResult["question"] is not null)
        {
            aiResult["reply"] = JsonValue.Create(aiResult["question"]!.ToString());
        }

        return Ok(new { success = true, data = aiResult, message = "AI đã xử lý yêu cầu lập lịch trình" });
    }

    private static string BuildSchedulePlannerPrompt(AiSchedulePlanRequest request)
    {
        var currentScheduleJson = "{}";
        if (request.CurrentSchedule.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            currentScheduleJson = request.CurrentSchedule.GetRawText();
        }

        var today = GetVietnamToday();
        var tomorrow = today.AddDays(1);
        return "Ngày hiện tại tại Việt Nam: " + today.ToString("yyyy-MM-dd") + "\n" +
               "Ngày mai tại Việt Nam: " + tomorrow.ToString("yyyy-MM-dd") + "\n\n" +
               "Yêu cầu mới của người dùng:\n" + request.Message!.Trim() + "\n\n" +
               "Thông tin hiện đang có trong form lịch trình của TravelwAI:\n" + currentScheduleJson + "\n\n" +
               "Quy tắc: hiểu hôm nay/ngày mai theo ngày đã cung cấp; nếu người dùng nói số ngày, tính end_date = start_date + số ngày - 1; nếu chỉ sửa một phần, giữ nguyên phần khác trong form; 1 củ = 1000000 VND, 500k = 500000 VND. Trả JSON đúng schema.";
    }

    private static DateOnly GetVietnamToday()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var vietnamNow = utcNow.ToOffset(TimeSpan.FromHours(7));
        return DateOnly.FromDateTime(vietnamNow.DateTime);
    }

    private static string NormalizeAiAssistantMode(string? assistant)
    {
        var value = NormalizeVietnameseForSearch(assistant ?? string.Empty).Replace("_", "-").Trim();
        if (value.Contains("guide") || value.Contains("rag") || value.Contains("huong-dan") || value.Contains("huong dan")) return "guide-rag";
        return "travelwai";
    }

    private static string? TryBuildManagerQuickReply(string? message)
    {
        var normalized = NormalizeVietnameseForSearch(message ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        if (Regex.IsMatch(normalized, @"dang\s*xuat|thoat\s*tai\s*khoan|log\s*out")) return "Đang đăng xuất tài khoản.";
        if (Regex.IsMatch(normalized, @"doi\s*mat\s*khau|change\s*password|doi\s*password")) return "Đang mở Hồ sơ để đổi mật khẩu.";
        if (Regex.IsMatch(normalized, @"(co\s*)?trang\s*nao|danh\s*sach\s*trang|menu|chuc\s*nang|huong\s*dan\s*(web|website)?"))
        {
            return "Các trang TravelwAI gồm Đăng nhập, Đăng ký, Bản đồ Việt Nam, Lịch trình, Kế hoạch, Bảng giá, Giỏ hàng, Thanh toán, Hồ sơ, Nhắn tin, Liên hệ, Thông báo, Bài viết, Tour du lịch, Sales, Business, Admin và Manage. Bạn có thể nhắn: mở trang lịch trình.";
        }

        var routes = new (string Pattern, string Reply)[]
        {
            (@"dang\s*nhap|login", "Đang mở trang Đăng nhập."),
            (@"dang\s*ky|tao\s*tai\s*khoan|register|sign\s*up|signup", "Đang mở trang Đăng ký."),
            (@"quen\s*mat\s*khau|khoi\s*phuc\s*mat\s*khau|lay\s*lai\s*mat\s*khau|forgot\s*password|reset\s*password", "Đang mở trang Quên mật khẩu."),
            (@"bang\s*gia|pricing|gia\s*goi|goi\s*tai\s*khoan|mua\s*goi", "Đang mở Bảng giá."),
            (@"gio\s*hang|cart", "Đang mở Giỏ hàng."),
            (@"thanh\s*toan|checkout|xac\s*nhan\s*thanh\s*toan|qr\s*thanh\s*toan", "Đang mở Thanh toán."),
            (@"manage|quan\s*ly\s*goi|quan\s*ly\s*don\s*goi|don\s*goi", "Đang mở Manage."),
            (@"business|trang\s*business|doanh\s*nghiep|kinh\s*doanh|cong\s*ty", "Đang mở Business."),
            (@"trang\s*lien\s*he|contact\s*page|lien\s*he\s*travelwai", "Đang mở Liên hệ."),
            (@"sales|trang\s*sales|qua\s*sales|ban\s*tour|don\s*ban\s*tour", "Đang mở trang Sales."),
            (@"admin|quan\s*tri|quan\s*ly\s*he\s*thong", "Đang mở trang Admin."),
            (@"lap\s*lich\s*trinh|tao\s*lich\s*trinh|lich\s*trinh", "Đang mở trang Lịch trình."),
            (@"lap\s*ke\s*hoach|tao\s*ke\s*hoach|ke\s*hoach", "Đang mở trang Kế hoạch."),
            (@"ban\s*do|tinh\s*thanh|34\s*tinh|viet\s*nam", "Đang mở Bản đồ Việt Nam."),
            (@"bai\s*viet|tin\s*du\s*lich|kham\s*pha\s*bai", "Đang mở trang Bài viết."),
            (@"tour\s*du\s*lich|dat\s*tour|xem\s*tour|qua\s*tour|trang\s*tour|^tour$|\btour\b", "Đang mở trang Tour du lịch."),
            (@"tin\s*nhan|nhan\s*tin|messaging|chat", "Đang mở trang Nhắn tin."),
            (@"ho\s*so|thong\s*tin\s*ca\s*nhan|tai\s*khoan|doi\s*ten", "Đang mở trang Hồ sơ."),
            (@"thong\s*bao|notification", "Đang mở trang Thông báo."),
            (@"phan\s*hoi|gop\s*y|ho\s*tro", "Đang mở hội thoại với Admin."),
            (@"trang\s*chu|home", "Đang mở trang chủ."),
            (@"landing|gioi\s*thieu|trang\s*gioi\s*thieu", "Đang mở giới thiệu TravelwAI.")
        };

        foreach (var route in routes)
        {
            if (Regex.IsMatch(normalized, route.Pattern)) return route.Reply;
        }

        return null;
    }

    private static string BuildTravelGuideSystemPrompt()
    {
        return "Bạn là Hướng dẫn viên RAG AI của TravelwAI. Trả lời bằng tiếng Việt tự nhiên, đúng vai hướng dẫn viên du lịch. Dựa ưu tiên vào RAG_CONTEXT được cung cấp. Trả lời tối đa khoảng 150 chữ, không markdown, không bảng, không gạch đầu dòng, không emoji, không dùng các ký tự trang trí như |, >, #, *, ---. Khi dùng dữ liệu truy xuất, nêu nguồn ngắn gọn trong câu trả lời nếu có URL hoặc tên nguồn. Không bịa nguồn pháp lý, danh hiệu, quyết định, giá vé, giờ mở cửa. Nếu thiếu dữ liệu chắc chắn, nói chưa đủ nguồn để khẳng định. Với truyền thuyết/lời kể, ghi rõ là truyền thuyết hoặc lời kể dân gian. Nếu câu trả lời bị giới hạn độ dài, phải kết thúc ở một câu hoàn chỉnh.";
    }

    private static string NormalizeVietnameseForSearch(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var formD = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(ch switch { 'đ' => 'd', 'Đ' => 'D', _ => ch });
        }
        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private static string BuildAiContextBlock(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return string.Empty;
        var clean = Regex.Replace(context.Trim(), @"\s+", " ");
        if (clean.Length > 8000) clean = clean[..8000];
        return "CONTEXT từ giao diện TravelwAI: " + clean;
    }

    private static string CleanSimpleChatbotReply(string text, int maxWords, bool dropUnfinishedTail = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var clean = text.Trim();
        clean = Regex.Replace(clean, @"```(?:[a-zA-Z0-9_-]+)?", string.Empty);
        clean = clean.Replace("```", string.Empty);
        clean = Regex.Replace(clean, @"(?m)^\s*(?:[-*_]{3,}|[|]+)\s*$", " ");
        clean = Regex.Replace(clean, @"(?m)^\s*>+\s?", string.Empty);
        clean = Regex.Replace(clean, @"(?m)^\s{0,3}#{1,6}\s*", string.Empty);
        clean = Regex.Replace(clean, @"\*{1,3}([^*]+)\*{1,3}", "$1");
        clean = Regex.Replace(clean, @"[_`~]+", string.Empty);
        clean = clean.Replace("|", " ").Replace(">", " ").Replace("#", string.Empty).Replace("*", string.Empty);
        clean = Regex.Replace(clean, @"-{3,}", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();

        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > maxWords)
        {
            clean = string.Join(' ', words.Take(maxWords));
            dropUnfinishedTail = true;
        }

        if (dropUnfinishedTail)
        {
            clean = DropUnfinishedLastSentence(clean);
        }

        return clean.Trim();
    }

    private static string DropUnfinishedLastSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var clean = text.Trim();
        var end = LastSentenceEndIndex(clean);
        if (end >= 40) return clean[..(end + 1)].Trim();
        return clean.Length <= 220 ? clean : string.Empty;
    }

    private static int LastSentenceEndIndex(string text)
    {
        var last = -1;
        foreach (var mark in new[] { '.', '!', '?', '…', '。' })
        {
            var index = text.LastIndexOf(mark);
            if (index > last) last = index;
        }
        return last;
    }

    private static JsonObject? TryParseAiJsonObject(string text)
    {
        var cleaned = text.Trim();
        cleaned = Regex.Replace(cleaned, @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s*```$", "", RegexOptions.IgnoreCase).Trim();
        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start >= 0 && end > start) cleaned = cleaned[start..(end + 1)];
        try { return JsonNode.Parse(cleaned) as JsonObject; } catch { return null; }
    }

    private static bool IsOpenRouterAnswerCutOff(string? finishReason) => string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);

    private string? GetOpenRouterApiKey()
    {
        var apiKey = GetOpenRouterConfigValue("ApiKey", "OPENROUTER_API_KEY", string.Empty);
        return IsMissingOpenRouterSecret(apiKey) ? null : apiKey.Trim();
    }

    private string GetOpenRouterConfigValue(string key, string envName, string fallback)
    {
        var value = _configuration[$"OpenRouter:{key}"];
        if (string.IsNullOrWhiteSpace(value)) value = _configuration[envName];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }


    private int GetOpenRouterConfigInt(string key, string envName, int fallback)
    {
        var value = GetOpenRouterConfigValue(key, envName, fallback.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static bool ShouldCooldownOpenRouterModel(int statusCode, string responseText)
    {
        if (statusCode is 408 or 409 or 425 or 429 or 500 or 502 or 503 or 504) return true;
        var text = responseText ?? string.Empty;
        return text.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("capacity", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("provider returned error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no endpoints found", StringComparison.OrdinalIgnoreCase);
    }

    private int GetOpenRouterModelCooldownSeconds(int statusCode, string responseText)
    {
        if (statusCode == 429) return Math.Clamp(GetOpenRouterConfigInt("RateLimitCooldownSeconds", "OPENROUTER_RATE_LIMIT_COOLDOWN_SECONDS", 240), 30, 900);
        if (statusCode == 404 || (responseText ?? string.Empty).Contains("no endpoints found", StringComparison.OrdinalIgnoreCase)) return 1800;
        if (statusCode is 500 or 502 or 503 or 504) return Math.Clamp(GetOpenRouterConfigInt("OverloadCooldownSeconds", "OPENROUTER_OVERLOAD_COOLDOWN_SECONDS", 180), 30, 900);
        return 120;
    }

    private static int? GetRetryAfterSeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is not null) return Math.Max(1, (int)Math.Ceiling(retryAfter.Delta.Value.TotalSeconds));
        if (retryAfter?.Date is not null)
        {
            var seconds = (retryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
            return Math.Max(1, (int)Math.Ceiling(seconds));
        }
        return null;
    }

    private static void MarkOpenRouterModelCooldown(string model, int seconds)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 15, 1800));
        OpenRouterModelCooldownUntil.AddOrUpdate(model.Trim(), until, (_, oldUntil) => oldUntil > until ? oldUntil : until);
    }

    private static bool TryGetOpenRouterModelCooldown(string model, out int waitSeconds)
    {
        waitSeconds = 0;
        if (string.IsNullOrWhiteSpace(model)) return false;
        if (!OpenRouterModelCooldownUntil.TryGetValue(model.Trim(), out var until)) return false;
        var now = DateTime.UtcNow;
        if (until <= now)
        {
            OpenRouterModelCooldownUntil.TryRemove(model.Trim(), out _);
            return false;
        }
        waitSeconds = Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
        return true;
    }

    private static string BuildAiResponseCacheKey(string assistantMode, string message, string? context, List<AiHistoryMessage>? history)
    {
        var historyText = string.Empty;
        if (history is not null)
        {
            historyText = string.Join("\n", history.TakeLast(4).Where(item => !string.IsNullOrWhiteSpace(item.Content)).Select(item => (item.Role ?? "user") + ":" + item.Content!.Trim()));
        }
        var raw = assistantMode + "\n" + NormalizeVietnameseForSearch(message) + "\n" + (context ?? string.Empty).Trim() + "\n" + historyText;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return "ai-chat:" + hash;
    }

    private bool TryGetCachedAiReply(string cacheKey, out string reply)
    {
        if (_memoryCache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            reply = cached;
            return true;
        }
        reply = string.Empty;
        return false;
    }

    private void CacheAiReply(string cacheKey, string reply, string assistantMode)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(reply)) return;
        var minutes = assistantMode == "guide-rag"
            ? Math.Clamp(GetOpenRouterConfigInt("RagCacheMinutes", "OPENROUTER_RAG_CACHE_MINUTES", 20), 1, 120)
            : Math.Clamp(GetOpenRouterConfigInt("ChatCacheMinutes", "OPENROUTER_CHAT_CACHE_MINUTES", 10), 1, 120);
        _memoryCache.Set(cacheKey, reply.Trim(), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(minutes),
            Size = 1
        });
    }

    private bool ShouldSendOpenRouterReasoningOptions()
    {
        var value = GetOpenRouterConfigValue("UseReasoning", "OPENROUTER_USE_REASONING", "false");
        return bool.TryParse(value, out var enabled) && enabled;
    }

    private Uri BuildOpenRouterChatCompletionsUri()
    {
        var baseUrl = GetOpenRouterConfigValue("BaseUrl", "OPENROUTER_BASE_URL", "https://openrouter.ai/api/v1");
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)) uri = new Uri("https://openrouter.ai/api/v1/");
        return new Uri(uri, "chat/completions");
    }

    private static object BuildOpenRouterMinimalReasoningOptions() => new { effort = "minimal", exclude = true };

    private static bool IsMissingOpenRouterSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var clean = value.Trim();
        return clean.Equals("PASTE_OPENROUTER_API_KEY_HERE", StringComparison.OrdinalIgnoreCase)
            || clean.Equals("YOUR_OPENROUTER_API_KEY", StringComparison.OrdinalIgnoreCase)
            || clean.Equals("<OPENROUTER_API_KEY>", StringComparison.OrdinalIgnoreCase)
            || clean.Contains("YOUR_OPENROUTER_API_KEY", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildOpenRouterErrorMessage(int statusCode, string responseText)
    {
        var message = responseText;
        try { message = JsonNode.Parse(responseText)?["error"]?["message"]?.ToString() ?? responseText; } catch { }
        message = Regex.Replace(message ?? string.Empty, @"\s+", " ").Trim();
        if (message.Length > 260) message = message[..260].Trim();

        if (statusCode == 400) return "OpenRouter từ chối request. Kiểm tra model, payload hoặc tham số reasoning. Chi tiết: " + message;
        if (statusCode == 401) return "OpenRouter API key chưa đúng hoặc chưa được cấu hình trong OPENROUTER_API_KEY.";
        if (statusCode == 402) return "Tài khoản OpenRouter hết credits hoặc model yêu cầu credits. Đổi model free hoặc nạp credits.";
        if (statusCode == 403) return "OpenRouter không cho phép dùng model/API key hiện tại. Kiểm tra quyền truy cập model.";
        if (statusCode == 404 || message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || message.Contains("not found", StringComparison.OrdinalIgnoreCase)) return "Model OpenRouter không khả dụng hoặc sai tên model. Hãy đổi OPENROUTER_RAG_MODEL/OPENROUTER_MODEL.";
        if (statusCode == 429 || message.Contains("rate", StringComparison.OrdinalIgnoreCase) || message.Contains("limit", StringComparison.OrdinalIgnoreCase)) return "OpenRouter đang giới hạn lượt gọi hoặc model free quá tải. Đổi model/fallback model hoặc thử lại sau.";
        if (string.IsNullOrWhiteSpace(message)) return "OpenRouter lỗi HTTP " + statusCode + ".";
        return "OpenRouter lỗi HTTP " + statusCode + ": " + message;
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> GetConversations()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var conversations = await _chatService.GetConversationsAsync(current.userId!);
        return Ok(new { success = true, data = conversations, message = "Đã tải danh sách cuộc trò chuyện" });
    }

    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation(CreateConversationRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var participantIds = (request.ParticipantIds ?? new List<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Where(id => !string.Equals(id, current.userId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (participantIds.Count >= 2)
        {
            var groupId = await _chatService.CreateGroupConversationAsync(current.userId!, participantIds, request.GroupName);
            return groupId is null
                ? StatusCode(500, new { success = false, detail = "Không thể tạo nhóm trò chuyện" })
                : Ok(new { success = true, conversation_id = groupId, is_group = true, message = "Đã tạo nhóm trò chuyện" });
        }

        var otherUserId = !string.IsNullOrWhiteSpace(request.OtherUserId)
            ? request.OtherUserId.Trim()
            : participantIds.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(otherUserId))
        {
            return BadRequest(new { success = false, detail = "Thiếu mã người dùng cần trò chuyện" });
        }

        var id = await _chatService.CreateConversationAsync(current.userId!, otherUserId);
        return id is null
            ? StatusCode(500, new { success = false, detail = "Không thể tạo cuộc trò chuyện" })
            : Ok(new { success = true, conversation_id = id, is_group = false, message = "Đã tạo cuộc trò chuyện" });
    }

    [HttpPost("support/admin-conversation")]
    public async Task<IActionResult> CreateSupportAdminConversation()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var conversationId = await _chatService.CreateOrGetSupportAdminConversationAsync(current.userId!);
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return NotFound(new { success = false, detail = "Chưa tìm thấy tài khoản Admin chính để nhận hỗ trợ." });
        }

        return Ok(new { success = true, conversation_id = conversationId, message = "Đã mở hội thoại Admin chính." });
    }

    [HttpDelete("support/admin-conversation")]
    public async Task<IActionResult> DeleteSupportAdminConversation()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var deleted = await _chatService.DeleteSupportAdminConversationsForUserAsync(current.userId!);
        return Ok(new { success = true, deleted, message = "Đã xoá hội thoại hỗ trợ Admin chính." });
    }

    [HttpGet("conversations/{conversationId}/messages")]
    public async Task<IActionResult> GetMessages(string conversationId, [FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var conversations = await _chatService.GetConversationsAsync(current.userId!);
        if (!conversations.Any(c => c.GetValueOrDefault("id")?.ToString() == conversationId)) return StatusCode(403, new { success = false, detail = "Bạn không có quyền truy cập cuộc trò chuyện này" });
        var messages = await _chatService.GetMessagesAsync(conversationId, limit, offset);
        return Ok(new { success = true, data = messages, message = "Đã tải tin nhắn" });
    }

    [HttpPut("conversations/{conversationId}/name")]
    public async Task<IActionResult> UpdateConversationName(string conversationId, [FromBody] UpdateConversationNameRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return BadRequest(new { success = false, detail = "Tên không được để trống." });
        }

        var updated = await _chatService.UpdateConversationDisplayNameAsync(conversationId, current.userId!, request.DisplayName);
        if (updated is null)
        {
            return NotFound(new { success = false, detail = "Không tìm thấy cuộc trò chuyện hoặc bạn không có quyền đổi tên." });
        }

        return Ok(new { success = true, data = updated, message = "Đã lưu tên cuộc trò chuyện." });
    }

    [HttpPost("conversations/{conversationId}/attachments")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> UploadConversationAttachment(string conversationId, IFormFile? file)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var conversations = await _chatService.GetConversationsAsync(current.userId!);
        if (!conversations.Any(c => c.GetValueOrDefault("id")?.ToString() == conversationId))
        {
            return StatusCode(403, new { success = false, detail = "Bạn không có quyền truy cập cuộc trò chuyện này" });
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest(new { success = false, detail = "Chưa chọn tệp đính kèm" });
        }

        var url = await _fileStorage.SaveFileAsync(file, current.userId!, $"chat/{conversationId}");
        if (url is null)
        {
            return BadRequest(new { success = false, detail = "Tệp không hợp lệ, quá lớn hoặc chưa được hỗ trợ" });
        }

        return Ok(new
        {
            success = true,
            data = new
            {
                name = Path.GetFileName(file.FileName),
                url,
                contentType = file.ContentType,
                size = file.Length
            },
            message = "Đã tải tệp đính kèm"
        });
    }

    [HttpDelete("conversations/{conversationId}")]
    public async Task<IActionResult> DeleteConversation(string conversationId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var deleted = await _chatService.DeleteConversationAsync(conversationId, current.userId!);
        if (!deleted) return NotFound(new { success = false, detail = "Không tìm thấy cuộc trò chuyện hoặc bạn không có quyền xóa." });
        return Ok(new { success = true, message = "Đã xóa cuộc trò chuyện." });
    }

    [HttpPost("friends/request")]
    public async Task<IActionResult> SendFriendRequest(FriendRequestPayload payload)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var result = await _friendService.CreateFriendRequestAsync(current.userId!, payload.TargetUserEmail);
        if (result.GetValueOrDefault("success") is bool ok && ok) return Ok(result);
        var message = result.GetValueOrDefault("message")?.ToString() ?? "Đã xảy ra lỗi";
        return Conflict(new { success = false, detail = message, message });
    }

    [HttpGet("friend_requests")]
    public async Task<IActionResult> GetFriendRequests()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var (friends, pending) = await _friendService.GetFriendsAsync(current.userId!);
        return Ok(new { success = true, data = friends, friends, pending, message = "Đã tải trạng thái bạn bè" });
    }

    [HttpGet("get_friends")]
    public async Task<IActionResult> GetFriendsLegacy()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var (friends, pending) = await _friendService.GetFriendsAsync(current.userId!);
        return Ok(new { success = true, data = friends, friends, pending, message = "Đã tải danh sách bạn bè" });
    }

    [HttpPost("friend_requests")]
    public async Task<IActionResult> UpdateFriendStatus([FromForm] string request_email, [FromForm] string action)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var result = await _friendService.UpdateFriendStatusAsync(request_email, current.userId!, action);
        if (result.GetValueOrDefault("success") is bool ok && ok) return Ok(new { success = true, data = result, message = result.GetValueOrDefault("message") });
        var message = result.GetValueOrDefault("message")?.ToString() ?? "Không thể xử lý yêu cầu kết bạn.";
        return BadRequest(new { success = false, data = result, message });
    }

    [HttpDelete("friends/{friendUserId}")]
    public async Task<IActionResult> RemoveFriend(string friendUserId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var result = await _friendService.RemoveFriendAsync(current.userId!, friendUserId);
        if (result.GetValueOrDefault("success") is bool ok && ok) return Ok(result);
        var message = result.GetValueOrDefault("message")?.ToString() ?? "Không thể xóa bạn bè.";
        return NotFound(new { success = false, data = result, message, detail = message });
    }

    [HttpPost("support/admin-message")]
    public async Task<IActionResult> SendSupportMessageToAdmin([FromBody] SupportAdminMessageRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var message = (request?.Message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return BadRequest(new { success = false, detail = "Bạn chưa nhập nội dung cần hỗ trợ." });
        }

        if (message.Length > 1200) message = message[..1200];

        var conversationId = await _chatService.CreateOrGetSupportAdminConversationAsync(current.userId!);
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return NotFound(new { success = false, detail = "Chưa tìm thấy tài khoản Admin chính để nhận hỗ trợ." });
        }

        var senderName = current.authUser?.GetValueOrDefault("username")?.ToString()
            ?? current.authUser?.GetValueOrDefault("displayName")?.ToString()
            ?? current.authUser?.GetValueOrDefault("email")?.ToString()
            ?? "Người dùng";

        var supportMessage = $"[Hỗ trợ Admin chính] {senderName}: {message}";
        var messageId = await _chatService.SendMessageAsync(conversationId, current.userId!, supportMessage);
        if (messageId is null)
        {
            return StatusCode(500, new { success = false, detail = "Không thể gửi tin nhắn hỗ trợ." });
        }

        return Ok(new
        {
            success = true,
            conversation_id = conversationId,
            message = "Đã gửi tin nhắn cho Admin."
        });
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var users = await _chatService.GetAllUsersExceptAsync(current.userId!);
        return Ok(new { success = true, data = users, message = "Danh sách người dùng để tìm kiếm" });
    }
}

public sealed class SupportAdminMessageRequest
{
    public string? Message { get; set; }
}

public sealed class AiSchedulePlanRequest
{
    public string? Message { get; set; }
    public List<AiHistoryMessage>? History { get; set; }
    public JsonElement CurrentSchedule { get; set; }
}

public sealed class AiChatRequest
{
    public string? Message { get; set; }
    public List<AiHistoryMessage>? History { get; set; }
    public string? Assistant { get; set; }
    public string? Context { get; set; }
}

public sealed class AiHistoryMessage
{
    public string? Role { get; set; }
    public string? Content { get; set; }
}
