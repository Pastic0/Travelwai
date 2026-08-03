using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/role-features")]
public sealed class RoleFeaturesController : ApiControllerBase
{
    private readonly RoleFeaturePolicyService _policies;

    public RoleFeaturesController(IAuthService authService, RoleFeaturePolicyService policies) : base(authService)
    {
        _policies = policies;
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMyFeatures()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        var policy = _policies.GetPolicy(current.authUser?.GetValueOrDefault("role"));
        return Ok(new
        {
            success = true,
            data = new
            {
                role = policy.Role,
                windowMinutes = policy.WindowMinutes,
                aiItinerary = new { enabled = policy.CanUseAiItinerary },
                aiPost = new { enabled = true, limit = policy.AiPostLimitPerWindow },
                aiChat = new { enabled = true, limit = policy.AiChatLimitPerWindow },
                postOffer = new { enabled = policy.CanUsePostOffer },
                chatbotStyles = new
                {
                    canChange = policy.CanChangeChatbotStyle,
                    hasAllStyles = policy.HasAllChatbotStyles
                }
            }
        });
    }
}
