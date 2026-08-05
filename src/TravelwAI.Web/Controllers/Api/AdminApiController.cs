using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/admin")]
public sealed class AdminApiController : ApiControllerBase
{
    private const string AdminEmail = "2324802010387@student.tdmu.edu.vn";
    private const int MaxAdminAccounts = 4;
    private const string PlanStatusOptionsCollection = "plan_status_options";
    private const string ProvinceTravelTagsCollection = "province_travel_tags";
    private const string PlanTravelTagsCollection = "plan_travel_tags";
    private const string SalesLevelSettingsCollection = "sales_level_settings";
    private const string SalesLevelSettingsDocumentId = "default";
    private const long MaxAvatarBytes = 10 * 1024 * 1024;
    private static readonly string[] DashboardTourFields =
    {
        "status", "slots", "sold", "created_by", "createdBy", "tour_sales_id", "tourSalesId", "seller_id", "sellerId"
    };
    private static readonly string[] DashboardOrderFields =
    {
        "status", "tour_id", "tourId", "tour_sales_id", "tourSalesId", "created_by", "createdBy", "seller_id", "sellerId", "owner_role", "ownerRole",
        "total_price", "totalPrice", "original_total_price", "originalTotalPrice", "commission_base_total", "commissionBaseTotal",
        "discount_amount", "discountAmount", "commission_percent", "commissionPercent", "commission_amount", "commissionAmount",
        "service_fee_percent", "serviceFeePercent", "service_percent", "servicePercent",
        "service_fee_amount", "serviceFeeAmount", "service_amount", "serviceAmount"
    };
    private static readonly string[] RevenueOrderFields =
    {
        "status", "quantity", "tour_id", "tourId", "tour_sales_id", "tourSalesId", "created_by", "createdBy", "seller_id", "sellerId", "owner_role", "ownerRole",
        "total_price", "totalPrice", "original_total_price", "originalTotalPrice", "commission_base_total", "commissionBaseTotal",
        "discount_amount", "discountAmount", "commission_percent", "commissionPercent", "commission_amount", "commissionAmount",
        "service_fee_percent", "serviceFeePercent", "service_percent", "servicePercent",
        "service_fee_amount", "serviceFeeAmount", "service_amount", "serviceAmount"
    };
    private static readonly string[] RevenueTourFields = { "created_by", "createdBy", "tour_sales_id", "tourSalesId", "seller_id", "sellerId" };
    private static readonly string[] DashboardScheduleFields = { "created_at" };
    private static readonly string[] DashboardPostFields = { "status", "is_deleted", "isDeleted" };
    private static readonly string[] ScheduleListFields =
    {
        "title", "name", "schedule_name", "user_id", "created_by_user_id", "created_by", "creator_name", "creatorName",
        "creator_email", "creatorEmail", "owner_name", "ownerName", "owner_email", "ownerEmail", "description", "start_date",
        "startDate", "end_date", "endDate", "status", "created_at"
    };
    private static readonly string[] AccountUserFields =
    {
        "uid", "email", "username", "displayName", "display_name", "role", "userRole", "sales_level", "salesLevel",
        "commission_level", "commissionLevel", "offer_level", "offerLevel", "service_level", "serviceLevel",
        "offer_discount_percent", "offerDiscountPercent", "admin_offer_discount_percent", "adminOfferDiscountPercent",
        "admin_offer_override", "adminOfferOverride", "commission_percent", "commissionPercent", "commission_manual_override",
        "commissionManualOverride", "service_fee_percent", "serviceFeePercent", "service_percent", "servicePercent",
        "plan_role", "planRole", "plan_started_at", "planStartedAt", "plan_expires_at", "planExpiresAt",
        "plan_last_order_id", "planLastOrderId", "next_plan_role", "nextPlanRole", "next_plan_started_at",
        "nextPlanStartedAt", "next_plan_expires_at", "nextPlanExpiresAt", "plan_countdown_seconds", "planCountdownSeconds"
    };
    private static readonly string[] AccountOrderCountFields =
    {
        "status", "quantity", "tour_sales_id", "tourSalesId", "created_by", "createdBy", "seller_id", "sellerId"
    };
    private static readonly string[] AccountInviteFields = { "inviter_id", "invited_email", "status", "accepted_at", "invited_user_id", "updated_at", "created_at" };
    private static readonly string[] AccountPostOfferFields = { "user_id", "status", "updated_at", "created_at" };
    private readonly IDataRepository _repo;
    private readonly NpgsqlDataSource _dataSource;
    private readonly TourOfferService _offerService;
    private readonly IFileStorageService _fileStorage;
    private readonly ChatbotSettingsService _chatbotSettings;
    private readonly PlanQueueService _planQueueService;
    private readonly AiProviderSettingsService _aiProviderSettings;

    public AdminApiController(
        IAuthService authService,
        IDataRepository repo,
        NpgsqlDataSource dataSource,
        TourOfferService offerService,
        IFileStorageService fileStorage,
        ChatbotSettingsService chatbotSettings,
        PlanQueueService planQueueService,
        AiProviderSettingsService aiProviderSettings) : base(authService)
    {
        _repo = repo;
        _dataSource = dataSource;
        _offerService = offerService;
        _fileStorage = fileStorage;
        _chatbotSettings = chatbotSettings;
        _planQueueService = planQueueService;
        _aiProviderSettings = aiProviderSettings;
    }


    [HttpGet("ai-provider")]
    public async Task<IActionResult> GetAiProvider()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var status = await _aiProviderSettings.GetStatusAsync(forceRefresh: true);
        return Ok(new
        {
            success = true,
            data = new
            {
                provider = status.Provider,
                model = status.Model,
                openRouterConfigured = status.OpenRouterConfigured,
                openRouterModel = status.OpenRouterModel,
                ollamaModel = status.OllamaModel
            }
        });
    }

    [HttpPut("ai-provider")]
    public async Task<IActionResult> SetAiProvider([FromBody] AdminAiProviderUpdateRequest? request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var provider = AiProviderSettingsService.NormalizeProvider(request?.Provider);
        if (provider != AiProviderSettingsService.OllamaProvider && provider != AiProviderSettingsService.OpenRouterProvider)
            return BadRequest(new { success = false, message = "Nhà cung cấp AI không hợp lệ." });

        try
        {
            var status = await _aiProviderSettings.SetProviderAsync(provider, access.userId);
            return Ok(new
            {
                success = true,
                message = status.Provider == AiProviderSettingsService.OpenRouterProvider
                    ? $"Đã chuyển AI sang OpenRouter ({status.Model})."
                    : $"Đã chuyển AI sang Ollama ({status.Model}).",
                data = new
                {
                    provider = status.Provider,
                    model = status.Model,
                    openRouterConfigured = status.OpenRouterConfigured,
                    openRouterModel = status.OpenRouterModel,
                    ollamaModel = status.OllamaModel
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }


    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var accountsTask = ReadAccountsAsync();
        var toursTask = _repo.GetAllFieldsAsync("tours", DashboardTourFields);
        var ordersTask = _repo.GetAllFieldsAsync("tour_orders", DashboardOrderFields);
        var schedulesTask = _repo.GetAllFieldsAsync("schedules", DashboardScheduleFields, limit: 500, includeId: false);
        var postsTask = _repo.GetAllFieldsAsync("travel_posts", DashboardPostFields, limit: 400);

        await Task.WhenAll(accountsTask, toursTask, ordersTask, schedulesTask, postsTask);

        var accounts = await accountsTask;
        var tours = await toursTask;
        var orders = await ordersTask;
        var schedules = await schedulesTask;
        var posts = await postsTask;
        var soldRevenueOrders = BuildOwnedRevenueOrders(
            orders.Where(order => string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase)),
            tours,
            accounts);
        var adminOwnedOrders = soldRevenueOrders.Where(item => IsAdminRole(item.OwnerRole)).ToList();
        var adminSalesOrders = soldRevenueOrders.Where(item => IsSalesRole(item.OwnerRole)).ToList();
        var adminCompanyOrders = soldRevenueOrders.Where(item => IsCompanyRole(item.OwnerRole)).ToList();
        var adminServiceRevenue = adminCompanyOrders.Sum(item => GetOrderServiceAmount(item.Order));
        var adminDiscount = adminOwnedOrders.Sum(item => GetOrderDiscountAmount(item.Order))
            + adminSalesOrders.Sum(item => GetOrderDiscountAmount(item.Order))
            + adminCompanyOrders.Sum(item => GetOrderDiscountAmount(item.Order));
        var adminOwnTourRevenue = adminOwnedOrders.Sum(item => GetAdminOwnedOrderRevenue(item.Order));
        var adminSalesRevenue = adminSalesOrders.Sum(item => GetAdminSalesOrderRevenue(item.Order));
        var adminCompanyRevenue = adminCompanyOrders.Sum(item => GetAdminCompanyOrderRevenue(item.Order));

        return Ok(new
        {
            success = true,
            data = new
            {
                accounts = accounts.Count,
                lockedAccounts = accounts.Count(a => IsTruthy(a.GetValueOrDefault("is_locked"))),
                tours = tours.Count,
                activeTours = tours.Count(t => string.Equals(Text(t, "status"), "Đang bán", StringComparison.OrdinalIgnoreCase) && !(GetInt(t, "slots") > 0 && GetInt(t, "sold") >= GetInt(t, "slots"))),
                tourOrders = orders.Count,
                schedules = schedules.Count,
                posts = posts.Count(p => !IsDeletedPost(p)),
                revenue = adminOwnTourRevenue + adminSalesRevenue + adminCompanyRevenue,
                grossRevenue = adminOwnedOrders.Sum(item => GetOrderOriginalTotal(item.Order))
                    + adminSalesOrders.Sum(item => GetOrderOriginalTotal(item.Order)),
                discountDeducted = adminDiscount,
                commissionDeducted = adminSalesOrders.Sum(item => GetOrderCommissionAmount(item.Order)),
                serviceFee = adminServiceRevenue,
                service_fee = adminServiceRevenue
            }
        });
    }

    [HttpGet("revenue-by-account")]
    public async Task<IActionResult> RevenueByAccount()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var accountsTask = ReadAccountsAsync();
        var ordersTask = _repo.GetAllFieldsAsync("tour_orders", RevenueOrderFields);
        var toursTask = _repo.GetAllFieldsAsync("tours", RevenueTourFields);
        await Task.WhenAll(accountsTask, ordersTask, toursTask);

        var accounts = await accountsTask;
        var tours = await toursTask;
        var soldOrders = (await ordersTask)
            .Where(order => string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ownedRevenueOrders = BuildOwnedRevenueOrders(soldOrders, tours, accounts);
        var ordersByAccount = ownedRevenueOrders
            .Where(item => !string.IsNullOrWhiteSpace(item.OwnerId))
            .GroupBy(item => item.OwnerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.OrdinalIgnoreCase);

        var accountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<object>();
        foreach (var account in accounts)
        {
            var accountId = Text(account, "id");
            if (!string.IsNullOrWhiteSpace(accountId)) accountIds.Add(accountId);
            var accountOrders = !string.IsNullOrWhiteSpace(accountId) && ordersByAccount.TryGetValue(accountId, out var matchedOrders)
                ? matchedOrders
                : new List<OwnedRevenueOrder>();
            rows.Add(BuildAccountRevenueRow(account, accountId, accountOrders, ownedRevenueOrders));
        }

        foreach (var (accountId, accountOrders) in ordersByAccount)
        {
            if (accountIds.Contains(accountId)) continue;
            rows.Add(BuildAccountRevenueRow(null, accountId, accountOrders, ownedRevenueOrders));
        }

        var orderedRows = rows
            .OrderByDescending(row => GetAnonymousDecimal(row, "revenue"))
            .ThenBy(row => GetAnonymousText(row, "username"), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Ok(new { success = true, data = orderedRows });
    }

    [HttpGet("accounts")]
    public async Task<IActionResult> Accounts()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var accounts = await ReadAccountsAsync();
        await EnsureAccountRolePrefixesAsync(accounts);
        await AttachOfferDiscountsAsync(accounts);
        return Ok(new { success = true, data = accounts });
    }
    [HttpGet("storage")]
    public async Task<IActionResult> StorageOverview()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var accounts = await ReadAccountsAsync();
        var accountIds = accounts
            .Select(account => Text(account, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        var usageRows = await _fileStorage.GetUsersImageStorageUsageAsync(accountIds);
        var usageByUser = usageRows.ToDictionary(item => item.UserId, StringComparer.Ordinal);
        var users = accounts.Select(account =>
        {
            var id = Text(account, "id");
            usageByUser.TryGetValue(id, out var row);
            var usage = row?.Usage ?? new TravelwAI.Models.Storage.UserImageStorageUsage(0, 200L * 1024 * 1024, 0);
            return new
            {
                id,
                username = Text(account, "username"),
                email = Text(account, "email"),
                role = Text(account, "role"),
                usage = BuildStorageUsagePayload(usage),
                categories = (row?.Categories ?? Array.Empty<TravelwAI.Models.Storage.UserImageStorageCategoryUsage>())
                    .Select(BuildStorageCategoryPayload)
            };
        }).ToList();

        var totalUsage = await _fileStorage.GetTotalImageStorageUsageAsync();
        var totalUsedBytes = totalUsage.UsedBytes;
        var totalLimitBytes = totalUsage.LimitBytes;
        var totalImageCount = totalUsage.ImageCount;
        var totalPercent = totalUsage.UsedPercent;

        return Ok(new
        {
            success = true,
            data = new
            {
                usedBytes = totalUsedBytes,
                limitBytes = totalLimitBytes,
                remainingBytes = Math.Max(0, totalLimitBytes - totalUsedBytes),
                usedPercent = totalPercent,
                imageCount = totalImageCount,
                accountCount = users.Count,
                users
            }
        });
    }

    [HttpPut("storage/limit")]
    public async Task<IActionResult> UpdateStorageLimit([FromBody] AdminStorageLimitRequest? request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        if (request is null || request.LimitBytes <= 0)
        {
            return BadRequest(new { success = false, message = "Tổng hạn mức không hợp lệ." });
        }
        if (request.LimitBytes < TravelwAI.Business.Services.FileStorageService.MinTotalImageStorageLimitBytes
            || request.LimitBytes > TravelwAI.Business.Services.FileStorageService.MaxTotalImageStorageLimitBytes)
        {
            return BadRequest(new { success = false, message = "Tổng hạn mức phải từ 1 MB đến 10 TB." });
        }

        var usage = await _fileStorage.SetTotalImageStorageLimitAsync(request.LimitBytes, access.userId!);
        return Ok(new
        {
            success = true,
            message = "Đã lưu tổng hạn mức.",
            data = BuildStorageUsagePayload(usage)
        });
    }

    [HttpGet("storage/{userId}")]
    public async Task<IActionResult> StorageDetails(string userId)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var account = await ReadAccountAsync(userId);
        if (account is null) return NotFound(new { success = false, message = "Không tìm thấy tài khoản." });
        var details = await _fileStorage.GetUserImageStorageDetailsAsync(userId);
        return Ok(new
        {
            success = true,
            data = BuildStorageDetailsPayload(account, details)
        });
    }

    [HttpDelete("storage/{userId}")]
    public async Task<IActionResult> DeleteUserStorage(string userId)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var account = await ReadAccountAsync(userId);
        if (account is null) return NotFound(new { success = false, message = "Không tìm thấy tài khoản." });
        var result = await _fileStorage.DeleteAllUserImagesAsync(userId);
        var details = await _fileStorage.GetUserImageStorageDetailsAsync(userId);
        return Ok(new
        {
            success = true,
            message = result.DeletedCount > 0 ? "Đã xóa toàn bộ ảnh của tài khoản." : "Tài khoản không có ảnh để xóa.",
            deletedCount = result.DeletedCount,
            deletedBytes = result.DeletedBytes,
            data = BuildStorageDetailsPayload(account, details)
        });
    }

    [HttpDelete("storage/{userId}/items/{uploadId}")]
    public async Task<IActionResult> DeleteUserStorageItem(string userId, string uploadId)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var account = await ReadAccountAsync(userId);
        if (account is null) return NotFound(new { success = false, message = "Không tìm thấy tài khoản." });
        var result = await _fileStorage.DeleteUserImageAsync(userId, uploadId);
        if (result.DeletedCount == 0)
        {
            return NotFound(new { success = false, message = "Không tìm thấy ảnh." });
        }
        var details = await _fileStorage.GetUserImageStorageDetailsAsync(userId);
        return Ok(new
        {
            success = true,
            message = "Đã xóa ảnh.",
            deletedCount = result.DeletedCount,
            deletedBytes = result.DeletedBytes,
            data = BuildStorageDetailsPayload(account, details)
        });
    }

    [HttpPost("background/{theme}")]
    public async Task<IActionResult> UpdateSiteBackground(string theme, [FromForm] IFormFile? image)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var background = NormalizeBackgroundTheme(theme);
        if (background is null)
        {
            return BadRequest(new { success = false, message = "Loại nền không hợp lệ." });
        }
        if (image is null || image.Length == 0)
        {
            return BadRequest(new { success = false, message = "Vui lòng chọn ảnh nền." });
        }
        if (image.Length > MaxAvatarBytes)
        {
            return BadRequest(new { success = false, message = "Ảnh nền tối đa 10MB." });
        }

        var imageExt = NormalizeOptimizedUploadExtension(image.FileName);
        if (!string.Equals(imageExt, ".webp", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Ảnh nền phải được chuyển sang WEBP trước khi upload." });
        }

        string? backgroundUrl;
        try
        {
            backgroundUrl = await _fileStorage.SaveImageToSupabaseAsync(
                image,
                access.userId!,
                $"site-branding/backgrounds/{background.Value.Key}");
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                message = ex.Message
            });
        }

        if (string.IsNullOrWhiteSpace(backgroundUrl))
        {
            return BadRequest(new { success = false, message = "Không thể lưu ảnh nền lên Supabase Storage." });
        }

        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var now = DateTime.UtcNow;
        var settingPrefix = background.Value.Key == "dark" ? "background_dark" : "background_light";
        var camelPrefix = background.Value.Key == "dark" ? "backgroundDark" : "backgroundLight";
        await _repo.SetAsync("site_settings", "branding", new Dictionary<string, object?>
        {
            [$"{settingPrefix}_url"] = backgroundUrl,
            [$"{camelPrefix}Url"] = backgroundUrl,
            [$"{settingPrefix}_version"] = version,
            [$"{camelPrefix}Version"] = version,
            ["updated_by"] = access.userId,
            ["updatedBy"] = access.userId,
            ["updated_at"] = now,
            ["updatedAt"] = now
        }, merge: true);

        return Ok(new
        {
            success = true,
            message = $"Đã cập nhật ảnh {background.Value.Label} trên Supabase Storage.",
            data = new
            {
                theme = background.Value.Key,
                backgroundUrl,
                version
            }
        });
    }
    [HttpGet("chatbot-style")]
    public async Task<IActionResult> GetChatbotStyle()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var configuration = await _chatbotSettings.GetConfigurationAsync();
        return Ok(new
        {
            success = true,
            data = new
            {
                chatbotName = configuration.ChatbotName,
                defaultStyleId = configuration.DefaultStyleId,
                styles = configuration.Styles.Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    prompt = item.Prompt,
                    price = item.Price,
                    isFree = item.IsFree,
                    maxResponseWords = item.MaxResponseWords
                }),
                limits = new
                {
                    chatbotName = ChatbotSettingsService.MaxChatbotNameLength,
                    styleCount = ChatbotSettingsService.MaxStyleCount,
                    styleName = ChatbotSettingsService.MaxStyleNameLength,
                    stylePrompt = ChatbotSettingsService.MaxStylePromptLength,
                    minResponseWords = ChatbotSettingsService.MinResponseWords,
                    maxResponseWords = ChatbotSettingsService.MaxResponseWords
                }
            }
        });
    }

    [HttpPut("chatbot-style")]
    public async Task<IActionResult> UpdateChatbotStyle([FromBody] AdminChatbotSettingsRequest request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        if (request is null)
        {
            return BadRequest(new { success = false, message = "Thiếu cấu hình chatbot." });
        }

        var chatbotName = (request.ChatbotName ?? string.Empty).Trim();
        if (chatbotName.Length > ChatbotSettingsService.MaxChatbotNameLength)
        {
            return BadRequest(new { success = false, message = $"Tên chatbot tối đa {ChatbotSettingsService.MaxChatbotNameLength} ký tự." });
        }

        var requestedStyles = request.Styles ?? new List<AdminChatbotStyleItemRequest>();
        if (requestedStyles.Count > ChatbotSettingsService.MaxStyleCount)
        {
            return BadRequest(new { success = false, message = $"Chỉ được tạo tối đa {ChatbotSettingsService.MaxStyleCount} phong cách." });
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var styles = new List<ChatbotConversationStyle>();
        foreach (var item in requestedStyles)
        {
            var name = (item.Name ?? string.Empty).Trim();
            var prompt = (item.Prompt ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { success = false, message = "Mỗi phong cách phải có tên." });
            }
            if (name.Length > ChatbotSettingsService.MaxStyleNameLength)
            {
                return BadRequest(new { success = false, message = $"Tên phong cách tối đa {ChatbotSettingsService.MaxStyleNameLength} ký tự." });
            }
            if (prompt.Length > ChatbotSettingsService.MaxStylePromptLength)
            {
                return BadRequest(new { success = false, message = $"Nội dung mỗi phong cách tối đa {ChatbotSettingsService.MaxStylePromptLength} ký tự." });
            }

            var id = ChatbotSettingsService.CreateStyleId(item.Id, name, usedIds);
            var isFree = ChatbotSettingsService.IsFreeStyle(id);
            var price = isFree ? 0m : Math.Clamp(item.Price.GetValueOrDefault(ChatbotSettingsService.DefaultPaidStylePrice), 1000m, ChatbotSettingsService.MaxStylePrice);
            var maxResponseWords = Math.Clamp(
                item.MaxResponseWords.GetValueOrDefault(ChatbotSettingsService.DefaultResponseWords),
                ChatbotSettingsService.MinResponseWords,
                ChatbotSettingsService.MaxResponseWords);
            styles.Add(new ChatbotConversationStyle(id, name, prompt, price, isFree, maxResponseWords));
        }

        var configuration = await _chatbotSettings.SaveConfigurationAsync(
            chatbotName,
            styles,
            request.DefaultStyleId,
            access.userId!);

        return Ok(new
        {
            success = true,
            message = "Đã lưu tên chatbot và danh sách phong cách nói chuyện.",
            data = new
            {
                chatbotName = configuration.ChatbotName,
                defaultStyleId = configuration.DefaultStyleId,
                styles = configuration.Styles.Select(item => new { id = item.Id, name = item.Name, prompt = item.Prompt, price = item.Price, isFree = item.IsFree, maxResponseWords = item.MaxResponseWords })
            }
        });
    }

    [HttpGet("sales-level-settings")]
    public async Task<IActionResult> GetSalesLevelSettings()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var settings = await GetSalesLevelSettingsAsync();
        return Ok(new
        {
            success = true,
            data = settings.Select(ToSalesLevelResponse).ToList()
        });
    }

    [HttpPut("sales-level-settings")]
    public async Task<IActionResult> UpdateSalesLevelSettings([FromBody] AdminSalesLevelSettingsRequest request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var incoming = request.Levels ?? new List<AdminSalesLevelSettingRequest>();
        var levels = NormalizeSalesLevelSettings(incoming.Select(item => new SalesLevelSetting(
            ClampSalesLevel(item.Level),
            NormalizePercent(item.CommissionPercent ?? DefaultSalesLevelSetting(ClampSalesLevel(item.Level)).CommissionPercent, DefaultSalesLevelSetting(ClampSalesLevel(item.Level)).CommissionPercent),
            NormalizePercent(item.OfferDiscountPercent ?? DefaultSalesLevelSetting(ClampSalesLevel(item.Level)).OfferDiscountPercent),
            NormalizePercent(item.ServicePercent ?? DefaultSalesLevelSetting(ClampSalesLevel(item.Level)).ServicePercent)
        )));

        await _repo.SetAsync(SalesLevelSettingsCollection, SalesLevelSettingsDocumentId, new Dictionary<string, object?>
        {
            ["levels"] = levels.Select(ToSalesLevelDictionary).ToList(),
            ["updated_at"] = DateTime.UtcNow
        }, merge: false);

        return Ok(new
        {
            success = true,
            message = "Đã lưu ưu đãi từng cấp",
            data = levels.Select(ToSalesLevelResponse).ToList()
        });
    }

    [HttpPut("accounts/{id}")]
    public async Task<IActionResult> UpdateAccount(string id, [FromBody] AdminAccountUpdateRequest request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var account = await ReadAccountAsync(id);
        if (account is null) return NotFound(new { success = false, message = "Không tìm thấy tài khoản" });

        var email = Text(account, "email").ToLowerInvariant();
        var isProtectedAdmin = IsProtectedAdmin(account);
        var currentRole = NormalizeRole(Text(account, "role")) ?? "Free";
        var role = NormalizeRole(string.IsNullOrWhiteSpace(request.Role) ? currentRole : request.Role);
        var baseUsername = StripRolePrefix(string.IsNullOrWhiteSpace(request.Username) ? Text(account, "username") : request.Username.Trim());
        var username = BuildRoleUsername(role ?? "Free", baseUsername);
        var isLocked = request.IsLocked ?? IsTruthy(account.GetValueOrDefault("is_locked"));
        var levelSettings = await GetSalesLevelSettingsAsync();
        var requestedCommissionLevel = ClampSalesLevel(request.CommissionLevel ?? request.SalesLevel ?? TryInt(account.GetValueOrDefault("commission_level")) ?? TryInt(account.GetValueOrDefault("commissionLevel")) ?? TryInt(account.GetValueOrDefault("sales_level")) ?? TryInt(account.GetValueOrDefault("salesLevel")) ?? 1);
        var requestedOfferLevel = ClampSalesLevel(request.OfferLevel ?? TryInt(account.GetValueOrDefault("offer_level")) ?? TryInt(account.GetValueOrDefault("offerLevel")) ?? requestedCommissionLevel);
        var requestedServiceLevel = ClampSalesLevel(request.ServiceLevel ?? TryInt(account.GetValueOrDefault("service_level")) ?? TryInt(account.GetValueOrDefault("serviceLevel")) ?? 1);
        var selectedCommissionSetting = GetSalesLevelSetting(levelSettings, requestedCommissionLevel);
        var selectedOfferSetting = GetSalesLevelSetting(levelSettings, requestedOfferLevel);
        var selectedServiceSetting = GetSalesLevelSetting(levelSettings, requestedServiceLevel);
        var offerDiscountPercent = NormalizePercent(request.OfferDiscountPercent ?? selectedOfferSetting.OfferDiscountPercent);
        var servicePercent = NormalizePercent(request.ServicePercent ?? selectedServiceSetting.ServicePercent);
        var commissionPercent = NormalizePercent(request.CommissionPercent ?? selectedCommissionSetting.CommissionPercent, selectedCommissionSetting.CommissionPercent);
        var commissionManualOverride = true;

        if (role is null) return BadRequest(new { success = false, message = "Vai trò không hợp lệ. Chỉ dùng Free, VIP, Premium, Admin, Sales hoặc Company." });

        var userDoc = await _repo.GetByIdAsync("users", id) ?? new Dictionary<string, object?>();
        var currentPlanExpiresAt = ParseUtcDate(TextAny(userDoc, "plan_expires_at", "planExpiresAt"));
        var requestedPlanExpiresAt = request.PlanExpiresAt?.UtcDateTime;
        var roleChanged = !string.Equals(currentRole, role, StringComparison.OrdinalIgnoreCase);
        var timedRole = RoleRequiresPlanExpiry(role);
        if (timedRole && roleChanged && requestedPlanExpiresAt is null)
        {
            return BadRequest(new { success = false, message = "Vui lòng chọn hạn gói khi đổi vai trò." });
        }
        if (timedRole && requestedPlanExpiresAt is not null && requestedPlanExpiresAt.Value <= DateTime.UtcNow)
        {
            return BadRequest(new { success = false, message = "Hạn gói phải lớn hơn thời điểm hiện tại." });
        }
        if (timedRole && requestedPlanExpiresAt is null && currentPlanExpiresAt is null)
        {
            return BadRequest(new { success = false, message = "Vai trò này phải có hạn gói." });
        }
        var effectivePlanExpiresAt = timedRole ? requestedPlanExpiresAt ?? currentPlanExpiresAt : null;
        var expiryChanged = !SameMinute(currentPlanExpiresAt, effectivePlanExpiresAt);
        var planChanged = roleChanged || expiryChanged;

        if (role == "Sales")
        {
            servicePercent = 0m;
        }
        else
        {
            commissionPercent = 0m;
            commissionManualOverride = false;
        }

        if (role != "Company")
        {
            servicePercent = 0m;
        }

        if (isProtectedAdmin)
        {
            role = "Admin";
            isLocked = false;
            username = BuildRoleUsername("Admin", StripRolePrefix(username));
            offerDiscountPercent = 0m;
            servicePercent = 0m;
            commissionPercent = 0m;
            commissionManualOverride = false;
        }
        else if (role == "Admin")
        {
            var currentIsAdmin = IsRole(account.GetValueOrDefault("role"), "Admin");
            if (!currentIsAdmin)
            {
                var accounts = await ReadAccountsAsync();
                var adminCount = accounts.Count(a => IsRole(a.GetValueOrDefault("role"), "Admin"));
                if (adminCount >= MaxAdminAccounts)
                {
                    return BadRequest(new { success = false, message = $"Chỉ được tối đa {MaxAdminAccounts} tài khoản Admin." });
                }
            }
        }

        if (!isProtectedAdmin && planChanged)
        {
            var now = DateTime.UtcNow;
            await EndActivePlanOrdersAsync(id, access.userId!, now);
            if (timedRole && effectivePlanExpiresAt is not null)
            {
                await SaveManualPlanOrderAsync(id, role, effectivePlanExpiresAt.Value, access.userId!, now);
            }
        }

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            update app_users_auth
            set username = @username,
                role = @role,
                is_locked = @is_locked,
                updated_at = now()
            where id = @id;
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("username", username);
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("is_locked", isLocked);
        await cmd.ExecuteNonQueryAsync();

        await _repo.SetAsync("users", id, new Dictionary<string, object?>
        {
            ["id"] = id,
            ["uid"] = id,
            ["email"] = email,
            ["username"] = username,
            ["displayName"] = username,
            ["role"] = role,
            ["offer_discount_percent"] = offerDiscountPercent,
            ["offerDiscountPercent"] = offerDiscountPercent,
            ["admin_offer_discount_percent"] = offerDiscountPercent,
            ["adminOfferDiscountPercent"] = offerDiscountPercent,
            ["admin_offer_override"] = true,
            ["adminOfferOverride"] = true,
            ["commission_percent"] = commissionPercent,
            ["commissionPercent"] = commissionPercent,
            ["commission_manual_override"] = commissionManualOverride,
            ["commissionManualOverride"] = commissionManualOverride,
            ["sales_level"] = requestedCommissionLevel,
            ["salesLevel"] = requestedCommissionLevel,
            ["commission_level"] = requestedCommissionLevel,
            ["commissionLevel"] = requestedCommissionLevel,
            ["offer_level"] = requestedOfferLevel,
            ["offerLevel"] = requestedOfferLevel,
            ["service_level"] = requestedServiceLevel,
            ["serviceLevel"] = requestedServiceLevel,
            ["sales_level_manual_override"] = role == "Sales",
            ["salesLevelManualOverride"] = role == "Sales",
            ["service_fee_percent"] = servicePercent,
            ["serviceFeePercent"] = servicePercent,
            ["service_percent"] = servicePercent,
            ["servicePercent"] = servicePercent,
            ["is_locked"] = isLocked,
            ["isLocked"] = isLocked,
            ["is_protected"] = isProtectedAdmin,
            ["isProtected"] = isProtectedAdmin,
            ["is_active"] = !isLocked,
            ["updated_at"] = DateTime.UtcNow
        }, merge: true);

        if (!isProtectedAdmin && planChanged)
        {
            if (timedRole)
            {
                await _planQueueService.SyncUserAsync(id, role);
                await MarkManualPlanOrderActivatedAsync($"admin-plan-{id}", role);
            }
            else
            {
                await ClearUserPlanStateAsync(id, role);
            }
        }

        await SyncTourSalesNameAsync(id, username);
        await SyncPostAuthorNameAsync(id, username);

        return Ok(new { success = true, message = "Đã cập nhật tài khoản" });
    }

    private async Task EndActivePlanOrdersAsync(string userId, string adminUserId, DateTime now)
    {
        var orders = await _repo.WhereEqualAsync("plan_orders", "buyer_id", userId, limit: 500);
        foreach (var order in orders)
        {
            if (!string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase)) continue;
            var orderId = TextAny(order, "id", "Id");
            if (string.IsNullOrWhiteSpace(orderId)) continue;
            await _repo.UpdateAsync("plan_orders", orderId, new Dictionary<string, object?>
            {
                ["status"] = "Đã thay đổi",
                ["ended_at"] = now,
                ["endedAt"] = now,
                ["ended_by"] = adminUserId,
                ["endedBy"] = adminUserId,
                ["updated_at"] = now
            });
        }
    }

    private async Task SaveManualPlanOrderAsync(string userId, string role, DateTime expiresAt, string adminUserId, DateTime now)
    {
        var orderId = $"admin-plan-{userId}";
        var durationMonths = Math.Max(1, (int)Math.Ceiling((expiresAt - now).TotalDays / 30.4375d));
        await _repo.SetAsync("plan_orders", orderId, new Dictionary<string, object?>
        {
            ["id"] = orderId,
            ["buyer_id"] = userId,
            ["buyerId"] = userId,
            ["plan_role"] = role,
            ["planRole"] = role,
            ["status"] = "Đã bán",
            ["duration_months"] = durationMonths,
            ["durationMonths"] = durationMonths,
            ["plan_started_at"] = now,
            ["planStartedAt"] = now,
            ["plan_expires_at"] = expiresAt,
            ["planExpiresAt"] = expiresAt,
            ["price_amount"] = 0,
            ["priceAmount"] = 0,
            ["source"] = "admin",
            ["managed_by"] = adminUserId,
            ["managedBy"] = adminUserId,
            ["sold_by"] = adminUserId,
            ["soldBy"] = adminUserId,
            ["sold_at"] = now,
            ["created_at"] = now,
            ["updated_at"] = now
        }, merge: false);
    }

    private async Task MarkManualPlanOrderActivatedAsync(string orderId, string role)
    {
        var now = DateTime.UtcNow;
        await _repo.UpdateAsync("plan_orders", orderId, new Dictionary<string, object?>
        {
            ["payment_status"] = "Đã thanh toán",
            ["paymentStatus"] = "Đã thanh toán",
            ["benefits_applied"] = true,
            ["benefitsApplied"] = true,
            ["activation_status"] = "activated",
            ["activationStatus"] = "activated",
            ["benefit_type"] = "account_plan",
            ["benefitType"] = "account_plan",
            ["benefit_value"] = role,
            ["benefitValue"] = role,
            ["benefits_applied_at"] = now,
            ["benefitsAppliedAt"] = now,
            ["updated_at"] = now
        });
    }

    private async Task ClearUserPlanStateAsync(string userId, string role)
    {
        var now = DateTime.UtcNow;
        await _repo.SetAsync("users", userId, new Dictionary<string, object?>
        {
            ["id"] = userId,
            ["uid"] = userId,
            ["role"] = role,
            ["plan_role"] = role,
            ["planRole"] = role,
            ["plan_started_at"] = string.Empty,
            ["planStartedAt"] = string.Empty,
            ["plan_expires_at"] = string.Empty,
            ["planExpiresAt"] = string.Empty,
            ["plan_last_order_id"] = string.Empty,
            ["planLastOrderId"] = string.Empty,
            ["next_plan_role"] = string.Empty,
            ["nextPlanRole"] = string.Empty,
            ["next_plan_started_at"] = string.Empty,
            ["nextPlanStartedAt"] = string.Empty,
            ["next_plan_expires_at"] = string.Empty,
            ["nextPlanExpiresAt"] = string.Empty,
            ["next_plan_order_id"] = string.Empty,
            ["nextPlanOrderId"] = string.Empty,
            ["plan_countdown_seconds"] = 0,
            ["planCountdownSeconds"] = 0,
            ["updated_at"] = now
        }, merge: true);
    }

    private static bool RoleRequiresPlanExpiry(string role)
        => role is "VIP" or "Premium" or "Sales" or "Company";

    private static DateTime? ParseUtcDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTimeOffset.TryParse(value, out var parsed)) return null;
        return parsed.UtcDateTime;
    }

    private static bool SameMinute(DateTime? left, DateTime? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return Math.Abs((left.Value - right.Value).TotalSeconds) < 60;
    }

    private async Task SyncTourSalesNameAsync(string userId, string username)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(username)) return;
        var tours = await _repo.GetAllAsync("tours", limit: 300);
        foreach (var tour in tours)
        {
            var ownerId = Text(tour, "created_by");
            if (string.IsNullOrWhiteSpace(ownerId)) ownerId = Text(tour, "createdBy");
            if (string.IsNullOrWhiteSpace(ownerId)) ownerId = Text(tour, "tour_sales_id");
            if (string.IsNullOrWhiteSpace(ownerId)) ownerId = Text(tour, "tourSalesId");
            if (!string.Equals(ownerId, userId, StringComparison.Ordinal)) continue;
            if (IsTruthy(tour.GetValueOrDefault("tour_sales_manual_name")) || IsTruthy(tour.GetValueOrDefault("tourSalesManualName"))) continue;

            var tourId = Text(tour, "id");
            if (string.IsNullOrWhiteSpace(tourId)) continue;
            await _repo.UpdateAsync("tours", tourId, new Dictionary<string, object?>
            {
                ["tour_sales_name"] = username,
                ["tourSalesName"] = username,
                ["updated_at"] = DateTime.UtcNow
            });
        }
    }

    private async Task SyncPostAuthorNameAsync(string userId, string username)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(username)) return;
        var posts = await _repo.GetAllAsync("travel_posts", limit: 500);
        foreach (var post in posts)
        {
            var authorId = Text(post, "author_id");
            if (string.IsNullOrWhiteSpace(authorId)) authorId = Text(post, "authorId");
            if (!string.Equals(authorId, userId, StringComparison.Ordinal)) continue;

            var postId = Text(post, "id");
            if (string.IsNullOrWhiteSpace(postId)) continue;
            await _repo.UpdateAsync("travel_posts", postId, new Dictionary<string, object?>
            {
                ["author_name"] = username,
                ["authorName"] = username,
                ["updated_at"] = DateTime.UtcNow
            });
        }
    }

    private async Task<(string id, string name)?> GetTransferAdminAccountAsync(string deletedUserId)
    {
        var accounts = await ReadAccountsAsync();
        var admin = accounts.FirstOrDefault(account =>
                !string.Equals(Text(account, "id"), deletedUserId, StringComparison.Ordinal)
                && IsProtectedAdmin(account))
            ?? accounts.FirstOrDefault(account =>
                !string.Equals(Text(account, "id"), deletedUserId, StringComparison.Ordinal)
                && IsRole(account.GetValueOrDefault("role"), "Admin"));

        if (admin is null) return null;
        var adminId = Text(admin, "id");
        if (string.IsNullOrWhiteSpace(adminId)) return null;

        var adminName = CleanTransferAdminName(Text(admin, "username"));
        if (string.IsNullOrWhiteSpace(adminName)) adminName = CleanTransferAdminName(Text(admin, "email"));
        if (string.IsNullOrWhiteSpace(adminName)) adminName = "Admin";
        return (adminId, adminName);
    }

    private async Task TransferAccountContentToAdminAsync(string deletedUserId, string adminId, string adminName)
    {
        await ReassignToursToAdminAsync(deletedUserId, adminId, adminName);
        await ReassignTourOrdersToAdminAsync(deletedUserId, adminId, adminName);
        await ReassignPostsToAdminAsync(deletedUserId, adminId, adminName);
    }

    private async Task ReassignToursToAdminAsync(string deletedUserId, string adminId, string adminName)
    {
        var tours = await _repo.GetAllAsync("tours");
        foreach (var tour in tours)
        {
            if (!MatchesAnyId(tour, deletedUserId, "created_by", "createdBy", "tour_sales_id", "tourSalesId", "seller_id", "sellerId")) continue;

            var tourId = Text(tour, "id");
            if (string.IsNullOrWhiteSpace(tourId)) continue;

            await _repo.UpdateAsync("tours", tourId, new Dictionary<string, object?>
            {
                ["created_by"] = adminId,
                ["createdBy"] = adminId,
                ["tour_sales_id"] = adminId,
                ["tourSalesId"] = adminId,
                ["seller_id"] = adminId,
                ["sellerId"] = adminId,
                ["tour_sales_name"] = adminName,
                ["tourSalesName"] = adminName,
                ["sales_name"] = adminName,
                ["salesName"] = adminName,
                ["seller_name"] = adminName,
                ["sellerName"] = adminName,
                ["tour_sales_manual_name"] = false,
                ["tourSalesManualName"] = false,
                ["updated_at"] = DateTime.UtcNow
            });
        }
    }

    private async Task ReassignTourOrdersToAdminAsync(string deletedUserId, string adminId, string adminName)
    {
        var orders = await _repo.GetAllAsync("tour_orders");
        foreach (var order in orders)
        {
            if (!MatchesAnyId(order, deletedUserId, "tour_sales_id", "tourSalesId", "seller_id", "sellerId", "created_by", "createdBy")) continue;

            var orderId = Text(order, "id");
            if (string.IsNullOrWhiteSpace(orderId)) continue;

            await _repo.UpdateAsync("tour_orders", orderId, new Dictionary<string, object?>
            {
                ["tour_sales_id"] = adminId,
                ["tourSalesId"] = adminId,
                ["seller_id"] = adminId,
                ["sellerId"] = adminId,
                ["created_by"] = adminId,
                ["createdBy"] = adminId,
                ["tour_sales_name"] = adminName,
                ["tourSalesName"] = adminName,
                ["sales_name"] = adminName,
                ["salesName"] = adminName,
                ["seller_name"] = adminName,
                ["sellerName"] = adminName,
                ["updated_at"] = DateTime.UtcNow
            });
        }
    }

    private async Task ReassignPostsToAdminAsync(string deletedUserId, string adminId, string adminName)
    {
        var posts = await _repo.GetAllAsync("travel_posts");
        foreach (var post in posts)
        {
            if (!MatchesAnyId(post, deletedUserId, "author_id", "authorId", "owner_id", "ownerId", "created_by", "createdBy")) continue;

            var postId = Text(post, "id");
            if (string.IsNullOrWhiteSpace(postId)) continue;

            await _repo.UpdateAsync("travel_posts", postId, new Dictionary<string, object?>
            {
                ["author_id"] = adminId,
                ["authorId"] = adminId,
                ["owner_id"] = adminId,
                ["ownerId"] = adminId,
                ["created_by"] = adminId,
                ["createdBy"] = adminId,
                ["author_name"] = adminName,
                ["authorName"] = adminName,
                ["updated_at"] = DateTime.UtcNow
            });
        }
    }

    private static bool MatchesAnyId(Dictionary<string, object?> row, string expectedId, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(expectedId)) return false;
        return keys.Any(key => string.Equals(Text(row, key), expectedId, StringComparison.Ordinal));
    }

    private static string CleanTransferAdminName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.StartsWith("Tài khoản ", StringComparison.OrdinalIgnoreCase)) name = name[10..].Trim();
        return name;
    }

    [HttpDelete("accounts/{id}")]
    public async Task<IActionResult> DeleteAccount(string id)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var account = await ReadAccountAsync(id);
        if (account is null) return NotFound(new { success = false, message = "Không tìm thấy tài khoản" });
        if (IsProtectedAdmin(account)) return BadRequest(new { success = false, message = "Không thể xóa tài khoản quản trị hệ thống." });

        var transferAdmin = await GetTransferAdminAccountAsync(id);
        if (transferAdmin is null)
        {
            return BadRequest(new { success = false, message = "Không tìm thấy tài khoản Admin để nhận tour và bài viết." });
        }

        await TransferAccountContentToAdminAsync(id, transferAdmin.Value.id, transferAdmin.Value.name);
        var deletedOffers = await _offerService.DeleteOffersForDeletedAccountAsync(id, Text(account, "email"));

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "delete from app_users_auth where id = @id;";
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();

        await _repo.DeleteAsync("users", id);
        return Ok(new
        {
            success = true,
            message = deletedOffers > 0
            ? "Đã xóa tài khoản, ưu đãi liên quan đã xoá, tour và bài viết đã chuyển sang Admin"
            : "Đã xóa tài khoản, tour và bài viết đã chuyển sang Admin"
        });
    }

    [HttpGet("schedules")]
    public async Task<IActionResult> Schedules()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        var schedules = await _repo.GetAllFieldsAsync("schedules", ScheduleListFields, limit: 500);
        await AttachScheduleCreatorNamesAsync(schedules);
        return Ok(new { success = true, data = schedules });
    }

    [HttpDelete("schedules/{id}")]
    public async Task<IActionResult> DeleteSchedule(string id)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        var ok = await _repo.DeleteAsync("schedules", id);
        return ok
            ? Ok(new { success = true, message = "Đã xóa lịch trình" })
            : NotFound(new { success = false, message = "Không tìm thấy lịch trình" });
    }

    [HttpGet("plan-status-options")]
    public async Task<IActionResult> PlanStatusOptions()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        var travelTags = await GetTravelTagsAsync();
        return Ok(new { success = true, data = await GetPlanStatusOptionsAsync(includeDisabled: true), allowed_tags = GetAllowedTagNames(travelTags), travel_tags = travelTags });
    }

    [HttpPut("plan-status-options/{key}")]
    public async Task<IActionResult> UpdatePlanStatusOption(string key, [FromBody] AdminPlanStatusOptionRequest request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var normalizedKey = PlanCatalog.NormalizeKey(string.IsNullOrWhiteSpace(request.Key) ? key : request.Key);
        if (string.IsNullOrWhiteSpace(normalizedKey)) return BadRequest(new { success = false, message = "Mã trạng thái không hợp lệ" });

        var data = new Dictionary<string, object?>
        {
            ["id"] = normalizedKey,
            ["key"] = normalizedKey,
            ["label"] = string.IsNullOrWhiteSpace(request.Label) ? normalizedKey : request.Label.Trim(),
            ["description"] = request.Description?.Trim() ?? string.Empty,
            ["tags"] = PlanCatalog.CleanTags(request.Tags),
            ["match_all"] = request.MatchAll,
            ["enabled"] = request.Enabled,
            ["order"] = request.Order,
            ["color"] = PlanCatalog.ResolveStatusColor(normalizedKey, request.Color, request.Tags),
            ["updated_at"] = DateTime.UtcNow
        };

        await _repo.SetAsync(PlanStatusOptionsCollection, normalizedKey, data, merge: false);
        return Ok(new { success = true, data, message = "Đã cập nhật trạng thái kế hoạch" });
    }

    [HttpDelete("plan-status-options/{key}")]
    public async Task<IActionResult> DisablePlanStatusOption(string key)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var normalizedKey = PlanCatalog.NormalizeKey(key);
        var existing = (await GetPlanStatusOptionsAsync(includeDisabled: true))
            .FirstOrDefault(item => string.Equals(item.GetValueOrDefault("key")?.ToString(), normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return NotFound(new { success = false, message = "Không tìm thấy trạng thái" });
        existing["enabled"] = false;
        existing["updated_at"] = DateTime.UtcNow;
        await _repo.SetAsync(PlanStatusOptionsCollection, normalizedKey, existing, merge: false);
        return Ok(new { success = true, message = "Đã ẩn trạng thái kế hoạch" });
    }

    [HttpGet("province-tags")]
    public async Task<IActionResult> ProvinceTags()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        var travelTags = await GetTravelTagsAsync();
        return Ok(new { success = true, data = await GetProvinceTagsAsync(), allowed_tags = GetAllowedTagNames(travelTags), travel_tags = travelTags });
    }

    [HttpPost("travel-tags")]
    public async Task<IActionResult> CreateTravelTag([FromBody] AdminTravelTagRequest request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { success = false, message = "Bạn chưa nhập tên tag" });

        var normalizedName = PlanCatalog.NormalizeTag(name);
        var documentId = PlanCatalog.NormalizeKey(normalizedName);
        if (string.IsNullOrWhiteSpace(documentId)) return BadRequest(new { success = false, message = "Tên tag không hợp lệ" });

        var color = PlanCatalog.NormalizeColor(request.Color) ?? PlanCatalog.GetDefaultTagColor(normalizedName);
        var data = new Dictionary<string, object?>
        {
            ["id"] = documentId,
            ["name"] = normalizedName,
            ["label"] = normalizedName,
            ["color"] = color,
            ["enabled"] = true,
            ["updated_at"] = DateTime.UtcNow
        };

        await _repo.SetAsync(PlanTravelTagsCollection, documentId, data, merge: false);
        return Ok(new { success = true, data, message = "Đã thêm tag" });
    }

    [HttpDelete("travel-tags/{name}")]
    public async Task<IActionResult> DeleteTravelTag(string name)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var normalizedName = PlanCatalog.NormalizeTag(name);
        var documentId = PlanCatalog.NormalizeKey(normalizedName);
        if (string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(normalizedName))
        {
            return BadRequest(new { success = false, message = "Tên tag không hợp lệ" });
        }

        var existingTags = await GetTravelTagsAsync();
        var existing = existingTags.FirstOrDefault(item =>
            string.Equals(PlanCatalog.NormalizeKey(item.GetValueOrDefault("name")?.ToString()), documentId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PlanCatalog.NormalizeKey(item.GetValueOrDefault("label")?.ToString()), documentId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PlanCatalog.NormalizeKey(item.GetValueOrDefault("id")?.ToString()), documentId, StringComparison.OrdinalIgnoreCase));

        if (existing is null) return NotFound(new { success = false, message = "Không tìm thấy tag" });

        var disabledTag = new Dictionary<string, object?>
        {
            ["id"] = documentId,
            ["name"] = normalizedName,
            ["label"] = normalizedName,
            ["color"] = PlanCatalog.NormalizeColor(existing.GetValueOrDefault("color")?.ToString()) ?? PlanCatalog.GetDefaultTagColor(normalizedName),
            ["enabled"] = false,
            ["updated_at"] = DateTime.UtcNow
        };
        await _repo.SetAsync(PlanTravelTagsCollection, documentId, disabledTag, merge: false);

        var updatedStatuses = 0;
        var statuses = await GetPlanStatusOptionsAsync(includeDisabled: true);
        foreach (var status in statuses)
        {
            var tags = PlanCatalog.CleanTags(PlanCatalog.ToStringList(status.GetValueOrDefault("tags")));
            var cleanedTags = tags.Where(tag => !string.Equals(PlanCatalog.NormalizeKey(tag), documentId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (cleanedTags.Count == tags.Count) continue;

            var key = status.GetValueOrDefault("key")?.ToString() ?? status.GetValueOrDefault("id")?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)) continue;

            status["id"] = key;
            status["key"] = key;
            status["tags"] = cleanedTags;
            status["updated_at"] = DateTime.UtcNow;
            await _repo.SetAsync(PlanStatusOptionsCollection, key, status, merge: false);
            updatedStatuses++;
        }

        var updatedProvinces = 0;
        var provinces = await GetProvinceTagsAsync();
        foreach (var province in provinces)
        {
            var tags = PlanCatalog.CleanTags(PlanCatalog.ToStringList(province.GetValueOrDefault("tags")));
            var cleanedTags = tags.Where(tag => !string.Equals(PlanCatalog.NormalizeKey(tag), documentId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (cleanedTags.Count == tags.Count) continue;

            var provinceId = province.GetValueOrDefault("id")?.ToString()
                ?? province.GetValueOrDefault("province_id")?.ToString()
                ?? province.GetValueOrDefault("name")?.ToString()
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(provinceId)) continue;

            province["id"] = provinceId;
            province["tags"] = cleanedTags;
            province["updated_at"] = DateTime.UtcNow;
            await _repo.SetAsync(ProvinceTravelTagsCollection, provinceId, province, merge: false);
            updatedProvinces++;
        }

        return Ok(new { success = true, message = "Đã xoá tag", updated_statuses = updatedStatuses, updated_provinces = updatedProvinces });
    }

    [HttpPut("province-tags/{id}")]
    public async Task<IActionResult> UpdateProvinceTags(string id, [FromBody] AdminProvinceTagsRequest request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var provinceId = string.IsNullOrWhiteSpace(request.Id) ? id : request.Id.Trim();
        if (string.IsNullOrWhiteSpace(provinceId)) return BadRequest(new { success = false, message = "Mã tỉnh/thành không hợp lệ" });

        var current = (await GetProvinceTagsAsync()).FirstOrDefault(item =>
            string.Equals(item.GetValueOrDefault("id")?.ToString(), provinceId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.GetValueOrDefault("province_id")?.ToString(), provinceId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.GetValueOrDefault("name")?.ToString(), provinceId, StringComparison.OrdinalIgnoreCase));

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? current?.GetValueOrDefault("name")?.ToString() ?? provinceId
            : request.Name.Trim();
        var documentId = current?.GetValueOrDefault("id")?.ToString() ?? provinceId;

        var data = new Dictionary<string, object?>
        {
            ["id"] = documentId,
            ["province_id"] = TryInt(request.ProvinceId) ?? TryInt(current?.GetValueOrDefault("province_id")) ?? TryInt(documentId) ?? 999,
            ["name"] = name,
            ["province_name"] = name,
            ["area"] = request.Area?.Trim() ?? current?.GetValueOrDefault("area")?.ToString() ?? string.Empty,
            ["region"] = request.Region?.Trim() ?? current?.GetValueOrDefault("region")?.ToString() ?? string.Empty,
            ["tags"] = PlanCatalog.CleanTags(request.Tags),
            ["description"] = request.Description?.Trim() ?? current?.GetValueOrDefault("description")?.ToString() ?? string.Empty,
            ["updated_at"] = DateTime.UtcNow
        };

        await _repo.SetAsync(ProvinceTravelTagsCollection, documentId, data, merge: false);
        return Ok(new { success = true, data, message = "Đã cập nhật tag và thông tin tỉnh thành" });
    }

    private async Task<List<Dictionary<string, object?>>> GetTravelTagsAsync()
    {
        var defaults = PlanCatalog.AllowedTags.Select((tag, index) => new Dictionary<string, object?>
        {
            ["id"] = PlanCatalog.NormalizeKey(tag),
            ["name"] = tag,
            ["label"] = tag,
            ["color"] = PlanCatalog.GetDefaultTagColor(tag),
            ["enabled"] = true,
            ["order"] = index
        }).ToDictionary(item => item["id"]?.ToString() ?? string.Empty, item => item, StringComparer.OrdinalIgnoreCase);

        var saved = await _repo.GetAllAsync(PlanTravelTagsCollection, limit: 100);
        foreach (var item in saved)
        {
            var name = item.GetValueOrDefault("name")?.ToString()
                ?? item.GetValueOrDefault("label")?.ToString()
                ?? item.GetValueOrDefault("id")?.ToString()
                ?? string.Empty;
            name = PlanCatalog.NormalizeTag(name);
            var id = PlanCatalog.NormalizeKey(name);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

            item["id"] = id;
            item["name"] = name;
            item["label"] = name;
            item["color"] = PlanCatalog.NormalizeColor(item.GetValueOrDefault("color")?.ToString()) ?? PlanCatalog.GetDefaultTagColor(name);
            if (!item.ContainsKey("enabled")) item["enabled"] = true;
            defaults[id] = item;
        }

        return defaults.Values
            .Where(item => IsTruthy(item.GetValueOrDefault("enabled")))
            .OrderBy(item => PlanCatalog.GetInt(item, "order", 999))
            .ThenBy(item => item.GetValueOrDefault("name")?.ToString())
            .ToList();
    }

    private static List<string> GetAllowedTagNames(IEnumerable<Dictionary<string, object?>> tags)
    {
        return tags
            .Select(item => item.GetValueOrDefault("name")?.ToString() ?? item.GetValueOrDefault("label")?.ToString() ?? string.Empty)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<Dictionary<string, object?>>> GetPlanStatusOptionsAsync(bool includeDisabled)
    {
        var saved = await _repo.GetAllAsync(PlanStatusOptionsCollection, limit: 100);
        var defaults = PlanCatalog.DefaultStatusOptions();
        var merged = defaults.ToDictionary(item => item.GetValueOrDefault("key")?.ToString() ?? string.Empty, item => item, StringComparer.OrdinalIgnoreCase);

        foreach (var item in saved)
        {
            var key = item.GetValueOrDefault("key")?.ToString() ?? item.GetValueOrDefault("id")?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)) continue;
            item["id"] = key;
            item["key"] = key;
            item["tags"] = PlanCatalog.CleanTags(PlanCatalog.ToStringList(item.GetValueOrDefault("tags")));
            item["color"] = PlanCatalog.ResolveStatusColor(key, item.GetValueOrDefault("color")?.ToString(), PlanCatalog.ToStringList(item.GetValueOrDefault("tags")));
            if (!item.ContainsKey("enabled")) item["enabled"] = true;
            merged[key] = item;
        }

        return merged.Values
            .Where(item => includeDisabled || PlanCatalog.IsEnabled(item))
            .OrderBy(item => PlanCatalog.GetInt(item, "order", 999))
            .ThenBy(item => item.GetValueOrDefault("label")?.ToString())
            .ToList();
    }

    private async Task<List<Dictionary<string, object?>>> GetProvinceTagsAsync()
    {
        var saved = await _repo.GetAllAsync(ProvinceTravelTagsCollection, limit: 100);
        var defaults = PlanCatalog.DefaultProvinceTags();
        var merged = defaults.ToDictionary(item => item.GetValueOrDefault("name")?.ToString() ?? string.Empty, item => item, StringComparer.OrdinalIgnoreCase);

        foreach (var item in saved)
        {
            var name = item.GetValueOrDefault("name")?.ToString() ?? item.GetValueOrDefault("province_name")?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) continue;
            item["name"] = name;
            item["province_name"] = name;
            item["tags"] = PlanCatalog.CleanTags(PlanCatalog.ToStringList(item.GetValueOrDefault("tags")));
            merged[name] = item;
        }

        return merged.Values
            .OrderBy(item => PlanCatalog.GetInt(item, "province_id", 999))
            .ThenBy(item => item.GetValueOrDefault("name")?.ToString())
            .ToList();
    }

    private async Task<(bool ok, string? userId, IActionResult? error)> RequireAdminAsync()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return (false, null, current.error);
        if (!IsRole(current.authUser?.GetValueOrDefault("role"), "Admin"))
        {
            return (false, null, StatusCode(403, new { success = false, message = "Chỉ Admin mới được truy cập." }));
        }
        return (true, current.userId, null);
    }

    private static (string Key, string Label)? NormalizeBackgroundTheme(string? theme)
    {
        var normalized = (theme ?? string.Empty).Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
        return normalized switch
        {
            "light" or "sang" or "nen-sang" or "nền-sáng" => ("light", "nền sáng"),
            "dark" or "night" or "toi" or "tối" or "nen-toi" or "nền-tối" => ("dark", "nền tối"),
            _ => null
        };
    }

    private static string NormalizeOptimizedUploadExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase) ? ".webp" : string.Empty;
    }

    private async Task AttachOfferDiscountsAsync(List<Dictionary<string, object?>> accounts)
    {
        if (accounts.Count == 0) return;

        var levelSettingsTask = GetSalesLevelSettingsAsync();
        var usersTask = _repo.GetAllFieldsAsync("users", AccountUserFields, limit: 5000);
        var ordersTask = _repo.GetAllFieldsAsync("tour_orders", AccountOrderCountFields, limit: 5000);
        var invitesTask = _repo.GetAllFieldsAsync(TourOfferService.InviteCollection, AccountInviteFields, limit: 5000);
        var postOffersTask = _repo.GetAllFieldsAsync(TourOfferService.PostOfferCollection, AccountPostOfferFields, limit: 5000);

        await Task.WhenAll(levelSettingsTask, usersTask, ordersTask, invitesTask, postOffersTask);

        var levelSettings = await levelSettingsTask;
        var userMap = (await usersTask)
            .Where(user => !string.IsNullOrWhiteSpace(Text(user, "id")))
            .GroupBy(user => Text(user, "id"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var salesSoldCounts = BuildSalesSoldCountMap(await ordersTask);
        var inviteDiscounts = BuildInviteDiscountMap(await invitesTask);
        var activePostOfferUsers = BuildActivePostOfferUserSet(await postOffersTask);

        foreach (var account in accounts)
        {
            var id = Text(account, "id");
            var userDoc = !string.IsNullOrWhiteSpace(id) && userMap.TryGetValue(id, out var mappedUser) ? mappedUser : null;
            var automaticDiscount = inviteDiscounts.GetValueOrDefault(id, 0);
            if (activePostOfferUsers.Contains(id) && CanUsePostOffer(account, userDoc)) automaticDiscount += TourOfferService.PostOfferDiscountPercent;
            var salesSoldCount = salesSoldCounts.GetValueOrDefault(id, 0);
            var level = GetUserSalesLevel(userDoc, salesSoldCount);
            var commissionLevel = ClampSalesLevel(TryInt(userDoc?.GetValueOrDefault("commission_level")) ?? TryInt(userDoc?.GetValueOrDefault("commissionLevel")) ?? level.Level);
            var offerLevel = ClampSalesLevel(TryInt(userDoc?.GetValueOrDefault("offer_level")) ?? TryInt(userDoc?.GetValueOrDefault("offerLevel")) ?? level.Level);
            var serviceLevel = ClampSalesLevel(TryInt(userDoc?.GetValueOrDefault("service_level")) ?? TryInt(userDoc?.GetValueOrDefault("serviceLevel")) ?? 1);
            var commissionSetting = GetSalesLevelSetting(levelSettings, commissionLevel);
            var offerSetting = GetSalesLevelSetting(levelSettings, offerLevel);
            var serviceSetting = GetSalesLevelSetting(levelSettings, serviceLevel);
            var isSales = IsSalesRole(account.GetValueOrDefault("role"));
            var discount = isSales
                ? GetUserOfferPercent(userDoc, offerSetting.OfferDiscountPercent)
                : TryGetAdminOfferOverride(userDoc, out var manualDiscount)
                    ? manualDiscount
                    : NormalizePercent(automaticDiscount);
            var commissionPercent = isSales ? GetUserCommissionPercent(userDoc, commissionSetting.CommissionPercent) : GetUserCommissionPercent(userDoc, commissionSetting.CommissionPercent);
            var servicePercent = GetUserServicePercent(userDoc, serviceSetting.ServicePercent);

            account["offer_discount_percent"] = discount;
            account["offerDiscountPercent"] = discount;
            account["automatic_offer_discount_percent"] = automaticDiscount;
            account["automaticOfferDiscountPercent"] = automaticDiscount;
            account["commission_percent"] = commissionPercent;
            account["commissionPercent"] = commissionPercent;
            account["sales_level"] = commissionLevel;
            account["salesLevel"] = commissionLevel;
            account["commission_level"] = commissionLevel;
            account["commissionLevel"] = commissionLevel;
            account["offer_level"] = offerLevel;
            account["offerLevel"] = offerLevel;
            account["service_level"] = serviceLevel;
            account["serviceLevel"] = serviceLevel;
            account["sales_sold_count"] = salesSoldCount;
            account["salesSoldCount"] = salesSoldCount;
            account["service_fee_percent"] = servicePercent;
            account["serviceFeePercent"] = servicePercent;
            account["service_percent"] = servicePercent;
            account["servicePercent"] = servicePercent;
            account["plan_role"] = TextAny(userDoc, "plan_role", "planRole", "role");
            account["planRole"] = account["plan_role"];
            account["plan_started_at"] = TextAny(userDoc, "plan_started_at", "planStartedAt");
            account["planStartedAt"] = account["plan_started_at"];
            account["plan_expires_at"] = TextAny(userDoc, "plan_expires_at", "planExpiresAt");
            account["planExpiresAt"] = account["plan_expires_at"];
            account["next_plan_role"] = TextAny(userDoc, "next_plan_role", "nextPlanRole");
            account["nextPlanRole"] = account["next_plan_role"];
            account["next_plan_started_at"] = TextAny(userDoc, "next_plan_started_at", "nextPlanStartedAt");
            account["nextPlanStartedAt"] = account["next_plan_started_at"];
            account["next_plan_expires_at"] = TextAny(userDoc, "next_plan_expires_at", "nextPlanExpiresAt");
            account["nextPlanExpiresAt"] = account["next_plan_expires_at"];
            account["plan_countdown_seconds"] = TextAny(userDoc, "plan_countdown_seconds", "planCountdownSeconds");
            account["planCountdownSeconds"] = account["plan_countdown_seconds"];
        }
    }

    private static Dictionary<string, int> BuildSalesSoldCountMap(IEnumerable<Dictionary<string, object?>> orders)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var order in orders)
        {
            if (!string.Equals(Text(order, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase)) continue;

            var sellerId = TextAny(order, "tour_sales_id", "tourSalesId", "created_by", "createdBy", "seller_id", "sellerId");
            if (string.IsNullOrWhiteSpace(sellerId)) continue;

            counts[sellerId] = counts.GetValueOrDefault(sellerId, 0) + Math.Max(1, GetInt(order, "quantity"));
        }
        return counts;
    }

    private static Dictionary<string, int> BuildInviteDiscountMap(IEnumerable<Dictionary<string, object?>> invites)
    {
        return invites
            .Where(IsAcceptedTourOfferInvite)
            .GroupBy(invite => Text(invite, "inviter_id"), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(invite => Text(invite, "invited_email").Trim().ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
                    .Take(TourOfferService.MaxInvitesForDiscount)
                    .Count() * TourOfferService.DiscountPerAcceptedInvite,
                StringComparer.Ordinal);
    }

    private static HashSet<string> BuildActivePostOfferUserSet(IEnumerable<Dictionary<string, object?>> postOffers)
    {
        return postOffers
            .Where(IsActivePostOffer)
            .Select(offer => Text(offer, "user_id"))
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool CanUsePostOffer(Dictionary<string, object?> account, Dictionary<string, object?>? user)
    {
        var role = TextAny(user, "role", "userRole");
        if (string.IsNullOrWhiteSpace(role)) role = Text(account, "role");
        if (string.Equals(role, "User", StringComparison.OrdinalIgnoreCase)) role = "Free";
        if (string.Equals(role, "Business", StringComparison.OrdinalIgnoreCase)) role = "Company";
        return !string.Equals(role, "Free", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAcceptedTourOfferInvite(Dictionary<string, object?> invite)
    {
        return string.Equals(Text(invite, "status"), "Đã đăng ký", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Text(invite, "accepted_at"))
            || !string.IsNullOrWhiteSpace(Text(invite, "invited_user_id"));
    }

    private static bool IsActivePostOffer(Dictionary<string, object?> offer)
    {
        var status = Text(offer, "status");
        return string.IsNullOrWhiteSpace(status)
            || string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Đang có", StringComparison.OrdinalIgnoreCase);
    }

    private async Task AttachScheduleCreatorNamesAsync(List<Dictionary<string, object?>> schedules)
    {
        if (schedules.Count == 0) return;

        var accounts = await ReadAccountsAsync();
        var accountMap = accounts
            .Where(account => !string.IsNullOrWhiteSpace(Text(account, "id")))
            .GroupBy(account => Text(account, "id"))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var schedule in schedules)
        {
            var creatorId = Text(schedule, "user_id");
            if (string.IsNullOrWhiteSpace(creatorId)) creatorId = Text(schedule, "created_by_user_id");
            if (string.IsNullOrWhiteSpace(creatorId)) creatorId = Text(schedule, "created_by");

            var creatorName = Text(schedule, "creator_name");
            var creatorEmail = Text(schedule, "creator_email");

            if (!string.IsNullOrWhiteSpace(creatorId) && accountMap.TryGetValue(creatorId, out var account))
            {
                creatorName = Text(account, "username");
                creatorEmail = Text(account, "email");
            }

            if (string.IsNullOrWhiteSpace(creatorName)) creatorName = Text(schedule, "owner_name");
            if (string.IsNullOrWhiteSpace(creatorName)) creatorName = Text(schedule, "ownerName");
            if (string.IsNullOrWhiteSpace(creatorEmail)) creatorEmail = Text(schedule, "owner_email");
            if (string.IsNullOrWhiteSpace(creatorEmail)) creatorEmail = Text(schedule, "ownerEmail");

            var displayName = !string.IsNullOrWhiteSpace(creatorName)
                ? creatorName
                : (!string.IsNullOrWhiteSpace(creatorEmail) ? creatorEmail : creatorId);

            schedule["creator_id"] = creatorId;
            schedule["creatorId"] = creatorId;
            schedule["creator_name"] = displayName;
            schedule["creatorName"] = displayName;
            schedule["owner_name"] = displayName;
            schedule["ownerName"] = displayName;
            if (!string.IsNullOrWhiteSpace(creatorEmail))
            {
                schedule["creator_email"] = creatorEmail;
                schedule["creatorEmail"] = creatorEmail;
            }
        }
    }

    private static object BuildStorageUsagePayload(TravelwAI.Models.Storage.UserImageStorageUsage usage)
    {
        return new
        {
            usedBytes = usage.UsedBytes,
            limitBytes = usage.LimitBytes,
            remainingBytes = usage.RemainingBytes,
            usedPercent = usage.UsedPercent,
            imageCount = usage.ImageCount
        };
    }

    private static object BuildStorageCategoryPayload(TravelwAI.Models.Storage.UserImageStorageCategoryUsage category)
    {
        return new
        {
            category = category.Category,
            usedBytes = category.UsedBytes,
            imageCount = category.ImageCount
        };
    }

    private static object BuildStorageDetailsPayload(
        Dictionary<string, object?> account,
        TravelwAI.Models.Storage.UserImageStorageDetails details)
    {
        return new
        {
            user = new
            {
                id = Text(account, "id"),
                username = Text(account, "username"),
                email = Text(account, "email"),
                role = Text(account, "role")
            },
            usage = BuildStorageUsagePayload(details.Usage),
            categories = details.Categories.Select(BuildStorageCategoryPayload),
            items = details.Items.Select(item => new
            {
                id = item.Id,
                publicUrl = item.PublicUrl,
                storagePath = item.StoragePath,
                folder = item.Folder,
                category = item.Category,
                contentType = item.ContentType,
                fileSize = item.FileSize,
                createdAt = item.CreatedAt.ToString("O")
            })
        };
    }

    private async Task<List<Dictionary<string, object?>>> ReadAccountsAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select id, email, username, role, is_locked, is_protected, created_at, updated_at, last_login_at,
                   (is_online = true and last_seen_at is not null and last_seen_at >= now() - interval '3 minutes') as is_online,
                   last_seen_at,
                   last_logout_at
            from app_users_auth
            order by is_protected desc, created_at desc;
            """;
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(ReadAccountRow(reader));
        }
        return rows;
    }

    private async Task<Dictionary<string, object?>?> ReadAccountAsync(string id)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select id, email, username, role, is_locked, is_protected, created_at, updated_at, last_login_at,
                   (is_online = true and last_seen_at is not null and last_seen_at >= now() - interval '3 minutes') as is_online,
                   last_seen_at,
                   last_logout_at
            from app_users_auth
            where id = @id
            limit 1;
            """;
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return ReadAccountRow(reader);
    }


    private async Task EnsureAccountRolePrefixesAsync(List<Dictionary<string, object?>> accounts)
    {
        foreach (var account in accounts)
        {
            var id = Text(account, "id");
            var normalizedRole = NormalizeRole(Text(account, "role"));
            if (string.IsNullOrWhiteSpace(id) || normalizedRole is null) continue;

            var currentUsername = Text(account, "username");
            var targetUsername = BuildRoleUsername(normalizedRole, currentUsername);
            var roleChanged = !string.Equals(Text(account, "role"), normalizedRole, StringComparison.OrdinalIgnoreCase);
            var nameChanged = !string.Equals(currentUsername, targetUsername, StringComparison.Ordinal);
            if (!roleChanged && !nameChanged) continue;

            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                update app_users_auth
                set username = @username,
                    role = @role,
                    updated_at = now()
                where id = @id;
                """;
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("username", targetUsername);
            cmd.Parameters.AddWithValue("role", normalizedRole);
            await cmd.ExecuteNonQueryAsync();

            account["username"] = targetUsername;
            account["role"] = normalizedRole;
            account["updated_at"] = DateTime.UtcNow.ToString("O");

            await _repo.SetAsync("users", id, new Dictionary<string, object?>
            {
                ["id"] = id,
                ["uid"] = id,
                ["email"] = Text(account, "email"),
                ["username"] = targetUsername,
                ["displayName"] = targetUsername,
                ["role"] = normalizedRole,
                ["updated_at"] = DateTime.UtcNow
            }, merge: true);

            await SyncTourSalesNameAsync(id, targetUsername);
            await SyncPostAuthorNameAsync(id, targetUsername);
        }
    }

    private static Dictionary<string, object?> ReadAccountRow(NpgsqlDataReader reader) => new()
    {
        ["id"] = reader.GetString(0),
        ["email"] = reader.GetString(1),
        ["username"] = reader.GetString(2),
        ["role"] = reader.GetString(3),
        ["is_locked"] = reader.GetBoolean(4),
        ["isLocked"] = reader.GetBoolean(4),
        ["is_protected"] = reader.GetBoolean(5),
        ["isProtected"] = reader.GetBoolean(5),
        ["created_at"] = reader.GetDateTime(6).ToUniversalTime().ToString("O"),
        ["updated_at"] = reader.GetDateTime(7).ToUniversalTime().ToString("O"),
        ["last_login_at"] = reader.IsDBNull(8) ? null : reader.GetDateTime(8).ToUniversalTime().ToString("O"),
        ["is_online"] = reader.GetBoolean(9),
        ["isOnline"] = reader.GetBoolean(9),
        ["presence_status"] = reader.GetBoolean(9) ? "online" : "offline",
        ["last_seen_at"] = reader.IsDBNull(10) ? null : reader.GetDateTime(10).ToUniversalTime().ToString("O"),
        ["lastSeenAt"] = reader.IsDBNull(10) ? null : reader.GetDateTime(10).ToUniversalTime().ToString("O"),
        ["last_logout_at"] = reader.IsDBNull(11) ? null : reader.GetDateTime(11).ToUniversalTime().ToString("O"),
        ["lastLogoutAt"] = reader.IsDBNull(11) ? null : reader.GetDateTime(11).ToUniversalTime().ToString("O")
    };

    private static bool IsDeletedPost(Dictionary<string, object?> post)
    {
        return IsTruthy(post.GetValueOrDefault("is_deleted"))
            || IsTruthy(post.GetValueOrDefault("isDeleted"))
            || string.Equals(Text(post, "status"), "Đã xóa", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProtectedAdmin(Dictionary<string, object?> account)
    {
        var email = Text(account, "email").ToLowerInvariant();
        return email == AdminEmail || IsTruthy(account.GetValueOrDefault("is_protected"));
    }

    private static decimal NormalizePercent(decimal value, decimal fallback = 0m)
    {
        if (value < 0) return fallback;
        if (value > 100) return 100m;
        return value;
    }

    private sealed record OwnedRevenueOrder(string OwnerId, string OwnerRole, Dictionary<string, object?> Order);

    private sealed record SalesLevelSetting(int Level, decimal CommissionPercent, decimal OfferDiscountPercent, decimal ServicePercent);

    private async Task<List<SalesLevelSetting>> GetSalesLevelSettingsAsync()
    {
        Dictionary<string, object?>? doc = null;
        try
        {
            doc = await _repo.GetByIdAsync(SalesLevelSettingsCollection, SalesLevelSettingsDocumentId);
        }
        catch
        {
            doc = null;
        }

        if (doc?.GetValueOrDefault("levels") is IEnumerable<object?> rawLevels)
        {
            var parsed = rawLevels
                .OfType<Dictionary<string, object?>>()
                .Select(item => new SalesLevelSetting(
                    ClampSalesLevel(TryInt(item.GetValueOrDefault("level")) ?? 1),
                    NormalizePercent(TryDecimal(item.GetValueOrDefault("commission_percent")) ?? TryDecimal(item.GetValueOrDefault("commissionPercent")) ?? DefaultSalesLevelSetting(ClampSalesLevel(TryInt(item.GetValueOrDefault("level")) ?? 1)).CommissionPercent),
                    NormalizePercent(TryDecimal(item.GetValueOrDefault("offer_discount_percent")) ?? TryDecimal(item.GetValueOrDefault("offerDiscountPercent")) ?? DefaultSalesLevelSetting(ClampSalesLevel(TryInt(item.GetValueOrDefault("level")) ?? 1)).OfferDiscountPercent),
                    NormalizePercent(TryDecimal(item.GetValueOrDefault("service_percent")) ?? TryDecimal(item.GetValueOrDefault("servicePercent")) ?? TryDecimal(item.GetValueOrDefault("service_fee_percent")) ?? TryDecimal(item.GetValueOrDefault("serviceFeePercent")) ?? DefaultSalesLevelSetting(ClampSalesLevel(TryInt(item.GetValueOrDefault("level")) ?? 1)).ServicePercent)
                ))
                .ToList();
            if (parsed.Count > 0) return NormalizeSalesLevelSettings(parsed);
        }

        return NormalizeSalesLevelSettings(Array.Empty<SalesLevelSetting>());
    }

    private static List<SalesLevelSetting> NormalizeSalesLevelSettings(IEnumerable<SalesLevelSetting> settings)
    {
        var map = settings
            .GroupBy(item => ClampSalesLevel(item.Level))
            .ToDictionary(group => group.Key, group => group.Last());

        return Enumerable.Range(1, 5)
            .Select(level => map.TryGetValue(level, out var item)
                ? new SalesLevelSetting(level, NormalizePercent(item.CommissionPercent, DefaultSalesLevelSetting(level).CommissionPercent), NormalizePercent(item.OfferDiscountPercent), NormalizePercent(item.ServicePercent))
                : DefaultSalesLevelSetting(level))
            .ToList();
    }

    private static SalesLevelSetting DefaultSalesLevelSetting(int level) => ClampSalesLevel(level) switch
    {
        2 => new SalesLevelSetting(2, 12m, 0m, 0m),
        3 => new SalesLevelSetting(3, 15m, 0m, 0m),
        4 => new SalesLevelSetting(4, 18m, 0m, 0m),
        5 => new SalesLevelSetting(5, 20m, 0m, 0m),
        _ => new SalesLevelSetting(1, 8m, 0m, 0m)
    };

    private static SalesLevelSetting GetSalesLevelSetting(IReadOnlyCollection<SalesLevelSetting> settings, int level)
    {
        var safeLevel = ClampSalesLevel(level);
        return settings.FirstOrDefault(item => item.Level == safeLevel) ?? DefaultSalesLevelSetting(safeLevel);
    }

    private static object ToSalesLevelResponse(SalesLevelSetting setting) => new
    {
        level = setting.Level,
        commission_percent = setting.CommissionPercent,
        commissionPercent = setting.CommissionPercent,
        offer_discount_percent = setting.OfferDiscountPercent,
        offerDiscountPercent = setting.OfferDiscountPercent,
        service_percent = setting.ServicePercent,
        servicePercent = setting.ServicePercent,
        service_fee_percent = setting.ServicePercent,
        serviceFeePercent = setting.ServicePercent
    };

    private static Dictionary<string, object?> ToSalesLevelDictionary(SalesLevelSetting setting) => new()
    {
        ["level"] = setting.Level,
        ["commission_percent"] = setting.CommissionPercent,
        ["commissionPercent"] = setting.CommissionPercent,
        ["offer_discount_percent"] = setting.OfferDiscountPercent,
        ["offerDiscountPercent"] = setting.OfferDiscountPercent,
        ["service_percent"] = setting.ServicePercent,
        ["servicePercent"] = setting.ServicePercent,
        ["service_fee_percent"] = setting.ServicePercent,
        ["serviceFeePercent"] = setting.ServicePercent
    };

    private static int ClampSalesLevel(int? level)
    {
        var value = level ?? 1;
        if (value < 1) return 1;
        if (value > 5) return 5;
        return value;
    }

    private static (int Level, decimal Percent) GetUserSalesLevel(Dictionary<string, object?>? user, int soldCount)
    {
        var automatic = GetSalesLevel(soldCount);
        if (user is null) return automatic;
        var storedLevel = TryInt(user.GetValueOrDefault("sales_level")) ?? TryInt(user.GetValueOrDefault("salesLevel"));
        if (storedLevel is null) return automatic;
        return (ClampSalesLevel(storedLevel), automatic.Percent);
    }

    private static bool TryGetAdminOfferOverride(Dictionary<string, object?>? user, out decimal discount)
    {
        discount = 0m;
        if (user is null) return false;
        if (!IsTruthy(user.GetValueOrDefault("admin_offer_override")) && !IsTruthy(user.GetValueOrDefault("adminOfferOverride"))) return false;
        discount = NormalizePercent(
            TryDecimal(user.GetValueOrDefault("admin_offer_discount_percent"))
            ?? TryDecimal(user.GetValueOrDefault("adminOfferDiscountPercent"))
            ?? TryDecimal(user.GetValueOrDefault("offer_discount_percent"))
            ?? TryDecimal(user.GetValueOrDefault("offerDiscountPercent"))
            ?? 0m);
        return true;
    }

    private static decimal GetUserOfferPercent(Dictionary<string, object?>? user, decimal fallback = 0m)
    {
        if (user is null) return NormalizePercent(fallback);
        return NormalizePercent(
            TryDecimal(user.GetValueOrDefault("offer_discount_percent"))
            ?? TryDecimal(user.GetValueOrDefault("offerDiscountPercent"))
            ?? TryDecimal(user.GetValueOrDefault("admin_offer_discount_percent"))
            ?? TryDecimal(user.GetValueOrDefault("adminOfferDiscountPercent"))
            ?? fallback,
            fallback);
    }

    private static decimal GetUserCommissionPercent(Dictionary<string, object?>? user, decimal fallback = 8m)
    {
        if (user is null) return NormalizePercent(fallback, 8m);
        if (IsTruthy(user.GetValueOrDefault("commission_manual_override")) || IsTruthy(user.GetValueOrDefault("commissionManualOverride")))
        {
            return NormalizePercent(
                TryDecimal(user.GetValueOrDefault("commission_percent"))
                ?? TryDecimal(user.GetValueOrDefault("commissionPercent"))
                ?? fallback,
                fallback);
        }
        return NormalizePercent(fallback, 8m);
    }

    private static decimal GetUserServicePercent(Dictionary<string, object?>? user, decimal fallback = 0m)
    {
        if (user is null) return NormalizePercent(fallback);
        return NormalizePercent(
            TryDecimal(user.GetValueOrDefault("service_fee_percent"))
            ?? TryDecimal(user.GetValueOrDefault("serviceFeePercent"))
            ?? TryDecimal(user.GetValueOrDefault("service_percent"))
            ?? TryDecimal(user.GetValueOrDefault("servicePercent"))
            ?? fallback,
            fallback);
    }

    private static object BuildAccountRevenueRow(
        Dictionary<string, object?>? account,
        string accountId,
        IReadOnlyList<OwnedRevenueOrder> accountOrders,
        IReadOnlyList<OwnedRevenueOrder> allOwnedOrders)
    {
        var currentRole = NormalizeRole(TextAny(account, "role", "userRole"));
        var historicalRole = accountOrders
            .Select(item => NormalizeRole(item.OwnerRole) ?? item.OwnerRole)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var role = currentRole ?? historicalRole ?? "Free";

        var accountEmail = TextAny(account, "email");
        var isPrimaryAdmin = string.Equals(accountEmail, AdminEmail, StringComparison.OrdinalIgnoreCase);
        var ownSalesOrders = accountOrders.Where(item => IsSalesRole(item.OwnerRole)).ToList();
        var ownCompanyOrders = accountOrders.Where(item => IsCompanyRole(item.OwnerRole)).ToList();

        decimal grossRevenue = accountOrders.Sum(item => GetOrderOriginalTotal(item.Order));
        // Impact fields are signed: positive means the account receives money,
        // negative means the amount is deducted from the account.
        decimal discountDeducted = 0m;
        decimal commission = ownSalesOrders.Sum(item => GetOrderCommissionAmount(item.Order));
        decimal serviceFee = -ownCompanyOrders.Sum(item => GetOrderServiceAmount(item.Order));
        decimal revenue = commission
            + ownCompanyOrders.Sum(item => Math.Max(0, GetOrderOriginalTotal(item.Order) - GetOrderServiceAmount(item.Order)));
        var orderCount = accountOrders.Count;

        if (isPrimaryAdmin)
        {
            // The primary Admin receives:
            // 1) Revenue from every Admin-owned tour because secondary Admin accounts do not receive money.
            // 2) The platform share from each Sales account's orders.
            // 3) The service fee from each Company account's orders, while Admin bears all discounts.
            var allAdminOrders = allOwnedOrders.Where(item => IsAdminRole(item.OwnerRole)).ToList();
            var allSalesOrders = allOwnedOrders.Where(item => IsSalesRole(item.OwnerRole)).ToList();
            var allCompanyOrders = allOwnedOrders.Where(item => IsCompanyRole(item.OwnerRole)).ToList();

            grossRevenue = allAdminOrders.Sum(item => GetOrderOriginalTotal(item.Order))
                + allSalesOrders.Sum(item => GetOrderOriginalTotal(item.Order));
            discountDeducted = -(allAdminOrders.Sum(item => GetOrderDiscountAmount(item.Order))
                + allSalesOrders.Sum(item => GetOrderDiscountAmount(item.Order))
                + allCompanyOrders.Sum(item => GetOrderDiscountAmount(item.Order)));
            // Admin pays Sales commission, so it is shown as a negative impact.
            commission = -allSalesOrders.Sum(item => GetOrderCommissionAmount(item.Order));
            // Admin receives the service fee paid by Company, so it is positive.
            serviceFee = allCompanyOrders.Sum(item => GetOrderServiceAmount(item.Order));
            revenue = allAdminOrders.Sum(item => GetAdminOwnedOrderRevenue(item.Order))
                + allSalesOrders.Sum(item => GetAdminSalesOrderRevenue(item.Order))
                + allCompanyOrders.Sum(item => GetAdminCompanyOrderRevenue(item.Order));
            orderCount = allAdminOrders.Count + allSalesOrders.Count + allCompanyOrders.Count;
        }
        else if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            // Admin is the only exception to per-account payout: all Admin-owned revenue goes to the primary Admin.
            grossRevenue = 0m;
            discountDeducted = 0m;
            commission = 0m;
            serviceFee = 0m;
            revenue = 0m;
            orderCount = 0;
        }
        else
        {
            // Revenue is calculated per exact account and per role stored on each sold order.
            // Sales orders pay commission; Company orders pay original price minus service fee.
            discountDeducted = 0m;
        }

        var discountRelevant = isPrimaryAdmin;
        // Show both sides of each transfer: Sales receives commission while Admin pays it;
        // Company pays the service fee while Admin receives it.
        var commissionRelevant = isPrimaryAdmin || ownSalesOrders.Count > 0;
        var serviceFeeRelevant = isPrimaryAdmin || ownCompanyOrders.Count > 0;

        var username = TextAny(account, "username", "displayName", "display_name");
        var email = accountEmail;
        if (string.IsNullOrWhiteSpace(username)) username = !string.IsNullOrWhiteSpace(email) ? email : $"Tài khoản {accountId}";

        return new
        {
            accountId,
            username,
            displayName = username,
            email,
            role,
            isPrimaryAdmin,
            discountRelevant,
            commissionRelevant,
            serviceFeeRelevant,
            orderCount,
            grossRevenue,
            discountDeducted,
            commission,
            serviceFee,
            revenue
        };
    }

    private static decimal GetAnonymousDecimal(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        if (property?.GetValue(value) is decimal amount) return amount;
        return TryDecimal(property?.GetValue(value)) ?? 0;
    }

    private static string GetAnonymousText(object value, string propertyName)
    {
        return value.GetType().GetProperty(propertyName)?.GetValue(value)?.ToString() ?? string.Empty;
    }

    private static string? NormalizeRole(string? role)
    {
        var value = (role ?? string.Empty).Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        value = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return value switch
        {
            "user" or "free" or "mien phi" or "miễn phí" or "nguoi dung" or "người dùng" => "Free",
            "vip" => "VIP",
            "premium" => "Premium",
            "admin" => "Admin",
            "sales" or "sale" or "tour sales" or "toursales" or "tour sale" or "ban tour" or "bán tour" => "Sales",
            "company" or "business" or "cong ty" or "công ty" or "doanh nghiep" or "doanh nghiệp" => "Company",
            _ => null
        };
    }

    private static bool IsAdminRole(object? role)
    {
        return string.Equals(role?.ToString(), "Admin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSalesRole(object? role)
    {
        var value = role?.ToString() ?? string.Empty;
        return string.Equals(value, "Sales", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Tour Sales", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompanyRole(object? role)
    {
        var value = role?.ToString() ?? string.Empty;
        return string.Equals(value, "Business", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Company", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripRolePrefix(string? username)
    {
        var value = (username ?? string.Empty).Trim();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var prefix in new[] { "Admin-", "Sales-", "Company-", "Business-" })
            {
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = value[prefix.Length..].Trim();
                    changed = true;
                }
            }
        }
        return string.IsNullOrWhiteSpace(value) ? "Tài khoản" : value;
    }

    private static string BuildRoleUsername(string? role, string? username)
    {
        var clean = StripRolePrefix(username);
        var normalized = NormalizeRole(role) ?? role ?? "Free";
        if (string.Equals(normalized, "Admin", StringComparison.OrdinalIgnoreCase)) return $"Admin-{clean}";
        if (IsSalesRole(normalized)) return $"Sales-{clean}";
        if (IsCompanyRole(normalized)) return $"Company-{clean}";
        return clean;
    }

    private static (int Level, decimal Percent) GetSalesLevel(int soldCount)
    {
        if (soldCount >= 300) return (5, 20m);
        if (soldCount >= 200) return (4, 18m);
        if (soldCount >= 120) return (3, 15m);
        if (soldCount >= 50) return (2, 12m);
        return (1, 8m);
    }

    private static bool IsRole(object? role, string expected) => string.Equals(role?.ToString(), expected, StringComparison.OrdinalIgnoreCase);
    private static string Text(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    private static string TextAny(Dictionary<string, object?>? row, params string[] keys)
    {
        if (row is null) return string.Empty;
        foreach (var key in keys)
        {
            var value = Text(row, key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }
    private static bool IsTruthy(object? value) => value is bool b ? b : bool.TryParse(value?.ToString(), out var parsed) && parsed;
    private static decimal GetOrderTotal(Dictionary<string, object?> order) => TryDecimal(order.GetValueOrDefault("total_price")) ?? TryDecimal(order.GetValueOrDefault("totalPrice")) ?? 0;
    private static decimal GetOrderOriginalTotal(Dictionary<string, object?> order)
    {
        var original = TryDecimal(order.GetValueOrDefault("original_total_price"))
            ?? TryDecimal(order.GetValueOrDefault("originalTotalPrice"))
            ?? TryDecimal(order.GetValueOrDefault("commission_base_total"))
            ?? TryDecimal(order.GetValueOrDefault("commissionBaseTotal"))
            ?? 0;
        if (original > 0) return original;
        var discount = TryDecimal(order.GetValueOrDefault("discount_amount")) ?? TryDecimal(order.GetValueOrDefault("discountAmount")) ?? 0;
        return GetOrderTotal(order) + Math.Max(0, discount);
    }
    private static decimal GetOrderDiscountAmount(Dictionary<string, object?> order)
    {
        var stored = TryDecimal(order.GetValueOrDefault("discount_amount")) ?? TryDecimal(order.GetValueOrDefault("discountAmount")) ?? 0;
        if (stored > 0) return stored;
        return Math.Max(0, GetOrderOriginalTotal(order) - GetOrderTotal(order));
    }
    private static decimal GetOrderCommissionPercent(Dictionary<string, object?> order)
    {
        foreach (var key in new[] { "commission_percent", "commissionPercent" })
        {
            if (order.TryGetValue(key, out var raw) && TryDecimal(raw) is { } value)
            {
                return NormalizePercent(value);
            }
        }
        return 0m;
    }
    private static decimal GetOrderCommissionAmount(Dictionary<string, object?> order)
    {
        foreach (var key in new[] { "commission_amount", "commissionAmount" })
        {
            if (order.TryGetValue(key, out var raw) && TryDecimal(raw) is { } stored)
            {
                return Math.Max(0, stored);
            }
        }

        return Math.Round(GetOrderOriginalTotal(order) * GetOrderCommissionPercent(order) / 100m, 0, MidpointRounding.AwayFromZero);
    }
    private static decimal GetOrderServicePercent(Dictionary<string, object?> order)
    {
        foreach (var key in new[] { "service_fee_percent", "serviceFeePercent", "service_percent", "servicePercent" })
        {
            if (order.TryGetValue(key, out var raw) && TryDecimal(raw) is { } value)
            {
                return NormalizePercent(value);
            }
        }
        return 0m;
    }
    private static decimal GetOrderServiceAmount(Dictionary<string, object?> order)
    {
        foreach (var key in new[] { "service_fee_amount", "serviceFeeAmount", "service_amount", "serviceAmount" })
        {
            if (order.TryGetValue(key, out var raw) && TryDecimal(raw) is { } stored)
            {
                return Math.Max(0, stored);
            }
        }

        return Math.Round(GetOrderOriginalTotal(order) * GetOrderServicePercent(order) / 100m, 0, MidpointRounding.AwayFromZero);
    }
    private static decimal GetAdminOwnedOrderRevenue(Dictionary<string, object?> order)
    {
        return Math.Max(0, GetOrderOriginalTotal(order) - GetOrderDiscountAmount(order));
    }

    private static decimal GetAdminSalesOrderRevenue(Dictionary<string, object?> order)
    {
        return Math.Max(0, GetOrderOriginalTotal(order) - GetOrderDiscountAmount(order) - GetOrderCommissionAmount(order));
    }

    private static decimal GetAdminCompanyOrderRevenue(Dictionary<string, object?> order)
    {
        return GetOrderServiceAmount(order) - GetOrderDiscountAmount(order);
    }

    private static List<OwnedRevenueOrder> BuildOwnedRevenueOrders(
        IEnumerable<Dictionary<string, object?>> orders,
        IEnumerable<Dictionary<string, object?>> tours,
        IEnumerable<Dictionary<string, object?>> accounts)
    {
        var accountRoleMap = accounts
            .Where(account => !string.IsNullOrWhiteSpace(Text(account, "id")))
            .GroupBy(account => Text(account, "id"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => NormalizeRole(TextAny(group.First(), "role", "userRole")) ?? TextAny(group.First(), "role", "userRole"),
                StringComparer.OrdinalIgnoreCase);
        var tourOwnerMap = tours
            .Where(tour => !string.IsNullOrWhiteSpace(Text(tour, "id")))
            .GroupBy(tour => Text(tour, "id"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => TextAny(group.First(), "created_by", "createdBy", "tour_sales_id", "tourSalesId", "seller_id", "sellerId"),
                StringComparer.OrdinalIgnoreCase);
        var result = new List<OwnedRevenueOrder>();

        foreach (var order in orders)
        {
            var ownerId = TextAny(order, "tour_sales_id", "tourSalesId", "created_by", "createdBy");
            var tourId = TextAny(order, "tour_id", "tourId");
            if (string.IsNullOrWhiteSpace(ownerId) && !string.IsNullOrWhiteSpace(tourId))
            {
                ownerId = tourOwnerMap.GetValueOrDefault(tourId) ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(ownerId)) ownerId = TextAny(order, "seller_id", "sellerId");

            var storedOwnerRole = TextAny(order, "owner_role", "ownerRole");
            var ownerRole = NormalizeRole(storedOwnerRole) ?? storedOwnerRole;
            if (string.IsNullOrWhiteSpace(ownerRole) && !string.IsNullOrWhiteSpace(ownerId))
            {
                ownerRole = accountRoleMap.GetValueOrDefault(ownerId) ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(ownerRole)) ownerRole = "Free";

            result.Add(new OwnedRevenueOrder(ownerId, ownerRole, order));
        }

        return result;
    }
    private static int GetInt(Dictionary<string, object?> row, string key) => int.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value) ? value : 0;
    private static int? TryInt(object? value) => int.TryParse(value?.ToString(), out var i) ? i : null;
    private static decimal? TryDecimal(object? value) => decimal.TryParse(value?.ToString(), out var d) ? d : null;
}

public sealed class AdminStorageLimitRequest
{
    public long LimitBytes { get; set; }
}

public sealed class AdminChatbotSettingsRequest
{
    public string? ChatbotName { get; set; }
    public string? DefaultStyleId { get; set; }
    public List<AdminChatbotStyleItemRequest>? Styles { get; set; }
}

public sealed class AdminChatbotStyleItemRequest
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Prompt { get; set; }
    public decimal? Price { get; set; }
    public int? MaxResponseWords { get; set; }
}

public sealed class AdminAccountUpdateRequest
{
    public string? Username { get; set; }
    public string? Role { get; set; }
    public DateTimeOffset? PlanExpiresAt { get; set; }
    public bool? IsLocked { get; set; }
    public decimal? OfferDiscountPercent { get; set; }
    public decimal? CommissionPercent { get; set; }
    public bool? CommissionManualOverride { get; set; }
    public int? SalesLevel { get; set; }
    public int? CommissionLevel { get; set; }
    public int? OfferLevel { get; set; }
    public decimal? ServicePercent { get; set; }
    public int? ServiceLevel { get; set; }
}

public sealed class AdminSalesLevelSettingsRequest
{
    public List<AdminSalesLevelSettingRequest>? Levels { get; set; }
}

public sealed class AdminSalesLevelSettingRequest
{
    public int Level { get; set; }
    public decimal? CommissionPercent { get; set; }
    public decimal? OfferDiscountPercent { get; set; }
    public decimal? ServicePercent { get; set; }
}

public sealed class AdminPlanStatusOptionRequest
{
    public string? Key { get; set; }
    public string? Label { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool MatchAll { get; set; }
    public bool Enabled { get; set; } = true;
    public int Order { get; set; } = 999;
    public string? Color { get; set; }
}

public sealed class AdminTravelTagRequest
{
    public string? Name { get; set; }
    public string? Color { get; set; }
}

public sealed class AdminProvinceTagsRequest
{
    public string? Id { get; set; }
    public int? ProvinceId { get; set; }
    public string? Name { get; set; }
    public string? Area { get; set; }
    public string? Region { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
}

public sealed class AdminAiProviderUpdateRequest
{
    public string Provider { get; set; } = string.Empty;
}
