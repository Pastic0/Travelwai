using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Models.Requests;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/profile")]
public sealed class ProfileController : ApiControllerBase
{
    private readonly IChatService _chatService;
    private readonly IFileStorageService _fileStorage;

    public ProfileController(IAuthService authService, IChatService chatService, IFileStorageService fileStorage) : base(authService)
    {
        _chatService = chatService;
        _fileStorage = fileStorage;
    }

    [HttpGet]
    public async Task<IActionResult> GetProfile()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var profile = await _chatService.GetUserByIdAsync(current.userId!);
        if (profile is null)
        {
            profile = BuildProfileFromAuthUser(current.userId!, current.authUser);
            await _chatService.CreateOrUpdateUserAsync(current.userId!, profile);
        }
        else
        {
            var patch = BuildProfilePatch(profile, current.authUser);
            if (patch.Count > 0)
            {
                await _chatService.CreateOrUpdateUserAsync(current.userId!, patch);
                foreach (var item in patch) profile[item.Key] = item.Value;
            }
        }

        NormalizeProfileForClient(profile, current.authUser);
        return Ok(new { message = "Đã tải hồ sơ", success = true, user = profile });
    }

    [HttpPost]
    public async Task<IActionResult> UploadProfilePicture(IFormFile profilePic)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;
        if (profilePic is null || profilePic.Length == 0) return BadRequest(new { success = false, detail = "Chưa chọn tệp" });

        var url = await _fileStorage.SaveImageAsync(profilePic, current.userId!, "profiles");
        if (url is null) return BadRequest(new { success = false, detail = "Chỉ cho phép ảnh jpg, jpeg, png, gif và webp" });

        await _chatService.CreateOrUpdateUserAsync(current.userId!, new Dictionary<string, object?> { ["profilePic"] = url });
        return Ok(new { success = true, message = "Đã tải ảnh đại diện", profilePic = url });
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        if (request is null || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { success = false, message = "Vui lòng nhập mật khẩu mới" });
        }

        if (request.Password.Length < 6)
        {
            return BadRequest(new { success = false, message = "Mật khẩu mới phải có ít nhất 6 ký tự" });
        }

        return Ok(await AuthService.ChangePasswordAsync(current.userId!, request.Password));
    }

    private static Dictionary<string, object?> BuildProfileFromAuthUser(string userId, Dictionary<string, object?>? authUser)
    {
        var email = GetText(authUser, "email")?.ToLowerInvariant() ?? string.Empty;
        var displayName = GetText(authUser, "displayName");
        var username = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : (!string.IsNullOrWhiteSpace(email) ? email.Split('@')[0] : userId);
        var registeredAt = GetAuthCreatedAt(authUser) ?? DateTime.UtcNow;

        return new Dictionary<string, object?>
        {
            ["id"] = userId,
            ["username"] = username,
            ["displayName"] = username,
            ["email"] = email,
            ["phone"] = string.Empty,
            ["profilePic"] = string.Empty,
            ["is_active"] = true,
            ["created_at"] = registeredAt,
            ["createdAt"] = registeredAt,
            ["registeredAt"] = registeredAt,
            ["updated_at"] = DateTime.UtcNow
        };
    }

    private static Dictionary<string, object?> BuildProfilePatch(Dictionary<string, object?> profile, Dictionary<string, object?>? authUser)
    {
        var patch = new Dictionary<string, object?>();
        var email = GetText(authUser, "email")?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(profile.GetValueOrDefault("email")?.ToString()))
        {
            patch["email"] = email;
        }

        var displayName = GetText(authUser, "displayName");
        var currentUsername = profile.GetValueOrDefault("username")?.ToString()?.Trim();
        var emailPrefix = !string.IsNullOrWhiteSpace(email) ? email.Split('@')[0] : string.Empty;
        if (!string.IsNullOrWhiteSpace(displayName)
            && (string.IsNullOrWhiteSpace(currentUsername)
                || string.Equals(currentUsername, emailPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            patch["username"] = displayName;
            patch["displayName"] = displayName;
        }
        else if (!string.IsNullOrWhiteSpace(currentUsername) && string.IsNullOrWhiteSpace(profile.GetValueOrDefault("displayName")?.ToString()))
        {
            patch["displayName"] = currentUsername;
        }

        var registeredAt = GetProfileCreatedAt(profile) ?? GetAuthCreatedAt(authUser);
        if (registeredAt.HasValue)
        {
            if (!profile.ContainsKey("created_at") || string.IsNullOrWhiteSpace(profile.GetValueOrDefault("created_at")?.ToString())) patch["created_at"] = registeredAt.Value;
            if (!profile.ContainsKey("createdAt") || string.IsNullOrWhiteSpace(profile.GetValueOrDefault("createdAt")?.ToString())) patch["createdAt"] = registeredAt.Value;
            if (!profile.ContainsKey("registeredAt") || string.IsNullOrWhiteSpace(profile.GetValueOrDefault("registeredAt")?.ToString())) patch["registeredAt"] = registeredAt.Value;
        }

        return patch;
    }

    private static void NormalizeProfileForClient(Dictionary<string, object?> profile, Dictionary<string, object?>? authUser)
    {
        var email = profile.GetValueOrDefault("email")?.ToString() ?? GetText(authUser, "email") ?? string.Empty;
        var username = profile.GetValueOrDefault("username")?.ToString();
        if (string.IsNullOrWhiteSpace(username)) username = GetText(authUser, "displayName") ?? (!string.IsNullOrWhiteSpace(email) ? email.Split('@')[0] : string.Empty);

        var registeredAt = GetProfileCreatedAt(profile) ?? GetAuthCreatedAt(authUser);

        profile["email"] = email;
        profile["username"] = username;
        profile["displayName"] = profile.GetValueOrDefault("displayName")?.ToString() ?? username;
        profile["role"] = GetText(authUser, "role") ?? profile.GetValueOrDefault("role")?.ToString() ?? "User";
        profile["userRole"] = profile["role"];
        if (registeredAt.HasValue)
        {
            profile["created_at"] = registeredAt.Value.ToString("O");
            profile["createdAt"] = registeredAt.Value.ToString("O");
            profile["registeredAt"] = registeredAt.Value.ToString("O");
        }
    }

    private static DateTime? GetProfileCreatedAt(Dictionary<string, object?> profile)
    {
        foreach (var key in new[] { "created_at", "createdAt", "registeredAt" })
        {
            if (profile.TryGetValue(key, out var raw) && TryParseAnyDate(raw, out var date)) return date;
        }
        return null;
    }

    private static DateTime? GetAuthCreatedAt(Dictionary<string, object?>? authUser)
    {
        var raw = GetText(authUser, "createdAt");
        return TryParseAnyDate(raw, out var date) ? date : null;
    }

    private static string? GetText(Dictionary<string, object?>? source, string key)
    {
        if (source is null) return null;
        return source.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static bool TryParseAnyDate(object? raw, out DateTime date)
    {
        date = default;
        if (raw is null) return false;
        if (raw is DateTime dt)
        {
            date = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
            return true;
        }

        var text = raw.ToString();
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (long.TryParse(text, out var number))
        {
            try
            {
                date = DateTimeOffset.FromUnixTimeMilliseconds(number).UtcDateTime;
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (DateTimeOffset.TryParse(text, out var dto))
        {
            date = dto.UtcDateTime;
            return true;
        }

        return false;
    }
}
