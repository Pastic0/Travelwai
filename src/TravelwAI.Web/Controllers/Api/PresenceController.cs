using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/presence")]
public sealed class PresenceController : ApiControllerBase
{
    private readonly AccountPresenceService _presenceService;

    public PresenceController(IAuthService authService, AccountPresenceService presenceService)
        : base(authService)
    {
        _presenceService = presenceService;
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        await _presenceService.TouchAsync(current.userId!);
        return Ok(new
        {
            success = true,
            is_online = true,
            presence_status = "online",
            checked_at = DateTime.UtcNow.ToString("O")
        });
    }
}
