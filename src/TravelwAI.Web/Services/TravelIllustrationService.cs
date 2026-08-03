using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using TravelwAI.Models.Common;
using TravelwAI.Web.Models;

namespace TravelwAI.Web.Services;

/// <summary>
/// Finds reusable travel illustrations from Wikimedia Commons for questions about
/// Vietnamese places, destinations, festivals and cuisine. Failures are intentionally
/// non-fatal so the chatbot can always return its text answer.
/// </summary>
public sealed partial class TravelIllustrationService
{
    private const int DesiredImageCount = 4;
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TravelIllustrationService> _logger;

    private static readonly string[] DestinationAliases =
    {
        "Đà Lạt", "Sa Pa", "Sapa", "Hạ Long", "Vịnh Hạ Long", "Hội An", "Phú Quốc",
        "Nha Trang", "Vũng Tàu", "Mũi Né", "Tràng An", "Bà Nà Hills", "Fansipan",
        "Côn Đảo", "Cát Bà", "Tam Đảo", "Mộc Châu", "Pù Luông", "Ninh Bình",
        "Phong Nha", "Kẻ Bàng", "Mỹ Sơn", "Cù Lao Chàm", "Lý Sơn", "Tây Bắc",
        "Đồng bằng sông Cửu Long", "Mekong Delta",
        "Nhà tù Phú Lợi", "Khu di tích Nhà tù Phú Lợi"
    };

    private static readonly string[] CuisineWords =
    {
        "ẩm thực", "đặc sản", "món ăn", "ăn gì", "đồ ăn", "cuisine", "food", "dish",
        "specialty", "restaurant", "phở", "bún", "bánh", "cơm", "chè", "nem", "chả",
        "mì", "hủ tiếu", "cao lầu", "bò kho", "gỏi", "lẩu", "coffee", "cà phê"
    };

    private static readonly string[] FestivalWords =
    {
        "lễ hội", "hội làng", "festival", "carnival", "celebration", "tết", "tet holiday"
    };

    private static readonly string[] PlaceWords =
    {
        "tỉnh", "thành phố", "địa danh", "điểm đến", "du lịch", "tham quan", "danh lam",
        "thắng cảnh", "di tích", "khu di tích", "di sản", "landmark", "destination",
        "attraction", "province", "city", "travel", "tourism", "đảo", "biển", "bãi biển",
        "núi", "hang", "động", "chùa", "đền", "tháp", "phố cổ", "vườn quốc gia",
        "công viên", "nhà thờ", "bảo tàng", "nhà tù", "nhà lao", "trại giam", "cầu",
        "lăng", "dinh", "cung", "thành", "pháo đài", "tượng đài", "quảng trường", "chợ",
        "làng", "hồ", "thác", "suối", "đèo", "bến", "khu du lịch", "khu sinh thái"
    };

    // Chỉ loại bỏ các từ mô tả chung. Những danh từ riêng như "nhà tù", "chùa",
    // "cầu"... được giữ lại vì chúng giúp Wikimedia tìm đúng công trình hơn.
    private static readonly string[] GenericPlaceWords =
    {
        "tỉnh", "thành phố", "địa danh", "điểm đến", "du lịch", "tham quan", "danh lam",
        "thắng cảnh", "landmark", "destination", "attraction", "province", "city",
        "travel", "tourism", "có gì", "địa điểm"
    };


    private static readonly string[] ExcludedImageTitleWords =
    {
        "map", "locator", "flag", "logo", "icon", "coat of arms", "emblem", "seal",
        "diagram", "route", "administrative", "blank map", "wikidata", "commons-logo"
    };

    private static readonly string[] SearchNoisePhrases =
    {
        "hãy cho tôi biết", "cho tôi biết", "hãy giới thiệu", "giới thiệu cho tôi", "giới thiệu",
        "tìm hiểu về", "thông tin về", "kể về", "nói về", "có gì nổi tiếng", "có gì đẹp",
        "có gì hay", "có những gì", "nên đi đâu", "ở đâu", "là gì", "như thế nào",
        "what can i do in", "tell me about", "show me", "information about", "what is",
        "where is", "best places in", "things to do in"
    };

    private static readonly IReadOnlyDictionary<string, string[]> CuratedCommonsFiles =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["nha tu phu loi"] = new[]
            {
                "Nhà tù Phú Lợi, lối vào di tích nhà tù.jpg",
                "Cổng vào Nhà tù Phú Lợi.JPG",
                "Khu nhà giam ở Nhà tù Phú Lợi.JPG",
                "Phong biet giam nhà tù Phú Lợi.JPG"
            },
            ["khu di tich nha tu phu loi"] = new[]
            {
                "Nhà tù Phú Lợi, lối vào di tích nhà tù.jpg",
                "Cổng vào Nhà tù Phú Lợi.JPG",
                "Khu nhà giam ở Nhà tù Phú Lợi.JPG",
                "Tượng đài ở nhà tù Phú Lợi ở Bình Dương.jpg"
            }
        };

    private static readonly IReadOnlyDictionary<string, string> CommonTypingCorrections =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nahf"] = "nhà",
            ["nhaf"] = "nhà",
            ["dija"] = "địa",
            ["diaj"] = "địa",
            ["leex"] = "lễ",
            ["hooji"] = "hội",
            ["amr"] = "ẩm",
            ["thuwcj"] = "thực"
        };

    public TravelIllustrationService(
        HttpClient httpClient,
        IMemoryCache cache,
        ILogger<TravelIllustrationService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    public Task<IReadOnlyList<TravelIllustration>> FindForQuestionAsync(
        string? message,
        CancellationToken cancellationToken)
    {
        var request = BuildSearchRequest(message);
        if (request is null)
            return Task.FromResult<IReadOnlyList<TravelIllustration>>(Array.Empty<TravelIllustration>());

        var cacheKey = $"travel-illustrations:{request.Topic}:{NormalizeForComparison(request.Subject)}";
        return _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
            entry.Size = 1;
            return await SearchCommonsAsync(request, cancellationToken);
        })!;
    }

    private async Task<IReadOnlyList<TravelIllustration>> SearchCommonsAsync(
        IllustrationSearchRequest request,
        CancellationToken cancellationToken)
    {
        var results = new List<TravelIllustration>(DesiredImageCount);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var image in BuildCuratedFallback(request.Subject))
        {
            if (!seenUrls.Add(image.OriginalUrl.Length > 0 ? image.OriginalUrl : image.Url)) continue;
            results.Add(image);
            if (results.Count >= DesiredImageCount) return results;
        }

        try
        {
            foreach (var query in BuildQueries(request))
            {
                var batch = await SearchCommonsQueryAsync(query, cancellationToken);
                foreach (var image in batch)
                {
                    if (!seenUrls.Add(image.OriginalUrl.Length > 0 ? image.OriginalUrl : image.Url)) continue;
                    results.Add(image);
                    if (results.Count >= DesiredImageCount) return results;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                _logger.LogWarning(ex, "Wikimedia Commons phản hồi quá lâu cho truy vấn ảnh {Subject}", request.Subject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể tải ảnh minh hoạ Wikimedia Commons cho truy vấn {Subject}", request.Subject);
        }

        return results;
    }

    private async Task<IReadOnlyList<TravelIllustration>> SearchCommonsQueryAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["action"] = "query",
            ["format"] = "json",
            ["formatversion"] = "2",
            ["generator"] = "search",
            ["gsrsearch"] = query,
            ["gsrnamespace"] = "6",
            ["gsrlimit"] = "16",
            ["prop"] = "imageinfo|info",
            ["inprop"] = "url",
            ["iiprop"] = "url|size|mime|extmetadata",
            ["iiurlwidth"] = "900"
        };
        var queryString = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        using var response = await _httpClient.GetAsync($"w/api.php?{queryString}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Wikimedia Commons trả về HTTP {StatusCode} cho truy vấn {Query}", response.StatusCode, query);
            return Array.Empty<TravelIllustration>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("query", out var queryElement)
            || !queryElement.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TravelIllustration>();
        }

        var images = new List<TravelIllustration>();
        foreach (var page in pages.EnumerateArray().OrderBy(page =>
        {
            var index = GetInt(page, "index");
            return index > 0 ? index : int.MaxValue;
        }))
        {
            if (!page.TryGetProperty("imageinfo", out var imageInfoArray)
                || imageInfoArray.ValueKind != JsonValueKind.Array
                || imageInfoArray.GetArrayLength() == 0)
            {
                continue;
            }

            var imageInfo = imageInfoArray[0];
            var mime = GetString(imageInfo, "mime");
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || mime.Contains("svg", StringComparison.OrdinalIgnoreCase)
                || mime.Contains("gif", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var width = GetInt(imageInfo, "width");
            var height = GetInt(imageInfo, "height");
            if (width < 450 || height < 300) continue;
            var ratio = width / (double)Math.Max(1, height);
            if (ratio is > 3.4 or < 0.28) continue;

            var originalUrl = GetString(imageInfo, "url");
            var thumbnailUrl = GetString(imageInfo, "thumburl");
            var displayUrl = thumbnailUrl.Length > 0 ? thumbnailUrl : originalUrl;
            if (!IsHttpsUrl(displayUrl) || !IsHttpsUrl(originalUrl)) continue;

            var title = GetString(page, "title");
            if (IsExcludedImageTitle(title)) continue;
            if (title.StartsWith("File:", StringComparison.OrdinalIgnoreCase)) title = title[5..];
            title = Path.GetFileNameWithoutExtension(title.Replace('_', ' ')).Trim();

            var metadata = imageInfo.TryGetProperty("extmetadata", out var metadataElement)
                ? metadataElement
                : default;
            var description = ReadMetadata(metadata, "ImageDescription");
            var author = ReadMetadata(metadata, "Artist");
            var licenseName = ReadMetadata(metadata, "LicenseShortName");
            var licenseUrl = ReadMetadata(metadata, "LicenseUrl");
            var sourceUrl = GetString(page, "fullurl");

            images.Add(new TravelIllustration
            {
                Url = displayUrl,
                OriginalUrl = originalUrl,
                SourceUrl = IsHttpsUrl(sourceUrl) ? sourceUrl : originalUrl,
                ContentType = mime,
                Title = title.Length > 0 ? title : "Ảnh minh hoạ",
                Alt = description.Length > 0 ? description : (title.Length > 0 ? title : $"Ảnh minh hoạ cho {query}"),
                Author = author,
                LicenseName = licenseName,
                LicenseUrl = IsHttpsUrl(licenseUrl) ? licenseUrl : string.Empty,
                Width = width,
                Height = height
            });
        }

        return images;
    }

    private static IllustrationSearchRequest? BuildSearchRequest(string? message)
    {
        var text = NormalizeCommonTypingErrors(CollapseWhitespace(message));
        if (text.Length < 2) return null;

        var topic = DetectTopic(text);
        var subject = FindKnownPlace(text);
        if (topic == IllustrationTopic.None && subject.Length == 0) return null;

        if (subject.Length == 0) subject = ExtractSubject(text, topic);
        if (subject.Length < 2) return null;
        if (topic == IllustrationTopic.None) topic = IllustrationTopic.Destination;

        return new IllustrationSearchRequest(subject, topic);
    }

    private static IllustrationTopic DetectTopic(string text)
    {
        if (ContainsAny(text, CuisineWords)) return IllustrationTopic.Cuisine;
        if (ContainsAny(text, FestivalWords)) return IllustrationTopic.Festival;
        if (ContainsAny(text, PlaceWords)) return IllustrationTopic.Destination;
        return IllustrationTopic.None;
    }

    private static string FindKnownPlace(string text)
    {
        var normalizedText = NormalizeForComparison(text);
        var candidates = VietnamesePlaceName.AllKnownNames.Concat(DestinationAliases)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length);

        foreach (var candidate in candidates)
        {
            var normalizedCandidate = NormalizeForComparison(candidate);
            if (normalizedCandidate.Length > 1 && ContainsNormalizedPhrase(normalizedText, normalizedCandidate))
                return candidate;
        }

        return string.Empty;
    }

    private static string ExtractSubject(string text, IllustrationTopic topic)
    {
        var value = text;
        foreach (var phrase in SearchNoisePhrases)
            value = ReplaceInvariant(value, phrase, " ");

        var topicWords = topic switch
        {
            IllustrationTopic.Cuisine => CuisineWords,
            IllustrationTopic.Festival => FestivalWords,
            _ => GenericPlaceWords
        };
        foreach (var phrase in topicWords)
            value = ReplaceInvariant(value, phrase, " ");

        value = QuestionPunctuationRegex().Replace(value, " ");
        value = StandaloneStopWordRegex().Replace(value, " ");
        value = CollapseWhitespace(value).Trim(' ', '-', ':', ';', ',');
        if (value.Length > 90) value = value[..90].Trim();
        return value.Length > 0 ? value : text[..Math.Min(text.Length, 90)].Trim();
    }

    private static IReadOnlyList<string> BuildQueries(IllustrationSearchRequest request)
    {
        var topicEnglish = request.Topic switch
        {
            IllustrationTopic.Cuisine => "food cuisine",
            IllustrationTopic.Festival => "festival",
            _ => GetDestinationSearchHint(request.Subject)
        };
        var genericFallback = request.Topic switch
        {
            IllustrationTopic.Cuisine => "Vietnamese cuisine food",
            IllustrationTopic.Festival => "Vietnam festival traditional",
            _ => "Vietnam tourist attraction landscape"
        };

        var asciiSubject = VietnamesePlaceName.ToAscii(request.Subject);
        return new[]
        {
            $"intitle:\"{request.Subject}\"",
            $"\"{request.Subject}\"",
            request.Subject,
            $"{request.Subject} Vietnam",
            $"intitle:\"{asciiSubject}\" {topicEnglish}",
            $"{asciiSubject} {topicEnglish} Vietnam",
            $"{request.Subject} {topicEnglish}",
            genericFallback
        }
        .Select(CollapseWhitespace)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static IReadOnlyList<TravelIllustration> BuildCuratedFallback(string subject)
    {
        var normalized = NormalizeForComparison(subject);
        if (!CuratedCommonsFiles.TryGetValue(normalized, out var files) || files.Length == 0)
            return Array.Empty<TravelIllustration>();

        return files.Take(DesiredImageCount).Select(fileName =>
        {
            var encodedFile = Uri.EscapeDataString(fileName.Replace(' ', '_'));
            var title = Path.GetFileNameWithoutExtension(fileName.Replace('_', ' ')).Trim();
            var redirectUrl = $"https://commons.wikimedia.org/wiki/Special:Redirect/file/{encodedFile}?width=900";
            return new TravelIllustration
            {
                Url = redirectUrl,
                OriginalUrl = redirectUrl,
                SourceUrl = $"https://commons.wikimedia.org/wiki/File:{encodedFile}",
                ContentType = fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg",
                Title = title.Length > 0 ? title : "Nhà tù Phú Lợi",
                Alt = title.Length > 0 ? title : "Ảnh minh hoạ Nhà tù Phú Lợi",
                Author = string.Empty,
                LicenseName = string.Empty,
                LicenseUrl = string.Empty
            };
        }).ToArray();
    }

    private static string GetDestinationSearchHint(string subject)
    {
        var normalized = NormalizeForComparison(subject);
        if (ContainsNormalizedPhrase(normalized, "nha tu")
            || ContainsNormalizedPhrase(normalized, "nha lao")
            || ContainsNormalizedPhrase(normalized, "trai giam"))
            return "prison historical site";
        if (ContainsNormalizedPhrase(normalized, "chua") || ContainsNormalizedPhrase(normalized, "den"))
            return "temple pagoda";
        if (ContainsNormalizedPhrase(normalized, "nha tho")) return "church";
        if (ContainsNormalizedPhrase(normalized, "bao tang")) return "museum";
        if (ContainsNormalizedPhrase(normalized, "cau")) return "bridge landmark";
        if (ContainsNormalizedPhrase(normalized, "thac")) return "waterfall";
        if (ContainsNormalizedPhrase(normalized, "hang") || ContainsNormalizedPhrase(normalized, "dong"))
            return "cave";
        if (ContainsNormalizedPhrase(normalized, "bai bien")) return "beach";
        if (ContainsNormalizedPhrase(normalized, "di tich") || ContainsNormalizedPhrase(normalized, "di san"))
            return "historical site";
        return "travel landmark";
    }

    private static string NormalizeCommonTypingErrors(string text)
    {
        if (text.Length == 0) return text;

        return WordTokenRegex().Replace(text, match =>
            CommonTypingCorrections.TryGetValue(match.Value, out var corrected)
                ? corrected
                : match.Value);
    }

    private static bool IsExcludedImageTitle(string title)
    {
        var normalized = NormalizeForComparison(title);
        return ExcludedImageTitleWords.Any(word => normalized.Contains(NormalizeForComparison(word), StringComparison.Ordinal));
    }

    private static bool ContainsAny(string text, IEnumerable<string> candidates)
    {
        var normalized = NormalizeForComparison(text);
        return candidates.Any(candidate => ContainsNormalizedPhrase(normalized, NormalizeForComparison(candidate)));
    }

    private static bool ContainsNormalizedPhrase(string normalizedText, string normalizedPhrase)
    {
        if (normalizedText.Length == 0 || normalizedPhrase.Length == 0) return false;
        return $" {normalizedText} ".Contains($" {normalizedPhrase} ", StringComparison.Ordinal);
    }

    private static string NormalizeForComparison(string? value)
    {
        var source = VietnamesePlaceName.ToAscii(value).ToLowerInvariant();
        source = NonAlphaNumericRegex().Replace(source, " ");
        return CollapseWhitespace(source);
    }

    private static string ReplaceInvariant(string source, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(oldValue)) return source;
        return Regex.Replace(source, Regex.Escape(oldValue), newValue, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string CollapseWhitespace(string? value) =>
        WhitespaceRegex().Replace(value ?? string.Empty, " ").Trim();

    private static string GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static string ReadMetadata(JsonElement metadata, string key)
    {
        if (metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty(key, out var item)
            || item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var text = value.GetString() ?? string.Empty;
        text = HtmlTagRegex().Replace(text, " ");
        return CollapseWhitespace(WebUtility.HtmlDecode(text));
    }

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    private sealed record IllustrationSearchRequest(string Subject, IllustrationTopic Topic);

    private enum IllustrationTopic
    {
        None,
        Destination,
        Festival,
        Cuisine
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex("[?!.。,，;:()\\[\\]{}\"'“”‘’]+")]
    private static partial Regex QuestionPunctuationRegex();

    [GeneratedRegex(@"\b(?:toi|mình|minh|tôi|cho|hay|về|ve|ở|o|tại|tai|của|cua|những|nhung|các|cac|nào|nao|giúp|giup|và|va|please|me|about|the|a|an|in|of|and)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneStopWordRegex();

    [GeneratedRegex(@"[\p{L}\p{M}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordTokenRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
