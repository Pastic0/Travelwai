using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TravelwAI.Web.Services;

internal static class ExternalKnowledgeText
{
    private static readonly HashSet<string> IgnoredTerms = new(StringComparer.Ordinal)
    {
        "ai", "toi", "minh", "ban", "cho", "hoi", "giup", "ve", "la", "gi", "nao", "o", "tai",
        "co", "khong", "mot", "nhung", "cac", "va", "cua", "duoc", "hay", "noi", "thong", "tin",
        "du", "lich", "viet", "nam", "travel", "tourism", "question", "answer", "cau", "tra", "loi",
        "the", "this", "that", "what", "where", "when", "how", "with", "from", "into", "about"
    };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }
        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
    }

    public static IReadOnlyCollection<string> Terms(string? value, int maximum = 16)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized)) return Array.Empty<string>();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 2 && !IgnoredTerms.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Clamp(maximum, 1, 512))
            .ToArray();
    }
}
