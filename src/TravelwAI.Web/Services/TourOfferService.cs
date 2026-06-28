using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using TravelwAI.Data.Interfaces;
using TravelwAI.Data.Options;
using TravelwAI.Data.Services;

namespace TravelwAI.Web.Services;

public sealed class TourOfferService
{
    public const int MaxInvitesForDiscount = 5;
    public const int DiscountPerAcceptedInvite = 4;
    public const int InviteExpirationMinutes = 3;
    public const string InviteCollection = "tour_offer_invites";
    public const string PostOfferCollection = "post_tour_offers";
    public const int PostOfferDiscountPercent = 5;
    private const string SalesLevelSettingsCollection = "sales_level_settings";
    private const string SalesLevelSettingsDocumentId = "default";

    private readonly IDataRepository _repo;
    private readonly IAuthRepository _authRepository;
    private readonly EmailOptions _emailOptions;
    private readonly IConfiguration _configuration;

    public TourOfferService(
        IDataRepository repo,
        IAuthRepository authRepository,
        IOptions<EmailOptions> emailOptions,
        IConfiguration configuration)
    {
        _repo = repo;
        _authRepository = authRepository;
        _emailOptions = emailOptions.Value;
        _configuration = configuration;
    }

    public async Task<Dictionary<string, object?>> GetStatusAsync(string userId, string? userEmail)
    {
        await CleanupExpiredPendingInvitesForInviterAsync(userId);
        var invites = await GetInvitesByInviterAsync(userId);
        var accepted = invites
            .Where(IsAcceptedInvite)
            .GroupBy(x => Text(x, "invited_email").ToLowerInvariant())
            .Select(g => g.First())
            .Take(MaxInvitesForDiscount)
            .ToList();

        var progress = Math.Min(MaxInvitesForDiscount, accepted.Count);
        var inviteDiscountPercent = progress * DiscountPerAcceptedInvite;
        var canUsePostOffer = await CanUsePostOfferAsync(userId);
        var postOffer = canUsePostOffer ? await GetActivePostOfferAsync(userId) : null;
        var postOfferDiscountPercent = postOffer is null ? 0 : PostOfferDiscountPercent;
        var automaticDiscountPercent = inviteDiscountPercent + postOfferDiscountPercent;
        var adminOfferDiscountPercent = await GetAdminOfferDiscountOverrideAsync(userId);
        var discountPercent = adminOfferDiscountPercent ?? automaticDiscountPercent;

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["inviter_id"] = userId,
            ["inviter_email"] = userEmail ?? string.Empty,
            ["progress"] = progress,
            ["target"] = MaxInvitesForDiscount,
            ["discount_percent"] = discountPercent,
            ["automatic_discount_percent"] = automaticDiscountPercent,
            ["admin_offer_discount_percent"] = adminOfferDiscountPercent ?? 0,
            ["admin_offer_override"] = adminOfferDiscountPercent.HasValue,
            ["invite_discount_percent"] = inviteDiscountPercent,
            ["post_offer_discount_percent"] = postOfferDiscountPercent,
            ["post_offer_active"] = postOffer is not null,
            ["invites"] = invites.Select(ToClientInvite).ToList()
        };
    }

    public async Task<int> GetDiscountPercentAsync(string userId)
    {
        await CleanupExpiredPendingInvitesForInviterAsync(userId);
        var invites = await GetInvitesByInviterAsync(userId);
        var acceptedCount = invites
            .Where(IsAcceptedInvite)
            .Select(x => Text(x, "invited_email").ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxInvitesForDiscount)
            .Count();

        return Math.Min(MaxInvitesForDiscount, acceptedCount) * DiscountPerAcceptedInvite;
    }

    public async Task<Dictionary<string, object?>> GetPostOfferStatusAsync(string userId)
    {
        var canUsePostOffer = await CanUsePostOfferAsync(userId);
        var postOffer = canUsePostOffer ? await GetActivePostOfferAsync(userId) : null;
        var active = postOffer is not null;
        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["has_offer"] = active,
            ["post_offer_active"] = active,
            ["discount_percent"] = active ? PostOfferDiscountPercent : 0,
            ["target"] = 1,
            ["progress"] = active ? 1 : 0,
            ["message"] = active
                ? "Bạn có ưu đãi 5% cho đơn tour tiếp theo."
                : "Tạo bài viết để nhận ưu đãi 5% cho lần đặt tour tiếp theo."
        };
    }

    public async Task<BookingDiscountResult> GetBookingDiscountAsync(string userId)
    {
        var inviteDiscountPercent = await GetDiscountPercentAsync(userId);
        var canUsePostOffer = await CanUsePostOfferAsync(userId);
        var postOffer = canUsePostOffer ? await GetActivePostOfferAsync(userId) : null;
        var postOfferDiscountPercent = postOffer is null ? 0 : PostOfferDiscountPercent;
        var automaticDiscountPercent = inviteDiscountPercent + postOfferDiscountPercent;
        var adminOfferDiscountPercent = await GetAdminOfferDiscountOverrideAsync(userId);
        var discountPercent = adminOfferDiscountPercent ?? automaticDiscountPercent;
        var source = discountPercent <= 0
            ? string.Empty
            : adminOfferDiscountPercent.HasValue
                ? "Admin"
                : inviteDiscountPercent > 0 && postOfferDiscountPercent > 0
                    ? "Mời bạn + Bài viết"
                    : postOfferDiscountPercent > 0
                        ? "Bài viết"
                        : "Mời bạn";

        return new BookingDiscountResult(
            discountPercent,
            inviteDiscountPercent,
            postOfferDiscountPercent,
            source,
            postOffer is null ? string.Empty : Text(postOffer, "id"));
    }

    public async Task GrantPostOfferAsync(string userId, string? postId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        var now = DateTime.UtcNow;
        await _repo.SetAsync(PostOfferCollection, BuildPostOfferDocumentId(userId), new Dictionary<string, object?>
        {
            ["user_id"] = userId,
            ["post_id"] = postId ?? string.Empty,
            ["discount_percent"] = PostOfferDiscountPercent,
            ["status"] = "active",
            ["source"] = "post",
            ["created_at"] = now,
            ["updated_at"] = now
        }, merge: false);
    }

    public async Task ConsumePostOfferAsync(string userId, string? orderId)
    {
        var postOffer = await GetActivePostOfferAsync(userId);
        if (postOffer is null) return;

        var id = Text(postOffer, "id");
        if (string.IsNullOrWhiteSpace(id)) return;

        await _repo.DeleteAsync(PostOfferCollection, id);
    }

    private async Task<bool> CanUsePostOfferAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var user = await _repo.GetByIdAsync("users", userId);
        if (user is null) return true;
        var role = TextAny(user, "role", "userRole");
        if (string.Equals(role, "User", StringComparison.OrdinalIgnoreCase)) role = "Free";
        if (string.Equals(role, "Company", StringComparison.OrdinalIgnoreCase)) role = "Business";
        return !string.Equals(role, "Free", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Dictionary<string, object?>> InviteAsync(HttpRequest request, string inviterId, string inviterEmail, string? inviterName, string invitedEmail)
    {
        var normalizedInvitedEmail = NormalizeEmail(invitedEmail);
        if (string.IsNullOrWhiteSpace(normalizedInvitedEmail) || !normalizedInvitedEmail.Contains('@'))
        {
            return new Dictionary<string, object?> { ["success"] = false, ["message"] = "Vui lòng nhập Gmail hợp lệ." };
        }

        var normalizedInviterEmail = NormalizeEmail(inviterEmail);
        if (!string.IsNullOrWhiteSpace(normalizedInviterEmail)
            && string.Equals(normalizedInviterEmail, normalizedInvitedEmail, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?> { ["success"] = false, ["message"] = "Không thể tự mời chính mình." };
        }

        if (await _authRepository.EmailExistsAsync(normalizedInvitedEmail))
        {
            await DeletePendingInvitesForEmailAsync(normalizedInvitedEmail);
            return new Dictionary<string, object?>
            {
                ["success"] = true,
                ["removed"] = true,
                ["message"] = "Gmail đã được sử dụng."
            };
        }

        var emailInvites = await GetInvitesByInvitedEmailAsync(normalizedInvitedEmail);
        await DeleteExpiredPendingInvitesAsync(emailInvites);
        emailInvites = emailInvites.Where(x => !IsExpiredPendingInvite(x)).ToList();
        var existing = emailInvites.FirstOrDefault(x =>
            string.Equals(Text(x, "inviter_id"), inviterId, StringComparison.OrdinalIgnoreCase));

        var deterministicId = BuildInviteDocumentId(inviterId, normalizedInvitedEmail);

        var token = existing is null ? CreateToken() : Text(existing, "token");
        if (string.IsNullOrWhiteSpace(token)) token = CreateToken();

        var inviteCode = existing is null ? await CreateUniqueInviteCodeAsync() : CleanInviteCode(Text(existing, "invite_code"));
        if (string.IsNullOrWhiteSpace(inviteCode)) inviteCode = await CreateUniqueInviteCodeAsync();

        var inviteLink = BuildInviteLink(request, inviteCode, normalizedInvitedEmail);
        var now = DateTime.UtcNow;
        string? id = existing is null ? deterministicId : Text(existing, "id");
        if (string.IsNullOrWhiteSpace(id)) id = deterministicId;

        if (existing is null)
        {
            await _repo.SetAsync(InviteCollection, id!, new Dictionary<string, object?>
            {
                ["inviter_id"] = inviterId,
                ["inviter_email"] = normalizedInviterEmail,
                ["inviter_name"] = inviterName ?? string.Empty,
                ["invited_email"] = normalizedInvitedEmail,
                ["token"] = token,
                ["invite_code"] = inviteCode,
                ["status"] = "Đã mời",
                ["discount_percent"] = 0,
                ["expires_at"] = now.AddMinutes(InviteExpirationMinutes),
                ["created_at"] = now,
                ["updated_at"] = now
            }, merge: false);
        }
        else if (!string.Equals(Text(existing, "status"), "Đã đăng ký", StringComparison.OrdinalIgnoreCase))
        {
            await _repo.UpdateAsync(InviteCollection, id!, new Dictionary<string, object?>
            {
                ["token"] = token,
                ["invite_code"] = inviteCode,
                ["status"] = "Đã mời",
                ["discount_percent"] = 0,
                ["expires_at"] = now.AddMinutes(InviteExpirationMinutes),
                ["updated_at"] = now
            });
        }

        var emailError = await TrySendInviteEmailAsync(normalizedInvitedEmail, inviterName, inviteLink, inviteCode);
        var alreadyAccepted = existing is not null && IsAcceptedInvite(existing);
        var message = alreadyAccepted
            ? $"Gmail này đã đăng ký từ mã mời {inviteCode}."
            : string.IsNullOrWhiteSpace(emailError)
                ? $"Đã gửi mã mời {inviteCode} đến Gmail. Mã có hiệu lực trong {InviteExpirationMinutes} phút."
                : $"Đã tạo mã {inviteCode} nhưng chưa gửi được email. Mã có hiệu lực trong {InviteExpirationMinutes} phút. Lỗi: {emailError}";

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["message"] = message,
            ["invite_id"] = id,
            ["invite_code"] = inviteCode,
            ["invite_link"] = inviteLink,
            ["email_error"] = emailError ?? string.Empty
        };
    }

    public async Task ConfirmSignupAsync(string registeredEmail, string registeredUserId, string? inviteToken)
    {
        var normalizedEmail = NormalizeEmail(registeredEmail);
        if (string.IsNullOrWhiteSpace(normalizedEmail)) return;

        var inviteKey = CleanInviteCode(inviteToken);
        if (string.IsNullOrWhiteSpace(inviteKey)) return;

        var invites = await _repo.WhereEqualAsync(InviteCollection, "invite_code", inviteKey, limit: 10);
        if (invites.Count == 0 && !string.IsNullOrWhiteSpace(inviteToken))
        {
            invites = await _repo.WhereEqualAsync(InviteCollection, "token", inviteToken.Trim(), limit: 10);
        }

        var invite = invites.FirstOrDefault(x =>
            string.Equals(Text(x, "invited_email"), normalizedEmail, StringComparison.OrdinalIgnoreCase)
            && !IsAcceptedInvite(x));

        if (invite is null) return;

        var inviteId = Text(invite, "id");
        if (string.IsNullOrWhiteSpace(inviteId)) return;

        if (IsExpiredPendingInvite(invite))
        {
            await _repo.DeleteAsync(InviteCollection, inviteId);
            return;
        }

        await _repo.UpdateAsync(InviteCollection, inviteId, new Dictionary<string, object?>
        {
            ["status"] = "Đã đăng ký",
            ["invited_user_id"] = registeredUserId,
            ["discount_percent"] = DiscountPerAcceptedInvite,
            ["accepted_at"] = DateTime.UtcNow,
            ["updated_at"] = DateTime.UtcNow
        });
    }

    public async Task<int> DeletePendingInvitesForEmailAsync(string email)
    {
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail)) return 0;

        var invites = await GetInvitesByInvitedEmailAsync(normalizedEmail);
        var deleted = 0;
        foreach (var invite in invites.Where(x => !IsAcceptedInvite(x)))
        {
            var id = Text(invite, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (await _repo.DeleteAsync(InviteCollection, id)) deleted++;
        }
        return deleted;
    }


    public async Task<int> DeleteOffersForDeletedAccountAsync(string userId, string? email)
    {
        var deleted = 0;
        var cleanUserId = (userId ?? string.Empty).Trim();
        var normalizedEmail = NormalizeEmail(email);

        var inviteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var postOfferIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddIds(IEnumerable<Dictionary<string, object?>> rows, HashSet<string> ids)
        {
            foreach (var row in rows)
            {
                var id = Text(row, "id");
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            }
        }

        if (!string.IsNullOrWhiteSpace(cleanUserId))
        {
            AddIds(await _repo.WhereEqualAsync(InviteCollection, "inviter_id", cleanUserId, limit: 500), inviteIds);
            AddIds(await _repo.WhereEqualAsync(InviteCollection, "invited_user_id", cleanUserId, limit: 500), inviteIds);
            AddIds(await _repo.WhereEqualAsync(PostOfferCollection, "user_id", cleanUserId, limit: 200), postOfferIds);
        }

        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            AddIds(await _repo.WhereEqualAsync(InviteCollection, "inviter_email", normalizedEmail, limit: 500), inviteIds);
            AddIds(await _repo.WhereEqualAsync(InviteCollection, "invited_email", normalizedEmail, limit: 500), inviteIds);
        }

        foreach (var id in inviteIds)
        {
            if (await _repo.DeleteAsync(InviteCollection, id)) deleted++;
        }

        foreach (var id in postOfferIds)
        {
            if (await _repo.DeleteAsync(PostOfferCollection, id)) deleted++;
        }

        return deleted;
    }

    private async Task CleanupExpiredPendingInvitesForInviterAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        var invites = await GetInvitesByInviterAsync(userId);
        await DeleteExpiredPendingInvitesAsync(invites);
    }

    private async Task DeleteExpiredPendingInvitesAsync(IEnumerable<Dictionary<string, object?>> invites)
    {
        foreach (var invite in invites.Where(IsExpiredPendingInvite))
        {
            var id = Text(invite, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                await _repo.DeleteAsync(InviteCollection, id);
            }
        }
    }


    private sealed record SalesLevelSetting(int Level, int CommissionPercent, int OfferDiscountPercent);

    private async Task<int?> GetAdminOfferDiscountOverrideAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        var user = await _repo.GetByIdAsync("users", userId);
        if (user is null) return null;

        if (IsSalesRole(TextAny(user, "role", "userRole")))
        {
            var level = ClampSalesLevel(TryInt(user.GetValueOrDefault("offer_level")) ?? TryInt(user.GetValueOrDefault("offerLevel")) ?? TryInt(user.GetValueOrDefault("sales_level")) ?? TryInt(user.GetValueOrDefault("salesLevel")) ?? 1);
            var settings = await GetSalesLevelSettingsAsync();
            return ClampPercent(TryInt(user.GetValueOrDefault("offer_discount_percent")) ?? TryInt(user.GetValueOrDefault("offerDiscountPercent")) ?? GetSalesLevelSetting(settings, level).OfferDiscountPercent);
        }

        if (!IsTruthy(user.GetValueOrDefault("admin_offer_override")) && !IsTruthy(user.GetValueOrDefault("adminOfferOverride"))) return null;
        var value = TryInt(user.GetValueOrDefault("admin_offer_discount_percent"))
            ?? TryInt(user.GetValueOrDefault("adminOfferDiscountPercent"))
            ?? TryInt(user.GetValueOrDefault("offer_discount_percent"))
            ?? TryInt(user.GetValueOrDefault("offerDiscountPercent"))
            ?? 0;
        return Math.Clamp(value, 0, 100);
    }

    private async Task<List<SalesLevelSetting>> GetSalesLevelSettingsAsync()
    {
        Dictionary<string, object?>? doc = null;
        try
        {
            doc = await _repo.GetByIdAsync(SalesLevelSettingsCollection, SalesLevelSettingsDocumentId);
        }
        catch
        {
            doc = null;
        }

        if (doc?.GetValueOrDefault("levels") is IEnumerable<object?> rawLevels)
        {
            var parsed = rawLevels
                .OfType<Dictionary<string, object?>>()
                .Select(item => new SalesLevelSetting(
                    ClampSalesLevel(TryInt(item.GetValueOrDefault("level")) ?? 1),
                    ClampPercent(TryInt(item.GetValueOrDefault("commission_percent")) ?? TryInt(item.GetValueOrDefault("commissionPercent")) ?? DefaultSalesLevelSetting(ClampSalesLevel(TryInt(item.GetValueOrDefault("level")) ?? 1)).CommissionPercent),
                    ClampPercent(TryInt(item.GetValueOrDefault("offer_discount_percent")) ?? TryInt(item.GetValueOrDefault("offerDiscountPercent")) ?? DefaultSalesLevelSetting(ClampSalesLevel(TryInt(item.GetValueOrDefault("level")) ?? 1)).OfferDiscountPercent)
                ))
                .ToList();
            if (parsed.Count > 0) return NormalizeSalesLevelSettings(parsed);
        }

        return NormalizeSalesLevelSettings(Array.Empty<SalesLevelSetting>());
    }

    private static List<SalesLevelSetting> NormalizeSalesLevelSettings(IEnumerable<SalesLevelSetting> settings)
    {
        var map = settings
            .GroupBy(item => ClampSalesLevel(item.Level))
            .ToDictionary(group => group.Key, group => group.Last());

        return Enumerable.Range(1, 5)
            .Select(level => map.TryGetValue(level, out var item)
                ? new SalesLevelSetting(level, ClampPercent(item.CommissionPercent), ClampPercent(item.OfferDiscountPercent))
                : DefaultSalesLevelSetting(level))
            .ToList();
    }

    private static SalesLevelSetting DefaultSalesLevelSetting(int level) => ClampSalesLevel(level) switch
    {
        2 => new SalesLevelSetting(2, 12, 0),
        3 => new SalesLevelSetting(3, 15, 0),
        4 => new SalesLevelSetting(4, 18, 0),
        5 => new SalesLevelSetting(5, 20, 0),
        _ => new SalesLevelSetting(1, 8, 0)
    };

    private static SalesLevelSetting GetSalesLevelSetting(IReadOnlyCollection<SalesLevelSetting> settings, int level)
    {
        var safeLevel = ClampSalesLevel(level);
        return settings.FirstOrDefault(item => item.Level == safeLevel) ?? DefaultSalesLevelSetting(safeLevel);
    }

    private static int ClampSalesLevel(int level) => Math.Clamp(level, 1, 5);
    private static int ClampPercent(int value) => Math.Clamp(value, 0, 100);
    private static bool IsSalesRole(string? role) => string.Equals(role, "Sales", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "Tour Sales", StringComparison.OrdinalIgnoreCase);
    private static string TextAny(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Text(row, key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }

    private async Task<Dictionary<string, object?>?> GetActivePostOfferAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        var rows = await _repo.WhereEqualAsync(PostOfferCollection, "user_id", userId, limit: 20);
        return rows
            .Where(IsActivePostOffer)
            .OrderByDescending(x => TryDate(x.GetValueOrDefault("updated_at")) ?? TryDate(x.GetValueOrDefault("created_at")) ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    private static bool IsActivePostOffer(Dictionary<string, object?> offer)
    {
        var status = Text(offer, "status");
        return string.IsNullOrWhiteSpace(status)
            || string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Đang có", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPostOfferDocumentId(string userId)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("post-offer:" + userId.Trim()));
        return "postoffer_" + Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private async Task<List<Dictionary<string, object?>>> GetInvitesByInviterAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return new List<Dictionary<string, object?>>();
        var rows = await _repo.WhereEqualAsync(InviteCollection, "inviter_id", userId, limit: 200);
        return rows
            .OrderByDescending(x => TryDate(x.GetValueOrDefault("updated_at")) ?? TryDate(x.GetValueOrDefault("created_at")) ?? DateTime.MinValue)
            .ToList();
    }

    private async Task<List<Dictionary<string, object?>>> GetInvitesByInvitedEmailAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return new List<Dictionary<string, object?>>();
        var rows = await _repo.WhereEqualAsync(InviteCollection, "invited_email", email, limit: 50);
        return rows
            .OrderByDescending(x => TryDate(x.GetValueOrDefault("updated_at")) ?? TryDate(x.GetValueOrDefault("created_at")) ?? DateTime.MinValue)
            .ToList();
    }

    private static Dictionary<string, object?> ToClientInvite(Dictionary<string, object?> invite) => new()
    {
        ["id"] = Text(invite, "id"),
        ["invited_email"] = Text(invite, "invited_email"),
        ["invite_code"] = CleanInviteCode(Text(invite, "invite_code")),
        ["status"] = IsAcceptedInvite(invite) ? "Đã đăng ký" : "Đã mời",
        ["discount_percent"] = IsAcceptedInvite(invite) ? DiscountPerAcceptedInvite : 0,
        ["created_at"] = invite.GetValueOrDefault("created_at"),
        ["expires_at"] = invite.GetValueOrDefault("expires_at"),
        ["accepted_at"] = invite.GetValueOrDefault("accepted_at")
    };

    private string BuildInviteLink(HttpRequest request, string inviteCode, string email)
    {
        var code = Uri.EscapeDataString(CleanInviteCode(inviteCode));
        var query = $"/signup?offerInvite={code}&inviteCode={code}&email={Uri.EscapeDataString(email)}";

        var configuredBaseUrl = FirstNonEmpty(
            _configuration["App:PublicUrl"],
            _configuration["APP_PUBLIC_URL"],
            _configuration["RENDER_EXTERNAL_URL"],
            _configuration["OpenRouter:SiteUrl"]);

        if (!string.IsNullOrWhiteSpace(configuredBaseUrl)
            && Uri.TryCreate(configuredBaseUrl.TrimEnd('/'), UriKind.Absolute, out var configuredUri))
        {
            return configuredUri.ToString().TrimEnd('/') + query;
        }

        var scheme = request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto) && !string.IsNullOrWhiteSpace(forwardedProto)
            ? forwardedProto.ToString().Split(',')[0].Trim()
            : request.Scheme;
        var host = request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHost) && !string.IsNullOrWhiteSpace(forwardedHost)
            ? forwardedHost.ToString().Split(',')[0].Trim()
            : request.Host.Value;
        var pathBase = request.PathBase.Value?.TrimEnd('/') ?? string.Empty;

        return $"{scheme}://{host}{pathBase}{query}";
    }

    private Task<string?> TrySendInviteEmailAsync(string toEmail, string? inviterName, string inviteLink, string inviteCode)
    {
        var cleanName = string.IsNullOrWhiteSpace(inviterName) ? "Một người bạn" : inviterName.Trim();
        var body = $"""
            Xin chào,

            {cleanName} đã mời bạn tham gia TravelwAI.

            Mã mời của bạn: {inviteCode}

            Bấm link bên dưới để đăng ký tài khoản:
            {inviteLink}

            Mã mời có hiệu lực trong {InviteExpirationMinutes} phút. Gmail đã có tài khoản sẽ không nhận ưu đãi.

            TravelwAI
            """;

        return ResendEmailSender.SendPlainEmailAsync(
            _emailOptions,
            toEmail,
            "Bạn đã nhận được lời mời tham gia TravelwAI",
            body);
    }

    private static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private async Task<string> CreateUniqueInviteCodeAsync()
    {
        for (var i = 0; i < 8; i++)
        {
            var code = CreateInviteCode();
            var rows = await _repo.WhereEqualAsync(InviteCollection, "invite_code", code, limit: 1);
            if (rows.Count == 0) return code;
        }

        return CreateInviteCode();
    }

    private static string CreateInviteCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        var result = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            result[i] = chars[bytes[i] % chars.Length];
        }

        return "TWAI-" + new string(result);
    }

    private static string BuildInviteDocumentId(string inviterId, string email)
    {
        var raw = $"{inviterId.Trim().ToLowerInvariant()}|{NormalizeEmail(email)}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return "invite_" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string CleanInviteCode(string? code) => (code ?? string.Empty)
        .Trim()
        .Replace(" ", string.Empty)
        .ToUpperInvariant();

    private static bool IsExpiredPendingInvite(Dictionary<string, object?> invite)
    {
        if (IsAcceptedInvite(invite)) return false;

        var expiresAt = TryDate(invite.GetValueOrDefault("expires_at"));
        if (expiresAt.HasValue) return expiresAt.Value <= DateTime.UtcNow;

        var createdAt = TryDate(invite.GetValueOrDefault("created_at"))
            ?? TryDate(invite.GetValueOrDefault("updated_at"));
        return createdAt.HasValue && createdAt.Value.AddMinutes(InviteExpirationMinutes) <= DateTime.UtcNow;
    }

    private static bool IsAcceptedInvite(Dictionary<string, object?> invite)
    {
        return string.Equals(Text(invite, "status"), "Đã đăng ký", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Text(invite, "accepted_at"))
            || !string.IsNullOrWhiteSpace(Text(invite, "invited_user_id"));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return string.Empty;
    }

    private static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();
    private static string Text(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    private static int? TryInt(object? value) => int.TryParse(value?.ToString(), out var i) ? i : null;
    private static bool IsTruthy(object? value) => value is bool b ? b : bool.TryParse(value?.ToString(), out var parsed) && parsed;
    private static DateTime? TryDate(object? raw)
    {
        if (raw is null) return null;
        if (raw is DateTime dt) return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
        return DateTimeOffset.TryParse(raw.ToString(), out var dto) ? dto.UtcDateTime : null;
    }
}

public sealed record BookingDiscountResult(
    int DiscountPercent,
    int InviteDiscountPercent,
    int PostOfferDiscountPercent,
    string Source,
    string PostOfferId);