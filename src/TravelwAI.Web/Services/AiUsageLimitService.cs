using Npgsql;

namespace TravelwAI.Web.Services;

public sealed class AiUsageLimitService
{
    public const string ChatFeature = "chatbot_chat";
    public const string PostFeature = "post_content";

    private readonly NpgsqlDataSource _dataSource;

    public AiUsageLimitService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<AiUsageLimitResult> TryConsumeAsync(
        string userId,
        string feature,
        int limit,
        int windowMinutes = RoleFeaturePolicyService.UsageWindowMinutes,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return new AiUsageLimitResult(true, 0, int.MaxValue, null, 0, null);
        }

        var cleanUserId = (userId ?? string.Empty).Trim();
        var cleanFeature = (feature ?? string.Empty).Trim();
        if (cleanUserId.Length == 0 || cleanFeature.Length == 0)
        {
            return new AiUsageLimitResult(false, limit, 0, DateTimeOffset.UtcNow.AddMinutes(windowMinutes), windowMinutes * 60, null);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "select pg_advisory_xact_lock(hashtext(@key));";
            lockCommand.Parameters.AddWithValue("key", $"ai-usage:{cleanUserId}:{cleanFeature}");
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cleanupCommand = connection.CreateCommand())
        {
            cleanupCommand.Transaction = transaction;
            cleanupCommand.CommandText = "delete from app_ai_usage_events where user_id = @user_id and feature = @feature and created_at < now() - interval '24 hours';";
            cleanupCommand.Parameters.AddWithValue("user_id", cleanUserId);
            cleanupCommand.Parameters.AddWithValue("feature", cleanFeature);
            await cleanupCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var threshold = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(1, windowMinutes));
        int used;
        DateTimeOffset? firstUsedAt = null;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = """
                select count(*)::int, min(created_at)
                from app_ai_usage_events
                where user_id = @user_id
                  and feature = @feature
                  and created_at > @threshold;
                """;
            countCommand.Parameters.AddWithValue("user_id", cleanUserId);
            countCommand.Parameters.AddWithValue("feature", cleanFeature);
            countCommand.Parameters.AddWithValue("threshold", threshold.UtcDateTime);
            await using var reader = await countCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            used = reader.GetInt32(0);
            if (!reader.IsDBNull(1))
            {
                var first = reader.GetDateTime(1);
                firstUsedAt = new DateTimeOffset(DateTime.SpecifyKind(first, DateTimeKind.Utc));
            }
        }

        if (used >= limit)
        {
            await transaction.CommitAsync(cancellationToken);
            var resetAt = (firstUsedAt ?? DateTimeOffset.UtcNow).AddMinutes(windowMinutes);
            var retry = Math.Max(1, (int)Math.Ceiling((resetAt - DateTimeOffset.UtcNow).TotalSeconds));
            return new AiUsageLimitResult(false, limit, 0, resetAt, retry, null);
        }

        long usageEventId;
        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                insert into app_ai_usage_events(user_id, feature, created_at)
                values (@user_id, @feature, now())
                returning id;
                """;
            insertCommand.Parameters.AddWithValue("user_id", cleanUserId);
            insertCommand.Parameters.AddWithValue("feature", cleanFeature);
            usageEventId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        var remaining = Math.Max(0, limit - used - 1);
        return new AiUsageLimitResult(true, limit, remaining, null, 0, usageEventId);
    }

    public async Task ReleaseAsync(
        long? usageEventId,
        string userId,
        string feature,
        CancellationToken cancellationToken = default)
    {
        if (!usageEventId.HasValue) return;

        var cleanUserId = (userId ?? string.Empty).Trim();
        var cleanFeature = (feature ?? string.Empty).Trim();
        if (cleanUserId.Length == 0 || cleanFeature.Length == 0) return;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            delete from app_ai_usage_events
            where id = @id
              and user_id = @user_id
              and feature = @feature;
            """;
        command.Parameters.AddWithValue("id", usageEventId.Value);
        command.Parameters.AddWithValue("user_id", cleanUserId);
        command.Parameters.AddWithValue("feature", cleanFeature);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record AiUsageLimitResult(
    bool Allowed,
    int Limit,
    int Remaining,
    DateTimeOffset? ResetAt,
    int RetryAfterSeconds,
    long? UsageEventId);
