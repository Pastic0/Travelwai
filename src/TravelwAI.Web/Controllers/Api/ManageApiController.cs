using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/manage")]
public sealed class ManageApiController : ApiControllerBase
{
    private readonly IDataRepository _repo;
    private readonly NpgsqlDataSource _dataSource;
    private readonly PlanQueueService _planQueueService;
    private readonly IFileStorageService _fileStorage;
    private readonly ChatbotSettingsService _chatbotSettings;

    public ManageApiController(IAuthService authService, IDataRepository repo, NpgsqlDataSource dataSource, PlanQueueService planQueueService, IFileStorageService fileStorage, ChatbotSettingsService chatbotSettings) : base(authService)
    {
        _repo = repo;
        _dataSource = dataSource;
        _planQueueService = planQueueService;
        _fileStorage = fileStorage;
        _chatbotSettings = chatbotSettings;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;
        var accounts = await LoadAccountsAsync();
        var orders = await _repo.GetAllAsync("plan_orders", limit: 500);
        await DeleteExpiredPendingPlanOrdersAsync(orders);
        orders = await _repo.GetAllAsync("plan_orders", limit: 500);
        orders = orders
            .Where(order => !string.Equals(Text(order, "status"), "Hết hạn", StringComparison.OrdinalIgnoreCase))
            .ToList();
        await _planQueueService.SyncAccountsAsync(accounts);
        var applications = await _repo.GetAllAsync("business_applications", limit: 500);
        HydratePlanOrderAccounts(orders, accounts);
        HydrateApplicationAccounts(applications, accounts);
        return Ok(new
        {
            success = true,
            data = new
            {
                accounts,
                orders = orders.OrderByDescending(o => ParseDate(o.GetValueOrDefault("created_at"))).ToList(),
                applications = applications.OrderByDescending(o => ParseDate(o.GetValueOrDefault("created_at"))).ToList()
            }
        });
    }

    [HttpPost("site-logo")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UpdateSiteLogo([FromForm] IFormFile? logo)
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;

        if (logo is null || logo.Length == 0)
        {
            return BadRequest(new { success = false, message = "Vui lòng chọn ảnh logo." });
        }
        if (logo.Length > 10 * 1024 * 1024)
        {
            return BadRequest(new { success = false, message = "Logo tối đa 10MB." });
        }

        var extension = Path.GetExtension(logo.FileName).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".webp"))
        {
            return BadRequest(new { success = false, message = "Logo phải là ảnh JPG, PNG, GIF hoặc WEBP." });
        }

        string? logoUrl;
        try
        {
            logoUrl = await _fileStorage.SaveImageToSupabaseAsync(logo, admin.userId!, "site-branding/logos");
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                message = ex.Message
            });
        }
        if (string.IsNullOrWhiteSpace(logoUrl))
        {
            return BadRequest(new { success = false, message = "Không thể lưu logo lên Supabase Storage. Vui lòng kiểm tra định dạng và dung lượng ảnh." });
        }

        var now = DateTime.UtcNow;
        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        await _repo.SetAsync("site_settings", "branding", new Dictionary<string, object?>
        {
            ["logo_url"] = logoUrl,
            ["logoUrl"] = logoUrl,
            ["logo_version"] = version,
            ["logoVersion"] = version,
            ["updated_by"] = admin.userId,
            ["updatedBy"] = admin.userId,
            ["updated_at"] = now,
            ["updatedAt"] = now
        }, merge: true);

        return Ok(new
        {
            success = true,
            message = "Đã cập nhật logo TravelwAI trên toàn bộ trang web.",
            data = new { logoUrl, version }
        });
    }

    [HttpPut("accounts/{id}/plan")]
    public async Task<IActionResult> UpdateAccountPlan(string id, [FromBody] ManageAccountPlanUpdateRequest request)
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;
        if (request is null) return BadRequest(new { success = false, message = "Thiếu dữ liệu cập nhật gói." });

        var currentRole = await LoadAccountRoleAsync(id);
        if (string.IsNullOrWhiteSpace(currentRole)) return NotFound(new { success = false, message = "Không tìm thấy tài khoản." });
        if (string.Equals(currentRole, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Không thể thay đổi gói của tài khoản Admin." });
        }

        var role = NormalizePlanRole(request.Role);
        if (role is not ("Free" or "VIP" or "Premium" or "Sales" or "Company"))
        {
            return BadRequest(new { success = false, message = "Gói tài khoản không hợp lệ." });
        }

        var now = DateTime.UtcNow;
        DateTime? requestedExpiresAt = null;
        if (role != "Free")
        {
            if (request.ExpiresAt is null)
            {
                return BadRequest(new { success = false, message = "Vui lòng chọn hạn gói." });
            }
            requestedExpiresAt = request.ExpiresAt.Value.UtcDateTime;
            if (requestedExpiresAt.Value <= now)
            {
                return BadRequest(new { success = false, message = "Hạn gói phải lớn hơn thời điểm hiện tại." });
            }
        }

        await EndActivePlanOrdersAsync(id, admin.userId!, now);

        if (role == "Free")
        {
            await UpdateUserRoleAsync(id, "Free");
            await _repo.SetAsync("users", id, new Dictionary<string, object?>
            {
                ["id"] = id,
                ["uid"] = id,
                ["role"] = "Free",
                ["plan_role"] = "Free",
                ["planRole"] = "Free",
                ["plan_started_at"] = string.Empty,
                ["planStartedAt"] = string.Empty,
                ["plan_expires_at"] = string.Empty,
                ["planExpiresAt"] = string.Empty,
                ["plan_last_order_id"] = string.Empty,
                ["planLastOrderId"] = string.Empty,
                ["next_plan_role"] = string.Empty,
                ["nextPlanRole"] = string.Empty,
                ["next_plan_started_at"] = string.Empty,
                ["nextPlanStartedAt"] = string.Empty,
                ["next_plan_expires_at"] = string.Empty,
                ["nextPlanExpiresAt"] = string.Empty,
                ["next_plan_order_id"] = string.Empty,
                ["nextPlanOrderId"] = string.Empty,
                ["plan_countdown_seconds"] = 0,
                ["planCountdownSeconds"] = 0,
                ["plan_is_permanent"] = false,
                ["planIsPermanent"] = false,
                ["updated_at"] = now
            }, merge: true);
            return Ok(new { success = true, message = "Đã chuyển tài khoản về gói Free." });
        }

        var expiresAt = requestedExpiresAt!.Value;
        var manualOrderId = $"manage-plan-{id}";
        var durationMonths = Math.Max(1, (int)Math.Ceiling((expiresAt - now).TotalDays / 30.4375d));
        await _repo.SetAsync("plan_orders", manualOrderId, new Dictionary<string, object?>
        {
            ["id"] = manualOrderId,
            ["buyer_id"] = id,
            ["buyerId"] = id,
            ["plan_role"] = role,
            ["planRole"] = role,
            ["status"] = "Đã bán",
            ["duration_months"] = durationMonths,
            ["durationMonths"] = durationMonths,
            ["plan_started_at"] = now,
            ["planStartedAt"] = now,
            ["plan_expires_at"] = expiresAt,
            ["planExpiresAt"] = expiresAt,
            ["price_amount"] = 0,
            ["priceAmount"] = 0,
            ["source"] = "manage",
            ["managed_by"] = admin.userId,
            ["managedBy"] = admin.userId,
            ["sold_by"] = admin.userId,
            ["soldBy"] = admin.userId,
            ["sold_at"] = now,
            ["created_at"] = now,
            ["updated_at"] = now
        }, merge: false);

        await _repo.SetAsync("users", id, new Dictionary<string, object?>
        {
            ["id"] = id,
            ["uid"] = id,
            ["plan_is_permanent"] = false,
            ["planIsPermanent"] = false,
            ["updated_at"] = now
        }, merge: true);
        await _planQueueService.SyncUserAsync(id, currentRole);
        await MarkManualPlanOrderActivatedAsync(manualOrderId, role);
        return Ok(new
        {
            success = true,
            message = "Cập nhật gói thành công."
        });
    }

    [HttpDelete("accounts/{id}/plan-expiry")]
    public async Task<IActionResult> DeleteAccountPlanExpiry(string id)
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;

        var currentRole = await LoadAccountRoleAsync(id);
        if (string.IsNullOrWhiteSpace(currentRole))
        {
            return NotFound(new { success = false, message = "Không tìm thấy tài khoản." });
        }
        if (string.Equals(currentRole, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Không thể xóa hạn gói của tài khoản Admin." });
        }
        if (string.Equals(currentRole, "Free", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Tài khoản Free không có hạn gói để xóa." });
        }

        var userDoc = await _repo.GetByIdAsync("users", id) ?? new Dictionary<string, object?>();
        var expiresAt = FirstText(userDoc, "plan_expires_at", "planExpiresAt", "expires_at", "expiresAt");
        var isPermanent = Truthy(userDoc.GetValueOrDefault("plan_is_permanent"))
            || Truthy(userDoc.GetValueOrDefault("planIsPermanent"));
        if (string.IsNullOrWhiteSpace(expiresAt) && isPermanent)
        {
            return Ok(new { success = true, message = "Gói của tài khoản đã là không giới hạn thời gian." });
        }
        if (string.IsNullOrWhiteSpace(expiresAt))
        {
            return BadRequest(new { success = false, message = "Tài khoản này không có hạn gói để xóa." });
        }

        var now = DateTime.UtcNow;
        await EndActivePlanOrdersAsync(id, admin.userId!, now);
        await _repo.SetAsync("users", id, new Dictionary<string, object?>
        {
            ["id"] = id,
            ["uid"] = id,
            ["role"] = currentRole,
            ["plan_role"] = currentRole,
            ["planRole"] = currentRole,
            ["plan_expires_at"] = string.Empty,
            ["planExpiresAt"] = string.Empty,
            ["plan_last_order_id"] = string.Empty,
            ["planLastOrderId"] = string.Empty,
            ["next_plan_role"] = string.Empty,
            ["nextPlanRole"] = string.Empty,
            ["next_plan_started_at"] = string.Empty,
            ["nextPlanStartedAt"] = string.Empty,
            ["next_plan_expires_at"] = string.Empty,
            ["nextPlanExpiresAt"] = string.Empty,
            ["next_plan_order_id"] = string.Empty,
            ["nextPlanOrderId"] = string.Empty,
            ["plan_countdown_seconds"] = 0,
            ["planCountdownSeconds"] = 0,
            ["plan_is_permanent"] = true,
            ["planIsPermanent"] = true,
            ["plan_expiry_removed_at"] = now,
            ["planExpiryRemovedAt"] = now,
            ["plan_expiry_removed_by"] = admin.userId,
            ["planExpiryRemovedBy"] = admin.userId,
            ["updated_at"] = now
        }, merge: true);

        return Ok(new
        {
            success = true,
            message = $"Đã xóa hạn gói. Gói {currentRole} được giữ nguyên và chuyển thành không giới hạn thời gian."
        });
    }

    private async Task MarkManualPlanOrderActivatedAsync(string orderId, string role)
    {
        var now = DateTime.UtcNow;
        await _repo.UpdateAsync("plan_orders", orderId, new Dictionary<string, object?>
        {
            ["payment_status"] = "Đã thanh toán",
            ["paymentStatus"] = "Đã thanh toán",
            ["benefits_applied"] = true,
            ["benefitsApplied"] = true,
            ["activation_status"] = "activated",
            ["activationStatus"] = "activated",
            ["benefit_type"] = "account_plan",
            ["benefitType"] = "account_plan",
            ["benefit_value"] = role,
            ["benefitValue"] = role,
            ["benefits_applied_at"] = now,
            ["benefitsAppliedAt"] = now,
            ["updated_at"] = now
        });
    }

    [HttpPost("plan-orders/{id}/sell")]
    public async Task<IActionResult> SellPlanOrder(string id)
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;
        var order = await _repo.GetByIdAsync("plan_orders", id);
        if (order is null) return NotFound(new { success = false, message = "Không tìm thấy đơn gói." });
        if (string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Đơn đã được bán." });
        }
        var expiresAt = ParseDate(FirstText(order, "expires_at", "expiresAt"));
        if (expiresAt != DateTime.MinValue && expiresAt <= DateTime.UtcNow)
        {
            await _repo.DeleteAsync("plan_orders", id);
            return BadRequest(new { success = false, message = "Đơn đã hết hạn và đã bị xoá." });
        }

        var buyerId = FirstText(order, "buyer_id", "buyerId");
        if (string.Equals(FirstText(order, "order_type", "orderType"), "chatbot_style", StringComparison.OrdinalIgnoreCase))
        {
            var styleId = FirstText(order, "style_id", "styleId");
            if (string.IsNullOrWhiteSpace(buyerId) || string.IsNullOrWhiteSpace(styleId))
                return BadRequest(new { success = false, message = "Đơn thiếu tài khoản hoặc phong cách." });
            if (!await _chatbotSettings.GrantPurchasedStyleAsync(buyerId, styleId))
                return BadRequest(new { success = false, message = "Phong cách không còn tồn tại." });

            var nowStyle = DateTime.UtcNow;
            await _repo.UpdateAsync("plan_orders", id, new Dictionary<string, object?>
            {
                ["status"] = "Đã bán",
                ["payment_status"] = "Đã thanh toán",
                ["paymentStatus"] = "Đã thanh toán",
                ["benefits_applied"] = true,
                ["benefitsApplied"] = true,
                ["activation_status"] = "activated",
                ["activationStatus"] = "activated",
                ["benefit_type"] = "chatbot_style",
                ["benefitType"] = "chatbot_style",
                ["benefit_value"] = styleId,
                ["benefitValue"] = styleId,
                ["benefits_applied_at"] = nowStyle,
                ["benefitsAppliedAt"] = nowStyle,
                ["sold_by"] = admin.userId,
                ["soldBy"] = admin.userId,
                ["sold_at"] = nowStyle,
                ["updated_at"] = nowStyle
            });
            return Ok(new { success = true, message = "Đã mở khóa phong cách cho tài khoản." });
        }

        var role = NormalizePlanRole(FirstText(order, "plan_role", "planRole", "role"));
        if (string.IsNullOrWhiteSpace(buyerId) || string.IsNullOrWhiteSpace(role))
        {
            return BadRequest(new { success = false, message = "Đơn thiếu tài khoản hoặc gói." });
        }

        var now = DateTime.UtcNow;
        var months = NormalizePlanMonths(Int(order, "duration_months", Int(order, "durationMonths", 1)));
        var queueStart = await _planQueueService.GetNextPlanStartAsync(buyerId, id);
        var newExpiresAt = queueStart.AddMonths(months);

        await _repo.UpdateAsync("plan_orders", id, new Dictionary<string, object?>
        {
            ["status"] = "Đã bán",
            ["duration_months"] = months,
            ["durationMonths"] = months,
            ["plan_started_at"] = queueStart,
            ["planStartedAt"] = queueStart,
            ["plan_expires_at"] = newExpiresAt,
            ["planExpiresAt"] = newExpiresAt,
            ["sold_by"] = admin.userId,
            ["soldBy"] = admin.userId,
            ["sold_at"] = now,
            ["updated_at"] = now
        });
        await _planQueueService.SyncUserAsync(buyerId);
        await _repo.UpdateAsync("plan_orders", id, new Dictionary<string, object?>
        {
            ["payment_status"] = "Đã thanh toán",
            ["paymentStatus"] = "Đã thanh toán",
            ["benefits_applied"] = true,
            ["benefitsApplied"] = true,
            ["activation_status"] = "activated",
            ["activationStatus"] = "activated",
            ["benefit_type"] = "account_plan",
            ["benefitType"] = "account_plan",
            ["benefit_value"] = role,
            ["benefitValue"] = role,
            ["benefits_applied_at"] = DateTime.UtcNow,
            ["benefitsAppliedAt"] = DateTime.UtcNow,
            ["updated_at"] = DateTime.UtcNow
        });

        var startText = queueStart <= now.AddSeconds(5) ? "bắt đầu ngay" : $"bắt đầu sau gói hiện tại: {queueStart:dd/MM/yyyy HH:mm}";
        return Ok(new { success = true, message = "Bán gói thành công." });
    }

    [HttpDelete("plan-orders/{id}")]
    public async Task<IActionResult> DeletePlanOrder(string id)
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;
        var order = await _repo.GetByIdAsync("plan_orders", id);
        var buyerId = order is null ? string.Empty : FirstText(order, "buyer_id", "buyerId");
        var ok = await _repo.DeleteAsync("plan_orders", id);
        if (ok && !string.IsNullOrWhiteSpace(buyerId)) await _planQueueService.SyncUserAsync(buyerId);
        return ok ? Ok(new { success = true, message = "Đã xoá đơn gói." }) : NotFound(new { success = false, message = "Không tìm thấy đơn gói." });
    }

    [HttpPost("business-applications/{id}/approve")]
    public async Task<IActionResult> ApproveBusinessApplication(string id)
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;
        var application = await _repo.GetByIdAsync("business_applications", id);
        if (application is null) return NotFound(new { success = false, message = "Không tìm thấy biểu mẫu." });
        var userId = FirstText(application, "user_id", "userId");
        var role = NormalizePlanRole(FirstText(application, "plan_role", "planRole"));
        if (role is not ("Sales" or "Company")) return BadRequest(new { success = false, message = "Gói đăng ký không hợp lệ." });
        if (!string.IsNullOrWhiteSpace(userId))
        {
            await UpdateUserRoleAsync(userId, role);
            await _repo.SetAsync("users", userId, new Dictionary<string, object?>
            {
                ["id"] = userId,
                ["uid"] = userId,
                ["role"] = role,
                ["updated_at"] = DateTime.UtcNow
            }, merge: true);
        }
        await _repo.UpdateAsync("business_applications", id, new Dictionary<string, object?>
        {
            ["status"] = "Đã duyệt",
            ["approved_by"] = admin.userId,
            ["approvedBy"] = admin.userId,
            ["approved_at"] = DateTime.UtcNow,
            ["updated_at"] = DateTime.UtcNow
        });
        return Ok(new { success = true, message = "Duyệt biểu mẫu thành công." });
    }

    [HttpDelete("business-applications/{id}")]
    public async Task<IActionResult> DeleteBusinessApplication(string id)
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;
        var ok = await _repo.DeleteAsync("business_applications", id);
        return ok ? Ok(new { success = true, message = "Đã xoá biểu mẫu." }) : NotFound(new { success = false, message = "Không tìm thấy biểu mẫu." });
    }

    private async Task EndActivePlanOrdersAsync(string userId, string adminUserId, DateTime now)
    {
        var orders = await _repo.WhereEqualAsync("plan_orders", "buyer_id", userId, limit: 500);
        foreach (var order in orders)
        {
            if (!string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase)) continue;
            var orderId = FirstText(order, "id", "Id");
            if (string.IsNullOrWhiteSpace(orderId)) continue;
            await _repo.UpdateAsync("plan_orders", orderId, new Dictionary<string, object?>
            {
                ["status"] = "Đã thay đổi",
                ["ended_at"] = now,
                ["endedAt"] = now,
                ["ended_by"] = adminUserId,
                ["endedBy"] = adminUserId,
                ["updated_at"] = now
            });
        }
    }

    private async Task<string> LoadAccountRoleAsync(string userId)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select role from app_users_auth where id = @id limit 1;";
        cmd.Parameters.AddWithValue("id", userId);
        var value = await cmd.ExecuteScalarAsync();
        if (value is null) return string.Empty;
        return NormalizePlanRole(value.ToString()) is { Length: > 0 } role ? role : "Free";
    }

    private async Task DeleteExpiredPendingPlanOrdersAsync(IEnumerable<Dictionary<string, object?>> orders)
    {
        var now = DateTime.UtcNow;
        foreach (var order in orders)
        {
            var id = FirstText(order, "id", "Id");
            var status = Text(order, "status");
            var expiresAt = ParseDate(FirstText(order, "expires_at", "expiresAt"));
            if (!string.IsNullOrWhiteSpace(id)
                && string.Equals(status, "Khách đặt", StringComparison.OrdinalIgnoreCase)
                && expiresAt != DateTime.MinValue
                && expiresAt <= now)
            {
                await _repo.UpdateAsync("plan_orders", id, new Dictionary<string, object?>
                {
                    ["status"] = "Hết hạn",
                    ["payment_status"] = "Hết hạn",
                    ["paymentStatus"] = "Hết hạn",
                    ["expired_at"] = now,
                    ["expiredAt"] = now,
                    ["updated_at"] = now
                });
            }
        }
    }

    private async Task<(bool ok, string? userId, IActionResult? error)> RequireAdminAsync()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return (false, null, current.error);
        var role = NormalizeAccountRole(current.authUser?.GetValueOrDefault("role"));
        if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, StatusCode(403, new { success = false, message = "Chỉ Admin mới được vào Manage." }));
        }
        return (true, current.userId, null);
    }

    private async Task<List<Dictionary<string, object?>>> LoadAccountsAsync()
    {
        var result = new List<Dictionary<string, object?>>();
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select id, email, username, role, is_locked, is_protected, created_at, updated_at, last_login_at
            from app_users_auth
            order by created_at desc;
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var role = reader.GetString(3);
            result.Add(new Dictionary<string, object?>
            {
                ["id"] = reader.GetString(0),
                ["email"] = reader.GetString(1),
                ["username"] = reader.GetString(2),
                ["role"] = role,
                ["plan_role"] = role,
                ["planRole"] = role,
                ["is_locked"] = reader.GetBoolean(4),
                ["is_protected"] = reader.GetBoolean(5),
                ["created_at"] = reader.GetDateTime(6),
                ["updated_at"] = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                ["last_login_at"] = reader.IsDBNull(8) ? null : reader.GetDateTime(8)
            });
        }

        var userDocs = await _repo.GetAllAsync("users", limit: 5000);
        var docMap = userDocs
            .Select(doc => new { Id = FirstText(doc, "id", "uid", "user_id", "userId"), Doc = doc })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Doc, StringComparer.Ordinal);
        foreach (var account in result)
        {
            if (!docMap.TryGetValue(Text(account, "id"), out var doc)) continue;
            account["plan_expires_at"] = FirstText(doc, "plan_expires_at", "planExpiresAt", "expires_at", "expiresAt");
            account["planExpiresAt"] = account["plan_expires_at"];
            account["plan_started_at"] = FirstText(doc, "plan_started_at", "planStartedAt");
            account["planStartedAt"] = account["plan_started_at"];
            account["plan_duration_months"] = FirstText(doc, "plan_duration_months", "planDurationMonths");
            account["planDurationMonths"] = account["plan_duration_months"];
            account["next_plan_role"] = FirstText(doc, "next_plan_role", "nextPlanRole");
            account["nextPlanRole"] = account["next_plan_role"];
            account["next_plan_started_at"] = FirstText(doc, "next_plan_started_at", "nextPlanStartedAt");
            account["nextPlanStartedAt"] = account["next_plan_started_at"];
            account["next_plan_expires_at"] = FirstText(doc, "next_plan_expires_at", "nextPlanExpiresAt");
            account["nextPlanExpiresAt"] = account["next_plan_expires_at"];
            account["plan_countdown_seconds"] = FirstText(doc, "plan_countdown_seconds", "planCountdownSeconds");
            account["planCountdownSeconds"] = account["plan_countdown_seconds"];
            account["plan_is_permanent"] = Truthy(doc.GetValueOrDefault("plan_is_permanent")) || Truthy(doc.GetValueOrDefault("planIsPermanent"));
            account["planIsPermanent"] = account["plan_is_permanent"];
        }
        return result;
    }

    private async Task UpdateUserRoleAsync(string userId, string role)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "update app_users_auth set role = @role, updated_at = now() where id = @id;";
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("id", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void HydratePlanOrderAccounts(List<Dictionary<string, object?>> orders, List<Dictionary<string, object?>> accounts)
    {
        var map = accounts.ToDictionary(a => Text(a, "id"), a => a, StringComparer.Ordinal);
        foreach (var order in orders)
        {
            var buyerId = FirstText(order, "buyer_id", "buyerId");
            if (!map.TryGetValue(buyerId, out var account)) continue;
            order["buyer_name"] = FirstText(order, "buyer_name", "buyerName") is { Length: > 0 } name ? name : FirstText(account, "username", "email");
            order["buyer_email"] = FirstText(order, "buyer_email", "buyerEmail") is { Length: > 0 } email ? email : Text(account, "email");
            if (string.IsNullOrWhiteSpace(FirstText(order, "current_role", "currentRole")))
            {
                order["current_role"] = Text(account, "role");
                order["currentRole"] = Text(account, "role");
            }
            order["current_plan_expires_at"] = FirstText(account, "plan_expires_at", "planExpiresAt");
            order["currentPlanExpiresAt"] = order["current_plan_expires_at"];
        }
    }

    private static void HydrateApplicationAccounts(List<Dictionary<string, object?>> applications, List<Dictionary<string, object?>> accounts)
    {
        var map = accounts.ToDictionary(a => Text(a, "id"), a => a, StringComparer.Ordinal);
        foreach (var app in applications)
        {
            var userId = FirstText(app, "user_id", "userId");
            if (!map.TryGetValue(userId, out var account)) continue;
            app["account_name"] = FirstText(account, "username", "email");
            app["account_email"] = Text(account, "email");
            app["current_role"] = Text(account, "role");
        }
    }

    private static string NormalizePlanRole(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text switch
        {
            "vip" => "VIP",
            "premium" => "Premium",
            "sales" or "sale" or "tour sales" or "toursales" => "Sales",
            "business" or "company" => "Company",
            "admin" => "Admin",
            "free" or "user" => "Free",
            _ => string.Empty
        };
    }
    private static int NormalizePlanMonths(int value) => Math.Clamp(value <= 0 ? 1 : value, 1, 12);
    private static int Int(Dictionary<string, object?> row, string key, int fallback = 0) => int.TryParse(Text(row, key), out var value) ? value : fallback;
    private static string Text(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    private static string FirstText(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            var text = Text(row, key);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return string.Empty;
    }
    private static bool Truthy(object? value)
    {
        if (value is bool boolean) return boolean;
        var text = value?.ToString()?.Trim();
        return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase);
    }
    private static DateTime ParseDate(object? value) => DateTime.TryParse(value?.ToString(), out var date) ? date : DateTime.MinValue;
}


public sealed class ManageAccountPlanUpdateRequest
{
    public string? Role { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
