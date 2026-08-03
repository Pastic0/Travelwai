using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TravelwAI.Web.Options;

namespace TravelwAI.Web.Services;

public sealed class ExternalKnowledgeImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ExternalKnowledgeOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ExternalKnowledgeState _state;
    private readonly ILogger<ExternalKnowledgeImportService> _logger;
    private readonly SemaphoreSlim _importLock = new(1, 1);

    public ExternalKnowledgeImportService(
        IHttpClientFactory httpClientFactory,
        IOptions<ExternalKnowledgeOptions> options,
        IHostEnvironment environment,
        ExternalKnowledgeState state,
        ILogger<ExternalKnowledgeImportService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _environment = environment;
        _state = state;
        _logger = logger;
    }

    public async Task EnsureLoadedAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;

        await _importLock.WaitAsync(cancellationToken);
        try
        {
            var directory = ResolveDataDirectory();
            Directory.CreateDirectory(directory);
            var indexPath = Path.Combine(directory, "knowledge-index.jsonl");

            if (!forceRefresh && File.Exists(indexPath))
            {
                var loaded = await LoadIndexAsync(indexPath, cancellationToken);
                if (loaded.Count > 0)
                {
                    ReplaceState(loaded, File.GetLastWriteTimeUtc(indexPath));
                    if (!_options.RefreshOnStartup) return;
                }
            }

            await ImportAllSourcesAsync(directory, indexPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _state.MarkFailed(ex);
            _logger.LogError(ex, "Không thể nhập bộ dữ liệu tri thức bên ngoài cho AI");
            throw;
        }
        finally
        {
            _importLock.Release();
        }
    }

    private async Task ImportAllSourcesAsync(
        string directory,
        string indexPath,
        CancellationToken cancellationToken)
    {
        _state.MarkLoading();
        var downloadsDirectory = Path.Combine(directory, "downloads");
        Directory.CreateDirectory(downloadsDirectory);

        var documents = new List<ExternalKnowledgeDocument>();
        var sourceErrors = new List<Exception>();
        var maxInMemory = Math.Clamp(_options.MaxInMemoryDocuments, 1000, 200000);

        foreach (var source in _options.Sources.Where(item => item.Enabled))
        {
            if (documents.Count >= maxInMemory) break;
            if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Url)) continue;

            try
            {
                var filePath = await DownloadSourceAsync(source, downloadsDirectory, cancellationToken);
                var remaining = maxInMemory - documents.Count;
                var sourceLimit = Math.Clamp(source.MaxDocuments, 1, remaining);
                var imported = await ParseSourceAsync(filePath, source, sourceLimit, cancellationToken);
                documents.AddRange(imported.Take(remaining));
                _logger.LogInformation(
                    "Đã nhập {Count} tài liệu từ nguồn {Source}",
                    imported.Count,
                    source.Name);
            }
            catch (Exception ex)
            {
                sourceErrors.Add(ex);
                _logger.LogWarning(ex, "Không thể nhập nguồn tri thức {Source}", source.Name);
            }
        }

        documents = documents
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(maxInMemory)
            .ToList();

        if (documents.Count == 0)
        {
            throw new InvalidOperationException(
                "Không nhập được tài liệu nào từ các nguồn đã cấu hình.",
                sourceErrors.FirstOrDefault());
        }

        await SaveIndexAsync(indexPath, documents, cancellationToken);
        ReplaceState(documents, DateTimeOffset.UtcNow);
    }

    private async Task<string> DownloadSourceAsync(
        ExternalKnowledgeSourceOptions source,
        string downloadsDirectory,
        CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            var localPath = Path.IsPathRooted(source.Url)
                ? source.Url
                : Path.Combine(_environment.ContentRootPath, source.Url);
            if (!File.Exists(localPath)) throw new FileNotFoundException("Không tìm thấy file dữ liệu.", localPath);
            return localPath;
        }

        var extension = NormalizeFormat(source.Format) switch
        {
            "zip" => ".zip",
            "jsonl" => ".jsonl",
            _ => ".json"
        };
        var destination = Path.Combine(downloadsDirectory, SafeFileName(source.Name) + extension);
        var temporary = destination + ".tmp";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_options.RequestTimeoutMinutes, 1, 120)));

        var client = _httpClientFactory.CreateClient("ExternalKnowledge");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("TravelwAI/1.0 external-knowledge-importer");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));

        if (uri.Host.Contains("kaggle.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(_options.KaggleApiToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    _options.KaggleApiToken.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(_options.KaggleUsername) &&
                     !string.IsNullOrWhiteSpace(_options.KaggleApiKey))
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{_options.KaggleUsername.Trim()}:{_options.KaggleApiKey.Trim()}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }
        }

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        response.EnsureSuccessStatusCode();

        await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
        await using (var output = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await input.CopyToAsync(output, 128 * 1024, timeout.Token);
        }

        File.Move(temporary, destination, true);
        return destination;
    }

    private async Task<List<ExternalKnowledgeDocument>> ParseSourceAsync(
        string filePath,
        ExternalKnowledgeSourceOptions source,
        int limit,
        CancellationToken cancellationToken)
    {
        var format = NormalizeFormat(source.Format);
        if (format == "zip" || Path.GetExtension(filePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return await ParseZipAsync(filePath, source, limit, cancellationToken);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return format == "jsonl"
            ? await ParseJsonLinesAsync(stream, source, limit, cancellationToken)
            : await ParseJsonAsync(stream, source, limit, cancellationToken);
    }

    private async Task<List<ExternalKnowledgeDocument>> ParseZipAsync(
        string filePath,
        ExternalKnowledgeSourceOptions source,
        int limit,
        CancellationToken cancellationToken)
    {
        var documents = new List<ExternalKnowledgeDocument>();
        using var archive = ZipFile.OpenRead(filePath);
        foreach (var entry in archive.Entries
                     .Where(item => item.Length > 0)
                     .OrderByDescending(item => item.Name.Contains("train", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(item => item.FullName, StringComparer.OrdinalIgnoreCase))
        {
            if (documents.Count >= limit) break;
            var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (extension is not ".json" and not ".jsonl" and not ".ndjson") continue;

            await using var entryStream = entry.Open();
            var remaining = limit - documents.Count;
            var parsed = extension is ".jsonl" or ".ndjson"
                ? await ParseJsonLinesAsync(entryStream, source, remaining, cancellationToken)
                : await ParseJsonAsync(entryStream, source, remaining, cancellationToken);
            documents.AddRange(parsed);
        }
        return documents;
    }

    private async Task<List<ExternalKnowledgeDocument>> ParseJsonAsync(
        Stream stream,
        ExternalKnowledgeSourceOptions source,
        int limit,
        CancellationToken cancellationToken)
    {
        var documents = new List<ExternalKnowledgeDocument>();
        var prefix = await ReadFirstNonWhitespaceByteAsync(stream, cancellationToken);
        if (stream.CanSeek) stream.Seek(0, SeekOrigin.Begin);
        else
        {
            var copy = new MemoryStream();
            if (prefix.HasValue) copy.WriteByte(prefix.Value);
            await stream.CopyToAsync(copy, cancellationToken);
            copy.Position = 0;
            stream = copy;
        }

        if (prefix == (byte)'[')
        {
            await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                               stream,
                               JsonOptions,
                               cancellationToken))
            {
                if (documents.Count >= limit) break;
                documents.AddRange(ExtractDocuments(item, source, limit - documents.Count));
            }
            return documents;
        }

        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        documents.AddRange(ExtractDocuments(document.RootElement, source, limit));
        return documents.Take(limit).ToList();
    }

    private async Task<List<ExternalKnowledgeDocument>> ParseJsonLinesAsync(
        Stream stream,
        ExternalKnowledgeSourceOptions source,
        int limit,
        CancellationToken cancellationToken)
    {
        var documents = new List<ExternalKnowledgeDocument>();
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, leaveOpen: true);
        while (!reader.EndOfStream && documents.Count < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var item = JsonDocument.Parse(line);
                documents.AddRange(ExtractDocuments(item.RootElement, source, limit - documents.Count));
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Bỏ qua một dòng JSONL không hợp lệ từ {Source}", source.Name);
            }
        }
        return documents;
    }

    private static IEnumerable<ExternalKnowledgeDocument> ExtractDocuments(
        JsonElement element,
        ExternalKnowledgeSourceOptions source,
        int limit)
    {
        if (limit <= 0) yield break;

        if (element.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var child in element.EnumerateArray())
            {
                foreach (var document in ExtractDocuments(child, source, limit - count))
                {
                    yield return document;
                    count++;
                    if (count >= limit) yield break;
                }
            }
            yield break;
        }

        if (element.ValueKind != JsonValueKind.Object) yield break;

        if (TryGetProperty(element, "questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
        {
            var category = ReadScalar(element, "category");
            var keyword = FirstNonEmpty(ReadScalar(element, "keyword"), ReadScalar(element, "title"));
            var imageId = ReadScalar(element, "image_id");
            var baseContext = JoinNonEmpty(
                Prefix("Mã ảnh", imageId),
                Prefix("Đường dẫn ảnh", ReadScalar(element, "image_path")),
                Prefix("Mô tả", ReadNestedScalar(element, "image_analysis", "overall_description")),
                Prefix("Nhóm văn hóa", ReadNestedScalar(element, "cultural_context", "cultural_category")),
                Prefix("Khu vực", ReadNestedScalar(element, "cultural_context", "regional_significance")),
                Prefix("Lịch sử", ReadNestedScalar(element, "cultural_context", "historical_context")),
                Prefix("Ý nghĩa hiện đại", ReadNestedScalar(element, "cultural_context", "modern_relevance")));

            var questionParts = new List<string>();
            var questionTags = new List<string>();
            foreach (var question in questions.EnumerateArray())
            {
                if (question.ValueKind != JsonValueKind.Object) continue;
                var questionText = ReadScalar(question, "question");
                var answer = ReadScalar(question, "answer");
                var explanation = ReadScalar(question, "detailed_explanation");
                var significance = ReadScalar(question, "cultural_significance");
                var additional = TryGetProperty(question, "additional_context", out var context)
                    ? FlattenScalars(context, 1000)
                    : string.Empty;
                questionParts.Add(JoinNonEmpty(
                    Prefix("Câu hỏi", questionText),
                    Prefix("Trả lời", answer),
                    Prefix("Giải thích", explanation),
                    Prefix("Ý nghĩa văn hóa", significance),
                    Prefix("Thông tin bổ sung", additional)));
                questionTags.Add(ReadScalar(question, "question_type"));
                questionTags.Add(ReadScalar(question, "difficulty"));
            }

            var text = JoinNonEmpty(baseContext, string.Join(" | ", questionParts));
            var created = CreateDocument(
                source,
                FirstNonEmpty(keyword, imageId, "Văn hóa Việt Nam"),
                text,
                JoinNonEmpty(category, keyword, string.Join(" ", questionTags.Where(value => !string.IsNullOrWhiteSpace(value)))));
            if (created is not null) yield return created;
            yield break;
        }

        if (TryExtractConversation(element, out var conversationTitle, out var conversationText, out var conversationTags))
        {
            var created = CreateDocument(source, conversationTitle, conversationText, conversationTags);
            if (created is not null) yield return created;
            yield break;
        }

        var questionValue = FirstNonEmpty(
            ReadScalar(element, "question"),
            ReadScalar(element, "instruction"),
            ReadScalar(element, "prompt"),
            ReadScalar(element, "input"));
        var answerValue = FirstNonEmpty(
            ReadScalar(element, "answer"),
            ReadScalar(element, "output"),
            ReadScalar(element, "response"),
            ReadScalar(element, "completion"));

        if (!string.IsNullOrWhiteSpace(questionValue) && !string.IsNullOrWhiteSpace(answerValue))
        {
            var title = FirstNonEmpty(
                ReadScalar(element, "title"),
                ReadScalar(element, "topic"),
                ReadScalar(element, "destination"),
                ReadScalar(element, "location"),
                questionValue);
            var text = JoinNonEmpty(
                Prefix("Câu hỏi", questionValue),
                Prefix("Trả lời", answerValue),
                Prefix("Bối cảnh", ReadScalar(element, "context")),
                Prefix("Giải thích", ReadScalar(element, "explanation")));
            var tags = JoinNonEmpty(
                ReadScalar(element, "category"),
                ReadScalar(element, "topic"),
                ReadScalar(element, "location"),
                ReadScalar(element, "province"));
            var created = CreateDocument(source, title, text, tags);
            if (created is not null) yield return created;
            yield break;
        }

        foreach (var containerName in new[] { "data", "items", "records", "examples", "train", "validation", "test" })
        {
            if (!TryGetProperty(element, containerName, out var container) || container.ValueKind != JsonValueKind.Array) continue;
            var count = 0;
            foreach (var document in ExtractDocuments(container, source, limit))
            {
                yield return document;
                count++;
                if (count >= limit) yield break;
            }
            yield break;
        }

        var genericText = FlattenScalars(element, 5000);
        var genericTitle = FirstNonEmpty(
            ReadScalar(element, "title"),
            ReadScalar(element, "name"),
            ReadScalar(element, "destination"),
            ReadScalar(element, "location"),
            ReadScalar(element, "province"),
            ReadScalar(element, "keyword"));
        var genericDocument = CreateDocument(
            source,
            FirstNonEmpty(genericTitle, "Dữ liệu du lịch Việt Nam"),
            genericText,
            JoinNonEmpty(ReadScalar(element, "category"), ReadScalar(element, "province")));
        if (genericDocument is not null) yield return genericDocument;
    }

    private static bool TryExtractConversation(
        JsonElement element,
        out string title,
        out string text,
        out string tags)
    {
        title = string.Empty;
        text = string.Empty;
        tags = string.Empty;
        JsonElement messages;
        if (!TryGetProperty(element, "messages", out messages) &&
            !TryGetProperty(element, "conversations", out messages))
            return false;
        if (messages.ValueKind != JsonValueKind.Array) return false;

        var parts = new List<string>();
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object) continue;
            var role = FirstNonEmpty(
                ReadScalar(message, "role"),
                ReadScalar(message, "from"),
                ReadScalar(message, "speaker"));
            var content = FirstNonEmpty(
                ReadScalar(message, "content"),
                ReadScalar(message, "value"),
                ReadScalar(message, "text"));
            if (string.IsNullOrWhiteSpace(content)) continue;
            parts.Add(string.IsNullOrWhiteSpace(role) ? content : $"{role}: {content}");
            if (string.IsNullOrWhiteSpace(title) &&
                (role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                 role.Equals("human", StringComparison.OrdinalIgnoreCase)))
                title = content;
        }

        if (parts.Count < 2) return false;
        text = string.Join("\n", parts);
        tags = JoinNonEmpty(ReadScalar(element, "category"), ReadScalar(element, "topic"));
        title = FirstNonEmpty(ReadScalar(element, "title"), title, "Hỏi đáp du lịch Việt Nam");
        return true;
    }

    private static ExternalKnowledgeDocument? CreateDocument(
        ExternalKnowledgeSourceOptions source,
        string title,
        string text,
        string tags)
    {
        title = Clean(title, 320);
        text = Clean(text, 5500);
        tags = Clean(tags, 700);
        if (text.Length < 20) return null;

        var identity = $"{source.Name}\n{title}\n{text}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..24];
        return new ExternalKnowledgeDocument(
            id,
            source.Name.Trim(),
            title,
            text,
            tags,
            source.Url.Trim(),
            source.Attribution.Trim(),
            source.License.Trim());
    }

    private async Task<List<ExternalKnowledgeDocument>> LoadIndexAsync(
        string indexPath,
        CancellationToken cancellationToken)
    {
        var documents = new List<ExternalKnowledgeDocument>();
        var maximum = Math.Clamp(_options.MaxInMemoryDocuments, 1000, 200000);
        using var reader = new StreamReader(indexPath, Encoding.UTF8, true, 64 * 1024);
        while (!reader.EndOfStream && documents.Count < maximum)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var document = JsonSerializer.Deserialize<ExternalKnowledgeDocument>(line, JsonOptions);
                if (document is not null) documents.Add(document);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Bỏ qua một dòng chỉ mục tri thức không hợp lệ");
            }
        }
        return documents;
    }

    private static async Task SaveIndexAsync(
        string indexPath,
        IReadOnlyCollection<ExternalKnowledgeDocument> documents,
        CancellationToken cancellationToken)
    {
        var temporary = indexPath + ".tmp";
        await using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024))
        {
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(document, JsonOptions));
            }
        }
        File.Move(temporary, indexPath, true);
    }

    private void ReplaceState(
        IReadOnlyCollection<ExternalKnowledgeDocument> documents,
        DateTimeOffset importedAt)
    {
        var sourceCounts = documents
            .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        _state.Replace(documents, importedAt, sourceCounts);
    }

    private string ResolveDataDirectory()
    {
        var configured = string.IsNullOrWhiteSpace(_options.DataDirectory)
            ? "App_Data/ai-knowledge"
            : _options.DataDirectory.Trim();
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(_environment.ContentRootPath, configured);
    }

    private static async Task<byte?> ReadFirstNonWhitespaceByteAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, cancellationToken) == 1)
        {
            var value = buffer[0];
            if (value is 0xEF or 0xBB or 0xBF) continue; // UTF-8 BOM
            if (!char.IsWhiteSpace((char)value)) return value;
        }
        return null;
    }

    private static string NormalizeFormat(string? format)
    {
        var normalized = format?.Trim().TrimStart('.').ToLowerInvariant() ?? "json";
        return normalized switch
        {
            "ndjson" => "jsonl",
            "jsonlines" => "jsonl",
            _ => normalized
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string ReadScalar(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) ? ScalarToString(value) : string.Empty;
    }

    private static string ReadNestedScalar(JsonElement element, string objectName, string propertyName)
    {
        return TryGetProperty(element, objectName, out var nested) &&
               TryGetProperty(nested, propertyName, out var value)
            ? ScalarToString(value)
            : string.Empty;
    }

    private static string ScalarToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static string FlattenScalars(JsonElement element, int maxLength)
    {
        var parts = new List<string>();
        Flatten(element, parts, maxLength);
        return Clean(string.Join("; ", parts), maxLength);
    }

    private static void Flatten(JsonElement element, List<string> parts, int maxLength)
    {
        if (parts.Sum(item => item.Length) >= maxLength) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Contains("image", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        (property.Value.GetString()?.Length ?? 0) > 300)
                        continue;
                    var scalar = ScalarToString(property.Value);
                    if (!string.IsNullOrWhiteSpace(scalar)) parts.Add($"{property.Name}: {scalar}");
                    else Flatten(property.Value, parts, maxLength);
                    if (parts.Sum(item => item.Length) >= maxLength) break;
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray().Take(30))
                {
                    var scalar = ScalarToString(child);
                    if (!string.IsNullOrWhiteSpace(scalar)) parts.Add(scalar);
                    else Flatten(child, parts, maxLength);
                    if (parts.Sum(item => item.Length) >= maxLength) break;
                }
                break;
        }
    }

    private static string Prefix(string label, string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : $"{label}: {value}";

    private static string JoinNonEmpty(params string[] values) =>
        string.Join("; ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "…";
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
            builder.Append(invalid.Contains(character) ? '_' : character);
        return builder.Length == 0 ? "dataset" : builder.ToString();
    }
}
