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
    private readonly HeritageKnowledgeService _heritageKnowledge;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public ChatController(
        IAuthService authService,
        IChatService chatService,
        IFriendService friendService,
        IFileStorageService fileStorage,
        HeritageKnowledgeService heritageKnowledge,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration) : base(authService)
    {
        _chatService = chatService;
        _friendService = friendService;
        _fileStorage = fileStorage;
        _heritageKnowledge = heritageKnowledge;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    [HttpPost("ai/chat")]
    public async Task<IActionResult> AskAi([FromBody] AiChatRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { success = false, detail = "Bạn chưa nhập nội dung để hỏi AI." });
        }

        var assistantKey = NormalizeAiAssistantKey(request.Assistant);
        var quickReply = assistantKey == "travelwai" ? TryBuildManagerQuickReply(request.Message) : null;
        if (!string.IsNullOrWhiteSpace(quickReply))
        {
            return Ok(new { success = true, data = new { reply = quickReply }, message = "Quản lý TravelwAI đã xử lý nội bộ" });
        }

        var current = await CurrentUserAsync();
        if (current.ok)
        {
            var quotaError = TryConsumeAiChatQuota(current.userId!, current.authUser);
            if (quotaError is not null) return quotaError;
        }
        else
        {
            var loginReply = assistantKey == "guide"
                ? "Bạn vui lòng đăng ký hoặc đăng nhập để Hướng dẫn viên AI hỗ trợ đầy đủ hơn."
                : "Bạn vui lòng đăng ký hoặc đăng nhập để Quản lý TravelwAI hỗ trợ đầy đủ các chức năng tài khoản, lịch trình, tour và tin nhắn.";
            return Ok(new
            {
                success = true,
                data = new { reply = loginReply },
                message = "Cần đăng ký"
            });
        }

        HeritageRetrievalResult? heritageRetrieval = null;
        if (assistantKey == "guide")
        {
            var retrievalQuery = BuildGuideRetrievalQuery(request.Message, request.History);
            heritageRetrieval = await _heritageKnowledge.RetrieveAsync(retrievalQuery, null);
            if (!heritageRetrieval.HasMatches || heritageRetrieval.Chunks.Count == 0)
            {
                return Ok(new
                {
                    success = true,
                    data = new
                    {
                        reply = BuildNoApprovedSourceReply(heritageRetrieval.Intent),
                        sources = Array.Empty<object>()
                    },
                    message = "Không có nguồn đã duyệt phù hợp"
                });
            }
        }

        var apiKey = GetOpenRouterApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return StatusCode(500, new { success = false, detail = "Chưa cấu hình API key AI. Vui lòng kiểm tra cấu hình trên Render." });
        }

        var model = GetOpenRouterConfigValue("Model", "OPENROUTER_MODEL", "openrouter/free");
        var siteUrl = GetOpenRouterConfigValue("SiteUrl", "OPENROUTER_SITE_URL", "https://travelwai.onrender.com");
        var appName = GetOpenRouterConfigValue("AppName", "OPENROUTER_APP_NAME", "TravelwAI");
        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = BuildAiChatSystemPrompt(assistantKey)
            }
        };

        if (assistantKey == "guide")
        {
            messages.Add(new { role = "system", content = _heritageKnowledge.BuildContextBlock(heritageRetrieval!) });
            messages.Add(new { role = "system", content = BuildGuideConversationContext(request.History) });
        }
        else
        {
            var contextBlock = BuildAiContextBlock(request.Context);
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
        }

        messages.Add(new { role = "user", content = request.Message.Trim() });

        var payload = new
        {
            model,
            messages,
            temperature = assistantKey == "guide" ? 0.1 : 0.35,
            max_tokens = 1200,
            reasoning = BuildOpenRouterMinimalReasoningOptions()
        };

        using var http = _httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildOpenRouterChatCompletionsUri());
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.TryAddWithoutValidation("HTTP-Referer", siteUrl);
        httpRequest.Headers.TryAddWithoutValidation("X-Title", appName);
        httpRequest.Headers.TryAddWithoutValidation("X-OpenRouter-Title", appName);
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
            return StatusCode(502, new { success = false, detail = "AI trả về dữ liệu không hợp lệ.", raw = responseText });
        }

        var answer = json?["choices"]?[0]?["message"]?["content"]?.ToString();
        var finishReason = json?["choices"]?[0]?["finish_reason"]?.ToString();
        if (string.IsNullOrWhiteSpace(answer))
        {
            return StatusCode(502, new { success = false, detail = "AI chưa trả về nội dung hợp lệ.", raw = responseText });
        }

        var maxWords = assistantKey == "guide" ? 260 : 150;
        var cleaned = CleanSimpleChatbotReply(answer, maxWords, IsOpenRouterAnswerCutOff(finishReason));
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Mình chưa nhận được câu trả lời hoàn chỉnh từ AI. Bạn hỏi lại giúp mình nhé.";
        var sources = heritageRetrieval?.Chunks.Select(chunk => new
        {
            title = chunk.SourceTitle,
            source = chunk.SourceName,
            type = chunk.SourceType,
            url = chunk.Url,
            reliability_score = chunk.ReliabilityScore
        }).ToList();
        return Ok(new { success = true, data = new { reply = cleaned, sources }, message = "AI đã trả lời" });
    }

    private static string NormalizeAiAssistantKey(string? assistant)
    {
        var key = NormalizeVietnameseForSearch(assistant ?? string.Empty);
        if (key.Contains("guide") || key.Contains("huong dan") || key.Contains("tourguide") || key.Contains("du lich") || key.Contains("heritage")) return "guide";
        return "travelwai";
    }

    private static string BuildGuideRetrievalQuery(string? message, List<AiHistoryMessage>? history)
    {
        var parts = new List<string>();
        if (history is not null)
        {
            parts.AddRange(history
                .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.Content))
                .TakeLast(4)
                .Select(item => item.Content!.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(message)) parts.Add(message.Trim());
        var query = string.Join(" ", parts);
        query = Regex.Replace(query, @"\s+", " ").Trim();
        return query.Length > 1400 ? query[^1400..] : query;
    }

    private static string BuildGuideConversationContext(List<AiHistoryMessage>? history)
    {
        if (history is null || history.Count == 0) return "NGỮ CẢNH HỘI THOẠI: Không có. Chỉ dùng kho tri thức đã truy xuất làm nguồn.";
        var userTurns = history
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.Content))
            .TakeLast(4)
            .Select(item => Regex.Replace(item.Content!.Trim(), @"\s+", " "))
            .Where(item => item.Length > 0)
            .ToList();
        if (userTurns.Count == 0) return "NGỮ CẢNH HỘI THOẠI: Không có. Chỉ dùng kho tri thức đã truy xuất làm nguồn.";
        var context = string.Join(" | ", userTurns);
        if (context.Length > 900) context = context[^900..];
        return "NGỮ CẢNH HỘI THOẠI CHỈ ĐỂ HIỂU CÂU HỎI, KHÔNG PHẢI NGUỒN THÔNG TIN: " + context;
    }

    private static string BuildNoApprovedSourceReply(string intent)
    {
        return intent switch
        {
            "legal_verification" => "Mình chưa có nguồn đã duyệt phù hợp trong kho tri thức để xác minh thông tin pháp lý này.",
            "current_visit_info" => "Mình chưa có nguồn đã duyệt phù hợp trong kho tri thức để xác nhận thông tin cập nhật này.",
            "itinerary" => "Mình chưa có đủ nguồn đã duyệt trong kho tri thức để gợi ý lịch trình chắc chắn cho câu hỏi này.",
            _ => "Mình chưa có nguồn đã duyệt phù hợp trong kho tri thức để trả lời chắc chắn câu hỏi này."
        };
    }

    private static string BuildAiChatSystemPrompt(string assistantKey)
    {
        var today = GetVietnamToday().ToString("yyyy-MM-dd");
        if (assistantKey == "guide")
        {
            return "Bạn là Hướng dẫn viên AI của TravelwAI. Ngày hiện tại tại Việt Nam: " + today + ". Trả lời bằng tiếng Việt tự nhiên, đúng trọng tâm, không markdown phức tạp, không emoji. OpenRouter/model chỉ được dùng để diễn đạt câu chữ, tuyệt đối không được dùng làm nguồn thông tin. Chỉ được sử dụng các đoạn trong KHO TRI THỨC NỘI BỘ ĐÃ TRUY XUẤT. Không dùng kiến thức nền của model, không dùng dữ liệu frontend, không dùng Wikipedia, không suy đoán để lấp chỗ trống. Nếu kho nguồn không có chi tiết được hỏi, phải nói chưa có nguồn đã duyệt phù hợp. Phân biệt sự kiện đã kiểm chứng với truyền thuyết/lời kể dân gian nếu nguồn có nêu. Không bịa số quyết định, danh hiệu, giá vé, giờ mở cửa, niên đại hoặc sự kiện hiện tại.";
        }

        return "Bạn là Quản lý TravelwAI, trợ lý hỗ trợ người dùng sử dụng website TravelwAI. Ngày hiện tại tại Việt Nam: " + today + ". Không dùng markdown, không gạch đầu dòng, không emoji. Trả lời ngắn gọn bằng tiếng Việt. Hỗ trợ điều hướng trang, lịch trình, kế hoạch, nhắn tin, tour du lịch, hồ sơ, thông báo, phản hồi và tài khoản. Khi người dùng muốn mở trang, hướng dẫn dùng cú pháp: tới trang [tên trang]. Khi không chắc, hỏi lại một câu ngắn.";
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

        var model = GetOpenRouterConfigValue("Model", "OPENROUTER_MODEL", "openrouter/free");
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
        var payload = new
        {
            model,
            messages,
            temperature = 0.35,
            max_tokens = 2600,
            reasoning = BuildOpenRouterMinimalReasoningOptions()
        };

        using var http = _httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildOpenRouterChatCompletionsUri());
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.TryAddWithoutValidation("HTTP-Referer", siteUrl);
        httpRequest.Headers.TryAddWithoutValidation("X-Title", appName);
        httpRequest.Headers.TryAddWithoutValidation("X-OpenRouter-Title", appName);
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

        if (Match(normalized, @"(dang\s*xuat|thoat\s*tai\s*khoan|log\s*out)")) return "Đang đăng xuất tài khoản.";
        if (Match(normalized, @"(doi\s*mat\s*khau|doi\s*password|change\s*password)")) return "Đang mở Hồ sơ để đổi mật khẩu.";
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
        var clean = Regex.Replace(text.Trim(), @"\s+", " ");
        clean = clean.Replace("```", string.Empty).Trim();
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > maxWords)
        {
            clean = string.Join(' ', words.Take(maxWords));
            dropUnfinishedTail = true;
        }
        if (dropUnfinishedTail)
        {
            var end = Math.Max(clean.LastIndexOf('.'), Math.Max(clean.LastIndexOf('!'), clean.LastIndexOf('?')));
            if (end > 40) clean = clean[..(end + 1)].Trim();
        }
        return clean;
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
        if (statusCode == 401) return "AI chưa được cấu hình đúng. Vui lòng kiểm tra cấu hình API key.";
        if (statusCode == 429 || message.Contains("rate", StringComparison.OrdinalIgnoreCase) || message.Contains("limit", StringComparison.OrdinalIgnoreCase)) return "Hiện chưa trả lời được. Bạn thử lại sau.";
        if (statusCode == 404 || message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || message.Contains("not found", StringComparison.OrdinalIgnoreCase)) return "Model AI hiện không khả dụng. Vui lòng kiểm tra lại cấu hình model.";
        return "Hiện chưa trả lời được. Bạn thử lại sau.";
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
