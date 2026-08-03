using System.Globalization;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

public sealed class PaymentOrderExpirationHostedService : BackgroundService
{
    private const int PaymentExpireMinutes = 5;
    private const string OrdersCollection = "plan_orders";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentOrderExpirationHostedService> _logger;

    public PaymentOrderExpirationHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<PaymentOrderExpirationHostedService> logger)
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
                var repository = scope.ServiceProvider.GetRequiredService<IDataRepository>();
                var orders = await repository.WhereEqualAsync(
                    OrdersCollection,
                    "status",
                    "Khách đặt",
                    limit: 1000);

                var now = DateTimeOffset.UtcNow;
                foreach (var order in orders)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    var orderId = FirstText(order, "id", "Id");
                    var expiresAt = GetEffectiveExpiry(order);
                    if (string.IsNullOrWhiteSpace(orderId)
                        || expiresAt == DateTimeOffset.MinValue
                        || expiresAt > now)
                    {
                        continue;
                    }

                    var currentOrder = await repository.GetByIdAsync(OrdersCollection, orderId);
                    if (currentOrder is null
                        || !string.Equals(FirstText(currentOrder, "status"), "Khách đặt", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var currentExpiresAt = GetEffectiveExpiry(currentOrder);
                    if (currentExpiresAt == DateTimeOffset.MinValue || currentExpiresAt > DateTimeOffset.UtcNow)
                    {
                        continue;
                    }

                    await repository.UpdateAsync(OrdersCollection, orderId, new Dictionary<string, object?>
                    {
                        ["status"] = "Hết hạn",
                        ["payment_status"] = "Hết hạn",
                        ["paymentStatus"] = "Hết hạn",
                        ["expired_at"] = DateTime.UtcNow,
                        ["expiredAt"] = DateTime.UtcNow,
                        ["updated_at"] = DateTime.UtcNow
                    });
                }

                // Keep expired payment records for a grace period so a delayed bank
                // webhook can still find the unique TWAI code and activate the order.
                // They are hidden from Manage immediately and removed after 24 hours.
                var expiredOrders = await repository.WhereEqualAsync(
                    OrdersCollection,
                    "status",
                    "Hết hạn",
                    limit: 1000);
                foreach (var order in expiredOrders)
                {
                    var orderId = FirstText(order, "id", "Id");
                    var expiresAt = GetEffectiveExpiry(order);
                    if (!string.IsNullOrWhiteSpace(orderId)
                        && expiresAt != DateTimeOffset.MinValue
                        && expiresAt.AddHours(24) <= DateTimeOffset.UtcNow)
                    {
                        await repository.DeleteAsync(OrdersCollection, orderId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể xoá đơn thanh toán QR đã hết hạn.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
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

    private static DateTimeOffset GetEffectiveExpiry(Dictionary<string, object?> order)
    {
        // Never delete before the exact deadline displayed to the buyer.
        var configuredExpiry = ParseDate(FirstText(order, "expires_at", "expiresAt"));
        if (configuredExpiry != DateTimeOffset.MinValue) return configuredExpiry;

        var createdAt = ParseDate(FirstText(order, "created_at", "createdAt"));
        return createdAt == DateTimeOffset.MinValue
            ? DateTimeOffset.MinValue
            : createdAt.AddMinutes(PaymentExpireMinutes);
    }

    private static DateTimeOffset ParseDate(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }
}
