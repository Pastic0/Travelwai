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
                tourSalesAccounts = accounts.Count(a => IsRole(a.GetValueOrDefault("role"), "Tour Sales")),
                tours = tours.Count,
                activeTours = tours.Count(t => string.Equals(Text(t, "status"), "Đang bán", StringComparison.OrdinalIgnoreCase) && !(GetInt(t, "slots") > 0 && GetInt(t, "sold") >= GetInt(t, "slots"))),
                tourOrders = orders.Count,
                schedules = schedules.Count,
                planStatuses = statusOptions.Count,
                provinces = provinceTags.Count,
                posts = posts.Count(p => !IsDeletedPost(p)),
                revenue = orders.Sum(GetOrderTotal)
            }
        });
    }

    [HttpGet("accounts")]
    public async Task<IActionResult> Accounts()
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var accounts = await ReadAccountsAsync();
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

    [HttpPut("accounts/{id}")]
    public async Task<IActionResult> UpdateAccount(string id, [FromBody] AdminAccountUpdateRequest request)
    {
        var access = await RequireAdminAsync();
        if (!access.ok) return access.error!;

        var account = await ReadAccountAsync(id);
        if (account is null) return NotFound(new { success = false, message = "Không tìm thấy tài khoản" });

        var email = Text(account, "email").ToLowerInvariant();
        var isProtectedAdmin = IsProtectedAdmin(account);
        var username = string.IsNullOrWhiteSpace(request.Username) ? Text(account, "username") : request.Username.Trim();
        var role = NormalizeRole(string.IsNullOrWhiteSpace(request.Role) ? Text(account, "role") : request.Role);
        var isLocked = request.IsLocked ?? IsTruthy(account.GetValueOrDefault("is_locked"));

        if (role is null) return BadRequest(new { success = false, message = "Vai trò không hợp lệ. Chỉ dùng User, Admin hoặc Tour Sales." });

        if (isProtectedAdmin)
        {
            role = "Admin";
            isLocked = false;
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
        foreach (var account in accounts)
        {
            var id = Text(account, "id");
            var status = string.IsNullOrWhiteSpace(id) ? null : await _offerService.GetStatusAsync(id, Text(account, "email"));
            var discount = int.TryParse(status?.GetValueOrDefault("discount_percent")?.ToString(), out var value) ? value : 0;
            account["offer_discount_percent"] = discount;
            account["offerDiscountPercent"] = discount;
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

    private static string? NormalizeRole(string? role)
    {
        var value = (role ?? string.Empty).Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        value = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return value switch
        {
            "user" or "nguoi dung" or "người dùng" => "User",
            "admin" => "Admin",
            "tour sales" or "toursales" or "tour sale" or "ban tour" or "bán tour" => "Tour Sales",
            _ => null
        };
    }

    private static bool IsRole(object? role, string expected) => string.Equals(role?.ToString(), expected, StringComparison.OrdinalIgnoreCase);
    private static string Text(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    private static bool IsTruthy(object? value) => value is bool b ? b : bool.TryParse(value?.ToString(), out var parsed) && parsed;
    private static decimal GetOrderTotal(Dictionary<string, object?> order) => TryDecimal(order.GetValueOrDefault("total_price")) ?? TryDecimal(order.GetValueOrDefault("totalPrice")) ?? 0;
    private static int GetInt(Dictionary<string, object?> row, string key) => int.TryParse(row.GetValueOrDefault(key)?.ToString(), out var value) ? value : 0;
    private static int? TryInt(object? value) => int.TryParse(value?.ToString(), out var i) ? i : null;
    private static decimal? TryDecimal(object? value) => decimal.TryParse(value?.ToString(), out var d) ? d : null;
}

public sealed class AdminAccountUpdateRequest
{
    public string? Username { get; set; }
    public string? Role { get; set; }
    public bool? IsLocked { get; set; }
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
