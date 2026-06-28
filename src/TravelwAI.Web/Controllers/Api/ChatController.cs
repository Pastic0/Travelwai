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
        var guideNeedsTrustedSource = assistantMode == "guide" && GuideMessageNeedsWikipedia(request.Message);
        const int aiReplyLimit = 300;
        const int aiMaxTokens = 750;
        using var http = _httpClientFactory.CreateClient();

        if (assistantMode == "guide")
        {
            var everydayFactReply = CleanSimpleChatbotReply(TryBuildGuideEverydayFactReply(request.Message), aiReplyLimit);
            if (!string.IsNullOrWhiteSpace(everydayFactReply))
            {
                return Ok(new
                {
                    success = true,
                    data = new { reply = everydayFactReply },
                    message = "Hướng dẫn viên đã trả lời thông tin hiện tại"
                });
            }
        }

        var guideTrustedSourceBlock = string.Empty;
        var guideTrustedFallbackReply = string.Empty;
        var guideTrustedSourceName = string.Empty;

        if (assistantMode == "guide" && guideNeedsTrustedSource)
        {
            guideTrustedSourceBlock = await BuildWikipediaContextBlockAsync(http, request.Message, request.Context, guideQuestionAsksForDate);
            if (!string.IsNullOrWhiteSpace(guideTrustedSourceBlock))
            {
                guideTrustedSourceName = "Wikipedia tiếng Việt";
                guideTrustedFallbackReply = CleanSimpleChatbotReply(await BuildWikipediaSmartFallbackReplyAsync(http, request.Message, request.Context, guideQuestionAsksForDate), aiReplyLimit);
            }

            if (string.IsNullOrWhiteSpace(guideTrustedSourceBlock))
            {
                guideTrustedSourceBlock = await BuildWikivoyageContextBlockAsync(http, request.Message, request.Context, guideQuestionAsksForDate);
                if (!string.IsNullOrWhiteSpace(guideTrustedSourceBlock))
                {
                    guideTrustedSourceName = "Wikivoyage tiếng Việt";
                    guideTrustedFallbackReply = CleanSimpleChatbotReply(await BuildWikivoyageSmartFallbackReplyAsync(http, request.Message, request.Context, guideQuestionAsksForDate), aiReplyLimit);
                }
            }

            if (string.IsNullOrWhiteSpace(guideTrustedSourceBlock))
            {
                var localContextBlock = BuildGuideTrustedLocalContextBlock(request.Message, request.Context, guideQuestionAsksForDate);
                if (!string.IsNullOrWhiteSpace(localContextBlock))
                {
                    guideTrustedSourceBlock = localContextBlock;
                    guideTrustedSourceName = "dữ liệu TravelwAI";
                    guideTrustedFallbackReply = CleanSimpleChatbotReply(TryBuildGuideTrustedLocalReply(request.Message, request.Context, guideQuestionAsksForDate), aiReplyLimit);
                }
            }

            if (string.IsNullOrWhiteSpace(guideTrustedSourceBlock))
            {
                return Ok(new
                {
                    success = true,
                    data = new { reply = "Mình chưa tìm thấy nguồn đủ tin cậy để trả lời chính xác. Bạn gửi đúng tên địa danh, lễ hội, nhân vật hoặc tỉnh/thành cụ thể hơn để mình tra lại nhé." },
                    message = "Không tìm thấy nguồn phù hợp"
                });
            }
        }

        if (assistantMode == "guide" && !guideNeedsTrustedSource)
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

        var apiKey = GetOpenRouterApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (!string.IsNullOrWhiteSpace(guideTrustedFallbackReply))
            {
                return Ok(new
                {
                    success = true,
                    data = new { reply = guideTrustedFallbackReply },
                    message = string.IsNullOrWhiteSpace(guideTrustedSourceName) ? "Đã trả lời bằng nguồn tin cậy" : $"Đã trả lời bằng {guideTrustedSourceName}"
                });
            }

            return StatusCode(500, new { success = false, detail = "Chưa cấu hình API key AI. Vui lòng kiểm tra cấu hình trên Render." });
        }

        var model = GetOpenRouterConfigValue("Model", "OPENROUTER_MODEL", "openrouter/free");
        var siteUrl = GetOpenRouterConfigValue("SiteUrl", "OPENROUTER_SITE_URL", "https://travelwai.onrender.com");
        var appName = GetOpenRouterConfigValue("AppName", "OPENROUTER_APP_NAME", "TravelwAI");
        var openRouterEndpoint = BuildOpenRouterChatCompletionsUri();
        var systemPrompt = assistantMode == "guide"
            ? "Bạn là Hướng dẫn viên Travelwinne. Trò chuyện tự nhiên, thân thiện như một hướng dẫn viên du lịch Việt Nam. Chỉ trả lời bằng tiếng Việt đơn giản, không markdown, không gạch đầu dòng, không emoji. Với câu hỏi cần thông tin chính xác, chỉ dùng THÔNG TIN NỀN TỪ NGUỒN TIN CẬY được hệ thống đưa vào: ưu tiên Wikipedia tiếng Việt, nếu Wikipedia không có thì dùng nguồn thay thế như Wikivoyage tiếng Việt hoặc dữ liệu TravelwAI. Hãy dùng nguồn để tự diễn giải đúng ý người dùng, không chép nguyên văn toàn đoạn, không mở đầu bằng Theo Wikipedia hoặc Theo nguồn. Nếu người dùng hỏi một mảng riêng như văn hoá, lịch sử, địa danh hoặc lễ hội thì chỉ tập trung đúng mảng đó; nếu người dùng hỏi nhiều mảng cùng lúc thì trả lời đủ các mảng được hỏi, không tự chọn sai chủ đề. Nếu không có nguồn nền phù hợp, hãy nói chưa đủ nguồn và hỏi lại tên cụ thể. Tuyệt đối không tự bịa địa danh, số liệu, ngày tháng, lịch sử, văn hoá, lễ hội hoặc ngày lễ. Trả lời tối đa 300 chữ, ưu tiên câu ngắn, đủ ý. Nếu sắp vượt giới hạn, chỉ dừng ở câu đã hoàn chỉnh, không viết câu đang dở."
            : "Bạn là Quản lí TravelwAI, trợ lí điều hướng và hướng dẫn sử dụng toàn bộ website TravelwAI. Chỉ trả lời bằng tiếng Việt đơn giản. Không dùng markdown, không gạch đầu dòng, không emoji, không ký hiệu lạ. Hướng dẫn ngắn gọn người dùng dùng các trang Lịch trình, Kế hoạch, Bản đồ Việt Nam, Nhắn tin, Tour du lịch, Sales, Admin, Hồ sơ, Thông báo và Phản hồi. Khi người dùng muốn mở trang, chỉ nhận cú pháp tới trang [tên trang] hoặc qua trang [tên trang]. Khi người dùng muốn xem hướng dẫn trang, nhận cú pháp chi tiết trang [tên trang] hoặc chỉ ghi đúng tên trang. Nếu người dùng ghi sai cú pháp mở trang, hãy hướng dẫn ghi đúng cú pháp thật ngắn. Với đổi mật khẩu hoặc đăng xuất, hãy xác nhận thao tác thật ngắn và giao diện sẽ tự chuyển trang nếu nhận diện được. Trả lời tối đa 300 chữ, ưu tiên câu ngắn, đủ ý. Nếu sắp vượt giới hạn, chỉ dừng ở câu đã hoàn chỉnh, không viết câu đang dở. Khi nói khoảng ngày, viết dạng 1 đến 15/01 âm lịch hoặc 5 đến 8/06 dương lịch, không viết 1-15/01 và không viết 1 15/01.";

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        var contextBlock = BuildAiContextBlock(request.Context, assistantMode, assistantMode != "guide" || guideQuestionAsksForDate);

        if (assistantMode == "guide")
        {
            if (!string.IsNullOrWhiteSpace(guideTrustedSourceBlock))
            {
                messages.Add(new { role = "system", content = guideTrustedSourceBlock });
            }
            else
            {
                messages.Add(new { role = "system", content = string.IsNullOrWhiteSpace(contextBlock) ? "Không có dữ liệu nền cho câu hỏi hiện tại. Chỉ trả lời giao tiếp chung, không nêu thông tin chính xác nếu không có nguồn." : "Chỉ dùng dữ liệu ứng dụng TravelwAI nếu phù hợp và không tự thêm chi tiết ngoài nguồn." });
            }

            if (!string.IsNullOrWhiteSpace(contextBlock) && !string.Equals(guideTrustedSourceName, "dữ liệu TravelwAI", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new { role = "system", content = contextBlock });
            }

            var guideAspectInstruction = BuildGuideAspectInstruction(request.Message);
            if (!string.IsNullOrWhiteSpace(guideAspectInstruction))
            {
                messages.Add(new { role = "system", content = guideAspectInstruction });
            }

            messages.Add(new { role = "system", content = "QUY TẮC CHO HƯỚNG DẪN VIÊN TRAVELWINNE: OpenRouter chỉ được dùng để diễn giải lại thông tin từ nguồn nền đã cung cấp, không được tự bịa. Thứ tự nguồn: 1 Wikipedia tiếng Việt, 2 Wikivoyage tiếng Việt, 3 dữ liệu TravelwAI. Không đọc lại nguyên văn nguồn. Không lấy nhầm sang báo chí, phát thanh, truyền hình, cơ quan nhà nước, đường cao tốc hoặc chủ đề không được hỏi. Không bịa thông tin chính xác về địa danh, tỉnh thành, lễ hội, lịch sử, văn hoá, ngày lễ, số liệu hoặc lịch sự kiện. Nếu nguồn nền không đủ thông tin cho câu hỏi, hãy nói chưa đủ nguồn và hỏi lại tên cụ thể." });
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
        var answerWasCutOff = false;
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
                if (!string.IsNullOrWhiteSpace(guideTrustedFallbackReply))
                {
                    return Ok(new
                    {
                        success = true,
                        data = new { reply = guideTrustedFallbackReply },
                        message = string.IsNullOrWhiteSpace(guideTrustedSourceName) ? "Đã trả lời bằng nguồn tin cậy" : $"Đã trả lời bằng {guideTrustedSourceName}"
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
                if (!string.IsNullOrWhiteSpace(guideTrustedFallbackReply))
                {
                    return Ok(new
                    {
                        success = true,
                        data = new { reply = guideTrustedFallbackReply },
                        message = string.IsNullOrWhiteSpace(guideTrustedSourceName) ? "Đã trả lời bằng nguồn tin cậy" : $"Đã trả lời bằng {guideTrustedSourceName}"
                    });
                }
                return StatusCode(502, new { success = false, detail = "AI trả về dữ liệu không hợp lệ.", raw = responseText });
            }

            var answerPart = json?["choices"]?[0]?["message"]?["content"]?.ToString();
            var finishReason = json?["choices"]?[0]?["finish_reason"]?.ToString();

            if (string.IsNullOrWhiteSpace(answerPart))
            {
                if (!string.IsNullOrWhiteSpace(guideTrustedFallbackReply))
                {
                    return Ok(new
                    {
                        success = true,
                        data = new { reply = guideTrustedFallbackReply },
                        message = string.IsNullOrWhiteSpace(guideTrustedSourceName) ? "Đã trả lời bằng nguồn tin cậy" : $"Đã trả lời bằng {guideTrustedSourceName}"
                    });
                }
                return StatusCode(502, new { success = false, detail = "AI chưa trả về nội dung hợp lệ.", raw = responseText });
            }

            if (fullAnswer.Length > 0)
            {
                fullAnswer.Append(' ');
            }

            fullAnswer.Append(answerPart.Trim());

            if (IsOpenRouterAnswerCutOff(finishReason))
            {
                answerWasCutOff = true;
            }

        }

        var answer = CleanSimpleChatbotReply(fullAnswer.ToString(), aiReplyLimit, answerWasCutOff);
        if (assistantMode == "guide")
        {
            answer = StripGuideSourceLeadIn(answer);
        }
        if (assistantMode == "guide" && !guideQuestionAsksForDate)
        {
            answer = RemoveGuideDateMentions(answer);
            answer = CleanSimpleChatbotReply(answer, aiReplyLimit, answerWasCutOff);
            answer = StripGuideSourceLeadIn(answer);
        }
        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = !string.IsNullOrWhiteSpace(guideTrustedFallbackReply)
                ? guideTrustedFallbackReply
                : "Mình chưa nhận được câu trả lời hoàn chỉnh. Bạn hỏi lại giúp mình nhé.";
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

    private static DateTime GetVietnamNow()
    {
        try
        {
            var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
        }
        catch
        {
            try
            {
                var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vietnamTimeZone);
            }
            catch
            {
                return DateTime.UtcNow.AddHours(7);
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

    private static string TryBuildGuideEverydayFactReply(string? message)
    {
        var normalized = NormalizeVietnameseForSearch(message ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        var asksTime = Regex.IsMatch(normalized, @"\b(bay gio|gio hien tai|may gio|mấy giờ|luc nay|gio may)\b", RegexOptions.IgnoreCase);
        var asksDate = Regex.IsMatch(normalized, @"\b(hom nay|ngay may|ngay bao nhieu|thu may|thu gi|nam nay|thang may|ngay hien tai)\b", RegexOptions.IgnoreCase);
        if (!asksTime && !asksDate) return string.Empty;

        var now = GetVietnamNow();
        var dayNames = new[] { "Chủ nhật", "thứ Hai", "thứ Ba", "thứ Tư", "thứ Năm", "thứ Sáu", "thứ Bảy" };
        var dayName = dayNames[(int)now.DayOfWeek];

        if (asksTime)
        {
            return $"Bây giờ ở Việt Nam khoảng {now:HH:mm}, {dayName}, ngày {now:dd/MM/yyyy}.";
        }

        return $"Hôm nay là {dayName}, ngày {now:dd/MM/yyyy} theo giờ Việt Nam.";
    }

    private static bool GuideMessageNeedsWikipedia(string? message)
    {
        var normalized = NormalizeVietnameseForSearch(message ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        if (FindProvinceNamesInText(message).Any()) return true;
        if (IsGenericGuideExplorationMessage(message)) return false;

        var factualKeywords = new[]
        {
            "dia danh", "di tich", "danh lam", "tinh thanh", "tinh nao", "thanh pho", "le hoi", "ngay le",
            "lich su", "van hoa", "truyen thuyet", "nguon goc", "y nghia", "nhan vat", "dan toc", "di san",
            "bao tang", "den tho", "den", "dinh", "chua", "ngoi chua", "thap", "hoang thanh", "co do", "pho co", "lang nghe",
            "o dau", "la gi", "khi nao", "ngay nao", "dien ra", "to chuc", "ke chuyen", "gioi thieu", "thuyet minh",
            "ai la", "la ai", "vi sao", "tai sao", "co phai", "dung khong", "bao nhieu", "bao lau", "may",
            "dien tich", "dan so", "nam nao", "trieu dai", "xay dung", "duoc cong nhan", "unesco", "nguoi sang lap",
            "hoi lim", "gio to", "tet", "quoc khanh", "trung thu", "thang long", "ha long", "nha trang", "phu quoc", "da lat", "hoi an", "hue", "sa pa", "sapa", "tam coc", "trang an",
            "gau tao", "long tong", "nong tong", "roong pooc", "xoan", "nao cong", "katê", "kate", "ok om bok"
        };

        if (factualKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))) return true;

        var meaningfulTokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3)
            .Where(token => !GuideWikipediaStopWords.Contains(token))
            .ToList();

        if (meaningfulTokens.Count is >= 2 and <= 6 && !LooksLikeGuideCasualOrPlanningMessage(normalized)) return true;

        return false;
    }

    private static bool LooksLikeGuideCasualOrPlanningMessage(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return true;
        if (Regex.IsMatch(normalized, @"\b(xin chao|chao|hi|hello|alo|hey|cam on|thanks|ok|oke|uh|um|vâng|vang)\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(normalized, @"\b(tu van|goi y|nen di|muon di|du lich|lich trinh|di choi|nghi duong|bien|nui|team building|gia dinh|ban be|cap doi|ngan sach|bao nhieu tien)\b", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    private static string TryBuildGuideConversationalReply(string? message)
    {
        var normalized = NormalizeVietnameseForSearch(message ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        if (Regex.IsMatch(normalized, @"\b(xin chao|chao|hi|hello|alo|hey)\b", RegexOptions.IgnoreCase))
        {
            return "Chào bạn, mình là Hướng dẫn viên Travelwinne. Bạn muốn mình tư vấn chuyến đi, gợi ý điểm đến hay tra một câu chuyện lịch sử cụ thể?";
        }

        if (normalized.Contains("cam on", StringComparison.OrdinalIgnoreCase) || normalized.Contains("thanks", StringComparison.OrdinalIgnoreCase))
        {
            return "Không có gì. Bạn cần mình gợi ý điểm đến, lịch trình, cách di chuyển hoặc tra thông tin văn hoá lịch sử thì cứ nhắn tiếp nhé.";
        }

        if (normalized.Contains("ban la ai", StringComparison.OrdinalIgnoreCase) || normalized.Contains("lam duoc gi", StringComparison.OrdinalIgnoreCase) || normalized.Contains("giup duoc gi", StringComparison.OrdinalIgnoreCase))
        {
            return "Mình là Hướng dẫn viên Travelwinne. Mình có thể trò chuyện, hỏi nhu cầu chuyến đi, gợi ý lịch trình chung và ưu tiên tra Wikipedia tiếng Việt, nếu không có thì dùng nguồn thay thế đáng tin khi bạn hỏi về địa danh, tỉnh thành, lễ hội, lịch sử, văn hoá hoặc ngày lễ.";
        }

        if (IsGenericGuideExplorationMessage(message))
        {
            return "Được nhé. Bạn muốn khám phá tỉnh, thành phố, địa danh hoặc lễ hội nào? Ví dụ: Huế, Hội An, Hoàng thành Thăng Long hoặc lễ hội Gầu Tào. Có tên cụ thể mình mới tra Wikipedia tiếng Việt hoặc nguồn thay thế cho đúng, không lấy nhầm chủ đề.";
        }

        if (Regex.IsMatch(normalized, @"\b(tu van|goi y|nen di|muon di|du lich|lich trinh|di choi|nghi duong|bien|nui|team building|gia dinh|ban be|cap doi)\b", RegexOptions.IgnoreCase))
        {
            return "Được nhé. Bạn cho mình biết điểm xuất phát, thời gian đi, số người, ngân sách và kiểu trải nghiệm muốn ưu tiên như biển, núi, nghỉ dưỡng hay khám phá văn hoá. Có thông tin đó mình sẽ gợi ý sát hơn.";
        }

        if (normalized.Length <= 30)
        {
            return "Bạn nói rõ hơn một chút nhé. Nếu hỏi về địa danh, tỉnh thành, lễ hội, lịch sử, văn hoá hoặc ngày lễ, mình sẽ ưu tiên Wikipedia tiếng Việt, nếu không có thì dùng nguồn thay thế đáng tin để trả lời.";
        }

        return "Mình nghe rồi. Để hướng dẫn đúng hơn, bạn gửi thêm điểm đến, thời gian đi, số người hoặc ngân sách nhé. Với thông tin lịch sử, văn hoá, lễ hội hay ngày lễ, mình sẽ ưu tiên tra Wikipedia tiếng Việt, nếu không có thì dùng nguồn thay thế đáng tin và không tự bịa.";
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
    private static bool IsGenericGuideExplorationMessage(string? message)
    {
        var normalized = NormalizeVietnameseForSearch(message ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (FindProvinceNamesInText(message).Any()) return false;
        if (HasKnownSpecificGuideTopic(normalized)) return false;

        var broadTopicCount = 0;
        var broadTopics = new[]
        {
            "van hoa", "lich su", "di tich", "le hoi", "dia danh", "danh lam", "lang nghe", "di san", "nhan vat"
        };
        foreach (var topic in broadTopics)
        {
            if (normalized.Contains(topic, StringComparison.OrdinalIgnoreCase)) broadTopicCount++;
        }

        if (broadTopicCount < 2) return false;

        var asksBroadExplore = Regex.IsMatch(normalized, @"\b(kham pha|tim hieu|gioi thieu|thuyet minh|ke chuyen|noi ve|giai thich)\b", RegexOptions.IgnoreCase);
        if (!asksBroadExplore) return false;

        var meaningfulTokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim(',', '.', '-', ':', ';', '(', ')'))
            .Where(token => token.Length >= 3)
            .Where(token => !GuideWikipediaStopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (meaningfulTokens.Count == 0) return true;

        var genericOnlyTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "kham", "pha", "tim", "hieu", "gioi", "thieu", "thuyet", "minh", "chuyen", "giai", "thich",
            "van", "hoa", "lich", "su", "tich", "hoi", "dia", "danh", "noi", "bat", "tieu", "bieu", "mien",
            "tinh", "thanh", "pho", "diem", "den", "trai", "nghiem", "viet", "nam", "bac", "trung", "dong", "tay", "phia"
        };

        return meaningfulTokens.All(token => genericOnlyTokens.Contains(token));
    }

    private static bool HasKnownSpecificGuideTopic(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        var knownSpecificTopics = new[]
        {
            "hoang thanh thang long", "thang long", "van mieu", "quoc tu giam", "co loa", "ho guom", "ho hoan kiem",
            "festival hue", "hue", "hoi an", "ha long", "nha trang", "phu quoc", "da lat", "sa pa", "sapa",
            "tam coc", "trang an", "gau tao", "hoi lim", "gio to hung vuong", "den hung", "tet nguyen dan",
            "trung thu", "quoc khanh", "ok om bok", "kate", "katê", "long tong", "nong tong", "roong pooc", "xoan",
            "nha tho duc ba", "dinh doc lap", "dia dao cu chi", "my son", "thanh nha ho", "co do hue", "viet nam"
        };

        return knownSpecificTopics.Any(topic => normalized.Contains(topic, StringComparison.OrdinalIgnoreCase));
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
                ? "THÔNG TIN NỀN CỦA TRAVELWAI. Đây là nguồn ưu tiên số 2 sau Wikipedia tiếng Việt. Chỉ dùng phần này khi Wikipedia không có thông tin phù hợp. Khi dùng dữ liệu này, phải trả lời dựa trên phần này, không tự thêm chi tiết ngoài nguồn. Khi trả lời, đi thẳng vào nội dung và không dùng lời dẫn nguồn ở đầu câu. Thông tin: " + cleaned
                : "THÔNG TIN NỀN CỦA TRAVELWAI. Đây là nguồn ưu tiên số 2 sau Wikipedia tiếng Việt. Câu hỏi không hỏi ngày tháng, nên không nêu thời gian. Chỉ dùng phần này khi Wikipedia không có thông tin phù hợp. Khi dùng dữ liệu này, phải trả lời dựa trên tên lễ hội, tỉnh/thành, dân tộc, nguồn gốc, ý nghĩa và hoạt động trong phần này, không tự thêm chi tiết ngoài nguồn. Khi trả lời, đi thẳng vào nội dung và không dùng lời dẫn nguồn ở đầu câu. Thông tin: " + cleaned)
            : "NGỮ CẢNH TỪ ỨNG DỤNG TRAVELWAI. Dùng để trả lời hướng dẫn ngắn gọn nếu phù hợp. Dữ liệu: " + cleaned;
    }

    private sealed class WikipediaPageCandidate
    {
        public WikipediaPageCandidate(string title, string extract, string url, int score)
        {
            Title = title;
            Extract = extract;
            Url = url;
            Score = score;
        }

        public string Title { get; }
        public string Extract { get; }
        public string Url { get; }
        public int Score { get; }
    }

    private static async Task<string> BuildWikipediaDirectReplyAsync(HttpClient http, string? message, string? appContext, bool includeDateInformation)
    {
        return await BuildWikipediaSmartFallbackReplyAsync(http, message, appContext, includeDateInformation);
    }

    private static async Task<string> BuildWikipediaSmartFallbackReplyAsync(HttpClient http, string? message, string? appContext, bool includeDateInformation)
    {
        var page = await FindBestWikipediaPageAsync(http, message, appContext);
        if (page is null) return string.Empty;

        var extract = PrepareWikipediaExtractForQuestion(page.Extract, message, includeDateInformation, 1200);
        if (string.IsNullOrWhiteSpace(extract)) return string.Empty;

        return BuildGuideLocalAnswerFromWikipedia(page.Title, extract, message);
    }

    private static async Task<string> BuildWikipediaContextBlockAsync(HttpClient http, string? message, string? appContext, bool includeDateInformation)
    {
        var page = await FindBestWikipediaPageAsync(http, message, appContext);
        if (page is null) return string.Empty;

        var extract = PrepareWikipediaExtractForQuestion(page.Extract, message, includeDateInformation, 3400);
        if (string.IsNullOrWhiteSpace(extract)) return string.Empty;

        var source = string.IsNullOrWhiteSpace(page.Url) ? page.Title : $"{page.Title} ({page.Url})";
        return "THÔNG TIN NỀN TỪ WIKIPEDIA TIẾNG VIỆT. Đây là nguồn ưu tiên số 1. Người dùng hỏi: " + (message ?? string.Empty).Trim() + ". Hãy dùng nội dung này để tự diễn giải đúng trọng tâm câu hỏi, không chép nguyên văn toàn đoạn và không mở đầu bằng Theo Wikipedia. Nếu người dùng hỏi một mảng riêng như văn hoá, lịch sử, địa danh/du lịch hoặc lễ hội thì chỉ tập trung đúng mảng đó. Nếu người dùng hỏi nhiều mảng cùng lúc như văn hoá, lịch sử, di tích và lễ hội, hãy trả lời đủ các mảng được hỏi theo nguồn. Tuyệt đối không lấy nhầm sang báo chí, phát thanh, truyền hình, cơ quan truyền thông, đường cao tốc hoặc chủ đề không được hỏi. Nếu nội dung không đủ cho đúng khía cạnh người dùng hỏi, hãy nói chưa đủ thông tin từ Wikipedia tiếng Việt. Nếu thông tin Wikipedia và thông tin ứng dụng khác nhau, ưu tiên Wikipedia. Nguồn tham khảo nội bộ: " + source + ". Nội dung: " + extract;
    }

    private static async Task<string> BuildWikivoyageSmartFallbackReplyAsync(HttpClient http, string? message, string? appContext, bool includeDateInformation)
    {
        var page = await FindBestWikivoyagePageAsync(http, message, appContext);
        if (page is null) return string.Empty;

        var extract = PrepareWikipediaExtractForQuestion(page.Extract, message, includeDateInformation, 1200);
        if (string.IsNullOrWhiteSpace(extract)) return string.Empty;

        return BuildGuideLocalAnswerFromWikipedia(page.Title, extract, message);
    }

    private static async Task<string> BuildWikivoyageContextBlockAsync(HttpClient http, string? message, string? appContext, bool includeDateInformation)
    {
        var page = await FindBestWikivoyagePageAsync(http, message, appContext);
        if (page is null) return string.Empty;

        var extract = PrepareWikipediaExtractForQuestion(page.Extract, message, includeDateInformation, 3400);
        if (string.IsNullOrWhiteSpace(extract)) return string.Empty;

        var source = string.IsNullOrWhiteSpace(page.Url) ? page.Title : $"{page.Title} ({page.Url})";
        return "THÔNG TIN NỀN TỪ WIKIVOYAGE TIẾNG VIỆT. Đây là nguồn thay thế khi Wikipedia tiếng Việt không có thông tin phù hợp. Người dùng hỏi: " + (message ?? string.Empty).Trim() + ". Hãy dùng nội dung này để tự diễn giải đúng trọng tâm câu hỏi, không chép nguyên văn toàn đoạn và không mở đầu bằng Theo Wikivoyage hoặc Theo nguồn. Nếu người dùng hỏi một mảng riêng như văn hoá, lịch sử, địa danh/du lịch hoặc lễ hội thì chỉ tập trung đúng mảng đó. Nếu người dùng hỏi nhiều mảng cùng lúc, hãy trả lời đủ các mảng được hỏi theo nguồn. Nếu nội dung không đủ cho đúng khía cạnh người dùng hỏi, hãy nói chưa đủ thông tin từ nguồn thay thế. Nguồn tham khảo nội bộ: " + source + ". Nội dung: " + extract;
    }

    private static string BuildGuideTrustedLocalContextBlock(string? message, string? appContext, bool includeDateInformation)
    {
        var contextBlock = BuildAiContextBlock(appContext, "guide", includeDateInformation);
        if (!string.IsNullOrWhiteSpace(contextBlock))
        {
            return "THÔNG TIN NỀN TỪ DỮ LIỆU TRAVELWAI. Đây là nguồn thay thế khi Wikipedia tiếng Việt và Wikivoyage tiếng Việt không có thông tin phù hợp. Chỉ dùng đúng dữ liệu này để trả lời, không tự thêm chi tiết ngoài nguồn. " + contextBlock;
        }

        var trustedLocalReply = TryBuildGuideTrustedLocalReply(message, appContext, includeDateInformation);
        if (string.IsNullOrWhiteSpace(trustedLocalReply)) return string.Empty;

        return "THÔNG TIN NỀN TỪ DỮ LIỆU TRAVELWAI. Đây là nguồn thay thế khi Wikipedia tiếng Việt và Wikivoyage tiếng Việt không có thông tin phù hợp. Hãy diễn giải tự nhiên theo nội dung sau, không tự thêm chi tiết ngoài nguồn. Nội dung: " + trustedLocalReply;
    }

    private static async Task<WikipediaPageCandidate?> FindBestWikivoyagePageAsync(HttpClient http, string? message, string? appContext)
    {
        var queries = BuildWikipediaSearchQueries(message, appContext);
        if (queries.Count == 0) return null;

        WikipediaPageCandidate? best = null;

        foreach (var query in queries.Take(8))
        {
            var exactCandidate = await FetchWikivoyageTitleCandidateAsync(http, query);
            best = PickBetterWikipediaCandidate(best, exactCandidate);
            if (best is not null && best.Score >= 95) return best;

            var searchCandidate = await SearchWikivoyageCandidateAsync(http, query);
            best = PickBetterWikipediaCandidate(best, searchCandidate);
            if (best is not null && best.Score >= 95) return best;
        }

        return best is not null && best.Score >= 45 ? best : null;
    }

    private static async Task<WikipediaPageCandidate?> FetchWikivoyageTitleCandidateAsync(HttpClient http, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://vi.wikivoyage.org/w/api.php?action=query&redirects=1&prop=extracts|info&explaintext=1&exsectionformat=plain&inprop=url&format=json&origin=*&titles=" + Uri.EscapeDataString(query));
            request.Headers.TryAddWithoutValidation("User-Agent", "TravelwAI/1.0 (guide chatbot; vi.wikivoyage.org lookup)");

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var responseText = await response.Content.ReadAsStringAsync();
            var root = JsonNode.Parse(responseText);
            var pages = root?["query"]?["pages"]?.AsObject();
            if (pages is null || pages.Count == 0) return null;

            return pages
                .Select(item => BuildWikipediaCandidate(query, item.Value))
                .Where(item => item is not null)
                .OrderByDescending(item => item!.Score)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<WikipediaPageCandidate?> SearchWikivoyageCandidateAsync(HttpClient http, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://vi.wikivoyage.org/w/api.php?action=query&generator=search&gsrlimit=5&prop=extracts|info&explaintext=1&exsectionformat=plain&inprop=url&format=json&origin=*&gsrsearch=" + Uri.EscapeDataString(query));
            request.Headers.TryAddWithoutValidation("User-Agent", "TravelwAI/1.0 (guide chatbot; vi.wikivoyage.org lookup)");

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var responseText = await response.Content.ReadAsStringAsync();
            var root = JsonNode.Parse(responseText);
            var pages = root?["query"]?["pages"]?.AsObject();
            if (pages is null || pages.Count == 0) return null;

            return pages
                .Select(item => BuildWikipediaCandidate(query, item.Value))
                .Where(item => item is not null)
                .OrderByDescending(item => item!.Score)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<WikipediaPageCandidate?> FindBestWikipediaPageAsync(HttpClient http, string? message, string? appContext)
    {
        var queries = BuildWikipediaSearchQueries(message, appContext);
        if (queries.Count == 0) return null;

        WikipediaPageCandidate? best = null;

        foreach (var query in queries.Take(8))
        {
            var exactCandidate = await FetchWikipediaTitleCandidateAsync(http, query);
            best = PickBetterWikipediaCandidate(best, exactCandidate);
            if (best is not null && best.Score >= 95) return best;

            if (NormalizeVietnameseForSearch(message ?? string.Empty).Contains("le hoi", StringComparison.OrdinalIgnoreCase)
                && !NormalizeVietnameseForSearch(query).StartsWith("le hoi ", StringComparison.OrdinalIgnoreCase))
            {
                var festivalTitleCandidate = await FetchWikipediaTitleCandidateAsync(http, "Lễ hội " + query.Trim());
                best = PickBetterWikipediaCandidate(best, festivalTitleCandidate);
                if (best is not null && best.Score >= 95) return best;
            }

            var searchCandidate = await SearchWikipediaCandidateAsync(http, query);
            best = PickBetterWikipediaCandidate(best, searchCandidate);
            if (best is not null && best.Score >= 95) return best;
        }

        return best is not null && best.Score >= 45 ? best : null;
    }

    private static WikipediaPageCandidate? PickBetterWikipediaCandidate(WikipediaPageCandidate? current, WikipediaPageCandidate? next)
    {
        if (next is null) return current;
        if (current is null) return next;
        return next.Score > current.Score ? next : current;
    }

    private static async Task<WikipediaPageCandidate?> FetchWikipediaTitleCandidateAsync(HttpClient http, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://vi.wikipedia.org/w/api.php?action=query&redirects=1&prop=extracts|info&explaintext=1&exsectionformat=plain&inprop=url&format=json&origin=*&titles=" + Uri.EscapeDataString(query));
            request.Headers.TryAddWithoutValidation("User-Agent", "TravelwAI/1.0 (guide chatbot; vi.wikipedia.org lookup)");

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var responseText = await response.Content.ReadAsStringAsync();
            var root = JsonNode.Parse(responseText);
            var pages = root?["query"]?["pages"]?.AsObject();
            if (pages is null || pages.Count == 0) return null;

            return pages
                .Select(item => BuildWikipediaCandidate(query, item.Value))
                .Where(item => item is not null)
                .OrderByDescending(item => item!.Score)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<WikipediaPageCandidate?> SearchWikipediaCandidateAsync(HttpClient http, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://vi.wikipedia.org/w/api.php?action=query&generator=search&gsrlimit=5&prop=extracts|info&explaintext=1&exsectionformat=plain&inprop=url&format=json&origin=*&gsrsearch=" + Uri.EscapeDataString(query));
            request.Headers.TryAddWithoutValidation("User-Agent", "TravelwAI/1.0 (guide chatbot; vi.wikipedia.org lookup)");

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var responseText = await response.Content.ReadAsStringAsync();
            var root = JsonNode.Parse(responseText);
            var pages = root?["query"]?["pages"]?.AsObject();
            if (pages is null || pages.Count == 0) return null;

            return pages
                .Select(item => BuildWikipediaCandidate(query, item.Value))
                .Where(item => item is not null)
                .OrderByDescending(item => item!.Score)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static WikipediaPageCandidate? BuildWikipediaCandidate(string query, JsonNode? page)
    {
        var title = page?["title"]?.ToString()?.Trim() ?? string.Empty;
        var extract = page?["extract"]?.ToString()?.Trim() ?? string.Empty;
        var url = page?["fullurl"]?.ToString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(extract)) return null;
        if (title.StartsWith("Thảo luận:", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Thể loại:", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Wikipedia:", StringComparison.OrdinalIgnoreCase)) return null;

        var score = ScoreWikipediaResult(query, title, extract);
        return score <= 0 ? null : new WikipediaPageCandidate(title, extract, url, score);
    }

    private static string PrepareWikipediaExtractForReply(string extract, bool includeDateInformation, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(extract)) return string.Empty;

        extract = Regex.Replace(extract, @"\[[^\]]*\]", " ");
        extract = Regex.Replace(extract, @"\s+", " ").Trim();
        if (!includeDateInformation)
        {
            extract = RemoveGuideDateMentions(extract);
        }

        return LimitWikipediaText(extract, maxChars);
    }

    private static string PrepareWikipediaExtractForQuestion(string extract, string? message, bool includeDateInformation, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(extract)) return string.Empty;

        var cleaned = Regex.Replace(extract, @"\[[^\]]*\]", " ");
        cleaned = cleaned.Replace("\r\n", "\n").Replace('\r', '\n');
        cleaned = Regex.Replace(cleaned, @"[ \t]+", " ");
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n").Trim();
        if (!includeDateInformation)
        {
            cleaned = RemoveGuideDateMentions(cleaned);
        }

        var aspectKeywords = GetGuideAspectKeywords(message);
        if (aspectKeywords.Count > 0)
        {
            var selectedBlocks = SplitWikipediaExtractBlocks(cleaned)
                .Select((block, index) => new { Block = block, Index = index, Score = ScoreWikipediaBlockForQuestion(block, aspectKeywords) })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Index)
                .Take(5)
                .OrderBy(item => item.Index)
                .Select(item => item.Block)
                .ToList();

            var selected = string.Join(" ", selectedBlocks).Trim();
            if (Regex.Matches(selected, @"[\p{L}\p{N}]+").Count >= 25)
            {
                return LimitWikipediaText(selected, maxChars);
            }
        }

        return LimitWikipediaText(cleaned, maxChars);
    }

    private static IReadOnlyList<string> SplitWikipediaExtractBlocks(string text)
    {
        var blocks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return blocks;

        var current = new StringBuilder();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Length > 0)
                {
                    blocks.Add(current.ToString().Trim());
                    current.Clear();
                }
                continue;
            }

            var isHeading = Regex.IsMatch(line, @"^=+\s*[^=]+\s*=+$");
            if (isHeading && current.Length > 0)
            {
                blocks.Add(current.ToString().Trim());
                current.Clear();
            }

            if (current.Length > 0) current.Append(' ');
            current.Append(Regex.Replace(line, @"^=+\s*|\s*=+$", ""));
        }

        if (current.Length > 0) blocks.Add(current.ToString().Trim());

        if (blocks.Count >= 3)
        {
            return blocks.Where(block => Regex.Matches(block, @"[\p{L}\p{N}]+").Count >= 5).ToList();
        }

        var sentences = Regex.Split(Regex.Replace(text, @"\s+", " ").Trim(), @"(?<=[.!?])\s+")
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();
        blocks.Clear();
        for (var i = 0; i < sentences.Count; i += 3)
        {
            blocks.Add(string.Join(" ", sentences.Skip(i).Take(3)).Trim());
        }

        return blocks.Where(block => Regex.Matches(block, @"[\p{L}\p{N}]+").Count >= 5).ToList();
    }

    private static int ScoreWikipediaBlockForQuestion(string block, IReadOnlyList<string> aspectKeywords)
    {
        if (string.IsNullOrWhiteSpace(block)) return 0;
        var normalizedBlock = NormalizeVietnameseForSearch(block);
        if (LooksLikeMediaOrPressText(normalizedBlock)) return 0;
        var score = 0;

        foreach (var keyword in aspectKeywords)
        {
            if (normalizedBlock.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += keyword.Contains(" ", StringComparison.Ordinal) ? 25 : 12;
            }
        }

        if (Regex.IsMatch(block, @"^\s*(Văn hóa|Văn hoá|Lịch sử|Du lịch|Địa danh|Di tích|Danh lam|Lễ hội|Kinh tế|Xã hội)\b", RegexOptions.IgnoreCase))
        {
            score += 35;
        }

        return score;
    }

    private static IReadOnlyList<string> GetGuideAspectKeywords(string? message)
    {
        var normalized = NormalizeVietnameseForSearch(message ?? string.Empty);
        var keywords = new List<string>();

        if (Regex.IsMatch(normalized, @"\b(van hoa|phong tuc|tap quan|truyen thong|ban sac|am thuc|cong chieng|dan toc)\b", RegexOptions.IgnoreCase))
        {
            keywords.AddRange(new[] { "van hoa", "phong tuc", "tap quan", "truyen thong", "ban sac", "am thuc", "le hoi", "nghe thuat", "dan toc", "cong chieng", "gia rai", "jarai", "ba na", "bahnar", "tay nguyen" });
        }

        if (Regex.IsMatch(normalized, @"\b(lich su|nguon goc|hinh thanh|trieu dai|chien tranh|khoi nghia|thoi ky|thoi dai)\b", RegexOptions.IgnoreCase))
        {
            keywords.AddRange(new[] { "lich su", "hinh thanh", "nguon goc", "thoi ky", "chien tranh", "khoi nghia", "trieu dai", "sap nhap", "thanh lap" });
        }

        if (Regex.IsMatch(normalized, @"\b(dia danh|di tich|danh lam|diem den|du lich|tham quan|choi dau|co gi|noi bat)\b", RegexOptions.IgnoreCase))
        {
            keywords.AddRange(new[] { "du lich", "dia danh", "di tich", "danh lam", "thang canh", "diem den", "tham quan", "bao tang", "thac", "ho", "nui", "chua", "den", "khu du lich" });
        }

        if (Regex.IsMatch(normalized, @"\b(le hoi|ngay le|tet)\b", RegexOptions.IgnoreCase))
        {
            keywords.AddRange(new[] { "le hoi", "hoi", "nghi le", "nghi thuc", "tin nguong", "cau", "cung", "truyen thong" });
        }

        return keywords
            .Select(NormalizeVietnameseForSearch)
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DetectGuideQuestionAspect(string? message)
    {
        var normalized = NormalizeVietnameseForSearch(message ?? string.Empty);
        var hasCulture = Regex.IsMatch(normalized, @"\b(van hoa|phong tuc|tap quan|truyen thong|ban sac|am thuc|cong chieng|dan toc)\b", RegexOptions.IgnoreCase);
        var hasHistory = Regex.IsMatch(normalized, @"\b(lich su|nguon goc|hinh thanh|trieu dai|chien tranh|khoi nghia|thoi ky|thoi dai)\b", RegexOptions.IgnoreCase);
        var hasLandmark = Regex.IsMatch(normalized, @"\b(dia danh|di tich|danh lam|diem den|du lich|tham quan|choi dau|co gi|noi bat)\b", RegexOptions.IgnoreCase);
        var hasFestival = Regex.IsMatch(normalized, @"\b(le hoi|ngay le|tet)\b", RegexOptions.IgnoreCase);
        var count = new[] { hasCulture, hasHistory, hasLandmark, hasFestival }.Count(value => value);
        if (count >= 2) return "overview";
        if (hasCulture) return "culture";
        if (hasHistory) return "history";
        if (hasLandmark) return "landmark";
        if (hasFestival) return "festival";
        return "general";
    }


    private static string BuildGuideAspectInstruction(string? message)
    {
        return DetectGuideQuestionAspect(message) switch
        {
            "overview" => "Ý ĐỊNH CÂU HỎI: Người dùng hỏi nhiều mảng cùng lúc. Hãy trả lời đúng các mảng được hỏi như văn hoá, lịch sử, di tích/địa danh và lễ hội nếu nguồn có. Không chuyển sang báo chí, phát thanh, truyền hình, cơ quan truyền thông, đường sá hoặc chủ đề khác.",
            "culture" => "Ý ĐỊNH CÂU HỎI: Người dùng hỏi về văn hoá. Chỉ trả lời phần văn hoá, phong tục, dân tộc, truyền thống, lễ hội, ẩm thực hoặc bản sắc liên quan. Không lan sang lịch sử/địa lý nếu không cần.",
            "history" => "Ý ĐỊNH CÂU HỎI: Người dùng hỏi về lịch sử. Chỉ trả lời quá trình hình thành, mốc lịch sử, bối cảnh và sự kiện liên quan. Không biến câu trả lời thành giới thiệu du lịch chung.",
            "landmark" => "Ý ĐỊNH CÂU HỎI: Người dùng hỏi về địa danh/điểm đến. Chỉ trả lời các địa danh, di tích, danh lam, điểm tham quan hoặc nét du lịch liên quan. Không trả lời thành văn hoá chung.",
            "festival" => "Ý ĐỊNH CÂU HỎI: Người dùng hỏi về lễ hội/ngày lễ. Chỉ trả lời nguồn gốc, ý nghĩa, nghi thức và hoạt động chính. Nếu câu hỏi không hỏi thời gian thì không nêu ngày tháng.",
            _ => string.Empty
        };
    }

    private static string BuildGuideLocalAnswerFromWikipedia(string title, string extract, string? message)
    {
        var summary = TakeWikipediaSentences(extract, 4);
        if (string.IsNullOrWhiteSpace(summary)) return string.Empty;

        var lead = DetectGuideQuestionAspect(message) switch
        {
            "overview" => $"Về văn hoá, lịch sử, địa danh và lễ hội ở {title}, có thể tóm tắt các nét nổi bật sau.",
            "culture" => $"Văn hoá {title} có thể hiểu nổi bật qua các nét sau.",
            "history" => $"Lịch sử {title} có thể tóm tắt như sau.",
            "landmark" => $"Về địa danh và điểm đến ở {title}, có thể chú ý các nét sau.",
            "festival" => $"Về lễ hội/ngày lễ liên quan đến {title}, có thể hiểu như sau.",
            _ => $"{title} có thể tóm tắt như sau."
        };

        return (lead + " " + summary).Trim();
    }

    private static string TakeWikipediaSentences(string text, int maxSentences)
    {
        if (string.IsNullOrWhiteSpace(text) || maxSentences <= 0) return string.Empty;
        var sentences = Regex.Split(Regex.Replace(text, @"\s+", " ").Trim(), @"(?<=[.!?])\s+")
            .Select(sentence => sentence.Trim())
            .Where(sentence => Regex.Matches(sentence, @"[\p{L}\p{N}]+").Count >= 4)
            .Take(maxSentences)
            .ToList();

        return string.Join(" ", sentences).Trim();
    }

    private static string LimitWikipediaText(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var cleaned = Regex.Replace(text, @"\s+", " ").Trim();
        if (maxChars <= 0 || cleaned.Length <= maxChars) return cleaned;

        cleaned = cleaned[..maxChars].Trim();
        var lastSentence = cleaned.LastIndexOf(".", StringComparison.Ordinal);
        if (lastSentence > 160) cleaned = cleaned[..(lastSentence + 1)].Trim();

        return cleaned;
    }

    private static int ScoreWikipediaResult(string query, string? title, string extract)
    {
        var normalizedQuery = NormalizeVietnameseForSearch(query ?? string.Empty);
        var normalizedTitle = NormalizeVietnameseForSearch(title ?? string.Empty);
        var normalizedExtract = NormalizeVietnameseForSearch(extract ?? string.Empty);
        var normalizedSource = normalizedTitle + " " + normalizedExtract;

        if (string.IsNullOrWhiteSpace(normalizedQuery) || string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedExtract)) return 0;
        if (IsGenericWikipediaQuery(normalizedQuery)) return 0;
        if (LooksLikeTransportInfrastructureArticle(normalizedTitle, normalizedExtract) && !QueryAsksTransportInfrastructure(normalizedQuery)) return 0;
        if (LooksLikeMediaAgencyArticle(normalizedTitle, normalizedExtract) && !QueryAsksMediaAgency(normalizedQuery)) return 0;

        var queryTokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim(',', '.', '-', ':', ';', '(', ')'))
            .Where(token => token.Length >= 3)
            .Where(token => !GuideWikipediaStopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        if (queryTokens.Count == 0) return 0;

        if (normalizedTitle.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)) return 100;
        if (normalizedTitle.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) || normalizedQuery.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase))
        {
            if (queryTokens.Count <= 2 || HasKnownSpecificGuideTopic(normalizedQuery)) return 95;
        }

        var titleHits = queryTokens.Count(token => normalizedTitle.Contains(token, StringComparison.OrdinalIgnoreCase));
        var sourceHits = queryTokens.Count(token => normalizedSource.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (sourceHits == 0) return 0;
        if (queryTokens.Count <= 2 && titleHits == 0 && !HasKnownSpecificGuideTopic(normalizedQuery)) return 0;
        if (queryTokens.Count >= 3 && sourceHits < Math.Min(3, queryTokens.Count) && titleHits == 0) return 0;

        var score = sourceHits * 15 + titleHits * 20;
        if (sourceHits == queryTokens.Count) score += 25;
        if (titleHits == queryTokens.Count) score += 25;
        if (queryTokens.Count <= 2 && sourceHits == queryTokens.Count) score += 20;

        return score;
    }

    private static bool IsGenericWikipediaQuery(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery)) return true;
        var broadTopics = new[] { "van hoa", "lich su", "di tich", "le hoi", "dia danh", "danh lam", "lang nghe", "di san" };
        var broadCount = broadTopics.Count(topic => normalizedQuery.Contains(topic, StringComparison.OrdinalIgnoreCase));
        if (broadCount < 2) return false;
        if (HasKnownSpecificGuideTopic(normalizedQuery)) return false;

        var tokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3)
            .Where(token => !GuideWikipediaStopWords.Contains(token))
            .ToList();

        return tokens.Count == 0;
    }

    private static bool LooksLikeMediaAgencyArticle(string normalizedTitle, string normalizedExtract)
    {
        return LooksLikeMediaOrPressText((normalizedTitle + " " + normalizedExtract).Trim());
    }

    private static bool LooksLikeMediaOrPressText(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText)) return false;

        var mediaTerms = new[]
        {
            "bao va phat thanh", "phat thanh va truyen hinh", "phat thanh truyen hinh", "dai phat thanh",
            "truyen hinh", "bao quang", "bao gia lai", "bao quang ngai", "trung tam truyen thong",
            "co quan truyen thong", "co quan bao chi", "bao chi", "kenh truyen hinh", "dai truyen hinh"
        };

        return mediaTerms.Any(term => normalizedText.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool QueryAsksMediaAgency(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery)) return false;

        var mediaTerms = new[]
        {
            "bao", "phat thanh", "truyen hinh", "dai phat thanh", "bao chi", "trung tam truyen thong", "co quan truyen thong", "kenh truyen hinh"
        };

        return mediaTerms.Any(term => normalizedQuery.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeTransportInfrastructureArticle(string normalizedTitle, string normalizedExtract)
    {
        var source = (normalizedTitle + " " + normalizedExtract).Trim();
        if (string.IsNullOrWhiteSpace(source)) return false;

        var transportTerms = new[]
        {
            "duong cao toc", "cao toc", "quoc lo", "duong quoc lo", "duong sat", "tuyen duong sat", "tuyen duong bo"
        };

        return transportTerms.Any(term => source.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool QueryAsksTransportInfrastructure(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery)) return false;

        var transportTerms = new[]
        {
            "duong cao toc", "cao toc", "quoc lo", "duong quoc lo", "duong sat", "tuyen duong", "san bay", "cang", "cau"
        };

        return transportTerms.Any(term => normalizedQuery.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWikipediaResultRelevant(string query, string? title, string extract)
    {
        return ScoreWikipediaResult(query, title, extract) >= 45;
    }

    private static readonly HashSet<string> GuideWikipediaStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "hay", "cho", "toi", "biet", "ve", "la", "gi", "ai", "dau", "khi", "nao", "nhu", "the",
        "gioi", "thieu", "kham", "pha", "nghia", "nguon", "goc", "lich", "su", "van", "hoa",
        "huong", "dan", "vien", "travelwinne", "travelwai", "wiki", "wikipedia", "noi", "thong", "tin",
        "giai", "thich", "le", "hoi", "tom", "tat", "ke", "chuyen", "ro", "phan", "tich",
        "thoi", "gian", "to", "chuc", "dien", "ra", "ngay", "thang", "nam", "am", "duong",
        "bao", "gio", "luc", "mung", "may", "xay", "dung", "nguoi", "dan", "tai", "o",
        "di", "dia", "danh", "tich", "noi", "bat", "tieu", "bieu", "kieu", "trai", "nghiem", "mien", "cac", "nhung", "mot", "vung", "vung mien", "du", "lich", "va", "và"
    };

    private static IEnumerable<string> BuildProvinceWikipediaQueries(string provinceName)
    {
        if (string.IsNullOrWhiteSpace(provinceName)) yield break;

        var name = Regex.Replace(provinceName.Trim(), @"\s+", " ");
        var normalized = NormalizeVietnameseForSearch(name);

        if (normalized.StartsWith("thanh pho ", StringComparison.OrdinalIgnoreCase))
        {
            yield return name;
            var shortName = Regex.Replace(name, @"^Thành\s+phố\s+", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(shortName)) yield return shortName;
            yield break;
        }

        if (normalized.StartsWith("tinh ", StringComparison.OrdinalIgnoreCase))
        {
            var shortName = Regex.Replace(name, @"^Tỉnh\s+", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(shortName)) yield return shortName;
            yield return name;
            yield break;
        }

        yield return name;
        yield return "Tỉnh " + name;
    }

    private static IReadOnlyList<string> BuildWikipediaSearchQueries(string? message, string? appContext)
    {
        var source = !string.IsNullOrWhiteSpace(message) ? message! : appContext ?? string.Empty;
        var queries = new List<string>();
        if (string.IsNullOrWhiteSpace(source)) return queries;

        source = Regex.Replace(source, @"https?://\S+", " ", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"[^\p{L}\p{M}\p{N}\s,.'\-""“”]", " ");
        source = Regex.Replace(source, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(source)) return queries;

        var normalizedSource = NormalizeVietnameseForSearch(source);
        if (IsGenericGuideExplorationMessage(source)) return queries;

        void AddQuery(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var cleanedValue = CleanWikipediaCoreQuery(value);
            if (string.IsNullOrWhiteSpace(cleanedValue)) return;
            if (cleanedValue.Length > 80) cleanedValue = cleanedValue[..80].Trim();
            if (cleanedValue.Length < 2) return;
            if (!IsUsefulWikipediaSearchQuery(cleanedValue)) return;
            if (!queries.Any(item => string.Equals(NormalizeVietnameseForSearch(item), NormalizeVietnameseForSearch(cleanedValue), StringComparison.OrdinalIgnoreCase)))
            {
                queries.Add(cleanedValue);
            }
        }

        // Luôn ưu tiên tỉnh/thành nếu người dùng nhắc tỉnh/thành. Ví dụ:
        // "văn hoá Gia Lai", "lịch sử Gia Lai", "di tích Quảng Ngãi" chỉ tra lõi "Gia Lai" hoặc "Quảng Ngãi".
        var provinceNames = FindProvinceNamesInText(source).ToList();
        foreach (var provinceName in provinceNames)
        {
            foreach (var provinceQuery in BuildProvinceWikipediaQueries(provinceName))
            {
                AddQuery(provinceQuery);
            }
        }

        if (provinceNames.Count > 0)
        {
            return queries.Take(10).ToList();
        }

        // Ưu tiên tên được đặt trong ngoặc kép vì đó thường là từ khoá cốt lõi.
        var quoted = Regex.Matches(source, @"[""“”']([^""“”']{3,80})[""“”']")
            .Cast<Match>()
            .Select(match => match.Groups[1].Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        AddQuery(quoted);

        foreach (var knownTopic in BuildKnownGuideTopicQueries(normalizedSource))
        {
            AddQuery(knownTopic);
        }

        var coreTopic = ExtractGuideWikipediaCoreTopic(source);
        var normalizedCore = NormalizeVietnameseForSearch(coreTopic);
        if (!string.IsNullOrWhiteSpace(coreTopic))
        {
            if (normalizedSource.Contains("le hoi", StringComparison.OrdinalIgnoreCase)
                && !normalizedCore.StartsWith("le hoi ", StringComparison.OrdinalIgnoreCase))
            {
                AddQuery("Lễ hội " + coreTopic);
            }

            AddQuery(coreTopic);
        }

        return queries.Take(10).ToList();
    }

    private static IEnumerable<string> BuildKnownGuideTopicQueries(string normalizedSource)
    {
        if (string.IsNullOrWhiteSpace(normalizedSource)) yield break;

        var knownTopics = new (string Key, string Query)[]
        {
            ("hoang thanh thang long", "Hoàng thành Thăng Long"),
            ("van mieu quoc tu giam", "Văn Miếu - Quốc Tử Giám"),
            ("quoc tu giam", "Văn Miếu - Quốc Tử Giám"),
            ("van mieu", "Văn Miếu - Quốc Tử Giám"),
            ("co loa", "Cổ Loa"),
            ("ho hoan kiem", "Hồ Hoàn Kiếm"),
            ("ho guom", "Hồ Hoàn Kiếm"),
            ("hoi an", "Hội An"),
            ("ha long", "Vịnh Hạ Long"),
            ("vinh ha long", "Vịnh Hạ Long"),
            ("festival hue", "Festival Huế"),
            ("co do hue", "Quần thể di tích Cố đô Huế"),
            ("gau tao", "Gầu Tào"),
            ("hoi lim", "Hội Lim"),
            ("gio to hung vuong", "Giỗ Tổ Hùng Vương"),
            ("den hung", "Đền Hùng"),
            ("tet nguyen dan", "Tết Nguyên Đán"),
            ("trung thu", "Tết Trung thu"),
            ("quoc khanh", "Ngày Quốc khánh Việt Nam"),
            ("ok om bok", "Ok Om Bok"),
            ("kate", "Lễ hội Katê"),
            ("long tong", "Lồng tồng"),
            ("nong tong", "Lồng tồng"),
            ("rong pooc", "Roóng Poọc"),
            ("roong pooc", "Roóng Poọc"),
            ("dan ca xoan", "Hát xoan"),
            ("hat xoan", "Hát xoan"),
            ("nha tho duc ba", "Nhà thờ chính tòa Đức Bà Sài Gòn"),
            ("dinh doc lap", "Dinh Độc Lập"),
            ("dia dao cu chi", "Địa đạo Củ Chi"),
            ("thanh dia my son", "Thánh địa Mỹ Sơn"),
            ("my son", "Thánh địa Mỹ Sơn"),
            ("thanh nha ho", "Thành nhà Hồ"),
            ("khong gian van hoa cong chieng tay nguyen", "Không gian văn hóa Cồng Chiêng Tây Nguyên"),
            ("cong chieng tay nguyen", "Không gian văn hóa Cồng Chiêng Tây Nguyên"),
            ("viet nam", "Việt Nam")
        };

        foreach (var item in knownTopics)
        {
            if (normalizedSource.Contains(item.Key, StringComparison.OrdinalIgnoreCase))
            {
                yield return item.Query;
            }
        }
    }

    private static string ExtractGuideWikipediaCoreTopic(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        var cleaned = source;
        cleaned = Regex.Replace(cleaned, @"https?://\S+", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"[""“”']", " ");
        cleaned = Regex.Replace(cleaned, @"\b(hãy|hay|vui\s*lòng|cho\s+tôi|cho\s+toi|giúp\s+tôi|giup\s+toi|mình\s+muốn|minh\s+muon|tôi\s+muốn|toi\s+muon|bạn\s+có\s+thể|ban\s+co\s+the)\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\b(khám\s+phá|kham\s+pha|tìm\s+hiểu|tim\s+hieu|giới\s+thiệu|gioi\s+thieu|thuyết\s+minh|thuyet\s+minh|giải\s+thích|giai\s+thich|kể\s+chuyện|ke\s+chuyen|kể\s+về|ke\s+ve|nói\s+về|noi\s+ve|phân\s+tích|phan\s+tich|tóm\s+tắt|tom\s+tat)\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\b(văn\s+hoá|văn\s+hóa|van\s+hoa|lịch\s+sử|lich\s+su|địa\s+danh|dia\s+danh|di\s+tích|di\s+tich|danh\s+lam|ngày\s+lễ|ngay\s+le|du\s+lịch|du\s+lich)\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\b(lễ\s+hội|le\s+hoi)\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\b(nổi\s+bật|noi\s+bat|tiêu\s+biểu|tieu\s+bieu|ở\s+đâu|o\s+dau|là\s+gì|la\s+gi|có\s+gì|co\s+gi|như\s+thế\s+nào|nhu\s+the\s+nao)\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\b(của|cua|về|ve|ở|o|tại|tai|trong|và|va|hoặc|hoac|các|cac|những|nhung|một|mot|này|nay|kia|đó|do|cho|toi|tôi|bạn|ban|mình|minh)\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{M}\p{N}\s.'\-]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', ',', '.', '-', ':', ';', '\'', '"');

        return CleanWikipediaCoreQuery(cleaned);
    }

    private static string CleanWikipediaCoreQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var cleaned = Regex.Replace(value, @"\s+", " ").Trim(' ', ',', '.', '-', ':', ';', '\'', '"', '“', '”');
        cleaned = Regex.Replace(cleaned, @"^(về|ve|ở|o|tại|tai|của|cua)\s+", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+(là\s+gì|la\s+gi|ở\s+đâu|o\s+dau)$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', ',', '.', '-', ':', ';', '\'', '"', '“', '”');

        var normalized = NormalizeVietnameseForSearch(cleaned);
        var blockedWholeQueries = new[]
        {
            "van hoa", "lich su", "dia danh", "di tich", "danh lam", "le hoi", "ngay le", "du lich", "noi bat", "tieu bieu",
            "kham pha", "tim hieu", "gioi thieu", "giai thich", "thuyet minh", "ke chuyen"
        };
        if (blockedWholeQueries.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return string.Empty;

        return cleaned;
    }



    private static bool IsUsefulWikipediaSearchQuery(string? query)
    {
        var normalized = NormalizeVietnameseForSearch(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (IsGenericGuideExplorationMessage(query)) return false;

        var meaningfulTokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim(',', '.', '-', ':', ';', '(', ')'))
            .Where(token => token.Length >= 2)
            .Where(token => !GuideWikipediaStopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return meaningfulTokens.Count > 0 || HasKnownSpecificGuideTopic(normalized);
    }

    private static string BuildWikipediaSearchQuery(string? message, string? appContext)
    {
        return BuildWikipediaSearchQueries(message, appContext).FirstOrDefault() ?? string.Empty;
    }

    private static string TryBuildGuideTrustedLocalReply(string? message, string? appContext, bool includeDateInformation)
    {
        var normalized = NormalizeVietnameseForSearch((message ?? string.Empty) + " " + (appContext ?? string.Empty));
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        if (normalized.Contains("gau tao", StringComparison.OrdinalIgnoreCase))
        {
            var reply = "Lễ hội Gầu Tào là lễ hội của đồng bào H'Mông, gắn với việc cầu phúc hoặc cầu mệnh. Gia đình xin mở hội thường dựng cây nêu ở nơi cao, làm lễ cúng tổ tiên và thần linh để cầu con cái, sức khỏe, bình an, mùa màng và vật nuôi tốt lành. Phần hội là dịp cộng đồng gặp gỡ, vui chơi, hát giao duyên, múa khèn và giữ gìn bản sắc Mông.";
            if (includeDateInformation)
            {
                reply += " Lễ hội thường gắn với dịp đầu xuân sau Tết, nhưng thời gian cụ thể có thể khác theo từng địa phương.";
            }
            return reply;
        }

        return string.Empty;
    }

    private static string KeepOnlyCompletedSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var cleaned = text.Trim();
        var sentenceEnds = Regex.Matches(cleaned, @"[.](?=\s|$)").Cast<Match>().ToList();
        if (sentenceEnds.Count == 0) return string.Empty;

        var lastSentenceEnd = sentenceEnds[^1];
        return cleaned[..(lastSentenceEnd.Index + 1)].Trim();
    }

    private static string CleanSimpleChatbotReply(string text, int maxWords, bool dropUnfinishedTail = false)
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
        var wasWordLimited = false;
        if (wordMatches.Count > maxWords)
        {
            wasWordLimited = true;
            var lastWord = wordMatches[maxWords - 1];
            cleaned = cleaned[..(lastWord.Index + lastWord.Length)].Trim();
        }

        cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{N})/.-]+$", "").Trim();

        if (dropUnfinishedTail || wasWordLimited)
        {
            cleaned = KeepOnlyCompletedSentences(cleaned);
        }

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
            @"^\s*theo\s+(?:dữ\s+liệu\s+ứng\s+dụng|dữ\s+liệu|nguồn|wikipedia(?:\s+tiếng\s+việt)?|wikivoyage(?:\s+tiếng\s+việt)?|thông\s+tin\s+nền)(?:\s+[^,.:\n]{0,80})?\s*[:,.-]?\s*",
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
