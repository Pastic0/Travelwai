using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TravelwAI.Web.Options;

namespace TravelwAI.Web.Services;

public sealed class ExternalDatasetKnowledgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly ExternalKnowledgeState _state;
    private readonly ExternalKnowledgeOptions _options;

    public ExternalDatasetKnowledgeService(
        ExternalKnowledgeState state,
        IOptions<ExternalKnowledgeOptions> options)
    {
        _state = state;
        _options = options.Value;
    }

    public ExternalKnowledgeStatus GetStatus() => _state.GetStatus();

    public Task<string> RetrieveAsync(string? question, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(question))
            return Task.FromResult(string.Empty);

        var normalizedQuestion = ExternalKnowledgeText.Normalize(question);
        var terms = ExternalKnowledgeText.Terms(question, 16);
        if (terms.Count == 0) return Task.FromResult(string.Empty);

        var snapshot = _state.Snapshot;
        if (snapshot.Documents.Length == 0) return Task.FromResult(string.Empty);

        var candidateIndexes = new HashSet<int>();
        foreach (var term in terms)
        {
            if (!snapshot.InvertedIndex.TryGetValue(term, out var positions)) continue;
            foreach (var position in positions) candidateIndexes.Add(position);
        }
        if (candidateIndexes.Count == 0) return Task.FromResult(string.Empty);

        cancellationToken.ThrowIfCancellationRequested();
        var maxDocuments = Math.Clamp(_options.MaxContextDocuments, 1, 20);
        var minimumScore = Math.Max(1, _options.MinimumMatchScore);
        var ranked = new List<(ExternalKnowledgeDocument Document, int Score)>(candidateIndexes.Count);

        foreach (var index in candidateIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index < 0 || index >= snapshot.Documents.Length) continue;
            var document = snapshot.Documents[index];
            var score = Score(document, normalizedQuestion, terms);
            if (score >= minimumScore) ranked.Add((document, score));
        }

        var selected = ranked
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Document.Source, StringComparer.OrdinalIgnoreCase)
            .Take(maxDocuments)
            .ToList();

        if (selected.Count == 0) return Task.FromResult(string.Empty);

        var maxCharacters = Math.Clamp(_options.MaxContextCharacters, 2000, 30000);
        var output = new List<object>();
        var usedCharacters = 0;
        foreach (var item in selected)
        {
            var remaining = maxCharacters - usedCharacters;
            if (remaining < 200) break;

            var text = item.Document.Text;
            if (text.Length > remaining) text = text[..remaining] + "…";
            usedCharacters += text.Length;
            output.Add(new
            {
                source = item.Document.Source,
                title = item.Document.Title,
                content = text,
                tags = item.Document.Tags,
                source_url = item.Document.SourceUrl,
                attribution = item.Document.Attribution,
                license = item.Document.License,
                ai_match_score = item.Score
            });
        }

        return Task.FromResult(JsonSerializer.Serialize(output, JsonOptions));
    }

    private static int Score(
        ExternalKnowledgeDocument document,
        string normalizedQuestion,
        IReadOnlyCollection<string> terms)
    {
        var title = ExternalKnowledgeText.Normalize(document.Title);
        var text = ExternalKnowledgeText.Normalize(document.Text);
        var tags = ExternalKnowledgeText.Normalize(document.Tags);
        var score = 0;

        if (normalizedQuestion.Length >= 5)
        {
            if (title.Contains(normalizedQuestion, StringComparison.Ordinal)) score += 80;
            if (text.Contains(normalizedQuestion, StringComparison.Ordinal)) score += 45;
            if (tags.Contains(normalizedQuestion, StringComparison.Ordinal)) score += 60;
        }

        foreach (var term in terms)
        {
            if (ContainsWholeTerm(title, term)) score += 18;
            if (ContainsWholeTerm(tags, term)) score += 14;
            if (ContainsWholeTerm(text, term)) score += 5;
        }

        if (terms.Count >= 2)
        {
            var matchedTerms = terms.Count(term =>
                ContainsWholeTerm(title, term) ||
                ContainsWholeTerm(tags, term) ||
                ContainsWholeTerm(text, term));
            if (matchedTerms == terms.Count) score += 20;
        }

        return score;
    }

    private static bool ContainsWholeTerm(string value, string term) =>
        !string.IsNullOrWhiteSpace(value) &&
        $" {value} ".Contains($" {term} ", StringComparison.Ordinal);
}
