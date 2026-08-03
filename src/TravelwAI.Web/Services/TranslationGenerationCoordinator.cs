namespace TravelwAI.Web.Services;


public sealed class TranslationGenerationCoordinator
{
    private const int StripeCount = 64;
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, StripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    public async ValueTask<IAsyncDisposable> EnterAsync(
        IEnumerable<string> sourceTexts,
        CancellationToken cancellationToken = default)
    {
        var indexes = (sourceTexts ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(GetStripeIndex)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();

        var acquired = new List<SemaphoreSlim>(indexes.Length);
        try
        {
            foreach (var index in indexes)
            {
                var gate = _stripes[index];
                await gate.WaitAsync(cancellationToken);
                acquired.Add(gate);
            }

            return new Lease(acquired);
        }
        catch
        {
            for (var index = acquired.Count - 1; index >= 0; index -= 1)
            {
                acquired[index].Release();
            }
            throw;
        }
    }

    private static int GetStripeIndex(string sourceText)
    {
        var normalized = PersistentTranslationStore.NormalizeSource(sourceText);
        var hash = StringComparer.Ordinal.GetHashCode(normalized);
        return (int)((uint)hash % StripeCount);
    }

    private sealed class Lease : IAsyncDisposable
    {
        private IReadOnlyList<SemaphoreSlim>? _gates;

        public Lease(IReadOnlyList<SemaphoreSlim> gates)
        {
            _gates = gates;
        }

        public ValueTask DisposeAsync()
        {
            var gates = Interlocked.Exchange(ref _gates, null);
            if (gates is null) return ValueTask.CompletedTask;

            for (var index = gates.Count - 1; index >= 0; index -= 1)
            {
                gates[index].Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
