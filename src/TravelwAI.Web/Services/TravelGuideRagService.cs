using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;
using Microsoft.Extensions.Configuration;

namespace TravelwAI.Web.Services;

public sealed class TravelGuideRagService
{
    private const int MaxDocumentChars = 180_000;
    private const int MaxCrawlBytes = 2_500_000;
    private const string LocalEmbeddingModel = "travelwai-local-hash-v1";
    private readonly NpgsqlDataSource _dataSource;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int _embeddingDimensions;
    private readonly int _vectorCandidateLimit;

    public TravelGuideRagService(NpgsqlDataSource dataSource, IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _dataSource = dataSource;
        _httpClientFactory = httpClientFactory;
        _embeddingDimensions = Math.Clamp(ReadInt(configuration, "Rag:EmbeddingDimensions", "RAG_EMBEDDING_DIMENSIONS", 384), 128, 768);
        _vectorCandidateLimit = Math.Clamp(ReadInt(configuration, "Rag:VectorCandidateLimit", "RAG_VECTOR_CANDIDATE_LIMIT", 900), 120, 2500);
    }

    public async Task<string> BuildRagContextAsync(string? message, string? uiContext, CancellationToken cancellationToken = default)
    {
        var results = new List<TravelGuideRagSearchResult>();
        try
        {
            results = await SearchAsync((message ?? string.Empty) + " " + (uiContext ?? string.Empty), 10, cancellationToken);
        }
        catch
        {
            results = new List<TravelGuideRagSearchResult>();
        }

        var builder = new StringBuilder();
        builder.AppendLine("RAG_CONTEXT TravelwAI:");
        builder.AppendLine("Ưu tiên nguồn theo thứ tự: văn bản pháp luật/quyết định chính thức; Cục Di sản, Bộ VHTTDL, UNESCO; UBND/Sở địa phương; bảo tàng/ban quản lý di tích; sách địa chí/nghiên cứu; báo chí chính thống; tư liệu thực địa; nguồn phụ chỉ tham khảo.");

        var cleanUi = BuildUiContextBlock(uiContext);
        if (!string.IsNullOrWhiteSpace(cleanUi)) builder.AppendLine(cleanUi);

        if (results.Count > 0)
        {
            builder.AppendLine("Dữ liệu truy xuất từ kho RAG:");
            foreach (var item in results)
            {
                builder.AppendLine("---");
                builder.AppendLine("Tiêu đề: " + item.Title);
                builder.AppendLine("Nguồn: " + item.SourceName);
                builder.AppendLine("Loại nguồn: " + item.SourceType);
                builder.AppendLine("Độ tin cậy: " + item.Reliability);
                if (!string.IsNullOrWhiteSpace(item.Url)) builder.AppendLine("URL nguồn: " + item.Url);
                builder.AppendLine("Nội dung: " + item.Content);
            }
        }

        foreach (var chunk in GetFallbackChunks()
            .Select(chunk => new { Chunk = chunk, Score = ScoreFallbackChunk(message ?? string.Empty, uiContext ?? string.Empty, chunk) })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Chunk.Reliability)
            .Take(results.Count > 0 ? 4 : 8)
            .Select(item => item.Chunk))
        {
            builder.AppendLine("---");
            builder.AppendLine("Nguồn: " + chunk.Source);
            builder.AppendLine("Độ tin cậy: " + chunk.Reliability);
            builder.AppendLine(chunk.Title + ": " + chunk.Content);
        }

        var text = builder.ToString().Trim();
        return text.Length > 14000 ? text[..14000] : text;
    }

    public async Task<List<TravelGuideRagSearchResult>> SearchAsync(string? query, int limit = 8, CancellationToken cancellationToken = default)
    {
        var normalizedQuery = NormalizeForSearch(query ?? string.Empty);
        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();

        var queryEmbedding = CreateLocalEmbedding(normalizedQuery, _embeddingDimensions);
        var rows = new List<TravelGuideRagSearchResult>();
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        var candidateLimit = Math.Clamp(Math.Max(limit * 80, _vectorCandidateLimit), 120, 2500);
        if (tokens.Count == 0)
        {
            cmd.CommandText = """
                select c.id, c.title, c.content, c.keywords, c.search_text, c.updated_at,
                       s.name, s.source_type, s.reliability, coalesce(c.url, d.url, s.url, '') as url,
                       c.embedding::text as embedding, c.embedding_model
                from travel_guide_chunks c
                join travel_guide_sources s on s.id = c.source_id
                left join travel_guide_documents d on d.id = c.document_id
                where c.status = 'active' and s.status = 'active'
                order by s.reliability desc, c.updated_at desc
                limit @candidate_limit;
                """;
        }
        else
        {
            var clauses = tokens.Select((_, index) => $"c.search_text like @t{index}").ToList();
            clauses.Add("to_tsvector('simple', c.search_text) @@ websearch_to_tsquery('simple', @fts_query)");
            cmd.CommandText = $"""
                with keyword_hits as (
                    select c.id, c.title, c.content, c.keywords, c.search_text, c.updated_at,
                           s.name, s.source_type, s.reliability, coalesce(c.url, d.url, s.url, '') as url,
                           c.embedding::text as embedding, c.embedding_model
                    from travel_guide_chunks c
                    join travel_guide_sources s on s.id = c.source_id
                    left join travel_guide_documents d on d.id = c.document_id
                    where c.status = 'active' and s.status = 'active'
                      and ({string.Join(" or ", clauses)})
                    order by s.reliability desc, c.updated_at desc
                    limit @keyword_limit
                ), broad_candidates as (
                    select c.id, c.title, c.content, c.keywords, c.search_text, c.updated_at,
                           s.name, s.source_type, s.reliability, coalesce(c.url, d.url, s.url, '') as url,
                           c.embedding::text as embedding, c.embedding_model
                    from travel_guide_chunks c
                    join travel_guide_sources s on s.id = c.source_id
                    left join travel_guide_documents d on d.id = c.document_id
                    where c.status = 'active' and s.status = 'active'
                    order by s.reliability desc, c.updated_at desc
                    limit @candidate_limit
                )
                select distinct on (id) id, title, content, keywords, search_text, updated_at,
                       name, source_type, reliability, url, embedding, embedding_model
                from (
                    select * from keyword_hits
                    union all
                    select * from broad_candidates
                ) q;
                """;
            for (var i = 0; i < tokens.Count; i++) cmd.Parameters.AddWithValue($"t{i}", "%" + tokens[i] + "%");
            cmd.Parameters.AddWithValue("fts_query", string.Join(' ', tokens));
            cmd.Parameters.AddWithValue("keyword_limit", Math.Clamp(limit * 30, 80, 600));
        }

        cmd.Parameters.AddWithValue("candidate_limit", candidateLimit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new TravelGuideRagSearchResult
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Content = reader.GetString(2),
                Keywords = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                SearchText = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                UpdatedAt = reader.GetDateTime(5),
                SourceName = reader.GetString(6),
                SourceType = reader.GetString(7),
                Reliability = reader.GetInt32(8),
                Url = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                EmbeddingModel = reader.IsDBNull(11) ? string.Empty : reader.GetString(11)
            };
            item.Embedding = reader.IsDBNull(10) ? null : ParseEmbedding(reader.GetString(10));
            item.VectorScore = item.Embedding is { Length: > 0 }
                ? CosineSimilarity(queryEmbedding, item.Embedding)
                : CosineSimilarity(queryEmbedding, CreateLocalEmbedding(item.SearchText, _embeddingDimensions));
            item.KeywordScore = ScoreDbChunk(normalizedQuery, tokens, item);
            item.Score = item.KeywordScore + (item.VectorScore * 120.0) + FreshnessBoost(item.UpdatedAt);
            rows.Add(item);
        }

        return rows
            .Where(item => tokens.Count == 0 || item.KeywordScore > item.Reliability || item.VectorScore >= 0.12)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Reliability)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 20))
            .ToList();
    }

    public async Task<List<TravelGuideSourceDto>> ListSourcesAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<TravelGuideSourceDto>();
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select id, parent_source_id, name, source_type, url, publisher, reliability, access_level, license, status, updated_at
            from travel_guide_sources
            order by coalesce(parent_source_id, id), case when parent_source_id is null then 0 else 1 end, reliability desc, name asc;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TravelGuideSourceDto
            {
                Id = reader.GetString(0),
                ParentSourceId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Name = reader.GetString(2),
                SourceType = reader.GetString(3),
                Url = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Publisher = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                Reliability = reader.GetInt32(6),
                AccessLevel = reader.GetString(7),
                License = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                Status = reader.GetString(9),
                UpdatedAt = reader.GetDateTime(10)
            });
        }
        return rows;
    }

    public async Task<List<TravelGuideDocumentDto>> ListDocumentsAsync(int limit = 80, CancellationToken cancellationToken = default)
    {
        var rows = new List<TravelGuideDocumentDto>();
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select d.id, d.source_id, s.name, d.title, d.url, d.document_type, d.status, d.updated_at,
                   (select count(*) from travel_guide_chunks c where c.document_id = d.id) as chunk_count
            from travel_guide_documents d
            join travel_guide_sources s on s.id = d.source_id
            order by d.updated_at desc
            limit @limit;
            """;
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 300));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TravelGuideDocumentDto
            {
                Id = reader.GetString(0),
                SourceId = reader.GetString(1),
                SourceName = reader.GetString(2),
                Title = reader.GetString(3),
                Url = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                DocumentType = reader.GetString(5),
                Status = reader.GetString(6),
                UpdatedAt = reader.GetDateTime(7),
                ChunkCount = Convert.ToInt32(reader.GetInt64(8))
            });
        }
        return rows;
    }

    public async Task<TravelGuideIngestResult> UpsertSourceAsync(TravelGuideSourceInput input, string? userId, CancellationToken cancellationToken = default)
    {
        if (input is null) throw new InvalidOperationException("Thiếu dữ liệu nguồn.");
        var name = CleanSingleLine(input.Name);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Thiếu tên nguồn.");
        var id = Slug(input.Id ?? name);
        var reliability = Math.Clamp(input.Reliability <= 0 ? DefaultReliability(input.SourceType) : input.Reliability, 1, 100);
        var parentSourceId = string.IsNullOrWhiteSpace(input.ParentSourceId) ? string.Empty : Slug(input.ParentSourceId);
        var sourceType = CleanSingleLine(input.SourceType, "custom");
        var url = CleanSingleLine(input.Url);
        var publisher = CleanSingleLine(input.Publisher);
        var accessLevel = CleanSingleLine(input.AccessLevel, "public");
        var license = CleanSingleLine(input.License);
        var status = CleanSingleLine(input.Status, "active");

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            insert into travel_guide_sources(id, parent_source_id, name, source_type, url, publisher, reliability, access_level, license, status, created_by, updated_at)
            values (@id, @parent_source_id, @name, @source_type, @url, @publisher, @reliability, @access_level, @license, @status, @created_by, now())
            on conflict (id) do update
            set parent_source_id = excluded.parent_source_id,
                name = excluded.name,
                source_type = excluded.source_type,
                url = excluded.url,
                publisher = excluded.publisher,
                reliability = excluded.reliability,
                access_level = excluded.access_level,
                license = excluded.license,
                status = excluded.status,
                updated_at = now();
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("parent_source_id", string.IsNullOrWhiteSpace(parentSourceId) ? (object)DBNull.Value : parentSourceId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("source_type", sourceType);
        cmd.Parameters.AddWithValue("url", string.IsNullOrWhiteSpace(url) ? (object)DBNull.Value : url);
        cmd.Parameters.AddWithValue("publisher", string.IsNullOrWhiteSpace(publisher) ? (object)DBNull.Value : publisher);
        cmd.Parameters.AddWithValue("reliability", reliability);
        cmd.Parameters.AddWithValue("access_level", accessLevel);
        cmd.Parameters.AddWithValue("license", string.IsNullOrWhiteSpace(license) ? (object)DBNull.Value : license);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("created_by", string.IsNullOrWhiteSpace(userId) ? (object)DBNull.Value : userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new TravelGuideIngestResult { Id = id, Chunks = 0 };
    }

    public async Task<TravelGuideIngestResult> IngestDocumentAsync(TravelGuideDocumentInput input, string? userId, CancellationToken cancellationToken = default)
    {
        if (input is null) throw new InvalidOperationException("Thiếu dữ liệu tài liệu.");
        var sourceId = string.IsNullOrWhiteSpace(input.SourceId) ? string.Empty : Slug(input.SourceId);
        if (string.IsNullOrWhiteSpace(sourceId)) throw new InvalidOperationException("Thiếu mã nguồn.");
        var title = CleanSingleLine(input.Title);
        var rawText = CleanText(input.Content);
        if (string.IsNullOrWhiteSpace(title)) title = CleanSingleLine(input.Url, "Tài liệu RAG");
        if (string.IsNullOrWhiteSpace(rawText)) throw new InvalidOperationException("Thiếu nội dung tài liệu.");
        if (rawText.Length > MaxDocumentChars) rawText = rawText[..MaxDocumentChars];

        var url = CleanSingleLine(input.Url);
        var documentType = CleanSingleLine(input.DocumentType, "text");
        var metadata = input.Metadata.ValueKind is JsonValueKind.Undefined ? "{}" : input.Metadata.GetRawText();
        var hash = Sha256(sourceId + "\n" + url + "\n" + title + "\n" + rawText);
        var documentId = string.IsNullOrWhiteSpace(input.Id) ? Slug((!string.IsNullOrWhiteSpace(url) ? url : title) + "-" + hash[..10]) : Slug(input.Id);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        await using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "select 1 from travel_guide_sources where id = @source_id limit 1;";
            check.Parameters.AddWithValue("source_id", sourceId);
            var exists = await check.ExecuteScalarAsync(cancellationToken);
            if (exists is null) throw new InvalidOperationException("Nguồn RAG chưa tồn tại.");
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                insert into travel_guide_documents(id, source_id, title, url, document_type, content_hash, raw_text, metadata, status, created_by, updated_at)
                values (@id, @source_id, @title, @url, @document_type, @content_hash, @raw_text, @metadata::jsonb, 'active', @created_by, now())
                on conflict (id) do update
                set source_id = excluded.source_id,
                    title = excluded.title,
                    url = excluded.url,
                    document_type = excluded.document_type,
                    content_hash = excluded.content_hash,
                    raw_text = excluded.raw_text,
                    metadata = excluded.metadata,
                    status = 'active',
                    updated_at = now();
                """;
            cmd.Parameters.AddWithValue("id", documentId);
            cmd.Parameters.AddWithValue("source_id", sourceId);
            cmd.Parameters.AddWithValue("title", title);
            cmd.Parameters.AddWithValue("url", string.IsNullOrWhiteSpace(url) ? (object)DBNull.Value : url);
            cmd.Parameters.AddWithValue("document_type", documentType);
            cmd.Parameters.AddWithValue("content_hash", hash);
            cmd.Parameters.AddWithValue("raw_text", rawText);
            cmd.Parameters.AddWithValue("metadata", metadata);
            cmd.Parameters.AddWithValue("created_by", string.IsNullOrWhiteSpace(userId) ? (object)DBNull.Value : userId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "delete from travel_guide_chunks where document_id = @document_id;";
            delete.Parameters.AddWithValue("document_id", documentId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var chunks = BuildChunks(title, rawText, input.Keywords, url);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                insert into travel_guide_chunks(id, document_id, source_id, title, content, keywords, search_text, url, chunk_index, embedding, embedding_model, embedding_updated_at, status, updated_at)
                values (@id, @document_id, @source_id, @title, @content, @keywords, @search_text, @url, @chunk_index, @embedding::jsonb, @embedding_model, now(), 'active', now());
                """;
            cmd.Parameters.AddWithValue("id", documentId + "-chunk-" + (i + 1).ToString("000"));
            cmd.Parameters.AddWithValue("document_id", documentId);
            cmd.Parameters.AddWithValue("source_id", sourceId);
            cmd.Parameters.AddWithValue("title", chunk.Title);
            cmd.Parameters.AddWithValue("content", chunk.Content);
            cmd.Parameters.AddWithValue("keywords", chunk.Keywords);
            cmd.Parameters.AddWithValue("search_text", chunk.SearchText);
            cmd.Parameters.AddWithValue("url", string.IsNullOrWhiteSpace(url) ? (object)DBNull.Value : url);
            cmd.Parameters.AddWithValue("chunk_index", i);
            cmd.Parameters.AddWithValue("embedding", JsonSerializer.Serialize(CreateLocalEmbedding(chunk.SearchText, _embeddingDimensions)));
            cmd.Parameters.AddWithValue("embedding_model", LocalEmbeddingModel + ":" + _embeddingDimensions);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new TravelGuideIngestResult { Id = documentId, Chunks = chunks.Count };
    }

    public async Task<TravelGuideEmbeddingRebuildResult> RebuildEmbeddingsAsync(bool force = false, int limit = 500, CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 5000);
        var rows = new List<(string Id, string Text)>();
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var select = conn.CreateCommand())
        {
            select.CommandText = force
                ? """
                    select id, search_text
                    from travel_guide_chunks
                    where status = 'active'
                    order by updated_at desc
                    limit @take;
                    """
                : """
                    select id, search_text
                    from travel_guide_chunks
                    where status = 'active'
                      and (embedding is null or embedding_model is null or embedding_model <> @model)
                    order by updated_at desc
                    limit @take;
                    """;
            select.Parameters.AddWithValue("take", take);
            if (!force) select.Parameters.AddWithValue("model", LocalEmbeddingModel + ":" + _embeddingDimensions);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }
        }

        var updated = 0;
        foreach (var row in rows)
        {
            var embedding = CreateLocalEmbedding(row.Text, _embeddingDimensions);
            await using var update = conn.CreateCommand();
            update.CommandText = """
                update travel_guide_chunks
                set embedding = @embedding::jsonb,
                    embedding_model = @embedding_model,
                    embedding_updated_at = now(),
                    updated_at = now()
                where id = @id;
                """;
            update.Parameters.AddWithValue("id", row.Id);
            update.Parameters.AddWithValue("embedding", JsonSerializer.Serialize(embedding));
            update.Parameters.AddWithValue("embedding_model", LocalEmbeddingModel + ":" + _embeddingDimensions);
            updated += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return new TravelGuideEmbeddingRebuildResult
        {
            Scanned = rows.Count,
            Updated = updated,
            EmbeddingModel = LocalEmbeddingModel + ":" + _embeddingDimensions
        };
    }

    public async Task<TravelGuideIngestResult> CrawlUrlAsync(TravelGuideCrawlInput input, string? userId, CancellationToken cancellationToken = default)
    {
        if (input is null) throw new InvalidOperationException("Thiếu dữ liệu crawl.");
        var url = CleanSingleLine(input.Url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("URL không hợp lệ.");
        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", "TravelwAI-RAG/1.0 (+https://travelwai.onrender.com)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(contentType)
            && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            && !contentType.Contains("text", StringComparison.OrdinalIgnoreCase)
            && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("URL crawl phải là trang HTML/text công khai.");
        }

        if (response.Content.Headers.ContentLength is > MaxCrawlBytes)
        {
            throw new InvalidOperationException("Nội dung crawl quá lớn. Hãy nạp tài liệu thủ công hoặc chia nhỏ nguồn.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new MemoryStream();
        var buffer = new byte[32 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0) break;
            total += read;
            if (total > MaxCrawlBytes) throw new InvalidOperationException("Nội dung crawl quá lớn. Hãy nạp tài liệu thủ công hoặc chia nhỏ nguồn.");
            limited.Write(buffer, 0, read);
        }

        var html = Encoding.UTF8.GetString(limited.ToArray());
        var text = ExtractReadableText(html);
        if (string.IsNullOrWhiteSpace(text) || text.Length < 80) throw new InvalidOperationException("Không trích xuất được nội dung đủ dài từ URL này.");
        var title = string.IsNullOrWhiteSpace(input.Title) ? ExtractHtmlTitle(html) : input.Title;
        if (string.IsNullOrWhiteSpace(title)) title = uri.Host + uri.AbsolutePath;
        return await IngestDocumentAsync(new TravelGuideDocumentInput
        {
            SourceId = input.SourceId,
            Title = title,
            Url = url,
            Content = text,
            DocumentType = "web",
            Keywords = input.Keywords,
            Metadata = input.Metadata
        }, userId, cancellationToken);
    }

    private static double ScoreDbChunk(string normalizedQuery, List<string> tokens, TravelGuideRagSearchResult item)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery)) return item.Reliability;
        var haystack = item.SearchText;
        var score = (double)item.Reliability;
        foreach (var token in tokens)
        {
            if (haystack.Contains(token, StringComparison.Ordinal)) score += 16;
        }
        foreach (var phrase in new[] { "di tich", "lang nghe", "nghe nhan", "unesco", "le hoi", "lich trinh", "gia ve", "gio mo cua", "xep hang", "quyet dinh", "truyen thuyet", "tam linh", "kien truc", "bao tang", "cuc di san" })
        {
            if (normalizedQuery.Contains(phrase, StringComparison.Ordinal) && haystack.Contains(phrase, StringComparison.Ordinal)) score += 28;
        }
        if (!string.IsNullOrWhiteSpace(item.Url)) score += 3;
        return score;
    }

    private static List<TravelGuideChunkDraft> BuildChunks(string title, string text, string? keywords, string? url)
    {
        var paragraphs = Regex.Split(text, @"\n{2,}")
            .Select(CleanText)
            .Where(p => p.Length >= 40)
            .ToList();
        var chunks = new List<TravelGuideChunkDraft>();
        var buffer = new StringBuilder();
        var index = 1;
        foreach (var paragraph in paragraphs)
        {
            if (buffer.Length > 0 && buffer.Length + paragraph.Length > 1800)
            {
                AddChunk(chunks, title, buffer.ToString(), keywords, url, index++);
                buffer.Clear();
            }
            if (buffer.Length > 0) buffer.AppendLine().AppendLine();
            buffer.Append(paragraph);
        }
        if (buffer.Length > 0) AddChunk(chunks, title, buffer.ToString(), keywords, url, index);
        if (chunks.Count == 0) AddChunk(chunks, title, text.Length > 2200 ? text[..2200] : text, keywords, url, 1);
        return chunks.Take(120).ToList();
    }

    private static void AddChunk(List<TravelGuideChunkDraft> chunks, string title, string content, string? keywords, string? url, int index)
    {
        var cleanContent = CleanText(content);
        if (string.IsNullOrWhiteSpace(cleanContent)) return;
        var chunkTitle = chunks.Count == 0 ? title : title + " · phần " + index;
        var keywordText = CleanSingleLine(keywords);
        chunks.Add(new TravelGuideChunkDraft(chunkTitle, cleanContent, keywordText, NormalizeForSearch(chunkTitle + " " + cleanContent + " " + keywordText + " " + url)));
    }

    private static string ExtractReadableText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(br|p|div|li|tr|h[1-6])\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t\r\f\v]+", " ");
        text = Regex.Replace(text, @"\n\s+", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return CleanText(text);
    }

    private static string ExtractHtmlTitle(string html)
    {
        var match = Regex.Match(html ?? string.Empty, @"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase);
        return match.Success ? CleanSingleLine(WebUtility.HtmlDecode(Regex.Replace(match.Groups[1].Value, @"<[^>]+>", " "))) : string.Empty;
    }

    private static string BuildUiContextBlock(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return string.Empty;
        var clean = CleanText(context);
        if (clean.Length > 3500) clean = clean[..3500];
        return "Ngữ cảnh giao diện TravelwAI:\n" + clean;
    }

    private sealed record FallbackChunk(string Title, string Source, int Reliability, string Content, string Keywords);
    private sealed record TravelGuideChunkDraft(string Title, string Content, string Keywords, string SearchText);

    private static IReadOnlyList<FallbackChunk> GetFallbackChunks() => new[]
    {
        new FallbackChunk("Di tích và xếp hạng", "Cục Di sản Văn hoá, Bộ Văn hoá Thể thao và Du lịch, quyết định xếp hạng", 98, "Dùng để xác minh tên di tích, loại hình, niên đại trong hồ sơ, giá trị lịch sử văn hoá, xếp hạng di tích cấp tỉnh, quốc gia, quốc gia đặc biệt. Khi hỏi xếp hạng hoặc quyết định, chỉ khẳng định khi có nguồn chính thức.", "di tich xep hang quoc gia dac biet quyet dinh lich su kien truc gia tri van hoa tam linh"),
        new FallbackChunk("Di sản UNESCO", "UNESCO World Heritage, UNESCO Intangible Cultural Heritage", 98, "Dùng để xác minh di sản thế giới, di sản văn hoá phi vật thể được ghi danh, năm ghi danh, phạm vi thực hành và giá trị nổi bật. Không tự nhận một địa danh là UNESCO nếu chưa có nguồn UNESCO.", "unesco di san the gioi phi vat the ghi danh van hoa"),
        new FallbackChunk("Văn bản pháp luật", "Văn bản Chính phủ, Cơ sở dữ liệu quốc gia về pháp luật", 96, "Dùng cho Luật Di sản văn hoá, nghị định về làng nghề, danh hiệu Nghệ nhân nhân dân, Nghệ nhân ưu tú, tiêu chí công nhận nghề truyền thống và làng nghề truyền thống.", "luat di san van hoa nghi dinh lang nghe nghe nhan nhan dan uu tu tieu chi cong nhan"),
        new FallbackChunk("Nguồn địa phương", "UBND tỉnh/thành, Sở VHTTDL, Sở Du lịch, Sở NN&PTNT, Sở Công Thương", 92, "Dùng để xác minh quyết định công nhận làng nghề, di tích cấp tỉnh, lễ hội địa phương, điểm tham quan, thông báo quản lý và dữ liệu du lịch theo địa phương.", "ubnd so du lich so van hoa so cong thuong so nong nghiep lang nghe le hoi dia phuong"),
        new FallbackChunk("Bảo tàng và ban quản lý", "Bảo tàng quốc gia, bảo tàng địa phương, ban quản lý di tích", 88, "Dùng cho thuyết minh chính thức, hiện vật, câu chuyện trưng bày, sơ đồ tham quan, quy định tại điểm, bối cảnh lịch sử và giá trị kiến trúc.", "bao tang ban quan ly di tich hien vat thuyet minh kien truc tham quan"),
        new FallbackChunk("Sách và nghiên cứu", "Địa chí, sách lịch sử địa phương, luận văn, bài nghiên cứu, viện nghiên cứu", 82, "Dùng để mở rộng bối cảnh học thuật, so sánh các giai đoạn lịch sử, phân tích kiến trúc, nguồn gốc làng nghề, biến đổi nghề và giá trị văn hoá cộng đồng.", "dia chi nghien cuu hoc thuat lich su dia phuong kien truc lang nghe so sanh"),
        new FallbackChunk("Tư liệu thực địa", "Phỏng vấn nghệ nhân, người cao tuổi, ghi âm, video, ảnh hiện trường, bảng thuyết minh", 76, "Dùng cho storytelling, triết lý làm nghề, ký ức cộng đồng, quy trình sản xuất, nguyên liệu, công cụ, giai thoại. Phải phân biệt lời kể với sự kiện đã kiểm chứng.", "phong van nghe nhan cau chuyen ke chuyen quy trinh san xuat nguyen lieu cong cu truyen thuyet"),
        new FallbackChunk("Báo chí chính thống", "TTXVN, Nhân Dân, VOV, VTV, báo địa phương và tạp chí văn hoá du lịch", 70, "Dùng để cập nhật sự kiện, lễ hội, hoạt động bảo tồn, phỏng vấn, điểm mới trong du lịch. Không dùng làm căn cứ pháp lý cao nhất nếu có quyết định hoặc hồ sơ chính thức.", "bao chi su kien moi le hoi bao ton phong van du lich"),
        new FallbackChunk("Thông tin tham quan", "Cổng du lịch quốc gia, cổng du lịch địa phương, website hoặc Facebook chính thức của bảo tàng/ban quản lý", 68, "Dùng cho giờ mở cửa, giá vé, sự kiện theo mùa, tuyến tham quan, lưu ý khi đi. Đây là dữ liệu dễ thay đổi nên cần nói rõ nếu chưa có cập nhật mới.", "gio mo cua gia ve su kien lich trinh tuyen tham quan gan do"),
        new FallbackChunk("Nguồn phụ", "Wikipedia, Wikivoyage, blog, mạng xã hội, đánh giá người dùng", 42, "Chỉ dùng để định hướng ban đầu, tham khảo trải nghiệm và phát hiện chủ đề. Không dùng để khẳng định niên đại, danh hiệu, xếp hạng, quyết định công nhận hoặc sự kiện pháp lý.", "wikipedia wikivoyage blog mang xa hoi tham khao trai nghiem")
    };

    private static int ScoreFallbackChunk(string message, string uiContext, FallbackChunk chunk)
    {
        var query = NormalizeForSearch(message + " " + uiContext);
        if (string.IsNullOrWhiteSpace(query)) return chunk.Reliability / 20;
        var haystack = NormalizeForSearch(chunk.Title + " " + chunk.Source + " " + chunk.Content + " " + chunk.Keywords);
        var score = chunk.Reliability / 10;
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length >= 3).Distinct(StringComparer.Ordinal))
        {
            if (haystack.Contains(token, StringComparison.Ordinal)) score += 8;
        }
        foreach (var phrase in new[] { "di tich", "lang nghe", "nghe nhan", "unesco", "le hoi", "lich trinh", "gia ve", "gio mo cua", "xep hang", "quyet dinh", "truyen thuyet", "tam linh", "kien truc" })
        {
            if (query.Contains(phrase, StringComparison.Ordinal) && haystack.Contains(phrase, StringComparison.Ordinal)) score += 22;
        }
        return score;
    }

    private static float[] CreateLocalEmbedding(string? text, int dimensions)
    {
        var vector = new float[dimensions];
        var normalized = NormalizeForSearch(text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return vector;

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 2)
            .ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            AddTokenToVector(vector, tokens[i], 1.0f);
            if (i + 1 < tokens.Count) AddTokenToVector(vector, tokens[i] + " " + tokens[i + 1], 0.65f);
            if (i + 2 < tokens.Count) AddTokenToVector(vector, tokens[i] + " " + tokens[i + 1] + " " + tokens[i + 2], 0.35f);
        }

        NormalizeVector(vector);
        return vector;
    }

    private static void AddTokenToVector(float[] vector, string token, float weight)
    {
        if (vector.Length == 0 || string.IsNullOrWhiteSpace(token)) return;
        var hash = StableHash(token);
        var index = (int)(hash % (uint)vector.Length);
        var sign = (hash & 0x80000000) == 0 ? 1f : -1f;
        vector[index] += sign * weight;
    }

    private static uint StableHash(string value)
    {
        unchecked
        {
            const uint fnvOffset = 2166136261;
            const uint fnvPrime = 16777619;
            var hash = fnvOffset;
            foreach (var ch in value)
            {
                hash ^= (uint)ch;
                hash *= fnvPrime;
            }
            return hash;
        }
    }

    private static void NormalizeVector(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector) sum += value * value;
        if (sum <= 0) return;
        var norm = Math.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
    }

    private static float[]? ParseEmbedding(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<float[]>(json);
        }
        catch
        {
            return null;
        }
    }

    private static double CosineSimilarity(float[]? left, float[]? right)
    {
        if (left is null || right is null || left.Length == 0 || right.Length == 0) return 0;
        var length = Math.Min(left.Length, right.Length);
        double dot = 0;
        for (var i = 0; i < length; i++) dot += left[i] * right[i];
        return Math.Clamp(dot, -1.0, 1.0);
    }

    private static double FreshnessBoost(DateTime updatedAt)
    {
        var days = Math.Max(0, (DateTime.UtcNow - updatedAt.ToUniversalTime()).TotalDays);
        if (days <= 7) return 8;
        if (days <= 30) return 5;
        if (days <= 180) return 2;
        return 0;
    }

    private static int ReadInt(IConfiguration configuration, string key, string envKey, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(value)) value = configuration[key];
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Replace("\u0000", " ").Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"[ \t\f\v]+", " ");
        text = Regex.Replace(text, @" *\n *", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string CleanSingleLine(string? value, string fallback = "")
    {
        var text = CleanText(value).Replace('\n', ' ').Trim();
        text = Regex.Replace(text, @"\s+", " ");
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static int DefaultReliability(string? sourceType)
    {
        var type = NormalizeForSearch(sourceType ?? string.Empty);
        if (type.Contains("legal") || type.Contains("phap luat") || type.Contains("quyet dinh")) return 96;
        if (type.Contains("unesco") || type.Contains("cuc di san") || type.Contains("bo vhttdl")) return 95;
        if (type.Contains("ubnd") || type.Contains("so ")) return 90;
        if (type.Contains("bao tang") || type.Contains("ban quan ly")) return 85;
        if (type.Contains("nghien cuu") || type.Contains("dia chi")) return 80;
        if (type.Contains("bao chi")) return 70;
        if (type.Contains("thuc dia") || type.Contains("phong van")) return 76;
        if (type.Contains("wikipedia")) return 42;
        if (type.Contains("blog") || type.Contains("mang xa hoi")) return 38;
        return 60;
    }

    private static string Slug(string? value)
    {
        var text = NormalizeForSearch(value ?? string.Empty);
        text = Regex.Replace(text, @"[^a-z0-9]+", "-").Trim('-');
        if (text.Length > 96) text = text[..96].Trim('-');
        return string.IsNullOrWhiteSpace(text) ? Guid.NewGuid().ToString("N") : text;
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string NormalizeForSearch(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            builder.Append(ch == 'đ' || ch == 'Đ' ? 'd' : char.ToLowerInvariant(ch));
        }
        var text = builder.ToString().Normalize(NormalizationForm.FormC);
        text = Regex.Replace(text, @"[^a-z0-9]+", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}

public sealed class TravelGuideRagSearchResult
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Keywords { get; set; } = string.Empty;
    public string SearchText { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int Reliability { get; set; }
    public double Score { get; set; }
    public double KeywordScore { get; set; }
    public double VectorScore { get; set; }
    public string EmbeddingModel { get; set; } = string.Empty;
    [JsonIgnore]
    public float[]? Embedding { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TravelGuideSourceDto
{
    public string Id { get; set; } = string.Empty;
    public string ParentSourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public int Reliability { get; set; }
    public string AccessLevel { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

public sealed class TravelGuideDocumentDto
{
    public string Id { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int ChunkCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TravelGuideSourceInput
{
    public string? Id { get; set; }
    public string? ParentSourceId { get; set; }
    public string? Name { get; set; }
    public string? SourceType { get; set; }
    public string? Url { get; set; }
    public string? Publisher { get; set; }
    public int Reliability { get; set; }
    public string? AccessLevel { get; set; }
    public string? License { get; set; }
    public string? Status { get; set; }
}

public sealed class TravelGuideDocumentInput
{
    public string? Id { get; set; }
    public string? SourceId { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public string? Content { get; set; }
    public string? DocumentType { get; set; }
    public string? Keywords { get; set; }
    public JsonElement Metadata { get; set; }
}

public sealed class TravelGuideCrawlInput
{
    public string? SourceId { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }
    public string? Keywords { get; set; }
    public JsonElement Metadata { get; set; }
}

public sealed class TravelGuideIngestResult
{
    public string Id { get; set; } = string.Empty;
    public int Chunks { get; set; }
}

public sealed class TravelGuideEmbeddingRebuildResult
{
    public int Scanned { get; set; }
    public int Updated { get; set; }
    public string EmbeddingModel { get; set; } = string.Empty;
}
