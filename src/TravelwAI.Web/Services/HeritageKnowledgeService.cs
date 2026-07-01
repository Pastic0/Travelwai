using System.Globalization;
using System.Text;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

public sealed class HeritageKnowledgeService
{
    private const string SourcesCollection = "heritage_sources";
    private const string ChunksCollection = "heritage_chunks";
    private readonly IDataRepository _repository;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public HeritageKnowledgeService(IDataRepository repository, IMemoryCache cache, IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _repository = repository;
        _cache = cache;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }


    public async Task<HeritageIngestResult> IngestUrlAsync(HeritageUrlIngestRequest request, string userId)
    {
        if (string.IsNullOrWhiteSpace(request.Url) || !Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("URL không hợp lệ.");
        }

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(18);
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Không tải được nguồn.");

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var raw = await response.Content.ReadAsStringAsync();
        if (raw.Length > 900_000) raw = raw[..900_000];
        var content = contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || raw.Contains("<html", StringComparison.OrdinalIgnoreCase)
            ? ExtractTextFromHtml(raw)
            : CleanText(raw);

        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("Không đọc được nội dung nguồn.");

        return await IngestAsync(new HeritageSourceIngestRequest
        {
            SourceId = request.SourceId,
            Title = request.Title ?? TryExtractHtmlTitle(raw) ?? uri.Host,
            Content = content,
            SourceType = request.SourceType,
            SourceName = request.SourceName ?? uri.Host,
            Publisher = request.Publisher,
            Url = uri.ToString(),
            License = request.License,
            AccessLevel = request.AccessLevel,
            ReliabilityScore = request.ReliabilityScore,
            ApprovalStatus = request.ApprovalStatus,
            Province = request.Province,
            ObjectName = request.ObjectName,
            ObjectType = request.ObjectType,
            Topics = request.Topics,
            PublishedDate = request.PublishedDate
        }, userId);
    }

    public async Task<HeritageIngestResult> IngestAsync(HeritageSourceIngestRequest request, string userId)
    {
        var title = CleanText(request.Title);
        var content = CleanText(request.Content);
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("Thiếu tên nguồn.");
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("Thiếu nội dung nguồn.");

        var sourceType = NormalizeSourceType(request.SourceType);
        var reliability = request.ReliabilityScore is > 0 ? Math.Clamp(request.ReliabilityScore.Value, 1, 100) : GetDefaultReliabilityScore(sourceType);
        var sourceId = Slugify(request.SourceId) ?? Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var approvalStatus = string.IsNullOrWhiteSpace(request.ApprovalStatus) ? "approved" : request.ApprovalStatus.Trim().ToLowerInvariant();
        if (approvalStatus is not "approved" and not "pending" and not "rejected") approvalStatus = "pending";

        var sourceData = new Dictionary<string, object?>
        {
            ["id"] = sourceId,
            ["title"] = title,
            ["source_type"] = sourceType,
            ["source_name"] = CleanText(request.SourceName) ?? title,
            ["publisher"] = CleanText(request.Publisher),
            ["url"] = CleanUrl(request.Url),
            ["license"] = CleanText(request.License) ?? "public_reference",
            ["access_level"] = NormalizeAccessLevel(request.AccessLevel),
            ["reliability_score"] = reliability,
            ["approval_status"] = approvalStatus,
            ["province"] = CleanText(request.Province),
            ["object_name"] = CleanText(request.ObjectName),
            ["object_type"] = CleanText(request.ObjectType),
            ["topics"] = NormalizeStringList(request.Topics),
            ["published_date"] = CleanText(request.PublishedDate),
            ["collected_at"] = now,
            ["updated_at"] = now,
            ["created_by"] = userId
        };

        await _repository.SetAsync(SourcesCollection, sourceId, sourceData, merge: false);
        await _repository.DeleteWhereEqualAsync(ChunksCollection, "source_id", sourceId);

        var chunks = ChunkContent(content, 1150, 220).ToList();
        var topics = NormalizeStringList(request.Topics);
        var chunkCount = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (string.IsNullOrWhiteSpace(chunk)) continue;
            var chunkId = $"{sourceId}-{(i + 1).ToString("000", CultureInfo.InvariantCulture)}";
            var normalized = NormalizeVietnameseForSearch(chunk);
            var keywords = ExtractKeywords(title + " " + request.ObjectName + " " + request.Province + " " + chunk, 32);
            var chunkData = new Dictionary<string, object?>
            {
                ["id"] = chunkId,
                ["source_id"] = sourceId,
                ["source_title"] = title,
                ["source_type"] = sourceType,
                ["source_name"] = sourceData["source_name"],
                ["publisher"] = sourceData["publisher"],
                ["url"] = sourceData["url"],
                ["license"] = sourceData["license"],
                ["access_level"] = sourceData["access_level"],
                ["reliability_score"] = reliability,
                ["approval_status"] = approvalStatus,
                ["province"] = sourceData["province"],
                ["object_name"] = sourceData["object_name"],
                ["object_type"] = sourceData["object_type"],
                ["topics"] = topics,
                ["keywords"] = keywords,
                ["content"] = chunk,
                ["search_text"] = normalized,
                ["chunk_index"] = i + 1,
                ["published_date"] = sourceData["published_date"],
                ["collected_at"] = now,
                ["updated_at"] = now
            };
            await _repository.SetAsync(ChunksCollection, chunkId, chunkData, merge: false);
            chunkCount++;
        }

        _cache.Remove($"{BuildSearchCacheKeyPrefix()}:{GetKnowledgeCandidateLimit()}");
        return new HeritageIngestResult(sourceId, chunkCount, approvalStatus);
    }

    public async Task<List<Dictionary<string, object?>>> GetSourcesAsync(int limit = 80)
        => await _repository.GetAllAsync(SourcesCollection, Math.Clamp(limit, 1, 200));

    public async Task<bool> ApproveSourceAsync(string sourceId, bool approved)
    {
        sourceId = sourceId.Trim();
        if (string.IsNullOrWhiteSpace(sourceId)) return false;
        var status = approved ? "approved" : "rejected";
        var updated = DateTimeOffset.UtcNow.ToString("O");
        var ok = await _repository.UpdateAsync(SourcesCollection, sourceId, new Dictionary<string, object?>
        {
            ["approval_status"] = status,
            ["updated_at"] = updated
        });

        var chunks = await _repository.WhereEqualAsync(ChunksCollection, "source_id", sourceId, 400);
        foreach (var chunk in chunks)
        {
            var id = chunk.GetValueOrDefault("id")?.ToString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            await _repository.UpdateAsync(ChunksCollection, id, new Dictionary<string, object?>
            {
                ["approval_status"] = status,
                ["updated_at"] = updated
            });
        }
        return ok;
    }

    public async Task<HeritageRetrievalResult> RetrieveAsync(string query, string? interfaceContext = null, int maxResults = 7)
    {
        var cleanQuery = CleanText(query) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanQuery)) return new HeritageRetrievalResult(new List<HeritageRetrievedChunk>(), "unknown", false);

        var normalizedQuery = NormalizeVietnameseForSearch(cleanQuery + " " + interfaceContext);
        var queryTokens = ExtractQueryTokens(normalizedQuery).ToHashSet(StringComparer.Ordinal);
        var intent = DetectIntent(normalizedQuery);
        var candidateLimit = GetKnowledgeCandidateLimit();
        var cacheKey = $"{BuildSearchCacheKeyPrefix()}:{candidateLimit}";
        if (!_cache.TryGetValue(cacheKey, out List<Dictionary<string, object?>>? candidates))
        {
            candidates = await _repository.GetAllFieldsAsync(ChunksCollection, new[]
            {
                "source_id", "source_title", "source_type", "source_name", "publisher", "url", "license", "access_level",
                "reliability_score", "approval_status", "province", "object_name", "object_type", "topics", "keywords",
                "content", "search_text", "chunk_index", "published_date", "updated_at"
            }, candidateLimit);
            _cache.Set(cacheKey, candidates, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(45), Size = 1 });
        }

        var scored = new List<HeritageRetrievedChunk>();
        foreach (var item in candidates)
        {
            var status = item.GetValueOrDefault("approval_status")?.ToString() ?? "approved";
            if (!string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase)) continue;
            var content = item.GetValueOrDefault("content")?.ToString();
            if (string.IsNullOrWhiteSpace(content)) continue;

            var searchText = item.GetValueOrDefault("search_text")?.ToString();
            if (string.IsNullOrWhiteSpace(searchText)) searchText = NormalizeVietnameseForSearch(content);
            var score = ScoreChunk(normalizedQuery, queryTokens, intent, item, searchText);
            if (score <= 0) continue;

            scored.Add(new HeritageRetrievedChunk
            {
                Id = item.GetValueOrDefault("id")?.ToString() ?? string.Empty,
                SourceId = item.GetValueOrDefault("source_id")?.ToString() ?? string.Empty,
                SourceTitle = item.GetValueOrDefault("source_title")?.ToString() ?? string.Empty,
                SourceType = item.GetValueOrDefault("source_type")?.ToString() ?? string.Empty,
                SourceName = item.GetValueOrDefault("source_name")?.ToString() ?? string.Empty,
                Publisher = item.GetValueOrDefault("publisher")?.ToString(),
                Url = item.GetValueOrDefault("url")?.ToString(),
                License = item.GetValueOrDefault("license")?.ToString(),
                AccessLevel = item.GetValueOrDefault("access_level")?.ToString(),
                ReliabilityScore = ToInt(item.GetValueOrDefault("reliability_score"), 60),
                Province = item.GetValueOrDefault("province")?.ToString(),
                ObjectName = item.GetValueOrDefault("object_name")?.ToString(),
                ObjectType = item.GetValueOrDefault("object_type")?.ToString(),
                Content = content,
                ChunkIndex = ToInt(item.GetValueOrDefault("chunk_index"), 0),
                PublishedDate = item.GetValueOrDefault("published_date")?.ToString(),
                UpdatedAt = item.GetValueOrDefault("updated_at")?.ToString(),
                Score = score
            });
        }

        var final = scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.ReliabilityScore)
            .Take(Math.Clamp(maxResults, 1, 12))
            .ToList();

        return new HeritageRetrievalResult(final, intent, final.Count > 0);
    }

    public string BuildContextBlock(HeritageRetrievalResult retrieval)
    {
        if (retrieval.Chunks.Count == 0)
        {
            return "KHO TRI THỨC NỘI BỘ: Chưa tìm thấy đoạn dữ liệu đã duyệt khớp câu hỏi. Nếu câu hỏi cần số quyết định, danh hiệu, giá vé, giờ mở cửa hoặc sự kiện hiện tại thì phải nói chưa đủ nguồn chắc; không bịa.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("KHO TRI THỨC NỘI BỘ ĐÃ TRUY XUẤT CHO HƯỚNG DẪN VIÊN AI");
        builder.AppendLine("Ý định nhận diện: " + retrieval.Intent);
        builder.AppendLine("QUY TẮC BẮT BUỘC: Các đoạn dưới là nguồn thông tin duy nhất được phép dùng. OpenRouter/model không phải nguồn thông tin. Không dùng kiến thức nền, Wikipedia, dữ liệu frontend hoặc suy đoán. Nếu nguồn không nêu chi tiết được hỏi thì trả lời là chưa có nguồn đã duyệt phù hợp. Ưu tiên reliability_score cao và source_type chính thống. Nếu nguồn mâu thuẫn, nêu rõ là các nguồn chưa thống nhất.");

        for (var i = 0; i < retrieval.Chunks.Count; i++)
        {
            var chunk = retrieval.Chunks[i];
            builder.AppendLine();
            builder.Append('[').Append(i + 1).Append("] ")
                .Append(chunk.SourceTitle);
            if (!string.IsNullOrWhiteSpace(chunk.SourceName)) builder.Append(" | ").Append(chunk.SourceName);
            builder.Append(" | loại nguồn: ").Append(chunk.SourceType);
            builder.Append(" | độ tin cậy: ").Append(chunk.ReliabilityScore);
            if (!string.IsNullOrWhiteSpace(chunk.PublishedDate)) builder.Append(" | ngày nguồn: ").Append(chunk.PublishedDate);
            if (!string.IsNullOrWhiteSpace(chunk.Url)) builder.Append(" | url: ").Append(chunk.Url);
            builder.AppendLine();
            builder.AppendLine(TrimForContext(chunk.Content, 1450));
        }

        return builder.ToString();
    }

    public static string NormalizeVietnameseForSearch(string text)
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
        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant(), @"[^a-z0-9\s]", " ");
    }

    private int GetKnowledgeCandidateLimit()
    {
        var value = _configuration["HeritageKnowledge:CandidateLimit"];
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 100, 3000) : 1200;
    }

    private static string BuildSearchCacheKeyPrefix() => "heritage-knowledge-chunks";

    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = Regex.Replace(value, @"[\u0000-\u001F\u007F]", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return clean.Length == 0 ? null : clean;
    }

    private static string? CleanUrl(string? value)
    {
        var clean = CleanText(value);
        if (string.IsNullOrWhiteSpace(clean)) return null;
        return Uri.TryCreate(clean, UriKind.Absolute, out _) ? clean : null;
    }

    private static string NormalizeSourceType(string? value)
    {
        var key = NormalizeVietnameseForSearch(value ?? string.Empty).Trim();
        if (key.Contains("unesco")) return "unesco";
        if (key.Contains("cuc di san") || key.Contains("bo vh") || key.Contains("bo van hoa") || key.Contains("chinh phu") || key.Contains("phap luat") || key.Contains("ubnd") || key.Contains("so ")) return "official";
        if (key.Contains("bao tang") || key.Contains("ban quan ly")) return "museum";
        if (key.Contains("sach") || key.Contains("dia chi") || key.Contains("nghien cuu") || key.Contains("luan van")) return "academic";
        if (key.Contains("bao") || key.Contains("ttxvn") || key.Contains("vov") || key.Contains("vtv")) return "press";
        if (key.Contains("phong van") || key.Contains("thuc dia") || key.Contains("nghe nhan")) return "fieldwork";
        if (key.Contains("blog") || key.Contains("mang xa hoi") || key.Contains("facebook") || key.Contains("wikipedia")) return "reference";
        return string.IsNullOrWhiteSpace(value) ? "reference" : value.Trim().ToLowerInvariant();
    }

    private static string NormalizeAccessLevel(string? value)
    {
        var key = NormalizeVietnameseForSearch(value ?? string.Empty).Trim();
        if (key.Contains("private") || key.Contains("noi bo")) return "private";
        if (key.Contains("restricted") || key.Contains("ban quyen") || key.Contains("gioi han")) return "restricted";
        return "public";
    }

    private static int GetDefaultReliabilityScore(string sourceType) => sourceType switch
    {
        "unesco" => 98,
        "official" => 95,
        "museum" => 88,
        "academic" => 82,
        "press" => 70,
        "fieldwork" => 68,
        "reference" => 45,
        _ => 50
    };

    private static IEnumerable<string> ChunkContent(string content, int maxChars, int overlapChars)
    {
        content = CleanText(content) ?? string.Empty;
        if (content.Length <= maxChars)
        {
            if (!string.IsNullOrWhiteSpace(content)) yield return content;
            yield break;
        }

        var sentences = Regex.Split(content, @"(?<=[\.\!\?。！？])\s+").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var buffer = new StringBuilder();
        var previousTail = string.Empty;
        foreach (var sentence in sentences)
        {
            var cleanSentence = sentence.Trim();
            if (buffer.Length + cleanSentence.Length + 1 > maxChars && buffer.Length > 0)
            {
                var chunk = buffer.ToString().Trim();
                yield return chunk;
                previousTail = chunk.Length > overlapChars ? chunk[^overlapChars..] : chunk;
                buffer.Clear();
                if (!string.IsNullOrWhiteSpace(previousTail)) buffer.Append(previousTail).Append(' ');
            }
            buffer.Append(cleanSentence).Append(' ');
        }

        var last = buffer.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(last)) yield return last;
    }

    private static IReadOnlyList<string> NormalizeStringList(IEnumerable<string>? values)
    {
        if (values is null) return Array.Empty<string>();
        return values.Select(CleanText).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
    }

    private static IReadOnlyList<string> ExtractKeywords(string text, int limit)
        => ExtractQueryTokens(NormalizeVietnameseForSearch(text)).Take(limit).ToList();

    private static IEnumerable<string> ExtractQueryTokens(string normalized)
    {
        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "toi", "cho", "biet", "ve", "la", "gi", "o", "dau", "khi", "nao", "nhu", "the", "hay", "ke", "gioi", "thieu", "chi", "tiet", "hon", "di", "du", "lich", "mot", "cac", "nhung", "noi", "nay", "do", "va", "cua", "co", "khong", "can", "muon", "nen", "trong", "ngoai", "gan", "day", "duoc", "theo", "nguon"
        };

        return Regex.Split(normalized, @"\s+")
            .Where(t => t.Length >= 3 && !stopWords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .Take(80);
    }

    private static string DetectIntent(string normalizedQuery)
    {
        if (ContainsAny(normalizedQuery, "quyet dinh", "xep hang", "quoc gia dac biet", "danh hieu", "nghe nhan uu tu", "nghe nhan nhan dan", "cong nhan", "nghi dinh", "luat")) return "legal_verification";
        if (ContainsAny(normalizedQuery, "lich trinh", "mot ngay", "nua ngay", "nen di", "goi y", "gan do", "tham quan")) return "itinerary";
        if (ContainsAny(normalizedQuery, "ke chuyen", "cau chuyen", "truyen thuyet", "giai thoai", "de nho")) return "storytelling";
        if (ContainsAny(normalizedQuery, "kien truc", "vat lieu", "ket cau", "hoa van", "phong cach")) return "architecture";
        if (ContainsAny(normalizedQuery, "lang nghe", "quy trinh", "nguyen lieu", "cong cu", "san pham", "nghe nhan")) return "craft_village";
        if (ContainsAny(normalizedQuery, "gia ve", "gio mo cua", "hom nay", "dang dien ra", "su kien", "le hoi nam nay")) return "current_visit_info";
        return "core_qa";
    }

    private static bool ContainsAny(string text, params string[] values) => values.Any(v => text.Contains(v, StringComparison.Ordinal));

    private static double ScoreChunk(string normalizedQuery, HashSet<string> queryTokens, string intent, Dictionary<string, object?> item, string searchText)
    {
        var sourceType = item.GetValueOrDefault("source_type")?.ToString() ?? "reference";
        var reliability = ToInt(item.GetValueOrDefault("reliability_score"), GetDefaultReliabilityScore(sourceType));
        var title = NormalizeVietnameseForSearch(item.GetValueOrDefault("source_title")?.ToString() ?? string.Empty);
        var objectName = NormalizeVietnameseForSearch(item.GetValueOrDefault("object_name")?.ToString() ?? string.Empty);
        var province = NormalizeVietnameseForSearch(item.GetValueOrDefault("province")?.ToString() ?? string.Empty);
        var topicText = NormalizeVietnameseForSearch(JsonSerializer.Serialize(item.GetValueOrDefault("topics") ?? ""));
        var keywordText = NormalizeVietnameseForSearch(JsonSerializer.Serialize(item.GetValueOrDefault("keywords") ?? ""));

        var relevanceScore = 0d;
        foreach (var token in queryTokens)
        {
            if (searchText.Contains(token, StringComparison.Ordinal)) relevanceScore += 5;
            if (title.Contains(token, StringComparison.Ordinal)) relevanceScore += 7;
            if (objectName.Contains(token, StringComparison.Ordinal)) relevanceScore += 9;
            if (province.Contains(token, StringComparison.Ordinal)) relevanceScore += 4;
            if (topicText.Contains(token, StringComparison.Ordinal)) relevanceScore += 5;
            if (keywordText.Contains(token, StringComparison.Ordinal)) relevanceScore += 4;
        }

        var importantPhrases = ExtractImportantPhrases(normalizedQuery);
        foreach (var phrase in importantPhrases)
        {
            if (phrase.Length < 6) continue;
            if (searchText.Contains(phrase, StringComparison.Ordinal)) relevanceScore += 14;
            if (title.Contains(phrase, StringComparison.Ordinal) || objectName.Contains(phrase, StringComparison.Ordinal)) relevanceScore += 18;
        }

        if (relevanceScore <= 0) return 0;

        var score = relevanceScore;
        score += reliability / 10d;
        score += sourceType switch
        {
            "unesco" => 10,
            "official" => 9,
            "museum" => 7,
            "academic" => 6,
            "press" => 3,
            "fieldwork" => intent == "storytelling" ? 7 : 3,
            _ => 0
        };

        if (intent == "legal_verification" && sourceType is "unesco" or "official") score += 18;
        if (intent == "architecture" && sourceType is "museum" or "academic" or "official") score += 7;
        if (intent == "craft_village" && (searchText.Contains("lang nghe") || searchText.Contains("nghe nhan") || searchText.Contains("quy trinh"))) score += 8;
        if (intent == "current_visit_info" && sourceType is "official" or "museum" or "press") score += 8;

        var updatedAt = item.GetValueOrDefault("updated_at")?.ToString();
        if (DateTimeOffset.TryParse(updatedAt, out var date))
        {
            var ageDays = (DateTimeOffset.UtcNow - date).TotalDays;
            if (ageDays < 45) score += 5;
            else if (ageDays < 180) score += 2;
        }

        return score;
    }

    private static IEnumerable<string> ExtractImportantPhrases(string normalizedQuery)
    {
        var words = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToList();
        for (var size = Math.Min(5, words.Count); size >= 2; size--)
        {
            for (var i = 0; i <= words.Count - size; i++) yield return string.Join(' ', words.Skip(i).Take(size));
        }
    }

    private static int ToInt(object? value, int fallback)
    {
        if (value is int i) return i;
        if (value is long l) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (value is double d) return (int)Math.Round(d);
        if (value is decimal m) return (int)Math.Round(m);
        return int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static string TrimForContext(string value, int max)
    {
        var clean = CleanText(value) ?? string.Empty;
        if (clean.Length <= max) return clean;
        var cut = clean[..max];
        var end = Math.Max(cut.LastIndexOf('.'), Math.Max(cut.LastIndexOf('!'), cut.LastIndexOf('?')));
        return end > 160 ? cut[..(end + 1)] : cut.Trim();
    }

    private static string? Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var slug = NormalizeVietnameseForSearch(value).Trim();
        slug = Regex.Replace(slug, @"\s+", "-");
        slug = Regex.Replace(slug, @"[^a-z0-9\-]", string.Empty).Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? null : slug[..Math.Min(slug.Length, 90)];
    }
}

public sealed class HeritageSourceIngestRequest
{
    public string? SourceId { get; set; }
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? SourceType { get; set; }
    public string? SourceName { get; set; }
    public string? Publisher { get; set; }
    public string? Url { get; set; }
    public string? License { get; set; }
    public string? AccessLevel { get; set; }
    public int? ReliabilityScore { get; set; }
    public string? ApprovalStatus { get; set; }
    public string? Province { get; set; }
    public string? ObjectName { get; set; }
    public string? ObjectType { get; set; }
    public List<string>? Topics { get; set; }
    public string? PublishedDate { get; set; }
}

public sealed record HeritageIngestResult(string SourceId, int ChunkCount, string ApprovalStatus);

public sealed class HeritageRetrievedChunk
{
    public string Id { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SourceTitle { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? Url { get; set; }
    public string? License { get; set; }
    public string? AccessLevel { get; set; }
    public int ReliabilityScore { get; set; }
    public string? Province { get; set; }
    public string? ObjectName { get; set; }
    public string? ObjectType { get; set; }
    public string Content { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public string? PublishedDate { get; set; }
    public string? UpdatedAt { get; set; }
    public double Score { get; set; }
}

public sealed record HeritageRetrievalResult(List<HeritageRetrievedChunk> Chunks, string Intent, bool HasMatches);
