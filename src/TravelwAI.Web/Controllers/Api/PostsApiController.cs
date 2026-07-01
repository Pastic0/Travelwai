using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class PostsApiController : ApiControllerBase
{
    private const string PostsCollection = "travel_posts";
    private const string PostViewEventsCollection = "post_view_events";
    private const string SeedVersion = "2026-06-28-free-chatbot-quota-v13";
    private const int SeedPostLimit = 10;
    private static readonly string[] PostListFields =
    {
        "title", "summary", "month", "festival", "province", "holiday_type", "holidayType",
        "tour_keywords", "tourKeywords", "author_id", "authorId", "author_name", "authorName",
        "image_urls", "imageUrls", "images", "status", "source", "is_deleted", "isDeleted", "deleted_at", "updated_at"
    };
    private static readonly string[] PostSeedCheckFields = { "seed_version", "source", "author_id", "authorId", "author_name", "authorName", "is_deleted", "isDeleted", "deleted_at" };
    private static readonly Regex WordRegex = new(@"\p{L}[\p{L}\p{M}\p{N}'-]*", RegexOptions.Compiled);
    private readonly IDataRepository _repo;
    private readonly IFileStorageService _fileStorage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TourOfferService _offerService;

    public PostsApiController(IAuthService authService, IDataRepository repo, IFileStorageService fileStorage, IHttpClientFactory httpClientFactory, TourOfferService offerService) : base(authService)
    {
        _repo = repo;
        _fileStorage = fileStorage;
        _httpClientFactory = httpClientFactory;
        _offerService = offerService;
    }

    [HttpGet("posts")]
    public async Task<IActionResult> GetPosts([FromQuery] int? month = null)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        await EnsureSeedPostsAsync();
        var isAdmin = IsAdminUser(current.authUser);
        var posts = await _repo.GetAllFieldsAsync(PostsCollection, PostListFields, limit: 400);
        await AttachPostAuthorNamesAsync(posts);
        posts = posts
            .Where(p => !IsDeletedPost(p))
            .Where(p => isAdmin || IsActivePost(p) || IsPostOwner(p, current.userId!))
            .Where(p => month is null || GetInt(p, "month") == month.Value)
            .OrderBy(p => GetInt(p, "month"))
            .ThenBy(p => Text(p, "title"))
            .ToList();
        return Ok(new { success = true, data = posts });
    }

    [HttpGet("posts/{id}")]
    public async Task<IActionResult> GetPost(string id)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        await EnsureSeedPostsAsync();
        var post = await _repo.GetByIdAsync(PostsCollection, id);
        var isAdmin = IsAdminUser(current.authUser);
        if (post is null || IsDeletedPost(post) || (!isAdmin && !IsActivePost(post) && !IsPostOwner(post, current.userId!))) return NotFound(new { success = false, message = "Không tìm thấy bài viết" });
        await AttachPostAuthorNamesAsync(new List<Dictionary<string, object?>> { post });
        return Ok(new { success = true, data = post });
    }

    [HttpPost("posts/{id}/view")]
    public async Task<IActionResult> TrackPostView(string id)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        await EnsureSeedPostsAsync();
        var post = await _repo.GetByIdAsync(PostsCollection, id);
        var isAdmin = IsAdminUser(current.authUser);
        if (post is null || IsDeletedPost(post) || (!isAdmin && !IsActivePost(post) && !IsPostOwner(post, current.userId!)))
        {
            return NotFound(new { success = false, message = "Không tìm thấy bài viết" });
        }

        var title = Text(post, "title").Trim();
        if (string.IsNullOrWhiteSpace(title)) title = "Bài viết";
        try
        {
            await _repo.AddAsync(PostViewEventsCollection, new Dictionary<string, object?>
            {
                ["post_id"] = id,
                ["postId"] = id,
                ["post_title"] = title,
                ["postTitle"] = title,
                ["user_id"] = current.userId ?? string.Empty,
                ["userId"] = current.userId ?? string.Empty,
                ["source"] = "post-detail",
                ["created_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });
        }
        catch
        {
        }

        return Ok(new { success = true, message = "Đã ghi nhận lượt xem bài viết" });
    }

    [HttpPost("posts/images")]
    public async Task<IActionResult> UploadPostImages([FromForm] List<IFormFile>? images)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (images is null || images.Count == 0) return BadRequest(new { success = false, message = "Vui lòng chọn ảnh minh họa." });

        var urls = new List<string>();
        foreach (var image in images.Where(file => file is not null && file.Length > 0))
        {
            var url = await _fileStorage.SaveImageAsync(image, current.userId!, "posts");
            if (!string.IsNullOrWhiteSpace(url)) urls.Add(url);
        }

        if (urls.Count == 0) return BadRequest(new { success = false, message = "Ảnh không hợp lệ. Chỉ hỗ trợ JPG, PNG, GIF hoặc WEBP, tối đa 10MB." });
        return Ok(new { success = true, urls, images = urls, message = "Đã tải ảnh bài viết" });
    }

    [HttpPost("posts/ai-content")]
    public async Task<IActionResult> GeneratePostContent([FromBody] PostAiContentRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (!CanUseAiPost(current.authUser))
        {
            return StatusCode(403, new { success = false, message = "Tài khoản Free chưa dùng được AI tạo bài viết. Vui lòng nâng cấp VIP hoặc Premium." });
        }

        var festivalKeyword = request.Festival?.Trim();
        if (string.IsNullOrWhiteSpace(festivalKeyword))
        {
            return BadRequest(new { success = false, message = "Vui lòng nhập Lễ hội/ngày lễ trước khi dùng AI." });
        }

        var source = await FindWikipediaSourceAsync(festivalKeyword, festivalKeyword, request.Province);
        if (source is null)
        {
            return NotFound(new
            {
                success = false,
                message = "Không tìm thấy dữ liệu phù hợp. Hãy nhập rõ tên lễ hội/ngày lễ hơn."
            });
        }

        var facts = ExtractPostFacts(source, request);
        var content = BuildVerifiedPostContent(source, facts);
        var summary = BuildVerifiedSummary(source.Extract, facts);
        return Ok(new
        {
            success = true,
            title = BuildVerifiedTitle(source, facts),
            content,
            summary,
            festival = facts.Festival,
            province = facts.Province,
            month = facts.Month,
            holidayType = facts.HolidayType,
            message = "Đã điền nội dung bài viết."
        });
    }

    [HttpPost("posts")]
    public async Task<IActionResult> CreateCommunityPost([FromBody] TravelPostUpsertRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest(new { success = false, message = "Vui lòng nhập tiêu đề bài viết." });

        var data = ToPostData(request, string.Empty);
        var authorName = GetDisplayName(current.authUser, current.userId!);
        data["author_id"] = current.userId!;
        data["authorId"] = current.userId!;
        data["author_name"] = authorName;
        data["authorName"] = authorName;
        data["status"] = "Hiển thị";
        data["source"] = "community";
        data["created_at"] = DateTime.UtcNow;
        data["updated_at"] = DateTime.UtcNow;
        var id = await _repo.AddAsync(PostsCollection, data);
        if (CanUsePostOffer(current.authUser))
        {
            await _offerService.GrantPostOfferAsync(current.userId!, id);
        }
        return Ok(new { success = true, id, message = "Đã thêm bài viết" });
    }

    [HttpPut("posts/{id}")]
    public async Task<IActionResult> UpdateCommunityPost(string id, [FromBody] TravelPostUpsertRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest(new { success = false, message = "Vui lòng nhập tiêu đề bài viết." });

        var saved = await _repo.GetByIdAsync(PostsCollection, id);
        if (saved is null || IsDeletedPost(saved)) return NotFound(new { success = false, message = "Không tìm thấy bài viết" });
        if (!IsPostOwner(saved, current.userId!) && !IsAdminUser(current.authUser))
        {
            return StatusCode(403, new { success = false, message = "Chỉ Admin hoặc người tạo mới được sửa bài viết này." });
        }

        var data = ToPostData(request, id);
        PreservePostMetadata(data, saved, preserveAuthor: true);
        data["last_editor_id"] = current.userId!;
        data["lastEditorId"] = current.userId!;
        data["updated_at"] = DateTime.UtcNow;
        await _repo.UpdateAsync(PostsCollection, id, data);
        return Ok(new { success = true, message = "Đã lưu bài viết" });
    }

    [HttpDelete("posts/{id}")]
    public async Task<IActionResult> DeleteCommunityPost(string id)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var saved = await _repo.GetByIdAsync(PostsCollection, id);
        if (saved is null || IsDeletedPost(saved)) return NotFound(new { success = false, message = "Không tìm thấy bài viết" });
        if (!IsAdminUser(current.authUser) && !IsPostOwner(saved, current.userId!))
        {
            return StatusCode(403, new { success = false, message = "Chỉ Admin hoặc người tạo mới được xóa bài viết này." });
        }

        var ok = await DeletePostRecordAsync(id, saved);
        return ok ? Ok(new { success = true, message = "Đã xóa bài viết" }) : NotFound(new { success = false, message = "Không tìm thấy bài viết" });
    }

    [HttpGet("admin/posts")]
    public async Task<IActionResult> AdminPosts()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        await EnsureSeedPostsAsync();
        var posts = await _repo.GetAllFieldsAsync(PostsCollection, PostListFields, limit: 400);
        await AttachPostAuthorNamesAsync(posts);
        posts = posts.Where(p => !IsDeletedPost(p)).OrderBy(p => GetInt(p, "month")).ThenBy(p => Text(p, "title")).ToList();
        return Ok(new { success = true, data = posts });
    }

    [HttpGet("admin/posts/{id}")]
    public async Task<IActionResult> AdminPost(string id)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        await EnsureSeedPostsAsync();
        var post = await _repo.GetByIdAsync(PostsCollection, id);
        if (post is null || IsDeletedPost(post)) return NotFound(new { success = false, message = "Không tìm thấy bài viết" });
        await AttachPostAuthorNamesAsync(new List<Dictionary<string, object?>> { post });
        return Ok(new { success = true, data = post });
    }

    [HttpPost("admin/posts")]
    public async Task<IActionResult> CreatePost([FromBody] TravelPostUpsertRequest request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest(new { success = false, message = "Vui lòng nhập tiêu đề bài viết." });

        var data = ToPostData(request, string.Empty);
        await ApplyManagedAuthorAsync(data, request);
        data["created_at"] = DateTime.UtcNow;
        data["updated_at"] = DateTime.UtcNow;
        var id = await _repo.AddAsync(PostsCollection, data);
        return Ok(new { success = true, id, message = "Đã thêm bài viết" });
    }

    [HttpPut("admin/posts/{id}")]
    public async Task<IActionResult> UpdatePost(string id, [FromBody] TravelPostUpsertRequest request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest(new { success = false, message = "Vui lòng nhập tiêu đề bài viết." });

        var current = await _repo.GetByIdAsync(PostsCollection, id);
        if (current is null || IsDeletedPost(current)) return NotFound(new { success = false, message = "Không tìm thấy bài viết" });
        var data = ToPostData(request, id);
        await ApplyManagedAuthorAsync(data, request);
        PreservePostMetadata(data, current, preserveAuthor: false);
        data["updated_at"] = DateTime.UtcNow;
        await _repo.UpdateAsync(PostsCollection, id, data);
        return Ok(new { success = true, message = "Đã lưu bài viết" });
    }

    [HttpDelete("admin/posts/{id}")]
    public async Task<IActionResult> DeletePost(string id)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        var saved = await _repo.GetByIdAsync(PostsCollection, id);
        if (saved is null || IsDeletedPost(saved)) return NotFound(new { success = false, message = "Không tìm thấy bài viết" });
        var ok = await DeletePostRecordAsync(id, saved);
        return ok ? Ok(new { success = true, message = "Đã xóa bài viết" }) : NotFound(new { success = false, message = "Không tìm thấy bài viết" });
    }

    private async Task<bool> DeletePostRecordAsync(string id, Dictionary<string, object?> saved)
    {
        if (IsSeedPost(saved) || id.StartsWith("seed-post-", StringComparison.OrdinalIgnoreCase))
        {
            await _repo.UpdateAsync(PostsCollection, id, new Dictionary<string, object?>
            {
                ["is_deleted"] = true,
                ["isDeleted"] = true,
                ["status"] = "Đã xóa",
                ["deleted_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });
            return true;
        }

        return await _repo.DeleteAsync(PostsCollection, id);
    }

    private async Task<VerifiedPostSource?> FindWikipediaSourceAsync(string title, string? festival, string? province)
    {
        var queries = new[]
        {
            title,
            JoinQuery(title, province),
            festival,
            JoinQuery(festival, province),
            JoinQuery(title, "lễ hội"),
            JoinQuery(title, "văn hóa")
        }
        .Where(q => !string.IsNullOrWhiteSpace(q))
        .Select(q => q!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(6)
        .ToList();

        using var http = _httpClientFactory.CreateClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TravelwAI/1.0 (+https://travelwai.local)");

        foreach (var directTitle in new[] { festival, title, JoinQuery(festival, "lễ hội"), JoinQuery(title, "lễ hội") }
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directSource = await GetWikipediaExtractAsync(http, directTitle);
            if (directSource is not null
                && CountWords(directSource.Extract) >= 60
                && SourceLooksRelevant(directSource, title, festival, province))
            {
                return directSource;
            }
        }

        foreach (var query in queries)
        {
            var searchUrl = "https://vi.wikipedia.org/w/api.php?action=query&list=search&format=json&utf8=1&srlimit=5&srnamespace=0&srsearch=" + Uri.EscapeDataString(query);
            string searchText;
            try
            {
                searchText = await http.GetStringAsync(searchUrl);
            }
            catch
            {
                continue;
            }

            var searchJson = JsonNode.Parse(searchText);
            var items = searchJson?["query"]?["search"]?.AsArray();
            if (items is null || items.Count == 0) continue;

            foreach (var item in items)
            {
                var pageTitle = item?["title"]?.ToString();
                if (string.IsNullOrWhiteSpace(pageTitle)) continue;

                var source = await GetWikipediaExtractAsync(http, pageTitle);
                if (source is null) continue;
                if (CountWords(source.Extract) < 60) continue;
                if (!SourceLooksRelevant(source, title, festival, province)) continue;

                return source;
            }
        }

        return null;
    }

    private static string? JoinQuery(string? left, string? right)
    {
        left = left?.Trim();
        right = right?.Trim();
        if (string.IsNullOrWhiteSpace(left)) return null;
        return string.IsNullOrWhiteSpace(right) ? left : $"{left} {right}";
    }

    private static async Task<VerifiedPostSource?> GetWikipediaExtractAsync(HttpClient http, string title)
    {
        var apiUrl = "https://vi.wikipedia.org/w/api.php?action=query&prop=extracts|info&inprop=url&explaintext=1&exsectionformat=plain&redirects=1&format=json&exlimit=1&titles=" + Uri.EscapeDataString(title);
        string text;
        try
        {
            text = await http.GetStringAsync(apiUrl);
        }
        catch
        {
            return null;
        }

        var json = JsonNode.Parse(text);
        var pages = json?["query"]?["pages"]?.AsObject();
        if (pages is null) return null;

        foreach (var page in pages)
        {
            var node = page.Value;
            if (node is null) continue;
            var normalizedTitle = node["title"]?.ToString() ?? title;
            var extract = CleanWikipediaExtract(node["extract"]?.ToString() ?? string.Empty);
            var fullUrl = node["fullurl"]?.ToString();
            if (string.IsNullOrWhiteSpace(extract)) continue;
            if (string.IsNullOrWhiteSpace(fullUrl))
            {
                fullUrl = "https://vi.wikipedia.org/wiki/" + Uri.EscapeDataString(normalizedTitle.Replace(' ', '_'));
            }
            return new VerifiedPostSource(normalizedTitle, extract, fullUrl);
        }

        return null;
    }

    private static bool SourceLooksRelevant(VerifiedPostSource source, string title, string? festival, string? province)
    {
        var haystack = NormalizeForMatch(source.Title + " " + source.Extract);
        var sourceTitle = NormalizeForMatch(source.Title);
        var corePhrases = new[] { festival, title }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeForMatch)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (corePhrases.Any(phrase => sourceTitle.Contains(phrase, StringComparison.OrdinalIgnoreCase)
            || haystack.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var needles = new[] { title, festival }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(SplitImportantTerms)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (needles.Count == 0) return false;
        var matches = needles.Count(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
        return matches >= Math.Min(2, needles.Count);
    }

    private static IEnumerable<string> SplitImportantTerms(string? value)
    {
        var normalized = NormalizeForMatch(value);
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "le", "hoi", "ngay", "tet", "du", "xuan", "kham", "pha", "van", "hoa", "lich", "su", "tai", "o", "va", "cua", "cho", "voi", "ban"
        };
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3 && !stopWords.Contains(x));
    }

    private static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var chars = text.Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray();
        return Regex.Replace(new string(chars).Replace('đ', 'd'), @"[^a-z0-9 ]+", " ").Trim();
    }

    private static string CleanWikipediaExtract(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = WebUtility.HtmlDecode(value).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        var keptLines = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (IsWikipediaTailSectionHeading(line)) break;
            if (IsWikipediaSourceLine(line)) continue;
            keptLines.Add(rawLine);
        }

        text = string.Join("\n", keptLines);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\s+\n", "\n");
        return text.Trim();
    }

    private static bool IsWikipediaTailSectionHeading(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var clean = Regex.Replace(line.Trim('=').Trim(), @"\s+", " ");
        var normalized = NormalizeForMatch(clean);
        var blockedHeadings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "xem them",
            "tham khao",
            "lien ket ngoai",
            "chu thich",
            "ghi chu",
            "nguon tham khao",
            "thu muc"
        };
        return blockedHeadings.Contains(normalized);
    }

    private static bool IsWikipediaSourceLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return Regex.IsMatch(line, @"^Nguồn\s+dữ\s+liệu", RegexOptions.IgnoreCase)
            || line.Contains("Wikipedia tiếng Việt", StringComparison.OrdinalIgnoreCase)
            || line.Contains("vi.wikipedia.org", StringComparison.OrdinalIgnoreCase);
    }

    private static VerifiedPostFacts ExtractPostFacts(VerifiedPostSource source, PostAiContentRequest request)
    {
        var overrideFacts = GetFestivalFactOverride(request.Festival ?? source.Title);
        var festival = overrideFacts?.Festival ?? ChooseFestivalName(request.Festival, request.Title, source.Title);
        var sourceText = string.Join(" ", new[] { request.Festival, source.Title, source.Extract, request.Title, request.Province });
        var month = overrideFacts?.Month ?? InferMonth(sourceText) ?? request.Month;
        var provinceFromSource = InferVietnamProvince(source.Title + " " + source.Extract);
        var province = overrideFacts?.Province
            ?? (!string.IsNullOrWhiteSpace(provinceFromSource) ? provinceFromSource : request.Province?.Trim() ?? string.Empty);
        var holidayType = overrideFacts?.HolidayType ?? InferHolidayType(sourceText);
        return new VerifiedPostFacts(festival, province, month, holidayType);
    }

    private static VerifiedPostFacts? GetFestivalFactOverride(string? value)
    {
        var normalized = NormalizeForMatch(value);
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        if (normalized.Contains("hoi lim", StringComparison.OrdinalIgnoreCase) || normalized == "lim")
        {
            return new VerifiedPostFacts("Hội Lim", "Bắc Ninh", 1, "Lễ hội dân gian");
        }

        return null;
    }

    private static string ChooseFestivalName(string? festival, string? title, string sourceTitle)
    {
        if (!string.IsNullOrWhiteSpace(festival)) return festival.Trim();
        var cleanTitle = title?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(cleanTitle) && LooksLikeFestivalOrHoliday(cleanTitle)) return cleanTitle;
        return sourceTitle.Trim();
    }

    private static bool LooksLikeFestivalOrHoliday(string value)
    {
        var normalized = NormalizeForMatch(value);
        var terms = new[] { "le", "hoi", "ngay", "tet", "gio", "ky niem", "festival", "vu lan", "trung thu" };
        return terms.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static int? InferMonth(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = NormalizeForMatch(value);

        var numberMatch = Regex.Match(normalized, @"thang\s+(1[0-2]|0?[1-9])");
        if (numberMatch.Success && int.TryParse(numberMatch.Groups[1].Value, out var numberMonth)) return numberMonth;

        var numericDateMatch = Regex.Match(normalized, @"(?:ngay\s*)?\d{1,2}\s*[/-]\s*(1[0-2]|0?[1-9])");
        if (numericDateMatch.Success && int.TryParse(numericDateMatch.Groups[1].Value, out var dateMonth)) return dateMonth;

        var monthWords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["gieng"] = 1,
            ["mot"] = 1,
            ["hai"] = 2,
            ["ba"] = 3,
            ["tu"] = 4,
            ["bon"] = 4,
            ["nam"] = 5,
            ["sau"] = 6,
            ["bay"] = 7,
            ["tam"] = 8,
            ["chin"] = 9,
            ["muoi"] = 10,
            ["muoi mot"] = 11,
            ["muoi hai"] = 12,
            ["chap"] = 12
        };

        foreach (var pair in monthWords.OrderByDescending(x => x.Key.Length))
        {
            if (Regex.IsMatch(normalized, @"thang\s+" + Regex.Escape(pair.Key) + @"(\s|$)")) return pair.Value;
        }

        return null;
    }

    private static string InferVietnamProvince(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = NormalizeForMatch(value);
        foreach (var province in VietnamProvinceNames.OrderByDescending(x => x.Length))
        {
            var provinceKey = NormalizeProvinceKey(province);
            if (!string.IsNullOrWhiteSpace(provinceKey)
                && Regex.IsMatch(normalized, $@"\b(tinh|thanh pho|tp)\s+{Regex.Escape(provinceKey)}\b", RegexOptions.IgnoreCase))
            {
                return province;
            }
        }

        foreach (var province in VietnamProvinceNames.OrderByDescending(x => x.Length))
        {
            var provinceKey = NormalizeForMatch(province);
            var bareProvinceKey = NormalizeProvinceKey(province);
            if ((!string.IsNullOrWhiteSpace(provinceKey) && normalized.Contains(provinceKey, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(bareProvinceKey) && normalized.Contains(bareProvinceKey, StringComparison.OrdinalIgnoreCase)))
            {
                return province;
            }
        }
        return string.Empty;
    }

    private static string NormalizeProvinceKey(string province)
    {
        var normalized = NormalizeForMatch(province);
        normalized = Regex.Replace(normalized, @"^(thanh pho|tp|tinh)\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        return normalized;
    }

    private static string InferHolidayType(string value)
    {
        var normalized = NormalizeForMatch(value);
        if (normalized.Contains("phat giao") || normalized.Contains("chua") || normalized.Contains("vu lan")) return "Lễ hội Phật giáo";
        if (normalized.Contains("lich su") || normalized.Contains("chien") || normalized.Contains("anh hung") || normalized.Contains("ky niem")) return "Ngày lễ lịch sử";
        if (normalized.Contains("dan gian") || normalized.Contains("dinh lang") || normalized.Contains("hoi lang")) return "Lễ hội dân gian";
        if (normalized.Contains("khmer") || normalized.Contains("mong") || normalized.Contains("tay") || normalized.Contains("thai") || normalized.Contains("ede")) return "Lễ hội dân tộc";
        if (normalized.Contains("bien") || normalized.Contains("ngu dan") || normalized.Contains("cau ngu") || normalized.Contains("nghinh ong")) return "Lễ hội biển";
        if (normalized.Contains("tet")) return "Ngày Tết truyền thống";
        if (normalized.Contains("ngay")) return "Ngày lễ";
        return "Lễ hội văn hoá lịch sử";
    }

    private static string BuildVerifiedTitle(VerifiedPostSource source, VerifiedPostFacts facts)
    {
        var festival = string.IsNullOrWhiteSpace(facts.Festival) ? source.Title : facts.Festival.Trim();
        return $"Khám phá văn hoá lịch sử {festival}";
    }

    private static string BuildVerifiedPostContent(VerifiedPostSource source, VerifiedPostFacts facts)
    {
        var title = string.IsNullOrWhiteSpace(facts.Festival) ? source.Title : facts.Festival;
        var excerpt = LimitToSentenceBoundary(source.Extract, 2400);
        var parts = new List<string>
        {
            $"Khám phá văn hoá lịch sử: {title}"
        };

        if (facts.Month is >= 1 and <= 12) parts.Add($"Thời điểm: tháng {facts.Month}");
        if (!string.IsNullOrWhiteSpace(facts.Province)) parts.Add($"Tỉnh/thành: {facts.Province}");
        parts.Add(excerpt);

        return string.Join(Environment.NewLine + Environment.NewLine, parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string BuildVerifiedSummary(string extract, VerifiedPostFacts facts)
    {
        var title = string.IsNullOrWhiteSpace(facts.Festival) ? "lễ hội/ngày lễ này" : facts.Festival;
        var prefix = $"Khám phá văn hoá lịch sử về {title}. ";
        var summary = LimitToSentenceBoundary(prefix + extract, 300);
        return summary.Length > 300 ? summary[..300].Trim() : summary;
    }

    private static readonly string[] VietnamProvinceNames =
    {
        "Thành phố Hà Nội", "Thành phố Hải Phòng", "Thành phố Huế", "Thành phố Đà Nẵng", "Thành phố Cần Thơ", "TP. Hồ Chí Minh",
        "Cao Bằng", "Điện Biên", "Lai Châu", "Lạng Sơn", "Lào Cai", "Phú Thọ", "Quảng Ninh", "Sơn La", "Thái Nguyên", "Tuyên Quang",
        "Bắc Ninh", "Hưng Yên", "Ninh Bình", "Thanh Hóa", "Nghệ An", "Hà Tĩnh", "Quảng Trị", "Quảng Ngãi", "Gia Lai", "Khánh Hòa", "Lâm Đồng", "Đắk Lắk",
        "Đồng Nai", "Tây Ninh", "Vĩnh Long", "Đồng Tháp", "Cà Mau", "An Giang",
        "Hà Giang", "Yên Bái", "Hòa Bình", "Vĩnh Phúc", "Bắc Giang", "Hải Dương", "Hà Nam", "Nam Định", "Thái Bình", "Hà Tây", "Quảng Nam", "Bình Định", "Phú Yên", "Ninh Thuận", "Bình Thuận", "Đắk Nông", "Bình Phước", "Bà Rịa - Vũng Tàu", "Long An", "Bến Tre", "Trà Vinh", "Sóc Trăng", "Bạc Liêu", "Hậu Giang", "Kiên Giang", "Tiền Giang", "Bắc Kạn", "Kon Tum", "Hồ Chí Minh", "Sài Gòn"
    };

    private static string LimitToSentenceBoundary(string value, int maxLength)
    {
        value = CleanWikipediaExtract(value);
        if (value.Length <= maxLength) return value;
        var cut = value[..maxLength];
        var lastSentence = Math.Max(cut.LastIndexOf('.'), Math.Max(cut.LastIndexOf('!'), cut.LastIndexOf('?')));
        if (lastSentence > 160) return cut[..(lastSentence + 1)].Trim();
        return cut.TrimEnd() + "...";
    }

    private static string? CleanAuthorName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.StartsWith("Tài khoản ", StringComparison.OrdinalIgnoreCase))
        {
            name = name[10..].Trim();
        }
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private Dictionary<string, object?> ToPostData(TravelPostUpsertRequest request, string id)
    {
        var month = Math.Clamp(request.Month ?? DateTime.Now.Month, 1, 12);
        var images = CleanImageUrls(request.ImageUrls);
        return new Dictionary<string, object?>
        {
            ["title"] = request.Title?.Trim() ?? string.Empty,
            ["summary"] = request.Summary?.Trim() ?? string.Empty,
            ["content"] = request.Content?.Trim() ?? string.Empty,
            ["month"] = month,
            ["festival"] = request.Festival?.Trim() ?? string.Empty,
            ["province"] = request.Province?.Trim() ?? string.Empty,
            ["holiday_type"] = request.HolidayType?.Trim() ?? string.Empty,
            ["tour_keywords"] = request.TourKeywords?.Trim() ?? string.Empty,
            ["author_id"] = request.AuthorId?.Trim() ?? string.Empty,
            ["authorId"] = request.AuthorId?.Trim() ?? string.Empty,
            ["author_name"] = CleanAuthorName(request.AuthorName) ?? "TravelwAI",
            ["authorName"] = CleanAuthorName(request.AuthorName) ?? "TravelwAI",
            ["image_urls"] = images,
            ["imageUrls"] = images,
            ["images"] = images,
            ["status"] = string.IsNullOrWhiteSpace(request.Status) ? "Hiển thị" : request.Status.Trim()
        };
    }

    private async Task ApplyManagedAuthorAsync(Dictionary<string, object?> data, TravelPostUpsertRequest request)
    {
        var authorId = request.AuthorId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(authorId)) return;

        var account = await _repo.GetByIdAsync("users", authorId);
        var managedName = ManagedAccountDisplayName(account);
        if (string.IsNullOrWhiteSpace(managedName)) return;

        data["author_id"] = authorId;
        data["authorId"] = authorId;
        data["author_name"] = managedName;
        data["authorName"] = managedName;
    }

    private async Task AttachPostAuthorNamesAsync(List<Dictionary<string, object?>> posts)
    {
        if (posts.Count == 0) return;

        var authorIds = posts
            .Select(post => Text(post, "author_id"))
            .Select(id => string.IsNullOrWhiteSpace(id) ? string.Empty : id)
            .Concat(posts.Select(post => Text(post, "authorId")))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (authorIds.Count == 0) return;

        var accounts = await _repo.GetAllFieldsAsync("users", new[] { "username", "displayName", "display_name", "name", "email" }, limit: 1000);
        var accountMap = accounts
            .Where(account => !string.IsNullOrWhiteSpace(Text(account, "id")))
            .GroupBy(account => Text(account, "id"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var post in posts)
        {
            var authorId = Text(post, "author_id");
            if (string.IsNullOrWhiteSpace(authorId)) authorId = Text(post, "authorId");
            if (string.IsNullOrWhiteSpace(authorId) || !accountMap.TryGetValue(authorId, out var account)) continue;

            var managedName = ManagedAccountDisplayName(account);
            if (string.IsNullOrWhiteSpace(managedName)) continue;

            post["author_name"] = managedName;
            post["authorName"] = managedName;
        }
    }

    private static string? ManagedAccountDisplayName(Dictionary<string, object?>? account)
    {
        if (account is null) return null;
        foreach (var key in new[] { "username", "displayName", "display_name", "name", "email" })
        {
            var name = CleanAuthorName(Text(account, key));
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return null;
    }

    private static void PreservePostMetadata(Dictionary<string, object?> data, Dictionary<string, object?> saved, bool preserveAuthor)
    {
        var keys = preserveAuthor
            ? new[] { "author_id", "authorId", "author_name", "authorName", "source", "seed_version", "created_at" }
            : new[] { "source", "seed_version", "created_at" };

        foreach (var key in keys)
        {
            if (saved.TryGetValue(key, out var value) && value is not null && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                data[key] = value;
            }
        }
    }

    private async Task EnsureSeedPostsAsync()
    {
        var seedPosts = SeedPosts();
        var seedIds = seedPosts
            .Select(post => Text(post, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existing = await _repo.GetAllFieldsAsync(PostsCollection, PostSeedCheckFields, limit: 400);
        var byId = existing
            .Where(row => !string.IsNullOrWhiteSpace(Text(row, "id")))
            .ToDictionary(row => Text(row, "id"), row => row, StringComparer.OrdinalIgnoreCase);

        foreach (var row in existing)
        {
            var id = Text(row, "id");
            if (id.StartsWith("seed-post-", StringComparison.OrdinalIgnoreCase) && !seedIds.Contains(id))
            {
                await _repo.DeleteAsync(PostsCollection, id);
            }
        }

        foreach (var post in seedPosts)
        {
            var id = Text(post, "id");
            if (!byId.TryGetValue(id, out var saved) || Text(saved, "seed_version") != SeedVersion)
            {
                await _repo.SetAsync(PostsCollection, id, post, merge: false);
                continue;
            }

            var savedAuthorId = Text(saved, "author_id");
            if (string.IsNullOrWhiteSpace(savedAuthorId)) savedAuthorId = Text(saved, "authorId");
            var savedAuthorName = CleanAuthorName(Text(saved, "author_name"));
            if (string.IsNullOrWhiteSpace(savedAuthorName)) savedAuthorName = CleanAuthorName(Text(saved, "authorName"));
            if (string.IsNullOrWhiteSpace(savedAuthorId) && string.Equals(savedAuthorName, "An Nhiên", StringComparison.OrdinalIgnoreCase))
            {
                await _repo.UpdateAsync(PostsCollection, id, new Dictionary<string, object?>
                {
                    ["author_name"] = "Pastic",
                    ["authorName"] = "Pastic",
                    ["updated_at"] = DateTime.UtcNow
                });
            }
        }
    }

    private static List<Dictionary<string, object?>> SeedPosts()
    {
        var authors = new[] { "Pastic", "Admin TravelwAI", "Việt Hành", "Sắc Việt" };
        var seedImages = new[]
        {
            new[] { "/main_site_image/back1.webp" },
            new[] { "/main_site_image/back2.webp", "/main_site_image/back3.webp" },
            new[] { "/main_site_image/back4.webp" },
            new[] { "/main_site_image/back5.webp", "/main_site_image/back1.webp" }
        };
        var rows = new (int month, string title, string festival, string province, string type, string keywords, string summary)[]
        {
            (1, "Du xuân Hội Lim Bắc Ninh", "Hội Lim", "Bắc Ninh", "Lễ hội dân gian", "Bắc Ninh, quan họ, du xuân", "Hội Lim là điểm hẹn đầu năm của người yêu quan họ và không gian văn hóa Kinh Bắc."),
            (1, "Về Hà Nội xem hội Gióng đầu xuân", "Hội Gióng", "Hà Nội", "Lễ hội truyền thống", "Hà Nội, Thánh Gióng, Cổ Loa", "Hội Gióng gợi nhớ truyền thuyết người anh hùng làng Gióng đánh giặc giữ nước."),
            (1, "Gầu Tào trên núi rừng Tây Bắc", "Gầu Tào", "Lào Cai", "Lễ hội dân tộc Mông", "Lào Cai, Hà Giang, Tây Bắc, Mông", "Gầu Tào là lễ hội cầu phúc, cầu may đặc sắc của người Mông."),
            (2, "Hoa Ban và chuyện tình Tây Bắc", "Lễ hội Hoa Ban", "Điện Biên", "Lễ hội văn hóa Thái", "Điện Biên, Sơn La, hoa ban", "Hoa Ban gắn với vẻ đẹp núi rừng và câu chuyện tình trong văn hóa người Thái."),
            (2, "Lễ hội chùa Hương Tích Hà Tĩnh", "Chùa Hương Tích", "Hà Tĩnh", "Lễ hội tâm linh", "Hà Tĩnh, Hồng Lĩnh, tâm linh", "Chùa Hương Tích nằm giữa núi rừng Hồng Lĩnh, phù hợp cho chuyến đi đầu năm."),
            (2, "Nàng Hai Cao Bằng giữa mùa xuân", "Lễ hội Nàng Hai", "Cao Bằng", "Lễ hội dân tộc Tày", "Cao Bằng, Tày, Then", "Nàng Hai là lễ hội cầu mùa, cầu phúc của cộng đồng Tày ở Cao Bằng."),
            (3, "Về Đền Hùng trong tháng ba", "Giỗ Tổ Hùng Vương", "Phú Thọ", "Ngày lễ dân tộc", "Phú Thọ, Đền Hùng, cội nguồn", "Giỗ Tổ Hùng Vương là dịp tìm về cội nguồn dân tộc Việt."),
            (3, "Quán Thế Âm Ngũ Hành Sơn", "Lễ hội Quán Thế Âm", "Đà Nẵng", "Lễ hội tâm linh", "Đà Nẵng, Ngũ Hành Sơn, Hội An", "Lễ hội Quán Thế Âm đưa du khách đến không gian văn hóa Phật giáo và danh thắng Ngũ Hành Sơn."),
            (3, "Khao lề thế lính Hoàng Sa ở Lý Sơn", "Khao lề thế lính Hoàng Sa", "Quảng Ngãi", "Lễ hội biển đảo", "Quảng Ngãi, Lý Sơn, Hoàng Sa", "Lễ hội nhắc nhớ đội hùng binh Hoàng Sa và văn hóa biển đảo Việt Nam."),
            (4, "Chol Chnam Thmay ở miền Tây", "Chol Chnam Thmay", "Cần Thơ", "Lễ hội dân tộc Khmer", "Cần Thơ, Sóc Trăng, Trà Vinh, Khmer", "Chol Chnam Thmay là Tết cổ truyền của người Khmer Nam Bộ."),
            (4, "Vía Bà Chúa Xứ Núi Sam", "Vía Bà Chúa Xứ", "An Giang", "Lễ hội tâm linh", "An Giang, Châu Đốc, Núi Sam", "Vía Bà Chúa Xứ là lễ hội lớn ở vùng Bảy Núi, thu hút đông đảo du khách hành hương."),
            (5, "Làng Sen tháng năm", "Lễ hội Làng Sen", "Nghệ An", "Ngày kỷ niệm lịch sử", "Nghệ An, Kim Liên, Hồ Chí Minh", "Làng Sen là điểm đến ý nghĩa trong tháng sinh Chủ tịch Hồ Chí Minh."),
            (5, "Lễ Phật Đản ở cố đô Huế", "Lễ Phật Đản", "Huế", "Lễ hội Phật giáo", "Huế, chùa Thiên Mụ, Đại Nội", "Huế là điểm đến nổi bật để cảm nhận mùa Phật Đản trang nghiêm và nhiều sắc màu."),
            (6, "Lễ hội dừa Vĩnh Long", "Lễ hội dừa", "Vĩnh Long", "Lễ hội cộng đồng", "Vĩnh Long, Bến Tre, miệt vườn, dừa", "Lễ hội dừa tôn vinh cây dừa và đời sống miệt vườn Nam Bộ."),
            (6, "Cầu ngư mùa biển miền Trung", "Lễ hội Cầu ngư", "Đà Nẵng", "Lễ hội ngư dân", "Đà Nẵng, Quảng Ngãi, biển, cầu ngư", "Cầu ngư thể hiện tín ngưỡng biển và ước vọng mùa cá bình an."),
            (7, "Tri ân Thành cổ Quảng Trị", "Lễ tri ân Thành cổ", "Quảng Trị", "Ngày tưởng niệm lịch sử", "Quảng Trị, Thành cổ, Hiền Lương", "Tháng 7 là thời điểm nhiều du khách về Quảng Trị để tưởng niệm lịch sử."),
            (7, "Lễ Vu Lan ở Ninh Bình", "Lễ Vu Lan", "Ninh Bình", "Lễ hội Phật giáo", "Ninh Bình, Bái Đính, Tam Chúc", "Vu Lan là dịp hướng về cha mẹ và những giá trị hiếu nghĩa."),
            (8, "Trung thu phố cổ Hội An", "Tết Trung thu", "Đà Nẵng", "Lễ hội dân gian", "Hội An, Đà Nẵng, lồng đèn", "Trung thu ở Hội An nổi bật với lồng đèn, phố cổ và các hoạt động dân gian."),
            (8, "Nghinh Ông vùng biển Nam Bộ", "Nghinh Ông", "TP. Hồ Chí Minh", "Lễ hội ngư dân", "Cần Giờ, Vũng Tàu, biển, ngư dân", "Nghinh Ông là lễ hội của cộng đồng ngư dân, thể hiện lòng biết ơn cá Ông."),
            (8, "Kiếp Bạc và dấu ấn Đức Thánh Trần", "Lễ hội Kiếp Bạc", "Hải Phòng", "Lễ hội lịch sử", "Côn Sơn, Kiếp Bạc, Trần Hưng Đạo", "Kiếp Bạc là điểm đến gắn với Trần Hưng Đạo và truyền thống chống giặc giữ nước."),
            (9, "Đua bò Bảy Núi An Giang", "Đua bò Bảy Núi", "An Giang", "Lễ hội dân tộc Khmer", "An Giang, Bảy Núi, Khmer", "Đua bò Bảy Núi là lễ hội sôi động của người Khmer vùng Tri Tôn, Tịnh Biên."),
            (9, "Mùa ruộng bậc thang Mù Cang Chải", "Lễ hội ruộng bậc thang", "Lào Cai", "Lễ hội mùa vàng", "Mù Cang Chải, Tây Bắc, mùa vàng", "Tháng 9 là thời điểm lý tưởng để khám phá mùa vàng ruộng bậc thang Tây Bắc."),
            (9, "Mừng lúa mới Ê Đê ở Tây Nguyên", "Mừng lúa mới", "Đắk Lắk", "Lễ hội dân tộc Ê Đê", "Đắk Lắk, Ê Đê, cồng chiêng", "Mừng lúa mới thể hiện lòng biết ơn thần lúa và cộng đồng buôn làng."),
            (10, "Oóc Om Bóc và đua ghe ngo", "Oóc Om Bóc", "Cần Thơ", "Lễ hội dân tộc Khmer", "Sóc Trăng, Trà Vinh, Cần Thơ, Khmer", "Oóc Om Bóc là lễ cúng trăng nổi bật của người Khmer Nam Bộ."),
            (10, "Tết Trùng Cửu ở các điểm tâm linh", "Tết Trùng Cửu", "Ninh Bình", "Lễ tiết âm lịch", "Ninh Bình, Huế, tâm linh", "Tết Trùng Cửu gợi ý các chuyến đi nhẹ nhàng về chùa, núi và không gian thanh tịnh."),
            (11, "Ngày Di sản Văn hóa Việt Nam", "Ngày Di sản Văn hóa Việt Nam", "Hà Nội", "Ngày kỷ niệm văn hóa", "Hà Nội, Huế, Hội An, di sản", "Tháng 11 là dịp phù hợp để khám phá di sản văn hóa Việt Nam."),
            (11, "Tết Hạ Nguyên và văn hóa cuối thu", "Tết Hạ Nguyên", "Ninh Bình", "Lễ tiết âm lịch", "Ninh Bình, Huế, chùa, lễ tiết", "Tết Hạ Nguyên là dịp hướng về sự biết ơn và cầu an cuối năm âm lịch."),
            (12, "Giáng sinh ở Đà Lạt", "Giáng sinh", "Lâm Đồng", "Ngày hội cuối năm", "Đà Lạt, Giáng sinh, Festival Hoa", "Đà Lạt cuối năm phù hợp với không khí Giáng sinh, hoa và nghỉ dưỡng."),
            (12, "Quân đội nhân dân Việt Nam và hành trình lịch sử", "Ngày thành lập Quân đội nhân dân Việt Nam", "Điện Biên", "Ngày kỷ niệm lịch sử", "Điện Biên, Quảng Trị, lịch sử", "Tháng 12 phù hợp với các bài viết về chiến trường xưa và truyền thống quân đội."),
            (12, "Mùa lễ hội hoa cuối năm", "Festival Hoa Đà Lạt", "Lâm Đồng", "Lễ hội du lịch", "Đà Lạt, hoa, nghỉ dưỡng", "Festival Hoa Đà Lạt tôn vinh không gian hoa, nông nghiệp và du lịch cao nguyên.")
        };

        var list = new List<Dictionary<string, object?>>();
        for (var i = 0; i < Math.Min(rows.Length, SeedPostLimit); i++)
        {
            var r = rows[i];
            var images = seedImages[i % seedImages.Length].ToList();
            list.Add(new Dictionary<string, object?>
            {
                ["id"] = $"seed-post-{i + 1:00}",
                ["title"] = r.title,
                ["summary"] = r.summary,
                ["content"] = string.Empty,
                ["month"] = r.month,
                ["festival"] = r.festival,
                ["province"] = r.province,
                ["holiday_type"] = r.type,
                ["tour_keywords"] = r.keywords,
                ["author_id"] = string.Empty,
                ["authorId"] = string.Empty,
                ["author_name"] = authors[i % authors.Length],
                ["authorName"] = authors[i % authors.Length],
                ["image_urls"] = images,
                ["imageUrls"] = images,
                ["images"] = images,
                ["status"] = "Hiển thị",
                ["source"] = "seed",
                ["seed_version"] = SeedVersion,
                ["created_at"] = DateTime.UtcNow.AddDays(-rows.Length + i),
                ["updated_at"] = DateTime.UtcNow.AddDays(-rows.Length + i)
            });
        }
        return list;
    }

    private static string BuildLongContent(string title, string festival, string province, string type, string keywords, int month)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Khám phá văn hóa Việt Nam" : title.Trim();
        festival = string.IsNullOrWhiteSpace(festival) ? "không gian văn hóa địa phương" : festival.Trim();
        province = string.IsNullOrWhiteSpace(province) ? "Việt Nam" : province.Trim();
        type = string.IsNullOrWhiteSpace(type) ? "văn hóa bản địa" : type.Trim();
        keywords = string.IsNullOrWhiteSpace(keywords) ? $"{province}, khám phá, ý nghĩa" : keywords.Trim();
        month = Math.Clamp(month <= 0 ? DateTime.Now.Month : month, 1, 12);

        return string.Join(Environment.NewLine + Environment.NewLine, new[]
        {
            $"{title} giới thiệu {festival} tại {province}, tập trung vào văn hoá, lịch sử và trải nghiệm địa phương. Nội dung giúp người đọc biết điểm nổi bật, bối cảnh hình thành và lý do nên ghé thăm vào tháng {month}.",
            $"Các từ khóa chính của hành trình là {keywords}. Đây là những gợi ý để tìm hiểu địa điểm, hoạt động, phong tục và câu chuyện gắn với cộng đồng bản địa.",
            $"Khi đến {province}, người đọc có thể bắt đầu bằng các điểm tham quan nổi bật, sau đó tìm hiểu thêm về ẩm thực, làng nghề, lễ hội hoặc di tích gần đó. Nên dành thời gian hỏi người địa phương để hiểu rõ hơn về nguồn gốc và ý nghĩa của từng trải nghiệm.",
            $"Bài viết hướng đến cách đi du lịch có hiểu biết và tôn trọng văn hoá địa phương. Sau chuyến đi, người đọc không chỉ có hình ảnh đẹp mà còn hiểu thêm về con người, ký ức và giá trị của {province}."
        });
    }

    private static string BuildSummary(string title, string festival, string province)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Khám phá văn hóa Việt Nam" : title.Trim();
        festival = string.IsNullOrWhiteSpace(festival) ? "không gian văn hóa địa phương" : festival.Trim();
        province = string.IsNullOrWhiteSpace(province) ? "Việt Nam" : province.Trim();
        return $"{title} tập trung vào hành trình khám phá {festival} tại {province} và ý nghĩa văn hóa phía sau từng chi tiết địa phương.";
    }

    private static int CountWords(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? 0 : WordRegex.Matches(value).Count;
    }

    private static List<string> CleanImageUrls(List<string>? urls)
    {
        return (urls ?? new List<string>())
            .Select(url => url?.Trim() ?? string.Empty)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static string GetDisplayName(Dictionary<string, object?>? user, string fallback)
    {
        foreach (var key in new[] { "displayName", "username", "name", "email" })
        {
            if (user is not null && user.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString()))
            {
                return value!.ToString()!;
            }
        }
        return fallback;
    }

    private async Task<(bool ok, IActionResult? error)> RequireAdminAsync()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return (false, current.error);
        if (!IsAdminUser(current.authUser))
        {
            return (false, StatusCode(403, new { success = false, message = "Chỉ Admin mới được truy cập." }));
        }
        return (true, null);
    }

    private static bool IsAdminUser(Dictionary<string, object?>? user)
    {
        var role = user?.GetValueOrDefault("role")?.ToString() ?? string.Empty;
        return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeletedPost(Dictionary<string, object?> post)
    {
        return IsTruthy(post.GetValueOrDefault("is_deleted"))
            || IsTruthy(post.GetValueOrDefault("isDeleted"))
            || string.Equals(Text(post, "status"), "Đã xóa", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSeedPost(Dictionary<string, object?> post)
    {
        return string.Equals(Text(post, "source"), "seed", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Text(post, "seed_version"));
    }

    private static bool IsTruthy(object? value)
    {
        if (value is null) return false;
        if (value is bool b) return b;
        var text = value.ToString()?.Trim();
        return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActivePost(Dictionary<string, object?> post)
    {
        var status = Text(post, "status");
        return string.IsNullOrWhiteSpace(status) || !status.Equals("Ẩn", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPostOwner(Dictionary<string, object?> post, string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var authorId = Text(post, "author_id");
        if (string.IsNullOrWhiteSpace(authorId)) authorId = Text(post, "authorId");
        return !string.IsNullOrWhiteSpace(authorId) && string.Equals(authorId, userId, StringComparison.Ordinal);
    }

    private static int GetInt(Dictionary<string, object?> row, string key)
    {
        return row.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var parsed) ? parsed : 0;
    }

    private static string Text(Dictionary<string, object?> row, string key)
    {
        return row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    }

    public sealed class PostAiContentRequest
    {
        public string? Title { get; set; }
        public string? Festival { get; set; }
        public string? Province { get; set; }
        public int? Month { get; set; }
    }

    private sealed record VerifiedPostSource(string Title, string Extract, string Url);

    private sealed record VerifiedPostFacts(string Festival, string Province, int? Month, string HolidayType);

    public sealed class TravelPostUpsertRequest
    {
        public string? Title { get; set; }
        public string? Summary { get; set; }
        public string? Content { get; set; }
        public int? Month { get; set; }
        public string? Festival { get; set; }
        public string? Province { get; set; }
        public string? HolidayType { get; set; }
        public string? TourKeywords { get; set; }
        public string? AuthorId { get; set; }
        public string? AuthorName { get; set; }
        public List<string>? ImageUrls { get; set; }
        public string? Status { get; set; }
    }
}
