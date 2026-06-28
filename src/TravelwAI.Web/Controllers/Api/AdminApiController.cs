using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/admin")]
public sealed class AdminApiController : ApiControllerBase
{
    private const string AdminEmail = "2324802010387@student.tdmu.edu.vn";
    private const int MaxAdminAccounts = 4;
    private const string PlanStatusOptionsCollection = "plan_status_options";
    private const string ProvinceTravelTagsCollection = "province_travel_tags";
    private const string PlanTravelTagsCollection = "plan_travel_tags";
    private const string SalesLevelSettingsCollection = "sales_level_settings";
    private const string ProvinceSearchEventsCollection = "province_search_events";
    private const string PostViewEventsCollection = "post_view_events";
    private const string SalesLevelSettingsDocumentId = "default";
    private const long MaxAvatarBytes = 10 * 1024 * 1024;
    private readonly IDataRepository _repo;
    private readonly NpgsqlDataSource _dataSource;
    private readonly TourOfferService _offerService;
    private readonly IWebHostEnvironment _env;

    public AdminApiController(IAuthService authService, IDataRepository repo, NpgsqlDataSource dataSource, TourOfferService offerService, IWebHostEnvironment env) : base(authService)
    {
        _repo = repo;
        _dataSource = dataSource;
        _offerService = offerService;
        _env = env;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var accounts = await ReadAccountsAsync();
        var tours = await _repo.GetAllAsync("tours", limit: 200);
        var orders = await _repo.GetAllAsync("tour_orders", limit: 500);
        var schedules = await _repo.GetAllAsync("schedules", limit: 500);
        var statusOptions = await GetPlanStatusOptionsAsync(includeDisabled: true);
        var provinceTags = await GetProvinceTagsAsync();
        var posts = await _repo.GetAllAsync("travel_posts", limit: 400);

        return Ok(new
        {
            success = true,
            data = new
            {
                accounts = accounts.Count,
                lockedAccounts = accounts.Count(a => IsTruthy(a.GetValueOrDefault("is_locked"))),
                tourSalesAccounts = accounts.Count(a => IsSalesRole(a.GetValueOrDefault("role")) || IsBusinessRole(a.GetValueOrDefault("role"))),
                tours = tours.Count,
                activeTours = tours.Count(t => string.Equals(Text(t, "status"), "Đang bán", StringComparison.OrdinalIgnoreCase) && !(GetInt(t, "slots") > 0 && GetInt(t, "sold") >= GetInt(t, "slots"))),
                tourOrders = orders.Count,
                schedules = schedules.Count,
                planStatuses = statusOptions.Count,
                provinces = provinceTags.Count,
                posts = posts.Count(p => !IsDeletedPost(p)),
                revenue = orders
                    .Where(o => string.Equals(Text(o, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
                    .Sum(GetOrderNetRevenue),
                grossRevenue = orders
                    .Where(o => string.Equals(Text(o, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
                    .Sum(GetOrderOriginalTotal),
                discountDeducted = orders
                    .Where(o => string.Equals(Text(o, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
                    .Sum(GetOrderDiscountAmount),
                commissionDeducted = orders
                    .Where(o => string.Equals(Text(o, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
                    .Sum(GetOrderCommissionAmount),
                serviceFee = orders
                    .Where(o => string.Equals(Text(o, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
                    .Sum(GetOrderServiceAmount),
                service_fee = orders
                    .Where(o => string.Equals(Text(o, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
                    .Sum(GetOrderServiceAmount)
            }
        });
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearStart = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var provinceViews = await _repo.GetAllAsync(ProvinceSearchEventsCollection, limit: 3000);
        var postViews = await _repo.GetAllAsync(PostViewEventsCollection, limit: 3000);
        var plans = await _repo.GetAllAsync("plans", limit: 3000);
        var orders = await _repo.GetAllAsync("tour_orders", limit: 3000);
        var tours = await _repo.GetAllAsync("tours", limit: 1000);
        var monthStats = BuildAdminAnalyticsSnapshot(monthStart, monthStart.AddMonths(1), $"Tháng {monthStart.Month}/{monthStart.Year}", provinceViews, postViews, plans, orders, tours);
        var yearMonths = new List<Dictionary<string, object?>>();
        for (var month = 1; month <= 12; month++)
        {
            var start = new DateTime(now.Year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            yearMonths.Add(BuildAdminAnalyticsSnapshot(start, start.AddMonths(1), $"Tháng {month}", provinceViews, postViews, plans, orders, tours));
        }
        var aiSummary = BuildAdminAnalyticsSummary(monthStats, yearMonths, monthStart, yearStart);

        return Ok(new
        {
            success = true,
            data = new
            {
                month = monthStats,
                year_months = yearMonths,
                yearMonths = yearMonths,
                ai_summary = aiSummary,
                aiSummary = aiSummary
            }
        });
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

    [HttpPost("ai-avatar/{assistant}")]
    public async Task<IActionResult> UpdateAiAvatar(string assistant, [FromForm] IFormFile? avatar)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var avatarBaseName = NormalizeAiAvatarBaseName(assistant);
        if (avatarBaseName is null)
        {
            return BadRequest(new { success = false, message = "Chatbot AI không hợp lệ." });
        }
        if (avatar is null || avatar.Length == 0)
        {
            return BadRequest(new { success = false, message = "Vui lòng chọn ảnh avatar AI." });
        }
        if (avatar.Length > MaxAvatarBytes)
        {
            return BadRequest(new { success = false, message = "Ảnh avatar tối đa 10MB." });
        }

        var avatarExt = NormalizeOptimizedUploadExtension(avatar.FileName);
        if (!string.Equals(avatarExt, ".webp", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Ảnh phải được chuyển sang WEBP trước khi upload." });
        }

        var logoDir = GetSafeWebRootSubDirectory("logo");
        Directory.CreateDirectory(logoDir);
        DeleteFixedImageVariants(logoDir, avatarBaseName);

        await SaveFixedImageVariantAsync(logoDir, avatarBaseName, avatar);
        var publicFile = avatarBaseName + ".webp";
        var label = avatarBaseName.Contains("manager", StringComparison.OrdinalIgnoreCase) ? "Quản lý TravelwAI" : "Travelwinne";
        return Ok(new { success = true, url = $"/logo/{publicFile}", message = $"Đã cập nhật avatar {label}" });
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

        var imageDir = GetSafeWebRootSubDirectory("main_site_image");
        Directory.CreateDirectory(imageDir);
        foreach (var baseName in background.Value.FileBaseNames)
        {
            DeleteFixedImageVariants(imageDir, baseName);
            await SaveFixedImageVariantAsync(imageDir, baseName, image);
        }

        return Ok(new { success = true, message = $"Đã cập nhật ảnh {background.Value.Label}." });
    }

    [HttpPost("travelwinne-avatar")]
    public Task<IActionResult> UpdateTravelwinneAvatar([FromForm] IFormFile? avatar)
    {
        return UpdateAiAvatar("travelwinne", avatar);
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
        var role = NormalizeRole(string.IsNullOrWhiteSpace(request.Role) ? Text(account, "role") : request.Role);
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

        if (role is null) return BadRequest(new { success = false, message = "Vai trò không hợp lệ. Chỉ dùng Free, VIP, Premium, Admin, Sales hoặc Business." });

        if (role == "Sales")
        {
            servicePercent = 0m;
        }
        else
        {
            commissionPercent = 0m;
            commissionManualOverride = false;
        }

        if (role != "Business")
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

        await SyncTourSalesNameAsync(id, username);
        await SyncPostAuthorNameAsync(id, username);

        return Ok(new { success = true, message = "Đã cập nhật tài khoản" });
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
        return Ok(new { success = true, message = deletedOffers > 0
            ? "Đã xóa tài khoản, ưu đãi liên quan đã xoá, tour và bài viết đã chuyển sang Admin"
            : "Đã xóa tài khoản, tour và bài viết đã chuyển sang Admin" });
    }

    [HttpGet("schedules")]
    public async Task<IActionResult> Schedules()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;
        var schedules = await _repo.GetAllAsync("schedules", limit: 500);
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

    private static Dictionary<string, object?> BuildAdminAnalyticsSnapshot(
        DateTime start,
        DateTime end,
        string label,
        List<Dictionary<string, object?>> provinceViews,
        List<Dictionary<string, object?>> postViews,
        List<Dictionary<string, object?>> plans,
        List<Dictionary<string, object?>> orders,
        List<Dictionary<string, object?>> tours)
    {
        var viewsInRange = provinceViews
            .Where(item => IsInAnalyticsRange(AdminAnalyticsDate(item, "created_at", "createdAt", "updated_at", "updatedAt"), start, end))
            .ToList();
        var plansInRange = plans
            .Where(item => IsInAnalyticsRange(AdminAnalyticsDate(item, "created_at", "createdAt", "start_date", "startDate"), start, end))
            .ToList();
        var postViewsInRange = postViews
            .Where(item => IsInAnalyticsRange(AdminAnalyticsDate(item, "created_at", "createdAt", "updated_at", "updatedAt"), start, end))
            .ToList();
        var ordersInRange = orders
            .Where(item => IsInAnalyticsRange(AdminAnalyticsDate(item, "created_at", "createdAt", "tour_start_date", "tourStartDate"), start, end))
            .Where(IsCountableTourOrder)
            .ToList();

        var topProvinces = TopMetrics(viewsInRange
            .Select(item => TextAny(item, "province_name", "provinceName", "province", "name")), 5);

        var budgetRanges = FixedMetrics(new[] { "1.000.000 - 3.000.000", "3.000.000 - 5.000.000", "5.000.000 - 10.000.000" },
            plansInRange.Select(BudgetPerPersonFromPlan)
                .Concat(ordersInRange.Select(BudgetPerPersonFromOrder))
                .Select(BudgetBucket)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

        var tourById = tours
            .Select(tour => new { id = TextAny(tour, "id", "Id"), tour })
            .Where(item => !string.IsNullOrWhiteSpace(item.id))
            .GroupBy(item => item.id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().tour, StringComparer.Ordinal);

        var topTours = TopWeightedMetrics(ordersInRange.Select(order => (
            Label: GetTourAnalyticsName(order, tourById),
            Count: Math.Max(1, GetIntAny(order, "quantity", "people", "group_size", "groupSize"))
        )), 5);

        var groupSizes = FixedMetrics(new[] { "1 đến 2 người", "3 đến 5 người", "5 đến 10 người" },
            plansInRange.Select(item => GetIntAny(item, "target_people", "targetPeople", "people", "group_size", "groupSize"))
                .Concat(ordersInRange.Select(item => GetIntAny(item, "quantity", "people", "group_size", "groupSize")))
                .Select(GroupSizeBucket)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

        var topPosts = TopMetrics(postViewsInRange
            .Select(item => TextAny(item, "post_title", "postTitle", "title", "name")), 5);

        var topProvince = FirstMetricOrEmpty(topProvinces);
        var budgetRange = FirstMetricOrEmpty(budgetRanges.Where(HasMetricCount));
        var topTour = FirstMetricOrEmpty(topTours);
        var groupSize = FirstMetricOrEmpty(groupSizes.Where(HasMetricCount));
        var topPost = FirstMetricOrEmpty(topPosts);

        var details = new Dictionary<string, object?>
        {
            ["top_provinces"] = topProvinces,
            ["topProvinces"] = topProvinces,
            ["budget_ranges"] = budgetRanges,
            ["budgetRanges"] = budgetRanges,
            ["top_tours"] = topTours,
            ["topTours"] = topTours,
            ["group_sizes"] = groupSizes,
            ["groupSizes"] = groupSizes,
            ["top_posts"] = topPosts,
            ["topPosts"] = topPosts
        };

        return new Dictionary<string, object?>
        {
            ["label"] = label,
            ["month"] = start.Month,
            ["top_province"] = topProvince,
            ["topProvince"] = topProvince,
            ["budget_range"] = budgetRange,
            ["budgetRange"] = budgetRange,
            ["top_tour"] = topTour,
            ["topTour"] = topTour,
            ["group_size"] = groupSize,
            ["groupSize"] = groupSize,
            ["top_post"] = topPost,
            ["topPost"] = topPost,
            ["details"] = details,
            ["total_orders"] = ordersInRange.Count,
            ["totalOrders"] = ordersInRange.Count,
            ["total_province_views"] = viewsInRange.Count,
            ["totalProvinceViews"] = viewsInRange.Count,
            ["total_post_views"] = postViewsInRange.Count,
            ["totalPostViews"] = postViewsInRange.Count
        };
    }

    private static string BuildAdminAnalyticsSummary(Dictionary<string, object?> monthStats, List<Dictionary<string, object?>> yearMonths, DateTime monthStart, DateTime yearStart)
    {
        static IEnumerable<Dictionary<string, object?>> DetailRows(Dictionary<string, object?> stats, string snakeKey, string camelKey)
        {
            if (!stats.TryGetValue("details", out var rawDetails) || rawDetails is not Dictionary<string, object?> details)
            {
                if (!stats.TryGetValue("detail", out rawDetails) || rawDetails is not Dictionary<string, object?> altDetails) return Enumerable.Empty<Dictionary<string, object?>>();
                details = altDetails;
            }

            if (!details.TryGetValue(snakeKey, out var rawRows) && !details.TryGetValue(camelKey, out rawRows)) return Enumerable.Empty<Dictionary<string, object?>>();
            if (rawRows is IEnumerable<Dictionary<string, object?>> typedRows) return typedRows;
            if (rawRows is IEnumerable<object> objectRows) return objectRows.OfType<Dictionary<string, object?>>();
            return Enumerable.Empty<Dictionary<string, object?>>();
        }

        static List<Dictionary<string, object?>> AggregateDetails(List<Dictionary<string, object?>> months, string snakeKey, string camelKey, int take, string[]? fixedOrder = null)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (fixedOrder is not null)
            {
                foreach (var label in fixedOrder) counts[label] = 0;
            }

            foreach (var month in months)
            {
                foreach (var row in DetailRows(month, snakeKey, camelKey))
                {
                    var label = CleanAnalyticsLabel(AnalyticsMetricLabel(row));
                    if (string.IsNullOrWhiteSpace(label) || IsEmptyMetric(label)) continue;
                    var count = Math.Max(0, AnalyticsMetricCount(row));
                    counts[label] = counts.TryGetValue(label, out var current) ? current + count : count;
                }
            }

            var query = counts.Select(item => Metric(item.Key, item.Value));
            if (fixedOrder is not null)
            {
                return query
                    .Where(HasMetricCount)
                    .OrderByDescending(AnalyticsMetricCount)
                    .ThenBy(item => Array.IndexOf(fixedOrder, AnalyticsMetricLabel(item)))
                    .Take(take)
                    .ToList();
            }

            return query
                .Where(HasMetricCount)
                .OrderByDescending(AnalyticsMetricCount)
                .ThenBy(AnalyticsMetricLabel)
                .Take(take)
                .ToList();
        }

        static string JoinExamples(List<Dictionary<string, object?>> metrics)
        {
            var examples = metrics
                .Where(HasMetricCount)
                .Take(3)
                .Select(item =>
                {
                    var label = AnalyticsMetricLabel(item);
                    var count = AnalyticsMetricCount(item);
                    return string.IsNullOrWhiteSpace(label) || IsEmptyMetric(label)
                        ? string.Empty
                        : $"{label} ({count} lượt)";
                })
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();
            return examples.Count == 0 ? string.Empty : string.Join(", ", examples);
        }

        static void AddInsight(List<string> lines, string title, string note, List<Dictionary<string, object?>> metrics)
        {
            var examples = JoinExamples(metrics);
            if (string.IsNullOrWhiteSpace(examples)) return;
            lines.Add($"{title}: {note} {examples}.");
        }

        var topProvinces = AggregateDetails(yearMonths, "top_provinces", "topProvinces", 3);
        var budgetRanges = AggregateDetails(yearMonths, "budget_ranges", "budgetRanges", 3, new[] { "1.000.000 - 3.000.000", "3.000.000 - 5.000.000", "5.000.000 - 10.000.000" });
        var topTours = AggregateDetails(yearMonths, "top_tours", "topTours", 3);
        var groupSizes = AggregateDetails(yearMonths, "group_sizes", "groupSizes", 3, new[] { "1 đến 2 người", "3 đến 5 người", "5 đến 10 người" });
        var topPosts = AggregateDetails(yearMonths, "top_posts", "topPosts", 3);
        var lines = new List<string>();

        AddInsight(lines, "Tỉnh được tìm nhiều nhất", "dựa trên lượt mở chi tiết tỉnh, bấm Hỏi AI và câu hỏi AI có nhắc đến tỉnh/thành:", topProvinces);
        AddInsight(lines, "Ngân sách phổ biến", "dựa trên kế hoạch và đơn tour:", budgetRanges);
        AddInsight(lines, "Loại tour đặt nhiều nhất", "dựa trên đơn tour:", topTours);
        AddInsight(lines, "Du lịch theo nhóm", "dựa trên số người trong kế hoạch và đơn tour:", groupSizes);
        AddInsight(lines, "Bài viết được xem nhiều nhất", "dựa trên lượt bấm Xem bài viết:", topPosts);

        return lines.Count == 0
            ? $"Năm {yearStart.Year} chưa có dữ liệu thống kê đủ để tạo nhận xét."
            : $"Trong năm {yearStart.Year}, thống kê chỉ dựa trên dữ liệu đã ghi nhận trong hệ thống. " + string.Join(" ", lines);
    }

    private static Dictionary<string, object?> Metric(string label, int count, Dictionary<string, object?>? extra = null)
    {
        var data = extra is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(extra);
        data["label"] = string.IsNullOrWhiteSpace(label) ? "Chưa có dữ liệu" : label;
        data["count"] = count;
        return data;
    }

    private static Dictionary<string, object?> TopMetric(IEnumerable<string> values)
    {
        return FirstMetricOrEmpty(TopMetrics(values, 1));
    }

    private static List<Dictionary<string, object?>> TopMetrics(IEnumerable<string> values, int take)
    {
        return values
            .Select(value => CleanAnalyticsLabel(value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => Metric(group.First(), group.Count()))
            .OrderByDescending(AnalyticsMetricCount)
            .ThenBy(AnalyticsMetricLabel)
            .Take(Math.Max(1, take))
            .ToList();
    }

    private static List<Dictionary<string, object?>> TopWeightedMetrics(IEnumerable<(string Label, int Count)> values, int take)
    {
        return values
            .Select(item => new { Label = CleanAnalyticsLabel(item.Label), Count = Math.Max(0, item.Count) })
            .Where(item => !string.IsNullOrWhiteSpace(item.Label) && item.Count > 0)
            .GroupBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => Metric(group.First().Label, group.Sum(item => item.Count)))
            .OrderByDescending(AnalyticsMetricCount)
            .ThenBy(AnalyticsMetricLabel)
            .Take(Math.Max(1, take))
            .ToList();
    }

    private static List<Dictionary<string, object?>> FixedMetrics(IEnumerable<string> labels, IEnumerable<string> values)
    {
        var labelArray = labels.ToArray();
        var counts = values
            .Select(value => CleanAnalyticsLabel(value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return labelArray.Select(label => Metric(label, counts.TryGetValue(label, out var count) ? count : 0))
            .OrderByDescending(AnalyticsMetricCount)
            .ThenBy(item => Array.IndexOf(labelArray, AnalyticsMetricLabel(item)))
            .ToList();
    }

    private static Dictionary<string, object?> FirstMetricOrEmpty(IEnumerable<Dictionary<string, object?>> metrics)
    {
        return metrics.FirstOrDefault() ?? Metric("Chưa có dữ liệu", 0);
    }

    private static bool HasMetricCount(Dictionary<string, object?> metric) => AnalyticsMetricCount(metric) > 0;

    private static int AnalyticsMetricCount(Dictionary<string, object?> metric)
    {
        return int.TryParse(metric.GetValueOrDefault("count")?.ToString(), out var count) ? count : 0;
    }

    private static string AnalyticsMetricLabel(Dictionary<string, object?> metric)
    {
        return metric.GetValueOrDefault("label")?.ToString() ?? string.Empty;
    }

    private static int MetricCount(Dictionary<string, object?> stats, string key)
    {
        if (!stats.TryGetValue(key, out var raw) || raw is not Dictionary<string, object?> metric) return 0;
        return AnalyticsMetricCount(metric);
    }

    private static bool IsCountableTourOrder(Dictionary<string, object?> order)
    {
        var status = TextAny(order, "status", "order_status", "orderStatus");
        if (string.IsNullOrWhiteSpace(status)) return true;
        var normalized = NormalizeAnalyticsText(status);
        return !ContainsAny(normalized, "huy", "het han", "expired", "cancel");
    }

    private static string GetTourAnalyticsName(Dictionary<string, object?> order, Dictionary<string, Dictionary<string, object?>> toursById)
    {
        var name = TextAny(order, "tour_name", "tourName", "name", "title");
        var tourId = TextAny(order, "tour_id", "tourId");
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(tourId) && toursById.TryGetValue(tourId, out var tour))
        {
            name = TextAny(tour, "name", "title", "tour_name", "tourName");
        }
        return CleanAnalyticsLabel(name);
    }

    private static string MetricLabel(Dictionary<string, object?> stats, string key)
    {
        if (!stats.TryGetValue(key, out var raw) || raw is not Dictionary<string, object?> metric) return "Chưa có dữ liệu";
        return metric.TryGetValue("label", out var label) ? label?.ToString() ?? "Chưa có dữ liệu" : "Chưa có dữ liệu";
    }

    private static bool IsEmptyMetric(string value) => string.IsNullOrWhiteSpace(value) || string.Equals(value, "Chưa có dữ liệu", StringComparison.OrdinalIgnoreCase);
    private static string MetricOrFallback(string value, string fallback) => IsEmptyMetric(value) ? fallback : value;

    private static DateTime AdminAnalyticsDate(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            var date = ParseAdminAnalyticsDate(row.GetValueOrDefault(key));
            if (date != DateTime.MinValue) return date;
        }
        return DateTime.MinValue;
    }

    private static DateTime ParseAdminAnalyticsDate(object? value)
    {
        if (value is DateTime date) return date.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime();
        if (value is DateTimeOffset dto) return dto.UtcDateTime;
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return DateTime.MinValue;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            || DateTime.TryParse(text, CultureInfo.GetCultureInfo("vi-VN"), DateTimeStyles.AssumeLocal, out parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
    }

    private static bool IsInAnalyticsRange(DateTime date, DateTime start, DateTime end)
    {
        return date != DateTime.MinValue && date >= start && date < end;
    }

    private static bool IsYoungUser(Dictionary<string, object?> user)
    {
        var age = GetIntAny(user, "age", "tuoi", "user_age", "userAge");
        if (age <= 0)
        {
            var birthYear = GetIntAny(user, "birth_year", "birthYear", "year_of_birth", "yearOfBirth");
            if (birthYear > 1900) age = DateTime.UtcNow.Year - birthYear;
        }
        if (age <= 0)
        {
            var birthDate = AdminAnalyticsDate(user, "birth_date", "birthDate", "birthday", "dob", "date_of_birth", "dateOfBirth");
            if (birthDate != DateTime.MinValue)
            {
                var today = DateTime.UtcNow.Date;
                age = today.Year - birthDate.Year;
                if (birthDate.Date > today.AddYears(-age)) age--;
            }
        }
        return age >= 18 && age <= 25;
    }

    private static bool IsBeachInterest(Dictionary<string, object?> row)
    {
        var text = NormalizeAnalyticsText(AnalyticsText(row));
        return ContainsAny(text, "bien", "dao", "phu quoc", "nha trang", "da nang", "quy nhon", "vung tau", "ha long", "cat ba", "sam son", "phan thiet", "mui ne");
    }

    private static bool IsMountainInterest(Dictionary<string, object?> row)
    {
        var text = NormalizeAnalyticsText(AnalyticsText(row));
        return ContainsAny(text, "nui", "cao nguyen", "tay bac", "sa pa", "sapa", "da lat", "moc chau", "ha giang", "dien bien", "lao cai", "kon tum", "gia lai");
    }

    private static string ClassifyTourType(Dictionary<string, object?> order, Dictionary<string, Dictionary<string, object?>> toursById)
    {
        var tourId = TextAny(order, "tour_id", "tourId");
        var pieces = new List<string> { AnalyticsText(order) };
        if (!string.IsNullOrWhiteSpace(tourId) && toursById.TryGetValue(tourId, out var tour)) pieces.Add(AnalyticsText(tour));
        var text = NormalizeAnalyticsText(string.Join(" ", pieces));
        if (ContainsAny(text, "bien", "dao", "phu quoc", "nha trang", "da nang", "quy nhon", "vung tau", "ha long", "cat ba", "sam son", "phan thiet", "mui ne")) return "Tour biển";
        if (ContainsAny(text, "nui", "cao nguyen", "tay bac", "sa pa", "sapa", "da lat", "moc chau", "ha giang", "lao cai")) return "Tour núi";
        if (ContainsAny(text, "di tich", "lich su", "di san", "pho co", "co do", "hoi an", "hue", "den", "chua")) return "Tour văn hoá - lịch sử";
        if (ContainsAny(text, "nghi duong", "resort", "spa", "tuan trang mat")) return "Tour nghỉ dưỡng";
        if (ContainsAny(text, "team building", "teambuilding", "doan", "cong ty")) return "Tour team building";
        if (ContainsAny(text, "giai tri", "cong vien", "vinwonders", "khu vui choi")) return "Tour giải trí";
        return string.IsNullOrWhiteSpace(TextAny(order, "tour_name", "tourName")) ? "Chưa phân loại" : "Tour tổng hợp";
    }

    private static string AnalyticsText(Dictionary<string, object?> row)
    {
        var fields = new[]
        {
            "title", "name", "tour_name", "tourName", "destination", "tour_destination", "tourDestination", "province_name", "provinceName",
            "destination_status", "destinationStatus", "plan_status_label", "planStatusLabel", "status_key", "statusKey", "description", "tags", "province_tags", "provinceTags"
        };
        return string.Join(" ", fields.Select(field => row.GetValueOrDefault(field)?.ToString()).Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        return keywords.Any(keyword => text.Contains(NormalizeAnalyticsText(keyword), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeAnalyticsText(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) builder.Append(ch);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC).Replace('đ', 'd').Replace('Đ', 'D').ToLowerInvariant();
    }

    private static string CleanAnalyticsLabel(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return string.Join(" ", text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool PlanTouchesHoliday(Dictionary<string, object?> plan)
    {
        var start = AdminAnalyticsDate(plan, "start_date", "startDate", "created_at", "createdAt");
        var end = AdminAnalyticsDate(plan, "end_date", "endDate", "start_date", "startDate");
        return RangeTouchesHoliday(start, end);
    }

    private static bool OrderTouchesHoliday(Dictionary<string, object?> order)
    {
        var start = AdminAnalyticsDate(order, "tour_start_date", "tourStartDate", "created_at", "createdAt");
        var end = AdminAnalyticsDate(order, "tour_end_date", "tourEndDate", "tour_start_date", "tourStartDate");
        return RangeTouchesHoliday(start, end);
    }

    private static bool RangeTouchesHoliday(DateTime start, DateTime end)
    {
        if (start == DateTime.MinValue) return false;
        if (end == DateTime.MinValue || end < start) end = start;
        for (var day = start.Date; day <= end.Date && day <= start.Date.AddDays(14); day = day.AddDays(1))
        {
            if (IsHolidayDay(day)) return true;
        }
        return false;
    }

    private static bool IsHolidayDay(DateTime day)
    {
        var month = day.Month;
        var date = day.Day;
        return (month == 1 && date is >= 1 and <= 3)
            || (month == 4 && date is >= 28 and <= 30)
            || (month == 5 && date is >= 1 and <= 3)
            || (month == 9 && date is >= 1 and <= 3)
            || (month == 12 && date is >= 24 and <= 25);
    }

    private static decimal BudgetPerPersonFromPlan(Dictionary<string, object?> plan)
    {
        var budget = TryDecimal(plan.GetValueOrDefault("budget")) ?? TryDecimal(plan.GetValueOrDefault("total_budget")) ?? TryDecimal(plan.GetValueOrDefault("totalBudget")) ?? 0m;
        if (budget <= 0) return 0;
        var people = Math.Max(1, GetIntAny(plan, "target_people", "targetPeople", "people", "group_size", "groupSize"));
        return budget / people;
    }

    private static decimal BudgetPerPersonFromOrder(Dictionary<string, object?> order)
    {
        var total = GetOrderTotal(order);
        if (total <= 0) total = GetOrderOriginalTotal(order);
        if (total <= 0) return 0;
        var quantity = Math.Max(1, GetIntAny(order, "quantity", "people", "group_size", "groupSize"));
        return total / quantity;
    }

    private static string BudgetBucket(decimal value)
    {
        return value switch
        {
            >= 1000000m and < 3000000m => "1.000.000 - 3.000.000",
            >= 3000000m and < 5000000m => "3.000.000 - 5.000.000",
            >= 5000000m and <= 10000000m => "5.000.000 - 10.000.000",
            _ => string.Empty
        };
    }

    private static string GroupSizeBucket(int value)
    {
        return value switch
        {
            >= 1 and <= 2 => "1 đến 2 người",
            >= 3 and <= 5 => "3 đến 5 người",
            >= 5 and <= 10 => "5 đến 10 người",
            _ => string.Empty
        };
    }

    private static int GetIntAny(Dictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (int.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value)) return value;
        }
        return 0;
    }

    private async Task<(bool ok, IActionResult? error)> RequireAdminAsync()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return (false, current.error);
        if (!IsRole(current.authUser?.GetValueOrDefault("role"), "Admin"))
        {
            return (false, StatusCode(403, new { success = false, message = "Chỉ Admin mới được truy cập." }));
        }
        return (true, null);
    }

    private string GetSafeWebRootSubDirectory(string relativeFolder)
    {
        var webRoot = string.IsNullOrWhiteSpace(_env.WebRootPath)
            ? Path.Combine(string.IsNullOrWhiteSpace(_env.ContentRootPath) ? Directory.GetCurrentDirectory() : _env.ContentRootPath, "wwwroot")
            : _env.WebRootPath;
        var webRootFullPath = Path.GetFullPath(webRoot);
        var targetDir = Path.GetFullPath(Path.Combine(webRootFullPath, relativeFolder));
        if (!targetDir.StartsWith(webRootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Thư mục lưu ảnh không hợp lệ.");
        }
        return targetDir;
    }

    private static string? NormalizeAiAvatarBaseName(string? assistant)
    {
        var normalized = (assistant ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "travelwai" or "manager" or "quan-ly" or "quanly" => "travelwai-manager-avatar",
            "travelwinne" or "guide" or "huong-dan-vien" or "huongdanvien" => "travelwinne-guide-avatar",
            _ => null
        };
    }

    private static (string Label, string[] FileBaseNames)? NormalizeBackgroundTheme(string? theme)
    {
        var normalized = (theme ?? string.Empty).Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
        return normalized switch
        {
            "light" or "sang" or "nen-sang" or "nền-sáng" => ("nền sáng", new[] { "travelwai-bg-light" }),
            "dark" or "night" or "toi" or "tối" or "nen-toi" or "nền-tối" => ("nền tối", new[] { "travelwai-bg-dark", "travelwai-bg-night" }),
            _ => null
        };
    }

    private static string NormalizeOptimizedUploadExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase) ? ".webp" : string.Empty;
    }

    private static void DeleteFixedImageVariants(string directory, string fileBaseName)
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            var path = Path.Combine(directory, fileBaseName + ext);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    private static async Task SaveFixedImageVariantAsync(string directory, string fileBaseName, IFormFile file)
    {
        var path = Path.Combine(directory, fileBaseName + ".webp");
        await using var stream = System.IO.File.Create(path);
        await file.CopyToAsync(stream);
    }

    private async Task AttachOfferDiscountsAsync(List<Dictionary<string, object?>> accounts)
    {
        var levelSettings = await GetSalesLevelSettingsAsync();
        foreach (var account in accounts)
        {
            var id = Text(account, "id");
            Dictionary<string, object?>? userDoc = string.IsNullOrWhiteSpace(id) ? null : await _repo.GetByIdAsync("users", id);
            var status = string.IsNullOrWhiteSpace(id) ? null : await _offerService.GetStatusAsync(id, Text(account, "email"));
            var automaticDiscount = int.TryParse(status?.GetValueOrDefault("automatic_discount_percent")?.ToString(), out var autoValue)
                ? autoValue
                : (int)(TryDecimal(status?.GetValueOrDefault("discount_percent")) ?? 0m);
            var salesSoldCount = await GetSalesSoldCountAsync(id);
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
        }
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

    private async Task<List<Dictionary<string, object?>>> ReadAccountsAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select id, email, username, role, is_locked, is_protected, created_at, updated_at, last_login_at
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
            select id, email, username, role, is_locked, is_protected, created_at, updated_at, last_login_at
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
        ["last_login_at"] = reader.IsDBNull(8) ? null : reader.GetDateTime(8).ToUniversalTime().ToString("O")
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
            "company" or "business" or "cong ty" or "công ty" or "doanh nghiep" or "doanh nghiệp" => "Business",
            _ => null
        };
    }

    private static bool IsSalesRole(object? role)
    {
        var value = role?.ToString() ?? string.Empty;
        return string.Equals(value, "Sales", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Tour Sales", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBusinessRole(object? role)
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
            foreach (var prefix in new[] { "Admin-", "Business-" })
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
        if (IsSalesRole(normalized) || IsBusinessRole(normalized)) return $"Business-{clean}";
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

    private async Task<int> GetSalesSoldCountAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return 0;
        var orders = await _repo.GetAllAsync("tour_orders", limit: 1000);
        return orders
            .Where(o => string.Equals(Text(o, "status"), "Đã bán", StringComparison.OrdinalIgnoreCase))
            .Where(o => string.Equals(TextAny(o, "tour_sales_id", "tourSalesId", "created_by", "createdBy", "seller_id", "sellerId"), userId, StringComparison.Ordinal))
            .Sum(o => Math.Max(1, GetInt(o, "quantity")));
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
                return NormalizePercent(value, 8m);
            }
        }
        return 8m;
    }
    private static decimal GetOrderCommissionAmount(Dictionary<string, object?> order)
    {
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
        return Math.Round(GetOrderOriginalTotal(order) * GetOrderServicePercent(order) / 100m, 0, MidpointRounding.AwayFromZero);
    }
    private static decimal GetOrderNetRevenue(Dictionary<string, object?> order) => Math.Max(0, GetOrderOriginalTotal(order) - GetOrderDiscountAmount(order) - GetOrderCommissionAmount(order) + GetOrderServiceAmount(order));
    private static int GetInt(Dictionary<string, object?> row, string key) => int.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value) ? value : 0;
    private static int? TryInt(object? value) => int.TryParse(value?.ToString(), out var i) ? i : null;
    private static decimal? TryDecimal(object? value) => decimal.TryParse(value?.ToString(), out var d) ? d : null;
}

public sealed class AdminAccountUpdateRequest
{
    public string? Username { get; set; }
    public string? Role { get; set; }
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
