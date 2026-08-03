using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TravelwAI.Web.Options;
using TravelwAI.Models.Common;

namespace TravelwAI.Web.Services;

public sealed class PersistentDocumentTranslationService
{
    private const string TargetLanguage = "en";

    private static readonly HashSet<string> UserGeneratedCollections = new(StringComparer.OrdinalIgnoreCase)
    {
        "post_comments",
        "memories",
        "shared_memories",
        "schedules",
        "plans",
        "feedbacks"
    };

    private static readonly Regex VietnameseUniqueRegex = new(
        "[ăâđêôơưĂÂĐÊÔƠƯạảãấầẩẫậắằẳẵặẹẻẽếềểễệịỉĩọỏõốồổỗộớờởỡợụủũứừửữựỳỵỷỹ]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LatinDiacriticRegex = new(
        "[À-ỹ]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AsciiWordRegex = new(
        @"[a-z0-9]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> VietnameseAsciiWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "xin", "chao", "giang", "gon", "noi", "lich",
        "kham", "pha", "dia", "diem", "tham", "quan", "viet", "binh", "luan",
        "nhan", "nguoi", "dung", "thanh", "pho", "tinh", "hoi", "trinh", "thong",
        "bao", "kiem", "quay", "tiep", "hoan", "yeu", "cau", "mat", "khau",
        "khoan"
    };

    private static readonly HashSet<string> VietnameseAsciiPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "xinchao", "camon", "hengap", "gaplai", "vuilong", "dangnhap", "dangky", "quenmatkhau",
        "matkhau", "taikhoan", "dulich", "lichtrinh", "baiviet", "binhluan",
        "tinnhan", "nguoidung", "thanhpho", "trangchu", "lienhe", "thongtin",
        "thongbao", "timkiem", "quaylai", "tieptuc", "hoantat", "yeucau",
        "noidung", "saigon", "hanoi", "hagiang", "hanam", "angiang", "longan",
        "laocai", "gialai", "kontum", "tayninh", "ninhbinh", "hoabinh",
        "bacninh", "namdinh", "haiphong"
    };

    private static readonly Regex UrlOrEmailRegex = new(
        @"^(?:https?://|www\.|/|\\|data:|mailto:)|^[^\s@]+@[^\s@]+\.[^\s@]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DateOrCodeRegex = new(
        @"^(?:\d{1,4}[-/:]\d{1,2}(?:[-/:]\d{1,4})?(?:[ T]\d{1,2}:\d{2}(?::\d{2})?)?|[A-Z0-9_\-.]{5,})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ExcludedLeafNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "uid", "uuid", "document_id", "documentId",
        "user_id", "userId", "author_id", "authorId", "buyer_id", "buyerId",
        "seller_id", "sellerId", "tour_id", "tourId", "schedule_id", "scheduleId",
        "conversation_id", "conversationId", "message_id", "messageId",
        "email", "phone", "telephone", "username", "password", "token", "refresh_token",
        "slug", "code", "role", "type", "mime_type", "mimeType",
        "image", "images", "image_url", "imageUrl", "image_urls", "imageUrls",
        "video", "videos", "video_url", "videoUrl", "video_urls", "videoUrls",
        "media", "media_items", "mediaItems", "media_urls", "mediaUrls",
        "attachment", "attachments", "file", "files", "file_name", "fileName", "path", "url",
        "latitude", "longitude", "lat", "lng", "coordinates", "color", "icon",
        "created_at", "createdAt", "updated_at", "updatedAt", "deleted_at", "deletedAt",
        "start_date", "startDate", "end_date", "endDate", "expires_at", "expiresAt",
        "published_at", "publishedAt", "registered_at", "registeredAt",
        "source_hash", "sourceHash", "language", "language_code", "languageCode"
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly OllamaAiService _ollama;
    private readonly PersistentTranslationOptions _options;
    private readonly ILogger<PersistentDocumentTranslationService> _logger;
    private readonly HashSet<string> _collections;

    public PersistentDocumentTranslationService(
        NpgsqlDataSource dataSource,
        OllamaAiService ollama,
        IOptions<PersistentTranslationOptions> options,
        ILogger<PersistentDocumentTranslationService> logger)
    {
        _dataSource = dataSource;
        _ollama = ollama;
        _options = options.Value;
        _logger = logger;
        _collections = (_options.Collections ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => !UserGeneratedCollections.Contains(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return false;

        var workItem = await ClaimNextAsync(cancellationToken);
        if (workItem is null) return false;

        try
        {
            if (UserGeneratedCollections.Contains(workItem.Collection))
            {
                await DeleteDocumentTranslationsAsync(workItem, cancellationToken);
                await CompleteAsync(workItem, cancellationToken);
                return true;
            }

            if (!_collections.Contains(workItem.Collection))
            {
                await CompleteAsync(workItem, cancellationToken);
                return true;
            }

            var documentJson = await LoadDocumentJsonAsync(workItem, cancellationToken);
            if (documentJson is null)
            {
                await CompleteAsync(workItem, cancellationToken);
                return true;
            }

            using var document = JsonDocument.Parse(documentJson);
            var fields = new List<TranslatableField>();
            CollectFields(document.RootElement, string.Empty, fields);

            var currentByPath = fields
                .GroupBy(field => field.FieldPath, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            var existing = await LoadExistingDocumentTranslationsAsync(workItem, cancellationToken);
            var changedFields = currentByPath.Values
                .Where(field => !existing.TryGetValue(field.FieldPath, out var saved)
                    || !string.Equals(saved.SourceHash, field.SourceHash, StringComparison.Ordinal)
                    || !string.Equals(saved.SourceText, field.SourceText, StringComparison.Ordinal))
                .ToList();

            var translatedBySource = await TranslateChangedFieldsAsync(changedFields, cancellationToken);
            await SaveDocumentTranslationsAsync(
                workItem,
                currentByPath,
                existing,
                translatedBySource,
                cancellationToken);

            await CompleteAsync(workItem, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Không thể đồng bộ bản dịch vĩnh viễn cho {Collection}/{DocumentId}.",
                workItem.Collection,
                workItem.DocumentId);

            await FailAsync(workItem, ex, cancellationToken);
            return true;
        }
    }

    private async Task<TranslationWorkItem?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var lockMinutes = Math.Clamp(_options.LockMinutes, 2, 60);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            with candidate as (
                select collection, document_id
                from app_translation_queue
                where next_attempt_at <= now()
                  and (locked_until is null or locked_until < now())
                order by requested_at asc
                for update skip locked
                limit 1
            )
            update app_translation_queue queue
            set locked_until = now() + (@lockMinutes * interval '1 minute'),
                lock_token = @lockToken
            from candidate
            where queue.collection = candidate.collection
              and queue.document_id = candidate.document_id
            returning queue.collection,
                      queue.document_id,
                      queue.requested_at,
                      queue.attempts,
                      queue.lock_token;
            """;
        cmd.Parameters.AddWithValue("lockMinutes", lockMinutes);
        cmd.Parameters.AddWithValue("lockToken", token);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new TranslationWorkItem(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDateTime(2),
            reader.GetInt32(3),
            reader.GetString(4));
    }

    private async Task<string?> LoadDocumentJsonAsync(
        TranslationWorkItem workItem,
        CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select data::text
            from app_documents
            where collection = @collection
              and id = @documentId
            limit 1;
            """;
        cmd.Parameters.AddWithValue("collection", workItem.Collection);
        cmd.Parameters.AddWithValue("documentId", workItem.DocumentId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value?.ToString();
    }

    private async Task<Dictionary<string, ExistingTranslation>> LoadExistingDocumentTranslationsAsync(
        TranslationWorkItem workItem,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ExistingTranslation>(StringComparer.Ordinal);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select field_path, source_hash, source_text, translated_text
            from app_document_translations
            where collection = @collection
              and document_id = @documentId
              and language_code = @languageCode;
            """;
        cmd.Parameters.AddWithValue("collection", workItem.Collection);
        cmd.Parameters.AddWithValue("documentId", workItem.DocumentId);
        cmd.Parameters.AddWithValue("languageCode", TargetLanguage);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = new ExistingTranslation(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3));
        }

        return result;
    }

    private async Task<Dictionary<string, string>> TranslateChangedFieldsAsync(
        IReadOnlyCollection<TranslatableField> changedFields,
        CancellationToken cancellationToken)
    {
        var sources = changedFields
            .Select(field => field.SourceText)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (sources.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);


        var translated = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var source in sources)
        {
            if (VietnamesePlaceName.TryGetEnglishName(source, out var englishName))
            {
                translated[source] = englishName;
            }
            else
            {
                missing.Add(source);
            }
        }

        var chunkLimit = Math.Clamp(_options.TranslationChunkLength, 400, 4000);
        var shortBatch = new List<string>();
        var shortBatchCharacters = 0;

        async Task FlushShortBatchAsync()
        {
            if (shortBatch.Count == 0) return;
            var batch = shortBatch.ToList();
            shortBatch.Clear();
            shortBatchCharacters = 0;

            var batchTranslations = await _ollama.TranslateUiToEnglishAsync(batch, cancellationToken);
            for (var index = 0; index < batch.Count; index += 1)
            {
                var source = batch[index];
                var translation = index < batchTranslations.Count
                    ? batchTranslations[index]
                    : source;
                if (string.IsNullOrWhiteSpace(translation)
                    || !HasCompatibleLineStructure(source, translation))
                {
                    translation = source;
                }
                translated[source] = translation;
            }
        }

        foreach (var source in missing)
        {
            if (source.Length > chunkLimit)
            {
                await FlushShortBatchAsync();
                var translation = await TranslateLongTextAsync(source, cancellationToken);
                if (string.IsNullOrWhiteSpace(translation)
                    || !HasCompatibleLineStructure(source, translation))
                {
                    translation = source;
                }
                translated[source] = translation;
                continue;
            }

            if (shortBatch.Count >= 40 || (shortBatch.Count > 0 && shortBatchCharacters + source.Length > 18000))
            {
                await FlushShortBatchAsync();
            }

            shortBatch.Add(source);
            shortBatchCharacters += source.Length;
        }

        await FlushShortBatchAsync();


        return translated;
    }

    private async Task<string> TranslateLongTextAsync(
        string source,
        CancellationToken cancellationToken)
    {
        var chunks = SplitText(source, Math.Clamp(_options.TranslationChunkLength, 400, 4000));
        var canonicalTexts = chunks.Select(chunk => chunk.CanonicalText).ToList();
        var translatedPieces = new List<string>(canonicalTexts.Count);

        for (var offset = 0; offset < canonicalTexts.Count; offset += 20)
        {
            var batch = canonicalTexts.Skip(offset).Take(20).ToList();
            var translated = await _ollama.TranslateUiToEnglishAsync(batch, cancellationToken);
            for (var index = 0; index < batch.Count; index += 1)
            {
                translatedPieces.Add(index < translated.Count && !string.IsNullOrWhiteSpace(translated[index])
                    ? translated[index].Trim()
                    : batch[index]);
            }
        }

        var builder = new System.Text.StringBuilder(source.Length + 128);
        for (var index = 0; index < chunks.Count; index += 1)
        {
            var chunk = chunks[index];
            var translated = index < translatedPieces.Count
                ? translatedPieces[index]
                : chunk.CanonicalText;

            builder.Append(chunk.LeadingWhitespace);
            builder.Append(translated);
            builder.Append(chunk.TrailingWhitespace);
        }

        return builder.ToString().Trim();
    }

    private async Task SaveDocumentTranslationsAsync(
        TranslationWorkItem workItem,
        IReadOnlyDictionary<string, TranslatableField> currentByPath,
        IReadOnlyDictionary<string, ExistingTranslation> existing,
        IReadOnlyDictionary<string, string> translatedBySource,
        CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

        var currentPaths = currentByPath.Keys.ToArray();
        await using (var deleteCommand = conn.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            if (currentPaths.Length == 0)
            {
                deleteCommand.CommandText = """
                    delete from app_document_translations
                    where collection = @collection
                      and document_id = @documentId
                      and language_code = @languageCode;
                    """;
            }
            else
            {
                deleteCommand.CommandText = """
                    delete from app_document_translations
                    where collection = @collection
                      and document_id = @documentId
                      and language_code = @languageCode
                      and not (field_path = any(@currentPaths));
                    """;
                var pathParameter = deleteCommand.Parameters.Add(
                    "currentPaths",
                    NpgsqlDbType.Array | NpgsqlDbType.Text);
                pathParameter.Value = currentPaths;
            }

            deleteCommand.Parameters.AddWithValue("collection", workItem.Collection);
            deleteCommand.Parameters.AddWithValue("documentId", workItem.DocumentId);
            deleteCommand.Parameters.AddWithValue("languageCode", TargetLanguage);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var field in currentByPath.Values)
        {
            if (existing.TryGetValue(field.FieldPath, out var saved)
                && string.Equals(saved.SourceHash, field.SourceHash, StringComparison.Ordinal)
                && string.Equals(saved.SourceText, field.SourceText, StringComparison.Ordinal))
            {
                continue;
            }

            var translation = translatedBySource.GetValueOrDefault(field.SourceText);
            if (string.IsNullOrWhiteSpace(translation)) translation = field.SourceText;

            await using var upsertCommand = conn.CreateCommand();
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText = """
                insert into app_document_translations(
                    collection,
                    document_id,
                    field_path,
                    language_code,
                    source_hash,
                    source_text,
                    translated_text,
                    created_at,
                    updated_at
                )
                values (
                    @collection,
                    @documentId,
                    @fieldPath,
                    @languageCode,
                    @sourceHash,
                    @sourceText,
                    @translatedText,
                    now(),
                    now()
                )
                on conflict (collection, document_id, field_path, language_code) do update
                set source_hash = excluded.source_hash,
                    source_text = excluded.source_text,
                    translated_text = excluded.translated_text,
                    updated_at = now();
                """;
            upsertCommand.Parameters.AddWithValue("collection", workItem.Collection);
            upsertCommand.Parameters.AddWithValue("documentId", workItem.DocumentId);
            upsertCommand.Parameters.AddWithValue("fieldPath", field.FieldPath);
            upsertCommand.Parameters.AddWithValue("languageCode", TargetLanguage);
            upsertCommand.Parameters.AddWithValue("sourceHash", field.SourceHash);
            upsertCommand.Parameters.AddWithValue("sourceText", field.SourceText);
            upsertCommand.Parameters.AddWithValue("translatedText", translation);
            await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task DeleteDocumentTranslationsAsync(TranslationWorkItem workItem, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            delete from app_document_translations
            where collection = @collection
              and document_id = @documentId;
            """;
        cmd.Parameters.AddWithValue("collection", workItem.Collection);
        cmd.Parameters.AddWithValue("documentId", workItem.DocumentId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CompleteAsync(TranslationWorkItem workItem, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            delete from app_translation_queue
            where collection = @collection
              and document_id = @documentId
              and lock_token = @lockToken
              and requested_at <= @claimedRequestedAt;

            update app_translation_queue
            set locked_until = null,
                lock_token = null
            where collection = @collection
              and document_id = @documentId
              and lock_token = @lockToken;
            """;
        cmd.Parameters.AddWithValue("collection", workItem.Collection);
        cmd.Parameters.AddWithValue("documentId", workItem.DocumentId);
        cmd.Parameters.AddWithValue("lockToken", workItem.LockToken);
        cmd.Parameters.AddWithValue("claimedRequestedAt", workItem.RequestedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task FailAsync(
        TranslationWorkItem workItem,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var nextAttempt = workItem.Attempts + 1;
        var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 50);
        var delaySeconds = nextAttempt >= maxAttempts
            ? 6 * 60 * 60
            : Math.Min(60 * 60, 10 * (int)Math.Pow(2, Math.Min(nextAttempt, 8)));
        var error = exception.Message;
        if (error.Length > 1800) error = error[..1800];

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            update app_translation_queue
            set attempts = attempts + 1,
                next_attempt_at = @nextAttemptAt,
                locked_until = null,
                lock_token = null,
                last_error = @lastError
            where collection = @collection
              and document_id = @documentId
              and lock_token = @lockToken;
            """;
        cmd.Parameters.AddWithValue("nextAttemptAt", DateTime.UtcNow.AddSeconds(delaySeconds));
        cmd.Parameters.AddWithValue("lastError", error);
        cmd.Parameters.AddWithValue("collection", workItem.Collection);
        cmd.Parameters.AddWithValue("documentId", workItem.DocumentId);
        cmd.Parameters.AddWithValue("lockToken", workItem.LockToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private void CollectFields(JsonElement element, string path, List<TranslatableField> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = path + "/" + EscapePathSegment(property.Name);
                    CollectFields(property.Value, childPath, output);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    CollectFields(item, path + "/" + index, output);
                    index += 1;
                }
                break;

            case JsonValueKind.String:
                var value = (element.GetString() ?? string.Empty).Trim();
                if (ShouldTranslate(path, value))
                {
                    output.Add(new TranslatableField(
                        path,
                        value,
                        PersistentTranslationStore.HashText(value)));
                }
                break;
        }
    }

    private bool ShouldTranslate(string path, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length > Math.Clamp(_options.MaxSourceLength, 1000, 100000)) return false;
        if (!LooksVietnamese(value)) return false;

        var leaf = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        leaf = UnescapePathSegment(leaf);
        if (ExcludedLeafNames.Contains(leaf)) return false;
        if (UrlOrEmailRegex.IsMatch(value)) return false;
        if (DateOrCodeRegex.IsMatch(value)) return false;
        if (value.Count(char.IsLetter) < 2) return false;

        return true;
    }

    private static bool LooksVietnamese(string value)
    {
        if (VietnameseUniqueRegex.IsMatch(value)) return true;

        var tokens = AsciiWordRegex.Matches(RemoveVietnameseMarks(value))
            .Cast<Match>()
            .Select(match => match.Value.ToLowerInvariant())
            .ToList();
        var requiredScore = LatinDiacriticRegex.IsMatch(value) ? 1 : 2;
        var score = 0;
        for (var index = 0; index < tokens.Count; index += 1)
        {
            var token = tokens[index];
            var pair = index + 1 < tokens.Count ? token + tokens[index + 1] : string.Empty;
            if (VietnameseAsciiWords.Contains(token)) score += 1;
            if (pair.Length > 0 && VietnameseAsciiPhrases.Contains(pair)) score += 2;
            if (score >= requiredScore) return true;
        }

        return false;
    }

    private static string RemoveVietnameseMarks(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character is 'đ' or 'Đ' ? 'd' : character);
            }
        }
        return builder.ToString();
    }

    private static bool HasCompatibleLineStructure(string source, string translated)
    {
        static string Signature(string value) =>
            new string((value ?? string.Empty).Where(character => character is '\r' or '\n').ToArray());

        return string.Equals(Signature(source), Signature(translated), StringComparison.Ordinal);
    }

    private static List<TextChunk> SplitText(string source, int maxLength)
    {
        var rawUnits = new List<string>();
        var remaining = source;
        var threshold = (int)Math.Floor(maxLength * 0.45);

        while (remaining.Length > maxLength)
        {
            var cut = remaining.LastIndexOf('\n', maxLength);
            if (cut < threshold) cut = remaining.LastIndexOf(". ", maxLength, StringComparison.Ordinal);
            if (cut < threshold) cut = remaining.LastIndexOf("; ", maxLength, StringComparison.Ordinal);
            if (cut < threshold) cut = remaining.LastIndexOf(", ", maxLength, StringComparison.Ordinal);
            if (cut < threshold) cut = remaining.LastIndexOf(' ', maxLength);

            if (cut < 1)
            {
                cut = maxLength;
            }
            else if (cut + 1 < remaining.Length)
            {
                var pair = remaining.Substring(cut, Math.Min(2, remaining.Length - cut));
                if (pair is ". " or "; " or ", ") cut += 1;
            }

            rawUnits.Add(remaining[..cut]);
            remaining = remaining[cut..];
        }

        if (remaining.Length > 0) rawUnits.Add(remaining);
        if (rawUnits.Count == 0) rawUnits.Add(source);

        var chunks = new List<TextChunk>(rawUnits.Count);
        foreach (var raw in rawUnits)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var leadingLength = raw.Length - raw.TrimStart().Length;
            var trailingLength = raw.Length - raw.TrimEnd().Length;
            var coreLength = raw.Length - leadingLength - trailingLength;
            var leading = leadingLength > 0 ? raw[..leadingLength] : string.Empty;
            var trailing = trailingLength > 0 ? raw[^trailingLength..] : string.Empty;
            var core = raw.Substring(leadingLength, coreLength);
            chunks.Add(new TextChunk(core, leading, trailing));
        }

        if (chunks.Count == 0)
        {
            chunks.Add(new TextChunk(
                source.Trim(),
                string.Empty,
                string.Empty));
        }

        return chunks;
    }

    private static string EscapePathSegment(string value)
    {
        return value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
    }

    private static string UnescapePathSegment(string value)
    {
        return value.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
    }

    private sealed record TranslationWorkItem(
        string Collection,
        string DocumentId,
        DateTime RequestedAt,
        int Attempts,
        string LockToken);

    private sealed record TranslatableField(
        string FieldPath,
        string SourceText,
        string SourceHash);

    private sealed record ExistingTranslation(
        string SourceHash,
        string SourceText,
        string TranslatedText);

    private sealed record TextChunk(
        string CanonicalText,
        string LeadingWhitespace,
        string TrailingWhitespace);

}
