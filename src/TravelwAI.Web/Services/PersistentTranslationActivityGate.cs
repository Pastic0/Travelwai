using System.Collections.Concurrent;

namespace TravelwAI.Web.Services;

public sealed class PersistentTranslationActivityGate
{
    private static readonly TimeSpan ClientStaleAfter = TimeSpan.FromSeconds(100);
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _englishClients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _activationSignal = new(0, 1);

    public void SetClientState(string? clientId, bool englishActive)
    {
        var normalizedClientId = NormalizeClientId(clientId);
        if (normalizedClientId.Length == 0) return;

        if (englishActive)
        {
            _englishClients[normalizedClientId] = DateTimeOffset.UtcNow;
            SignalActivation();
            return;
        }

        _englishClients.TryRemove(normalizedClientId, out _);
    }

    public void TouchEnglishClient(string? clientId)
        => SetClientState(clientId, englishActive: true);

    public bool HasActiveEnglishClient()
    {
        RemoveStaleClients();
        return !_englishClients.IsEmpty;
    }

    public async Task WaitForEnglishClientAsync(CancellationToken cancellationToken)
    {
        while (!HasActiveEnglishClient())
        {
            await _activationSignal.WaitAsync(RecheckInterval, cancellationToken);
        }
    }

    private void RemoveStaleClients()
    {
        var staleBefore = DateTimeOffset.UtcNow - ClientStaleAfter;
        foreach (var pair in _englishClients)
        {
            if (pair.Value < staleBefore)
                _englishClients.TryRemove(pair.Key, out _);
        }
    }

    private void SignalActivation()
    {
        try
        {
            if (_activationSignal.CurrentCount == 0) _activationSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static string NormalizeClientId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        return text.Length <= 120 ? text : text[..120];
    }
}
