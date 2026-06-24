using Microsoft.Extensions.DependencyInjection;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Business.Services;

public sealed class AuthService : IAuthService
{
    private readonly IAuthRepository _authRepository;
    private readonly IServiceProvider _serviceProvider;

    public AuthService(IAuthRepository authRepository, IServiceProvider serviceProvider)
    {
        _authRepository = authRepository;
        _serviceProvider = serviceProvider;
    }

    public async Task<Dictionary<string, object?>> SignUpAsync(string email, string password, string username)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var normalizedUsername = NormalizeUsername(username, normalizedEmail);

        var result = await _authRepository.SignUpAsync(normalizedEmail, password, normalizedUsername);
        if (IsSuccess(result) && result.TryGetValue("localId", out var uidObj) && uidObj is string uid)
        {
            await TryCreateRegisteredProfileAsync(result, uid, normalizedEmail, normalizedUsername);
            result["username"] = normalizedUsername;
            result["displayName"] = normalizedUsername;
        }
        return result;
    }

    public async Task<Dictionary<string, object?>> LoginAsync(string email, string password, string username)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var result = await _authRepository.SignInAsync(normalizedEmail, password);
        if (IsSuccess(result) && result.TryGetValue("localId", out var uidObj) && uidObj is string uid)
        {

            await TrySyncUserProfileAfterLoginAsync(result, uid, normalizedEmail);
        }
        return result;
    }

    private async Task TryCreateRegisteredProfileAsync(Dictionary<string, object?> result, string uid, string email, string username)
    {
        try
        {
            var chatService = _serviceProvider.GetRequiredService<IChatService>();
            var existingProfile = await chatService.GetUserByIdAsync(uid);
            var registeredAt = GetProfileDate(existingProfile, "created_at", "createdAt", "registeredAt") ?? DateTime.UtcNow;

            await chatService.CreateOrUpdateUserAsync(uid, new Dictionary<string, object?>
            {
                ["username"] = username,
                ["displayName"] = username,
                ["email"] = email.ToLowerInvariant(),
                ["phone"] = existingProfile?.GetValueOrDefault("phone") ?? string.Empty,
                ["profilePic"] = existingProfile?.GetValueOrDefault("profilePic") ?? string.Empty,
                ["role"] = result.GetValueOrDefault("role")?.ToString() ?? "User",
                ["is_locked"] = result.GetValueOrDefault("is_locked") ?? false,
                ["is_protected"] = result.GetValueOrDefault("is_protected") ?? false,
                ["is_active"] = true,
                ["created_at"] = registeredAt,
                ["createdAt"] = registeredAt,
                ["registeredAt"] = registeredAt,
                ["updated_at"] = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {

            result["profileSyncWarning"] = "Tạo tài khoản thành công nhưng chưa đồng bộ hồ sơ Supabase PostgreSQL. Kiểm tra appsettings.json.";
            result["profileSyncError"] = ex.Message;
        }
    }

    private async Task TrySyncUserProfileAfterLoginAsync(Dictionary<string, object?> result, string uid, string email)
    {
        try
        {
            var chatService = _serviceProvider.GetRequiredService<IChatService>();
            var existingProfile = await chatService.GetUserByIdAsync(uid);
            var authDisplayName = result.GetValueOrDefault("displayName")?.ToString()?.Trim();

            if (existingProfile is null)
            {

                var username = NormalizeUsername(authDisplayName, email);
                var now = DateTime.UtcNow;
                await chatService.CreateOrUpdateUserAsync(uid, new Dictionary<string, object?>
                {
                    ["username"] = username,
                    ["displayName"] = username,
                    ["email"] = email.ToLowerInvariant(),
                    ["phone"] = string.Empty,
                    ["profilePic"] = string.Empty,
                    ["role"] = result.GetValueOrDefault("role")?.ToString() ?? "User",
                    ["is_locked"] = result.GetValueOrDefault("is_locked") ?? false,
                    ["is_protected"] = result.GetValueOrDefault("is_protected") ?? false,
                    ["is_active"] = true,
                    ["created_at"] = now,
                    ["createdAt"] = now,
                    ["registeredAt"] = now,
                    ["last_login_at"] = now,
                    ["updated_at"] = now
                });
                result["username"] = username;
                result["displayName"] = username;
                return;
            }

            var update = new Dictionary<string, object?>
            {
                ["email"] = email.ToLowerInvariant(),
                ["role"] = result.GetValueOrDefault("role")?.ToString() ?? existingProfile.GetValueOrDefault("role")?.ToString() ?? "User",
                ["is_locked"] = result.GetValueOrDefault("is_locked") ?? existingProfile.GetValueOrDefault("is_locked") ?? false,
                ["is_protected"] = result.GetValueOrDefault("is_protected") ?? existingProfile.GetValueOrDefault("is_protected") ?? false,
                ["is_active"] = true,
                ["last_login_at"] = DateTime.UtcNow
            };

            var currentUsername = existingProfile.GetValueOrDefault("username")?.ToString()?.Trim();
            var emailPrefix = email.Split('@')[0];

            if (!string.IsNullOrWhiteSpace(authDisplayName)
                && (string.IsNullOrWhiteSpace(currentUsername)
                    || string.Equals(currentUsername, emailPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                update["username"] = authDisplayName;
                update["displayName"] = authDisplayName;
                result["username"] = authDisplayName;
                result["displayName"] = authDisplayName;
            }
            else
            {
                result["username"] = currentUsername;
                result["displayName"] = existingProfile.GetValueOrDefault("displayName")?.ToString() ?? currentUsername;
            }

            var registeredAt = GetProfileDate(existingProfile, "created_at", "createdAt", "registeredAt");
            if (registeredAt.HasValue)
            {

                if (!existingProfile.ContainsKey("created_at")) update["created_at"] = registeredAt.Value;
                if (!existingProfile.ContainsKey("createdAt")) update["createdAt"] = registeredAt.Value;
                if (!existingProfile.ContainsKey("registeredAt")) update["registeredAt"] = registeredAt.Value;
            }

            await chatService.CreateOrUpdateUserAsync(uid, update);
        }
        catch (Exception ex)
        {

            result["profileSyncWarning"] = "Đăng nhập thành công nhưng chưa đồng bộ hồ sơ Supabase PostgreSQL. Kiểm tra appsettings.json.";
            result["profileSyncError"] = ex.Message;
        }
    }

    public async Task<Dictionary<string, object?>> VerifyTokenAsync(string idToken)
    {
        var result = await _authRepository.VerifyTokenAsync(idToken);
        if (!IsSuccess(result)) return result;

        var user = result.GetValueOrDefault("user") as Dictionary<string, object?>;
        var uid = user is null ? null : GetUserId(user);
        if (string.IsNullOrWhiteSpace(uid))
        {
            RejectUnregisteredAccount(result, "Không đọc được mã người dùng từ token đăng nhập.", stripAuthPayload: false);
            return result;
        }

        return result;
    }

    public async Task<Dictionary<string, object?>> RefreshTokenAsync(string refreshToken)
    {
        var result = await _authRepository.RefreshTokenAsync(refreshToken);
        if (!IsSuccess(result)) return result;

        if (result.GetValueOrDefault("idToken") is not string idToken || string.IsNullOrWhiteSpace(idToken))
        {
            RejectUnregisteredAccount(result, "Không làm mới được token đăng nhập.", stripAuthPayload: true);
            return result;
        }

        var verify = await _authRepository.VerifyTokenAsync(idToken);
        var user = verify.GetValueOrDefault("user") as Dictionary<string, object?>;
        var uid = user is null ? null : GetUserId(user);
        if (!IsSuccess(verify) || string.IsNullOrWhiteSpace(uid))
        {
            RejectUnregisteredAccount(result, verify.GetValueOrDefault("message")?.ToString() ?? "Token đăng nhập không hợp lệ.", stripAuthPayload: true);
            return result;
        }

        if (user is not null)
        {
            result["email"] = user.GetValueOrDefault("email")?.ToString();
            result["displayName"] = user.GetValueOrDefault("displayName")?.ToString();
            result["username"] = user.GetValueOrDefault("username")?.ToString() ?? user.GetValueOrDefault("displayName")?.ToString();
            result["role"] = user.GetValueOrDefault("role")?.ToString() ?? "User";
            result["is_locked"] = user.GetValueOrDefault("is_locked") ?? user.GetValueOrDefault("isLocked") ?? false;
            result["is_protected"] = user.GetValueOrDefault("is_protected") ?? user.GetValueOrDefault("isProtected") ?? false;
        }

        return result;
    }

    public Task<Dictionary<string, object?>> SendPasswordResetEmailAsync(string email) => _authRepository.SendPasswordResetEmailAsync(email);

    public Task<Dictionary<string, object?>> VerifyPasswordResetOtpAsync(string email, string otp)
    {
        return _authRepository.VerifyPasswordResetOtpAsync(email.Trim().ToLowerInvariant(), otp.Trim());
    }

    public Task<Dictionary<string, object?>> ResetPasswordWithTokenAsync(string email, string resetToken, string newPassword)
    {
        return _authRepository.ResetPasswordWithTokenAsync(email.Trim().ToLowerInvariant(), resetToken.Trim(), newPassword);
    }

    public Task<Dictionary<string, object?>> ChangePasswordAsync(string userId, string newPassword)
    {
        return _authRepository.ChangePasswordAsync(userId.Trim(), newPassword);
    }

    public string? GetUserId(Dictionary<string, object?> authUser)
    {
        if (authUser.TryGetValue("localId", out var localId) && localId is string l && !string.IsNullOrWhiteSpace(l)) return l;
        if (authUser.TryGetValue("uid", out var uid) && uid is string u && !string.IsNullOrWhiteSpace(u)) return u;
        return null;
    }

    private static bool IsSuccess(Dictionary<string, object?> result)
    {
        return result.TryGetValue("success", out var success) && success is bool b && b;
    }

    private static string NormalizeUsername(string? username, string email)
    {
        var value = username?.Trim();
        if (!string.IsNullOrWhiteSpace(value)) return value;
        var prefix = email.Split('@')[0].Trim();
        return string.IsNullOrWhiteSpace(prefix) ? email : prefix;
    }

    private static DateTime? GetProfileDate(Dictionary<string, object?>? profile, params string[] keys)
    {
        if (profile is null) return null;
        foreach (var key in keys)
        {
            if (profile.TryGetValue(key, out var raw) && TryParseAnyDate(raw, out var date)) return date;
        }
        return null;
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

    private static void RejectUnregisteredAccount(Dictionary<string, object?> result, string message, bool stripAuthPayload)
    {
        result["success"] = false;
        result["message"] = message;

        if (!stripAuthPayload) return;

        result.Remove("idToken");
        result.Remove("refreshToken");
        result.Remove("expiresIn");
    }
}
