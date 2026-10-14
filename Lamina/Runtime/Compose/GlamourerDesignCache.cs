using Lamina.Ipc;
using Newtonsoft.Json.Linq;

namespace Lamina.Runtime.Compose;

/// <summary>
/// Caches Glamourer design JObjects and the design list so composing does not hit IPC for every evaluation.
/// Parsing into drafts happens per request because provenance differs per call site; it is cheap.
/// The engine invalidates on Glamourer design changes / plugin reloads.
/// </summary>
public sealed class GlamourerDesignCache
{
    private readonly IGlamourerBridge _bridge;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, JObject?> _designs = new();
    private IReadOnlyDictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor)>? _names;

    public GlamourerDesignCache(IGlamourerBridge bridge)
        => _bridge = bridge;

    /// <summary> The raw design JObject, fetched on first use. Null when Glamourer is unavailable or the design is missing. </summary>
    public JObject? GetRaw(Guid designId)
    {
        lock (_lock)
        {
            if (_designs.TryGetValue(designId, out var cached))
                return cached;
        }

        var fetched = _bridge.Available ? _bridge.GetDesign(designId) : null;
        lock (_lock)
        {
            // Only remember misses while Glamourer is up; otherwise the next call after it loads should retry.
            if (fetched is not null || _bridge.Available)
                _designs[designId] = fetched;
        }

        return fetched;
    }

    /// <summary> Parses the design into a draft with the given provenance. Null when missing. </summary>
    public DesignDraft? Get(Guid designId, bool includeMods, Provenance source)
    {
        var raw = GetRaw(designId);
        return raw is null ? null : GlamourerDesignParser.Parse(raw, source, includeMods);
    }

    /// <summary> Display name from Glamourer's design list, or null. </summary>
    public string? TryGetDesignName(Guid designId)
        => Names.TryGetValue(designId, out var entry) ? entry.DisplayName : null;

    /// <summary> Full design list (display name, full path, color), refreshed on <see cref="InvalidateAll"/>. </summary>
    public IReadOnlyDictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor)> Names
    {
        get
        {
            lock (_lock)
            {
                if (_names is not null)
                    return _names;
            }

            var fetched = _bridge.Available
                ? _bridge.GetDesigns()
                : new Dictionary<Guid, (string, string, uint)>();
            lock (_lock)
            {
                if (_bridge.Available)
                    _names = fetched;
                return fetched;
            }
        }
    }

    /// <summary> Forget one design. </summary>
    public void Invalidate(Guid designId)
    {
        lock (_lock)
            _designs.Remove(designId);
    }

    /// <summary> Forget everything, including the design list. </summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _designs.Clear();
            _names = null;
        }
    }

    /// <summary>
    /// Re-reads the design list (one cheap IPC call) and drops cached designs that disappeared or whose name / path changed,
    /// so edits made in Glamourer show up without a plugin reload. Returns true when anything changed.
    /// </summary>
    public bool RefreshNames()
    {
        if (!_bridge.Available)
            return false;

        var fresh = _bridge.GetDesigns();
        var changed = false;
        lock (_lock)
        {
            if (_names is { } old)
            {
                foreach (var (id, entry) in fresh)
                {
                    if (!old.TryGetValue(id, out var previous) || previous.DisplayName != entry.DisplayName || previous.FullPath != entry.FullPath)
                    {
                        changed = true;
                        _designs.Remove(id);
                    }
                }

                foreach (var id in old.Keys)
                {
                    if (!fresh.ContainsKey(id))
                    {
                        changed = true;
                        _designs.Remove(id);
                    }
                }
            }

            foreach (var id in _designs.Keys.Where(id => !fresh.ContainsKey(id)).ToList())
            {
                changed = true;
                _designs.Remove(id);
            }

            _names = fresh;
        }

        return changed;
    }

    /// <summary> Fetch the given designs now so the first compose after a reload does not stall. </summary>
    public void Prime(IEnumerable<Guid> designIds)
    {
        foreach (var id in designIds.Distinct())
            GetRaw(id);
    }
}
