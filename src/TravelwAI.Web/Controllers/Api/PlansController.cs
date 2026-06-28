using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api")]
public sealed class PlansController : ApiControllerBase
{
    private const string PlansCollection = "plans";
    private const string PlanStatusesCollection = "plan_statuses";
    private const string PlanStatusOptionsCollection = "plan_status_options";
    private const string ProvinceTravelTagsCollection = "province_travel_tags";
    private readonly IDataRepository _repo;
    private readonly IChatService _chatService;

    public PlansController(IAuthService authService, IDataRepository repo, IChatService chatService) : base(authService)
    {
        _repo = repo;
        _chatService = chatService;
    }

    [HttpGet("plans/catalog")]
    public async Task<IActionResult> GetPlanCatalog()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var statusOptions = await GetStatusOptionsAsync();
        var provinceTags = await GetProvinceTagsAsync();
        return Ok(new
        {
            success = true,
            data = new
            {
                status_options = statusOptions,
                province_tags = provinceTags,
                allowed_tags = PlanCatalog.AllowedTags
            },
            message = "Đã tải trạng thái và tag tỉnh thành"
        });
    }

    [HttpGet("plans")]
    public async Task<IActionResult> GetPlans()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        await CleanupExpiredPlansAsync();
        var plans = await GetVisiblePlansForUserAsync(current.userId!);
        var currentStatus = await _repo.GetByIdAsync(PlanStatusesCollection, current.userId!);
        var statusOptions = await GetStatusOptionsAsync();
        var provinceTags = await GetProvinceTagsAsync();
        var savedStatusKey = currentStatus?.GetValueOrDefault("status_key")?.ToString()
                             ?? currentStatus?.GetValueOrDefault("status_text")?.ToString();
        var sameStatusUsers = await SearchUsersWithSameStatusAsync(current.userId!, savedStatusKey);

        return Ok(new
        {
            success = true,
            data = new
            {
                plans,
                current_status = currentStatus,
                same_status_users = sameStatusUsers,
                status_options = statusOptions,
                province_tags = provinceTags,
                allowed_tags = PlanCatalog.AllowedTags
            },
            message = "Đã tải kế hoạch"
        });
    }

    [HttpPost("plans")]
    public async Task<IActionResult> CreatePlan([FromBody] PlanCreateRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        await CleanupExpiredPlansAsync();

        var validationError = ValidateCreateRequest(request);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            return BadRequest(new { success = false, detail = validationError });
        }

        var activeUserPlan = await GetActiveUserPlanAsync(current.userId!);
        if (activeUserPlan is not null)
        {
            return Conflict(new
            {
                success = false,
                detail = "Bạn đang lập hoặc đang tham gia một kế hoạch đang hoạt động. Hãy hủy kế hoạch hoặc chờ nhóm giải tán sau ngày kết thúc 1 ngày rồi mới lập kế hoạch mới."
            });
        }

        var statusOptions = await GetStatusOptionsAsync();
        var provinceTags = await GetProvinceTagsAsync();
        var statusKey = PlanCatalog.ResolveStatusKey(request.PlanStatusKey ?? request.DestinationStatus, statusOptions);
        var statusOption = statusOptions.FirstOrDefault(option => string.Equals(option.GetValueOrDefault("key")?.ToString(), statusKey, StringComparison.OrdinalIgnoreCase));
        if (statusOption is null || !PlanCatalog.IsEnabled(statusOption))
        {
            return BadRequest(new { success = false, detail = "Trạng thái kế hoạch không hợp lệ." });
        }

        var statusLabel = statusOption.GetValueOrDefault("label")?.ToString() ?? request.DestinationStatus.Trim();
        var requiredTags = PlanCatalog.ToStringList(statusOption.GetValueOrDefault("tags"));
        var matchAll = PlanCatalog.GetBool(statusOption, "match_all");
        var provinceName = request.ProvinceName?.Trim() ?? string.Empty;
        var selectedProvince = string.IsNullOrWhiteSpace(provinceName)
            ? null
            : provinceTags.FirstOrDefault(province =>
                string.Equals(province.GetValueOrDefault("name")?.ToString(), provinceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(province.GetValueOrDefault("province_name")?.ToString(), provinceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(province.GetValueOrDefault("id")?.ToString(), provinceName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(provinceName) && selectedProvince is null)
        {
            return BadRequest(new { success = false, detail = "Tỉnh/thành không có trong danh sách 34 tỉnh thành." });
        }

        if (selectedProvince is not null)
        {
            var selectedProvinceTags = PlanCatalog.ToStringList(selectedProvince.GetValueOrDefault("tags"));
            if (!PlanCatalog.MatchesRequiredTags(selectedProvinceTags, requiredTags, matchAll))
            {
                return BadRequest(new { success = false, detail = "Tỉnh/thành không khớp với trạng thái kế hoạch." });
            }
            provinceName = selectedProvince.GetValueOrDefault("name")?.ToString() ?? selectedProvince.GetValueOrDefault("province_name")?.ToString() ?? provinceName;
        }

        var owner = await _repo.GetByIdAsync("users", current.userId!) ?? current.authUser ?? new Dictionary<string, object?>();
        var ownerEmail = GetText(owner, "email")?.ToLowerInvariant();
        var ownerName = GetDisplayName(owner, current.userId!);
        var targetPeople = Math.Clamp(request.TargetPeople, 2, 50);
        var inviteIds = CleanUserIds(request.InviteUserIds)
            .Where(id => !string.Equals(id, current.userId, StringComparison.Ordinal))
            .ToList();
        var computedTags = CleanTextList(request.Tags)
            .Concat(requiredTags)
            .Concat(selectedProvince is null ? Enumerable.Empty<string>() : PlanCatalog.ToStringList(selectedProvince.GetValueOrDefault("tags")))
            .Select(PlanCatalog.NormalizeTag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var data = new Dictionary<string, object?>
        {
            ["title"] = request.Title.Trim(),
            ["description"] = request.Description?.Trim() ?? string.Empty,
            ["destination_status"] = statusLabel,
            ["destination_key"] = statusKey,
            ["plan_status_key"] = statusKey,
            ["plan_status_label"] = statusLabel,
            ["required_tags"] = requiredTags,
            ["match_all_tags"] = matchAll,
            ["province_name"] = provinceName,
            ["province_tags"] = selectedProvince is null ? new List<string>() : PlanCatalog.ToStringList(selectedProvince.GetValueOrDefault("tags")),
            ["start_date"] = request.StartDate.Trim(),
            ["end_date"] = request.EndDate.Trim(),
            ["target_people"] = targetPeople,
            ["budget"] = request.Budget,
            ["currency"] = string.IsNullOrWhiteSpace(request.Currency) ? "VND" : request.Currency.Trim().ToUpperInvariant(),
            ["tags"] = computedTags,
            ["owner_user_id"] = current.userId,
            ["owner_name"] = ownerName,
            ["owner_email"] = ownerEmail,
            ["member_user_ids"] = new List<string> { current.userId! },
            ["member_emails"] = string.IsNullOrWhiteSpace(ownerEmail) ? new List<string>() : new List<string> { ownerEmail },
            ["invited_user_ids"] = inviteIds,
            ["status"] = "forming",
            ["schedule_id"] = null,
            ["conversation_id"] = string.Empty,
            ["group_disbands_at"] = GetDisbandTime(request.EndDate),
            ["created_at"] = DateTime.UtcNow,
            ["updated_at"] = DateTime.UtcNow
        };

        var planId = await _repo.AddAsync(PlansCollection, data);
        if (string.IsNullOrWhiteSpace(planId))
        {
            return StatusCode(500, new { success = false, detail = "Không thể tạo kế hoạch" });
        }

        await SetCurrentUserPlanStatusAsync(current.userId!, statusKey, statusLabel, requiredTags, matchAll);
        await TryMakePlanReadyAsync(planId, current.userId!);
        var plan = await GetHydratedPlanAsync(planId, current.userId!);

        return Ok(new { success = true, data = plan, plan_id = planId, message = "Đã lập kế hoạch" });
    }

    [HttpPost("plans/status")]
    public async Task<IActionResult> SetPlanStatus([FromBody] PlanStatusRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var statusOptions = await GetStatusOptionsAsync();
        var statusKey = PlanCatalog.ResolveStatusKey(request.StatusKey ?? request.StatusText, statusOptions);
        var statusOption = statusOptions.FirstOrDefault(option => string.Equals(option.GetValueOrDefault("key")?.ToString(), statusKey, StringComparison.OrdinalIgnoreCase));
        if (statusOption is null || !PlanCatalog.IsEnabled(statusOption))
        {
            return BadRequest(new { success = false, detail = "Bạn chưa chọn trạng thái muốn đi." });
        }

        var label = statusOption.GetValueOrDefault("label")?.ToString() ?? statusKey;
        var tags = PlanCatalog.ToStringList(statusOption.GetValueOrDefault("tags"));
        var matchAll = PlanCatalog.GetBool(statusOption, "match_all");
        await SetCurrentUserPlanStatusAsync(current.userId!, statusKey, label, tags, matchAll);
        var sameStatusUsers = await SearchUsersWithSameStatusAsync(current.userId!, statusKey);
        return Ok(new { success = true, data = sameStatusUsers, status = statusOption, message = "Đã cập nhật trạng thái muốn đi" });
    }

    [HttpGet("plans/status/matches")]
    public async Task<IActionResult> GetPlanStatusMatches([FromQuery] string? status, [FromQuery] string? statusKey)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        if (string.IsNullOrWhiteSpace(statusKey) && string.IsNullOrWhiteSpace(status))
        {
            var saved = await _repo.GetByIdAsync(PlanStatusesCollection, current.userId!);
            statusKey = saved?.GetValueOrDefault("status_key")?.ToString();
            status = saved?.GetValueOrDefault("status_text")?.ToString();
        }

        var users = await SearchUsersWithSameStatusAsync(current.userId!, statusKey ?? status);
        return Ok(new { success = true, data = users, message = "Đã tìm người" });
    }

    [HttpPost("plans/{planId}/join")]
    public async Task<IActionResult> JoinPlan(string planId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        await CleanupExpiredPlansAsync();
        var plan = await _repo.GetByIdAsync(PlansCollection, planId);
        if (plan is null) return NotFound(new { success = false, detail = "Không tìm thấy kế hoạch" });
        if (!IsJoinable(plan, current.userId!)) return StatusCode(403, new { success = false, detail = "Bạn không thể tham gia kế hoạch này" });

        var currentActivePlan = await GetActiveUserPlanAsync(current.userId!);
        var currentActivePlanId = currentActivePlan?.GetValueOrDefault("id")?.ToString();
        if (!string.IsNullOrWhiteSpace(currentActivePlanId) && !string.Equals(currentActivePlanId, planId, StringComparison.Ordinal))
        {
            return Conflict(new { success = false, detail = "Bạn đang lập hoặc đang tham gia một kế hoạch khác. Hãy hủy hoặc chờ kế hoạch đó giải tán rồi mới tham gia kế hoạch mới." });
        }

        var members = ToStringList(plan.GetValueOrDefault("member_user_ids"));
        if (!members.Contains(current.userId!, StringComparer.Ordinal)) members.Add(current.userId!);

        var invited = ToStringList(plan.GetValueOrDefault("invited_user_ids"));
        invited.RemoveAll(id => string.Equals(id, current.userId, StringComparison.Ordinal));

        var memberEmails = ToStringList(plan.GetValueOrDefault("member_emails"));
        var user = await _repo.GetByIdAsync("users", current.userId!);
        var email = GetText(user, "email")?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(email) && !memberEmails.Contains(email, StringComparer.OrdinalIgnoreCase)) memberEmails.Add(email);

        await _repo.UpdateAsync(PlansCollection, planId, new Dictionary<string, object?>
        {
            ["member_user_ids"] = members,
            ["member_emails"] = memberEmails,
            ["invited_user_ids"] = invited,
            ["updated_at"] = DateTime.UtcNow
        });

        await TryMakePlanReadyAsync(planId, current.userId!);
        var updated = await GetHydratedPlanAsync(planId, current.userId!);
        return Ok(new { success = true, data = updated, message = "Đã tham gia kế hoạch" });
    }

    [HttpPost("plans/{planId}/invite")]
    public async Task<IActionResult> InviteUsers(string planId, [FromBody] PlanInviteRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var plan = await _repo.GetByIdAsync(PlansCollection, planId);
        if (plan is null) return NotFound(new { success = false, detail = "Không tìm thấy kế hoạch" });
        if (!IsOwner(plan, current.userId!)) return StatusCode(403, new { success = false, detail = "Chỉ người lập kế hoạch mới được mời thêm người" });
        if (!IsActivePlan(plan)) return BadRequest(new { success = false, detail = "Kế hoạch này đã kết thúc hoặc đã hủy" });

        var invited = ToStringList(plan.GetValueOrDefault("invited_user_ids"));
        var members = ToStringList(plan.GetValueOrDefault("member_user_ids"));
        foreach (var userId in CleanUserIds(request.UserIds))
        {
            if (string.Equals(userId, current.userId, StringComparison.Ordinal)) continue;
            if (members.Contains(userId, StringComparer.Ordinal)) continue;
            if (!invited.Contains(userId, StringComparer.Ordinal)) invited.Add(userId);
        }

        await _repo.UpdateAsync(PlansCollection, planId, new Dictionary<string, object?>
        {
            ["invited_user_ids"] = invited,
            ["updated_at"] = DateTime.UtcNow
        });

        var updated = await GetHydratedPlanAsync(planId, current.userId!);
        return Ok(new { success = true, data = updated, message = "Đã mời người dùng vào kế hoạch" });
    }

    [HttpPost("plans/{planId}/create-group")]
    public async Task<IActionResult> CreatePlanGroup(string planId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        await CleanupExpiredPlansAsync();
        var plan = await _repo.GetByIdAsync(PlansCollection, planId);
        if (plan is null) return NotFound(new { success = false, detail = "Không tìm thấy kế hoạch" });
        if (!IsOwner(plan, current.userId!)) return StatusCode(403, new { success = false, detail = "Chỉ người lập kế hoạch mới được tạo nhóm" });

        var status = plan.GetValueOrDefault("status")?.ToString() ?? "forming";
        if (!string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "group_created", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, detail = "Kế hoạch chưa đủ người nên chưa thể tạo nhóm" });
        }

        var members = ToStringList(plan.GetValueOrDefault("member_user_ids"));
        var scheduleId = plan.GetValueOrDefault("schedule_id")?.ToString();
        if (string.IsNullOrWhiteSpace(scheduleId))
        {
            scheduleId = await CreateScheduleFromPlanAsync(plan, members);
            if (string.IsNullOrWhiteSpace(scheduleId))
            {
                return StatusCode(500, new { success = false, detail = "Không thể tạo lịch trình cho kế hoạch" });
            }
            await _repo.UpdateAsync(PlansCollection, planId, new Dictionary<string, object?>
            {
                ["schedule_id"] = scheduleId,
                ["updated_at"] = DateTime.UtcNow
            });
        }

        var existingConversationId = plan.GetValueOrDefault("conversation_id")?.ToString();
        if (!string.IsNullOrWhiteSpace(existingConversationId))
        {
            return Ok(new { success = true, conversation_id = existingConversationId, schedule_id = scheduleId, message = "Nhóm kế hoạch đã có" });
        }

        var otherMembers = members.Where(id => !string.Equals(id, current.userId, StringComparison.Ordinal)).ToList();
        if (otherMembers.Count == 0)
        {
            return BadRequest(new { success = false, detail = "Cần ít nhất 2 người trong kế hoạch để tạo nhóm" });
        }

        var groupName = plan.GetValueOrDefault("title")?.ToString();
        if (string.IsNullOrWhiteSpace(groupName)) groupName = "Kế hoạch du lịch";

        var conversationId = await _chatService.CreateGroupConversationAsync(current.userId!, otherMembers, groupName);
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return StatusCode(500, new { success = false, detail = "Không thể tạo nhóm cho kế hoạch" });
        }

        await _repo.UpdateAsync(PlansCollection, planId, new Dictionary<string, object?>
        {
            ["conversation_id"] = conversationId,
            ["status"] = "group_created",
            ["group_disbands_at"] = GetDisbandTime(plan.GetValueOrDefault("end_date")?.ToString()),
            ["updated_at"] = DateTime.UtcNow
        });

        return Ok(new { success = true, conversation_id = conversationId, schedule_id = scheduleId, message = "Đã tạo nhóm và lịch trình kế hoạch" });
    }

    [HttpDelete("plans/{planId}")]
    public async Task<IActionResult> CancelPlan(string planId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var plan = await _repo.GetByIdAsync(PlansCollection, planId);
        if (plan is null) return NotFound(new { success = false, detail = "Không tìm thấy kế hoạch" });
        if (!IsOwner(plan, current.userId!)) return StatusCode(403, new { success = false, detail = "Chỉ người lập kế hoạch mới được hủy" });

        var conversationId = plan.GetValueOrDefault("conversation_id")?.ToString();
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            await _repo.DeleteWhereEqualAsync("messages", "conversation_id", conversationId);
            await _repo.DeleteAsync("conversations", conversationId);
        }

        var scheduleId = plan.GetValueOrDefault("schedule_id")?.ToString();
        if (!string.IsNullOrWhiteSpace(scheduleId))
        {
            await _repo.DeleteAsync("schedules", scheduleId);
        }
        await _repo.DeleteWhereEqualAsync("schedules", "created_from_plan_id", planId);

        await _repo.UpdateAsync(PlansCollection, planId, new Dictionary<string, object?>
        {
            ["status"] = "cancelled",
            ["cancelled_at"] = DateTime.UtcNow,
            ["conversation_id"] = string.Empty,
            ["schedule_id"] = null,
            ["updated_at"] = DateTime.UtcNow
        });

        return Ok(new { success = true, message = "Đã hủy kế hoạch và xóa nhóm, lịch trình liên quan" });
    }

    private async Task<List<Dictionary<string, object?>>> GetVisiblePlansForUserAsync(string userId)
    {

        var all = (await _repo.GetAllAsync(PlansCollection, limit: 250))
            .Where(plan => !string.Equals(plan.GetValueOrDefault("status")?.ToString(), "cancelled", StringComparison.OrdinalIgnoreCase))
            .Where(plan => !string.Equals(plan.GetValueOrDefault("status")?.ToString(), "expired", StringComparison.OrdinalIgnoreCase))
            .Where(plan => !IsPlanExpired(plan))
            .ToList();

        var result = new List<Dictionary<string, object?>>();
        foreach (var plan in all)
        {
            result.Add(await HydratePlanAsync(plan, userId));
        }

        return result.OrderByDescending(plan => plan.GetValueOrDefault("updated_at")?.ToString()).ToList();
    }

    private async Task<Dictionary<string, object?>?> GetHydratedPlanAsync(string planId, string userId)
    {
        var plan = await _repo.GetByIdAsync(PlansCollection, planId);
        return plan is null ? null : await HydratePlanAsync(plan, userId);
    }

    private async Task<Dictionary<string, object?>> HydratePlanAsync(Dictionary<string, object?> plan, string userId)
    {
        var ownerId = plan.GetValueOrDefault("owner_user_id")?.ToString() ?? string.Empty;
        var members = ToStringList(plan.GetValueOrDefault("member_user_ids"));
        var invited = ToStringList(plan.GetValueOrDefault("invited_user_ids"));
        var target = ToInt(plan.GetValueOrDefault("target_people"), 2);
        var memberCount = members.Count;
        var progress = target <= 0 ? 0 : Math.Clamp((int)Math.Round(memberCount * 100d / target), 0, 100);

        plan["is_owner"] = string.Equals(ownerId, userId, StringComparison.Ordinal);
        plan["is_member"] = members.Contains(userId, StringComparer.Ordinal);
        plan["is_invited"] = invited.Contains(userId, StringComparer.Ordinal);
        plan["member_count"] = memberCount;
        plan["progress_percent"] = progress;
        plan["remaining_people"] = Math.Max(0, target - memberCount);
        plan["destination_display"] = BuildPlanDestinationText(plan);
        if (!plan.ContainsKey("plan_status_key")) plan["plan_status_key"] = plan.GetValueOrDefault("destination_key")?.ToString() ?? string.Empty;
        if (!plan.ContainsKey("plan_status_label")) plan["plan_status_label"] = plan.GetValueOrDefault("destination_status")?.ToString() ?? string.Empty;
        plan["can_join"] = IsJoinable(plan, userId);
        plan["can_create_group"] = plan["is_owner"] is true
            && memberCount >= target
            && string.IsNullOrWhiteSpace(plan.GetValueOrDefault("conversation_id")?.ToString())
            && !IsPlanExpired(plan);

        plan["owner"] = await BuildUserCardAsync(ownerId);
        plan["members"] = await BuildUserCardsAsync(members);
        plan["invited_users"] = await BuildUserCardsAsync(invited);

        return plan;
    }

    private async Task<Dictionary<string, object?>?> GetActiveUserPlanAsync(string userId)
    {
        var plans = await _repo.GetAllAsync(PlansCollection, limit: 250);
        return plans.FirstOrDefault(plan =>
        {
            if (!IsActivePlan(plan)) return false;
            if (IsOwner(plan, userId)) return true;
            var members = ToStringList(plan.GetValueOrDefault("member_user_ids"));
            return members.Contains(userId, StringComparer.Ordinal);
        });
    }

    private async Task TryMakePlanReadyAsync(string planId, string currentUserId)
    {
        var plan = await _repo.GetByIdAsync(PlansCollection, planId);
        if (plan is null || !IsActivePlan(plan)) return;

        var members = ToStringList(plan.GetValueOrDefault("member_user_ids"));
        var target = ToInt(plan.GetValueOrDefault("target_people"), 2);
        if (members.Count < target) return;

        var scheduleId = plan.GetValueOrDefault("schedule_id")?.ToString();
        if (string.IsNullOrWhiteSpace(scheduleId))
        {
            scheduleId = await CreateScheduleFromPlanAsync(plan, members);
        }

        if (!string.IsNullOrWhiteSpace(scheduleId))
        {
            await _repo.UpdateAsync(PlansCollection, planId, new Dictionary<string, object?>
            {
                ["status"] = string.IsNullOrWhiteSpace(plan.GetValueOrDefault("conversation_id")?.ToString()) ? "ready" : "group_created",
                ["schedule_id"] = scheduleId,
                ["ready_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });
        }
    }

    private async Task<string?> CreateScheduleFromPlanAsync(Dictionary<string, object?> plan, List<string> members)
    {
        var ownerId = plan.GetValueOrDefault("owner_user_id")?.ToString();
        if (string.IsNullOrWhiteSpace(ownerId)) return null;

        var memberEmails = new List<string>();
        foreach (var memberId in members.Where(id => !string.Equals(id, ownerId, StringComparison.Ordinal)))
        {
            var member = await _repo.GetByIdAsync("users", memberId);
            var email = GetText(member, "email")?.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(email) && !memberEmails.Contains(email, StringComparer.OrdinalIgnoreCase)) memberEmails.Add(email);
        }

        var destination = BuildPlanDestinationText(plan);
        var title = plan.GetValueOrDefault("title")?.ToString() ?? $"Kế hoạch {destination}";
        var startDate = plan.GetValueOrDefault("start_date")?.ToString() ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var endDate = plan.GetValueOrDefault("end_date")?.ToString() ?? startDate;
        var tags = ToStringList(plan.GetValueOrDefault("tags"));
        if (!tags.Contains("kế hoạch nhóm", StringComparer.OrdinalIgnoreCase)) tags.Add("kế hoạch nhóm");

        var scheduleData = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["description"] = BuildScheduleDescription(plan),
            ["start_date"] = startDate,
            ["end_date"] = endDate,
            ["budget"] = plan.GetValueOrDefault("budget"),
            ["currency"] = plan.GetValueOrDefault("currency")?.ToString() ?? "VND",
            ["shared_emails"] = memberEmails,
            ["shared_with_user_ids"] = members.Where(id => !string.Equals(id, ownerId, StringComparison.Ordinal)).ToList(),
            ["tags"] = tags,
            ["days"] = BuildSimpleDays(startDate, endDate, destination),
            ["user_id"] = ownerId,
            ["created_from_plan_id"] = plan.GetValueOrDefault("id"),
            ["created_at"] = DateTime.UtcNow,
            ["updated_at"] = DateTime.UtcNow
        };

        return await _repo.AddAsync("schedules", scheduleData);
    }

    private static string BuildScheduleDescription(Dictionary<string, object?> plan)
    {
        var description = plan.GetValueOrDefault("description")?.ToString() ?? string.Empty;
        var destination = BuildPlanDestinationText(plan);
        var members = ToStringList(plan.GetValueOrDefault("member_user_ids"));
        var target = ToInt(plan.GetValueOrDefault("target_people"), 2);
        var extra = $"Lịch trình tự động tạo từ kế hoạch nhóm: {destination}. Số người: {members.Count}/{target}.";
        return string.IsNullOrWhiteSpace(description) ? extra : description.Trim() + "\n" + extra;
    }

    private static List<Dictionary<string, object?>> BuildSimpleDays(string startDateText, string endDateText, string destination)
    {
        var start = ParseDateOnly(startDateText) ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var end = ParseDateOnly(endDateText) ?? start;
        if (end < start) end = start;

        var totalDays = Math.Clamp(end.DayNumber - start.DayNumber + 1, 1, 14);
        var days = new List<Dictionary<string, object?>>();
        for (var i = 0; i < totalDays; i++)
        {
            var date = start.AddDays(i);
            days.Add(new Dictionary<string, object?>
            {
                ["day_number"] = i + 1,
                ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["destinations"] = new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["name"] = i == 0 ? $"Khởi hành đến {destination}" : $"Khám phá {destination}",
                        ["description"] = i == 0 ? "Tập trung nhóm, di chuyển, nhận phòng và thống nhất lịch đi chung." : "Tham quan các điểm phù hợp với nhóm và điều chỉnh theo thời gian thực tế.",
                        ["estimated_duration"] = "3 giờ",
                        ["time_phase"] = "Sáng",
                        ["time_range"] = "08:00 - 11:00"
                    },
                    new()
                    {
                        ["name"] = "Ăn uống và nghỉ ngơi",
                        ["description"] = "Chọn quán ăn phù hợp ngân sách và khẩu vị của nhóm.",
                        ["estimated_duration"] = "2 giờ",
                        ["time_phase"] = "Trưa",
                        ["time_range"] = "11:30 - 13:30"
                    },
                    new()
                    {
                        ["name"] = i == totalDays - 1 ? "Tổng kết chuyến đi" : "Check-in và hoạt động nhóm",
                        ["description"] = i == totalDays - 1 ? "Mua quà, dọn đồ, trả phòng và kết thúc chuyến đi." : "Chụp ảnh, vui chơi và trao đổi kế hoạch ngày tiếp theo.",
                        ["estimated_duration"] = "4 giờ",
                        ["time_phase"] = "Chiều",
                        ["time_range"] = "14:00 - 18:00"
                    }
                }
            });
        }

        return days;
    }

    private async Task SetCurrentUserPlanStatusAsync(string userId, string statusKey, string statusLabel, List<string> tags, bool matchAll)
    {
        var user = await _repo.GetByIdAsync("users", userId);
        await _repo.SetAsync(PlanStatusesCollection, userId, new Dictionary<string, object?>
        {
            ["id"] = userId,
            ["user_id"] = userId,
            ["status_text"] = statusLabel,
            ["status_key"] = statusKey,
            ["tags"] = tags,
            ["match_all_tags"] = matchAll,
            ["display_name"] = GetDisplayName(user, userId),
            ["email"] = GetText(user, "email"),
            ["profilePic"] = GetText(user, "profilePic"),
            ["updated_at"] = DateTime.UtcNow
        }, merge: true);
    }

    private async Task<List<Dictionary<string, object?>>> SearchUsersWithSameStatusAsync(string currentUserId, string? statusKeyOrLabel)
    {
        var statusOptions = await GetStatusOptionsAsync();
        var key = PlanCatalog.ResolveStatusKey(statusKeyOrLabel, statusOptions);
        if (string.IsNullOrWhiteSpace(key)) return new List<Dictionary<string, object?>>();

        var statuses = await _repo.GetAllAsync(PlanStatusesCollection, limit: 200);
        var matched = statuses
            .Where(status => !string.Equals(status.GetValueOrDefault("user_id")?.ToString(), currentUserId, StringComparison.Ordinal))
            .Where(status => string.Equals(status.GetValueOrDefault("status_key")?.ToString(), key, StringComparison.Ordinal))
            .Take(30)
            .ToList();

        var users = new List<Dictionary<string, object?>>();
        foreach (var status in matched)
        {
            var userId = status.GetValueOrDefault("user_id")?.ToString();
            if (string.IsNullOrWhiteSpace(userId)) continue;
            var user = await BuildUserCardAsync(userId);
            user["status_text"] = status.GetValueOrDefault("status_text")?.ToString();
            user["status_key"] = status.GetValueOrDefault("status_key")?.ToString();
            user["updated_at"] = status.GetValueOrDefault("updated_at")?.ToString();
            users.Add(user);
        }

        return users;
    }

    private async Task CleanupExpiredPlansAsync()
    {
        var plans = await _repo.GetAllAsync(PlansCollection, limit: 250);
        foreach (var plan in plans)
        {
            if (!IsPlanExpired(plan)) continue;

            var status = plan.GetValueOrDefault("status")?.ToString();
            if (string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)) continue;

            var conversationId = plan.GetValueOrDefault("conversation_id")?.ToString();
            if (!string.IsNullOrWhiteSpace(conversationId))
            {
                await _repo.DeleteWhereEqualAsync("messages", "conversation_id", conversationId);
                await _repo.DeleteAsync("conversations", conversationId);
            }

            var planId = plan.GetValueOrDefault("id")?.ToString();
            if (string.IsNullOrWhiteSpace(planId)) continue;
            await _repo.UpdateAsync(PlansCollection, planId, new Dictionary<string, object?>
            {
                ["status"] = "expired",
                ["conversation_id"] = string.Empty,
                ["expired_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow
            });
        }
    }

    private async Task<List<Dictionary<string, object?>>> BuildUserCardsAsync(IEnumerable<string> userIds)
    {
        var users = new List<Dictionary<string, object?>>();
        foreach (var userId in userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            users.Add(await BuildUserCardAsync(userId));
        }
        return users;
    }

    private async Task<Dictionary<string, object?>> BuildUserCardAsync(string userId)
    {
        var user = string.IsNullOrWhiteSpace(userId) ? null : await _repo.GetByIdAsync("users", userId);
        return new Dictionary<string, object?>
        {
            ["id"] = userId,
            ["username"] = GetDisplayName(user, userId),
            ["name"] = GetDisplayName(user, userId),
            ["email"] = GetText(user, "email"),
            ["profilePic"] = GetText(user, "profilePic")
        };
    }

    private static bool IsJoinable(Dictionary<string, object?> plan, string userId)
    {
        if (!IsActivePlan(plan) || IsPlanExpired(plan)) return false;
        var members = ToStringList(plan.GetValueOrDefault("member_user_ids"));
        if (members.Contains(userId, StringComparer.Ordinal)) return false;

        var invited = ToStringList(plan.GetValueOrDefault("invited_user_ids"));
        if (invited.Contains(userId, StringComparer.Ordinal)) return true;

        return !string.IsNullOrWhiteSpace(plan.GetValueOrDefault("destination_key")?.ToString());
    }

    private static bool PlanMatchesStatus(Dictionary<string, object?> plan, string statusKey, List<string> requiredTags, bool matchAll)
    {
        var planStatusKey = plan.GetValueOrDefault("plan_status_key")?.ToString()
                            ?? plan.GetValueOrDefault("destination_key")?.ToString()
                            ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(statusKey)
            && string.Equals(planStatusKey, statusKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var tags = ToStringList(plan.GetValueOrDefault("tags"))
            .Concat(ToStringList(plan.GetValueOrDefault("province_tags")))
            .ToList();
        return PlanCatalog.MatchesRequiredTags(tags, requiredTags, matchAll);
    }

    private static bool IsOwner(Dictionary<string, object?> plan, string userId)
    {
        return string.Equals(plan.GetValueOrDefault("owner_user_id")?.ToString(), userId, StringComparison.Ordinal);
    }

    private static bool IsActivePlan(Dictionary<string, object?> plan)
    {
        var status = plan.GetValueOrDefault("status")?.ToString() ?? "forming";
        if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase)) return false;

        return !IsPlanExpired(plan);
    }

    private static bool IsPlanExpired(Dictionary<string, object?> plan)
    {
        var disbandText = plan.GetValueOrDefault("group_disbands_at")?.ToString();
        if (DateTimeOffset.TryParse(disbandText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var disbandAt))
        {
            return DateTimeOffset.UtcNow >= disbandAt.ToUniversalTime();
        }

        var end = ParseDateOnly(plan.GetValueOrDefault("end_date")?.ToString());
        if (end is null) return false;
        return DateOnly.FromDateTime(DateTime.UtcNow) > end.Value.AddDays(1);
    }

    private static string? ValidateCreateRequest(PlanCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return "Bạn chưa nhập tên kế hoạch.";
        if (string.IsNullOrWhiteSpace(request.PlanStatusKey) && string.IsNullOrWhiteSpace(request.DestinationStatus)) return "Bạn chưa chọn trạng thái muốn đi.";
        if (string.IsNullOrWhiteSpace(request.StartDate) || ParseDateOnly(request.StartDate) is null) return "Ngày bắt đầu không hợp lệ.";
        if (string.IsNullOrWhiteSpace(request.EndDate) || ParseDateOnly(request.EndDate) is null) return "Ngày kết thúc không hợp lệ.";

        var start = ParseDateOnly(request.StartDate)!.Value;
        var end = ParseDateOnly(request.EndDate)!.Value;
        if (end < start) return "Ngày kết thúc phải sau hoặc bằng ngày bắt đầu.";
        if (request.TargetPeople < 2) return "Số người cần ít nhất là 2.";
        return null;
    }

    private static DateTimeOffset GetDisbandTime(string? endDateText)
    {
        var end = ParseDateOnly(endDateText) ?? DateOnly.FromDateTime(DateTime.UtcNow);
        return new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
    }

    private static DateOnly? ParseDateOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static List<string> CleanUserIds(IEnumerable<string>? ids)
    {
        return (ids ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> CleanTextList(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ToStringList(object? value)
    {
        if (value is null) return new List<string>();
        if (value is IEnumerable<string> strings) return strings.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (value is IEnumerable<object> objects) return objects.Select(o => o?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
        return new List<string>();
    }

    private static int ToInt(object? value, int fallback)
    {
        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)Math.Round(d),
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => fallback
        };
    }

    private static string? GetText(Dictionary<string, object?>? source, params string[] keys)
    {
        if (source is null) return null;
        foreach (var key in keys)
        {
            if (source.TryGetValue(key, out var value) && value is not null)
            {
                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return null;
    }

    private static string GetDisplayName(Dictionary<string, object?>? user, string fallback)
    {
        return GetText(user, "displayName", "username", "name", "email") ?? fallback;
    }

    private static string NormalizePlanKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim().ToLowerInvariant();
        text = text.Replace('đ', 'd');
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark) builder.Append(ch);
        }

        text = builder.ToString().Normalize(NormalizationForm.FormC);
        text = Regex.Replace(text, "\\b(muon|muốn|di|đi|toi|toi|den|đến|du lich|dulich)\\b", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "[^a-z0-9]+", " ").Trim();
        return Regex.Replace(text, "\\s+", " ");
    }

    private async Task<List<Dictionary<string, object?>>> GetStatusOptionsAsync()
    {
        var saved = await _repo.GetAllAsync(PlanStatusOptionsCollection, limit: 100);
        var defaults = PlanCatalog.DefaultStatusOptions();
        if (saved.Count == 0) return defaults;

        var merged = defaults.ToDictionary(x => x.GetValueOrDefault("key")?.ToString() ?? string.Empty, x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var item in saved)
        {
            var key = item.GetValueOrDefault("key")?.ToString() ?? item.GetValueOrDefault("id")?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)) continue;
            item["key"] = key;
            item["id"] = key;
            item["tags"] = PlanCatalog.CleanTags(PlanCatalog.ToStringList(item.GetValueOrDefault("tags")));
            item["color"] = PlanCatalog.ResolveStatusColor(key, item.GetValueOrDefault("color")?.ToString(), PlanCatalog.ToStringList(item.GetValueOrDefault("tags")));
            if (!item.ContainsKey("enabled")) item["enabled"] = true;
            merged[key] = item;
        }

        return merged.Values
            .Where(PlanCatalog.IsEnabled)
            .OrderBy(item => PlanCatalog.GetInt(item, "order", 999))
            .ThenBy(item => item.GetValueOrDefault("label")?.ToString())
            .ToList();
    }

    private async Task<List<Dictionary<string, object?>>> GetProvinceTagsAsync()
    {
        var saved = await _repo.GetAllAsync(ProvinceTravelTagsCollection, limit: 100);
        var defaults = PlanCatalog.DefaultProvinceTags();
        if (saved.Count == 0) return defaults;

        var merged = defaults.ToDictionary(x => x.GetValueOrDefault("name")?.ToString() ?? string.Empty, x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var item in saved)
        {
            var name = item.GetValueOrDefault("name")?.ToString()
                       ?? item.GetValueOrDefault("province_name")?.ToString()
                       ?? string.Empty;
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

    private static string BuildPlanDestinationText(Dictionary<string, object?> plan)
    {
        var label = plan.GetValueOrDefault("plan_status_label")?.ToString()
                    ?? plan.GetValueOrDefault("destination_status")?.ToString()
                    ?? string.Empty;
        var province = plan.GetValueOrDefault("province_name")?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(province)) return plan.GetValueOrDefault("title")?.ToString() ?? "chuyến đi";
        if (string.IsNullOrWhiteSpace(province)) return label;
        if (string.IsNullOrWhiteSpace(label)) return province;
        return $"{label} - {province}";
    }
}

public sealed class PlanCreateRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DestinationStatus { get; set; } = string.Empty;
    public string? PlanStatusKey { get; set; }
    public string? ProvinceName { get; set; }
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    public int TargetPeople { get; set; } = 2;
    public double? Budget { get; set; }
    public string Currency { get; set; } = "VND";
    public List<string> Tags { get; set; } = new();
    public List<string> InviteUserIds { get; set; } = new();
}

public sealed class PlanInviteRequest
{
    public List<string> UserIds { get; set; } = new();
}

public sealed class PlanStatusRequest
{
    public string StatusText { get; set; } = string.Empty;
    public string? StatusKey { get; set; }
}
