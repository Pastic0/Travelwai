using TravelwAI.Web.Services;

namespace TravelwAI.Web.Services;

public sealed class TourOrderExpirationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TourOrderExpirationHostedService> _logger;

    public TourOrderExpirationHostedService(IServiceScopeFactory scopeFactory, ILogger<TourOrderExpirationHostedService> logger)
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
                var automation = scope.ServiceProvider.GetRequiredService<TourOrderAutomation>();
                await automation.ExpirePendingOrdersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể tự động hủy đơn tour quá hạn.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}
