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
    public const string InviteCollection = "tour_offer_invites";
    public const string PostOfferCollection = "post_tour_offers";
    public const int PostOfferDiscountPercent = 5;

    private readonly IDataRepository _repo;
    private readonly EmailOptions _emailOptions;
    private readonly IConfiguration _configuration;

    public TourOfferService(IDataRepository repo, IOptions<EmailOptions> emailOptions, IConfiguration configuration)
    {
        _repo = repo;
        _emailOptions = emailOptions.Value;
        _configuration = configuration;
    }

    public async Task<Dictionary<string, object?>> GetStatusAsync(string userId, string? userEmail)
    {
        var invites = await GetInvitesByInviterAsync(userId);
        var accepted = invites
            .Where(IsAcceptedInvite)
            .GroupBy(x => Text(x, "invited_email").ToLowerInvariant())
            .Select(g => g.First())
            .Take(MaxInvitesForDiscount)
            .ToList();

        var progress = Math.Min(MaxInvitesForDiscount, accepted.Count);
        var inviteDiscountPercent = progress * DiscountPerAcceptedInvite;
        var postOffer = await GetActivePostOfferAsync(userId);
        var postOfferDiscountPercent = postOffer is null ? 0 : PostOfferDiscountPercent;
        var discountPercent = inviteDiscountPercent + postOfferDiscountPercent;

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["inviter_id"] = userId,
            ["inviter_email"] = userEmail ?? string.Empty,
            ["progress"] = progress,
            ["target"] = MaxInvitesForDiscount,
            ["discount_percent"] = discountPercent,
            ["invite_discount_percent"] = inviteDiscountPercent,
            ["post_offer_discount_percent"] = postOfferDiscountPercent,
            ["post_offer_active"] = postOffer is not null,
            ["invites"] = invites.Select(ToClientInvite).ToList()
        };
    }

    public async Task<int> GetDiscountPercentAsync(string userId)
    {
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
        var postOffer = await GetActivePostOfferAsync(userId);
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
                ? "Bạn đang có ưu đãi 5% cho lần đặt tour tiếp theo."
                : "Tạo 1 bài viết để nhận ưu đãi 5% cho lần đặt tour tiếp theo."
        };
    }

    public async Task<BookingDiscountResult> GetBookingDiscountAsync(string userId)
    {
        var inviteDiscountPercent = await GetDiscountPercentAsync(userId);
        var postOffer = await GetActivePostOfferAsync(userId);
        var postOfferDiscountPercent = postOffer is null ? 0 : PostOfferDiscountPercent;
        var discountPercent = inviteDiscountPercent + postOfferDiscountPercent;
        var source = discountPercent <= 0
            ? string.Empty
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
            return new Dictionary<string, object?> { ["success"] = false, ["message"] = "Không thể tự mời chính tài khoản của mình." };
        }

        var emailInvites = await GetInvitesByInvitedEmailAsync(normalizedInvitedEmail);
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
                ["updated_at"] = now
            });
        }

        var emailError = await TrySendInviteEmailAsync(normalizedInvitedEmail, inviterName, inviteLink, inviteCode);
        var alreadyAccepted = existing is not null && IsAcceptedInvite(existing);
        var message = alreadyAccepted
            ? $"Gmail này đã đăng ký từ mã mời {inviteCode}."
            : string.IsNullOrWhiteSpace(emailError)
                ? $"Đã gửi mã mời {inviteCode} đến Gmail."
                : $"Đã tạo mã mời {inviteCode} nhưng chưa gửi được email. Lỗi: {emailError}";

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

        await _repo.UpdateAsync(InviteCollection, inviteId, new Dictionary<string, object?>
        {
            ["status"] = "Đã đăng ký",
            ["invited_user_id"] = registeredUserId,
            ["discount_percent"] = DiscountPerAcceptedInvite,
            ["accepted_at"] = DateTime.UtcNow,
            ["updated_at"] = DateTime.UtcNow
        });
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

            Khi bạn đăng ký bằng đúng Gmail được mời và đúng mã mời, tiến trình ưu đãi của người mời sẽ được cập nhật.

            TravelwAI
            """;

        return ResendEmailSender.SendPlainEmailAsync(
            _emailOptions,
            toEmail,
            "Lời mời tham gia TravelwAI để nhận ưu đãi tour",
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
