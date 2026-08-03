using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using TravelwAI.Models.Common;
using TravelwAI.Web.Models;

namespace TravelwAI.Web.Services;

public sealed class PlaceImageService
{
    private const int MaxImagesPerPlace = 4;
    private const int MinPreferredImagesPerPlace = 3;
    private const int SearchTitleLimit = 5;
    private const int RawImageCandidateLimit = 24;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);

    private static readonly string[] PlaceIntentKeywords =
    {
        "địa điểm", "dia diem", "điểm đến", "diem den", "danh lam", "thắng cảnh", "thang canh",
        "tham quan", "khám phá", "kham pha", "du lịch", "du lich", "ở đâu", "o dau",
        "vịnh", "vinh", "núi", "nui", "biển", "bien",
        "chùa", "chua", "đền", "den", "hang", "đảo", "dao", "thác", "thac",
        "cầu", "cau", "thành phố", "thanh pho", "tỉnh", "tinh", "huyện", "huyen",
        "xã", "xa", "phường", "phuong"
    };

    private static readonly string[] NonPhotoKeywords =
    {
        "logo", "icon", "symbol", "flag", "coat of arms", "locator", "map", "location map",
        "blank map", "route map", "svg", "seal", "emblem", "bản đồ", "huy hiệu", "quốc kỳ"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PlaceImageService> _logger;

    public PlaceImageService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<PlaceImageService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AiReplyAttachment>> GetPlaceImagesAsync(
        string? userMessage,
        string? aiReply,
        CancellationToken cancellationToken)
    {
        var message = (userMessage ?? string.Empty).Trim();
        if (message.Length == 0) return Array.Empty<AiReplyAttachment>();

        var candidates = BuildCandidates(message, aiReply);
        if (candidates.Count == 0) return Array.Empty<AiReplyAttachment>();

        foreach (var candidate in candidates)
        {
            try
            {
                var cached = await GetCachedOrFetchAsync(candidate, cancellationToken);
                if (cached.Count >= MinPreferredImagesPerPlace) return cached;
                if (cached.Count > 0) return cached;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Không lấy được ảnh địa điểm cho truy vấn {Candidate}", candidate);
            }
        }

        return Array.Empty<AiReplyAttachment>();
    }

    private async Task<IReadOnlyList<AiReplyAttachment>> GetCachedOrFetchAsync(string candidate, CancellationToken cancellationToken)
    {
        var cacheKey = $"place-images:{Normalize(candidate)}";
        if (_cache.TryGetValue<IReadOnlyList<AiReplyAttachment>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var attachments = await FetchPlaceImagesAsync(candidate, cancellationToken);
        var result = attachments.ToArray();
        _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration,
            Size = 1
        });
        return result;
    }

    private async Task<IReadOnlyList<AiReplyAttachment>> FetchPlaceImagesAsync(string candidate, CancellationToken cancellationToken)
    {
        var titles = await SearchWikipediaTitlesAsync(candidate, cancellationToken);
        foreach (var title in titles)
        {
            var attachments = await GetImagesForTitleAsync(title, cancellationToken);
            if (attachments.Count >= MinPreferredImagesPerPlace) return attachments;
            if (attachments.Count > 0) return attachments;
        }

        return Array.Empty<AiReplyAttachment>();
    }

    private async Task<List<string>> SearchWikipediaTitlesAsync(string query, CancellationToken cancellationToken)
    {
        var cleanQuery = (query ?? string.Empty).Trim();
        if (cleanQuery.Length == 0) return new List<string>();

        using var client = CreateWikipediaClient();
        var url = $"https://vi.wikipedia.org/w/api.php?action=query&list=search&format=json&utf8=1&srlimit={SearchTitleLimit}&srsearch={WebUtility.UrlEncode(cleanQuery)}";
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var titles = new List<string>();
        if (document.RootElement.TryGetProperty("query", out var queryNode)
            && queryNode.TryGetProperty("search", out var searchNode)
            && searchNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in searchNode.EnumerateArray())
            {
                if (!item.TryGetProperty("title", out var titleNode)) continue;
                var title = titleNode.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(title)) continue;
                AddUniqueCaseInsensitive(titles, title);
            }
        }

        return titles;
    }

    private async Task<List<AiReplyAttachment>> GetImagesForTitleAsync(string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) return new List<AiReplyAttachment>();

        var images = new List<AiReplyAttachment>();
        var summary = await GetSummaryPayloadAsync(title, cancellationToken);
        if (summary.IsDisambiguation) return images;

        if (!string.IsNullOrWhiteSpace(summary.ThumbnailUrl))
        {
            AddAttachment(images, new AiReplyAttachment(
                summary.ThumbnailUrl!,
                summary.DisplayTitle ?? title,
                GuessContentType(summary.ThumbnailUrl!),
                0,
                "image"));
        }

        var fileTitles = await GetPageImageTitlesAsync(title, cancellationToken);
        if (fileTitles.Count > 0)
        {
            var extraImages = await GetImageInfosAsync(fileTitles, summary.DisplayTitle ?? title, cancellationToken);
            foreach (var image in extraImages)
            {
                AddAttachment(images, image);
                if (images.Count >= MaxImagesPerPlace) break;
            }
        }

        return images.Take(MaxImagesPerPlace).ToList();
    }

    private async Task<(string? DisplayTitle, string? ThumbnailUrl, bool IsDisambiguation)> GetSummaryPayloadAsync(string title, CancellationToken cancellationToken)
    {
        using var client = CreateWikipediaClient();
        var url = $"https://vi.wikipedia.org/api/rest_v1/page/summary/{WebUtility.UrlEncode(title.Replace(' ', '_'))}";
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return (title, null, false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var isDisambiguation = root.TryGetProperty("type", out var typeNode)
            && string.Equals(typeNode.GetString(), "disambiguation", StringComparison.OrdinalIgnoreCase);

        string? thumbnailUrl = null;
        if (root.TryGetProperty("thumbnail", out var thumbnailNode)
            && thumbnailNode.TryGetProperty("source", out var sourceNode))
        {
            thumbnailUrl = sourceNode.GetString()?.Trim();
        }

        var displayTitle = root.TryGetProperty("title", out var displayTitleNode)
            ? displayTitleNode.GetString()?.Trim()
            : title;

        return (displayTitle, thumbnailUrl, isDisambiguation);
    }

    private async Task<List<string>> GetPageImageTitlesAsync(string title, CancellationToken cancellationToken)
    {
        using var client = CreateWikipediaClient();
        var url = $"https://vi.wikipedia.org/w/api.php?action=query&format=json&prop=images&imlimit={RawImageCandidateLimit}&titles={WebUtility.UrlEncode(title)}";
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var results = new List<string>();
        if (!document.RootElement.TryGetProperty("query", out var queryNode)
            || !queryNode.TryGetProperty("pages", out var pagesNode)
            || pagesNode.ValueKind != JsonValueKind.Object)
        {
            return results;
        }

        foreach (var pageNode in pagesNode.EnumerateObject())
        {
            if (!pageNode.Value.TryGetProperty("images", out var imagesNode)
                || imagesNode.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var imageNode in imagesNode.EnumerateArray())
            {
                if (!imageNode.TryGetProperty("title", out var titleNode)) continue;
                var fileTitle = titleNode.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(fileTitle)) continue;
                if (!LooksLikeUsefulPhoto(fileTitle, null)) continue;
                AddUniqueCaseInsensitive(results, fileTitle);
                if (results.Count >= RawImageCandidateLimit) return results;
            }
        }

        return results;
    }

    private async Task<List<AiReplyAttachment>> GetImageInfosAsync(
        IReadOnlyList<string> fileTitles,
        string displayTitle,
        CancellationToken cancellationToken)
    {
        var results = new List<AiReplyAttachment>();
        if (fileTitles.Count == 0) return results;

        using var client = CreateWikipediaClient();
        foreach (var batch in Batch(fileTitles, 10))
        {
            var titlesValue = string.Join("|", batch);
            var url = $"https://vi.wikipedia.org/w/api.php?action=query&format=json&prop=imageinfo&iiprop=url&iiurlwidth=1280&titles={WebUtility.UrlEncode(titlesValue)}";
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("query", out var queryNode)
                || !queryNode.TryGetProperty("pages", out var pagesNode)
                || pagesNode.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var pageNode in pagesNode.EnumerateObject())
            {
                var title = pageNode.Value.TryGetProperty("title", out var titleNode)
                    ? titleNode.GetString()?.Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(title)) continue;
                if (!pageNode.Value.TryGetProperty("imageinfo", out var imageInfoNode)
                    || imageInfoNode.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var imageInfo in imageInfoNode.EnumerateArray())
                {
                    var source = imageInfo.TryGetProperty("thumburl", out var thumbNode)
                        ? thumbNode.GetString()?.Trim()
                        : imageInfo.TryGetProperty("url", out var urlNode)
                            ? urlNode.GetString()?.Trim()
                            : null;
                    if (string.IsNullOrWhiteSpace(source)) continue;
                    if (!LooksLikeUsefulPhoto(title, source)) continue;

                    AddAttachment(results, new AiReplyAttachment(
                        source!,
                        BuildImageName(displayTitle, title),
                        GuessContentType(source!),
                        0,
                        "image"));

                    if (results.Count >= MaxImagesPerPlace) return results;
                }
            }
        }

        return results;
    }

    private HttpClient CreateWikipediaClient()
    {
        var client = _httpClientFactory.CreateClient();
        if (client.Timeout > TimeSpan.FromSeconds(12)) client.Timeout = TimeSpan.FromSeconds(12);
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TravelwAI/1.0 (+https://travelwai.local)");
        }
        return client;
    }

    private static List<string> BuildCandidates(string message, string? aiReply)
    {
        var candidates = new List<string>();
        var normalizedMessage = Normalize(message);
        var looksLikePlaceQuestion = PlaceIntentKeywords.Any(keyword => normalizedMessage.Contains(Normalize(keyword), StringComparison.Ordinal));

        foreach (var province in ProvinceCatalog.All.OrderByDescending(item => item.Name.Length))
        {
            if (ContainsNormalized(normalizedMessage, province.Name))
            {
                AddCandidate(candidates, province.Name);
                continue;
            }

            foreach (var alias in province.MergedFrom.Concat(GetProvinceAliases(province.Name)))
            {
                if (ContainsNormalized(normalizedMessage, alias))
                {
                    AddCandidate(candidates, province.Name);
                    break;
                }
            }
        }

        foreach (var knownName in VietnamesePlaceName.AllKnownNames.OrderByDescending(item => item.Length))
        {
            if (ContainsNormalized(normalizedMessage, knownName)) AddCandidate(candidates, knownName);
            if (candidates.Count >= 5) break;
        }

        if (looksLikePlaceQuestion)
        {
            AddCandidate(candidates, CleanSearchQuery(message));
            if (!string.IsNullOrWhiteSpace(aiReply))
            {
                var extracted = ExtractPlaceLikePhrase(aiReply);
                AddCandidate(candidates, extracted);
            }
        }

        return candidates
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(5)
            .ToList();
    }

    private static IEnumerable<string> GetProvinceAliases(string provinceName)
    {
        return provinceName switch
        {
            "Thành phố Hà Nội" => new[] { "Hà Nội", "Thủ đô Hà Nội", "TP. Hà Nội", "TP Hà Nội" },
            "Thành phố Hải Phòng" => new[] { "Hải Phòng", "TP. Hải Phòng", "TP Hải Phòng" },
            "Thành phố Huế" => new[] { "Huế", "TP. Huế", "TP Huế" },
            "Thành phố Đà Nẵng" => new[] { "Đà Nẵng", "TP. Đà Nẵng", "TP Đà Nẵng" },
            "Thành phố Hồ Chí Minh" => new[] { "Hồ Chí Minh", "TP. Hồ Chí Minh", "TP Hồ Chí Minh", "Sài Gòn", "TP. HCM", "TP HCM" },
            "Thành phố Cần Thơ" => new[] { "Cần Thơ", "TP. Cần Thơ", "TP Cần Thơ" },
            _ => Array.Empty<string>()
        };
    }

    private static string CleanSearchQuery(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        var normalized = Normalize(text);
        var stopPhrases = new[]
        {
            "cho toi biet", "giup toi", "gioi thieu", "thong tin", "ve", "la gi", "co gi", "o dau",
            "dia diem", "du lich", "tham quan", "kham pha", "noi tieng", "nhu the nao", "co dep khong",
            "hay", "toi muon", "minh muon", "xin", "vui long"
        };

        foreach (var phrase in stopPhrases) normalized = normalized.Replace(phrase, " ", StringComparison.Ordinal);
        normalized = string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0) return text;
        return normalized;
    }

    private static string ExtractPlaceLikePhrase(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        var sentences = text.Split(new[] { '.', '!', '?', '\n', ';', ':' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var sentence in sentences.Take(2))
        {
            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var run = new List<string>();
            foreach (var word in words)
            {
                var cleanWord = word.Trim(',', '"', '\'', '(', ')');
                if (cleanWord.Length == 0) continue;
                var first = cleanWord[0];
                if (char.IsUpper(first) || VietnamesePlaceName.AllKnownNames.Contains(cleanWord, StringComparer.OrdinalIgnoreCase))
                {
                    run.Add(cleanWord);
                    if (run.Count >= 4) break;
                }
                else if (run.Count >= 2)
                {
                    break;
                }
                else
                {
                    run.Clear();
                }
            }

            if (run.Count >= 2) return string.Join(' ', run);
        }

        return string.Empty;
    }

    private static bool ContainsNormalized(string normalizedSource, string candidate)
    {
        var normalizedCandidate = Normalize(candidate);
        if (normalizedCandidate.Length == 0) return false;
        return normalizedSource.Contains(normalizedCandidate, StringComparison.Ordinal);
    }

    private static void AddCandidate(ICollection<string> list, string? candidate)
    {
        var value = (candidate ?? string.Empty).Trim();
        if (value.Length < 2) return;
        if (list.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        list.Add(value);
    }

    private static void AddAttachment(ICollection<AiReplyAttachment> list, AiReplyAttachment? attachment)
    {
        if (attachment is null) return;
        if (string.IsNullOrWhiteSpace(attachment.Url)) return;
        if (list.Any(item => string.Equals(item.Url, attachment.Url, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(attachment);
    }

    private static void AddUniqueCaseInsensitive(ICollection<string> list, string? value)
    {
        var clean = (value ?? string.Empty).Trim();
        if (clean.Length == 0) return;
        if (list.Contains(clean, StringComparer.OrdinalIgnoreCase)) return;
        list.Add(clean);
    }

    private static IEnumerable<List<string>> Batch(IReadOnlyList<string> items, int size)
    {
        for (var index = 0; index < items.Count; index += size)
        {
            yield return items.Skip(index).Take(size).ToList();
        }
    }

    private static string BuildImageName(string displayTitle, string fileTitle)
    {
        var cleanFileTitle = Regex.Replace(fileTitle, "^(File|Tập tin):", string.Empty, RegexOptions.IgnoreCase).Trim();
        if (cleanFileTitle.Length == 0) return displayTitle;
        return $"{displayTitle} - {cleanFileTitle}";
    }

    private static bool LooksLikeUsefulPhoto(string? title, string? url)
    {
        var source = $"{title} {url}".Trim();
        if (source.Length == 0) return false;

        var normalized = Normalize(source);
        if (NonPhotoKeywords.Any(keyword => normalized.Contains(Normalize(keyword), StringComparison.Ordinal)))
        {
            return false;
        }

        var lowerUrl = (url ?? string.Empty).ToLowerInvariant();
        if (lowerUrl.Length > 0)
        {
            if (!(lowerUrl.EndsWith(".jpg")
                || lowerUrl.EndsWith(".jpeg")
                || lowerUrl.EndsWith(".png")
                || lowerUrl.EndsWith(".webp")
                || lowerUrl.Contains(".jpg?")
                || lowerUrl.Contains(".jpeg?")
                || lowerUrl.Contains(".png?")
                || lowerUrl.Contains(".webp?")))
            {
                return false;
            }
        }

        return true;
    }

    private static string Normalize(string? value)
    {
        var ascii = VietnamesePlaceName.ToAscii(value ?? string.Empty).ToLowerInvariant();
        var cleaned = new string(ascii.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray());
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    private static string GuessContentType(string url)
    {
        var lower = (url ?? string.Empty).ToLowerInvariant();
        if (lower.EndsWith(".png") || lower.Contains(".png?")) return "image/png";
        if (lower.EndsWith(".webp") || lower.Contains(".webp?")) return "image/webp";
        if (lower.EndsWith(".svg") || lower.Contains(".svg?")) return "image/svg+xml";
        if (lower.EndsWith(".gif") || lower.Contains(".gif?")) return "image/gif";
        return "image/jpeg";
    }
}
