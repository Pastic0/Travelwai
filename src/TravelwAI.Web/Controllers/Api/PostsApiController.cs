using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class PostsApiController : ApiControllerBase
{
    private const string PostsCollection = "travel_posts";
    private const string PostAiGenerationCollection = "post_ai_generations";
    private const string PostViewEventsCollection = "post_view_events";
    private const string PostLikesCollection = "post_likes";
    private const string PostCommentsCollection = "post_comments";
    private const string PostRatingsCollection = "post_ratings";
    private const string SeedVersion = "2026-07-09-clean-v1";
    private const int SeedPostLimit = 10;
    private static readonly string[] PostListFields =
    {
        "title", "summary", "month", "festival", "province", "holiday_type", "holidayType",
        "tour_keywords", "tourKeywords", "author_id", "authorId", "author_name", "authorName",
        "image_urls", "imageUrls", "images", "video_urls", "videoUrls", "media", "media_items", "mediaItems", "media_urls", "mediaUrls", "status", "source", "is_deleted", "isDeleted", "deleted_at", "updated_at"
    };
    private static readonly string[] PostSeedCheckFields = { "seed_version", "source", "author_id", "authorId", "author_name", "authorName", "is_deleted", "isDeleted", "deleted_at" };
    private readonly IDataRepository _repo;
    private readonly IFileStorageService _fileStorage;
    private readonly TourOfferService _offerService;

    public PostsApiController(IAuthService authService, IDataRepository repo, IFileStorageService fileStorage, TourOfferService offerService) : base(authService)
    {
        _repo = repo;
        _fileStorage = fileStorage;
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
        await AttachPostEngagementAsync(posts, current.userId!);
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
        var singlePost = new List<Dictionary<string, object?>> { post };
        await AttachPostAuthorNamesAsync(singlePost);
        await AttachPostEngagementAsync(singlePost, current.userId!);
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


    [HttpPost("posts/{id}/like")]
    public async Task<IActionResult> TogglePostLike(string id)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var post = await GetAccessiblePostAsync(id, current.userId!, current.authUser);
        if (post is null) return NotFound(new { success = false, message = "Không tìm thấy bài viết" });

        var likeId = StableInteractionId("like", id, current.userId!);
        var existing = await _repo.GetByIdAsync(PostLikesCollection, likeId);
        var liked = existing is null;
        if (liked)
        {
            await _repo.SetAsync(PostLikesCollection, likeId, new Dictionary<string, object?>
            {
                ["post_id"] = id,
                ["postId"] = id,
                ["user_id"] = current.userId!,
                ["userId"] = current.userId!,
                ["created_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            }, merge: false);
        }
        else
        {
            await _repo.DeleteAsync(PostLikesCollection, likeId);
        }

        var summary = await GetPostEngagementSummaryAsync(id, current.userId!);
        return Ok(new { success = true, liked, data = summary, message = liked ? "Đã thích bài viết" : "Đã bỏ thích bài viết" });
    }

    [HttpPut("posts/{id}/rating")]
    public async Task<IActionResult> RatePost(string id, [FromBody] PostRatingRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var post = await GetAccessiblePostAsync(id, current.userId!, current.authUser);
        if (post is null) return NotFound(new { success = false, message = "Không tìm thấy bài viết" });
        if (request.Rating is < 1 or > 5) return BadRequest(new { success = false, message = "Đánh giá phải từ 1 đến 5 tim." });

        var ratingId = StableInteractionId("rating", id, current.userId!);
        var existing = await _repo.GetByIdAsync(PostRatingsCollection, ratingId);
        var existingRating = existing is null ? 0 : GetInt(existing, "rating");
        var removed = existingRating == request.Rating;

        if (removed)
        {
            await _repo.DeleteAsync(PostRatingsCollection, ratingId);
        }
        else
        {
            await _repo.SetAsync(PostRatingsCollection, ratingId, new Dictionary<string, object?>
            {
                ["post_id"] = id,
                ["postId"] = id,
                ["user_id"] = current.userId!,
                ["userId"] = current.userId!,
                ["rating"] = request.Rating,
                ["created_at"] = existing?.GetValueOrDefault("created_at") ?? DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            }, merge: true);
        }

        var summary = await GetPostEngagementSummaryAsync(id, current.userId!);
        return Ok(new
        {
            success = true,
            removed,
            data = summary,
            message = removed ? "Đã hủy đánh giá" : $"Đã đánh giá {request.Rating} tim"
        });
    }

    [HttpGet("posts/{id}/comments")]
    public async Task<IActionResult> GetPostComments(string id)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var post = await GetAccessiblePostAsync(id, current.userId!, current.authUser);
        if (post is null) return NotFound(new { success = false, message = "Không tìm thấy bài viết" });

        var comments = await _repo.WhereEqualAsync(PostCommentsCollection, "post_id", id, limit: 300);
        comments = comments
            .Where(comment => !IsTruthy(comment.GetValueOrDefault("is_deleted")))
            .OrderBy(comment => DateValue(comment, "created_at"))
            .ToList();
        return Ok(new { success = true, data = comments });
    }

    [HttpPost("posts/{id}/comments")]
    public async Task<IActionResult> AddPostComment(string id, [FromBody] PostCommentRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var post = await GetAccessiblePostAsync(id, current.userId!, current.authUser);
        if (post is null) return NotFound(new { success = false, message = "Không tìm thấy bài viết" });

        var content = (request.Content ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(content)) return BadRequest(new { success = false, message = "Vui lòng nhập bình luận." });
        if (content.Length > 1000) return BadRequest(new { success = false, message = "Bình luận tối đa 1.000 ký tự." });

        var now = DateTime.UtcNow;
        var comment = new Dictionary<string, object?>
        {
            ["post_id"] = id,
            ["postId"] = id,
            ["user_id"] = current.userId!,
            ["userId"] = current.userId!,
            ["user_name"] = GetDisplayName(current.authUser, current.userId!),
            ["userName"] = GetDisplayName(current.authUser, current.userId!),
            ["content"] = content,
            ["is_deleted"] = false,
            ["created_at"] = now,
            ["updated_at"] = now
        };
        var commentId = await _repo.AddAsync(PostCommentsCollection, comment);
        comment["id"] = commentId ?? string.Empty;
        var summary = await GetPostEngagementSummaryAsync(id, current.userId!);
        return Ok(new { success = true, data = comment, engagement = summary, message = "Đã gửi bình luận" });
    }

    [HttpDelete("posts/{postId}/comments/{commentId}")]
    public async Task<IActionResult> DeletePostComment(string postId, string commentId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var comment = await _repo.GetByIdAsync(PostCommentsCollection, commentId);
        if (comment is null || !string.Equals(Text(comment, "post_id"), postId, StringComparison.Ordinal))
            return NotFound(new { success = false, message = "Không tìm thấy bình luận" });
        var ownerId = Text(comment, "user_id");
        if (!string.Equals(ownerId, current.userId, StringComparison.Ordinal) && !IsAdminUser(current.authUser))
            return StatusCode(403, new { success = false, message = "Bạn không thể xóa bình luận này." });
        await _repo.DeleteAsync(PostCommentsCollection, commentId);
        var summary = await GetPostEngagementSummaryAsync(postId, current.userId!);
        return Ok(new { success = true, data = summary, message = "Đã xóa bình luận" });
    }

    [HttpPost("posts/images")]
    [RequestSizeLimit(125 * 1024 * 1024)]
    public async Task<IActionResult> UploadPostMedia([FromForm] List<IFormFile>? files, [FromForm] List<IFormFile>? images)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var uploads = (files ?? new List<IFormFile>())
            .Concat(images ?? new List<IFormFile>())
            .Where(file => file is not null && file.Length > 0)
            .Take(12)
            .ToList();
        if (uploads.Count == 0) return BadRequest(new { success = false, message = "Vui lòng chọn ảnh hoặc video." });

        var media = new List<Dictionary<string, object?>>();
        foreach (var file in uploads)
        {
            var contentType = (file.ContentType ?? string.Empty).Trim().ToLowerInvariant();
            if (!contentType.StartsWith("image/") && !contentType.StartsWith("video/")) continue;

            var url = await _fileStorage.SaveFileAsync(file, current.userId!, "posts");
            if (string.IsNullOrWhiteSpace(url)) continue;
            media.Add(new Dictionary<string, object?>
            {
                ["url"] = url,
                ["name"] = Path.GetFileName(file.FileName),
                ["contentType"] = contentType,
                ["size"] = file.Length,
                ["type"] = contentType.StartsWith("video/") ? "video" : "image"
            });
        }

        if (media.Count == 0) return BadRequest(new { success = false, message = "Tệp không hợp lệ. Chỉ hỗ trợ ảnh hoặc video, mỗi tệp tối đa 10MB." });
        var urls = media.Select(item => item["url"]?.ToString()).Where(url => !string.IsNullOrWhiteSpace(url)).ToList();
        return Ok(new { success = true, media, urls, images = urls, message = "Đã tải tệp bài viết" });
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
        await FinalizeAiGenerationAsync(request, current.userId!, id);
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
        await FinalizeAiGenerationAsync(request, current.userId!, id);
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
        var currentUser = await CurrentUserAsync();
        if (!currentUser.ok) return currentUser.error!;

        var data = ToPostData(request, string.Empty);
        await ApplyManagedAuthorAsync(data, request);
        data["created_at"] = DateTime.UtcNow;
        data["updated_at"] = DateTime.UtcNow;
        var id = await _repo.AddAsync(PostsCollection, data);
        await FinalizeAiGenerationAsync(request, currentUser.userId!, id);
        return Ok(new { success = true, id, message = "Đã thêm bài viết" });
    }

    [HttpPut("admin/posts/{id}")]
    public async Task<IActionResult> UpdatePost(string id, [FromBody] TravelPostUpsertRequest request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest(new { success = false, message = "Vui lòng nhập tiêu đề bài viết." });
        var currentUser = await CurrentUserAsync();
        if (!currentUser.ok) return currentUser.error!;

        var current = await _repo.GetByIdAsync(PostsCollection, id);
        if (current is null || IsDeletedPost(current)) return NotFound(new { success = false, message = "Không tìm thấy bài viết" });
        var data = ToPostData(request, id);
        await ApplyManagedAuthorAsync(data, request);
        PreservePostMetadata(data, current, preserveAuthor: false);
        data["updated_at"] = DateTime.UtcNow;
        await _repo.UpdateAsync(PostsCollection, id, data);
        await FinalizeAiGenerationAsync(request, currentUser.userId!, id);
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


    private async Task<Dictionary<string, object?>?> GetAccessiblePostAsync(string id, string userId, Dictionary<string, object?>? authUser)
    {
        var post = await _repo.GetByIdAsync(PostsCollection, id);
        if (post is null || IsDeletedPost(post)) return null;
        if (!IsAdminUser(authUser) && !IsActivePost(post) && !IsPostOwner(post, userId)) return null;
        return post;
    }

    private async Task AttachPostEngagementAsync(List<Dictionary<string, object?>> posts, string userId)
    {
        if (posts.Count == 0) return;
        var postIds = posts.Select(post => Text(post, "id")).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var likes = await _repo.GetAllFieldsAsync(PostLikesCollection, new[] { "post_id", "postId", "user_id", "userId" }, limit: 20000);
        var comments = await _repo.GetAllFieldsAsync(PostCommentsCollection, new[] { "post_id", "postId", "is_deleted" }, limit: 20000);
        var ratings = await _repo.GetAllFieldsAsync(PostRatingsCollection, new[] { "post_id", "postId", "user_id", "userId", "rating" }, limit: 20000);

        var validLikes = likes.Where(row => postIds.Contains(InteractionPostId(row))).ToList();
        var validComments = comments.Where(row => postIds.Contains(InteractionPostId(row)) && !IsTruthy(row.GetValueOrDefault("is_deleted"))).ToList();
        var validRatings = ratings.Where(row => postIds.Contains(InteractionPostId(row)) && GetInt(row, "rating") is >= 1 and <= 5).ToList();

        foreach (var post in posts)
        {
            var id = Text(post, "id");
            var postLikes = validLikes.Where(row => string.Equals(InteractionPostId(row), id, StringComparison.Ordinal)).ToList();
            var postRatings = validRatings.Where(row => string.Equals(InteractionPostId(row), id, StringComparison.Ordinal)).ToList();
            var ratingValues = postRatings.Select(row => GetInt(row, "rating")).Where(value => value is >= 1 and <= 5).ToList();
            post["like_count"] = postLikes.Count;
            post["likeCount"] = postLikes.Count;
            post["comment_count"] = validComments.Count(row => string.Equals(InteractionPostId(row), id, StringComparison.Ordinal));
            post["commentCount"] = post["comment_count"];
            post["rating_count"] = ratingValues.Count;
            post["ratingCount"] = ratingValues.Count;
            post["rating_average"] = ratingValues.Count == 0 ? 0 : Math.Round(ratingValues.Average(), 1);
            post["ratingAverage"] = post["rating_average"];
            post["user_liked"] = postLikes.Any(row => string.Equals(InteractionUserId(row), userId, StringComparison.Ordinal));
            post["userLiked"] = post["user_liked"];
            var ownRating = postRatings.FirstOrDefault(row => string.Equals(InteractionUserId(row), userId, StringComparison.Ordinal));
            post["user_rating"] = ownRating is null ? 0 : GetInt(ownRating, "rating");
            post["userRating"] = post["user_rating"];
        }
    }

    private async Task<Dictionary<string, object?>> GetPostEngagementSummaryAsync(string postId, string userId)
    {
        var post = new Dictionary<string, object?> { ["id"] = postId };
        await AttachPostEngagementAsync(new List<Dictionary<string, object?>> { post }, userId);
        return post;
    }

    private static string InteractionPostId(Dictionary<string, object?> row)
    {
        var value = Text(row, "post_id");
        return string.IsNullOrWhiteSpace(value) ? Text(row, "postId") : value;
    }

    private static string InteractionUserId(Dictionary<string, object?> row)
    {
        var value = Text(row, "user_id");
        return string.IsNullOrWhiteSpace(value) ? Text(row, "userId") : value;
    }

    private static string StableInteractionId(string kind, string postId, string userId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{postId}|{userId}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static DateTime DateValue(Dictionary<string, object?> row, string key)
    {
        return row.TryGetValue(key, out var value) && DateTime.TryParse(value?.ToString(), out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
    }

    private async Task FinalizeAiGenerationAsync(TravelPostUpsertRequest request, string userId, string? postId)
    {
        var sessionId = NormalizeAiGenerationSessionId(request.AiGenerationSessionId);
        var generationId = (request.AiGenerationId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(generationId) || string.IsNullOrWhiteSpace(userId)) return;

        try
        {
            var sessionRows = await _repo.WhereEqualAsync(PostAiGenerationCollection, "session_id", sessionId, limit: 100);
            var ownedRows = sessionRows
                .Where(row => string.Equals(Text(row, "created_by"), userId, StringComparison.Ordinal))
                .ToList();
            var finalTitle = request.Title?.Trim() ?? string.Empty;
            var selected = ownedRows.FirstOrDefault(row =>
                    !string.IsNullOrWhiteSpace(finalTitle)
                    && string.Equals(Text(row, "title").Trim(), finalTitle, StringComparison.OrdinalIgnoreCase))
                ?? ownedRows.FirstOrDefault(row => string.Equals(Text(row, "id"), generationId, StringComparison.Ordinal));
            if (selected is null) return;

            var selectedId = Text(selected, "id");
            if (string.IsNullOrWhiteSpace(selectedId)) return;
            await _repo.UpdateAsync(PostAiGenerationCollection, selectedId, new Dictionary<string, object?>
            {
                ["status"] = "published",
                ["post_id"] = postId ?? string.Empty,
                ["final_title"] = string.IsNullOrWhiteSpace(finalTitle) ? Text(selected, "title") : finalTitle,
                ["completed_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });

            foreach (var row in ownedRows)
            {
                var rowId = Text(row, "id");
                if (string.IsNullOrWhiteSpace(rowId) || string.Equals(rowId, selectedId, StringComparison.Ordinal)) continue;
                if (string.Equals(Text(row, "status"), "published", StringComparison.OrdinalIgnoreCase)) continue;
                await _repo.DeleteAsync(PostAiGenerationCollection, rowId);
            }
        }
        catch
        {

        }
    }

    private static string NormalizeAiGenerationSessionId(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        return Guid.TryParse(raw, out var parsed) ? parsed.ToString("N") : string.Empty;
    }

    private async Task<bool> DeletePostRecordAsync(string id, Dictionary<string, object?> saved)
    {
        bool deleted;
        if (IsSeedPost(saved) || id.StartsWith("seed-post-", StringComparison.OrdinalIgnoreCase))
        {
            deleted = await _repo.UpdateAsync(PostsCollection, id, new Dictionary<string, object?>
            {
                ["is_deleted"] = true,
                ["isDeleted"] = true,
                ["status"] = "Đã xóa",
                ["deleted_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });
        }
        else
        {
            deleted = await _repo.DeleteAsync(PostsCollection, id);
        }

        if (deleted)
        {
            await _repo.DeleteWhereEqualAsync(PostLikesCollection, "post_id", id);
            await _repo.DeleteWhereEqualAsync(PostCommentsCollection, "post_id", id);
            await _repo.DeleteWhereEqualAsync(PostRatingsCollection, "post_id", id);
        }
        return deleted;
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
        var media = CleanPostMedia(request.Media, request.ImageUrls);
        var images = media.Where(item => string.Equals(item["type"]?.ToString(), "image", StringComparison.OrdinalIgnoreCase)).Select(item => item["url"]?.ToString() ?? string.Empty).Where(url => !string.IsNullOrWhiteSpace(url)).ToList();
        var videos = media.Where(item => string.Equals(item["type"]?.ToString(), "video", StringComparison.OrdinalIgnoreCase)).Select(item => item["url"]?.ToString() ?? string.Empty).Where(url => !string.IsNullOrWhiteSpace(url)).ToList();
        var mediaUrls = media.Select(item => item["url"]?.ToString() ?? string.Empty).Where(url => !string.IsNullOrWhiteSpace(url)).ToList();
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
            ["author_name"] = CleanAuthorName(request.AuthorName) ?? "Travel Việt",
            ["authorName"] = CleanAuthorName(request.AuthorName) ?? "Travel Việt",
            ["image_urls"] = images,
            ["imageUrls"] = images,
            ["images"] = images,
            ["video_urls"] = videos,
            ["videoUrls"] = videos,
            ["media"] = media,
            ["media_items"] = media,
            ["mediaItems"] = media,
            ["media_urls"] = mediaUrls,
            ["mediaUrls"] = mediaUrls,
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
        var authors = new[] { "Pastic", "Admin", "Việt Hành", "Sắc Việt" };
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


    private static List<Dictionary<string, object?>> CleanPostMedia(List<PostMediaItemRequest>? items, List<string>? legacyImageUrls)
    {
        var normalized = new List<Dictionary<string, object?>>();
        foreach (var item in items ?? new List<PostMediaItemRequest>())
        {
            var url = item.Url?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url)) continue;
            var contentType = NormalizeMediaContentType(item.ContentType, url);
            var type = contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video" : "image";
            normalized.Add(new Dictionary<string, object?>
            {
                ["url"] = url,
                ["name"] = item.Name?.Trim() ?? Path.GetFileName(url.Split('?', '#')[0]),
                ["contentType"] = contentType,
                ["size"] = Math.Max(0, item.Size ?? 0),
                ["type"] = type
            });
        }

        foreach (var url in legacyImageUrls ?? new List<string>())
        {
            var cleanUrl = url?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cleanUrl) || normalized.Any(item => string.Equals(item["url"]?.ToString(), cleanUrl, StringComparison.OrdinalIgnoreCase))) continue;
            normalized.Add(new Dictionary<string, object?>
            {
                ["url"] = cleanUrl,
                ["name"] = Path.GetFileName(cleanUrl.Split('?', '#')[0]),
                ["contentType"] = NormalizeMediaContentType(null, cleanUrl),
                ["size"] = 0L,
                ["type"] = IsVideoUrl(cleanUrl) ? "video" : "image"
            });
        }

        return normalized
            .GroupBy(item => item["url"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(12)
            .ToList();
    }

    private static string NormalizeMediaContentType(string? contentType, string url)
    {
        var clean = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        if (clean.StartsWith("image/") || clean.StartsWith("video/")) return clean;
        return IsVideoUrl(url) ? "video/mp4" : "image/jpeg";
    }

    private static bool IsVideoUrl(string? url)
    {
        var path = (url ?? string.Empty).Split('?', '#')[0];
        var extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase);
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

    public sealed class PostRatingRequest
    {
        public int Rating { get; set; }
    }

    public sealed class PostCommentRequest
    {
        public string? Content { get; set; }
    }

    public sealed class PostMediaItemRequest
    {
        public string? Url { get; set; }
        public string? Name { get; set; }
        public string? ContentType { get; set; }
        public long? Size { get; set; }
    }

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
        public List<PostMediaItemRequest>? Media { get; set; }
        public string? Status { get; set; }
        public string? AiGenerationSessionId { get; set; }
        public string? AiGenerationId { get; set; }
    }
}
