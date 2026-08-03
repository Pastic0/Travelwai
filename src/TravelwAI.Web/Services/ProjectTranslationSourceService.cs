using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace TravelwAI.Web.Services;

/// <summary>
/// Collects Vietnamese text from public project data so a language switch can
/// prepare translations for every public page, not only the page currently open.
/// Private messages, user profiles, orders, notifications and account data are
/// deliberately excluded.
/// </summary>
public sealed class ProjectTranslationSourceService
{
    private const int MaximumDocuments = 4_000;
    private const int MaximumSources = 8_000;
    private const int MaximumSourceLength = 20_000;
    private const int MaximumTotalCharacters = 2_000_000;

    private static readonly string[] PublicCollections =
    {
        "tours",
        "travel_posts",
        "provinces",
        "destinations",
        "plan_status_options",
        "province_tags",
        "province_travel_tags",
        "plan_travel_tags",
        "post_tour_offers"
    };

    private static readonly HashSet<string> ExcludedLeafNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "uid", "uuid", "document_id", "documentId",
        "user_id", "userId", "author_id", "authorId", "owner_id", "ownerId",
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

    private static readonly Regex VietnameseUniqueRegex = new(
        "[ăâđêôơưĂÂĐÊÔƠƯạảãấầẩẫậắằẳẵặẹẻẽếềểễệịỉĩọỏõốồổỗộớờởỡợụủũứừửữựỳỵỷỹ]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LatinDiacriticRegex = new(
        "[À-ỹ]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WordRegex = new(
        @"[a-z0-9]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProtectedValueRegex = new(
        @"^(?:https?://|www\.|/|\\|data:|mailto:)|^[^\s@]+@[^\s@]+\.[^\s@]+$|^\+?[\d\s().-]{7,}$|^[\s\d.,:%+\-–—/\\|()\[\]{}₫$€£¥₹₩₽]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> VietnameseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "xin", "chao", "giang", "gon", "noi", "lich", "kham", "pha", "dia", "diem",
        "tham", "quan", "viet", "binh", "luan", "nhan", "nguoi", "dung", "thanh", "pho",
        "tinh", "hoi", "trinh", "thong", "bao", "kiem", "quay", "tiep", "hoan", "yeu",
        "cau", "mat", "khau", "khoan", "dang", "nhap", "ky", "tour", "bai", "tim"
    };

    private static readonly HashSet<string> VietnamesePhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "xinchao", "camon", "hengap", "gaplai", "vuilong", "dangnhap", "dangky", "quenmatkhau",
        "matkhau", "taikhoan", "dulich", "lichtrinh", "baiviet", "binhluan", "tinnhan",
        "nguoidung", "thanhpho", "trangchu", "lienhe", "thongtin", "thongbao", "timkiem",
        "quaylai", "tieptuc", "hoantat", "yeucau", "noidung", "thanhtoan", "dattour"
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ProjectTranslationSourceService> _logger;

    public ProjectTranslationSourceService(
        NpgsqlDataSource dataSource,
        ILogger<ProjectTranslationSourceService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetPublicProjectSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var totalCharacters = 0;

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select data::text
                from app_documents
                where collection = any(@collections)
                order by updated_at desc
                limit @limit;
                """;
            command.Parameters.AddWithValue(
                "collections",
                NpgsqlDbType.Array | NpgsqlDbType.Text,
                PublicCollections);
            command.Parameters.AddWithValue("limit", MaximumDocuments);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)
                   && result.Count < MaximumSources
                   && totalCharacters < MaximumTotalCharacters)
            {
                var json = reader.GetString(0);
                using var document = JsonDocument.Parse(json);
                Collect(document.RootElement, string.Empty, result, ref totalCharacters);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể đọc toàn bộ nội dung công khai để chuẩn bị bản dịch dự án.");
        }

        return result.ToList();
    }

    private static void Collect(
        JsonElement element,
        string leafName,
        ISet<string> result,
        ref int totalCharacters)
    {
        if (result.Count >= MaximumSources || totalCharacters >= MaximumTotalCharacters) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ExcludedLeafNames.Contains(property.Name)) continue;
                    Collect(property.Value, property.Name, result, ref totalCharacters);
                    if (result.Count >= MaximumSources || totalCharacters >= MaximumTotalCharacters) break;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, leafName, result, ref totalCharacters);
                    if (result.Count >= MaximumSources || totalCharacters >= MaximumTotalCharacters) break;
                }
                break;

            case JsonValueKind.String:
                if (ExcludedLeafNames.Contains(leafName)) return;
                var source = PersistentTranslationStore.CanonicalizeSource(element.GetString());
                if (source.Length == 0 || source.Length > MaximumSourceLength || !LooksVietnamese(source)) return;
                if (result.Add(source)) totalCharacters += source.Length;
                break;
        }
    }

    private static bool LooksVietnamese(string value)
    {
        var text = value.Trim();
        if (text.Length < 2 || ProtectedValueRegex.IsMatch(text)) return false;
        if (VietnameseUniqueRegex.IsMatch(text)) return true;

        var normalized = RemoveVietnameseMarks(text);
        var tokens = WordRegex.Matches(normalized).Cast<Match>().Select(match => match.Value).ToArray();
        if (tokens.Length == 0) return false;

        var requiredScore = LatinDiacriticRegex.IsMatch(text) ? 1 : 2;
        var score = 0;
        for (var index = 0; index < tokens.Length; index += 1)
        {
            if (VietnameseWords.Contains(tokens[index])) score += 1;
            if (index + 1 < tokens.Length
                && VietnamesePhrases.Contains(tokens[index] + tokens[index + 1]))
            {
                score += 2;
            }

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
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(character switch
            {
                'đ' => 'd',
                'Đ' => 'D',
                _ => char.ToLowerInvariant(character)
            });
        }

        return builder.ToString();
    }
}
