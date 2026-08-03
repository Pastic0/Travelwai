using Npgsql;
using TravelwAI.Models.Common;

namespace TravelwAI.Web.Services;


public sealed class ProvinceTranslationRepairService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PersistentTranslationStore _translationStore;
    private readonly ILogger<ProvinceTranslationRepairService> _logger;

    public ProvinceTranslationRepairService(
        NpgsqlDataSource dataSource,
        PersistentTranslationStore translationStore,
        ILogger<ProvinceTranslationRepairService> logger)
    {
        _dataSource = dataSource;
        _translationStore = translationStore;
        _logger = logger;
    }

    public async Task RepairAsync(CancellationToken cancellationToken = default)
    {
        var translations = VietnamesePlaceName.AllKnownNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                source => source,
                VietnamesePlaceName.ToAscii,
                StringComparer.Ordinal);

        await _translationStore.SaveTextTranslationsAsync(
            translations,
            "en",
            cancellationToken);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var pair in translations)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                update app_document_translations
                set translated_text = @translatedText,
                    updated_at = now()
                where language_code = 'en'
                  and lower(source_text) = lower(@sourceText)
                  and translated_text is distinct from @translatedText;
                """;
            command.Parameters.AddWithValue("sourceText", pair.Key);
            command.Parameters.AddWithValue("translatedText", pair.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        _logger.LogInformation("Đã chuẩn hóa cache backend cho {Count} tên tỉnh/thành và địa danh hành chính.", translations.Count);
    }
}
