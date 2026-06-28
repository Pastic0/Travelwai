using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/tour-offers")]
public sealed class TourOffersApiController : ApiControllerBase
{
    private readonly TourOfferService _offerService;

    public TourOffersApiController(IAuthService authService, TourOfferService offerService) : base(authService)
    {
        _offerService = offerService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var email = current.authUser?.GetValueOrDefault("email")?.ToString() ?? string.Empty;
        return Ok(await _offerService.GetStatusAsync(current.userId!, email));
    }

    [HttpGet("post-status")]
    public async Task<IActionResult> PostStatus()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (!CanUsePostOffer(current.authUser))
        {
            return Ok(new
            {
                success = true,
                has_offer = false,
                post_offer_active = false,
                discount_percent = 0,
                progress = 0,
                target = 1,
                message = "Gói Free và VIP chưa dùng được ưu đãi bài viết."
            });
        }

        return Ok(await _offerService.GetPostOfferStatusAsync(current.userId!));
    }

    [HttpPost("invite")]
    public async Task<IActionResult> Invite([FromBody] TourOfferInviteRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var authUser = current.authUser ?? new Dictionary<string, object?>();
        var inviterEmail = authUser.GetValueOrDefault("email")?.ToString() ?? string.Empty;
        var inviterName = authUser.GetValueOrDefault("displayName")?.ToString()
            ?? authUser.GetValueOrDefault("username")?.ToString()
            ?? inviterEmail;

        var result = await _offerService.InviteAsync(Request, current.userId!, inviterEmail, inviterName, request.Email);
        return Ok(result);
    }
}

public sealed class TourOfferInviteRequest
{
    public string Email { get; set; } = string.Empty;
}
