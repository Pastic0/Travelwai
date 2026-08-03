using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TravelwAI.Web.Services;

public static class LanguageNameResolver
{
    private static readonly Lazy<IReadOnlyDictionary<string, ResolvedLanguage>> Languages =
        new(BuildLanguages, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool TryResolve(string? input, out ResolvedLanguage language)
    {
        var key = NormalizeName(input);
        if (key.Length > 0 && Languages.Value.TryGetValue(key, out var resolved))
        {
            language = resolved;
            return true;
        }

        language = ResolvedLanguage.Vietnamese;
        return false;
    }

    public static string NormalizeName(string? value)
    {
        var source = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static IReadOnlyDictionary<string, ResolvedLanguage> BuildLanguages()
    {
        var result = new Dictionary<string, ResolvedLanguage>(StringComparer.Ordinal);
        var byCode = new Dictionary<string, ResolvedLanguage>(StringComparer.OrdinalIgnoreCase);

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                     .Where(culture => !string.IsNullOrWhiteSpace(culture.Name))
                     .OrderBy(culture => culture.Name.Length)
                     .ThenBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase))
        {
            var code = culture.Name.Trim().ToLowerInvariant();
            var englishName = CleanEnglishName(culture.EnglishName);
            if (englishName.Length < 2) continue;

            var language = new ResolvedLanguage(
                code,
                englishName,
                NormalizeName(englishName),
                BuildButtonLabel(englishName));

            byCode.TryAdd(code, language);
            AddAlias(result, englishName, language);
            AddAlias(result, code, language);

            var baseName = Regex.Replace(englishName, @"\s*\([^)]*\)\s*$", string.Empty).Trim();
            if (baseName.Length > 1 && !result.ContainsKey(NormalizeName(baseName)))
            {
                AddAlias(result, baseName, language);
            }
        }

        AddCommonAliases(result, byCode);
        return result;
    }

    private static void AddCommonAliases(
        IDictionary<string, ResolvedLanguage> result,
        IReadOnlyDictionary<string, ResolvedLanguage> byCode)
    {
        AddAliasesForCode(result, byCode, "vi", "Vietnam", "Vietnamese", "Viet Nam");
        AddAliasesForCode(result, byCode, "en", "English");
        AddAliasesForCode(result, byCode, "ja", "Japanese", "Japan");
        AddAliasesForCode(result, byCode, "ko", "Korean", "Korea");
        AddAliasesForCode(result, byCode, "fr", "French", "France");
        AddAliasesForCode(result, byCode, "de", "German", "Germany");
        AddAliasesForCode(result, byCode, "es", "Spanish", "Spain");
        AddAliasesForCode(result, byCode, "it", "Italian", "Italy");
        AddAliasesForCode(result, byCode, "pt", "Portuguese", "Portugal");
        AddAliasesForCode(result, byCode, "ru", "Russian", "Russia");
        AddAliasesForCode(result, byCode, "th", "Thai", "Thailand");
        AddAliasesForCode(result, byCode, "id", "Indonesian", "Indonesia");
        AddAliasesForCode(result, byCode, "ms", "Malay", "Malaysia");
        AddAliasesForCode(result, byCode, "ar", "Arabic");
        AddAliasesForCode(result, byCode, "hi", "Hindi", "India");
        AddAliasesForCode(result, byCode, "zh-hans", "Chinese", "Simplified Chinese", "Chinese Simplified");
        AddAliasesForCode(result, byCode, "zh-hant", "Traditional Chinese", "Chinese Traditional");
    }

    private static void AddAliasesForCode(
        IDictionary<string, ResolvedLanguage> result,
        IReadOnlyDictionary<string, ResolvedLanguage> byCode,
        string code,
        params string[] aliases)
    {
        if (!byCode.TryGetValue(code, out var language)) return;
        foreach (var alias in aliases) AddAlias(result, alias, language);
    }

    private static void AddAlias(
        IDictionary<string, ResolvedLanguage> result,
        string alias,
        ResolvedLanguage language)
    {
        var key = NormalizeName(alias);
        if (key.Length > 0) result[key] = language;
    }

    private static string CleanEnglishName(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string BuildButtonLabel(string englishName)
    {
        var letters = new string((englishName ?? string.Empty)
            .Where(char.IsLetter)
            .Take(2)
            .ToArray());
        if (letters.Length == 0) return "--";
        if (letters.Length == 1) letters += letters;
        return char.ToUpperInvariant(letters[0]) + letters[1..].ToLowerInvariant();
    }
}

public sealed record ResolvedLanguage(
    string Code,
    string EnglishName,
    string NormalizedName,
    string ButtonLabel)
{
    public static readonly ResolvedLanguage Vietnamese =
        new("vi", "Vietnamese", "vietnamese", "Vi");

    public bool IsVietnamese => string.Equals(Code, "vi", StringComparison.OrdinalIgnoreCase);
}
