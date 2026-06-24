using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using TravelwAI.Data.Interfaces;

namespace TravelwAI.Data.Repositories;

public sealed class SupabaseDocumentRepository : IDataRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IMemoryCache _cache;
    private static readonly ConcurrentDictionary<string, byte> CacheKeys = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        WriteIndented = false
    };

    public SupabaseDocumentRepository(NpgsqlDataSource dataSource, IMemoryCache cache)
    {
        _dataSource = dataSource;
        _cache = cache;
    }

    public async Task<string?> AddAsync(string collection, Dictionary<string, object?> data)
    {
        var id = Guid.NewGuid().ToString("N");
        var clean = RemoveNullValues(CloneDictionary(data));
        clean["id"] = id;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            insert into app_documents(collection, id, data, created_at, updated_at)
            values (@collection, @id, @data::jsonb, now(), now())
            on conflict (collection, id) do update
            set data = excluded.data,
                updated_at = now();
            """;
        cmd.Parameters.AddWithValue("collection", collection);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("data", ToJson(clean));
        await cmd.ExecuteNonQueryAsync();

        InvalidateCollectionCache(collection);
        return id;
    }

    public async Task<bool> SetAsync(string collection, string documentId, Dictionary<string, object?> data, bool merge = true)
    {
        var clean = RemoveNullValues(CloneDictionary(data));
        clean["id"] = documentId;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = merge
            ? """
              insert into app_documents(collection, id, data, created_at, updated_at)
              values (@collection, @id, @data::jsonb, now(), now())
              on conflict (collection, id) do update
              set data = app_documents.data || excluded.data,
                  updated_at = now();
              """
            : """
              insert into app_documents(collection, id, data, created_at, updated_at)
              values (@collection, @id, @data::jsonb, now(), now())
              on conflict (collection, id) do update
              set data = excluded.data,
                  updated_at = now();
              """;
        cmd.Parameters.AddWithValue("collection", collection);
        cmd.Parameters.AddWithValue("id", documentId);
        cmd.Parameters.AddWithValue("data", ToJson(clean));
        await cmd.ExecuteNonQueryAsync();

        InvalidateCollectionCache(collection);
        return true;
    }

    public async Task<bool> UpdateAsync(string collection, string documentId, Dictionary<string, object?> data)
    {
        var clean = RemoveNullValues(CloneDictionary(data));
        if (clean.Count == 0) return true;
        clean["id"] = documentId;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            update app_documents
            set data = data || @data::jsonb,
                updated_at = now()
            where collection = @collection and id = @id;
            """;
        cmd.Parameters.AddWithValue("collection", collection);
        cmd.Parameters.AddWithValue("id", documentId);
        cmd.Parameters.AddWithValue("data", ToJson(clean));
        var affected = await cmd.ExecuteNonQueryAsync();

        if (affected > 0) InvalidateCollectionCache(collection);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(string collection, string documentId)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "delete from app_documents where collection = @collection and id = @id;";
        cmd.Parameters.AddWithValue("collection", collection);
        cmd.Parameters.AddWithValue("id", documentId);
        var affected = await cmd.ExecuteNonQueryAsync();

        if (affected > 0) InvalidateCollectionCache(collection);
        return affected > 0;
    }

    public async Task<int> DeleteWhereEqualAsync(string collection, string field, object value)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            delete from app_documents
            where collection = @collection
              and ((@field = 'id' and id = @value) or (data ->> @field) = @value);
            """;
        cmd.Parameters.AddWithValue("collection", collection);
        cmd.Parameters.AddWithValue("field", field);
        cmd.Parameters.AddWithValue("value", ToSearchText(value));
        var affected = await cmd.ExecuteNonQueryAsync();

        if (affected > 0) InvalidateCollectionCache(collection);
        return affected;
    }

    public async Task<Dictionary<string, object?>?> GetByIdAsync(string collection, string documentId, bool includeId = true)
    {
        var key = BuildCacheKey(collection, "doc", documentId, includeId);
        if (_cache.TryGetValue(key, out Dictionary<string, object?>? cachedDoc))
        {
            return CloneDictionary(cachedDoc);
        }

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select id, data::text from app_documents where collection = @collection and id = @id limit 1;";
        cmd.Parameters.AddWithValue("collection", collection);
        cmd.Parameters.AddWithValue("id", documentId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var row = FromJson(reader.GetString(1));
        if (includeId) row["id"] = reader.GetString(0);

        TrackAndSet(key, collection, CloneDictionary(row));
        return row;
    }

    public async Task<List<Dictionary<string, object?>>> GetAllAsync(string collection, int? limit = null, bool includeId = true)
    {
        var key = BuildCacheKey(collection, "all", limit, includeId);
        if (_cache.TryGetValue(key, out List<Dictionary<string, object?>>? cachedRows))
        {
            return CloneList(cachedRows);
        }

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = limit.HasValue
            ? "select id, data::text from app_documents where collection = @collection order by updated_at desc limit @limit;"
            : "select id, data::text from app_documents where collection = @collection order by updated_at desc;";
        cmd.Parameters.AddWithValue("collection", collection);
        if (limit.HasValue) cmd.Parameters.AddWithValue("limit", limit.Value);

        var rows = await ReadRowsAsync(cmd, includeId);
        TrackAndSet(key, collection, CloneList(rows));
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>> GetAllFieldsAsync(string collection, IReadOnlyCollection<string> fields, int? limit = null, bool includeId = true)
    {
        var cleanFields = fields
            .Where(IsSafeJsonFieldName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (cleanFields.Count == 0) return await GetAllAsync(collection, limit, includeId);

        var key = BuildCacheKey(collection, "all-fields", string.Join(",", cleanFields), limit, includeId);
        if (_cache.TryGetValue(key, out List<Dictionary<string, object?>>? cachedRows))
        {
            return CloneList(cachedRows);
        }

        var jsonProjection = string.Join(", ", cleanFields.Select(field => $"'{field}', data -> '{field}'"));
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = limit.HasValue
            ? $"""
               select id, jsonb_strip_nulls(jsonb_build_object({jsonProjection}))::text
               from app_documents
               where collection = @collection
               order by updated_at desc
               limit @limit;
               """
            : $"""
               select id, jsonb_strip_nulls(jsonb_build_object({jsonProjection}))::text
               from app_documents
               where collection = @collection
               order by updated_at desc;
               """;
        cmd.Parameters.AddWithValue("collection", collection);
        if (limit.HasValue) cmd.Parameters.AddWithValue("limit", limit.Value);

        var rows = await ReadRowsAsync(cmd, includeId);
        TrackAndSet(key, collection, CloneList(rows));
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>> WhereEqualAsync(string collection, string field, object value, int? limit = null, bool includeId = true)
    {
        var key = BuildCacheKey(collection, "where-eq", field, value, limit, includeId);
        if (_cache.TryGetValue(key, out List<Dictionary<string, object?>>? cachedRows))
        {
            return CloneList(cachedRows);
        }

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = limit.HasValue
            ? """
              select id, data::text
              from app_documents
              where collection = @collection
                and ((@field = 'id' and id = @value) or (data ->> @field) = @value)
              order by updated_at desc
              limit @limit;
              """
            : """
              select id, data::text
              from app_documents
              where collection = @collection
                and ((@field = 'id' and id = @value) or (data ->> @field) = @value)
              order by updated_at desc;
              """;
        cmd.Parameters.AddWithValue("collection", collection);
        cmd.Parameters.AddWithValue("field", field);
        cmd.Parameters.AddWithValue("value", ToSearchText(value));
        if (limit.HasValue) cmd.Parameters.AddWithValue("limit", limit.Value);

        var rows = await ReadRowsAsync(cmd, includeId);
        TrackAndSet(key, collection, CloneList(rows));
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>> WhereEqualPagedAsync(
        string collection,
        string field,
        object value,
        string orderField,
        bool descending,
        int limit,
        int offset,
        bool includeId = true)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);
        var safeOffset = Math.Max(0, offset);
        var key = BuildCacheKey(collection, "where-eq-paged", field, value, orderField, descending, safeLimit, safeOffset, includeId);
        if (_cache.TryGetValue(key, out List<Dictionary<string, object?>>? cachedRows))
        {
            return CloneList(cachedRows);
        }

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = descending
            ? """
              select id, data::text
              from app_documents
              where collection = @collection
                and ((@field = 'id' and id = @value) or (data ->> @field) = @value)
              order by coalesce(data ->> @orderField, '') desc, updated_at desc
              limit @limit offset @offset;
              """
            : """
              select id, data::text
              from app_documents
              where collection = @collection
                and ((@field = 'id' and id = @value) or (data ->> @field) = @value)
              order by coalesce(data ->> @orderField, '') asc, updated_at asc
              limit @limit offset @offset;
              """;
        cmd.Parameters.AddWithValue("collection", collection);
        cmd.Parameters.AddWithValue("field", field);
        cmd.Parameters.AddWithValue("value", ToSearchText(value));
        cmd.Parameters.AddWithValue("orderField", orderField);
        cmd.Parameters.AddWithValue("limit", safeLimit);
        cmd.Parameters.AddWithValue("offset", safeOffset);

        var rows = await ReadRowsAsync(cmd, includeId);
        TrackAndSet(key, collection, CloneList(rows));
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>> WhereArrayContainsAsync(string collection, string field, object value, int? limit = null, bool includeId = true)
    {
        var key = BuildCacheKey(collection, "where-array", field, value, limit, includeId);
        if (_cache.TryGetValue(key, out List<Dictionary<string, object?>>? cachedRows))
        {
            return CloneList(cachedRows);
        }

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = limit.HasValue
            ? """
              select id, data::text
              from app_documents
              where collection = @collection
                and jsonb_typeof(data -> @field) = 'array'
                and (data -> @field) @> @probe::jsonb
              order by updated_at desc
              limit @limit;
              """
            : """
              select id, data::text
              from app_documents
              where collection = @collection
                and jsonb_typeof(data -> @field) = 'array'
                and (data -> @field) @> @probe::jsonb
              order by updated_at desc;
              """;
        cmd.Parameters.AddWithValue("collection", collection);
        cmd.Parameters.AddWithValue("field", field);
        cmd.Parameters.AddWithValue("probe", ToJson(new List<object?> { value }));
        if (limit.HasValue) cmd.Parameters.AddWithValue("limit", limit.Value);

        var rows = await ReadRowsAsync(cmd, includeId);
        TrackAndSet(key, collection, CloneList(rows));
        return rows;
    }

    private static async Task<List<Dictionary<string, object?>>> ReadRowsAsync(NpgsqlCommand cmd, bool includeId)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var data = FromJson(reader.GetString(1));
            if (includeId) data["id"] = id;
            rows.Add(data);
        }
        return rows;
    }

    private void TrackAndSet<T>(string key, string collection, T value)
    {
        CacheKeys[key] = 1;
        _cache.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = GetCacheDuration(collection),
            Size = 1
        });
    }

    private void InvalidateCollectionCache(string collection)
    {
        var prefix = $"sql:{collection}:";
        foreach (var key in CacheKeys.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _cache.Remove(key);
            CacheKeys.TryRemove(key, out _);
        }
    }

    private static TimeSpan GetCacheDuration(string collection)
    {
        return collection switch
        {
            "provinces" or "destinations" => TimeSpan.FromMinutes(30),
            "users" => TimeSpan.FromMinutes(5),
            "messages" or "conversations" or "friends" or "schedules" or "plans" or "plan_statuses" or "memories" or "shared_memories" or "tours" or "tour_orders" => TimeSpan.FromSeconds(15),
            _ => TimeSpan.FromSeconds(30)
        };
    }

    private static string BuildCacheKey(string collection, params object?[] parts)
    {
        return $"sql:{collection}:" + string.Join(":", parts.Select(NormalizeCachePart));
    }

    private static string NormalizeCachePart(object? value)
    {
        if (value is null) return "null";
        return Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static bool IsSafeJsonFieldName(string field)
    {
        if (string.IsNullOrWhiteSpace(field) || field.Length > 80) return false;
        return field.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    private static string ToJson(object? value) => JsonSerializer.Serialize(value, JsonOptions);

    private static Dictionary<string, object?> FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonElementToObject(doc.RootElement) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string ToSearchText(object value)
    {
        return value switch
        {
            DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static Dictionary<string, object?> RemoveNullValues(Dictionary<string, object?> data)
    {
        return data.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static List<Dictionary<string, object?>> CloneList(List<Dictionary<string, object?>>? rows)
    {
        return rows is null
            ? new List<Dictionary<string, object?>>()
            : rows.Select(CloneDictionary).ToList();
    }

    private static Dictionary<string, object?> CloneDictionary(Dictionary<string, object?>? source)
    {
        return source is null
            ? new Dictionary<string, object?>()
            : source.ToDictionary(kvp => kvp.Key, kvp => CloneValue(kvp.Value));
    }

    private static object? CloneValue(object? value)
    {
        if (value is null || value is string || value.GetType().IsValueType) return value;

        if (value is Dictionary<string, object?> typedDictionary)
        {
            return CloneDictionary(typedDictionary);
        }

        if (value is IDictionary dictionary)
        {
            var clone = new Dictionary<string, object?>();
            foreach (DictionaryEntry entry in dictionary)
            {
                clone[Convert.ToString(entry.Key) ?? string.Empty] = CloneValue(entry.Value);
            }
            return clone;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var clone = new List<object?>();
            foreach (var item in enumerable)
            {
                clone.Add(CloneValue(item));
            }
            return clone;
        }

        return value;
    }
}
