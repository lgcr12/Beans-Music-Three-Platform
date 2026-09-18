using System.Collections.Concurrent;

namespace Beans.Core;

public enum CredentialProbeMode { Manual, Automatic }
public enum CredentialProbeStatus { NotAuthorized, NeverChecked, Checking, Valid, PlaybackLimited, Invalid, NetworkError }

public sealed record CredentialProbeOutcome(
    CredentialProbeStatus Status,
    bool LoginValid,
    string? Membership,
    bool? PlaybackReady,
    string? ReasonCode,
    string? Quality = null,
    int? VkeyCode = null,
    int? HttpStatus = null);

public sealed record PlatformCredentialProbeResult(
    string Platform,
    CredentialProbeMode Mode,
    CredentialProbeStatus Status,
    bool LoginValid,
    string? Membership,
    bool? PlaybackReady,
    DateTimeOffset CheckedAt,
    DateTimeOffset NextCheckAt,
    string? ReasonCode,
    string? Quality = null,
    int? VkeyCode = null,
    int? HttpStatus = null);

public sealed class CredentialProbeService
{
    public static readonly TimeSpan SuccessInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan NetworkRetryInterval = TimeSpan.FromHours(1);

    private readonly PlatformMusicClient client;
    private readonly Func<string, IReadOnlyDictionary<string, string>?> credentialProvider;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, Lazy<Task<PlatformCredentialProbeResult>>> active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PlatformCredentialProbeResult> results = new(StringComparer.OrdinalIgnoreCase);

    public CredentialProbeService(
        PlatformMusicClient client,
        Func<string, IReadOnlyDictionary<string, string>?> credentialProvider,
        IEnumerable<PlatformCredentialProbeResult>? restored = null,
        TimeProvider? clock = null)
    {
        this.client = client;
        this.credentialProvider = credentialProvider;
        this.clock = clock ?? TimeProvider.System;
        foreach (var result in restored ?? []) results[result.Platform] = result;
    }

    public event EventHandler<PlatformCredentialProbeResult>? ResultChanged;
    public IReadOnlyDictionary<string, PlatformCredentialProbeResult> Results => results;

    public CredentialProbeStatus StatusFor(string platform)
    {
        if (!HasCredentials(platform)) return CredentialProbeStatus.NotAuthorized;
        if (active.ContainsKey(platform)) return CredentialProbeStatus.Checking;
        return results.TryGetValue(platform, out var result) ? result.Status : CredentialProbeStatus.NeverChecked;
    }

    public bool IsDue(string platform, DateTimeOffset? now = null) =>
        !results.TryGetValue(platform, out var result) || result.NextCheckAt <= (now ?? clock.GetUtcNow());

    public async Task<IReadOnlyList<PlatformCredentialProbeResult>> RunDueAsync(bool automaticEnabled, CancellationToken ct = default)
    {
        if (!automaticEnabled) return [];
        var due = new[] { "qq", "netease" }.Where(provider => HasCredentials(provider) && IsDue(provider)).ToArray();
        return await Task.WhenAll(due.Select(provider => RunAsync(provider, CredentialProbeMode.Automatic, ct)));
    }

    public Task<PlatformCredentialProbeResult> RunAsync(string platform, CredentialProbeMode mode, CancellationToken ct = default)
    {
        if (active.TryGetValue(platform, out var running)) return running.Value;
        if (mode == CredentialProbeMode.Automatic && results.TryGetValue(platform, out var saved) && saved.NextCheckAt > clock.GetUtcNow())
            return Task.FromResult(saved);
        return active.GetOrAdd(
            platform,
            _ => new Lazy<Task<PlatformCredentialProbeResult>>(
                () => RunCoreAsync(platform, mode, ct),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public void Clear(string platform)
    {
        results.TryRemove(platform, out _);
    }

    private async Task<PlatformCredentialProbeResult> RunCoreAsync(string platform, CredentialProbeMode mode, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            var checkedAt = clock.GetUtcNow();
            var credentials = credentialProvider(platform);
            CredentialProbeOutcome outcome;
            if (credentials is null || !PlatformCredentialPolicy.LooksUsable(platform, credentials))
            {
                outcome = new(CredentialProbeStatus.NotAuthorized, false, null, false, "not_authorized");
            }
            else
            {
                outcome = await client.ProbeAsync(platform, credentials, ct);
            }

            var interval = outcome.Status == CredentialProbeStatus.NetworkError ? NetworkRetryInterval : SuccessInterval;
            var result = new PlatformCredentialProbeResult(
                platform, mode, outcome.Status, outcome.LoginValid, outcome.Membership, outcome.PlaybackReady,
                checkedAt, checkedAt.Add(interval), outcome.ReasonCode, outcome.Quality, outcome.VkeyCode, outcome.HttpStatus);
            results[platform] = result;
            ResultChanged?.Invoke(this, result);
            return result;
        }
        finally
        {
            active.TryRemove(platform, out _);
        }
    }

    private bool HasCredentials(string platform)
    {
        var credentials = credentialProvider(platform);
        return credentials is not null && PlatformCredentialPolicy.LooksUsable(platform, credentials);
    }
}
