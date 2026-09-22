using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace PayPlanner.Api.Services.Storage;

public sealed record StorageProviderHealthSnapshot(
    string ProviderKey,
    string State,
    int ConsecutiveFailures,
    DateTime? OpenUntilUtc,
    DateTime? LastSuccessAtUtc,
    DateTime? LastFailureAtUtc,
    string? LastError);

public interface IStorageProviderHealthTracker
{
    bool CanAttempt(string providerKey);
    void Record(string providerKey, StorageOperationStatus status, string? error = null);
    IReadOnlyList<StorageProviderHealthSnapshot> Snapshot(IEnumerable<string> providerKeys);
}

public sealed class StorageProviderHealthTracker(
    IOptionsMonitor<StorageOrchestrationOptions> options,
    TimeProvider timeProvider) : IStorageProviderHealthTracker
{
    private readonly ConcurrentDictionary<string, ProviderState> _states = new(StringComparer.OrdinalIgnoreCase);

    public bool CanAttempt(string providerKey)
    {
        if (!_states.TryGetValue(providerKey, out var state)) return true;
        lock (state)
        {
            return state.OpenUntilUtc is null || state.OpenUntilUtc <= timeProvider.GetUtcNow().UtcDateTime;
        }
    }

    public void Record(string providerKey, StorageOperationStatus status, string? error = null)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return;
        var state = _states.GetOrAdd(providerKey, static _ => new ProviderState());
        var now = timeProvider.GetUtcNow().UtcDateTime;
        lock (state)
        {
            if (status == StorageOperationStatus.Succeeded)
            {
                state.ConsecutiveFailures = 0;
                state.OpenUntilUtc = null;
                state.LastSuccessAtUtc = now;
                state.LastError = null;
                return;
            }

            if (status is not (StorageOperationStatus.Unavailable or StorageOperationStatus.Unknown)) return;
            state.ConsecutiveFailures++;
            state.LastFailureAtUtc = now;
            state.LastError = Limit(error ?? status.ToString());
            var threshold = Math.Clamp(options.CurrentValue.ProviderFailureThreshold, 1, 100);
            if (state.ConsecutiveFailures >= threshold)
            {
                state.OpenUntilUtc = now.AddSeconds(Math.Clamp(options.CurrentValue.ProviderCircuitBreakSeconds, 5, 3600));
            }
        }
    }

    public IReadOnlyList<StorageProviderHealthSnapshot> Snapshot(IEnumerable<string> providerKeys)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return providerKeys.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .Select(key =>
            {
                if (!_states.TryGetValue(key, out var state))
                    return new StorageProviderHealthSnapshot(key, "healthy", 0, null, null, null, null);
                lock (state)
                {
                    var status = state.OpenUntilUtc > now ? "open" : state.ConsecutiveFailures > 0 ? "halfOpen" : "healthy";
                    return new StorageProviderHealthSnapshot(key, status, state.ConsecutiveFailures,
                        state.OpenUntilUtc, state.LastSuccessAtUtc, state.LastFailureAtUtc, state.LastError);
                }
            }).ToArray();
    }

    private static string Limit(string value) => value.Length <= 500 ? value : value[..500];

    private sealed class ProviderState
    {
        public int ConsecutiveFailures { get; set; }
        public DateTime? OpenUntilUtc { get; set; }
        public DateTime? LastSuccessAtUtc { get; set; }
        public DateTime? LastFailureAtUtc { get; set; }
        public string? LastError { get; set; }
    }
}
