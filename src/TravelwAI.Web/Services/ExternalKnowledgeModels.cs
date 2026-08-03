namespace TravelwAI.Web.Services;

public sealed record ExternalKnowledgeDocument(
    string Id,
    string Source,
    string Title,
    string Text,
    string Tags,
    string SourceUrl,
    string Attribution,
    string License);

public sealed record ExternalKnowledgeStatus(
    string State,
    int DocumentCount,
    DateTimeOffset? LastImportedAt,
    string? LastError,
    IReadOnlyDictionary<string, int> SourceCounts);

internal sealed record ExternalKnowledgeSnapshot(
    ExternalKnowledgeDocument[] Documents,
    IReadOnlyDictionary<string, int[]> InvertedIndex);
