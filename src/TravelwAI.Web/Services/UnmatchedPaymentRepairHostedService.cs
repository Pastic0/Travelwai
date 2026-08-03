using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

/// <summary>
/// Reprocesses SePay webhooks that were previously acknowledged but could not be
/// matched to an order. This is intentionally a one-time startup repair so a
/// deployment containing improved payment-code parsing can recover old payments
/// without asking the customer to transfer money again.
/// </summary>
public sealed class UnmatchedPaymentRepairHostedService : BackgroundService
{
    private const string TransactionsCollection = "payment_transactions";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UnmatchedPaymentRepairHostedService> _logger;

    public UnmatchedPaymentRepairHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<UnmatchedPaymentRepairHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);

            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDataRepository>();
            var payments = scope.ServiceProvider.GetRequiredService<AutomaticPaymentService>();
            var transactions = (await repo.GetAllAsync(TransactionsCollection, limit: 1000))
                .Where(transaction =>
                {
                    var status = FirstText(transaction, "processing_status", "processingStatus");
                    var note = FirstText(transaction, "note");
                    return string.Equals(status, "unmatched", StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(status, "ignored", StringComparison.OrdinalIgnoreCase)
                            && note.Contains("tài khoản", StringComparison.OrdinalIgnoreCase));
                })
                .ToList();

            foreach (var transaction in transactions)
            {
                stoppingToken.ThrowIfCancellationRequested();
                var rawPayload = FirstText(transaction, "raw_payload", "rawPayload");
                if (string.IsNullOrWhiteSpace(rawPayload)) continue;

                SePayWebhookPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<SePayWebhookPayload>(rawPayload, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Không thể đọc lại webhook SePay cũ từ transaction {TransactionId}.", FirstText(transaction, "id", "Id"));
                    continue;
                }

                if (payload is null || payload.Id <= 0) continue;

                try
                {
                    var result = await payments.ProcessSePayAsync(payload, stoppingToken);
                    if (result.Success)
                    {
                        _logger.LogInformation(
                            "Đã xử lý lại webhook SePay {TransactionId} và kích hoạt đơn {OrderId}.",
                            payload.Id,
                            result.OrderId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Xử lý lại webhook SePay {TransactionId} chưa thành công.", payload.Id);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Application is stopping.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không thể quét lại các webhook SePay chưa khớp đơn.");
        }
    }

    private static string FirstText(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetValue(key, out var value)) continue;
            var text = value?.ToString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return string.Empty;
    }
}
