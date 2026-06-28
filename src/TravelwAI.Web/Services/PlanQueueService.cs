using Npgsql;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

public sealed class PlanQueueService
{
    private const string PlanOrdersCollection = "plan_orders";
    private readonly IDataRepository _repo;
    private readonly NpgsqlDataSource _dataSource;

    public PlanQueueService(IDataRepository repo, NpgsqlDataSource dataSource)
    {
        _repo = repo;
        _dataSource = dataSource;
    }

    public async Task<PlanQueueState> SyncUserAsync(string userId, string? currentAuthRole = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) return PlanQueueState.Free;
        currentAuthRole = NormalizePlanRole(currentAuthRole) is { Length: > 0 } normalized ? normalized : await LoadAuthRoleAsync(userId);
        if (string.Equals(currentAuthRole, "Admin", StringComparison.OrdinalIgnoreCase)) return new PlanQueueState("Admin", null, null, null, null, null, 0);

        var now = DateTime.UtcNow;
        var userDoc = await _repo.GetByIdAsync("users", userId) ?? new Dictionary<string, object?>();
        var docRole = NormalizePlanRole(FirstText(userDoc, "plan_role", "planRole", "role"));
        var orders = await _repo.WhereEqualAsync(PlanOrdersCollection, "buyer_id", userId, limit: 500);
        var sold = BuildSoldPlanItems(orders)
            .Where(item => item.ExpiresAt > now)
            .OrderBy(item => item.StartedAt == DateTime.MinValue ? DateTime.MinValue : item.StartedAt)
            .ThenBy(item => item.CreatedAt)
            .ToList();

        var hasManagedPlan = sold.Count > 0 || currentAuthRole is "VIP" or "Premium" || docRole is "VIP" or "Premium";
        if (!hasManagedPlan)
        {
            return new PlanQueueState(string.IsNullOrWhiteSpace(currentAuthRole) ? "Free" : currentAuthRole, null, null, null, null, null, 0);
        }

        var active = sold
            .Where(item => item.StartedAt <= now && item.ExpiresAt > now)
            .OrderByDescending(item => item.StartedAt)
            .FirstOrDefault();
        var next = sold
            .Where(item => item.StartedAt > now && item.ExpiresAt > now)
            .OrderBy(item => item.StartedAt)
            .FirstOrDefault();

        var targetRole = active is null ? "Free" : active.Role;
        if (targetRole == "Free" && currentAuthRole is "Sales" or "Business")
        {
            targetRole = currentAuthRole;
        }

        await UpdateAuthRoleIfNeededAsync(userId, currentAuthRole, targetRole);
        await SaveUserPlanStateAsync(userId, targetRole, active, next, now);
        return ToState(targetRole, active, next, now);
    }

    public async Task SyncAccountsAsync(List<Dictionary<string, object?>> accounts)
    {
        foreach (var account in accounts)
        {
            var userId = Text(account, "id");
            if (string.IsNullOrWhiteSpace(userId)) continue;
            var state = await SyncUserAsync(userId, Text(account, "role"));
            account["role"] = state.CurrentRole;
            account["plan_role"] = state.CurrentRole;
            account["planRole"] = state.CurrentRole;
            account["plan_started_at"] = state.CurrentStartedAt;
            account["planStartedAt"] = state.CurrentStartedAt;
            account["plan_expires_at"] = state.CurrentExpiresAt;
            account["planExpiresAt"] = state.CurrentExpiresAt;
            account["next_plan_role"] = state.NextRole;
            account["nextPlanRole"] = state.NextRole;
            account["next_plan_started_at"] = state.NextStartedAt;
            account["nextPlanStartedAt"] = state.NextStartedAt;
            account["next_plan_expires_at"] = state.NextExpiresAt;
            account["nextPlanExpiresAt"] = state.NextExpiresAt;
            account["plan_countdown_seconds"] = state.CountdownSeconds;
            account["planCountdownSeconds"] = state.CountdownSeconds;
        }
    }

    public async Task<DateTime> GetNextPlanStartAsync(string userId, string? excludeOrderId = null)
    {
        await SyncUserAsync(userId);
        var now = DateTime.UtcNow;
        var orders = await _repo.WhereEqualAsync(PlanOrdersCollection, "buyer_id", userId, limit: 500);
        var lastEnd = BuildSoldPlanItems(orders)
            .Where(item => !string.Equals(item.Id, excludeOrderId, StringComparison.Ordinal) && item.ExpiresAt > now)
            .Select(item => item.ExpiresAt)
            .DefaultIfEmpty(now)
            .Max();
        return lastEnd > now ? lastEnd : now;
    }

    public async Task SyncUsersFromOrdersAsync(IEnumerable<Dictionary<string, object?>> orders)
    {
        var buyerIds = orders
            .Select(order => FirstText(order, "buyer_id", "buyerId", "user_id", "userId"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var buyerId in buyerIds)
        {
            await SyncUserAsync(buyerId);
        }
    }

    private async Task SaveUserPlanStateAsync(string userId, string targetRole, PlanQueueItem? active, PlanQueueItem? next, DateTime now)
    {
        await _repo.SetAsync("users", userId, new Dictionary<string, object?>
        {
            ["id"] = userId,
            ["uid"] = userId,
            ["role"] = targetRole,
            ["plan_role"] = targetRole,
            ["planRole"] = targetRole,
            ["plan_started_at"] = active?.StartedAt,
            ["planStartedAt"] = active?.StartedAt,
            ["plan_expires_at"] = active?.ExpiresAt,
            ["planExpiresAt"] = active?.ExpiresAt,
            ["plan_last_order_id"] = active?.Id ?? string.Empty,
            ["planLastOrderId"] = active?.Id ?? string.Empty,
            ["next_plan_role"] = next?.Role ?? string.Empty,
            ["nextPlanRole"] = next?.Role ?? string.Empty,
            ["next_plan_started_at"] = next?.StartedAt,
            ["nextPlanStartedAt"] = next?.StartedAt,
            ["next_plan_expires_at"] = next?.ExpiresAt,
            ["nextPlanExpiresAt"] = next?.ExpiresAt,
            ["next_plan_order_id"] = next?.Id ?? string.Empty,
            ["nextPlanOrderId"] = next?.Id ?? string.Empty,
            ["plan_countdown_seconds"] = active is null ? 0 : Math.Max(0, (long)Math.Ceiling((active.ExpiresAt - now).TotalSeconds)),
            ["planCountdownSeconds"] = active is null ? 0 : Math.Max(0, (long)Math.Ceiling((active.ExpiresAt - now).TotalSeconds)),
            ["updated_at"] = now
        }, merge: true);
    }

    private async Task UpdateAuthRoleIfNeededAsync(string userId, string currentAuthRole, string targetRole)
    {
        if (string.Equals(currentAuthRole, targetRole, StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(currentAuthRole, "Admin", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(targetRole)) targetRole = "Free";
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "update app_users_auth set role = @role, updated_at = now() where id = @id;";
        cmd.Parameters.AddWithValue("role", targetRole);
        cmd.Parameters.AddWithValue("id", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> LoadAuthRoleAsync(string userId)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select role from app_users_auth where id = @id limit 1;";
        cmd.Parameters.AddWithValue("id", userId);
        var value = await cmd.ExecuteScalarAsync();
        return NormalizePlanRole(value?.ToString()) is { Length: > 0 } role ? role : "Free";
    }

    private static List<PlanQueueItem> BuildSoldPlanItems(IEnumerable<Dictionary<string, object?>> orders)
    {
        return orders
            .Where(order => string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
            .Select(order =>
            {
                var role = NormalizePlanRole(FirstText(order, "plan_role", "planRole", "role"));
                var expiresAt = ParseDate(FirstText(order, "plan_expires_at", "planExpiresAt"));
                var startedAt = ParseDate(FirstText(order, "plan_started_at", "planStartedAt"));
                var createdAt = ParseDate(FirstText(order, "sold_at", "soldAt", "created_at", "createdAt"));
                var months = Int(order, "duration_months", Int(order, "durationMonths", 1));
                if (startedAt == DateTime.MinValue && expiresAt != DateTime.MinValue) startedAt = expiresAt.AddMonths(-Math.Clamp(months <= 0 ? 1 : months, 1, 12));
                if (createdAt == DateTime.MinValue) createdAt = startedAt;
                return new PlanQueueItem(FirstText(order, "id", "Id"), role, startedAt, expiresAt, createdAt);
            })
            .Where(item => item.Role is "VIP" or "Premium" && item.ExpiresAt != DateTime.MinValue)
            .ToList();
    }

    private static PlanQueueState ToState(string targetRole, PlanQueueItem? active, PlanQueueItem? next, DateTime now) => new(
        string.IsNullOrWhiteSpace(targetRole) ? "Free" : targetRole,
        active?.StartedAt,
        active?.ExpiresAt,
        next?.Role,
        next?.StartedAt,
        next?.ExpiresAt,
        active is null ? 0 : Math.Max(0, (long)Math.Ceiling((active.ExpiresAt - now).TotalSeconds)));

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
            "admin" => "Admin",
            "free" or "user" => "Free",
            _ => string.Empty
        };
    }

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
    private static DateTime ParseDate(object? value) => DateTime.TryParse(value?.ToString(), out var date) ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : DateTime.MinValue;

    private sealed record PlanQueueItem(string Id, string Role, DateTime StartedAt, DateTime ExpiresAt, DateTime CreatedAt);
}

public sealed record PlanQueueState(
    string CurrentRole,
    DateTime? CurrentStartedAt,
    DateTime? CurrentExpiresAt,
    string? NextRole,
    DateTime? NextStartedAt,
    DateTime? NextExpiresAt,
    long CountdownSeconds)
{
    public static PlanQueueState Free { get; } = new("Free", null, null, null, null, null, 0);
}
