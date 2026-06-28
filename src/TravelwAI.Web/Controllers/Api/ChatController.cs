using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
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

    private const string ProvinceSearchEventsCollection = "province_search_events";
    private static readonly ConcurrentDictionary<string, AiChatQuotaWindow> AiChatQuota = new(StringComparer.Ordinal);
    private readonly IChatService _chatService;
    private readonly IFriendService _friendService;
    private readonly IFileStorageService _fileStorage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IDataRepository _repo;

    public ChatController(
        IAuthService authService,
        IChatService chatService,
        IFriendService friendService,
        IFileStorageService fileStorage,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IDataRepository repo) : base(authService)
    {
        _chatService = chatService;
        _friendService = friendService;
        _fileStorage = fileStorage;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _repo = repo;
    }

    [HttpPost("ai/chat")]
    public async Task<IActionResult> AskAi([FromBody] AiChatRequest request)
    {
        var assistantMode = NormalizeAiAssistantMode(request.Assistant);

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { success = false, detail = "Bạn chưa nhập nội dung để hỏi AI." });
        }

        var current = await CurrentUserAsync();
        var isGuest = !current.ok;
        if (current.ok)
        {
            var quotaError = TryConsumeAiChatQuota(current.userId!, current.authUser);
            if (quotaError is not null) return quotaError;
            if (assistantMode == "guide")
            {
                await TrackProvinceMentionsFromAiAsync(request.Message, current.userId);
            }
        }

        if (assistantMode != "guide")
        {
            var managerQuickReply = TryBuildManagerQuickReply(request.Message);
            if (!string.IsNullOrWhiteSpace(managerQuickReply))
            {
                return Ok(new
                {
                    success = true,
                    data = new { reply = managerQuickReply },
                    message = "Quản lý TravelwAI đã xử lý nội bộ"
                });
            }
        }

        if (isGuest && assistantMode != "guide")
        {
            return Ok(new
            {
                success = true,
                data = new { reply = "Bạn vui lòng đăng ký hoặc đăng nhập để Quản lý TravelwAI hỗ trợ đầy đủ các chức năng tài khoản, lịch trình, tour và tin nhắn." },
                message = "Cần đăng ký"
            });
        }

        var guideQuestionAsksForDate = assistantMode == "guide" && IsGuideDateQuestion(request.Message);
        var guideNeedsWikipedia = assistantMode == "guide" && GuideMessageNeedsWikipedia(request.Message);
        const int aiReplyLimit = 100;
        const int aiMaxTokens = 130;
        using var http = _httpClientFactory.CreateClient();

        if (assistantMode == "guide" && guideNeedsWikipedia)
        {
            var wikipediaReply = CleanSimpleChatbotReply(await BuildWikipediaDirectReplyAsync(http, request.Message, request.Context, guideQuestionAsksForDate), aiReplyLimit);
            if (!string.IsNullOrWhiteSpace(wikipediaReply))
            {
                return Ok(new
                {
                    success = true,
                    data = new { reply = wikipediaReply },
                    message = "Đã trả lời bằng Wikipedia tiếng Việt"
                });
            }

            return Ok(new
            {
                success = true,
                data = new { reply = "Mình chưa tìm thấy thông tin phù hợp trên Wikipedia tiếng Việt. Bạn hãy hỏi lại bằng tên địa danh, tỉnh thành, lễ hội hoặc sự kiện cụ thể hơn." },
                message = "Không tìm thấy Wikipedia phù hợp"
            });
        }

        if (assistantMode == "guide")
        {
            var conversationalReply = TryBuildGuideConversationalReply(request.Message);
            if (!string.IsNullOrWhiteSpace(conversationalReply))
            {
                return Ok(new
                {
                    success = true,
                    data = new { reply = conversationalReply },
                    message = "Hướng dẫn viên đã trả lời"
                });
            }
        }

        var guideWikipediaFallback = string.Empty;

        var apiKey = GetOpenRouterApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (!string.IsNullOrWhiteSpace(guideWikipediaFallback))
            {
                return Ok(new
                {
                    success = true,
                    data = new { reply = guideWikipediaFallback },
                    message = "Đã trả lời bằng Wikipedia tiếng Việt"
                });
            }

            return StatusCode(500, new { success = false, detail = "Chưa cấu hình API key AI. Vui lòng kiểm tra cấu hình trên Render." });
        }

        var model = GetOpenRouterConfigValue("Model", "OPENROUTER_MODEL", "openrouter/free");
        var siteUrl = GetOpenRouterConfigValue("SiteUrl", "OPENROUTER_SITE_URL", "https://travelwai.onrender.com");
        var appName = GetOpenRouterConfigValue("AppName", "OPENROUTER_APP_NAME", "TravelwAI");
        var openRouterEndpoint = BuildOpenRouterChatCompletionsUri();
        var systemPrompt = assistantMode == "guide"
            ? "Bạn là Hướng dẫn viên Travelwinne. Trò chuyện tự nhiên, thân thiện như một hướng dẫn viên du lịch Việt Nam. Chỉ trả lời bằng tiếng Việt đơn giản, không markdown, không gạch đầu dòng, không emoji. Với câu hỏi giao tiếp, hỏi cách dùng, hỏi gợi ý chung hoặc người dùng chưa nêu điểm đến cụ thể, hãy hỏi lại ngắn gọn để hiểu nhu cầu. Tuyệt đối không tự bịa địa danh, số liệu, ngày tháng, lịch sử, văn hoá, lễ hội hoặc ngày lễ. Nếu câu hỏi cần thông tin chính xác thì hệ thống sẽ xử lý bằng Wikipedia trước, còn trong nhánh này chỉ trả lời giao tiếp chung. Trả lời tối đa 100 chữ, ưu tiên 3-5 câu ngắn, không bỏ dở câu."
            : "Bạn là Quản lí TravelwAI, trợ lí điều hướng và hướng dẫn sử dụng toàn bộ website TravelwAI. Chỉ trả lời bằng tiếng Việt đơn giản. Không dùng markdown, không gạch đầu dòng, không emoji, không ký hiệu lạ. Hướng dẫn ngắn gọn người dùng dùng các trang Lịch trình, Kế hoạch, Bản đồ Việt Nam, Nhắn tin, Tour du lịch, Sales, Admin, Hồ sơ, Thông báo và Phản hồi. Khi người dùng muốn mở trang, chỉ nhận cú pháp tới trang [tên trang] hoặc qua trang [tên trang]. Khi người dùng muốn xem hướng dẫn trang, nhận cú pháp chi tiết trang [tên trang] hoặc chỉ ghi đúng tên trang. Nếu người dùng ghi sai cú pháp mở trang, hãy hướng dẫn ghi đúng cú pháp thật ngắn. Với đổi mật khẩu hoặc đăng xuất, hãy xác nhận thao tác thật ngắn và giao diện sẽ tự chuyển trang nếu nhận diện được. Trả lời tối đa 100 chữ, ưu tiên 3-5 câu ngắn, không bỏ dở câu. Khi nói khoảng ngày, viết dạng 1 đến 15/01 âm lịch hoặc 5 đến 8/06 dương lịch, không viết 1-15/01 và không viết 1 15/01.";

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        var contextBlock = BuildAiContextBlock(request.Context, assistantMode, assistantMode != "guide" || guideQuestionAsksForDate);

        if (assistantMode == "guide")
        {
            var wikipediaBlock = await BuildWikipediaContextBlockAsync(http, request.Message, request.Context, guideQuestionAsksForDate);
            if (!string.IsNullOrWhiteSpace(wikipediaBlock))
            {
                messages.Add(new { role = "system", content = wikipediaBlock });
            }
            else
            {
                messages.Add(new { role = "system", content = string.IsNullOrWhiteSpace(contextBlock) ? "Không có dữ liệu nền cho câu hỏi hiện tại. Chỉ trả lời giao tiếp chung, không nêu thông tin chính xác nếu không có nguồn." : "Chỉ dùng dữ liệu ứng dụng TravelwAI nếu phù hợp và không tự thêm chi tiết ngoài nguồn." });
            }

            if (!string.IsNullOrWhiteSpace(contextBlock))
            {
                messages.Add(new { role = "system", content = contextBlock });
            }

            messages.Add(new { role = "system", content = "QUY TẮC CHO HƯỚNG DẪN VIÊN TRAVELWINNE: Trong nhánh này chỉ trả lời giao tiếp hoặc gợi ý chung. Không bịa thông tin chính xác về địa danh, tỉnh thành, lễ hội, lịch sử, văn hoá, ngày lễ, số liệu hoặc lịch sự kiện. Nếu người dùng hỏi thông tin chính xác mà chưa có nguồn, hãy nói cần tên cụ thể để tra Wikipedia." });
        }
        else if (!string.IsNullOrWhiteSpace(contextBlock))
        {
            messages.Add(new { role = "system", content = contextBlock });
        }

        if (assistantMode == "guide" && !guideQuestionAsksForDate)
        {
            messages.Add(new { role = "system", content = "Câu hỏi hiện tại không hỏi thời gian. Khi trả lời, không nêu ngày/tháng, không nêu âm lịch/dương lịch, không nêu khoảng ngày của lễ hội; chỉ nói nguồn gốc, ý nghĩa, hoạt động và nét văn hoá." });
        }

        if (request.History is not null)
        {
            foreach (var item in request.History
                         .Where(item => !string.IsNullOrWhiteSpace(item.Content))
                         .TakeLast(12))
            {
                var role = string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? "assistant"
                    : "user";
                messages.Add(new { role, content = item.Content!.Trim() });
            }
        }

        messages.Add(new { role = "user", content = request.Message.Trim() });

        var fullAnswer = new StringBuilder();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var payload = new
            {
                model,
                messages,
                temperature = assistantMode == "guide" ? 0.0 : 0.35,
                max_tokens = aiMaxTokens
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, openRouterEndpoint);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            httpRequest.Headers.TryAddWithoutValidation("HTTP-Referer", siteUrl);
            httpRequest.Headers.TryAddWithoutValidation("X-Title", appName);
            httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(httpRequest);
            var responseText = await response.Content.ReadAsStringAsync();

            LogOpenRouterRawResponse("AI CHAT", response, responseText);

            if (!response.IsSuccessStatusCode)
            {
                if (!string.IsNullOrWhiteSpace(guideWikipediaFallback))
                {
                    return Ok(new
                    {
                        success = true,
                        data = new { reply = guideWikipediaFallback },
                        message = "Đã trả lời bằng Wikipedia tiếng Việt"
                    });
                }

                var friendlyDetail = BuildOpenRouterErrorMessage((int)response.StatusCode, responseText);
                return StatusCode((int)response.StatusCode, new { success = false, detail = friendlyDetail, raw = responseText });
            }

            JsonNode? json;
            try
            {
                json = JsonNode.Parse(responseText);
            }
            catch (JsonException ex)
            {
                Console.WriteLine("===== OPENROUTER AI CHAT JSON PARSE ERROR =====");
                Console.WriteLine(ex.Message);
                if (!string.IsNullOrWhiteSpace(guideWikipediaFallback))
                {
                    return Ok(new
                    {
                        success = true,
                        data = new { reply = guideWikipediaFallback },
                        message = "Đã trả lời bằng Wikipedia tiếng Việt"
                    });
                }
                return StatusCode(502, new { success = false, detail = "AI trả về dữ liệu không hợp lệ.", raw = responseText });
            }

            var answerPart = json?["choices"]?[0]?["message"]?["content"]?.ToString();
            var finishReason = json?["choices"]?[0]?["finish_reason"]?.ToString();

            if (string.IsNullOrWhiteSpace(answerPart))
            {
                if (!string.IsNullOrWhiteSpace(guideWikipediaFallback))
                {
                    return Ok(new
                    {
                        success = true,
                        data = new { reply = guideWikipediaFallback },
                        message = "Đã trả lời bằng Wikipedia tiếng Việt"
                    });
                }
                return StatusCode(502, new { success = false, detail = "AI chưa trả về nội dung hợp lệ.", raw = responseText });
            }

            if (fullAnswer.Length > 0)
            {
                fullAnswer.Append(' ');
            }

            fullAnswer.Append(answerPart.Trim());

            if (!IsOpenRouterAnswerCutOff(finishReason))
            {
                break;
            }

            messages.Add(new { role = "assistant", content = answerPart.Trim() });
            messages.Add(new { role = "user", content = "Câu trả lời bị cắt. Hãy viết tiếp ngay phần còn thiếu, không lặp lại phần đã viết, kết thúc bằng câu hoàn chỉnh và vẫn giữ tổng nội dung ngắn gọn." });
        }

        var answer = CleanSimpleChatbotReply(fullAnswer.ToString(), aiReplyLimit);
        if (assistantMode == "guide")
        {
            answer = StripGuideSourceLeadIn(answer);
        }
        if (assistantMode == "guide" && !guideQuestionAsksForDate)
        {
            answer = RemoveGuideDateMentions(answer);
            answer = CleanSimpleChatbotReply(answer, aiReplyLimit);
            answer = StripGuideSourceLeadIn(answer);
        }
        return Ok(new { success = true, data = new { reply = answer }, message = "AI đã trả lời" });
    }

    private async Task TrackProvinceMentionsFromAiAsync(string? message, string? userId)
    {
        var provinceNames = FindProvinceNamesInText(message).Take(5).ToList();
        if (provinceNames.Count == 0) return;

        foreach (var provinceName in provinceNames)
        {
            await TrackProvinceSearchEventAsync(provinceName, userId, "ai-chat");
        }
    }

    private async Task TrackProvinceSearchEventAsync(string provinceName, string? userId, string source)
    {
        var name = (provinceName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await _repo.AddAsync(ProvinceSearchEventsCollection, new Dictionary<string, object?>
            {
                ["province_name"] = name,
                ["provinceName"] = name,
                ["user_id"] = userId ?? string.Empty,
                ["userId"] = userId ?? string.Empty,
                ["source"] = source,
                ["created_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });
        }
        catch
        {
        }
    }

    private static IEnumerable<string> FindProvinceNamesInText(string? text)
    {
        var normalized = NormalizeVietnameseForSearch(text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return Enumerable.Empty<string>();

        normalized = Regex.Replace(normalized, @"[^a-z0-9\s]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        var provinceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var province in PlanCatalog.DefaultProvinceTags())
        {
            var name = province.GetValueOrDefault("name")?.ToString()
                       ?? province.GetValueOrDefault("province_name")?.ToString()
                       ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) continue;

            foreach (var alias in BuildProvinceAliasesForTracking(name))
            {
                var key = NormalizeVietnameseForSearch(alias);
                key = Regex.Replace(key, @"[^a-z0-9\s]", " ");
                key = Regex.Replace(key, @"\s+", " ").Trim();
                if (!string.IsNullOrWhiteSpace(key) && !provinceMap.ContainsKey(key))
                {
                    provinceMap[key] = name;
                }
            }
        }

        var manualAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ha long"] = "Quảng Ninh",
            ["co to"] = "Quảng Ninh",
            ["yen tu"] = "Quảng Ninh",
            ["cat ba"] = "Thành phố Hải Phòng",
            ["do son"] = "Thành phố Hải Phòng",
            ["ho guom"] = "Thành phố Hà Nội",
            ["ho hoan kiem"] = "Thành phố Hà Nội",
            ["hoan kiem"] = "Thành phố Hà Nội",
            ["hoang thanh thang long"] = "Thành phố Hà Nội",
            ["thang long"] = "Thành phố Hà Nội",
            ["van mieu"] = "Thành phố Hà Nội",
            ["quoc tu giam"] = "Thành phố Hà Nội",
            ["co loa"] = "Thành phố Hà Nội",
            ["pho co ha noi"] = "Thành phố Hà Nội",
            ["hoi an"] = "Thành phố Đà Nẵng",
            ["my khe"] = "Thành phố Đà Nẵng",
            ["ba na"] = "Thành phố Đà Nẵng",
            ["nha trang"] = "Khánh Hòa",
            ["cam ranh"] = "Khánh Hòa",
            ["da lat"] = "Lâm Đồng",
            ["binh thuan"] = "Lâm Đồng",
            ["quy nhon"] = "Gia Lai",
            ["pleiku"] = "Gia Lai",
            ["mang den"] = "Quảng Ngãi",
            ["ly son"] = "Quảng Ngãi",
            ["sam son"] = "Thanh Hóa",
            ["puluong"] = "Thanh Hóa",
            ["pu luong"] = "Thanh Hóa",
            ["cua lo"] = "Nghệ An",
            ["phong nha"] = "Quảng Trị",
            ["lang co"] = "Thành phố Huế",
            ["phu quoc"] = "An Giang",
            ["ha tien"] = "An Giang",
            ["nam du"] = "An Giang",
            ["nui sam"] = "An Giang",
            ["can gio"] = "Thành phố Hồ Chí Minh",
            ["vung tau"] = "Thành phố Hồ Chí Minh",
            ["cat tien"] = "Đồng Nai",
            ["ba den"] = "Tây Ninh",
            ["cho noi"] = "Thành phố Cần Thơ",
            ["ben ninh kieu"] = "Thành phố Cần Thơ",
            ["dat mui"] = "Cà Mau",
            ["tram chim"] = "Đồng Tháp",
            ["sa dec"] = "Đồng Tháp",
            ["thac ban gioc"] = "Cao Bằng",
            ["pac bo"] = "Cao Bằng",
            ["sa pa"] = "Lào Cai",
            ["sapa"] = "Lào Cai",
            ["moc chau"] = "Sơn La",
            ["ta xua"] = "Sơn La",
            ["tam coc"] = "Ninh Bình",
            ["trang an"] = "Ninh Bình",
            ["hoa lu"] = "Ninh Bình"
        };

        foreach (var item in manualAliases)
        {
            if (!provinceMap.ContainsKey(item.Key)) provinceMap[item.Key] = item.Value;
        }

        return provinceMap
            .Where(item => Regex.IsMatch(normalized, $@"(^|\s){Regex.Escape(item.Key)}(\s|$)", RegexOptions.IgnoreCase))
            .OrderByDescending(item => item.Key.Length)
            .Select(item => item.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> BuildProvinceAliasesForTracking(string name)
    {
        yield return name;
        var normalized = NormalizeVietnameseForSearch(name);
        if (normalized.StartsWith("thanh pho ", StringComparison.OrdinalIgnoreCase))
        {
            yield return name.Replace("Thành phố ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            yield return name.Replace("Thành phố", "TP", StringComparison.OrdinalIgnoreCase).Trim();
            yield return name.Replace("Thành phố", "TP.", StringComparison.OrdinalIgnoreCase).Trim();
        }
        if (normalized.StartsWith("tinh ", StringComparison.OrdinalIgnoreCase))
        {
            yield return name.Replace("Tỉnh ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        }
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

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { success = false, detail = "Bạn chưa nhập yêu cầu lập lịch trình cho AI." });
        }

        if (!CanCreateSchedule(current.authUser))
        {
            return StatusCode(403, new { success = false, detail = "Tài khoản Free chưa dùng được lịch trình. Vui lòng nâng cấp VIP hoặc Premium.", message = "Tài khoản Free chưa dùng được lịch trình." });
        }

        var quickEditResult = TryBuildQuickScheduleEditPatch(request);
        if (quickEditResult is not null)
        {
            return Ok(new { success = true, data = quickEditResult, message = "AI đã chỉnh lịch trình" });
        }

        var apiKey = GetOpenRouterApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return StatusCode(500, new { success = false, detail = "Chưa cấu hình API key AI. Vui lòng kiểm tra cấu hình trên Render." });
        }

        var model = GetOpenRouterConfigValue("Model", "OPENROUTER_MODEL", "openrouter/free");
        var siteUrl = GetOpenRouterConfigValue("SiteUrl", "OPENROUTER_SITE_URL", "https://travelwai.onrender.com");
        var appName = GetOpenRouterConfigValue("AppName", "OPENROUTER_APP_NAME", "TravelwAI");
        var openRouterEndpoint = BuildOpenRouterChatCompletionsUri();

        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = "Bạn là AI lập lịch trình du lịch cho TravelwAI. Trả lời DUY NHẤT bằng JSON hợp lệ, không markdown. " +
                          "Nhiệm vụ: tạo hoặc chỉnh lịch trình theo form Schedule của ứng dụng, hoặc hỏi lại nếu thiếu thông tin quan trọng. " +
                          "Hiểu ngày tương đối theo ngày hiện tại do prompt cung cấp: 'hôm nay' là ngày hiện tại, 'ngày mai' là ngày hiện tại + 1. " +
                          "Ví dụ người dùng nói 'đi Đà Lạt, 2 ngày, đi hôm nay' thì đủ thông tin: start_date là hôm nay, end_date là hôm nay + 1 ngày. " +
                          "Nếu người dùng chỉ yêu cầu chỉnh một phần như ngân sách, đơn vị tiền, ngày đi hoặc số ngày, hãy giữ nguyên các phần khác đang có trong form và chỉ sửa đúng phần được yêu cầu. " +
                          "Hiểu tiền Việt: 2 củ = 2 triệu = 2000000 VND, 500k = 500000 VND. Hiểu 'tiền đô', 'tiền đo', 'USD', 'đô la' là currency='USD'. " +
                          "Nếu thiếu điểm đến/tỉnh thành, ngày bắt đầu, ngày kết thúc hoặc số ngày để đủ tính ngày kết thúc, hãy trả status='needs_more_info' và question là câu hỏi ngắn gọn bằng tiếng Việt. " +
                          "Nếu đã đủ thông tin, trả status='ready', reply ngắn gọn, và schedule theo schema: " +
                          "{title:string, description:string, start_date:'yyyy-MM-dd', end_date:'yyyy-MM-dd', budget:number|null, currency:'VND'|'USD'|'EUR', tags:string[], days:[{day_number:number, date:'yyyy-MM-dd', destinations:[{name:string, description:string, estimated_duration:string|null, time_phase:string, time_range:string}]}]}. " +
                          "Mỗi ngày nên có 3-5 hoạt động, chia sáng/trưa/chiều/tối, ưu tiên địa điểm văn hoá, du lịch, ăn uống hợp lý. Không đưa giá chính xác nếu không có nguồn; nếu cần hãy ghi ước lượng trong description."
            }
        };

        if (request.History is not null)
        {
            foreach (var item in request.History
                         .Where(item => !string.IsNullOrWhiteSpace(item.Content))
                         .TakeLast(8))
            {
                var role = string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? "assistant"
                    : "user";
                messages.Add(new { role, content = item.Content!.Trim() });
            }
        }

        messages.Add(new { role = "user", content = BuildSchedulePlannerPrompt(request) });

        var payload = new
        {
            model,
            messages,
            temperature = 0.35,
            max_tokens = 2600
        };

        using var http = _httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, openRouterEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.TryAddWithoutValidation("HTTP-Referer", siteUrl);
        httpRequest.Headers.TryAddWithoutValidation("X-Title", appName);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(httpRequest);
        var responseText = await response.Content.ReadAsStringAsync();

        LogOpenRouterRawResponse("SCHEDULE PLAN", response, responseText);

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
        catch (JsonException ex)
        {
            Console.WriteLine("===== OPENROUTER SCHEDULE PLAN JSON PARSE ERROR =====");
            Console.WriteLine(ex.Message);
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
            return StatusCode(502, new
            {
                success = false,
                detail = "AI chưa trả về JSON hợp lệ để lập lịch trình. Vui lòng hỏi lại ngắn gọn hơn.",
                raw = answer
            });
        }

        if (aiResult["status"] is null)
        {
            aiResult["status"] = JsonValue.Create(aiResult["schedule"] is null ? "needs_more_info" : "ready");
        }

        if (string.Equals(aiResult["status"]?.ToString(), "ready", StringComparison.OrdinalIgnoreCase))
        {
            ApplyScheduleHintsToReadyResult(aiResult, request);
        }

        if (string.Equals(aiResult["status"]?.ToString(), "ready", StringComparison.OrdinalIgnoreCase) && !HasUsableAiSchedule(aiResult))
        {
            aiResult["status"] = JsonValue.Create("needs_more_info");
            aiResult["question"] = JsonValue.Create("Bạn cho mình biết rõ điểm đến, ngày bắt đầu, ngày kết thúc hoặc số ngày đi để mình lập lịch trình chính xác nhé.");
        }

        if (aiResult["reply"] is null && aiResult["question"] is not null)
        {
            aiResult["reply"] = JsonValue.Create(aiResult["question"]!.ToString());
        }

        return Ok(new { success = true, data = aiResult, message = "AI đã xử lý yêu cầu lập lịch trình" });
    }

    private static bool HasUsableAiSchedule(JsonObject result)
    {
        if (result["schedule"] is not JsonObject schedule) return false;
        var startDate = schedule["start_date"]?.ToString();
        var endDate = schedule["end_date"]?.ToString();
        if (string.IsNullOrWhiteSpace(startDate) || string.IsNullOrWhiteSpace(endDate)) return false;
        return schedule["days"] is JsonArray days && days.Count > 0;
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
               "Yêu cầu mới của người dùng:\n" +
               request.Message!.Trim() +
               "\n\nThông tin hiện đang có trong form lịch trình của TravelwAI:\n" +
               currentScheduleJson +
               "\n\nQuy tắc xử lý:\n" +
               "- Nếu người dùng nói 'hôm nay', dùng start_date=" + today.ToString("yyyy-MM-dd") + ".\n" +
               "- Nếu người dùng nói 'ngày mai' hoặc 'từ ngày mai', dùng start_date=" + tomorrow.ToString("yyyy-MM-dd") + ".\n" +
               "- Nếu người dùng nói số ngày, ví dụ 2 ngày, hãy tính end_date = start_date + số ngày - 1.\n" +
               "- Nếu người dùng chỉ sửa một phần, ví dụ 'ngân sách 2 củ', 'đổi sang tiền đô', 'đi từ ngày mai', hãy giữ nguyên các phần khác trong form.\n" +
               "- 1 củ = 1 triệu VND; 2 củ = 2000000; 500k = 500000. 'tiền đô/tiền đo/USD/đô la' nghĩa là currency USD.\n" +
               "Hãy kiểm tra đủ thông tin chưa. Nếu thiếu thông tin quan trọng thì chỉ hỏi lại 1-3 ý còn thiếu. " +
               "Nếu đủ thì tạo JSON lịch trình có thể đưa thẳng vào form.";
    }

    private static JsonObject? TryBuildQuickScheduleEditPatch(AiSchedulePlanRequest request)
    {
        var message = request.Message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message)) return null;

        var currentSchedule = GetCurrentScheduleObject(request);
        if (currentSchedule is null || !CurrentScheduleHasUsefulData(currentSchedule)) return null;
        if (LooksLikeNewDestinationRequest(message)) return null;

        var patch = new JsonObject();
        var replyParts = new List<string>();
        var currentStart = GetJsonString(currentSchedule, "start_date");
        var currentEnd = GetJsonString(currentSchedule, "end_date");

        var budget = TryParseBudgetAmount(message);
        if (budget.HasValue)
        {
            patch["budget"] = JsonValue.Create((double)budget.Value);
            if (ParseCurrencyHint(message) is null) patch["currency"] = JsonValue.Create("VND");
            replyParts.Add("ngân sách");
        }

        var currency = ParseCurrencyHint(message);
        if (!string.IsNullOrWhiteSpace(currency))
        {
            patch["currency"] = JsonValue.Create(currency);
            replyParts.Add("đơn vị tiền tệ");
        }

        var startHint = TryParseStartDateHint(message);
        var durationDays = TryParseDurationDays(message);
        var addDays = TryParseAddOrReduceDays(message);

        if (startHint.HasValue)
        {
            patch["start_date"] = JsonValue.Create(startHint.Value.ToString("yyyy-MM-dd"));
            var oldDuration = TryCalculateInclusiveDuration(currentStart, currentEnd);
            var finalDuration = durationDays ?? oldDuration ?? 1;
            patch["end_date"] = JsonValue.Create(startHint.Value.AddDays(finalDuration - 1).ToString("yyyy-MM-dd"));
            replyParts.Add("ngày đi");
        }
        else if (addDays.HasValue && !string.IsNullOrWhiteSpace(currentEnd))
        {
            var end = TryParseIsoDate(currentEnd);
            if (end.HasValue)
            {
                patch["end_date"] = JsonValue.Create(end.Value.AddDays(addDays.Value).ToString("yyyy-MM-dd"));
                replyParts.Add(addDays.Value > 0 ? "thêm ngày" : "giảm ngày");
            }
        }
        else if (durationDays.HasValue && !string.IsNullOrWhiteSpace(currentStart))
        {
            var start = TryParseIsoDate(currentStart) ?? GetVietnamToday();
            patch["end_date"] = JsonValue.Create(start.AddDays(durationDays.Value - 1).ToString("yyyy-MM-dd"));
            replyParts.Add("số ngày đi");
        }

        if (patch.Count == 0) return null;

        return new JsonObject
        {
            ["status"] = JsonValue.Create("ready"),
            ["mode"] = JsonValue.Create("patch"),
            ["patch"] = patch,
            ["reply"] = JsonValue.Create("Đã chỉnh " + string.Join(", ", replyParts.Distinct()) + " theo yêu cầu. Bạn kiểm tra lại rồi bấm Tạo lịch trình để lưu.")
        };
    }

    private static void ApplyScheduleHintsToReadyResult(JsonObject aiResult, AiSchedulePlanRequest request)
    {
        if (aiResult["schedule"] is not JsonObject schedule) return;

        var message = request.Message?.Trim() ?? string.Empty;
        var currentSchedule = GetCurrentScheduleObject(request);
        var currentStart = currentSchedule is null ? string.Empty : GetJsonString(currentSchedule, "start_date");
        var currentEnd = currentSchedule is null ? string.Empty : GetJsonString(currentSchedule, "end_date");

        var startHint = TryParseStartDateHint(message);
        var durationDays = TryParseDurationDays(message);
        var budget = TryParseBudgetAmount(message);
        var currency = ParseCurrencyHint(message);

        if (budget.HasValue)
        {
            schedule["budget"] = JsonValue.Create((double)budget.Value);
            if (string.IsNullOrWhiteSpace(currency)) currency = "VND";
        }

        if (!string.IsNullOrWhiteSpace(currency))
        {
            schedule["currency"] = JsonValue.Create(currency);
        }

        if (startHint.HasValue)
        {
            schedule["start_date"] = JsonValue.Create(startHint.Value.ToString("yyyy-MM-dd"));
        }

        var startText = schedule["start_date"]?.ToString();
        var start = TryParseIsoDate(startText) ?? TryParseIsoDate(currentStart) ?? GetVietnamToday();

        if (durationDays.HasValue)
        {
            schedule["start_date"] = JsonValue.Create(start.ToString("yyyy-MM-dd"));
            schedule["end_date"] = JsonValue.Create(start.AddDays(durationDays.Value - 1).ToString("yyyy-MM-dd"));
            NormalizeAiScheduleDayDates(schedule, start, durationDays.Value);
        }
        else if (startHint.HasValue)
        {
            var oldDuration = TryCalculateInclusiveDuration(currentStart, currentEnd);
            var aiDuration = TryCalculateInclusiveDuration(schedule["start_date"]?.ToString(), schedule["end_date"]?.ToString());
            var finalDuration = aiDuration ?? oldDuration;
            if (finalDuration.HasValue)
            {
                schedule["end_date"] = JsonValue.Create(startHint.Value.AddDays(finalDuration.Value - 1).ToString("yyyy-MM-dd"));
                NormalizeAiScheduleDayDates(schedule, startHint.Value, finalDuration.Value);
            }
        }
    }

    private static void NormalizeAiScheduleDayDates(JsonObject schedule, DateOnly start, int durationDays)
    {
        if (durationDays <= 0 || schedule["days"] is not JsonArray days) return;

        while (days.Count > durationDays)
        {
            days.RemoveAt(days.Count - 1);
        }

        for (var i = 0; i < days.Count; i++)
        {
            if (days[i] is not JsonObject day) continue;
            day["day_number"] = JsonValue.Create(i + 1);
            day["date"] = JsonValue.Create(start.AddDays(i).ToString("yyyy-MM-dd"));
        }
    }

    private static JsonObject? GetCurrentScheduleObject(AiSchedulePlanRequest request)
    {
        if (request.CurrentSchedule.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        try
        {
            return JsonNode.Parse(request.CurrentSchedule.GetRawText()) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static bool CurrentScheduleHasUsefulData(JsonObject currentSchedule)
    {
        return !string.IsNullOrWhiteSpace(GetJsonString(currentSchedule, "title"))
               || !string.IsNullOrWhiteSpace(GetJsonString(currentSchedule, "description"))
               || !string.IsNullOrWhiteSpace(GetJsonString(currentSchedule, "start_date"))
               || !string.IsNullOrWhiteSpace(GetJsonString(currentSchedule, "end_date"))
               || !string.IsNullOrWhiteSpace(GetJsonString(currentSchedule, "budget"))
               || (currentSchedule["tags"] is JsonArray tags && tags.Count > 0)
               || (currentSchedule["days"] is JsonObject daysObject && daysObject.Count > 0)
               || (currentSchedule["days"] is JsonArray daysArray && daysArray.Count > 0);
    }

    private static string GetJsonString(JsonObject obj, string propertyName)
    {
        return obj[propertyName]?.ToString()?.Trim() ?? string.Empty;
    }

    private static DateOnly GetVietnamToday()
    {
        try
        {
            var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone));
        }
        catch
        {
            try
            {
                var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone));
            }
            catch
            {
                return DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            }
        }
    }

    private static DateOnly? TryParseStartDateHint(string message)
    {
        var normalized = NormalizeVietnameseForSearch(message);
        var today = GetVietnamToday();

        if (Regex.IsMatch(normalized, @"\b(ngay mai|mai)\b", RegexOptions.IgnoreCase)) return today.AddDays(1);
        if (Regex.IsMatch(normalized, @"\b(hom nay|bay gio|toi nay|chieu nay|sang nay)\b", RegexOptions.IgnoreCase)) return today;

        var match = Regex.Match(message, @"(?<!\d)(\d{1,2})[/-](\d{1,2})(?:[/-](\d{2,4}))?(?!\d)");
        if (match.Success)
        {
            var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : today.Year;
            if (year < 100) year += 2000;
            try
            {
                var date = new DateOnly(year, month, day);
                if (!match.Groups[3].Success && date < today) date = date.AddYears(1);
                return date;
            }
            catch
            {
                return null;
            }
        }

        match = Regex.Match(message, @"(?<!\d)(\d{4})-(\d{2})-(\d{2})(?!\d)");
        if (match.Success && DateOnly.TryParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var isoDate))
        {
            return isoDate;
        }

        return null;
    }

    private static DateOnly? TryParseIsoDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        var match = Regex.Match(text, @"\d{4}-\d{2}-\d{2}");
        if (match.Success && DateOnly.TryParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        return null;
    }

    private static int? TryParseDurationDays(string message)
    {
        var normalized = NormalizeVietnameseForSearch(message);
        var match = Regex.Match(normalized, @"(?<!\d)(\d{1,2})\s*(ngay|day|days)(?!\w)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)) return null;
        return days is >= 1 and <= 30 ? days : null;
    }

    private static int? TryParseAddOrReduceDays(string message)
    {
        var normalized = NormalizeVietnameseForSearch(message);
        var add = Regex.Match(normalized, @"\b(them|cong)\s*(\d{1,2})\s*(ngay|day|days)\b", RegexOptions.IgnoreCase);
        if (add.Success && int.TryParse(add.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var addDays))
        {
            return addDays is >= 1 and <= 30 ? addDays : null;
        }

        var reduce = Regex.Match(normalized, @"\b(bot|giam|rut ngan)\s*(\d{1,2})\s*(ngay|day|days)\b", RegexOptions.IgnoreCase);
        if (reduce.Success && int.TryParse(reduce.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var reduceDays))
        {
            return reduceDays is >= 1 and <= 30 ? -reduceDays : null;
        }

        return null;
    }

    private static int? TryCalculateInclusiveDuration(string? startText, string? endText)
    {
        var start = TryParseIsoDate(startText);
        var end = TryParseIsoDate(endText);
        if (!start.HasValue || !end.HasValue || end.Value < start.Value) return null;
        return end.Value.DayNumber - start.Value.DayNumber + 1;
    }

    private static decimal? TryParseBudgetAmount(string message)
    {
        var normalized = NormalizeVietnameseForSearch(message).Replace(',', '.');
        decimal? MatchAmount(string pattern, decimal multiplier)
        {
            var match = Regex.Match(normalized, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            if (!decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return null;
            return Math.Round(value * multiplier);
        }

        return MatchAmount(@"(?<!\d)(\d+(?:[\.,]\d+)?)\s*(cu|trieu|tr|m|million)\b", 1_000_000m)
               ?? MatchAmount(@"(?<!\d)(\d+(?:[\.,]\d+)?)\s*(k|nghin|ngan|ngan dong|nghin dong)\b", 1_000m)
               ?? MatchAmount(@"\b(?:ngan sach|budget|tien)\s*(?:la|khoang|tam|toi da|duoi)?\s*(\d{5,12})\b", 1m);
    }

    private static string? ParseCurrencyHint(string message)
    {
        var normalized = NormalizeVietnameseForSearch(message);
        if (Regex.IsMatch(normalized, @"\b(usd|dollar|do la|tien do|dong do|do)\b", RegexOptions.IgnoreCase)) return "USD";
        if (Regex.IsMatch(normalized, @"\b(eur|euro)\b", RegexOptions.IgnoreCase)) return "EUR";
        if (Regex.IsMatch(normalized, @"\b(vnd|vnđ|viet nam dong|tien viet|dong viet|dong)\b", RegexOptions.IgnoreCase)) return "VND";
        return null;
    }

    private static bool LooksLikeNewDestinationRequest(string message)
    {
        var normalized = NormalizeVietnameseForSearch(message);
        return Regex.IsMatch(normalized, @"\b(di|den)\s+(?!tu\b|hom\b|ngay\b|vao\b|trong\b|luc\b|voi\b|may\b|bao\b|muon\b)([a-z])", RegexOptions.IgnoreCase);
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
            builder.Append(ch switch
            {
                '\u0111' => 'd',
                '\u0110' => 'D',
                _ => ch
            });
        }
        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
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
            ("Đặt lại mật khẩu", "/reset-password", new[] { "dat lai mat khau", "reset password", "reset mat khau" }, "Nhập mã khôi phục và tạo mật khẩu mới."),
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
            ("Business", "/business", new[] { "business", "business", "trang business" }, "Quản lý tour, đơn bán tour, doanh thu và phí dịch vụ."),
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

        if (Match(normalized, @"\b(dang\s*xuat|thoat\s*tai\s*khoan|log\s*out)\b"))
        {
            return "Đang đăng xuất tài khoản.";
        }

        if (Match(normalized, @"\b(doi\s*mat\s*khau|doi\s*password|change\s*password)\b"))
        {
            return "Đang mở Hồ sơ để đổi mật khẩu.";
        }

        if (Match(normalized, @"(co\s*)?trang\s*nao|danh\s*sach\s*trang|menu|cac\s*trang|nhung\s*trang|xem\s*trang"))
        {
            return "Các trang TravelwAI: " + PageListText() + ". " + SyntaxReply();
        }

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
        if (directPage.HasValue && (NormalizeLocal(directPage.Value.Name) == normalized || directPage.Value.Aliases.Any(alias => NormalizeLocal(alias) == normalized)))
        {
            return DetailReply(directPage.Value);
        }

        if (normalized.Contains("trang") || pages.Any(page => normalized.Contains(NormalizeLocal(page.Name)) || page.Aliases.Any(alias => normalized.Contains(NormalizeLocal(alias)))))
        {
            return SyntaxReply();
        }

        return null;
    }

    private static JsonObject? TryParseAiJsonObject(string text)
    {
        var cleaned = text.Trim();
        cleaned = Regex.Replace(cleaned, @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s*```$", "", RegexOptions.IgnoreCase).Trim();

        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            cleaned = cleaned[start..(end + 1)];
        }

        try
        {
            return JsonNode.Parse(cleaned) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsOpenRouterAnswerCutOff(string? finishReason)
    {
        return string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);
    }

    private static bool GuideMessageNeedsWikipedia(string? message)
    {
        var normalized = NormalizeVietnameseForSearch(message ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        if (FindProvinceNamesInText(message).Any()) return true;

        var factualKeywords = new[]
        {
            "dia danh", "di tich", "danh lam", "tinh thanh", "tinh nao", "thanh pho", "le hoi", "ngay le",
            "lich su", "van hoa", "truyen thuyet", "nguon goc", "y nghia", "nhan vat", "dan toc", "di san",
            "bao tang", "den tho", "ngoi chua", "thap", "hoang thanh", "co do", "pho co", "lang nghe",
            "o dau", "la gi", "khi nao", "ngay nao", "dien ra", "to chuc", "ke chuyen", "gioi thieu", "thuyet minh",
            "hoi lim", "gio to", "tet", "quoc khanh", "trung thu", "thang long", "ha long", "nha trang", "phu quoc", "da lat", "hoi an", "hue", "sa pa", "sapa", "tam coc", "trang an"
        };

        return factualKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string TryBuildGuideConversationalReply(string? message)
    {
        var normalized = NormalizeVietnameseForSearch(message ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        if (Regex.IsMatch(normalized, @"\b(xin chao|chao|hi|hello|alo|hey)\b", RegexOptions.IgnoreCase))
        {
            return "Chào bạn, mình là Hướng dẫn viên Travelwinne. Bạn muốn mình gợi ý lịch trình, tư vấn điểm đến hay kể về một địa danh cụ thể?";
        }

        if (normalized.Contains("cam on", StringComparison.OrdinalIgnoreCase) || normalized.Contains("thanks", StringComparison.OrdinalIgnoreCase))
        {
            return "Không có gì. Bạn cần mình gợi ý thêm điểm đến, lịch trình hay kinh nghiệm đi lại thì cứ nhắn tiếp nhé.";
        }

        if (normalized.Contains("ban la ai", StringComparison.OrdinalIgnoreCase) || normalized.Contains("lam duoc gi", StringComparison.OrdinalIgnoreCase) || normalized.Contains("giup duoc gi", StringComparison.OrdinalIgnoreCase))
        {
            return "Mình là Hướng dẫn viên Travelwinne. Mình có thể trò chuyện, gợi ý cách lên lịch trình và tra thông tin du lịch, văn hoá, lịch sử từ Wikipedia khi bạn hỏi về địa danh, tỉnh thành, lễ hội hoặc ngày lễ.";
        }

        if (normalized.Contains("toi muon di du lich", StringComparison.OrdinalIgnoreCase) || normalized.Contains("tu van", StringComparison.OrdinalIgnoreCase) || normalized.Contains("goi y", StringComparison.OrdinalIgnoreCase))
        {
            return "Bạn muốn đi kiểu nào: biển, núi, nghỉ dưỡng, khám phá văn hoá hay đi cùng nhóm bạn? Cho mình thêm thời gian đi, số người và ngân sách để gợi ý sát hơn.";
        }

        if (normalized.Length <= 30)
        {
            return "Bạn nói rõ hơn một chút nhé. Nếu hỏi về địa danh, tỉnh thành, lễ hội, lịch sử, văn hoá hoặc ngày lễ, mình sẽ tra Wikipedia để trả lời chính xác.";
        }

        return "Mình hiểu rồi. Bạn cho mình thêm điểm đến, thời gian đi, số người hoặc ngân sách để mình hỗ trợ như một hướng dẫn viên nhé.";
    }

    private static bool IsGuideDateQuestion(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        var text = NormalizeVietnameseForSearch(message);
        var dateKeywords = new[]
        {
            "ngay nao", "ngay may", "ngay bao nhieu", "khi nao", "bao gio", "luc nao",
            "thoi gian", "dien ra", "to chuc", "may thang", "thang nao", "nam nao",
            "am lich", "duong lich", "mung", "dien bien ngay", "dang dien ra"
        };

        return dateKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
    private static string RemoveGuideDateMentions(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var cleaned = text;
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        cleaned = Regex.Replace(cleaned, "\\b(?:ngày\\s*)?(?:mùng\\s*)?[0-3]?\\d\\s*(?:đến|den|[-\u2013\u2014])\\s*[0-3]?\\d/\\d{1,2}\\s*(?:âm\\s*lịch|am\\s*lich|dương\\s*lịch|duong\\s*lich|AL|DL)?", "", options);
        cleaned = Regex.Replace(cleaned, "\\b(?:ngày\\s*)?(?:mùng\\s*)?[0-3]?\\d/\\d{1,2}\\s*(?:âm\\s*lịch|am\\s*lich|dương\\s*lịch|duong\\s*lich|AL|DL)?", "", options);
        cleaned = Regex.Replace(cleaned, "\\b(?:ngày\\s*)?(?:mùng\\s*)?[0-3]?\\d\\s+đến\\s+[0-3]?\\d\\s+tháng\\s+\\d{1,2}\\s*(?:âm\\s*lịch|am\\s*lich|dương\\s*lịch|duong\\s*lich)?", "", options);
        cleaned = Regex.Replace(cleaned, "\\b[0-3]?\\d\\s+[0-3]?\\d\\s+\\d{1,2}\\s*(?:âm\\s*lịch|am\\s*lich|dương\\s*lịch|duong\\s*lich)\\b", "", options);
        cleaned = Regex.Replace(cleaned, "\\b(?:tháng\\s*)\\d{1,2}\\s*(?:âm\\s*lịch|am\\s*lich|dương\\s*lịch|duong\\s*lich|AL|DL)?", "", options);
        cleaned = Regex.Replace(cleaned, @"\s*(?:Thời gian|Thông tin thời gian)\s*:\s*[,.;]?", " ", options);
        cleaned = Regex.Replace(cleaned, @"\s+([,.;])", "$1");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"\(\s*\)", "").Trim();
        cleaned = Regex.Replace(cleaned, @"\s*,\s*,+", ", ").Trim();
        cleaned = Regex.Replace(cleaned, @"^[,.;\s]+", "").Trim();

        return cleaned;
    }

    private static string BuildAiContextBlock(string? context, string assistantMode, bool includeDateInformation)
    {
        if (string.IsNullOrWhiteSpace(context)) return string.Empty;

        var cleaned = Regex.Replace(context.Trim(), @"\s+", " ");
        cleaned = Regex.Replace(cleaned, @"[\u0000-\u001F\u007F]", " ").Trim();
        if (assistantMode == "guide" && !includeDateInformation)
        {
            cleaned = RemoveGuideDateMentions(cleaned);
        }
        const int maxContextChars = 3600;
        if (cleaned.Length > maxContextChars)
        {
            cleaned = cleaned[..maxContextChars].Trim();
        }

        return assistantMode == "guide"
            ? (includeDateInformation
                ? "THÔNG TIN NỀN CỦA TRAVELWAI. Đây là nguồn ưu tiên số 2 sau Wikipedia tiếng Việt. Chỉ dùng phần này khi Wikipedia không có thông tin phù hợp. Khi dùng dữ liệu này, phải trả lời dựa trên phần này, không tự thêm chi tiết ngoài nguồn. Chỉ khi Wikipedia và phần này đều không có thông tin phù hợp mới được dùng kiến thức chung. Khi trả lời, đi thẳng vào nội dung và không dùng lời dẫn nguồn ở đầu câu. Thông tin: " + cleaned
                : "THÔNG TIN NỀN CỦA TRAVELWAI. Đây là nguồn ưu tiên số 2 sau Wikipedia tiếng Việt. Câu hỏi không hỏi ngày tháng, nên không nêu thời gian. Chỉ dùng phần này khi Wikipedia không có thông tin phù hợp. Khi dùng dữ liệu này, phải trả lời dựa trên tên lễ hội, tỉnh/thành, dân tộc, nguồn gốc, ý nghĩa và hoạt động trong phần này, không tự thêm chi tiết ngoài nguồn. Chỉ khi Wikipedia và phần này đều không có thông tin phù hợp mới được dùng kiến thức chung. Khi trả lời, đi thẳng vào nội dung và không dùng lời dẫn nguồn ở đầu câu. Thông tin: " + cleaned)
            : "NGỮ CẢNH TỪ ỨNG DỤNG TRAVELWAI. Dùng để trả lời hướng dẫn ngắn gọn nếu phù hợp. Dữ liệu: " + cleaned;
    }

    private static async Task<string> BuildWikipediaDirectReplyAsync(HttpClient http, string? message, string? appContext, bool includeDateInformation)
    {
        var query = BuildWikipediaSearchQuery(message, appContext);
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://vi.wikipedia.org/w/api.php?action=query&generator=search&gsrlimit=1&prop=extracts|info&exintro=1&explaintext=1&inprop=url&format=json&origin=*&gsrsearch=" + Uri.EscapeDataString(query));
            request.Headers.TryAddWithoutValidation("User-Agent", "TravelwAI/1.0 (guide chatbot)");

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return string.Empty;

            var responseText = await response.Content.ReadAsStringAsync();
            var root = JsonNode.Parse(responseText);
            var pages = root?["query"]?["pages"]?.AsObject();
            if (pages is null || pages.Count == 0) return string.Empty;

            var page = pages.Select(item => item.Value).FirstOrDefault(item => item is not null);
            var title = page?["title"]?.ToString()?.Trim();
            var extract = page?["extract"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(extract)) return string.Empty;
            if (!IsWikipediaResultRelevant(query, title, extract)) return string.Empty;

            extract = Regex.Replace(extract, @"\s+", " ").Trim();
            if (!includeDateInformation)
            {
                extract = RemoveGuideDateMentions(extract);
            }

            const int maxWikipediaReplyChars = 900;
            if (extract.Length > maxWikipediaReplyChars)
            {
                extract = extract[..maxWikipediaReplyChars].Trim();
                var lastSentence = extract.LastIndexOf('.', StringComparison.Ordinal);
                if (lastSentence > 160) extract = extract[..(lastSentence + 1)].Trim();
            }

            return extract;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> BuildWikipediaContextBlockAsync(HttpClient http, string? message, string? appContext, bool includeDateInformation)
    {
        var query = BuildWikipediaSearchQuery(message, appContext);
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://vi.wikipedia.org/w/api.php?action=query&generator=search&gsrlimit=1&prop=extracts|info&exintro=1&explaintext=1&inprop=url&format=json&origin=*&gsrsearch=" + Uri.EscapeDataString(query));
            request.Headers.TryAddWithoutValidation("User-Agent", "TravelwAI/1.0 (guide chatbot)");

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return string.Empty;

            var responseText = await response.Content.ReadAsStringAsync();
            var root = JsonNode.Parse(responseText);
            var pages = root?["query"]?["pages"]?.AsObject();
            if (pages is null || pages.Count == 0) return string.Empty;

            var page = pages.Select(item => item.Value).FirstOrDefault(item => item is not null);
            var title = page?["title"]?.ToString()?.Trim();
            var extract = page?["extract"]?.ToString()?.Trim();
            var url = page?["fullurl"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(extract)) return string.Empty;
            if (!IsWikipediaResultRelevant(query, title, extract)) return string.Empty;

            extract = Regex.Replace(extract, @"\s+", " ").Trim();
            if (!includeDateInformation)
            {
                extract = RemoveGuideDateMentions(extract);
            }

            const int maxWikipediaChars = 2600;
            if (extract.Length > maxWikipediaChars)
            {
                extract = extract[..maxWikipediaChars].Trim();
            }

            var source = string.IsNullOrWhiteSpace(url) ? title : $"{title} ({url})";
            return "THÔNG TIN NỀN TỪ WIKIPEDIA TIẾNG VIỆT. Đây là nguồn ưu tiên số 1. Khi Wikipedia phù hợp với câu hỏi, phải bám theo nội dung này để bổ sung bối cảnh lịch sử, văn hoá và ý nghĩa, không tự thêm chi tiết ngoài nguồn. Nếu thông tin Wikipedia và thông tin ứng dụng khác nhau, ưu tiên Wikipedia. Chỉ khi cả Wikipedia và dữ liệu ứng dụng không có thông tin phù hợp mới được dùng kiến thức chung. Khi trả lời, đi thẳng vào nội dung và không dùng lời dẫn nguồn ở đầu câu. Nguồn tham khảo: " + source + ". Nội dung: " + extract;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsWikipediaResultRelevant(string query, string? title, string extract)
    {
        var normalizedSource = NormalizeVietnameseForSearch((title ?? string.Empty) + " " + extract);
        var queryTokens = NormalizeVietnameseForSearch(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim(',', '.', '-', ':', ';', '(', ')'))
            .Where(token => token.Length >= 3)
            .Where(token => !GuideWikipediaStopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (queryTokens.Count == 0) return false;
        return queryTokens.Any(token => normalizedSource.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> GuideWikipediaStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "hay", "cho", "toi", "biet", "ve", "la", "gi", "ai", "dau", "khi", "nao", "nhu", "the",
        "gioi", "thieu", "kham", "pha", "nghia", "nguon", "goc", "lich", "su", "van", "hoa",
        "huong", "dan", "vien", "travelwinne", "travelwai", "wiki", "wikipedia", "noi", "thong", "tin",
        "giai", "thich", "le", "hoi", "tom", "tat", "ke", "chuyen", "ro", "phan", "tich",
        "thoi", "gian", "to", "chuc", "dien", "ra", "ngay", "thang", "nam", "am", "duong",
        "bao", "gio", "luc", "mung", "may", "xay", "dung", "nguoi", "dan", "tai", "o"
    };

    private static string BuildWikipediaSearchQuery(string? message, string? appContext)
    {
        var source = !string.IsNullOrWhiteSpace(message) ? message! : appContext ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        source = Regex.Replace(source, @"https?://\S+", " ", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"[^\p{L}\p{M}\p{N}\s,.-]", " ");
        source = Regex.Replace(source, @"\s+", " ").Trim();

        var quoted = Regex.Matches(source, @"[""“”']([^""“”']{3,80})[""“”']")
            .Cast<Match>()
            .Select(match => match.Groups[1].Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(quoted)) return quoted;

        var keptTokens = Regex.Matches(source, @"[\p{L}\p{M}\p{N}]+")
            .Cast<Match>()
            .Select(match => match.Value.Trim())
            .Where(token => token.Length > 0)
            .Where(token => !GuideWikipediaStopWords.Contains(NormalizeVietnameseForSearch(token)))
            .Take(8)
            .ToList();

        var cleaned = string.Join(" ", keptTokens).Trim(' ', ',', '.', '-');
        if (cleaned.Length > 120)
        {
            cleaned = cleaned[..120].Trim();
        }

        return string.IsNullOrWhiteSpace(cleaned) ? source[..Math.Min(source.Length, 120)].Trim() : cleaned;
    }
    private static string CleanSimpleChatbotReply(string text, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(text) || maxWords <= 0) return string.Empty;

        var cleaned = text.Trim();
        cleaned = Regex.Replace(cleaned, @"^```(?:\w+)?\s*", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s*```$", "", RegexOptions.IgnoreCase).Trim();

        var lines = cleaned
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line.Trim(), @"^[-*•●▪▫►>\d]+[.)]?\s*", ""))
            .Where(line => !string.IsNullOrWhiteSpace(line));
        cleaned = string.Join(" ", lines);

        cleaned = Regex.Replace(cleaned, @"(\*\*|\*|__|_|`|#{1,6}|>|~|\[|\]|\{|\}|\||•|●|▪|▫|►|→|=>)", " ");

        var builder = new StringBuilder(cleaned.Length);
        foreach (var ch in cleaned)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
                continue;
            }

            if (ch is '.' or ',' or '(' or ')' or '/' or '-')
            {
                builder.Append(ch);
            }
            else if (ch is '!' or '?' or ':' or ';' or '\u2026')
            {
                builder.Append('.');
            }
            else
            {
                builder.Append(' ');
            }
        }

        cleaned = builder.ToString();
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"\s+([,./-])", "$1");
        cleaned = Regex.Replace(cleaned, @"([,.]){2,}", "$1");
        cleaned = ExpandVietnameseDateRanges(cleaned);

        var wordMatches = Regex.Matches(cleaned, @"[\p{L}\p{N}]+").Cast<Match>().ToList();
        if (wordMatches.Count > maxWords)
        {
            var lastWord = wordMatches[maxWords - 1];
            var limited = cleaned[..(lastWord.Index + lastWord.Length)].Trim();

            var sentenceEnds = Regex.Matches(limited, @"[.](?=\s|$)").Cast<Match>().ToList();
            if (sentenceEnds.Count > 0)
            {
                var lastSentenceEnd = sentenceEnds[^1];
                var sentenceSafe = limited[..(lastSentenceEnd.Index + 1)].Trim();
                if (Regex.Matches(sentenceSafe, @"[\p{L}\p{N}]+").Count >= Math.Min(20, maxWords))
                {
                    cleaned = sentenceSafe;
                }
                else
                {
                    cleaned = limited;
                }
            }
            else
            {
                cleaned = limited;
            }
        }

        cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{N})/.-]+$", "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return string.Empty;

        if (!Regex.IsMatch(cleaned, @"[.]$"))
        {
            cleaned += ".";
        }

        return cleaned;
    }

    private static string StripGuideSourceLeadIn(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var cleaned = Regex.Replace(
            text.Trim(),
            @"^\s*theo\s+(?:dữ\s+liệu\s+ứng\s+dụng|dữ\s+liệu|nguồn|wikipedia(?:\s+tiếng\s+việt)?|thông\s+tin\s+nền)(?:\s+[^,.:\n]{0,80})?\s*[:,.-]?\s*",
            "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return string.IsNullOrWhiteSpace(cleaned) ? text.Trim() : cleaned.Trim();
    }

    private static string ExpandVietnameseDateRanges(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        text = Regex.Replace(
            text,
            "\\b([0-3]?\\d)\\s*[-\u2013\u2014]\\s*([0-3]?\\d/\\d{1,2})(?=\\s*(?:âm\\s+lịch|am\\s+lich|dương\\s+lịch|duong\\s+lich|AL|DL|[,.)]|$))",
            match => FormatVietnameseDateRange(match.Groups[1].Value, match.Groups[2].Value));

        text = Regex.Replace(
            text,
            "\\b([0-3]?\\d)\\s+([0-3]?\\d/\\d{1,2})(?=\\s*(?:âm\\s+lịch|am\\s+lich|dương\\s+lịch|duong\\s+lich|AL|DL|[,.)]|$))",
            match => FormatVietnameseDateRange(match.Groups[1].Value, match.Groups[2].Value, match.Value));

        return text;
    }

    private static string FormatVietnameseDateRange(string startText, string endDateText, string fallback = "")
    {
        if (!int.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startDay))
        {
            return string.IsNullOrEmpty(fallback) ? $"{startText} đến {endDateText}" : fallback;
        }

        var parts = endDateText.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var endDay) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month) ||
            startDay < 1 || startDay > 31 || endDay < 1 || endDay > 31 || month < 1 || month > 12 || endDay <= startDay)
        {
            return string.IsNullOrEmpty(fallback) ? $"{startText} đến {endDateText}" : fallback;
        }

        return $"{startDay} đến {endDay}/{month:00}";
    }

    private string? GetOpenRouterApiKey()
    {
        var apiKey = GetOpenRouterConfigValue("ApiKey", "OPENROUTER_API_KEY", string.Empty);
        if (IsMissingOpenRouterSecret(apiKey))
        {
            return null;
        }

        return apiKey.Trim();
    }

    private string GetOpenRouterConfigValue(string key, string envName, string fallback)
    {
        var value = _configuration[$"OpenRouter:{key}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            value = _configuration[envName];
        }

        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private Uri BuildOpenRouterChatCompletionsUri()
    {
        var baseUrl = GetOpenRouterConfigValue("BaseUrl", "OPENROUTER_BASE_URL", "https://openrouter.ai/api/v1");
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri))
        {
            uri = new Uri("https://openrouter.ai/api/v1/");
        }

        return new Uri(uri, "chat/completions");
    }

    private static bool IsMissingOpenRouterSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        var clean = value.Trim();
        return clean.Equals("PASTE_OPENROUTER_API_KEY_HERE", StringComparison.OrdinalIgnoreCase)
            || clean.Equals("YOUR_OPENROUTER_API_KEY", StringComparison.OrdinalIgnoreCase)
            || clean.Equals("<OPENROUTER_API_KEY>", StringComparison.OrdinalIgnoreCase)
            || clean.Contains("YOUR_OPENROUTER_API_KEY", StringComparison.OrdinalIgnoreCase);
    }

    private static void LogOpenRouterRawResponse(string area, HttpResponseMessage response, string responseText)
    {
        Console.WriteLine("===== OPENROUTER " + area + " STATUS =====");
        Console.WriteLine((int)response.StatusCode + " " + response.StatusCode);

        Console.WriteLine("===== OPENROUTER " + area + " RAW RESPONSE =====");
        Console.WriteLine(TrimLogText(responseText));
    }

    private static string TrimLogText(string? text, int maxChars = 6000)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(empty)";
        return text.Length <= maxChars ? text : text[..maxChars] + "... [truncated]";
    }

    private static string BuildOpenRouterErrorMessage(int statusCode, string responseText)
    {
        var message = responseText;

        try
        {
            message = JsonNode.Parse(responseText)?["error"]?["message"]?.ToString() ?? responseText;
        }
        catch
        {

        }

        if (statusCode == 401)
        {
            return "AI chưa được cấu hình đúng. Vui lòng kiểm tra cấu hình API key.";
        }

        if (statusCode == 429 || message.Contains("rate", StringComparison.OrdinalIgnoreCase) || message.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            return "Hiện chưa trả lời được. Bạn thử lại sau.";
        }

        if (statusCode == 404 || message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "Model AI hiện không khả dụng. Vui lòng kiểm tra lại cấu hình model.";
        }

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

    private static string NormalizeAiAssistantMode(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text is "guide" or "travelwinne" or "travelwinne-guide" or "huong-dan-vien" ? "guide" : "travelwai";
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
