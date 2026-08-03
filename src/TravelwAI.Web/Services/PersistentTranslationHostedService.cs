using Microsoft.Extensions.Options;
using TravelwAI.Web.Options;

namespace TravelwAI.Web.Services;

public sealed class PersistentTranslationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PersistentTranslationOptions _options;
    private readonly PersistentTranslationActivityGate _activityGate;
    private readonly ILogger<PersistentTranslationHostedService> _logger;

    public PersistentTranslationHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<PersistentTranslationOptions> options,
        PersistentTranslationActivityGate activityGate,
        ILogger<PersistentTranslationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _activityGate = activityGate;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Đồng bộ bản dịch vĩnh viễn đang tắt.");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        var idleDelay = TimeSpan.FromSeconds(Math.Clamp(_options.PollSeconds, 1, 60));
        var batchSize = Math.Clamp(_options.BatchSize, 1, 20);

        while (!stoppingToken.IsCancellationRequested)
        {
            await _activityGate.WaitForEnglishClientAsync(stoppingToken);

            var processed = false;
            try
            {
                for (var index = 0; index < batchSize && !stoppingToken.IsCancellationRequested; index += 1)
                {
                    if (!_activityGate.HasActiveEnglishClient()) break;

                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<PersistentDocumentTranslationService>();
                    var handled = await service.ProcessNextAsync(stoppingToken);
                    if (!handled) break;
                    processed = true;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker đồng bộ bản dịch vĩnh viễn gặp lỗi ngoài dự kiến.");
            }

            if (!processed)
            {
                await Task.Delay(idleDelay, stoppingToken);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
        }
    }
}
