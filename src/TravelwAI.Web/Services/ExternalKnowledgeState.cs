namespace TravelwAI.Web.Services;

public sealed class ExternalKnowledgeState
{
    private readonly object _sync = new();
    private ExternalKnowledgeSnapshot _snapshot = new(
        Array.Empty<ExternalKnowledgeDocument>(),
        new Dictionary<string, int[]>(StringComparer.Ordinal));
    private string _state = "not_loaded";
    private DateTimeOffset? _lastImportedAt;
    private string? _lastError;
    private IReadOnlyDictionary<string, int> _sourceCounts = new Dictionary<string, int>();

    internal ExternalKnowledgeSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public ExternalKnowledgeStatus GetStatus()
    {
        lock (_sync)
        {
            return new ExternalKnowledgeStatus(
                _state,
                _snapshot.Documents.Length,
                _lastImportedAt,
                _lastError,
                _sourceCounts.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase));
        }
    }

    public void MarkLoading()
    {
        lock (_sync)
        {
            _state = "loading";
            _lastError = null;
        }
    }

    public void Replace(
        IEnumerable<ExternalKnowledgeDocument> documents,
        DateTimeOffset importedAt,
        IReadOnlyDictionary<string, int> sourceCounts)
    {
        var documentArray = documents.ToArray();
        var indexBuilder = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < documentArray.Length; index++)
        {
            var document = documentArray[index];
            var searchable = string.Join(" ", document.Title, document.Tags, document.Text);
            foreach (var term in ExternalKnowledgeText.Terms(searchable, 256))
            {
                if (!indexBuilder.TryGetValue(term, out var positions))
                {
                    positions = new List<int>();
                    indexBuilder[term] = positions;
                }
                positions.Add(index);
            }
        }

        var invertedIndex = indexBuilder.ToDictionary(
            item => item.Key,
            item => item.Value.ToArray(),
            StringComparer.Ordinal);
        Volatile.Write(ref _snapshot, new ExternalKnowledgeSnapshot(documentArray, invertedIndex));

        lock (_sync)
        {
            _state = "ready";
            _lastImportedAt = importedAt;
            _lastError = null;
            _sourceCounts = sourceCounts.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public void MarkFailed(Exception exception)
    {
        lock (_sync)
        {
            _state = _snapshot.Documents.Length > 0 ? "ready_with_error" : "failed";
            _lastError = exception.Message;
        }
    }
}
