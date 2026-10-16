namespace Lamina.Decals.Publish;

/// <summary>
/// The publisher's memory of redirect maps (game path → bake file). Two things are kept, both pure bookkeeping (the publisher
/// owns the Penumbra calls; framework thread):
/// <list type="bullet">
/// <item> What is installed per collection, with the situation key it was installed for, so an identical re-install can be told
/// from a change (the game caches textures by path, so files it already loaded need no redraw) and the same situation needs no work at all. </item>
/// <item> A bounded, least-recently-used memory of situation key → map, so a situation that comes back (a rule set that stopped
/// matching and matches again) re-installs its bakes without baking. Files these maps name are protected from cache trimming. </item>
/// </list>
/// A situation key names everything that decides the result cheaply (drawn outfit, mods, decals, images, collection); the
/// bake cache key stays the source of truth for file names on the slow path.
/// </summary>
public sealed class RedirectLedger
{
    /// <summary> How many situations are remembered. </summary>
    public const int MemorySize = 16;

    private sealed record Installed(string? Situation, Dictionary<string, string> Map);

    private readonly Dictionary<Guid, Installed> _installed = new();
    private readonly Dictionary<string, LinkedListNode<(string Situation, Dictionary<string, string> Map)>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Situation, Dictionary<string, string> Map)> _order = new();

    // ---- installed ----

    /// <summary> True when the collection carries the map installed for exactly this situation (nothing to publish). </summary>
    public bool IsInstalled(Guid collectionId, string situation)
        => _installed.TryGetValue(collectionId, out var installed) && string.Equals(installed.Situation, situation, StringComparison.Ordinal);

    /// <summary> The situation the collection's map was installed for; null when nothing is installed or the mod was taken out since. </summary>
    public string? InstalledSituation(Guid collectionId)
        => _installed.GetValueOrDefault(collectionId)?.Situation;

    /// <summary> What is installed in the collection (as last recorded), or null. </summary>
    public IReadOnlyDictionary<string, string>? InstalledMap(Guid collectionId)
        => _installed.GetValueOrDefault(collectionId)?.Map;

    /// <summary>
    /// Records <paramref name="redirects"/> as installed in the collection for <paramref name="situation"/> (null when the
    /// situation could not be read) and reports whether the map differs from what was recorded before.
    /// </summary>
    /// <returns> True when the map changed, when nothing was recorded for the collection, or when the map is empty after a non-empty one. </returns>
    public bool Install(Guid collectionId, string? situation, IReadOnlyDictionary<string, string> redirects)
    {
        var changed = !_installed.TryGetValue(collectionId, out var previous) || !Same(previous.Map, redirects);
        _installed[collectionId] = new Installed(situation, Copy(redirects));
        return changed;
    }

    /// <summary>
    /// The publisher took the mod out of the collection to resolve the upstream textures: the situation no longer counts as
    /// installed, but the map stays for the change comparison (the game still shows those files until a reload).
    /// </summary>
    public void MarkRemoved(Guid collectionId)
    {
        if (_installed.TryGetValue(collectionId, out var installed))
            _installed[collectionId] = installed with { Situation = null };
    }

    /// <summary> Forgets what is installed in the collection (after a clear or a refused install). The situation memory is kept. </summary>
    public void Forget(Guid collectionId)
        => _installed.Remove(collectionId);

    /// <summary> Forgets what is installed everywhere (Penumbra dropped its temporary mods). The situation memory is kept: the files still exist. </summary>
    public void ForgetInstalled()
        => _installed.Clear();

    // ---- situation memory ----

    /// <summary> Remembers the map produced for a situation (most recent first; the oldest beyond <see cref="MemorySize"/> is dropped). </summary>
    public void Remember(string situation, IReadOnlyDictionary<string, string> redirects)
    {
        Drop(situation);
        _memory[situation] = _order.AddFirst((situation, Copy(redirects)));
        while (_order.Count > MemorySize)
        {
            _memory.Remove(_order.Last!.Value.Situation);
            _order.RemoveLast();
        }
    }

    /// <summary> The map remembered for a situation (marked most recently used), or null. </summary>
    public IReadOnlyDictionary<string, string>? Recall(string situation)
    {
        if (!_memory.TryGetValue(situation, out var node))
            return null;

        _order.Remove(node);
        _order.AddFirst(node);
        return node.Value.Map;
    }

    /// <summary> Drops a remembered situation (its files went missing). </summary>
    public void Drop(string situation)
    {
        if (_memory.Remove(situation, out var node))
            _order.Remove(node);
    }

    /// <summary> Drops every remembered situation except <paramref name="keep"/> (null keeps none). Returns how many were dropped. </summary>
    public int KeepOnly(string? keep)
    {
        var dropped = 0;
        foreach (var situation in _memory.Keys.ToList())
        {
            if (string.Equals(situation, keep, StringComparison.Ordinal))
                continue;
            Drop(situation);
            dropped++;
        }

        return dropped;
    }

    /// <summary> Drops every remembered situation and what is installed everywhere. Returns how many situations were dropped. </summary>
    public int ForgetAll()
    {
        var dropped = _memory.Count;
        _memory.Clear();
        _order.Clear();
        _installed.Clear();
        return dropped;
    }

    /// <summary> How many situations are remembered. </summary>
    public int Remembered
        => _memory.Count;

    /// <summary> Every bake file an installed or remembered map names; cache trimming must keep these. </summary>
    public IReadOnlyCollection<string> ReferencedFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var installed in _installed.Values)
            files.UnionWith(installed.Map.Values);
        foreach (var (_, map) in _order)
            files.UnionWith(map.Values);
        return files;
    }

    /// <summary> Every bake file the installed maps name (not the remembered ones). </summary>
    public IReadOnlyCollection<string> InstalledFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var installed in _installed.Values)
            files.UnionWith(installed.Map.Values);
        return files;
    }

    private static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string> redirects)
        => new(redirects, StringComparer.OrdinalIgnoreCase);

    private static bool Same(Dictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count)
            return false;

        foreach (var (gamePath, file) in b)
        {
            if (!a.TryGetValue(gamePath, out var previous) || !string.Equals(previous, file, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
