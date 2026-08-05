using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TravelwAI.Data.Interfaces;
using TravelwAI.Web.Options;

namespace TravelwAI.Web.Services;

public sealed class AiProviderSettingsService
{
    public const string OllamaProvider = "ollama";
    public const string OpenRouterProvider = "openrouter";

    private const string SettingsCollection = "ai_provider_settings";
    private const string SettingsDocumentId = "default";
    private const string CacheKey = "travelwai:ai-provider:default";

    private readonly IDataRepository _repo;
    private readonly IMemoryCache _cache;
    private readonly OpenRouterOptions _openRouter;
    private readonly OllamaOptions _ollama;
    private readonly ILogger<AiProviderSettingsService> _logger;
    private readonly string _defaultProvider;
    private readonly int _cacheSeconds;

    public AiProviderSettingsService(
        IDataRepository repo,
        IMemoryCache cache,
        IOptions<OpenRouterOptions> openRouter,
        IOptions<OllamaOptions> ollama,
        IConfiguration configuration,
        ILogger<AiProviderSettingsService> logger)
    {
        _repo = repo;
        _cache = cache;
        _openRouter = openRouter.Value;
        _ollama = ollama.Value;
        _logger = logger;
        var configuredDefault = NormalizeProvider(
            configuration["AI_PROVIDER"]
            ?? configuration["AiRouting:DefaultProvider"]
            ?? OllamaProvider);
        _defaultProvider = configuredDefault == OpenRouterProvider ? OpenRouterProvider : OllamaProvider;
        _cacheSeconds = int.TryParse(
            configuration["AI_PROVIDER_CACHE_SECONDS"]
            ?? configuration["AiRouting:CacheSeconds"],
            out var seconds)
            ? Math.Clamp(seconds, 1, 60)
            : 5;
    }

    public async Task<AiProviderStatus> GetStatusAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cache.TryGetValue(CacheKey, out AiProviderStatus? cached) && cached is not null)
            return cached;

        Dictionary<string, object?>? settings = null;
        try
        {
            settings = await _repo.GetByIdAsync(SettingsCollection, SettingsDocumentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không đọc được cấu hình nhà cung cấp AI; dùng provider mặc định {Provider}.", _defaultProvider);
        }

        var provider = NormalizeProvider(ReadText(settings, "provider", "ai_provider", "aiProvider", "selected_provider", "selectedProvider"));
        if (string.IsNullOrWhiteSpace(provider)) provider = _defaultProvider;

        var status = CreateStatus(provider);
        _cache.Set(CacheKey, status, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_cacheSeconds),
            Size = 1
        });
        return status;
    }

    public async Task<AiProviderStatus> SetProviderAsync(string? provider, string? adminUserId)
    {
        var normalized = NormalizeProvider(provider);
        if (normalized != OllamaProvider && normalized != OpenRouterProvider)
            throw new ArgumentException("Nhà cung cấp AI không hợp lệ.", nameof(provider));
        if (normalized == OpenRouterProvider && !IsOpenRouterConfigured())
            throw new InvalidOperationException("OpenRouter chưa có OPENROUTER_API_KEY trên Render.");

        var now = DateTime.UtcNow;
        var saved = await _repo.SetAsync(SettingsCollection, SettingsDocumentId, new Dictionary<string, object?>
        {
            ["provider"] = normalized,
            ["ai_provider"] = normalized,
            ["updated_by"] = adminUserId ?? string.Empty,
            ["updated_at"] = now,
            ["updatedAt"] = now
        }, merge: true);
        if (!saved) throw new InvalidOperationException("Không lưu được nhà cung cấp AI.");

        var status = CreateStatus(normalized);
        _cache.Set(CacheKey, status, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_cacheSeconds),
            Size = 1
        });
        return status;
    }

    public bool IsOpenRouterConfigured() => !string.IsNullOrWhiteSpace(_openRouter.ApiKey);

    private AiProviderStatus CreateStatus(string provider)
    {
        var normalized = NormalizeProvider(provider);
        if (normalized != OllamaProvider && normalized != OpenRouterProvider) normalized = _defaultProvider;
        return new AiProviderStatus(
            normalized,
            normalized == OpenRouterProvider ? _openRouter.Model : _ollama.Model,
            IsOpenRouterConfigured(),
            _openRouter.Model,
            _ollama.Model);
    }

    public static string NormalizeProvider(string? provider)
    {
        var value = (provider ?? string.Empty).Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty);
        return value switch
        {
            "openrouter" or "router" => OpenRouterProvider,
            "ollama" => OllamaProvider,
            _ => string.Empty
        };
    }

    private static string ReadText(Dictionary<string, object?>? row, params string[] keys)
    {
        if (row is null) return string.Empty;
        foreach (var key in keys)
        {
            if (row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString()))
                return value!.ToString()!.Trim();
        }
        return string.Empty;
    }
}

public sealed record AiProviderStatus(
    string Provider,
    string Model,
    bool OpenRouterConfigured,
    string OpenRouterModel,
    string OllamaModel);
