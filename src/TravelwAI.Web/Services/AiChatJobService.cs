using System.Collections.Concurrent;
using TravelwAI.Web.Models;

namespace TravelwAI.Web.Services;

public sealed class AiChatJobService
{
    private static readonly TimeSpan CompletedJobLifetime = TimeSpan.FromMinutes(30);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiChatJobService> _logger;
    private readonly ConcurrentDictionary<string, AiChatJobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeJobByUser = new(StringComparer.Ordinal);

    public AiChatJobService(IServiceScopeFactory scopeFactory, ILogger<AiChatJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool TryStart(string userId, AiChatRequest request, long? usageEventId, out AiChatJobSnapshot job)
    {
        CleanupExpiredJobs();

        if (TryGetActive(userId, out var active))
        {
            job = active;
            return false;
        }

        var record = new AiChatJobRecord(Guid.NewGuid().ToString("N"), userId, usageEventId);
        var requestCopy = CloneRequest(request);
        _jobs[record.JobId] = record;
        _activeJobByUser[userId] = record.JobId;
        job = record.Snapshot();

        _ = Task.Run(() => ExecuteAsync(record, requestCopy));
        return true;
    }

    public bool TryGet(string userId, string jobId, out AiChatJobSnapshot job)
    {
        CleanupExpiredJobs();
        if (_jobs.TryGetValue(jobId, out var record) && record.UserId == userId)
        {
            job = record.Snapshot();
            return true;
        }

        job = default!;
        return false;
    }

    public bool TryGetActive(string userId, out AiChatJobSnapshot job)
    {
        CleanupExpiredJobs();
        if (_activeJobByUser.TryGetValue(userId, out var jobId)
            && _jobs.TryGetValue(jobId, out var record))
        {
            var snapshot = record.Snapshot();
            if (!snapshot.IsTerminal)
            {
                job = snapshot;
                return true;
            }
        }

        job = default!;
        return false;
    }

    public bool TryCancel(string userId, string jobId, out AiChatJobSnapshot job)
    {
        CleanupExpiredJobs();
        if (!_jobs.TryGetValue(jobId, out var record) || record.UserId != userId)
        {
            job = default!;
            return false;
        }

        record.RequestCancellation();
        RemoveActiveMapping(record);
        job = record.Snapshot();
        return true;
    }

    public bool TryCancelActive(string userId, out AiChatJobSnapshot job)
    {
        CleanupExpiredJobs();
        if (!_activeJobByUser.TryGetValue(userId, out var jobId)
            || !_jobs.TryGetValue(jobId, out var record)
            || record.UserId != userId)
        {
            job = default!;
            return false;
        }

        record.RequestCancellation();
        RemoveActiveMapping(record);
        job = record.Snapshot();
        return true;
    }

    private async Task ExecuteAsync(AiChatJobRecord record, AiChatRequest request)
    {
        if (!record.TryMarkRunning())
        {
            await ReleaseUsageIfNeededAsync(record, completed: false);
            RemoveActiveMapping(record);
            record.DisposeCancellation();
            return;
        }

        var completed = false;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var knowledge = scope.ServiceProvider.GetRequiredService<AiKnowledgeContextService>();
            var ollama = scope.ServiceProvider.GetRequiredService<OllamaAiService>();
            var cancellationToken = record.CancellationToken;

            var systemContext = await knowledge.BuildForChatAsync(
                record.UserId,
                request.Message,
                (request.Images?.Count ?? 0) > 0,
                cancellationToken);
            var answer = await ollama.ChatForUserStreamingAsync(
                record.UserId,
                request.Message,
                request.History,
                request.ReferenceContext,
                systemContext,
                request.Images,
                (delta, _) =>
                {
                    record.AppendReply(delta);
                    return Task.CompletedTask;
                },
                cancellationToken);

            completed = record.TryMarkCompleted(answer);
        }
        catch (OperationCanceledException) when (record.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Đã dừng AI job {JobId} theo yêu cầu của người dùng {UserId}",
                record.JobId,
                record.UserId);
            record.MarkCancelled();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Ollama phản hồi quá lâu cho AI job {JobId} của người dùng {UserId}", record.JobId, record.UserId);
            record.MarkFailed("Ollama phản hồi quá lâu.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Ollama từ chối AI job {JobId} của người dùng {UserId}", record.JobId, record.UserId);
            record.MarkFailed(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Không thể kết nối Ollama cho AI job {JobId} của người dùng {UserId}", record.JobId, record.UserId);
            record.MarkFailed("Không thể kết nối máy chủ Ollama.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi AI job {JobId} của người dùng {UserId}", record.JobId, record.UserId);
            record.MarkFailed("Dịch vụ AI tạm thời không khả dụng.");
        }
        finally
        {
            await ReleaseUsageIfNeededAsync(record, completed);
            RemoveActiveMapping(record);
            record.DisposeCancellation();
        }
    }

    private async Task ReleaseUsageIfNeededAsync(AiChatJobRecord record, bool completed)
    {
        if (completed || !record.UsageEventId.HasValue || !record.TryMarkUsageReleased()) return;

        try
        {
            using var releaseScope = _scopeFactory.CreateScope();
            var usageLimits = releaseScope.ServiceProvider.GetRequiredService<AiUsageLimitService>();
            await usageLimits.ReleaseAsync(
                record.UsageEventId,
                record.UserId,
                AiUsageLimitService.ChatFeature,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Không thể hoàn lại lượt AI {UsageEventId} cho job {JobId} của người dùng {UserId}",
                record.UsageEventId,
                record.JobId,
                record.UserId);
        }
    }

    private void RemoveActiveMapping(AiChatJobRecord record)
    {
        if (_activeJobByUser.TryGetValue(record.UserId, out var currentJobId)
            && string.Equals(currentJobId, record.JobId, StringComparison.Ordinal))
        {
            _activeJobByUser.TryRemove(record.UserId, out _);
        }
    }

    private void CleanupExpiredJobs()
    {
        var threshold = DateTimeOffset.UtcNow - CompletedJobLifetime;
        foreach (var pair in _jobs)
        {
            var snapshot = pair.Value.Snapshot();
            if (snapshot.IsTerminal && snapshot.UpdatedAt < threshold)
            {
                if (_jobs.TryRemove(pair.Key, out var removed)) removed.DisposeCancellation();
            }
        }
    }

    private static AiChatRequest CloneRequest(AiChatRequest request)
    {
        return new AiChatRequest
        {
            Message = request.Message ?? string.Empty,
            ReferenceContext = request.ReferenceContext ?? string.Empty,
            Images = request.Images?.ToList() ?? new List<string>(),
            Language = string.IsNullOrWhiteSpace(request.Language) ? "auto" : request.Language.Trim().ToLowerInvariant(),
            History = request.History?.Select(item => new AiChatHistoryItem
            {
                Role = item.Role,
                Content = item.Content
            }).ToList() ?? new List<AiChatHistoryItem>()
        };
    }

    private sealed class AiChatJobRecord
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private string _status = "queued";
        private string _reply = string.Empty;
        private string _message = string.Empty;
        private DateTimeOffset _updatedAt;
        private bool _usageReleased;
        private bool _cancellationDisposed;

        public AiChatJobRecord(string jobId, string userId, long? usageEventId)
        {
            JobId = jobId;
            UserId = userId;
            UsageEventId = usageEventId;
            CreatedAt = DateTimeOffset.UtcNow;
            _updatedAt = CreatedAt;
        }

        public string JobId { get; }
        public string UserId { get; }
        public long? UsageEventId { get; }
        public DateTimeOffset CreatedAt { get; }
        public CancellationToken CancellationToken => _cancellation.Token;
        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public bool TryMarkRunning() => TryUpdateActive("running", string.Empty, string.Empty);
        public bool TryMarkCompleted(string reply) => TryUpdateActive("completed", reply, string.Empty);

        public void AppendReply(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            lock (_sync)
            {
                if (IsTerminalStatus(_status)) return;
                _reply += delta;
                _updatedAt = DateTimeOffset.UtcNow;
            }
        }
        public void MarkFailed(string message) => TryUpdateActive("failed", string.Empty, message);
        public void MarkCancelled() => UpdateCancelled();

        public bool RequestCancellation()
        {
            var shouldCancel = UpdateCancelled();
            if (!shouldCancel) return false;

            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            return true;
        }

        public bool TryMarkUsageReleased()
        {
            lock (_sync)
            {
                if (_usageReleased) return false;
                _usageReleased = true;
                return true;
            }
        }

        public void DisposeCancellation()
        {
            lock (_sync)
            {
                if (_cancellationDisposed) return;
                _cancellationDisposed = true;
            }
            _cancellation.Dispose();
        }

        public AiChatJobSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new AiChatJobSnapshot(JobId, _status, _reply, _message, CreatedAt, _updatedAt);
            }
        }

        private bool TryUpdateActive(string status, string reply, string message)
        {
            lock (_sync)
            {
                if (IsTerminalStatus(_status)) return false;
                _status = status;
                _reply = reply;
                _message = message;
                _updatedAt = DateTimeOffset.UtcNow;
                return true;
            }
        }

        private bool UpdateCancelled()
        {
            lock (_sync)
            {
                if (IsTerminalStatus(_status)) return false;
                _status = "cancelled";
                _reply = string.Empty;
                _message = "Đã dừng AI theo yêu cầu.";
                _updatedAt = DateTimeOffset.UtcNow;
                return true;
            }
        }

        private static bool IsTerminalStatus(string status) => status is "completed" or "failed" or "cancelled";
    }
}

public sealed record AiChatJobSnapshot(
    string JobId,
    string Status,
    string Reply,
    string Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsTerminal => Status is "completed" or "failed" or "cancelled";
}
