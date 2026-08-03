using Microsoft.Extensions.Options;
using TravelwAI.Web.Options;

namespace TravelwAI.Web.Services;

public sealed class ExternalDatasetImportHostedService : BackgroundService
{
    private readonly ExternalKnowledgeImportService _importService;
    private readonly ExternalKnowledgeOptions _options;
    private readonly ILogger<ExternalDatasetImportHostedService> _logger;

    public ExternalDatasetImportHostedService(
        ExternalKnowledgeImportService importService,
        IOptions<ExternalKnowledgeOptions> options,
        ILogger<ExternalDatasetImportHostedService> logger)
    {
        _importService = importService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.AutoImportOnStartup) return;
        try
        {
            await _importService.EnsureLoadedAsync(false, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tác vụ nhập dữ liệu AI bên ngoài đã dừng; ứng dụng vẫn tiếp tục chạy");
        }
    }
}
