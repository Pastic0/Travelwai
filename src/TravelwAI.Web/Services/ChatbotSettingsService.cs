using TravelwAI.Data.Interfaces;

namespace TravelwAI.Web.Services;

public sealed class ChatbotSettingsService
{
    public const string SettingsCollection = "site_settings";
    public const string SettingsDocumentId = "chatbot";
    public const string DefaultChatbotName = "WaiGo";
    public const int MaxChatbotNameLength = 40;
    public const int MaxStyleCount = 20;
    public const int MaxStyleNameLength = 60;
    public const int MaxStylePromptLength = 4000;
    public const int MinResponseWords = 50;
    public const int MaxResponseWords = 2000;
    public const int DefaultResponseWords = 500;
    public const decimal DefaultPaidStylePrice = 10000m;
    public const decimal MaxStylePrice = 100000000m;

    private static readonly HashSet<string> FreeStyleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "default", "gentle", "formal"
    };

    private readonly IDataRepository _repo;
    private readonly RoleFeaturePolicyService _rolePolicies;
    private readonly ILogger<ChatbotSettingsService> _logger;

    public ChatbotSettingsService(
        IDataRepository repo,
        RoleFeaturePolicyService rolePolicies,
        ILogger<ChatbotSettingsService> logger)
    {
        _repo = repo;
        _rolePolicies = rolePolicies;
        _logger = logger;
    }

    public async Task<ChatbotConfiguration> GetConfigurationAsync()
    {
        try
        {
            var settings = await _repo.GetByIdAsync(SettingsCollection, SettingsDocumentId);
            return BuildConfiguration(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Không thể đọc cấu hình chatbot {Collection}/{DocumentId}. Đang dùng cấu hình mặc định để tránh lỗi 500.",
                SettingsCollection,
                SettingsDocumentId);
            return BuildConfiguration(null);
        }
    }

    private static ChatbotConfiguration BuildConfiguration(Dictionary<string, object?>? settings)
    {
        var chatbotName = ReadText(settings, "chatbot_name", "chatbotName", "display_name", "displayName");
        if (string.IsNullOrWhiteSpace(chatbotName)) chatbotName = DefaultChatbotName;
        chatbotName = Limit(chatbotName, MaxChatbotNameLength);

        var styles = ReadStyles(settings);
        var legacyPrompt = ReadText(settings, "style_prompt", "stylePrompt", "conversation_style_prompt", "conversationStylePrompt");
        var defaultStyleId = ReadText(settings, "default_style_id", "defaultStyleId", "active_style_id", "activeStyleId");

        if (styles.Count == 0)
        {
            styles = BuildDefaultStyles();
            if (!string.IsNullOrWhiteSpace(legacyPrompt))
            {
                styles.Add(new ChatbotConversationStyle("legacy-custom", "Phong cách hiện tại", Limit(legacyPrompt, MaxStylePromptLength), DefaultPaidStylePrice, false, DefaultResponseWords));
                defaultStyleId = "legacy-custom";
            }
        }

        styles = NormalizeStyles(styles, ensureFreeStyles: true);
        var matchedDefault = styles.FirstOrDefault(item => string.Equals(item.Id, defaultStyleId, StringComparison.OrdinalIgnoreCase));
        defaultStyleId = matchedDefault?.Id ?? "default";
        return new ChatbotConfiguration(chatbotName, defaultStyleId, styles);
    }

    public async Task<ChatbotConfiguration> SaveConfigurationAsync(
        string? chatbotName,
        IEnumerable<ChatbotConversationStyle>? styles,
        string? defaultStyleId,
        string updatedBy)
    {
        var cleanName = string.IsNullOrWhiteSpace(chatbotName) ? DefaultChatbotName : Limit(chatbotName.Trim(), MaxChatbotNameLength);
        var cleanStyles = NormalizeStyles(styles ?? Array.Empty<ChatbotConversationStyle>(), ensureFreeStyles: true);
        var cleanDefaultId = (defaultStyleId ?? string.Empty).Trim();
        var defaultStyle = cleanStyles.FirstOrDefault(item => string.Equals(item.Id, cleanDefaultId, StringComparison.OrdinalIgnoreCase))
            ?? cleanStyles.First(item => item.Id == "default");
        cleanDefaultId = defaultStyle.Id;

        var now = DateTime.UtcNow;
        var serializedStyles = cleanStyles.Select(item => new Dictionary<string, object?>
        {
            ["id"] = item.Id,
            ["name"] = item.Name,
            ["prompt"] = item.Prompt,
            ["price"] = item.Price,
            ["is_free"] = item.IsFree,
            ["isFree"] = item.IsFree,
            ["max_response_words"] = item.MaxResponseWords,
            ["maxResponseWords"] = item.MaxResponseWords
        }).ToList();

        await _repo.SetAsync(SettingsCollection, SettingsDocumentId, new Dictionary<string, object?>
        {
            ["chatbot_name"] = cleanName,
            ["chatbotName"] = cleanName,
            ["styles"] = serializedStyles,
            ["conversation_styles"] = serializedStyles,
            ["default_style_id"] = cleanDefaultId,
            ["defaultStyleId"] = cleanDefaultId,
            ["style_prompt"] = defaultStyle.Prompt,
            ["stylePrompt"] = defaultStyle.Prompt,
            ["updated_by"] = updatedBy,
            ["updatedBy"] = updatedBy,
            ["updated_at"] = now,
            ["updatedAt"] = now
        }, merge: true);

        return new ChatbotConfiguration(cleanName, cleanDefaultId, cleanStyles);
    }

    public async Task<ChatbotUserConfiguration> GetForUserAsync(string userId, object? roleOverride = null)
    {
        var configuration = await GetConfigurationAsync();
        Dictionary<string, object?>? user = null;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            try
            {
                user = await _repo.GetByIdAsync("users", userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Không thể đọc cấu hình chatbot của người dùng {UserId}. Đang dùng quyền theo phiên đăng nhập hoặc gói Free.",
                    userId);
            }
        }
        var role = RoleFeaturePolicyService.NormalizeRole(roleOverride ?? user?.GetValueOrDefault("role"));
        var policy = _rolePolicies.GetPolicy(role);
        var ownedStyleIds = ReadStringSet(user, "chatbot_style_purchases", "chatbotStylePurchases", "purchased_chatbot_styles", "purchasedChatbotStyles");

        var accessible = configuration.Styles
            .Where(style => HasStyleAccess(style, policy, ownedStyleIds))
            .Select(style => style.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var canSelectStyle = !string.IsNullOrWhiteSpace(userId);
        var selectedStyleId = "default";
        if (canSelectStyle)
        {
            var stored = ReadText(user, "chatbot_style_id", "chatbotStyleId", "waigo_style_id", "waigoStyleId");
            if (accessible.Contains(stored)) selectedStyleId = configuration.Styles.First(item => string.Equals(item.Id, stored, StringComparison.OrdinalIgnoreCase)).Id;
            else if (accessible.Contains(configuration.DefaultStyleId)) selectedStyleId = configuration.DefaultStyleId;
            else selectedStyleId = configuration.Styles.FirstOrDefault(item => accessible.Contains(item.Id))?.Id ?? "default";
        }

        return new ChatbotUserConfiguration(configuration, selectedStyleId, role, canSelectStyle, policy.HasAllChatbotStyles, ownedStyleIds);
    }

    public async Task<ChatbotConversationProfile> ResolveConversationProfileAsync(string userId)
    {
        var userConfiguration = await GetForUserAsync(userId);
        var selected = userConfiguration.Configuration.Styles.FirstOrDefault(item => item.Id == userConfiguration.SelectedStyleId)
            ?? userConfiguration.Configuration.Styles.First(item => item.Id == "default");
        return new ChatbotConversationProfile(userConfiguration.Configuration.ChatbotName, selected);
    }

    public async Task<ChatbotStyleSelectionResult> SetUserStyleAsync(string userId, string styleId, object? roleOverride = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) return new(false, "Bạn cần đăng nhập để đổi phong cách.", null);
        var userConfiguration = await GetForUserAsync(userId, roleOverride);
        var selected = userConfiguration.Configuration.Styles.FirstOrDefault(item => string.Equals(item.Id, styleId, StringComparison.OrdinalIgnoreCase));
        if (selected is null) return new(false, "Phong cách không tồn tại hoặc đã bị xóa.", null);
        if (!HasStyleAccess(selected, _rolePolicies.GetPolicy(userConfiguration.Role), userConfiguration.OwnedStyleIds))
        {
            return new(false, "Phong cách này đang bị khóa. Hãy mua phong cách hoặc nâng cấp tài khoản.", selected);
        }

        var now = DateTime.UtcNow;
        await _repo.SetAsync("users", userId, new Dictionary<string, object?>
        {
            ["chatbot_style_id"] = selected.Id,
            ["chatbotStyleId"] = selected.Id,
            ["waigo_style_id"] = selected.Id,
            ["waigoStyleId"] = selected.Id,
            ["updated_at"] = now,
            ["updatedAt"] = now
        }, merge: true);
        return new(true, string.Empty, selected);
    }

    public async Task<bool> GrantPurchasedStyleAsync(string userId, string styleId)
    {
        var configuration = await GetConfigurationAsync();
        var style = configuration.Styles.FirstOrDefault(item => string.Equals(item.Id, styleId, StringComparison.OrdinalIgnoreCase));
        if (style is null) return false;
        var user = await _repo.GetByIdAsync("users", userId);
        var owned = ReadStringSet(user, "chatbot_style_purchases", "chatbotStylePurchases", "purchased_chatbot_styles", "purchasedChatbotStyles");
        owned.Add(style.Id);
        var values = owned.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        await _repo.SetAsync("users", userId, new Dictionary<string, object?>
        {
            ["chatbot_style_purchases"] = values,
            ["chatbotStylePurchases"] = values,
            ["updated_at"] = DateTime.UtcNow
        }, merge: true);
        return true;
    }

    public async Task<bool> UserOwnsStyleAsync(string userId, string styleId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(styleId)) return false;
        var user = await _repo.GetByIdAsync("users", userId);
        var owned = ReadStringSet(user, "chatbot_style_purchases", "chatbotStylePurchases", "purchased_chatbot_styles", "purchasedChatbotStyles");
        return owned.Contains(styleId.Trim());
    }

    public static bool IsFreeStyle(string? styleId) => FreeStyleIds.Contains((styleId ?? string.Empty).Trim());

    public static string CreateStyleId(string? proposedId, string? name, ISet<string> usedIds)
    {
        var raw = (proposedId ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(raw)) raw = Slug(name);
        raw = new string(raw.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray()).Trim('-', '_');
        if (string.IsNullOrWhiteSpace(raw)) raw = "style";
        if (raw.Length > 48) raw = raw[..48];

        var candidate = raw;
        var suffix = 2;
        while (usedIds.Contains(candidate))
        {
            var suffixText = $"-{suffix++}";
            candidate = raw.Length + suffixText.Length <= 48
                ? raw + suffixText
                : raw[..Math.Max(1, 48 - suffixText.Length)] + suffixText;
        }
        usedIds.Add(candidate);
        return candidate;
    }

    private static bool HasStyleAccess(ChatbotConversationStyle style, RoleFeaturePolicy policy, IReadOnlySet<string> owned)
        => policy.HasAllChatbotStyles || style.IsFree || owned.Contains(style.Id);

    private static List<ChatbotConversationStyle> ReadStyles(Dictionary<string, object?>? settings)
    {
        if (settings is null) return new List<ChatbotConversationStyle>();
        object? raw = null;
        foreach (var key in new[] { "styles", "conversation_styles", "conversationStyles" })
        {
            if (settings.TryGetValue(key, out raw) && raw is not null) break;
        }

        if (raw is not IEnumerable<object?> list) return new List<ChatbotConversationStyle>();
        var result = new List<ChatbotConversationStyle>();
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> row) continue;
            var id = ReadText(row, "id", "style_id", "styleId");
            var name = ReadText(row, "name", "label", "title");
            var prompt = ReadText(row, "prompt", "style_prompt", "stylePrompt");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var isFree = IsFreeStyle(id);
            var price = isFree ? 0m : ReadDecimal(row, "price", "price_amount", "priceAmount");
            if (!isFree && price <= 0) price = DefaultPaidStylePrice;
            var maxResponseWords = Math.Clamp(
                ReadInt(row, "max_response_words", "maxResponseWords", "response_word_limit", "responseWordLimit") ?? DefaultResponseWords,
                MinResponseWords,
                MaxResponseWords);
            result.Add(new ChatbotConversationStyle(id, name, prompt, price, isFree, maxResponseWords));
        }
        return result;
    }

    private static List<ChatbotConversationStyle> NormalizeStyles(IEnumerable<ChatbotConversationStyle> styles, bool ensureFreeStyles)
    {
        var source = styles.Take(MaxStyleCount).ToList();
        var defaults = BuildDefaultStyles();
        var result = new List<ChatbotConversationStyle>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ensureFreeStyles)
        {
            foreach (var fallback in defaults)
            {
                var incoming = source.FirstOrDefault(item => string.Equals(item.Id, fallback.Id, StringComparison.OrdinalIgnoreCase));
                var name = Limit((incoming?.Name ?? fallback.Name).Trim(), MaxStyleNameLength);
                var prompt = Limit((incoming?.Prompt ?? fallback.Prompt).Trim(), MaxStylePromptLength);
                var maxResponseWords = Math.Clamp(incoming?.MaxResponseWords ?? fallback.MaxResponseWords, MinResponseWords, MaxResponseWords);
                usedIds.Add(fallback.Id);
                result.Add(new ChatbotConversationStyle(fallback.Id, string.IsNullOrWhiteSpace(name) ? fallback.Name : name, prompt, 0m, true, maxResponseWords));
            }
        }

        foreach (var item in source)
        {
            if (FreeStyleIds.Contains(item.Id)) continue;
            if (result.Count >= MaxStyleCount) break;
            var name = Limit((item.Name ?? string.Empty).Trim(), MaxStyleNameLength);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var id = CreateStyleId(item.Id, name, usedIds);
            var prompt = Limit((item.Prompt ?? string.Empty).Trim(), MaxStylePromptLength);
            var price = Math.Clamp(item.Price <= 0 ? DefaultPaidStylePrice : item.Price, 1000m, MaxStylePrice);
            var maxResponseWords = Math.Clamp(item.MaxResponseWords, MinResponseWords, MaxResponseWords);
            result.Add(new ChatbotConversationStyle(id, name, prompt, price, false, maxResponseWords));
        }
        return result;
    }

    private static List<ChatbotConversationStyle> BuildDefaultStyles() => new()
    {
        new("default", "Mặc định", string.Empty, 0m, true, DefaultResponseWords),
        new("gentle", "Dịu dàng", "Hãy nói chuyện dịu dàng, ấm áp, tinh tế, lịch sự và dễ hiểu. Tránh trả lời cộc lốc; dùng emoji vừa phải khi phù hợp.", 0m, true, DefaultResponseWords),
        new("formal", "Trang nghiêm", "Hãy trả lời trang nghiêm, chuyên nghiệp, chuẩn mực, ngắn gọn và có cấu trúc rõ ràng. Không dùng tiếng lóng hoặc cách xưng hô suồng sã.", 0m, true, 400)
    };

    private static HashSet<string> ReadStringSet(Dictionary<string, object?>? data, params string[] keys)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (data is null) return result;
        foreach (var key in keys)
        {
            if (!data.TryGetValue(key, out var raw) || raw is null) continue;
            if (raw is IEnumerable<object?> objects)
            {
                foreach (var item in objects)
                {
                    var value = item?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
                }
            }
            else if (raw is IEnumerable<string> strings)
            {
                foreach (var item in strings.Where(item => !string.IsNullOrWhiteSpace(item))) result.Add(item.Trim());
            }
            else
            {
                foreach (var item in raw.ToString()?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>()) result.Add(item.Trim());
            }
            if (result.Count > 0) break;
        }
        return result;
    }

    private static string ReadText(Dictionary<string, object?>? data, params string[] keys)
    {
        if (data is null) return string.Empty;
        foreach (var key in keys)
        {
            if (!data.TryGetValue(key, out var value) || value is null) continue;
            var text = Convert.ToString(value)?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return string.Empty;
    }

    private static decimal ReadDecimal(Dictionary<string, object?> data, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (data.TryGetValue(key, out var value) && decimal.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return 0m;
    }

    private static int? ReadInt(Dictionary<string, object?> data, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (data.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static string Limit(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static string Slug(string? value)
    {
        var text = (value ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
        var chars = text.Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(ch => ch is 'Đ' or 'đ' ? 'd' : char.ToLowerInvariant(ch));
        return string.Join('-', new string(chars.ToArray()).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }
}

public sealed record ChatbotConversationStyle(
    string Id,
    string Name,
    string Prompt,
    decimal Price,
    bool IsFree,
    int MaxResponseWords = ChatbotSettingsService.DefaultResponseWords);
public sealed record ChatbotConfiguration(string ChatbotName, string DefaultStyleId, IReadOnlyList<ChatbotConversationStyle> Styles);
public sealed record ChatbotUserConfiguration(
    ChatbotConfiguration Configuration,
    string SelectedStyleId,
    string Role,
    bool CanChangeStyle,
    bool HasAllStyles,
    IReadOnlySet<string> OwnedStyleIds);
public sealed record ChatbotConversationProfile(string ChatbotName, ChatbotConversationStyle Style);
public sealed record ChatbotStyleSelectionResult(bool Success, string Message, ChatbotConversationStyle? Style);
