using Microsoft.AspNetCore.Mvc;
using Npgsql;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/manage")]
public sealed class ManageApiController : ApiControllerBase
{
    private readonly IDataRepository _repo;
    private readonly NpgsqlDataSource _dataSource;
    private readonly PlanQueueService _planQueueService;

    public ManageApiController(IAuthService authService, IDataRepository repo, NpgsqlDataSource dataSource, PlanQueueService planQueueService) : base(authService)
    {
        _repo = repo;
        _dataSource = dataSource;
        _planQueueService = planQueueService;
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

        var startText = queueStart <= now.AddSeconds(5) ? "bắt đầu ngay" : $"bắt đầu sau gói hiện tại: {queueStart:dd/MM/yyyy HH:mm}";
        return Ok(new { success = true, message = $"Đã bán gói {role} {months} tháng, {startText}. Hạn gói: {newExpiresAt:dd/MM/yyyy HH:mm}." });
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
        if (role is not ("Sales" or "Business")) return BadRequest(new { success = false, message = "Gói đăng ký không hợp lệ." });
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
        return Ok(new { success = true, message = $"Đã duyệt và cập nhật tài khoản thành {role}." });
    }

    [HttpDelete("business-applications/{id}")]
    public async Task<IActionResult> DeleteBusinessApplication(string id)
    {
        var admin = await RequireAdminAsync();
        if (!admin.ok) return admin.error!;
        var ok = await _repo.DeleteAsync("business_applications", id);
        return ok ? Ok(new { success = true, message = "Đã xoá biểu mẫu." }) : NotFound(new { success = false, message = "Không tìm thấy biểu mẫu." });
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
                && !string.Equals(status, "Đã bán", StringComparison.OrdinalIgnoreCase)
                && expiresAt != DateTime.MinValue
                && expiresAt <= now)
            {
                await _repo.DeleteAsync("plan_orders", id);
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
            "business" or "company" => "Business",
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
    private static DateTime ParseDate(object? value) => DateTime.TryParse(value?.ToString(), out var date) ? date : DateTime.MinValue;
}
