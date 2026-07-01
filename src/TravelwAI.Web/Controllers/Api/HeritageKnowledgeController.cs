using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/heritage")]
public sealed class HeritageKnowledgeController : ApiControllerBase
{
    private readonly HeritageKnowledgeService _knowledge;

    public HeritageKnowledgeController(IAuthService authService, HeritageKnowledgeService knowledge) : base(authService)
    {
        _knowledge = knowledge;
    }

    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest([FromBody] HeritageSourceIngestRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (!CanManageKnowledge(current.authUser)) return StatusCode(403, new { success = false, detail = "Bạn không có quyền cập nhật kho tri thức." });

        try
        {
            var result = await _knowledge.IngestAsync(request, current.userId!);
            return Ok(new { success = true, data = result, message = "Đã đưa nguồn vào kho tri thức." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, detail = ex.Message });
        }
    }


    [HttpPost("ingest-url")]
    public async Task<IActionResult> IngestUrl([FromBody] HeritageUrlIngestRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (!CanManageKnowledge(current.authUser)) return StatusCode(403, new { success = false, detail = "Bạn không có quyền cập nhật kho tri thức." });

        try
        {
            var result = await _knowledge.IngestUrlAsync(request, current.userId!);
            return Ok(new { success = true, data = result, message = "Đã crawl và đưa nguồn vào kho tri thức." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, detail = ex.Message });
        }
    }

    [HttpGet("sources")]
    public async Task<IActionResult> Sources([FromQuery] int limit = 80)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (!CanManageKnowledge(current.authUser)) return StatusCode(403, new { success = false, detail = "Bạn không có quyền xem kho tri thức." });
        var sources = await _knowledge.GetSourcesAsync(limit);
        return Ok(new { success = true, data = sources, message = "Đã tải nguồn tri thức." });
    }

    [HttpPost("sources/{sourceId}/approve")]
    public async Task<IActionResult> Approve(string sourceId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (!CanManageKnowledge(current.authUser)) return StatusCode(403, new { success = false, detail = "Bạn không có quyền duyệt kho tri thức." });
        var ok = await _knowledge.ApproveSourceAsync(sourceId, approved: true);
        return ok ? Ok(new { success = true, message = "Đã duyệt nguồn." }) : NotFound(new { success = false, detail = "Không tìm thấy nguồn." });
    }

    [HttpPost("sources/{sourceId}/reject")]
    public async Task<IActionResult> Reject(string sourceId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (!CanManageKnowledge(current.authUser)) return StatusCode(403, new { success = false, detail = "Bạn không có quyền duyệt kho tri thức." });
        var ok = await _knowledge.ApproveSourceAsync(sourceId, approved: false);
        return ok ? Ok(new { success = true, message = "Đã từ chối nguồn." }) : NotFound(new { success = false, detail = "Không tìm thấy nguồn." });
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string query, [FromQuery] int limit = 7)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var result = await _knowledge.RetrieveAsync(query, null, limit);
        return Ok(new { success = true, data = result, message = "Đã truy xuất kho tri thức." });
    }

    private static bool CanManageKnowledge(Dictionary<string, object?>? authUser)
    {
        var role = NormalizeAccountRole(authUser?.GetValueOrDefault("role"));
        return role.Equals("Admin", StringComparison.OrdinalIgnoreCase) || role.Equals("Business", StringComparison.OrdinalIgnoreCase);
    }
}
