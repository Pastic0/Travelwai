using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/memories")]
public sealed class MemoriesController : ApiControllerBase
{
    private readonly IMemoryService _memoryService;
    private readonly IFileStorageService _fileStorage;

    public MemoriesController(IAuthService authService, IMemoryService memoryService, IFileStorageService fileStorage) : base(authService)
    {
        _memoryService = memoryService;
        _fileStorage = fileStorage;
    }

    [HttpGet]
    public async Task<IActionResult> GetUserMemories()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var data = await _memoryService.GetUserMemoriesAsync(current.userId!);
        return Ok(new { success = true, data, message = "Đã tải danh sách kỷ niệm" });
    }

    [HttpPost]
    [RequestSizeLimit(125 * 1024 * 1024)]
    public async Task<IActionResult> CreateMemory([FromForm] string memory_name, [FromForm] string description, [FromForm] string province, [FromForm] string created_at, [FromForm] List<string> shared_emails, IFormFile? photo, [FromForm] List<IFormFile>? photos, [FromForm] List<IFormFile>? files)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (string.IsNullOrWhiteSpace(memory_name) || string.IsNullOrWhiteSpace(description))
            return BadRequest(new { success = false, detail = "Vui lòng nhập tên và mô tả kỷ niệm" });

        var photoUrls = new List<string>();
        var videoUrls = new List<string>();
        var uploads = (files ?? new List<IFormFile>())
            .Concat(photos ?? new List<IFormFile>())
            .Concat(photo is null ? Array.Empty<IFormFile>() : new[] { photo })
            .Where(file => file is not null && file.Length > 0)
            .Take(12)
            .ToList();
        foreach (var item in uploads)
        {
            var contentType = (item.ContentType ?? string.Empty).Trim().ToLowerInvariant();
            if (!contentType.StartsWith("image/") && !contentType.StartsWith("video/")) continue;
            var savedUrl = await _fileStorage.SaveFileAsync(item, current.userId!, "memories");
            if (string.IsNullOrWhiteSpace(savedUrl)) continue;
            if (contentType.StartsWith("video/")) videoUrls.Add(savedUrl); else photoUrls.Add(savedUrl);
        }

        var photoUrl = photoUrls.FirstOrDefault();
        var cleanSharedEmails = NormalizeSharedEmails(shared_emails);
        var id = await _memoryService.CreateMemoryAsync(current.userId!, memory_name, description, province, created_at, cleanSharedEmails, photoUrl, photoUrls, videoUrls);
        if (id is null) return StatusCode(500, new { success = false, detail = "Không thể tạo kỷ niệm" });

        return Ok(new
        {
            success = true,
            data = new
            {
                id,
                memory_name,
                description,
                province,
                created_at,
                photo_url = photoUrl,
                photo_urls = photoUrls,
                video_urls = videoUrls,
                media_urls = photoUrls.Concat(videoUrls).ToList(),
                media = photoUrls.Select(url => new { url, type = "image", contentType = "image/*" }).Concat(videoUrls.Select(url => new { url, type = "video", contentType = "video/*" })).ToList(),
                memory_collection = cleanSharedEmails.Count > 0 ? "shared_memories" : "memories"
            }
        });
    }

    private static List<string> NormalizeSharedEmails(List<string>? sharedEmails)
    {
        if (sharedEmails is null || sharedEmails.Count == 0) return new List<string>();

        return sharedEmails
            .SelectMany(email => (email ?? string.Empty)
                .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(email => email.Trim().ToLowerInvariant())
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [HttpGet("{memoryId}")]
    public async Task<IActionResult> GetMemory(string memoryId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var data = await _memoryService.GetMemoryByIdAsync(memoryId, current.userId!);
        return data is null ? NotFound(new { success = false, detail = "Không tìm thấy kỷ niệm" }) : Ok(new { success = true, data, message = "Đã tải kỷ niệm" });
    }

    [HttpDelete("{memoryId}")]
    public Task<IActionResult> DeleteMemory(string memoryId) => DeleteMemoryInternal(memoryId);

    [HttpPost("{memoryId}/delete")]
    public Task<IActionResult> DeleteMemoryFallback(string memoryId) => DeleteMemoryInternal(memoryId);

    private async Task<IActionResult> DeleteMemoryInternal(string memoryId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var ok = await _memoryService.DeleteMemoryAsync(memoryId, current.userId!);
        return ok ? Ok(new { success = true, message = "Đã xóa kỷ niệm" }) : NotFound(new { success = false, detail = "Không tìm thấy kỷ niệm" });
    }

    [HttpGet("province/{provinceName}")]
    public async Task<IActionResult> GetMemoriesByProvince(string provinceName)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var data = await _memoryService.GetMemoriesByProvinceAsync(current.userId!, provinceName);
        return Ok(new { success = true, data, total = data.Count, province = provinceName, message = "Đã tải kỷ niệm theo tỉnh/thành" });
    }
}
