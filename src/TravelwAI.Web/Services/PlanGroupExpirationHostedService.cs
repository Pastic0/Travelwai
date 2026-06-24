using System.Globalization;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

public sealed class PlanGroupExpirationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PlanGroupExpirationHostedService> _logger;

    public PlanGroupExpirationHostedService(IServiceScopeFactory scopeFactory, ILogger<PlanGroupExpirationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExpirePlansAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể kiểm tra kế hoạch hết hạn.");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task ExpirePlansAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDataRepository>();
        var plans = await repo.GetAllAsync("plans", limit: 250);

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = plan.GetValueOrDefault("status")?.ToString();
            if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsExpired(plan)) continue;

            var conversationId = plan.GetValueOrDefault("conversation_id")?.ToString();
            if (!string.IsNullOrWhiteSpace(conversationId))
            {
                await repo.DeleteWhereEqualAsync("messages", "conversation_id", conversationId);
                await repo.DeleteAsync("conversations", conversationId);
            }

            var planId = plan.GetValueOrDefault("id")?.ToString();
            if (string.IsNullOrWhiteSpace(planId)) continue;

            await repo.UpdateAsync("plans", planId, new Dictionary<string, object?>
            {
                ["status"] = "expired",
                ["conversation_id"] = string.Empty,
                ["expired_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });
        }
    }

    private static bool IsExpired(Dictionary<string, object?> plan)
    {
        var disbandText = plan.GetValueOrDefault("group_disbands_at")?.ToString();
        if (DateTimeOffset.TryParse(disbandText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var disbandAt))
        {
            return DateTimeOffset.UtcNow >= disbandAt.ToUniversalTime();
        }

        var endDateText = plan.GetValueOrDefault("end_date")?.ToString();
        if (DateOnly.TryParseExact(endDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate))
        {
            return DateOnly.FromDateTime(DateTime.UtcNow) > endDate.AddDays(1);
        }

        return false;
    }
}
