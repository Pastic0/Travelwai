using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace TravelwAI.Web.Services;


public sealed class PersistentTranslationStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PersistentTranslationStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<Dictionary<string, string>> GetKnownTranslationsAsync(
        IReadOnlyCollection<string> sourceTexts,
        string languageCode = "en",
        CancellationToken cancellationToken = default)
    {
        var originalTexts = sourceTexts
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var canonicalByOriginal = originalTexts.ToDictionary(
            value => value,
            CanonicalizeSource,
            StringComparer.Ordinal);
        var cleanTexts = canonicalByOriginal.Values
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (cleanTexts.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);


        var hashes = cleanTexts
            .SelectMany(value => new[] { HashText(value), HashLegacyText(value) })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceSet = cleanTexts.ToHashSet(StringComparer.Ordinal);
        var byNormalized = cleanTexts
            .GroupBy(NormalizeSource, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var rows = new List<KnownTranslationRow>();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select source_hash, source_text, translated_text
            from app_text_translations
            where language_code = @languageCode
              and source_hash = any(@hashes)
            order by updated_at desc;
            """;
        cmd.Parameters.AddWithValue("languageCode", languageCode);
        var hashParameter = cmd.Parameters.Add("hashes", NpgsqlDbType.Array | NpgsqlDbType.Text);
        hashParameter.Value = hashes;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var translated = reader.GetString(2).Trim();
            if (translated.Length == 0) continue;
            rows.Add(new KnownTranslationRow(
                reader.GetString(0),
                CanonicalizeSource(reader.GetString(1)),
                translated));
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);


        foreach (var row in rows)
        {
            if (sourceSet.Contains(row.SourceText) && !result.ContainsKey(row.SourceText))
            {
                result[row.SourceText] = row.TranslatedText;
            }
        }


        foreach (var row in rows)
        {
            if (!string.Equals(row.SourceHash, HashLegacyText(row.SourceText), StringComparison.Ordinal)) continue;
            var normalized = NormalizeSource(row.SourceText);
            if (!byNormalized.TryGetValue(normalized, out var candidates)) continue;
            foreach (var candidate in candidates)
            {
                if (!result.ContainsKey(candidate)) result[candidate] = row.TranslatedText;
            }
        }

        var mappedToOriginal = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var original in originalTexts)
        {
            if (result.TryGetValue(canonicalByOriginal[original], out var translated))
            {
                mappedToOriginal[original] = translated;
            }
        }

        return mappedToOriginal;
    }

    public async Task SaveTextTranslationsAsync(
        IReadOnlyDictionary<string, string> translations,
        string languageCode = "en",
        CancellationToken cancellationToken = default)
    {
        var clean = translations
            .Select(pair => new
            {
                Source = CanonicalizeSource(pair.Key),
                Translation = (pair.Value ?? string.Empty).Trim()
            })
            .Where(item => item.Source.Length > 0 && item.Translation.Length > 0)
            .GroupBy(item => item.Source, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();

        if (clean.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

        foreach (var item in clean)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                insert into app_text_translations(
                    language_code,
                    source_hash,
                    source_text,
                    translated_text,
                    created_at,
                    updated_at
                )
                values (
                    @languageCode,
                    @sourceHash,
                    @sourceText,
                    @translatedText,
                    now(),
                    now()
                )
                on conflict (language_code, source_hash) do update
                set source_text = excluded.source_text,
                    translated_text = excluded.translated_text,
                    updated_at = now();
                """;
            cmd.Parameters.AddWithValue("languageCode", languageCode);
            cmd.Parameters.AddWithValue("sourceHash", HashText(item.Source));
            cmd.Parameters.AddWithValue("sourceText", item.Source);
            cmd.Parameters.AddWithValue("translatedText", item.Translation);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalizeSource(value)));
        return Convert.ToHexString(bytes);
    }

    public static string CanonicalizeSource(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    public static string NormalizeSource(string? value)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            value ?? string.Empty,
            @"\s+",
            " ").Trim();
    }

    private static string HashLegacyText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeSource(value)));
        return Convert.ToHexString(bytes);
    }

    private sealed record KnownTranslationRow(
        string SourceHash,
        string SourceText,
        string TranslatedText);
}
