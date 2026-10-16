using Lamina.Ipc;

namespace Lamina.Runtime.Compose;

/// <summary>
/// Caches Customize+ profile bones and the profile list so composing does not hit IPC for every evaluation (the list call is
/// heavy on the Customize+ side). The engine invalidates on Customize+ profile changes, availability flips and reapply.
/// Customize+ only raises <c>Profile.OnUpdate</c> when the profile active on the local player changes, so a profile that is
/// created or edited without being assigned to the player never produces an event: the UI refreshes the list itself
/// (<see cref="InvalidateProfiles"/>) and offers a manual refresh (<see cref="InvalidateAll"/>).
/// </summary>
public sealed class CustomizePlusProfileCache
{
    private readonly ICustomizePlusBridge _bridge;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, CustomizePlusBone>?> _bones = new();
    private IReadOnlyList<CustomizePlusProfileInfo>? _profiles;

    public CustomizePlusProfileCache(ICustomizePlusBridge bridge)
        => _bridge = bridge;

    /// <summary> The bridge this cache reads from. </summary>
    public ICustomizePlusBridge Bridge
        => _bridge;

    /// <summary> The profile's bones, fetched on first use. Null when Customize+ is unavailable or the profile is missing. </summary>
    public IReadOnlyDictionary<string, CustomizePlusBone>? GetBones(Guid profileId)
    {
        lock (_lock)
        {
            if (_bones.TryGetValue(profileId, out var cached))
                return cached;
        }

        var fetched = _bridge.Available ? _bridge.GetProfileBones(profileId) : null;
        lock (_lock)
        {
            // Only remember misses while Customize+ is up; otherwise the next call after it loads should retry.
            if (fetched is not null || _bridge.Available)
                _bones[profileId] = fetched;
        }

        return fetched;
    }

    /// <summary> Every saved profile, refreshed on <see cref="InvalidateAll"/>. Empty while Customize+ is unavailable. </summary>
    public IReadOnlyList<CustomizePlusProfileInfo> Profiles
    {
        get
        {
            lock (_lock)
            {
                if (_profiles is not null)
                    return _profiles;
            }

            var fetched = _bridge.Available ? _bridge.GetProfiles() : Array.Empty<CustomizePlusProfileInfo>();
            lock (_lock)
            {
                if (_bridge.Available)
                    _profiles = fetched;
            }

            return fetched;
        }
    }

    /// <summary> Display name of a profile from the list, or null. </summary>
    public string? TryGetProfileName(Guid profileId)
        => Profiles.FirstOrDefault(p => p.Id == profileId)?.Name;

    /// <summary> Forgets the profile list only; cached bones stay. </summary>
    public void InvalidateProfiles()
    {
        lock (_lock)
            _profiles = null;
    }

    /// <summary> Forgets everything; the next request refetches. </summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _bones.Clear();
            _profiles = null;
        }
    }
}
