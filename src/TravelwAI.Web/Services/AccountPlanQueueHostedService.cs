using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

public sealed class AccountPlanQueueHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountPlanQueueHostedService> _logger;

    public AccountPlanQueueHostedService(IServiceScopeFactory scopeFactory, ILogger<AccountPlanQueueHostedService> logger)
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
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IDataRepository>();
                var planQueue = scope.ServiceProvider.GetRequiredService<PlanQueueService>();
                var orders = await repo.GetAllAsync("plan_orders", limit: 1000);
                await planQueue.SyncUsersFromOrdersAsync(orders);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể đồng bộ thời hạn gói tài khoản.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
