using Microsoft.AspNetCore.Mvc;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Controllers.Api;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/site-branding")]
public sealed class SiteBrandingController : ControllerBase
{
    private const string SettingsCollection = "site_settings";
    private const string BrandingDocumentId = "branding";
    private const string DefaultLogoUrl = "/logo/travelwai-icon.webp";
    private const string DefaultLogoVersion = "2026-08-05-brand-icon-v4";
    private const string DefaultLightBackgroundUrl = "/main_site_image/travelwai-bg-light.webp";
    private const string DefaultDarkBackgroundUrl = "/main_site_image/travelwai-bg-dark.webp";
    private const string DefaultBackgroundVersion = "2026-07-26-branding-cache-fix-v3";
    private readonly IDataRepository _repo;

    public SiteBrandingController(IDataRepository repo)
    {
        _repo = repo;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        var settings = await _repo.GetByIdAsync(SettingsCollection, BrandingDocumentId);
        var logoUrl = ReadText(settings, "logo_url", "logoUrl");
        var logoVersion = ReadText(settings, "logo_version", "logoVersion", "updated_at", "updatedAt");
        var lightBackgroundUrl = ReadText(settings, "background_light_url", "backgroundLightUrl");
        var lightBackgroundVersion = ReadText(settings, "background_light_version", "backgroundLightVersion");
        var darkBackgroundUrl = ReadText(settings, "background_dark_url", "backgroundDarkUrl");
        var darkBackgroundVersion = ReadText(settings, "background_dark_version", "backgroundDarkVersion");

        return Ok(new
        {
            success = true,
            data = new
            {
                logoUrl = string.IsNullOrWhiteSpace(logoUrl) ? DefaultLogoUrl : logoUrl,
                version = string.IsNullOrWhiteSpace(logoVersion) ? DefaultLogoVersion : logoVersion,
                backgroundLightUrl = string.IsNullOrWhiteSpace(lightBackgroundUrl) ? DefaultLightBackgroundUrl : lightBackgroundUrl,
                backgroundLightVersion = string.IsNullOrWhiteSpace(lightBackgroundVersion) ? DefaultBackgroundVersion : lightBackgroundVersion,
                backgroundDarkUrl = string.IsNullOrWhiteSpace(darkBackgroundUrl) ? DefaultDarkBackgroundUrl : darkBackgroundUrl,
                backgroundDarkVersion = string.IsNullOrWhiteSpace(darkBackgroundVersion) ? DefaultBackgroundVersion : darkBackgroundVersion
            }
        });
    }

    private static string ReadText(Dictionary<string, object?>? data, params string[] keys)
    {
        if (data is null) return string.Empty;
        foreach (var key in keys)
        {
            if (!data.TryGetValue(key, out var value) || value is null) continue;
            var text = Convert.ToString(value)?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return string.Empty;
    }
}
