using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/admin/travel-guide-rag")]
public sealed class TravelGuideRagAdminController : ApiControllerBase
{
    private readonly TravelGuideRagService _ragService;

    public TravelGuideRagAdminController(IAuthService authService, TravelGuideRagService ragService) : base(authService)
    {
        _ragService = ragService;
    }

    [HttpGet("sources")]
    public async Task<IActionResult> Sources(CancellationToken cancellationToken)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        var data = await _ragService.ListSourcesAsync(cancellationToken);
        return Ok(new { success = true, data });
    }

    [HttpPost("sources")]
    public async Task<IActionResult> SaveSource([FromBody] TravelGuideSourceInput input, CancellationToken cancellationToken)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        try
        {
            var data = await _ragService.UpsertSourceAsync(input, access.userId, cancellationToken);
            return Ok(new { success = true, data, message = "Đã lưu nguồn RAG." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("documents")]
    public async Task<IActionResult> Documents([FromQuery] int limit = 80, CancellationToken cancellationToken = default)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        var data = await _ragService.ListDocumentsAsync(limit, cancellationToken);
        return Ok(new { success = true, data });
    }

    [HttpPost("documents")]
    public async Task<IActionResult> IngestDocument([FromBody] TravelGuideDocumentInput input, CancellationToken cancellationToken)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        try
        {
            var data = await _ragService.IngestDocumentAsync(input, access.userId, cancellationToken);
            return Ok(new { success = true, data, message = "Đã nạp tài liệu RAG." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("crawl")]
    public async Task<IActionResult> Crawl([FromBody] TravelGuideCrawlInput input, CancellationToken cancellationToken)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        try
        {
            var data = await _ragService.CrawlUrlAsync(input, access.userId, cancellationToken);
            return Ok(new { success = true, data, message = "Đã crawl và nạp URL vào RAG." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int limit = 8, CancellationToken cancellationToken = default)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        var data = await _ragService.SearchAsync(q, limit, cancellationToken);
        return Ok(new { success = true, data });
    }

    private async Task<(bool ok, string? userId, IActionResult? error)> RequireAdminAsync()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return (false, null, current.error);
        if (!string.Equals(NormalizeAccountRole(current.authUser?.GetValueOrDefault("role")), "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, StatusCode(403, new { success = false, message = "Chỉ Admin mới được truy cập." }));
        }
        return (true, current.userId, null);
    }
}
