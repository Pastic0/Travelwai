using Npgsql;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

public sealed class AccountPresenceService
{
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(3);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataRepository _repository;

    public AccountPresenceService(NpgsqlDataSource dataSource, IDataRepository repository)
    {
        _dataSource = dataSource;
        _repository = repository;
    }

    public Task MarkOnlineAsync(string userId, bool updateLoginTime = false)
        => UpdateAsync(userId, isOnline: true, updateLoginTime, revokeRefreshToken: false);

    public Task TouchAsync(string userId)
        => UpdateAsync(userId, isOnline: true, updateLoginTime: false, revokeRefreshToken: false);

    public Task MarkOfflineAsync(string userId, bool revokeRefreshToken = false)
        => UpdateAsync(userId, isOnline: false, updateLoginTime: false, revokeRefreshToken);

    private async Task UpdateAsync(string userId, bool isOnline, bool updateLoginTime, bool revokeRefreshToken)
    {
        userId = (userId ?? string.Empty).Trim();
        if (userId.Length == 0) return;

        var now = DateTime.UtcNow;
        await using (var conn = await _dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                update app_users_auth
                set is_online = @is_online,
                    last_seen_at = now(),
                    last_login_at = case when @update_login_time then now() else last_login_at end,
                    last_logout_at = case when @is_online then last_logout_at else now() end,
                    refresh_token_hash = case when @revoke_refresh_token then null else refresh_token_hash end,
                    refresh_token_expires_at = case when @revoke_refresh_token then null else refresh_token_expires_at end,
                    updated_at = now()
                where id = @user_id;

                update app_documents
                set data = data || jsonb_build_object(
                        'is_online', true,
                        'isOnline', true,
                        'presence_status', 'online',
                        'last_seen_at', now(),
                        'lastSeenAt', now()
                    ),
                    updated_at = now()
                where @is_heartbeat = true
                  and collection = 'users'
                  and id = @user_id;
                """;
            cmd.Parameters.AddWithValue("user_id", userId);
            cmd.Parameters.AddWithValue("is_online", isOnline);
            cmd.Parameters.AddWithValue("update_login_time", updateLoginTime);
            cmd.Parameters.AddWithValue("revoke_refresh_token", revokeRefreshToken);
            cmd.Parameters.AddWithValue("is_heartbeat", isOnline && !updateLoginTime);
            await cmd.ExecuteNonQueryAsync();
        }

        var userPresence = new Dictionary<string, object?>
        {
            ["is_online"] = isOnline,
            ["isOnline"] = isOnline,
            ["presence_status"] = isOnline ? "online" : "offline",
            ["last_seen_at"] = now,
            ["lastSeenAt"] = now
        };
        if (updateLoginTime)
        {
            userPresence["last_login_at"] = now;
            userPresence["lastLoginAt"] = now;
        }
        if (!isOnline)
        {
            userPresence["last_logout_at"] = now;
            userPresence["lastLogoutAt"] = now;
        }

        if (updateLoginTime || !isOnline)
        {
            // Login/logout phải hiển thị ngay nên đi qua repository để xóa cache users.
            await _repository.SetAsync("users", userId, userPresence, merge: true);
        }
    }
}
