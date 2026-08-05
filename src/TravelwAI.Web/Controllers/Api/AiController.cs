using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TravelwAI.Business.Interfaces;
using TravelwAI.Web.Models;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Controllers.Api;

[Route("api/ai")]
public sealed class AiController : ApiControllerBase
{
    private readonly OllamaAiService _ollama;
    private readonly AiKnowledgeContextService _knowledge;
    private readonly AiChatJobService _jobs;
    private readonly IFileStorageService _fileStorage;
    private readonly RoleFeaturePolicyService _rolePolicies;
    private readonly AiUsageLimitService _usageLimits;
    private readonly ILogger<AiController> _logger;

    public AiController(
        IAuthService authService,
        OllamaAiService ollama,
        AiKnowledgeContextService knowledge,
        AiChatJobService jobs,
        IFileStorageService fileStorage,
        RoleFeaturePolicyService rolePolicies,
        AiUsageLimitService usageLimits,
        ILogger<AiController> logger) : base(authService)
    {
        _ollama = ollama;
        _knowledge = knowledge;
        _jobs = jobs;
        _fileStorage = fileStorage;
        _rolePolicies = rolePolicies;
        _usageLimits = usageLimits;
        _logger = logger;
    }

    [HttpPost("attachments")]
    [RequestSizeLimit(60 * 1024 * 1024)]
    public async Task<IActionResult> UploadAttachments([FromForm] List<IFormFile>? files)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var uploads = (files ?? new List<IFormFile>())
            .Where(file => file is not null && file.Length > 0)
            .Take(2)
            .ToList();
        if (uploads.Count == 0) return BadRequest(new { success = false, message = "Vui lòng chọn ảnh hoặc video." });

        var media = new List<Dictionary<string, object?>>();
        foreach (var file in uploads)
        {
            var contentType = (file.ContentType ?? string.Empty).Trim().ToLowerInvariant();
            if (!contentType.StartsWith("image/") && !contentType.StartsWith("video/")) continue;
            var url = await _fileStorage.SaveFileAsync(file, current.userId!, "ai-chat");
            if (string.IsNullOrWhiteSpace(url)) continue;
            media.Add(new Dictionary<string, object?>
            {
                ["url"] = url,
                ["name"] = Path.GetFileName(file.FileName),
                ["contentType"] = contentType,
                ["size"] = file.Length,
                ["type"] = contentType.StartsWith("video/") ? "video" : "image"
            });
        }

        if (media.Count == 0) return BadRequest(new { success = false, message = "Tệp không hợp lệ. Chỉ hỗ trợ ảnh hoặc video, mỗi tệp tối đa 10MB." });
        return Ok(new { success = true, media });
    }

    [HttpPost("location-analysis")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> AnalyzeLocationImage(
        [FromBody] LocationImageAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var image = (request?.Image ?? string.Empty).Trim();
        if (image.Length == 0)
            return BadRequest(new { success = false, message = "Vui lòng chọn ảnh cần phân tích." });
        if (image.Length > 3_500_000)
            return BadRequest(new { success = false, message = "Ảnh chưa được tối ưu hoặc có dung lượng quá lớn." });
        try
        {
            _ = Convert.FromBase64String(image);
        }
        catch (FormatException)
        {
            return BadRequest(new { success = false, message = "Dữ liệu ảnh không đúng định dạng base64." });
        }

        var reservation = await ReserveChatUsageAsync(current.userId!, current.authUser, cancellationToken);
        if (reservation.Error is not null) return reservation.Error;

        var completed = false;
        try
        {
            var language = string.Equals(request?.Language, "en", StringComparison.OrdinalIgnoreCase)
                ? "en"
                : "vi";
            var analysis = await _ollama.AnalyzeTravelImageAsync(
                image,
                language,
                cancellationToken);
            completed = true;
            return Ok(new { success = true, analysis });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { success = false, message = "AI phân tích ảnh phản hồi quá lâu." });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Nhà cung cấp AI không thể phân tích địa danh cho người dùng {UserId}", current.userId);
            return StatusCode(StatusCodes.Status502BadGateway, new { success = false, message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Không thể kết nối dịch vụ AI để phân tích địa danh cho người dùng {UserId}", current.userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, message = "Không thể kết nối máy chủ AI để phân tích ảnh." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi phân tích địa danh không xác định cho người dùng {UserId}", current.userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, message = "Dịch vụ phân tích ảnh tạm thời không khả dụng." });
        }
        finally
        {
            if (!completed)
            {
                await ReleaseUsageSafelyAsync(
                    reservation.Usage.UsageEventId,
                    current.userId!,
                    AiUsageLimitService.ChatFeature);
            }
        }
    }

    [HttpPost("chat")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public async Task<IActionResult> Chat([FromBody] AiChatRequest request, CancellationToken cancellationToken)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var validationError = ValidateRequest(request);
        if (validationError != null) return validationError;

        var reservation = await ReserveChatUsageAsync(current.userId!, current.authUser, cancellationToken);
        if (reservation.Error is not null) return reservation.Error;

        var completed = false;
        try
        {
            var systemContext = await _knowledge.BuildForChatAsync(
                current.userId!,
                request.Message,
                (request.Images?.Count ?? 0) > 0,
                cancellationToken);
            var answer = await _ollama.ChatForUserAsync(
                current.userId!,
                request.Message,
                request.History,
                request.ReferenceContext,
                systemContext,
                request.Images,
                cancellationToken);
            completed = true;
            return Ok(new { success = true, reply = answer });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { success = false, message = "AI phản hồi quá lâu." });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Nhà cung cấp AI từ chối yêu cầu của người dùng {UserId}", current.userId);
            return StatusCode(StatusCodes.Status502BadGateway, new { success = false, message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Không thể kết nối dịch vụ AI cho người dùng {UserId}", current.userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                message = "Không thể kết nối dịch vụ AI. Kiểm tra cấu hình OLLAMA_* hoặc OPENROUTER_* trên Render và kết nối mạng của server."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi nhà cung cấp AI không xác định cho người dùng {UserId}", current.userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, message = "Dịch vụ AI tạm thời không khả dụng." });
        }
        finally
        {
            if (!completed)
            {
                await ReleaseUsageSafelyAsync(
                    reservation.Usage.UsageEventId,
                    current.userId!,
                    AiUsageLimitService.ChatFeature);
            }
        }
    }

    [HttpPost("translate")]
    [HttpPost("translate-to-vietnamese")]
    public async Task<IActionResult> TranslateText(
        [FromBody] AiTextTranslationRequest request,
        CancellationToken cancellationToken)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var text = (request?.Text ?? string.Empty).Trim();
        if (text.Length == 0)
            return BadRequest(new { success = false, message = "Không có nội dung để dịch." });
        if (text.Length > 8000)
            return BadRequest(new { success = false, message = "Nội dung dịch tối đa 8.000 ký tự." });

        var targetLanguage = string.Equals(request?.TargetLanguage, "en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "vi";

        try
        {
            var translation = await _ollama.TranslateMessageAsync(text, targetLanguage, cancellationToken);
            return Ok(new { success = true, translation, targetLanguage });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { success = false, message = "AI dịch phản hồi quá lâu." });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Nhà cung cấp AI không thể dịch nội dung sang {TargetLanguage} cho người dùng {UserId}", targetLanguage, current.userId);
            return StatusCode(StatusCodes.Status502BadGateway, new { success = false, message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Không thể kết nối dịch vụ AI để dịch sang {TargetLanguage} cho người dùng {UserId}", targetLanguage, current.userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, message = "Không thể kết nối máy chủ AI để dịch." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi dịch AI sang {TargetLanguage} không xác định cho người dùng {UserId}", targetLanguage, current.userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { success = false, message = "Dịch vụ dịch AI tạm thời không khả dụng." });
        }
    }


    [HttpPost("chat/jobs")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public async Task<IActionResult> StartChatJob([FromBody] AiChatRequest request)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        var validationError = ValidateRequest(request);
        if (validationError != null) return validationError;

        if (_jobs.TryGetActive(current.userId!, out var activeJob))
        {
            return Conflict(new
            {
                success = false,
                message = "AI đang trả lời câu trước.",
                jobId = activeJob.JobId,
                status = activeJob.Status
            });
        }

        var reservation = await ReserveChatUsageAsync(current.userId!, current.authUser, HttpContext.RequestAborted);
        if (reservation.Error is not null) return reservation.Error;

        if (!_jobs.TryStart(current.userId!, request, reservation.Usage.UsageEventId, out var job))
        {
            await ReleaseUsageSafelyAsync(
                reservation.Usage.UsageEventId,
                current.userId!,
                AiUsageLimitService.ChatFeature);
            return Conflict(new
            {
                success = false,
                message = "AI đang trả lời câu trước.",
                jobId = job.JobId,
                status = job.Status
            });
        }

        return Accepted(new
        {
            success = true,
            jobId = job.JobId,
            status = job.Status,
            createdAt = job.CreatedAt
        });
    }

    [HttpGet("chat/jobs/active")]
    public async Task<IActionResult> GetActiveChatJob()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        if (!_jobs.TryGetActive(current.userId!, out var job))
            return Ok(new { success = true, active = false });

        return Ok(ToJobResponse(job, active: true));
    }

    [HttpGet("chat/jobs/{jobId}")]
    public async Task<IActionResult> GetChatJob(string jobId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        if (string.IsNullOrWhiteSpace(jobId) || !_jobs.TryGet(current.userId!, jobId, out var job))
            return NotFound(new { success = false, message = "Không tìm thấy tiến trình AI." });

        return Ok(ToJobResponse(job, active: !job.IsTerminal));
    }

    [HttpGet("chat/jobs/{jobId}/stream")]
    public async Task<IActionResult> StreamChatJob(string jobId, CancellationToken cancellationToken)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        if (string.IsNullOrWhiteSpace(jobId) || !_jobs.TryGet(current.userId!, jobId, out _))
            return NotFound(new { success = false, message = "Không tìm thấy tiến trình AI." });

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache, no-store";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.StartAsync(cancellationToken);

        var lastVersion = string.Empty;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_jobs.TryGet(current.userId!, jobId, out var job))
                {
                    await WriteNdjsonAsync(new { success = false, status = "failed", message = "Không tìm thấy tiến trình AI." }, cancellationToken);
                    break;
                }

                var version = $"{job.Status}|{job.UpdatedAt.UtcDateTime.Ticks}|{job.Reply.Length}|{job.Message}";
                if (!string.Equals(version, lastVersion, StringComparison.Ordinal))
                {
                    await WriteNdjsonAsync(ToJobResponse(job, active: !job.IsTerminal), cancellationToken);
                    lastVersion = version;
                }

                if (job.IsTerminal) break;
                await Task.Delay(120, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return new EmptyResult();
    }

    [HttpDelete("chat/jobs/active")]
    public async Task<IActionResult> CancelActiveChatJob()
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        if (!_jobs.TryCancelActive(current.userId!, out var job))
            return Ok(new { success = true, active = false, message = "Không có tiến trình AI đang chạy." });

        return Ok(ToJobResponse(job, active: false));
    }

    [HttpDelete("chat/jobs/{jobId}")]
    public async Task<IActionResult> CancelChatJob(string jobId)
    {
        var current = await CurrentUserAsync();
        if (!current.ok) return current.error!;

        if (string.IsNullOrWhiteSpace(jobId) || !_jobs.TryCancel(current.userId!, jobId, out var job))
            return NotFound(new { success = false, message = "Không tìm thấy tiến trình AI." });

        return Ok(ToJobResponse(job, active: false));
    }

    private async Task WriteNdjsonAsync(object payload, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(payload) + "\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private static object ToJobResponse(AiChatJobSnapshot job, bool active)
    {
        return new
        {
            success = true,
            active,
            jobId = job.JobId,
            status = job.Status,
            reply = job.Reply,
            message = job.Message,
            createdAt = job.CreatedAt,
            updatedAt = job.UpdatedAt
        };
    }


    private async Task<ChatUsageReservation> ReserveChatUsageAsync(
        string userId,
        Dictionary<string, object?>? authUser,
        CancellationToken cancellationToken)
    {
        var policy = _rolePolicies.GetPolicy(authUser?.GetValueOrDefault("role"));
        var usage = await _usageLimits.TryConsumeAsync(
            userId,
            AiUsageLimitService.ChatFeature,
            policy.AiChatLimitPerWindow,
            policy.WindowMinutes,
            cancellationToken);
        if (usage.Allowed) return new ChatUsageReservation(usage, null);

        Response.Headers["Retry-After"] = usage.RetryAfterSeconds.ToString();
        return new ChatUsageReservation(
            usage,
            StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                success = false,
                code = "AI_CHAT_RATE_LIMIT",
                message = $"Gói {policy.Role} được nhắn với chatbot tối đa {policy.AiChatLimitPerWindow} câu trong {policy.WindowMinutes} phút.",
                limit = usage.Limit,
                remaining = usage.Remaining,
                retryAfterSeconds = usage.RetryAfterSeconds,
                resetAt = usage.ResetAt
            }));
    }

    private async Task ReleaseUsageSafelyAsync(long? usageEventId, string userId, string feature)
    {
        if (!usageEventId.HasValue) return;

        try
        {
            await _usageLimits.ReleaseAsync(usageEventId, userId, feature, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể hoàn lại lượt AI {UsageEventId} cho người dùng {UserId}", usageEventId, userId);
        }
    }

    private sealed record ChatUsageReservation(AiUsageLimitResult Usage, IActionResult? Error);

    private IActionResult? ValidateRequest(AiChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message) && (request.Images == null || request.Images.Count == 0))
            return BadRequest(new { success = false, message = "Vui lòng nhập câu hỏi hoặc đính kèm ảnh/video." });
        if ((request.Message?.Length ?? 0) > 4000)
            return BadRequest(new { success = false, message = "Câu hỏi tối đa 4.000 ký tự." });
        if ((request.ReferenceContext?.Length ?? 0) > 20000)
            return BadRequest(new { success = false, message = "Thông tin tham khảo tối đa 20.000 ký tự." });
        if ((request.Images?.Count ?? 0) > 2)
            return BadRequest(new { success = false, message = "Chỉ được gửi tối đa 2 ảnh hoặc khung hình video cho AI." });
        var images = request.Images ?? new List<string>();
        if (images.Any(image => string.IsNullOrWhiteSpace(image) || image.Length > 3_500_000))
            return BadRequest(new { success = false, message = "Ảnh hoặc khung hình video chưa được tối ưu hoặc quá lớn." });
        if (images.Sum(image => (long)image.Length) > 7_000_000)
            return BadRequest(new { success = false, message = "Tổng dung lượng ảnh gửi AI quá lớn. Hãy gửi ít ảnh hơn." });
        foreach (var image in images)
        {
            try
            {
                _ = Convert.FromBase64String(image);
            }
            catch (FormatException)
            {
                return BadRequest(new { success = false, message = "Dữ liệu ảnh gửi AI không đúng định dạng base64." });
            }
        }

        return null;
    }
}
