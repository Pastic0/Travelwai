using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using TravelwAI.Data.Interfaces;
using TravelwAI.Data.Options;
using TravelwAI.Data.Services;

namespace TravelwAI.Data.Repositories;

public sealed class SupabaseAuthRepository : IAuthRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SupabaseOptions _options;
    private readonly EmailOptions _emailOptions;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        WriteIndented = false
    };

    public SupabaseAuthRepository(
        NpgsqlDataSource dataSource,
        IOptions<SupabaseOptions> options,
        IOptions<EmailOptions> emailOptions)
    {
        _dataSource = dataSource;
        _options = options.Value;
        _emailOptions = emailOptions.Value;
    }

    public async Task<bool> EmailExistsAsync(string email)
    {
        email = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email)) return false;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select 1 from app_users_auth where lower(email) = @email limit 1;";
        cmd.Parameters.AddWithValue("email", email);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    public async Task<Dictionary<string, object?>> SignUpAsync(string email, string password, string username)
    {
        email = email.Trim().ToLowerInvariant();
        username = string.IsNullOrWhiteSpace(username) ? email.Split('@')[0] : username.Trim();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return Fail("Vui lòng nhập email và mật khẩu.");
        }

        await using var conn = await _dataSource.OpenConnectionAsync();

        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "select 1 from app_users_auth where email = @email limit 1;";
            checkCmd.Parameters.AddWithValue("email", email);
            var exists = await checkCmd.ExecuteScalarAsync();
            if (exists is not null) return Fail("Email này đã được đăng ký.");
        }

        var userId = Guid.NewGuid().ToString("N");
        var salt = RandomBase64(32);
        var hash = HashPassword(password, salt);
        var createdAt = DateTime.UtcNow;
        var refreshToken = RandomToken(64);
        var refreshTokenHash = Sha256(refreshToken);
        var refreshExpires = createdAt.AddDays(Math.Max(1, _options.RefreshTokenDays));

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                insert into app_users_auth(
                    id,
                    email,
                    username,
                    password_hash,
                    password_salt,
                    refresh_token_hash,
                    refresh_token_expires_at,
                    role,
                    is_locked,
                    is_protected,
                    created_at,
                    updated_at,
                    last_login_at
                )
                values (
                    @id,
                    @email,
                    @username,
                    @password_hash,
                    @password_salt,
                    @refresh_token_hash,
                    @refresh_token_expires_at,
                    'Free',
                    false,
                    false,
                    @created_at,
                    @updated_at,
                    @last_login_at
                );
                """;
            cmd.Parameters.AddWithValue("id", userId);
            cmd.Parameters.AddWithValue("email", email);
            cmd.Parameters.AddWithValue("username", username);
            cmd.Parameters.AddWithValue("password_hash", hash);
            cmd.Parameters.AddWithValue("password_salt", salt);
            cmd.Parameters.AddWithValue("refresh_token_hash", refreshTokenHash);
            cmd.Parameters.AddWithValue("refresh_token_expires_at", refreshExpires);
            cmd.Parameters.AddWithValue("created_at", createdAt);
            cmd.Parameters.AddWithValue("updated_at", createdAt);
            cmd.Parameters.AddWithValue("last_login_at", createdAt);
            await cmd.ExecuteNonQueryAsync();
        }

        return SuccessTokenResult(userId, email, username, createdAt, refreshToken, "Free", false, false, "User registered successfully");
    }

    public async Task<Dictionary<string, object?>> SignInAsync(string email, string password)
    {
        email = email.Trim().ToLowerInvariant();

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select id, email, username, password_hash, password_salt, created_at, role, is_locked, is_protected
            from app_users_auth
            where email = @email
            limit 1;
            """;
        cmd.Parameters.AddWithValue("email", email);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return Fail("Email hoặc mật khẩu không đúng.");

        var userId = reader.GetString(0);
        var storedEmail = reader.GetString(1);
        var username = reader.GetString(2);
        var passwordHash = reader.GetString(3);
        var salt = reader.GetString(4);
        var createdAt = reader.GetDateTime(5).ToUniversalTime();
        var role = reader.GetString(6);
        var isLocked = reader.GetBoolean(7);
        var isProtected = reader.GetBoolean(8);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(passwordHash),
                Encoding.UTF8.GetBytes(HashPassword(password, salt))))
        {
            return Fail("Email hoặc mật khẩu không đúng.");
        }

        if (isLocked)
        {
            return Fail("Tài khoản của bạn đã bị khóa. Vui lòng liên hệ Admin.");
        }

        await reader.DisposeAsync();

        var refreshToken = RandomToken(64);
        var refreshTokenHash = Sha256(refreshToken);
        var refreshExpires = DateTime.UtcNow.AddDays(Math.Max(1, _options.RefreshTokenDays));

        await using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = """
            update app_users_auth
            set refresh_token_hash = @refresh_token_hash,
                refresh_token_expires_at = @refresh_token_expires_at,
                last_login_at = now(),
                updated_at = now()
            where id = @id;
            """;
        updateCmd.Parameters.AddWithValue("id", userId);
        updateCmd.Parameters.AddWithValue("refresh_token_hash", refreshTokenHash);
        updateCmd.Parameters.AddWithValue("refresh_token_expires_at", refreshExpires);
        await updateCmd.ExecuteNonQueryAsync();

        return SuccessTokenResult(userId, storedEmail, username, createdAt, refreshToken, role, isLocked, isProtected, "Login successful");
    }

    public async Task<Dictionary<string, object?>> VerifyTokenAsync(string idToken)
    {
        var payload = ValidateToken(idToken);
        if (payload is null) return Fail("Token đăng nhập không hợp lệ hoặc đã hết hạn.");

        var userId = payload.GetValueOrDefault("uid")?.ToString();
        if (string.IsNullOrWhiteSpace(userId)) return Fail("Token đăng nhập không có mã người dùng.");

        var user = await GetUserAsync(userId);
        if (user is null) return Fail("Không tìm thấy tài khoản.");
        if (IsTruthy(user.GetValueOrDefault("is_locked"))) return Fail("Tài khoản của bạn đã bị khóa. Vui lòng liên hệ Admin.");

        return new Dictionary<string, object?>
        {
            ["message"] = "Token verified",
            ["success"] = true,
            ["user"] = user
        };
    }

    public async Task<Dictionary<string, object?>> RefreshTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return Fail("Refresh token không hợp lệ.");

        var tokenHash = Sha256(refreshToken.Trim());
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select id, email, username, created_at, role, is_locked, is_protected
            from app_users_auth
            where refresh_token_hash = @refresh_token_hash
              and refresh_token_expires_at > now()
            limit 1;
            """;
        cmd.Parameters.AddWithValue("refresh_token_hash", tokenHash);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return Fail("Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.");

        var userId = reader.GetString(0);
        var email = reader.GetString(1);
        var username = reader.GetString(2);
        var createdAt = reader.GetDateTime(3).ToUniversalTime();
        var role = reader.GetString(4);
        var isLocked = reader.GetBoolean(5);
        var isProtected = reader.GetBoolean(6);
        await reader.DisposeAsync();

        if (isLocked)
        {
            return Fail("Tài khoản của bạn đã bị khóa. Vui lòng liên hệ Admin.");
        }

        var newRefreshToken = RandomToken(64);
        var newRefreshTokenHash = Sha256(newRefreshToken);
        var refreshExpires = DateTime.UtcNow.AddDays(Math.Max(1, _options.RefreshTokenDays));

        await using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = """
            update app_users_auth
            set refresh_token_hash = @refresh_token_hash,
                refresh_token_expires_at = @refresh_token_expires_at,
                updated_at = now()
            where id = @id;
            """;
        updateCmd.Parameters.AddWithValue("id", userId);
        updateCmd.Parameters.AddWithValue("refresh_token_hash", newRefreshTokenHash);
        updateCmd.Parameters.AddWithValue("refresh_token_expires_at", refreshExpires);
        await updateCmd.ExecuteNonQueryAsync();

        return SuccessTokenResult(userId, email, username, createdAt, newRefreshToken, role, isLocked, isProtected, "Token refreshed successfully");
    }

    public async Task<Dictionary<string, object?>> SendPasswordResetEmailAsync(string email)
    {
        email = email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email)) return Fail("Vui lòng nhập email.");

        await using var conn = await _dataSource.OpenConnectionAsync();

        string? username = null;
        await using (var userCmd = conn.CreateCommand())
        {
            userCmd.CommandText = "select username from app_users_auth where lower(email) = @email limit 1;";
            userCmd.Parameters.AddWithValue("email", email);
            var userResult = await userCmd.ExecuteScalarAsync();
            username = userResult?.ToString();
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return new Dictionary<string, object?>
            {
                ["success"] = true,
                ["message"] = "Nếu email tồn tại, mã OTP đổi mật khẩu đã được gửi."
            };
        }

        var emailConfigError = ResendEmailSender.GetConfigError(_emailOptions);
        if (!string.IsNullOrWhiteSpace(emailConfigError))
        {
            return Fail(emailConfigError);
        }

        var otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(5);

        await using (var cleanupCmd = conn.CreateCommand())
        {
            cleanupCmd.CommandText = "update password_reset_codes set used_at = now() where lower(email) = @email and used_at is null;";
            cleanupCmd.Parameters.AddWithValue("email", email);
            await cleanupCmd.ExecuteNonQueryAsync();
        }

        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.CommandText = """
                insert into password_reset_codes(id, email, code_hash, expires_at, created_at)
                values (@id, @email, @code_hash, @expires_at, @created_at);
                """;
            insertCmd.Parameters.AddWithValue("id", Guid.NewGuid().ToString("N"));
            insertCmd.Parameters.AddWithValue("email", email);
            insertCmd.Parameters.AddWithValue("code_hash", Sha256($"{email}:{otp}"));
            insertCmd.Parameters.AddWithValue("expires_at", expiresAt);
            insertCmd.Parameters.AddWithValue("created_at", now);
            await insertCmd.ExecuteNonQueryAsync();
        }

        var emailError = await TrySendPasswordResetOtpEmailAsync(email, username!, otp, expiresAt);
        if (!string.IsNullOrWhiteSpace(emailError))
        {
            await using var markFailedCmd = conn.CreateCommand();
            markFailedCmd.CommandText = "update password_reset_codes set used_at = now(), updated_at = now() where lower(email) = @email and code_hash = @code_hash and used_at is null;";
            markFailedCmd.Parameters.AddWithValue("email", email);
            markFailedCmd.Parameters.AddWithValue("code_hash", Sha256($"{email}:{otp}"));
            await markFailedCmd.ExecuteNonQueryAsync();

            return Fail(emailError);
        }

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["message"] = "Mã OTP đổi mật khẩu đã được gửi đến email của bạn.",
            ["emailSent"] = true
        };
    }

    public async Task<Dictionary<string, object?>> VerifyPasswordResetOtpAsync(string email, string otp)
    {
        email = email.Trim().ToLowerInvariant();
        otp = otp.Trim();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(otp))
        {
            return Fail("Vui lòng nhập email và mã OTP.");
        }

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select id
            from password_reset_codes
            where lower(email) = @email
              and code_hash = @code_hash
              and used_at is null
              and expires_at > now()
            order by created_at desc
            limit 1;
            """;
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("code_hash", Sha256($"{email}:{otp}"));

        var codeId = (await cmd.ExecuteScalarAsync())?.ToString();
        if (string.IsNullOrWhiteSpace(codeId))
        {
            return Fail("Mã OTP không đúng hoặc đã hết hạn.");
        }

        var resetToken = RandomToken(48);
        await using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = """
            update password_reset_codes
            set reset_token_hash = @reset_token_hash,
                verified_at = now(),
                updated_at = now()
            where id = @id;
            """;
        updateCmd.Parameters.AddWithValue("id", codeId);
        updateCmd.Parameters.AddWithValue("reset_token_hash", Sha256($"{email}:{resetToken}"));
        await updateCmd.ExecuteNonQueryAsync();

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["message"] = "Xác nhận OTP thành công. Vui lòng đặt mật khẩu mới.",
            ["resetToken"] = resetToken
        };
    }

    public async Task<Dictionary<string, object?>> ResetPasswordWithTokenAsync(string email, string resetToken, string newPassword)
    {
        email = email.Trim().ToLowerInvariant();
        resetToken = resetToken.Trim();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(resetToken) || string.IsNullOrWhiteSpace(newPassword))
        {
            return Fail("Vui lòng nhập đầy đủ thông tin đổi mật khẩu.");
        }

        if (newPassword.Length < 6)
        {
            return Fail("Mật khẩu mới phải có ít nhất 6 ký tự.");
        }

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        string? codeId;
        await using (var codeCmd = conn.CreateCommand())
        {
            codeCmd.Transaction = tx;
            codeCmd.CommandText = """
                select id
                from password_reset_codes
                where lower(email) = @email
                  and reset_token_hash = @reset_token_hash
                  and verified_at is not null
                  and used_at is null
                  and expires_at > now()
                order by created_at desc
                limit 1;
                """;
            codeCmd.Parameters.AddWithValue("email", email);
            codeCmd.Parameters.AddWithValue("reset_token_hash", Sha256($"{email}:{resetToken}"));
            codeId = (await codeCmd.ExecuteScalarAsync())?.ToString();
        }

        if (string.IsNullOrWhiteSpace(codeId))
        {
            await tx.RollbackAsync();
            return Fail("Phiên đổi mật khẩu không hợp lệ hoặc đã hết hạn.");
        }

        var salt = RandomBase64(32);
        var hash = HashPassword(newPassword, salt);

        await using (var updatePasswordCmd = conn.CreateCommand())
        {
            updatePasswordCmd.Transaction = tx;
            updatePasswordCmd.CommandText = """
                update app_users_auth
                set password_hash = @password_hash,
                    password_salt = @password_salt,
                    refresh_token_hash = null,
                    refresh_token_expires_at = null,
                    updated_at = now()
                where lower(email) = @email;
                """;
            updatePasswordCmd.Parameters.AddWithValue("email", email);
            updatePasswordCmd.Parameters.AddWithValue("password_hash", hash);
            updatePasswordCmd.Parameters.AddWithValue("password_salt", salt);
            var affected = await updatePasswordCmd.ExecuteNonQueryAsync();
            if (affected == 0)
            {
                await tx.RollbackAsync();
                return Fail("Không tìm thấy tài khoản cần đổi mật khẩu.");
            }
        }

        await using (var usedCmd = conn.CreateCommand())
        {
            usedCmd.Transaction = tx;
            usedCmd.CommandText = "update password_reset_codes set used_at = now(), updated_at = now() where id = @id;";
            usedCmd.Parameters.AddWithValue("id", codeId);
            await usedCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["message"] = "Đổi mật khẩu thành công. Vui lòng đăng nhập lại."
        };
    }

    public async Task<Dictionary<string, object?>> ChangePasswordAsync(string userId, string newPassword)
    {
        userId = userId.Trim();

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(newPassword))
        {
            return Fail("Vui lòng nhập mật khẩu mới.");
        }

        if (newPassword.Length < 6)
        {
            return Fail("Mật khẩu mới phải có ít nhất 6 ký tự.");
        }

        var salt = RandomBase64(32);
        var hash = HashPassword(newPassword, salt);

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            update app_users_auth
            set password_hash = @password_hash,
                password_salt = @password_salt,
                updated_at = now()
            where id = @id;
            """;
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("password_hash", hash);
        cmd.Parameters.AddWithValue("password_salt", salt);

        var affected = await cmd.ExecuteNonQueryAsync();
        if (affected == 0)
        {
            return Fail("Không tìm thấy tài khoản cần đổi mật khẩu.");
        }

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["message"] = "Đổi mật khẩu thành công."
        };
    }

    private async Task<Dictionary<string, object?>?> GetUserAsync(string userId)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select id, email, username, created_at, last_login_at, role, is_locked, is_protected
            from app_users_auth
            where id = @id
            limit 1;
            """;
        cmd.Parameters.AddWithValue("id", userId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var createdAt = reader.GetDateTime(3).ToUniversalTime();
        var role = reader.GetString(5);
        var isLocked = reader.GetBoolean(6);
        var isProtected = reader.GetBoolean(7);
        return new Dictionary<string, object?>
        {
            ["localId"] = reader.GetString(0),
            ["uid"] = reader.GetString(0),
            ["email"] = reader.GetString(1),
            ["displayName"] = reader.GetString(2),
            ["username"] = reader.GetString(2),
            ["createdAt"] = new DateTimeOffset(createdAt).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["lastLoginAt"] = reader.IsDBNull(4) ? null : reader.GetDateTime(4).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["role"] = role,
            ["is_locked"] = isLocked,
            ["isLocked"] = isLocked,
            ["is_protected"] = isProtected,
            ["isProtected"] = isProtected
        };
    }

    private static string NormalizeStoredRole(string? role)
    {
        var value = (role ?? string.Empty).Trim();
        if (string.Equals(value, "User", StringComparison.OrdinalIgnoreCase)) return "Free";
        if (string.Equals(value, "Company", StringComparison.OrdinalIgnoreCase)) return "Business";
        return string.IsNullOrWhiteSpace(value) ? "Free" : value;
    }

    private Dictionary<string, object?> SuccessTokenResult(string userId, string email, string username, DateTime createdAt, string refreshToken, string role, bool isLocked, bool isProtected, string message)
    {
        var expiresIn = Math.Max(5, _options.AccessTokenMinutes) * 60;
        role = NormalizeStoredRole(role);
        var idToken = CreateToken(userId, email, username, role, TimeSpan.FromSeconds(expiresIn));

        return new Dictionary<string, object?>
        {
            ["message"] = message,
            ["success"] = true,
            ["idToken"] = idToken,
            ["refreshToken"] = refreshToken,
            ["expiresIn"] = expiresIn.ToString(CultureInfo.InvariantCulture),
            ["localId"] = userId,
            ["email"] = email,
            ["displayName"] = username,
            ["username"] = username,
            ["role"] = role,
            ["is_locked"] = isLocked,
            ["isLocked"] = isLocked,
            ["is_protected"] = isProtected,
            ["isProtected"] = isProtected,
            ["createdAt"] = new DateTimeOffset(createdAt).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
        };
    }

    private string CreateToken(string userId, string email, string username, string role, TimeSpan lifetime)
    {
        var now = DateTimeOffset.UtcNow;
        var header = new Dictionary<string, object?>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        };
        var payload = new Dictionary<string, object?>
        {
            ["uid"] = userId,
            ["localId"] = userId,
            ["email"] = email,
            ["displayName"] = username,
            ["role"] = role,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(lifetime).ToUnixTimeSeconds(),
            ["iss"] = "TravelwAI.Supabase"
        };

        var headerPart = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header, JsonOptions)));
        var payloadPart = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions)));
        var signaturePart = Sign($"{headerPart}.{payloadPart}");
        return $"{headerPart}.{payloadPart}.{signaturePart}";
    }

    private Dictionary<string, object?>? ValidateToken(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        var unsigned = $"{parts[0]}.{parts[1]}";
        var expected = Sign(unsigned);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(parts[2])))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
            if (!payload.TryGetValue("exp", out var expElement) || !expElement.TryGetInt64(out var exp)) return null;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= exp) return null;
            return payload.ToDictionary(k => k.Key, k => JsonElementToObject(k.Value));
        }
        catch
        {
            return null;
        }
    }

    private Task<string?> TrySendPasswordResetOtpEmailAsync(string toEmail, string username, string otp, DateTime expiresAt)
    {
        var body = $"""
            Xin chào {username},

            Mã OTP đổi mật khẩu TravelwAI của bạn là: {otp}

            Mã này có hiệu lực đến {expiresAt.ToLocalTime():HH:mm dd/MM/yyyy}. Không chia sẻ mã này cho người khác.

            TravelwAI
            """;

        return ResendEmailSender.SendPlainEmailAsync(
            _emailOptions,
            toEmail,
            "Mã OTP đổi mật khẩu TravelwAI",
            body);
    }

    private string Sign(string value)
    {
        var secret = GetJwtSecret();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private string GetJwtSecret()
    {
        if (!string.IsNullOrWhiteSpace(_options.JwtSecret) && _options.JwtSecret.Length >= 32)
        {
            return _options.JwtSecret;
        }
        throw new InvalidOperationException("Supabase:JwtSecret is missing or too short. Set a random string with at least 32 characters in appsettings.json or environment variables.");
    }

    private static string HashPassword(string password, string salt)
    {
        var bytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Convert.FromBase64String(salt),
            120_000,
            HashAlgorithmName.SHA256,
            32);
        return Convert.ToBase64String(bytes);
    }

    private static string RandomBase64(int byteCount)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount));
    }

    private static string RandomToken(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base64UrlEncode(bytes);
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };
        return Convert.FromBase64String(s);
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool IsTruthy(object? value)
    {
        if (value is bool b) return b;
        return bool.TryParse(value?.ToString(), out var parsed) && parsed;
    }

    private static Dictionary<string, object?> Fail(string message) => new()
    {
        ["message"] = message,
        ["success"] = false
    };
}
