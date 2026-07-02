using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
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
    private readonly IChatService _chatService;
    private readonly IFriendService _friendService;
    private readonly IFileStorageService _fileStorage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly TravelGuideRagService _travelGuideRagService;

    public ChatController(
        IAuthService authService,
        IChatService chatService,
        IFriendService friendService,
        IFileStorageService fileStorage,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        TravelGuideRagService travelGuideRagService) : base(authService)
    {
        _chatService = chatService;
        _friendService = friendService;
        _fileStorage = fileStorage;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _travelGuideRagService = travelGuideRagService;
    }

    [HttpPost("ai/chat")]
    public async Task<IActionResult> AskAi([FromBody] AiChatRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { success = false, detail = "Bạn chưa nhập nội dung để hỏi AI." });
        }

        var assistantMode = NormalizeAiAssistantMode(request.Assistant);

        if (assistantMode == "travelwai")
        {
            var quickReply = TryBuildManagerQuickReply(request.Message);
            if (!string.IsNullOrWhiteSpace(quickReply))
            {
                return Ok(new { success = true, data = new { reply = quickReply }, message = "Quản lý TravelwAI đã xử lý nội bộ" });
            }
        }

        var current = await CurrentUserAsync();
        if (current.ok)
        {
            var quotaError = TryConsumeAiChatQuota(current.userId!, current.authUser);
            if (quotaError is not null) return quotaError;
        }
        else if (assistantMode == "travelwai")
        {
            return Ok(new
            {
                success = true,
                data = new { reply = "Bạn vui lòng đăng ký hoặc đăng nhập để Quản lý TravelwAI hỗ trợ đầy đủ các chức năng tài khoản, lịch trình, tour và tin nhắn." },
                message = "Cần đăng ký"
            });
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

        OpenRouterChatResult aiResponse;
        try
        {
            aiResponse = await SendOpenRouterChatAsync(
                GetOpenRouterModelCandidates(assistantMode, model),
                messages,
                assistantMode == "guide-rag" ? 0.45 : 0.35,
                assistantMode == "guide-rag" ? 520 : 420,
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
        return Ok(new { success = true, data = new { reply = cleaned }, message = "AI đã trả lời" });
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

        foreach (var candidateModel in models.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = candidateModel.Trim(),
                ["messages"] = messages,
                ["temperature"] = temperature,
                ["max_tokens"] = maxTokens
            };

            if (ShouldSendOpenRouterReasoningOptions())
            {
                payload["reasoning"] = BuildOpenRouterMinimalReasoningOptions();
            }

            using var http = _httpClientFactory.CreateClient();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildOpenRouterChatCompletionsUri());
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            httpRequest.Headers.TryAddWithoutValidation("HTTP-Referer", siteUrl);
            httpRequest.Headers.TryAddWithoutValidation("X-Title", appName);
            httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(httpRequest, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = BuildOpenRouterErrorMessage((int)response.StatusCode, responseText);
                errors.Add($"{candidateModel}: {detail}");
                if ((int)response.StatusCode == 401 || (int)response.StatusCode == 402 || (int)response.StatusCode == 403)
                {
                    throw new OpenRouterChatException((int)response.StatusCode, detail, responseText);
                }
                continue;
            }

            JsonNode? json;
            try
            {
                json = JsonNode.Parse(responseText);
            }
            catch (JsonException)
            {
                errors.Add($"{candidateModel}: AI trả về dữ liệu không hợp lệ.");
                continue;
            }

            var answer = json?["choices"]?[0]?["message"]?["content"]?.ToString();
            var finishReason = json?["choices"]?[0]?["finish_reason"]?.ToString();
            if (string.IsNullOrWhiteSpace(answer))
            {
                errors.Add($"{candidateModel}: AI chưa trả về nội dung hợp lệ.");
                continue;
            }

            return new OpenRouterChatResult
            {
                Content = answer,
                FinishReason = finishReason,
                Model = candidateModel.Trim()
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
        AddMany("openrouter/auto");

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IActionResult? TryConsumeAiChatQuota(string userId, Dictionary<string, object?>? authUser)
    {
        var limit = GetAiChatLimitPerFiveMinutes(authUser);
        if (limit <= 0) return null;

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
                var role = NormalizeAccountRole(authUser?.GetValueOrDefault("role"));
                var isFree = string.Equals(role, "Free", StringComparison.OrdinalIgnoreCase);
                return StatusCode(429, new
                {
                    success = false,
                    code = isFree ? "free_ai_quota_exceeded" : "ai_quota_exceeded",
                    detail = isFree
                        ? "Tài khoản Free đã dùng hết 3 câu hỏi trong 5 phút. Vui lòng nâng cấp gói."
                        : $"Tài khoản {role} chỉ được hỏi chatbot AI {limit} câu trong 5 phút. Vui lòng thử lại sau.",
                    message = isFree
                        ? "Tài khoản Free đã dùng hết 3 câu hỏi trong 5 phút."
                        : $"Tài khoản {role} chỉ được hỏi chatbot AI {limit} câu trong 5 phút."
                });
            }

            window.Count++;
        }

        return null;
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
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = 0.35,
            ["max_tokens"] = 2600
        };
        if (ShouldSendOpenRouterReasoningOptions())
        {
            payload["reasoning"] = BuildOpenRouterMinimalReasoningOptions();
        }

        using var http = _httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildOpenRouterChatCompletionsUri());
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.TryAddWithoutValidation("HTTP-Referer", siteUrl);
        httpRequest.Headers.TryAddWithoutValidation("X-Title", appName);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(httpRequest);
        var responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var friendlyDetail = BuildOpenRouterErrorMessage((int)response.StatusCode, responseText);
            return StatusCode((int)response.StatusCode, new { success = false, detail = friendlyDetail, raw = responseText });
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(responseText);
        }
        catch (JsonException)
        {
            return StatusCode(502, new { success = false, detail = "AI trả về dữ liệu lập lịch trình không hợp lệ.", raw = responseText });
        }

        var answer = json?["choices"]?[0]?["message"]?["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(answer))
        {
            return StatusCode(502, new { success = false, detail = "AI chưa trả về nội dung lập lịch trình hợp lệ.", raw = responseText });
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

    private sealed record TravelGuideRagChunk(string Title, string Source, int Reliability, string Content, string Keywords);

    private static string NormalizeAiAssistantMode(string? assistant)
    {
        var value = NormalizeVietnameseForSearch(assistant ?? string.Empty);
        return value is "guide" or "rag" or "guide rag" or "guide-rag" or "travel-guide" or "travel-guide-rag" or "travel guide" or "travel guide rag" or "huong dan vien" or "travelwinne" ? "guide-rag" : "travelwai";
    }

    private static string BuildTravelGuideSystemPrompt()
    {
        return "Bạn là Hướng dẫn viên RAG AI của TravelwAI. Trả lời bằng tiếng Việt tự nhiên, đúng vai hướng dẫn viên du lịch. Dựa ưu tiên vào RAG_CONTEXT được cung cấp. Trả lời tối đa khoảng 150 chữ, không markdown, không bảng, không gạch đầu dòng, không emoji, không dùng các ký tự trang trí như |, >, #, *, ---. Khi dùng dữ liệu truy xuất, nêu nguồn ngắn gọn trong câu trả lời nếu có URL hoặc tên nguồn. Không bịa nguồn pháp lý, danh hiệu, quyết định, giá vé, giờ mở cửa. Nếu thiếu dữ liệu chắc chắn, nói chưa đủ nguồn để khẳng định. Với truyền thuyết/lời kể, ghi rõ là truyền thuyết hoặc lời kể dân gian. Nếu câu trả lời bị giới hạn độ dài, phải kết thúc ở một câu hoàn chỉnh.";
    }

    private static string BuildTravelGuideRagContext(string? message, string? uiContext)
    {
        var query = NormalizeVietnameseForSearch((message ?? string.Empty) + " " + (uiContext ?? string.Empty));
        var ranked = GetTravelGuideRagChunks()
            .Select(chunk => new { Chunk = chunk, Score = ScoreTravelGuideChunk(query, chunk) })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Chunk.Reliability)
            .Take(8)
            .Select(item => item.Chunk)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("RAG_CONTEXT TravelwAI:");
        builder.AppendLine("Thứ tự ưu tiên nguồn: văn bản pháp luật/quyết định chính thức; Cục Di sản, Bộ VHTTDL, UNESCO; UBND/Sở địa phương; bảo tàng/ban quản lý di tích; sách địa chí/nghiên cứu; báo chí chính thống; tư liệu thực địa; blog/mạng xã hội chỉ tham khảo.");

        var cleanUi = BuildAiContextBlock(uiContext);
        if (!string.IsNullOrWhiteSpace(cleanUi)) builder.AppendLine(cleanUi);

        foreach (var chunk in ranked)
        {
            builder.AppendLine("---");
            builder.AppendLine("Nguồn: " + chunk.Source);
            builder.AppendLine("Độ tin cậy: " + chunk.Reliability);
            builder.AppendLine(chunk.Title + ": " + chunk.Content);
        }

        var text = builder.ToString().Trim();
        return text.Length > 10000 ? text[..10000] : text;
    }

    private static int ScoreTravelGuideChunk(string query, TravelGuideRagChunk chunk)
    {
        if (string.IsNullOrWhiteSpace(query)) return chunk.Reliability / 20;
        var haystack = NormalizeVietnameseForSearch(chunk.Title + " " + chunk.Source + " " + chunk.Content + " " + chunk.Keywords);
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var score = chunk.Reliability / 10;
        foreach (var token in tokens)
        {
            if (haystack.Contains(token, StringComparison.Ordinal)) score += 8;
        }

        foreach (var phrase in new[] { "di tich", "lang nghe", "nghe nhan", "unesco", "le hoi", "lich trinh", "gia ve", "gio mo cua", "xep hang", "quyet dinh", "truyen thuyet", "tam linh", "kien truc" })
        {
            if (query.Contains(phrase, StringComparison.Ordinal) && haystack.Contains(phrase, StringComparison.Ordinal)) score += 22;
        }

        return score;
    }

    private static IReadOnlyList<TravelGuideRagChunk> GetTravelGuideRagChunks() => new[]
    {
        new TravelGuideRagChunk("Di tích và xếp hạng", "Cục Di sản Văn hoá, Bộ Văn hoá Thể thao và Du lịch, quyết định xếp hạng", 98, "Dùng để xác minh tên di tích, loại hình, niên đại trong hồ sơ, giá trị lịch sử văn hoá, xếp hạng di tích cấp tỉnh, quốc gia, quốc gia đặc biệt. Khi hỏi xếp hạng hoặc quyết định, chỉ khẳng định khi có nguồn chính thức.", "di tich xep hang quoc gia dac biet quyet dinh lich su kien truc gia tri van hoa tam linh"),
        new TravelGuideRagChunk("Di sản UNESCO", "UNESCO World Heritage, UNESCO Intangible Cultural Heritage", 98, "Dùng để xác minh di sản thế giới, di sản văn hoá phi vật thể được ghi danh, năm ghi danh, phạm vi thực hành và giá trị nổi bật. Không tự nhận một địa danh là UNESCO nếu chưa có nguồn UNESCO.", "unesco di san the gioi phi vat the ghi danh van hoa"),
        new TravelGuideRagChunk("Văn bản pháp luật", "Văn bản Chính phủ, Cơ sở dữ liệu quốc gia về pháp luật", 96, "Dùng cho Luật Di sản văn hoá, nghị định về làng nghề, danh hiệu Nghệ nhân nhân dân, Nghệ nhân ưu tú, tiêu chí công nhận nghề truyền thống và làng nghề truyền thống.", "luat di san van hoa nghi dinh lang nghe nghe nhan nhan dan uu tu tieu chi cong nhan"),
        new TravelGuideRagChunk("Nguồn địa phương", "UBND tỉnh/thành, Sở VHTTDL, Sở Du lịch, Sở NN&PTNT, Sở Công Thương", 92, "Dùng để xác minh quyết định công nhận làng nghề, di tích cấp tỉnh, lễ hội địa phương, điểm tham quan, thông báo quản lý và dữ liệu du lịch theo địa phương.", "ubnd so du lich so van hoa so cong thuong so nong nghiep lang nghe le hoi dia phuong"),
        new TravelGuideRagChunk("Bảo tàng và ban quản lý", "Bảo tàng quốc gia, bảo tàng địa phương, ban quản lý di tích", 88, "Dùng cho thuyết minh chính thức, hiện vật, câu chuyện trưng bày, sơ đồ tham quan, quy định tại điểm, bối cảnh lịch sử và giá trị kiến trúc.", "bao tang ban quan ly di tich hien vat thuyet minh kien truc tham quan"),
        new TravelGuideRagChunk("Sách và nghiên cứu", "Địa chí, sách lịch sử địa phương, luận văn, bài nghiên cứu, viện nghiên cứu", 82, "Dùng để mở rộng bối cảnh học thuật, so sánh các giai đoạn lịch sử, phân tích kiến trúc, nguồn gốc làng nghề, biến đổi nghề và giá trị văn hoá cộng đồng.", "dia chi nghien cuu hoc thuat lich su dia phuong kien truc lang nghe so sanh"),
        new TravelGuideRagChunk("Tư liệu thực địa", "Phỏng vấn nghệ nhân, người cao tuổi, ghi âm, video, ảnh hiện trường, bảng thuyết minh", 76, "Dùng cho storytelling, triết lý làm nghề, ký ức cộng đồng, quy trình sản xuất, nguyên liệu, công cụ, giai thoại. Phải phân biệt lời kể với sự kiện đã kiểm chứng.", "phong van nghe nhan cau chuyen ke chuyen quy trinh san xuat nguyen lieu cong cu truyen thuyet"),
        new TravelGuideRagChunk("Báo chí chính thống", "TTXVN, Nhân Dân, VOV, VTV, báo địa phương và tạp chí văn hoá du lịch", 70, "Dùng để cập nhật sự kiện, lễ hội, hoạt động bảo tồn, phỏng vấn, điểm mới trong du lịch. Không dùng làm căn cứ pháp lý cao nhất nếu có quyết định hoặc hồ sơ chính thức.", "bao chi su kien moi le hoi bao ton phong van du lich"),
        new TravelGuideRagChunk("Thông tin tham quan", "Cổng du lịch quốc gia, cổng du lịch địa phương, website hoặc Facebook chính thức của bảo tàng/ban quản lý", 68, "Dùng cho giờ mở cửa, giá vé, sự kiện theo mùa, tuyến tham quan, lưu ý khi đi. Đây là dữ liệu dễ thay đổi nên cần nói rõ nếu chưa có cập nhật mới.", "gio mo cua gia ve su kien lich trinh tuyen tham quan gan do"),
        new TravelGuideRagChunk("Nguồn phụ", "Wikipedia, Wikivoyage, blog, mạng xã hội, đánh giá người dùng", 38, "Chỉ dùng để định hướng ban đầu, tham khảo trải nghiệm và phát hiện chủ đề. Không dùng để khẳng định niên đại, danh hiệu, xếp hạng, quyết định công nhận hoặc sự kiện pháp lý.", "wikipedia wikivoyage blog mang xa hoi tham khao trai nghiem")
    };

    private static string? TryBuildManagerQuickReply(string message)
    {
        var normalized = NormalizeVietnameseForSearch(message);
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        static bool Match(string value, string pattern) => Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase);
        static string SyntaxReply() => "Dùng cú pháp: tới trang [tên trang], qua trang [tên trang] hoặc chi tiết trang [tên trang]. Ví dụ: tới trang Tour du lịch.";

        var pages = new (string Name, string Url, string[] Aliases, string Detail)[]
        {
            ("Đăng nhập", "/login", new[] { "dang nhap", "login" }, "Đăng nhập vào tài khoản TravelwAI."),
            ("Đăng ký", "/signup", new[] { "dang ky", "tao tai khoan", "register", "signup", "sign up" }, "Tạo tài khoản mới và nhập mã mời nếu có."),
            ("Quên mật khẩu", "/forgot-password", new[] { "quen mat khau", "lay lai mat khau", "khoi phuc mat khau", "forgot password" }, "Khôi phục mật khẩu bằng email."),
            ("Trang chủ", "/home", new[] { "trang chu", "home" }, "Trang chính sau khi đăng nhập."),
            ("Giới thiệu", "/landing", new[] { "gioi thieu", "landing", "trang gioi thieu" }, "Xem tổng quan TravelwAI."),
            ("Bản đồ Việt Nam", "/provinces", new[] { "ban do viet nam", "ban do", "tinh thanh", "34 tinh", "viet nam", "provinces" }, "Xem bản đồ 34 tỉnh thành và thông tin nổi bật."),
            ("Chi tiết tỉnh", "/detail", new[] { "chi tiet tinh", "chi tiet dia phuong", "detail" }, "Xem mô tả, khu vực, điểm đến và gợi ý tham quan."),
            ("Lịch trình", "/schedule", new[] { "lich trinh", "lap lich trinh", "tao lich trinh", "schedule" }, "Tạo và quản lý lịch trình du lịch."),
            ("Kế hoạch", "/plans", new[] { "ke hoach", "lap ke hoach", "tao ke hoach", "plans" }, "Tạo nhóm kế hoạch và mời người đi chung."),
            ("Nhắn tin", "/messaging", new[] { "nhan tin", "tin nhan", "chat", "messaging" }, "Nhắn tin với bạn bè, nhóm kế hoạch và Admin."),
            ("Phản hồi", "/contact", new[] { "phan hoi", "lien he", "gop y", "ho tro", "contact" }, "Gửi góp ý, yêu cầu hỗ trợ hoặc nhắn với Admin."),
            ("Thông báo", "/notifications", new[] { "thong bao", "notification", "notifications" }, "Xem lời mời, tin nhắn, kế hoạch và cập nhật hệ thống."),
            ("Bài viết", "/posts", new[] { "bai viet", "tin du lich", "kham pha bai", "posts" }, "Xem và quản lý bài viết du lịch."),
            ("Tour du lịch", "/tours", new[] { "tour du lich", "tour", "dat tour", "xem tour", "tours" }, "Xem tour, đặt tour và theo dõi ưu đãi."),
            ("Sales", "/tour-sales", new[] { "sales", "trang sales", "ban tour", "don ban tour", "tour sales", "tour-sales" }, "Quản lý tour, đơn bán tour, hoa hồng và doanh thu."),
            ("Business", "/business", new[] { "business", "trang business" }, "Quản lý tour, đơn bán tour, doanh thu và phí dịch vụ."),
            ("Admin", "/admin", new[] { "admin", "quan tri", "quan ly he thong", "trang admin" }, "Quản lý tài khoản, quyền, tour, bài viết và dữ liệu hệ thống."),
            ("Hồ sơ", "/profile", new[] { "ho so", "tai khoan", "thong tin ca nhan", "doi ten", "profile" }, "Xem hồ sơ, đổi ảnh, đổi tên và đổi mật khẩu.")
        };

        string NormalizeLocal(string value) => NormalizeVietnameseForSearch(value);
        (string Name, string Url, string[] Aliases, string Detail)? FindPage(string value)
        {
            var key = NormalizeLocal(value);
            if (string.IsNullOrWhiteSpace(key)) return null;
            foreach (var page in pages)
            {
                if (NormalizeLocal(page.Name) == key || page.Aliases.Any(alias => NormalizeLocal(alias) == key)) return page;
            }
            foreach (var page in pages)
            {
                var pageName = NormalizeLocal(page.Name);
                if (key.Contains(pageName) || pageName.Contains(key)) return page;
                if (page.Aliases.Any(alias => key.Contains(NormalizeLocal(alias)) || NormalizeLocal(alias).Contains(key))) return page;
            }
            return null;
        }

        string PageListText() => string.Join(", ", pages.Select(page => page.Name));
        string DetailReply((string Name, string Url, string[] Aliases, string Detail) page) => page.Detail + " Muốn mở trang này, nhắn: tới trang " + page.Name + ".";

        if (Match(normalized, @"\b(dang\s*xuat|thoat\s*tai\s*khoan|log\s*out)\b")) return "Đang đăng xuất tài khoản.";
        if (Match(normalized, @"\b(doi\s*mat\s*khau|doi\s*password|change\s*password)\b")) return "Đang mở Hồ sơ để đổi mật khẩu.";
        if (Match(normalized, @"(co\s*)?trang\s*nao|danh\s*sach\s*trang|menu|cac\s*trang|nhung\s*trang|xem\s*trang")) return "Các trang TravelwAI: " + PageListText() + ". " + SyntaxReply();

        var navigateMatch = Regex.Match(normalized, @"^(?:toi|toi\s*toi|qua|di\s*toi|chuyen\s*toi)\s+trang\s+(.+)$", RegexOptions.IgnoreCase);
        if (navigateMatch.Success)
        {
            var page = FindPage(navigateMatch.Groups[1].Value);
            return page.HasValue ? "Đang mở trang " + page.Value.Name + "." : "Chưa tìm thấy trang đó. " + SyntaxReply();
        }

        var detailMatch = Regex.Match(normalized, @"^chi\s*tiet\s+trang\s+(.+)$", RegexOptions.IgnoreCase);
        if (detailMatch.Success)
        {
            var page = FindPage(detailMatch.Groups[1].Value);
            return page.HasValue ? DetailReply(page.Value) : "Chưa tìm thấy trang đó. " + SyntaxReply();
        }

        var directPage = FindPage(normalized);
        if (directPage.HasValue && (NormalizeLocal(directPage.Value.Name) == normalized || directPage.Value.Aliases.Any(alias => NormalizeLocal(alias) == normalized))) return DetailReply(directPage.Value);
        if (normalized.Contains("trang") || pages.Any(page => normalized.Contains(NormalizeLocal(page.Name)) || page.Aliases.Any(alias => normalized.Contains(NormalizeLocal(alias))))) return SyntaxReply();
        return null;
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
