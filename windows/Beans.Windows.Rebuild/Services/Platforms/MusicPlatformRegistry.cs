using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Platforms;

public sealed class MusicPlatformRegistry : IMusicPlatformRegistry
{
    private const string CurrentPlatformKey = "platform.current";
    private const string EnabledPrefix = "platform.enabled.";
    private readonly IPlatformPreferenceStore _preferences;
    private readonly Dictionary<PlatformId, IMusicPlatformService> _services;
    private readonly Dictionary<PlatformId, MusicPlatformDescriptor> _descriptors;

    public MusicPlatformRegistry(IEnumerable<IMusicPlatformService> services, IPlatformPreferenceStore preferences)
    {
        _preferences = preferences;
        _services = services.ToDictionary(service => service.PlatformId);
        _descriptors = _services.Values.ToDictionary(service => service.PlatformId, service => RestoreEnabled(service.Descriptor));

        var enabled = GetEnabledOnlinePlatforms();
        if (enabled.Count == 0)
        {
            var first = _descriptors.Values
                .Where(IsCurrentProductOnlinePlatform)
                .FirstOrDefault(descriptor => descriptor.IsAvailable)
                ?? throw new InvalidOperationException("没有可用的在线音乐平台");
            _descriptors[first.Id] = first with { IsEnabled = true };
            PersistEnabled(first.Id, true);
            enabled = GetEnabledOnlinePlatforms();
        }

        var stored = _preferences.GetString(CurrentPlatformKey);
        CurrentPlatformId = PlatformIdExtensions.TryParseStableId(stored, out var parsed) && IsEnabled(parsed.ToStableId())
            ? parsed
            : enabled[0].Id;
        _preferences.SetString(CurrentPlatformKey, CurrentPlatformId.Value.ToStableId());
    }

    public PlatformId? CurrentPlatformId { get; private set; }
    public IReadOnlyList<IMusicPlatformService> EnabledPlatforms => GetEnabledPlatforms().Select(descriptor => _services[descriptor.Id]).ToArray();
    public event EventHandler<PlatformStateChangedEventArgs>? PlatformStateChanged;
    public event EventHandler<CurrentPlatformChangedEventArgs>? CurrentPlatformChanged;

    public IReadOnlyList<MusicPlatformDescriptor> GetAllPlatforms() => _descriptors.Values.OrderBy(PlatformOrder).ToArray();
    public IReadOnlyList<MusicPlatformDescriptor> GetEnabledPlatforms() => GetAllPlatforms().Where(descriptor => descriptor.IsEnabled && descriptor.IsAvailable).ToArray();
    public MusicPlatformDescriptor? GetPlatform(string platformId) => PlatformIdExtensions.TryParseStableId(platformId, out var id) && _descriptors.TryGetValue(id, out var descriptor) ? descriptor : null;
    public MusicPlatformDescriptor? GetCurrentPlatform() => CurrentPlatformId is { } id && _descriptors.TryGetValue(id, out var descriptor) ? descriptor : null;
    public IMusicPlatformService? Get(PlatformId platformId) => _services.GetValueOrDefault(platformId);

    public void SetCurrent(PlatformId platformId) => SetCurrentPlatform(platformId.ToStableId());

    public void SetCurrentPlatform(string platformId)
    {
        var descriptor = GetPlatform(platformId);
        if (descriptor is null || !descriptor.IsEnabled || !descriptor.IsAvailable) return;
        var previous = CurrentPlatformId?.ToStableId() ?? string.Empty;
        if (previous == descriptor.StableId) return;
        CurrentPlatformId = descriptor.Id;
        _preferences.SetString(CurrentPlatformKey, descriptor.StableId);
        CurrentPlatformChanged?.Invoke(this, new CurrentPlatformChangedEventArgs(previous, descriptor.StableId));
    }

    public bool IsEnabled(string platformId) => GetPlatform(platformId) is { IsEnabled: true, IsAvailable: true };

    public void SetEnabled(string platformId, bool enabled)
    {
        var descriptor = GetPlatform(platformId);
        if (descriptor is null || !descriptor.IsAvailable || descriptor.IsEnabled == enabled) return;
        if (!enabled && IsCurrentProductOnlinePlatform(descriptor) && GetEnabledOnlinePlatforms().Count == 1) return;

        var updated = descriptor with { IsEnabled = enabled };
        _descriptors[updated.Id] = updated;
        PersistEnabled(updated.Id, enabled);
        PlatformStateChanged?.Invoke(this, new PlatformStateChangedEventArgs(updated.StableId, updated));

        if (!enabled && CurrentPlatformId == updated.Id)
        {
            SetCurrent(GetEnabledOnlinePlatforms()[0].Id);
        }
    }

    public AuthorizationState GetAuthorizationState(string platformId) => GetPlatform(platformId)?.AuthorizationState ?? AuthorizationState.Unknown;

    public async Task<AuthorizationState> RefreshAuthorizationStateAsync(string platformId, CancellationToken cancellationToken)
    {
        var descriptor = GetPlatform(platformId);
        if (descriptor is null || !_services.TryGetValue(descriptor.Id, out var service)) return AuthorizationState.Unknown;
        var credentialState = await service.RefreshCredentialStateAsync(cancellationToken);
        var authorizationState = credentialState switch
        {
            CredentialState.Valid or CredentialState.Expiring => AuthorizationState.Authorized,
            CredentialState.Expired => AuthorizationState.Expired,
            CredentialState.Checking => AuthorizationState.SigningIn,
            CredentialState.Error => AuthorizationState.Error,
            _ => AuthorizationState.SignedOut
        };
        var updated = descriptor with { AuthorizationState = authorizationState };
        _descriptors[descriptor.Id] = updated;
        PlatformStateChanged?.Invoke(this, new PlatformStateChangedEventArgs(updated.StableId, updated));
        return authorizationState;
    }

    public async Task<IReadOnlyDictionary<PlatformId, PlatformSearchOutcome>> SearchAllAsync(string query, int page, int pageSize, CancellationToken cancellationToken)
    {
        var results = new Dictionary<PlatformId, PlatformSearchOutcome>();
        foreach (var platform in EnabledPlatforms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { results[platform.PlatformId] = new PlatformSearchOutcome(await platform.SearchAsync(query, page, pageSize, cancellationToken), null); }
            catch (OperationCanceledException) { throw; }
            catch { results[platform.PlatformId] = new PlatformSearchOutcome(null, "平台暂时不可用"); }
        }
        return results;
    }

    private MusicPlatformDescriptor RestoreEnabled(MusicPlatformDescriptor descriptor)
    {
        if (!descriptor.IsAvailable)
        {
            PersistEnabled(descriptor.Id, false);
            return descriptor with { IsEnabled = false };
        }

        var stored = _preferences.GetString(EnabledPrefix + descriptor.StableId);
        return stored is null ? descriptor : descriptor with { IsEnabled = bool.TryParse(stored, out var enabled) && enabled };
    }

    private IReadOnlyList<MusicPlatformDescriptor> GetEnabledOnlinePlatforms() =>
        GetEnabledPlatforms().Where(IsCurrentProductOnlinePlatform).ToArray();

    private static bool IsCurrentProductOnlinePlatform(MusicPlatformDescriptor descriptor) =>
        descriptor.Id is PlatformId.QqMusic or PlatformId.NetEaseMusic;

    private void PersistEnabled(PlatformId id, bool enabled) => _preferences.SetString(EnabledPrefix + id.ToStableId(), enabled.ToString());
    private static int PlatformOrder(MusicPlatformDescriptor descriptor) => descriptor.Id switch { PlatformId.QqMusic => 0, PlatformId.NetEaseMusic => 1, PlatformId.KuGouMusic => 2, PlatformId.Beans => 3, _ => 4 };
}
