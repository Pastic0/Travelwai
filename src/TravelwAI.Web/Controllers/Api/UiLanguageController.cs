using Microsoft.AspNetCore.Mvc;
using TravelwAI.Web.Services;
using TravelwAI.Models.Common;

namespace TravelwAI.Web.Controllers.Api;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/ui-language")]
public sealed class UiLanguageController : ControllerBase
{
    private readonly OllamaAiService _ollama;
    private readonly PersistentTranslationStore _translationStore;
    private readonly TranslationGenerationCoordinator _translationCoordinator;
    private readonly PersistentTranslationActivityGate _activityGate;
    private readonly ILogger<UiLanguageController> _logger;

    public UiLanguageController(
        OllamaAiService ollama,
        PersistentTranslationStore translationStore,
        TranslationGenerationCoordinator translationCoordinator,
        PersistentTranslationActivityGate activityGate,
        ILogger<UiLanguageController> logger)
    {
        _ollama = ollama;
        _translationStore = translationStore;
        _translationCoordinator = translationCoordinator;
        _activityGate = activityGate;
        _logger = logger;
    }

    [HttpPost("worker-state")]
    public IActionResult SetWorkerState([FromBody] UiLanguageWorkerStateRequest? request)
    {
        var clientId = NormalizeClientId(request?.ClientId);
        if (clientId.Length == 0)
        {
            return BadRequest(new { success = false, message = "Thiếu mã phiên ngôn ngữ." });
        }

        var englishActive = request?.Active == true
            && string.Equals(request.Language, "en", StringComparison.OrdinalIgnoreCase);
        _activityGate.SetClientState(clientId, englishActive);
        return Ok(new { success = true, englishActive });
    }

    [HttpPost("translate")]
    public async Task<IActionResult> Translate(
        [FromBody] UiTranslationRequest? request,
        CancellationToken cancellationToken)
    {
        _activityGate.TouchEnglishClient(
            NormalizeClientId(request?.ClientId) is { Length: > 0 } clientId
                ? clientId
                : $"request:{HttpContext.Connection.Id}");

        var source = (request?.Texts ?? new List<string>())
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0 && value.Length <= 4000)
            .Distinct(StringComparer.Ordinal)
            .Take(60)
            .ToList();

        if (source.Count == 0)
        {
            return Ok(new
            {
                success = true,
                translations = new Dictionary<string, string>(StringComparer.Ordinal)
            });
        }

        var translations = new Dictionary<string, string>(StringComparer.Ordinal);


        var deterministicPlaceNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in source)
        {
            if (!VietnamesePlaceName.TryGetEnglishName(text, out var englishName)) continue;
            translations[text] = englishName;
            deterministicPlaceNames[text] = englishName;
        }

        if (deterministicPlaceNames.Count > 0)
        {
            try
            {

                await _translationStore.SaveTextTranslationsAsync(
                    deterministicPlaceNames,
                    "en",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể sửa cache tên tỉnh/thành; vẫn trả về tên chuẩn trong request hiện tại.");
            }
        }

        var unresolvedSource = source.Where(text => !translations.ContainsKey(text)).ToList();
        Dictionary<string, string> known;
        try
        {
            known = await _translationStore.GetKnownTranslationsAsync(
                unresolvedSource,
                "en",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể đọc kho bản dịch vĩnh viễn; hệ thống sẽ dịch trực tiếp.");
            known = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        foreach (var text in unresolvedSource)
        {
            if (known.TryGetValue(text, out var translated)
                && HasCompatibleLineStructure(text, translated)
                && IsMeaningfulTranslation(text, translated))
            {
                translations[text] = translated;
            }
        }

        var missing = unresolvedSource.Where(text => !translations.ContainsKey(text)).ToList();
        if (missing.Count > 0)
        {
            await using var generationLease = await _translationCoordinator.EnterAsync(missing, cancellationToken);


            try
            {
                var refreshed = await _translationStore.GetKnownTranslationsAsync(
                    missing,
                    "en",
                    cancellationToken);

                foreach (var text in missing.ToList())
                {
                    if (refreshed.TryGetValue(text, out var translated)
                        && HasCompatibleLineStructure(text, translated)
                        && IsMeaningfulTranslation(text, translated))
                    {
                        translations[text] = translated;
                        missing.Remove(text);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể đọc lại kho bản dịch sau khi chờ khóa dịch.");
            }

            if (missing.Count > 0)
            {
                try
                {
                    var generated = await _ollama.TranslateUiToEnglishAsync(missing, cancellationToken);
                    var persistent = new Dictionary<string, string>(StringComparer.Ordinal);

                    for (var index = 0; index < missing.Count; index += 1)
                    {
                        var original = missing[index];
                        var translated = index < generated.Count
                            ? Clean(generated[index], 6000)
                            : original;

                        if (string.IsNullOrWhiteSpace(translated)
                            || !HasCompatibleLineStructure(original, translated)
                            || !IsMeaningfulTranslation(original, translated))
                        {
                            // Return the original text for this request, but never
                            // persist it as a successful translation. This prevents
                            // a temporary Ollama miss from poisoning the cache.
                            translations[original] = original;
                            continue;
                        }

                        translations[original] = translated;
                        persistent[original] = translated;
                    }

                    try
                    {
                        await _translationStore.SaveTextTranslationsAsync(
                            persistent,
                            "en",
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Đã dịch nhưng chưa thể lưu bản dịch giao diện vào database.");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
                {
                    _logger.LogWarning(ex, "Không thể dịch giao diện sang tiếng Anh bằng Ollama.");
                    foreach (var text in missing) translations[text] = text;
                }
            }
        }

        return Ok(new { success = true, translations });
    }

    private static bool IsMeaningfulTranslation(string source, string translated)
    {
        var normalizedSource = PersistentTranslationStore.NormalizeSource(source);
        var normalizedTranslation = PersistentTranslationStore.NormalizeSource(translated);
        return normalizedTranslation.Length > 0
            && !string.Equals(
                normalizedSource,
                normalizedTranslation,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCompatibleLineStructure(string source, string translated)
    {
        static string Signature(string value) =>
            new string((value ?? string.Empty).Where(character => character is '\r' or '\n').ToArray());

        return string.Equals(Signature(source), Signature(translated), StringComparison.Ordinal);
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string NormalizeClientId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        return text.Length <= 120 ? text : text[..120];
    }

    public sealed class UiTranslationRequest
    {
        public List<string> Texts { get; set; } = new();
        public string ClientId { get; set; } = string.Empty;
    }

    public sealed class UiLanguageWorkerStateRequest
    {
        public string ClientId { get; set; } = string.Empty;
        public string Language { get; set; } = "vi";
        public bool Active { get; set; }
    }
}
