using Microsoft.AspNetCore.Mvc;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/ai/knowledge")]
public sealed class AiKnowledgeStatusController : ControllerBase
{
    private readonly ExternalDatasetKnowledgeService _knowledge;

    public AiKnowledgeStatusController(ExternalDatasetKnowledgeService knowledge)
    {
        _knowledge = knowledge;
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        var status = _knowledge.GetStatus();
        return Ok(new
        {
            success = true,
            data = new
            {
                state = status.State,
                documentCount = status.DocumentCount,
                lastImportedAt = status.LastImportedAt,
                lastError = status.LastError,
                sourceCounts = status.SourceCounts
            }
        });
    }
}
