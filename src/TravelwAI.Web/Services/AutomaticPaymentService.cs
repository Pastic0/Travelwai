using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Options;

namespace TravelwAI.Web.Services;

public sealed class AutomaticPaymentService
{
    private const string PlanOrdersCollection = "plan_orders";
    private const string TransactionsCollection = "payment_transactions";
    private static readonly Regex LegacyOrderIdRegex = new(@"(?<![a-fA-F0-9])[a-fA-F0-9]{32}(?![a-fA-F0-9])", RegexOptions.Compiled);

    private readonly IDataRepository _repo;
    private readonly NpgsqlDataSource _dataSource;
    private readonly PlanQueueService _planQueueService;
    private readonly ChatbotSettingsService _chatbotSettings;
    private readonly InAppNotificationService _notifications;
    private readonly SePayOptions _options;
    private readonly ILogger<AutomaticPaymentService> _logger;

    public AutomaticPaymentService(
        IDataRepository repo,
        NpgsqlDataSource dataSource,
        PlanQueueService planQueueService,
        ChatbotSettingsService chatbotSettings,
        InAppNotificationService notifications,
        IOptions<SePayOptions> options,
        ILogger<AutomaticPaymentService> logger)
    {
        _repo = repo;
        _dataSource = dataSource;
        _planQueueService = planQueueService;
        _chatbotSettings = chatbotSettings;
        _notifications = notifications;
        _options = options.Value;
        _logger = logger;
    }

    public static string CreatePaymentCode(string orderId, string? configuredPrefix = null, int configuredSuffixLength = 20)
    {
        var prefix = NormalizePrefix(configuredPrefix);
        var suffixLength = Math.Clamp(configuredSuffixLength, 8, 30);
        var normalizedOrderId = new string((orderId ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

        if (normalizedOrderId.Length < suffixLength)
        {
            normalizedOrderId = normalizedOrderId.PadRight(suffixLength, '0');
        }

        return prefix + normalizedOrderId[..suffixLength];
    }

    public async Task<AutomaticPaymentResult> ProcessSePayAsync(SePayWebhookPayload payload, CancellationToken cancellationToken = default)
    {
        if (payload.Id <= 0)
        {
            return AutomaticPaymentResult.Skipped("Webhook không có mã giao dịch hợp lệ.");
        }

        var transactionDocumentId = $"sepay-{payload.Id}";
        await using var lockConnection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var lockKey = $"travelwai-sepay-payment:{payload.Id}";

        await using (var lockCommand = lockConnection.CreateCommand())
        {
            lockCommand.CommandText = "select pg_advisory_lock(hashtextextended(@lock_key, 0));";
            lockCommand.Parameters.AddWithValue("lock_key", lockKey);
            await lockCommand.ExecuteScalarAsync(cancellationToken);
        }

        try
        {
            var existingTransaction = await _repo.GetByIdAsync(TransactionsCollection, transactionDocumentId);
            if (existingTransaction is not null
                && string.Equals(FirstText(existingTransaction, "processing_status", "processingStatus"), "processed", StringComparison.OrdinalIgnoreCase))
            {
                var previousOrderId = FirstText(existingTransaction, "order_id", "orderId");
                if (!string.IsNullOrWhiteSpace(previousOrderId))
                {
                    var previousOrder = await _repo.GetByIdAsync(PlanOrdersCollection, previousOrderId);
                    if (previousOrder is not null)
                    {
                        var benefit = await EnsureOrderBenefitsAsync(previousOrderId, previousOrder);
                        if (!benefit.Success) throw new InvalidOperationException(benefit.Message);
                    }
                }
                return AutomaticPaymentResult.Processed(
                    previousOrderId,
                    "Giao dịch đã được xử lý trước đó.",
                    duplicate: true);
            }

            await SaveTransactionAsync(transactionDocumentId, payload, "received", string.Empty, string.Empty);

            if (!string.Equals(payload.TransferType, "in", StringComparison.OrdinalIgnoreCase))
            {
                await SaveTransactionAsync(transactionDocumentId, payload, "ignored", string.Empty, "Không phải giao dịch tiền vào.");
                return AutomaticPaymentResult.Skipped("Không phải giao dịch tiền vào.");
            }

            if (payload.TransferAmount <= 0)
            {
                await SaveTransactionAsync(transactionDocumentId, payload, "ignored", string.Empty, "Số tiền giao dịch không hợp lệ.");
                return AutomaticPaymentResult.Skipped("Số tiền giao dịch không hợp lệ.");
            }

            var paymentCodes = ExtractPaymentCodes(payload);
            await _repo.UpdateAsync(TransactionsCollection, transactionDocumentId, new Dictionary<string, object?>
            {
                ["detected_payment_codes"] = string.Join(",", paymentCodes),
                ["detectedPaymentCodes"] = string.Join(",", paymentCodes),
                ["updated_at"] = DateTime.UtcNow
            });

            var order = await FindOrderAsync(paymentCodes, payload);
            if (order is null)
            {
                var detectedCodes = paymentCodes.Count == 0
                    ? "không phát hiện được mã TWAI"
                    : string.Join(", ", paymentCodes);
                await SaveTransactionAsync(
                    transactionDocumentId,
                    payload,
                    "unmatched",
                    string.Empty,
                    $"Không tìm thấy đơn cho mã: {detectedCodes}.");
                return AutomaticPaymentResult.Skipped("Không tìm thấy đơn hàng tương ứng.");
            }

            var orderId = FirstText(order, "id", "Id");

            // SePay may put the receiving virtual/sub account in subAccount while
            // accountNumber contains the underlying bank account. A unique TWAI
            // code already identifies the order, so an account-field mismatch must
            // not prevent a valid paid order from being activated.
            if (_options.ValidateAccountNumber
                && !string.IsNullOrWhiteSpace(_options.BankAccountNumber)
                && !MatchesConfiguredAccount(payload, _options.BankAccountNumber))
            {
                _logger.LogWarning(
                    "Giao dịch SePay {TransactionId} khớp mã đơn {OrderId} nhưng accountNumber/subAccount không trùng tài khoản hiển thị. Vẫn tiếp tục xác nhận bằng mã thanh toán và số tiền.",
                    payload.Id,
                    orderId);
                await _repo.UpdateAsync(TransactionsCollection, transactionDocumentId, new Dictionary<string, object?>
                {
                    ["account_validation_warning"] = true,
                    ["accountValidationWarning"] = true,
                    ["configured_account"] = _options.BankAccountNumber,
                    ["configuredAccount"] = _options.BankAccountNumber,
                    ["updated_at"] = DateTime.UtcNow
                });
            }

            var expectedAmount = Decimal(order, "price_amount", "priceAmount");
            var receivedAmount = decimal.Round(payload.TransferAmount, 0, MidpointRounding.AwayFromZero);
            var roundedExpectedAmount = decimal.Round(expectedAmount, 0, MidpointRounding.AwayFromZero);

            if (roundedExpectedAmount <= 0 || receivedAmount != roundedExpectedAmount)
            {
                var mismatchMessage = $"Sai số tiền: cần {roundedExpectedAmount:0} VND, nhận {receivedAmount:0} VND.";
                await _repo.UpdateAsync(PlanOrdersCollection, orderId, new Dictionary<string, object?>
                {
                    ["payment_status"] = "Sai số tiền",
                    ["paymentStatus"] = "Sai số tiền",
                    ["payment_last_received_amount"] = receivedAmount,
                    ["paymentLastReceivedAmount"] = receivedAmount,
                    ["payment_last_transaction_id"] = payload.Id,
                    ["paymentLastTransactionId"] = payload.Id,
                    ["payment_mismatch_at"] = DateTime.UtcNow,
                    ["paymentMismatchAt"] = DateTime.UtcNow,
                    ["updated_at"] = DateTime.UtcNow
                });
                await SaveTransactionAsync(transactionDocumentId, payload, "amount_mismatch", orderId, mismatchMessage);
                return AutomaticPaymentResult.Skipped(mismatchMessage);
            }

            if (string.Equals(FirstText(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
            {
                var repaired = await EnsureOrderBenefitsAsync(orderId, order);
                if (!repaired.Success)
                {
                    throw new InvalidOperationException(repaired.Message);
                }
                await SaveTransactionAsync(transactionDocumentId, payload, "processed", orderId, "Đơn đã được thanh toán trước đó.");
                return AutomaticPaymentResult.Processed(orderId, "Đơn đã được thanh toán trước đó.", duplicate: true);
            }

            var result = await ActivateOrderAsync(orderId, order, payload);
            await SaveTransactionAsync(transactionDocumentId, payload, result.Success ? "processed" : "rejected", orderId, result.Message);
            if (result.Success)
            {
                await CreatePaymentSuccessNotificationAsync(orderId, order, payload.TransferAmount);
            }
            return result;
        }
        finally
        {
            try
            {
                await using var unlockCommand = lockConnection.CreateCommand();
                unlockCommand.CommandText = "select pg_advisory_unlock(hashtextextended(@lock_key, 0));";
                unlockCommand.Parameters.AddWithValue("lock_key", lockKey);
                await unlockCommand.ExecuteScalarAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể giải phóng advisory lock cho giao dịch SePay {TransactionId}.", payload.Id);
            }
        }
    }


    /// <summary>
    /// Reconciles an order against webhook payloads already stored by SePay.
    /// This lets the checkout polling recover immediately when SePay received the
    /// transfer but the first webhook pass was unmatched/ignored or interrupted.
    /// </summary>
    public async Task<AutomaticPaymentResult?> TryReconcileOrderAsync(
        string orderId,
        Dictionary<string, object?>? knownOrder = null,
        CancellationToken cancellationToken = default)
    {
        var order = knownOrder ?? await _repo.GetByIdAsync(PlanOrdersCollection, orderId);
        if (order is null) return null;
        if (string.Equals(FirstText(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
            return null;

        var expectedCode = NormalizePaymentCode(FirstText(order, "payment_code", "paymentCode", "payment_content", "paymentContent"));
        var expectedAmount = decimal.Round(Decimal(order, "price_amount", "priceAmount"), 0, MidpointRounding.AwayFromZero);
        if (string.IsNullOrWhiteSpace(expectedCode) || expectedAmount <= 0) return null;

        var transactions = await _repo.GetAllAsync(TransactionsCollection, limit: 1000);
        foreach (var transaction in transactions
                     .OrderByDescending(item => ParseDateValue(FirstText(item, "received_at", "receivedAt", "updated_at"))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processingStatus = FirstText(transaction, "processing_status", "processingStatus");
            if (string.Equals(processingStatus, "processed", StringComparison.OrdinalIgnoreCase)) continue;

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
            catch (JsonException)
            {
                continue;
            }

            if (payload is null
                || payload.Id <= 0
                || !string.Equals(payload.TransferType, "in", StringComparison.OrdinalIgnoreCase)
                || decimal.Round(payload.TransferAmount, 0, MidpointRounding.AwayFromZero) != expectedAmount)
            {
                continue;
            }

            var codes = ExtractPaymentCodes(payload);
            if (!codes.Any(code => string.Equals(NormalizePaymentCode(code), expectedCode, StringComparison.OrdinalIgnoreCase)))
                continue;

            return await ProcessSePayAsync(payload, cancellationToken);
        }

        return null;
    }


    public async Task<PaymentBenefitResult> EnsureOrderBenefitsAsync(
        string orderId,
        Dictionary<string, object?>? knownOrder = null)
    {
        var order = knownOrder ?? await _repo.GetByIdAsync(PlanOrdersCollection, orderId);
        if (order is null) return PaymentBenefitResult.Failed("Không tìm thấy đơn thanh toán.");
        if (!string.Equals(FirstText(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
            return PaymentBenefitResult.Failed("Đơn chưa được ngân hàng xác nhận.");

        var buyerId = FirstText(order, "buyer_id", "buyerId");
        if (string.IsNullOrWhiteSpace(buyerId)) return PaymentBenefitResult.Failed("Đơn thiếu tài khoản người mua.");

        if (string.Equals(FirstText(order, "order_type", "orderType"), "chatbot_style", StringComparison.OrdinalIgnoreCase))
        {
            var styleId = FirstText(order, "style_id", "styleId");
            if (string.IsNullOrWhiteSpace(styleId)) return PaymentBenefitResult.Failed("Đơn thiếu mã phong cách.");
            if (!await _chatbotSettings.GrantPurchasedStyleAsync(buyerId, styleId))
                return PaymentBenefitResult.Failed("Phong cách trong đơn không còn tồn tại.");
            if (!await _chatbotSettings.UserOwnsStyleAsync(buyerId, styleId))
                return PaymentBenefitResult.Failed("Chưa lưu được quyền sử dụng phong cách.");

            await MarkBenefitsAppliedAsync(orderId, "chatbot_style", styleId);
            return PaymentBenefitResult.Applied("Phong cách đã được mở khóa.");
        }

        var role = NormalizePlanRole(FirstText(order, "plan_role", "planRole", "role"));
        if (role is not ("VIP" or "Premium" or "Sales" or "Company"))
            return PaymentBenefitResult.Failed("Gói tài khoản trong đơn không hợp lệ.");

        var state = await _planQueueService.SyncUserAsync(buyerId);
        var startedAt = ParseDateValue(FirstText(order, "plan_started_at", "planStartedAt"));
        var expiresAt = ParseDateValue(FirstText(order, "plan_expires_at", "planExpiresAt"));
        var now = DateTime.UtcNow;
        var activeNow = startedAt != DateTime.MinValue && startedAt <= now.AddSeconds(10) && expiresAt > now;
        var queued = startedAt > now && expiresAt > startedAt;
        var roleMatches = string.Equals(state.CurrentRole, role, StringComparison.OrdinalIgnoreCase);

        if ((!activeNow || !roleMatches) && !queued)
            return PaymentBenefitResult.Failed("Chưa cập nhật được quyền của gói tài khoản.");

        await MarkBenefitsAppliedAsync(orderId, "account_plan", role);
        return PaymentBenefitResult.Applied("Gói tài khoản đã được kích hoạt.", state.CurrentRole);
    }

    private async Task MarkBenefitsAppliedAsync(string orderId, string benefitType, string benefitValue)
    {
        var now = DateTime.UtcNow;
        await _repo.UpdateAsync(PlanOrdersCollection, orderId, new Dictionary<string, object?>
        {
            ["benefits_applied"] = true,
            ["benefitsApplied"] = true,
            ["activation_status"] = "activated",
            ["activationStatus"] = "activated",
            ["activation_error"] = null,
            ["activationError"] = null,
            ["activation_error_at"] = null,
            ["activationErrorAt"] = null,
            ["benefit_type"] = benefitType,
            ["benefitType"] = benefitType,
            ["benefit_value"] = benefitValue,
            ["benefitValue"] = benefitValue,
            ["benefits_applied_at"] = now,
            ["benefitsAppliedAt"] = now,
            ["updated_at"] = now
        });
    }

    private async Task<AutomaticPaymentResult> ActivateOrderAsync(string orderId, Dictionary<string, object?> order, SePayWebhookPayload payload)
    {
        var buyerId = FirstText(order, "buyer_id", "buyerId");
        if (string.IsNullOrWhiteSpace(buyerId))
        {
            return AutomaticPaymentResult.Skipped("Đơn thanh toán thiếu tài khoản người mua.");
        }

        var now = DateTime.UtcNow;
        var commonUpdates = new Dictionary<string, object?>
        {
            ["status"] = "Đã bán",
            ["payment_status"] = "Đã thanh toán",
            ["paymentStatus"] = "Đã thanh toán",
            ["payment_provider"] = "SePay",
            ["paymentProvider"] = "SePay",
            ["payment_transaction_id"] = payload.Id,
            ["paymentTransactionId"] = payload.Id,
            ["payment_reference_code"] = payload.ReferenceCode ?? string.Empty,
            ["paymentReferenceCode"] = payload.ReferenceCode ?? string.Empty,
            ["payment_received_amount"] = payload.TransferAmount,
            ["paymentReceivedAmount"] = payload.TransferAmount,
            ["payment_received_at"] = ParseTransactionDate(payload.TransactionDate, now),
            ["paymentReceivedAt"] = ParseTransactionDate(payload.TransactionDate, now),
            ["sold_by"] = "sepay-webhook",
            ["soldBy"] = "sepay-webhook",
            ["sold_at"] = now,
            ["soldAt"] = now,
            ["benefits_applied"] = false,
            ["benefitsApplied"] = false,
            ["activation_status"] = "pending",
            ["activationStatus"] = "pending",
            ["updated_at"] = now
        };

        if (string.Equals(FirstText(order, "order_type", "orderType"), "chatbot_style", StringComparison.OrdinalIgnoreCase))
        {
            var styleId = FirstText(order, "style_id", "styleId");
            if (string.IsNullOrWhiteSpace(styleId) || !await _chatbotSettings.GrantPurchasedStyleAsync(buyerId, styleId))
            {
                return AutomaticPaymentResult.Skipped("Phong cách trong đơn không còn tồn tại.");
            }
            if (!await _chatbotSettings.UserOwnsStyleAsync(buyerId, styleId))
            {
                throw new InvalidOperationException("Chưa lưu được quyền sử dụng phong cách.");
            }

            commonUpdates["benefits_applied"] = true;
            commonUpdates["benefitsApplied"] = true;
            commonUpdates["activation_status"] = "activated";
            commonUpdates["activationStatus"] = "activated";
            commonUpdates["benefit_type"] = "chatbot_style";
            commonUpdates["benefitType"] = "chatbot_style";
            commonUpdates["benefit_value"] = styleId;
            commonUpdates["benefitValue"] = styleId;
            commonUpdates["benefits_applied_at"] = now;
            commonUpdates["benefitsAppliedAt"] = now;
            await _repo.UpdateAsync(PlanOrdersCollection, orderId, commonUpdates);
            return AutomaticPaymentResult.Processed(orderId, "Thanh toán thành công. Phong cách đã được mở khóa.");
        }

        var role = NormalizePlanRole(FirstText(order, "plan_role", "planRole", "role"));
        if (role is not ("VIP" or "Premium"))
        {
            return AutomaticPaymentResult.Skipped("Loại gói trong đơn không hỗ trợ xác nhận tự động.");
        }

        var months = Math.Clamp(Int(order, "duration_months", "durationMonths", fallback: 1), 1, 12);
        var queueStart = await _planQueueService.GetNextPlanStartAsync(buyerId, orderId);
        var planExpiresAt = queueStart.AddMonths(months);

        commonUpdates["duration_months"] = months;
        commonUpdates["durationMonths"] = months;
        commonUpdates["plan_started_at"] = queueStart;
        commonUpdates["planStartedAt"] = queueStart;
        commonUpdates["plan_expires_at"] = planExpiresAt;
        commonUpdates["planExpiresAt"] = planExpiresAt;

        await _repo.UpdateAsync(PlanOrdersCollection, orderId, commonUpdates);
        var benefit = await EnsureOrderBenefitsAsync(orderId);
        if (!benefit.Success)
        {
            throw new InvalidOperationException(benefit.Message);
        }

        return AutomaticPaymentResult.Processed(orderId, "Thanh toán thành công. Gói tài khoản đã được kích hoạt.");
    }

    private async Task CreatePaymentSuccessNotificationAsync(
        string orderId,
        Dictionary<string, object?> order,
        decimal amount)
    {
        var buyerId = FirstText(order, "buyer_id", "buyerId");
        if (string.IsNullOrWhiteSpace(buyerId)) return;

        var orderType = FirstText(order, "order_type", "orderType");
        var itemName = string.Equals(orderType, "chatbot_style", StringComparison.OrdinalIgnoreCase)
            ? FirstText(order, "style_name", "styleName", "style_id", "styleId")
            : FirstText(order, "plan_name", "planName", "plan_role", "planRole", "role");
        if (string.IsNullOrWhiteSpace(itemName)) itemName = "dịch vụ TravelwAI";
        var paidAmount = amount > 0 ? amount : Decimal(order, "price_amount", "priceAmount");
        var amountText = paidAmount > 0
            ? string.Format(CultureInfo.GetCultureInfo("vi-VN"), "{0:N0} ₫", paidAmount)
            : "số tiền của đơn";

        await _notifications.CreateForUserAsync(
            buyerId,
            "payment",
            InAppNotificationService.PaymentSuccessCategory,
            "Thanh toán thành công",
            $"Bạn đã thanh toán thành công {amountText} cho {itemName}. Mã đơn: {orderId}.",
            "/profile",
            "payment",
            orderId,
            "payment-success");
    }

    private async Task<Dictionary<string, object?>?> FindOrderAsync(
        IReadOnlyList<string> paymentCodes,
        SePayWebhookPayload payload)
    {
        // Mỗi nội dung webhook có thể chứa nhiều chuỗi TWAI. Thử lần lượt tất cả
        // mã phát hiện được và chỉ nhận đơn có payment_code trùng chính xác.
        foreach (var paymentCode in paymentCodes)
        {
            var matches = await _repo.WhereEqualAsync(PlanOrdersCollection, "payment_code", paymentCode, limit: 5);
            var order = matches.FirstOrDefault();
            if (order is not null) return order;

            matches = await _repo.WhereEqualAsync(PlanOrdersCollection, "paymentCode", paymentCode, limit: 5);
            order = matches.FirstOrDefault();
            if (order is not null) return order;

            matches = await _repo.WhereEqualAsync(PlanOrdersCollection, "payment_content", paymentCode, limit: 5);
            order = matches.FirstOrDefault();
            if (order is not null) return order;
        }

        var combinedText = string.Join(' ', new[] { payload.Code, payload.Content, payload.Description }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var legacyId = LegacyOrderIdRegex.Match(combinedText).Value;
        if (!string.IsNullOrWhiteSpace(legacyId))
        {
            return await _repo.GetByIdAsync(PlanOrdersCollection, legacyId.ToLowerInvariant())
                ?? await _repo.GetByIdAsync(PlanOrdersCollection, legacyId);
        }

        return null;
    }

    private IReadOnlyList<string> ExtractPaymentCodes(SePayWebhookPayload payload)
    {
        var prefix = NormalizePrefix(_options.PaymentCodePrefix);
        var suffixLength = Math.Clamp(_options.PaymentCodeSuffixLength, 8, 30);

        // Chỉ cần nội dung CHỨA mã PREFIX + hậu tố. Không yêu cầu toàn bộ nội dung
        // phải bằng mã và không yêu cầu mã phải có khoảng trắng ở hai bên.
        var pattern = $@"{Regex.Escape(prefix)}[A-Z0-9]{{{suffixLength}}}";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddMatches(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;

            foreach (Match match in regex.Matches(candidate.ToUpperInvariant()))
            {
                var code = match.Value.ToUpperInvariant();
                if (seen.Add(code)) result.Add(code);
            }

            // Một số ngân hàng chèn khoảng trắng, dấu chấm hoặc dấu gạch vào giữa mã.
            // Chuẩn hóa toàn bộ nội dung rồi tìm lại để vẫn nhận đúng mã.
            var compactCandidate = NormalizePaymentCode(candidate);
            foreach (Match match in regex.Matches(compactCandidate))
            {
                var code = match.Value.ToUpperInvariant();
                if (seen.Add(code)) result.Add(code);
            }
        }

        AddMatches(payload.Code);
        AddMatches(payload.Content);
        AddMatches(payload.Description);

        // Giữ tương thích với webhook cũ khi trường code đã chứa sẵn mã hợp lệ.
        var normalizedCode = NormalizePaymentCode(payload.Code);
        if (!string.IsNullOrWhiteSpace(normalizedCode)
            && normalizedCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && normalizedCode.Length == prefix.Length + suffixLength
            && seen.Add(normalizedCode))
        {
            result.Add(normalizedCode);
        }

        return result;
    }

    private async Task SaveTransactionAsync(string id, SePayWebhookPayload payload, string status, string orderId, string note)
    {
        var rawPayload = JsonSerializer.Serialize(payload);
        await _repo.SetAsync(TransactionsCollection, id, new Dictionary<string, object?>
        {
            ["provider"] = "SePay",
            ["provider_transaction_id"] = payload.Id,
            ["providerTransactionId"] = payload.Id,
            ["gateway"] = payload.Gateway ?? string.Empty,
            ["transaction_date"] = payload.TransactionDate ?? string.Empty,
            ["transactionDate"] = payload.TransactionDate ?? string.Empty,
            ["account_number"] = payload.AccountNumber ?? string.Empty,
            ["accountNumber"] = payload.AccountNumber ?? string.Empty,
            ["sub_account"] = payload.SubAccount ?? string.Empty,
            ["subAccount"] = payload.SubAccount ?? string.Empty,
            ["payment_code"] = NormalizePaymentCode(payload.Code),
            ["paymentCode"] = NormalizePaymentCode(payload.Code),
            ["content"] = payload.Content ?? string.Empty,
            ["description"] = payload.Description ?? string.Empty,
            ["transfer_type"] = payload.TransferType ?? string.Empty,
            ["transferType"] = payload.TransferType ?? string.Empty,
            ["transfer_amount"] = payload.TransferAmount,
            ["transferAmount"] = payload.TransferAmount,
            ["reference_code"] = payload.ReferenceCode ?? string.Empty,
            ["referenceCode"] = payload.ReferenceCode ?? string.Empty,
            ["order_id"] = orderId,
            ["orderId"] = orderId,
            ["processing_status"] = status,
            ["processingStatus"] = status,
            ["note"] = note,
            ["raw_payload"] = rawPayload,
            ["rawPayload"] = rawPayload,
            ["received_at"] = DateTime.UtcNow,
            ["receivedAt"] = DateTime.UtcNow,
            ["updated_at"] = DateTime.UtcNow
        }, merge: true);
    }

    private static string NormalizePrefix(string? value)
    {
        var prefix = new string((value ?? "TWAI")
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .Take(5)
            .ToArray());
        if (prefix.Length < 2) return "TWAI";
        return prefix;
    }

    private static string NormalizePaymentCode(string? value)
        => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool MatchesConfiguredAccount(SePayWebhookPayload payload, string configuredAccount)
    {
        static string Normalize(string? value)
            => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        var expected = Normalize(configuredAccount);
        if (string.IsNullOrWhiteSpace(expected)) return true;

        var accountNumber = Normalize(payload.AccountNumber);
        var subAccount = Normalize(payload.SubAccount);
        if (string.IsNullOrWhiteSpace(accountNumber) && string.IsNullOrWhiteSpace(subAccount)) return true;

        return string.Equals(accountNumber, expected, StringComparison.Ordinal)
            || string.Equals(subAccount, expected, StringComparison.Ordinal);
    }

    private static DateTime ParseTransactionDate(string? value, DateTime fallback)
    {
        if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var vietnamTime))
        {
            return new DateTimeOffset(
                DateTime.SpecifyKind(vietnamTime, DateTimeKind.Unspecified),
                TimeSpan.FromHours(7)).UtcDateTime;
        }
        return fallback;
    }

    private static DateTime ParseDateValue(string? value)
    {
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return DateTime.MinValue;
        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
    }

    private static string NormalizePlanRole(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text switch
        {
            "vip" => "VIP",
            "premium" => "Premium",
            "sales" or "sale" or "tour sales" or "toursales" => "Sales",
            "business" or "company" => "Company",
            _ => string.Empty
        };
    }

    private static int Int(Dictionary<string, object?> row, string firstKey, string secondKey, int fallback = 0)
    {
        foreach (var key in new[] { firstKey, secondKey })
        {
            if (int.TryParse(FirstText(row, key), out var value)) return value;
        }
        return fallback;
    }

    private static decimal Decimal(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            var text = FirstText(row, key);
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var invariant)) return invariant;
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("vi-VN"), out var vietnamese)) return vietnamese;
        }
        return 0m;
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

public sealed class SePayWebhookPayload
{
    public long Id { get; set; }
    public string? Gateway { get; set; }
    public string? TransactionDate { get; set; }
    public string? AccountNumber { get; set; }
    public string? SubAccount { get; set; }
    public string? Code { get; set; }
    public string? Content { get; set; }
    public string? TransferType { get; set; }
    public string? Description { get; set; }
    public decimal TransferAmount { get; set; }
    public decimal Accumulated { get; set; }
    public string? ReferenceCode { get; set; }
}

public sealed record AutomaticPaymentResult(bool Success, bool Ignored, bool Duplicate, string OrderId, string Message)
{
    public static AutomaticPaymentResult Processed(string orderId, string message, bool duplicate = false)
        => new(true, false, duplicate, orderId, message);

    public static AutomaticPaymentResult Skipped(string message)
        => new(false, true, false, string.Empty, message);
}

public sealed record PaymentBenefitResult(bool Success, string Message, string? CurrentRole = null)
{
    public static PaymentBenefitResult Applied(string message, string? currentRole = null) => new(true, message, currentRole);
    public static PaymentBenefitResult Failed(string message) => new(false, message);
}
