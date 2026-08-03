using Microsoft.Extensions.DependencyInjection;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

/// <summary>
/// Repairs paid orders whose payment status was saved before the purchased benefit
/// (account role or chatbot style) was fully applied. This also covers older orders
/// created before the explicit benefits_applied marker existed.
/// </summary>
public sealed class PaymentBenefitRepairHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentBenefitRepairHostedService> _logger;

    public PaymentBenefitRepairHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<PaymentBenefitRepairHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IDataRepository>();
                var payments = scope.ServiceProvider.GetRequiredService<AutomaticPaymentService>();
                var notifications = scope.ServiceProvider.GetRequiredService<InAppNotificationService>();
                var orders = await repo.GetAllAsync("plan_orders", limit: 500);

                foreach (var order in orders.Where(NeedsRepair))
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    var orderId = FirstText(order, "id", "Id");
                    if (string.IsNullOrWhiteSpace(orderId)) continue;

                    try
                    {
                        var result = await payments.EnsureOrderBenefitsAsync(orderId, order);
                        if (!result.Success)
                        {
                            _logger.LogWarning(
                                "Chưa thể áp dụng quyền cho đơn đã thanh toán {OrderId}: {Message}",
                                orderId,
                                result.Message);
                            await SaveRepairFailureAsync(repo, notifications, order, orderId, result.Message);
                        }
                        else
                        {
                            await ResolveRepairFailureAsync(notifications, order, orderId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Sửa quyền sau thanh toán thất bại cho đơn {OrderId}.", orderId);
                        await SaveRepairFailureAsync(repo, notifications, order, orderId, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể quét các đơn đã thanh toán chưa được áp dụng quyền.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static async Task SaveRepairFailureAsync(
        IDataRepository repo,
        InAppNotificationService notifications,
        Dictionary<string, object?> order,
        string orderId,
        string message)
    {
        var previousError = FirstText(order, "activation_error", "activationError");
        if (string.Equals(previousError, message, StringComparison.Ordinal)) return;

        var now = DateTime.UtcNow;
        await repo.UpdateAsync("plan_orders", orderId, new Dictionary<string, object?>
        {
            ["activation_status"] = "error",
            ["activationStatus"] = "error",
            ["activation_error"] = message,
            ["activationError"] = message,
            ["activation_error_at"] = now,
            ["activationErrorAt"] = now,
            ["updated_at"] = now
        });

        await notifications.CreateForRoleAsync(
            "Admin",
            "technical",
            "system",
            "Lỗi áp dụng quyền sau thanh toán",
            $"Đơn {orderId}: {message}",
            "/manage",
            "plan_order",
            orderId,
            "benefit-repair-failed-admin",
            "error");
    }

    private static async Task ResolveRepairFailureAsync(
        InAppNotificationService notifications,
        Dictionary<string, object?> order,
        string orderId)
    {
        await notifications.DeactivateForRoleAsync("Admin", "plan_order", orderId, "benefit-repair-failed-admin");
    }

    private static bool NeedsRepair(Dictionary<string, object?> order)
    {
        if (!string.Equals(FirstText(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase)) return false;
        if (Truthy(order, "benefits_applied", "benefitsApplied")
            && string.Equals(FirstText(order, "activation_status", "activationStatus"), "activated", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Quyền phong cách là vĩnh viễn nên vẫn cần repair dù đơn đã tạo từ lâu.
        if (string.Equals(FirstText(order, "order_type", "orderType"), "chatbot_style", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Gói tài khoản đã hết hạn không còn quyền để kích hoạt. Bỏ qua các đơn cũ
        // thiếu marker thay vì thử repair và ghi warning lặp lại mỗi 15 giây.
        var expiresAt = ParseDateValue(FirstText(order, "plan_expires_at", "planExpiresAt"));
        return expiresAt == DateTime.MinValue || expiresAt > DateTime.UtcNow;
    }

    private static DateTime ParseDateValue(string? value)
    {
        if (!DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return DateTime.MinValue;
        }

        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
    }

    private static bool Truthy(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetValue(key, out var value) || value is null) continue;
            if (value is bool boolean) return boolean;
            if (bool.TryParse(value.ToString(), out var parsed)) return parsed;
            if (long.TryParse(value.ToString(), out var number)) return number != 0;
        }
        return false;
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
