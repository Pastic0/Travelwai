namespace TravelwAI.Data.Interfaces;

public interface IDataRepository
{
    Task<string?> AddAsync(string collection, Dictionary<string, object?> data);
    Task<bool> SetAsync(string collection, string documentId, Dictionary<string, object?> data, bool merge = true);
    Task<bool> UpdateAsync(string collection, string documentId, Dictionary<string, object?> data);
    Task<bool> DeleteAsync(string collection, string documentId);
    Task<int> DeleteWhereEqualAsync(string collection, string field, object value);
    Task<Dictionary<string, object?>?> GetByIdAsync(string collection, string documentId, bool includeId = true);
    Task<List<Dictionary<string, object?>>> GetAllAsync(string collection, int? limit = null, bool includeId = true);
    Task<List<Dictionary<string, object?>>> GetAllFieldsAsync(string collection, IReadOnlyCollection<string> fields, int? limit = null, bool includeId = true);
    Task<List<Dictionary<string, object?>>> WhereEqualAsync(string collection, string field, object value, int? limit = null, bool includeId = true);
    Task<List<Dictionary<string, object?>>> WhereEqualPagedAsync(string collection, string field, object value, string orderField, bool descending, int limit, int offset, bool includeId = true);
    Task<List<Dictionary<string, object?>>> WhereArrayContainsAsync(string collection, string field, object value, int? limit = null, bool includeId = true);
}
