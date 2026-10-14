using System.IO;
using Vitrine.Penumbra;

namespace Vitrine.Catalog;

/// <summary> Serialized form of the catalog cache (mods.json). </summary>
public sealed class CatalogCacheFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<CachedMod> Mods { get; set; } = new();
}

/// <summary> One cached mod. Tuples are flattened into plain classes so the JSON stays readable and stable. </summary>
public sealed class CachedMod
{
    public string ModDirectory { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public VitrineCategory Type { get; set; }
    public List<CachedAffectedItem> AffectedItems { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, CachedOptionGroup> AvailableSettings { get; set; } = new();
    /// <summary> Signature of the mod folder when it was scanned (max last-write ticks of its top-level files), 0 when unknown. </summary>
    public long SourceStamp { get; set; }
    public DateTime ScannedAtUtc { get; set; }
}

public sealed class CachedAffectedItem
{
    public VitrineCategory Category { get; set; }
    public string Name { get; set; } = string.Empty;
    public ulong? ItemId { get; set; }
}

public sealed class CachedOptionGroup
{
    public List<string> Options { get; set; } = new();
    public int GroupType { get; set; }
}

/// <summary>
/// The reading layer over Penumbra's mods (TDD 12.1 / 12.2). Scans incrementally on the framework thread via <see cref="Tick"/>,
/// caches results to <c>mods.json</c> so unchanged mods are available instantly on the next start, and refreshes the per-collection
/// state on demand. Framework-thread only (no locking); the module guarantees that.
/// </summary>
public sealed class ModCatalog
{
    /// <summary> Mods processed per <see cref="Tick"/> during a scan. </summary>
    public int ModsPerTick { get; set; } = 10;

    private readonly IPenumbraReader _penumbra;
    private readonly string _cachePath;
    private readonly Action<string> _logWarning;
    private readonly Action<string> _logDebug;
    private readonly Func<DateTime> _clock;

    private readonly Dictionary<string, ModInformation> _mods = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedMod> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Directory, string Name, bool Force)> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private List<ModInformation> _sorted = new();
    private bool _cacheDirty;
    private bool _changedDuringTick;
    private int _scanTotal;
    private int _scanDone;

    /// <param name="penumbra"> The Penumbra reader. </param>
    /// <param name="cachePath"> Full path of mods.json. </param>
    /// <param name="logWarning"> Warning sink. </param>
    /// <param name="logDebug"> Debug sink. </param>
    /// <param name="clock"> UTC clock, injectable for tests. </param>
    public ModCatalog(IPenumbraReader penumbra, string cachePath, Action<string> logWarning, Action<string> logDebug, Func<DateTime>? clock = null)
    {
        _penumbra   = penumbra;
        _cachePath  = cachePath;
        _logWarning = logWarning;
        _logDebug   = logDebug;
        _clock      = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary> Every catalogued mod, sorted by name. Rebuilt lazily after changes. </summary>
    public IReadOnlyList<ModInformation> Mods
        => _sorted;

    /// <summary> Raised after a tick that changed the catalog. </summary>
    public event Action? Changed;

    /// <summary> True while a scan has queued work. </summary>
    public bool IsScanning
        => _queue.Count > 0;

    /// <summary> (done, total) of the current scan, both 0 when idle. </summary>
    public (int Done, int Total) ScanProgress
        => _queue.Count == 0 ? (0, 0) : (_scanDone, _scanTotal);

    /// <summary> Looks up one mod by directory. </summary>
    public ModInformation? Get(string modDirectory)
        => _mods.GetValueOrDefault(modDirectory);

    /// <summary> When the mod was last scanned (UTC), or MinValue when unknown. </summary>
    public DateTime ScannedAt(string modDirectory)
        => _cache.TryGetValue(modDirectory, out var cached) ? cached.ScannedAtUtc : DateTime.MinValue;

    /// <summary> Loads mods.json. Returns false (and logs) when the file was missing or broken. </summary>
    public bool LoadCache()
    {
        var file = JsonUtil.ReadOrNull<CatalogCacheFile>(_cachePath, out var error);
        if (error is not null)
        {
            _logWarning($"Vitrine: could not read mod cache {_cachePath}: {error.Message}");
            TryQuarantine(_cachePath);
            return false;
        }

        if (file is null)
            return false;

        _cache.Clear();
        _mods.Clear();
        foreach (var cached in file.Mods)
        {
            if (string.IsNullOrEmpty(cached.ModDirectory))
                continue;
            _cache[cached.ModDirectory] = cached;
            _mods[cached.ModDirectory]  = FromCache(cached);
        }

        RebuildSorted();
        _logDebug($"Vitrine: loaded {_mods.Count} mods from cache.");
        Changed?.Invoke();
        return true;
    }

    /// <summary> Writes mods.json if anything changed since the last save. </summary>
    public void SaveCacheIfDirty()
    {
        if (!_cacheDirty)
            return;

        try
        {
            var file = new CatalogCacheFile { Mods = _cache.Values.OrderBy(m => m.ModDirectory, StringComparer.OrdinalIgnoreCase).ToList() };
            JsonUtil.WriteAtomic(_cachePath, file);
            _cacheDirty = false;
        }
        catch (Exception ex)
        {
            _logWarning($"Vitrine: could not write mod cache: {ex.Message}");
        }
    }

    /// <summary> Queues every mod Penumbra knows. Mods missing from Penumbra are dropped. <paramref name="force"/> ignores the source stamp. </summary>
    public void BeginFullScan(bool force = false)
    {
        if (!_penumbra.Available)
            return;

        var list = _penumbra.GetModList();
        var gone = _mods.Keys.Where(dir => !list.ContainsKey(dir)).ToList();
        foreach (var dir in gone)
        {
            _mods.Remove(dir);
            _cache.Remove(dir);
            _cacheDirty = true;
        }

        if (gone.Count > 0)
        {
            RebuildSorted();
            Changed?.Invoke();
        }

        foreach (var (dir, name) in list.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
            Enqueue(dir, name, force);
    }

    /// <summary> Queues a single mod (new or changed). </summary>
    public void QueueMod(string modDirectory, bool force = true)
    {
        if (!_penumbra.Available)
            return;

        var list = _penumbra.GetModList();
        if (!list.TryGetValue(modDirectory, out var name))
            return;

        Enqueue(modDirectory, name, force);
    }

    /// <summary> Removes a mod from the catalog (Penumbra deleted it). </summary>
    public void RemoveMod(string modDirectory)
    {
        var removed = _mods.Remove(modDirectory) | _cache.Remove(modDirectory);
        if (!removed)
            return;

        _cacheDirty = true;
        RebuildSorted();
        Changed?.Invoke();
    }

    /// <summary> Handles Penumbra's ModMoved: the old directory disappears and the new one is scanned. </summary>
    public void MoveMod(string oldDirectory, string newDirectory)
    {
        RemoveMod(oldDirectory);
        QueueMod(newDirectory);
    }

    /// <summary> Processes up to <see cref="ModsPerTick"/> queued mods. Call every framework update. </summary>
    public void Tick()
    {
        if (_queue.Count == 0)
            return;

        _changedDuringTick = false;
        var budget = ModsPerTick;
        while (budget-- > 0 && _queue.Count > 0)
        {
            var (dir, name, force) = _queue.Dequeue();
            _queued.Remove(dir);
            _scanDone++;
            try
            {
                ScanOne(dir, name, force);
            }
            catch (Exception ex)
            {
                _logWarning($"Vitrine: scanning mod '{dir}' failed: {ex.Message}");
            }
        }

        if (_queue.Count == 0)
        {
            _scanDone = _scanTotal = 0;
            SaveCacheIfDirty();
        }

        if (_changedDuringTick)
        {
            RebuildSorted();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Refreshes per-collection state (enabled / priority / settings per collection and the local player's active collection) for every mod.
    /// One Penumbra call per collection, so it is cheap enough to run every few seconds while the window is open.
    /// </summary>
    public void RefreshCollectionStates()
    {
        if (!_penumbra.Available || _mods.Count == 0)
            return;

        var collections = _penumbra.GetCollections();
        var playerCollection = _penumbra.GetCollectionForObject(0);
        var perMod = new Dictionary<string, Dictionary<Guid, (bool, int, Dictionary<string, List<string>>)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (collectionId, _) in collections)
        {
            var all = _penumbra.GetAllModSettings(collectionId);
            if (all is null)
                continue;

            foreach (var (dir, settings) in all)
            {
                if (!perMod.TryGetValue(dir, out var map))
                    perMod[dir] = map = new Dictionary<Guid, (bool, int, Dictionary<string, List<string>>)>();
                map[collectionId] = (settings.Enabled, settings.Priority,
                    settings.Settings.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()));
            }
        }

        foreach (var (dir, mod) in _mods)
        {
            mod.PerCollection = perMod.TryGetValue(dir, out var map) ? map : new Dictionary<Guid, (bool, int, Dictionary<string, List<string>>)>();
            mod.CurrentActiveCollection = playerCollection is { } pc && mod.PerCollection.TryGetValue(pc.Id, out var state) && state.Enabled
                ? pc.Id
                : null;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------

    private void Enqueue(string dir, string name, bool force)
    {
        if (!_queued.Add(dir))
            return;

        _queue.Enqueue((dir, name, force));
        _scanTotal++;
    }

    private void ScanOne(string dir, string name, bool force)
    {
        var root  = _penumbra.GetModRootDirectory();
        var stamp = ComputeStamp(root, dir);

        if (!force && stamp != 0 && _cache.TryGetValue(dir, out var existing) && existing.SourceStamp == stamp && _mods.ContainsKey(dir))
        {
            if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                existing.Name       = name;
                _mods[dir].Name     = name;
                _cacheDirty         = true;
                _changedDuringTick  = true;
            }

            return;
        }

        var meta = _penumbra.GetModMeta(dir, name) ?? MetaJsonReader.Read(root, dir, _logDebug);
        var changed = _penumbra.GetChangedItems(dir);
        var items = changed
            .Select(kv =>
            {
                var category = ModClassifier.Classify(kv.Key, kv.Value);
                return new AffectedItem(category, kv.Key, ModClassifier.IsEquippable(category) ? ModClassifier.ExtractItemId(kv.Value) : null);
            })
            .OrderBy(i => i.Category).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var settings = _penumbra.GetAvailableModSettings(dir) ?? new Dictionary<string, (string[], int)>();

        var info = new ModInformation
        {
            ModDirectory      = dir,
            Name              = string.IsNullOrWhiteSpace(meta?.Name) ? name : meta!.Name,
            Author            = meta?.Author ?? string.Empty,
            Website           = meta?.Website ?? string.Empty,
            Description       = meta?.Description ?? string.Empty,
            Version           = meta?.Version ?? string.Empty,
            ImagePath         = meta?.ImagePath ?? string.Empty,
            Type              = ModClassifier.ClassifyMod(items),
            AffectedItems     = items,
            Tags              = meta?.Tags.ToList() ?? new List<string>(),
            AvailableSettings = settings.ToDictionary(kv => kv.Key, kv => (kv.Value.Options, kv.Value.GroupType)),
        };

        if (_mods.TryGetValue(dir, out var previous))
        {
            info.PerCollection           = previous.PerCollection;
            info.CurrentActiveCollection = previous.CurrentActiveCollection;
        }

        _mods[dir]         = info;
        _cache[dir]        = ToCache(info, stamp, _clock());
        _cacheDirty        = true;
        _changedDuringTick = true;
    }

    /// <summary> Max last-write ticks over the top-level files of the mod folder; 0 when the folder is unknown. </summary>
    public static long ComputeStamp(string root, string modDirectory)
    {
        if (string.IsNullOrEmpty(root))
            return 0;

        try
        {
            var folder = Path.Combine(root, modDirectory);
            if (!Directory.Exists(folder))
                return 0;

            long max = 0;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var ticks = File.GetLastWriteTimeUtc(file).Ticks;
                if (ticks > max)
                    max = ticks;
            }

            return max;
        }
        catch
        {
            return 0;
        }
    }

    private static CachedMod ToCache(ModInformation info, long stamp, DateTime now)
        => new()
        {
            ModDirectory      = info.ModDirectory,
            Name              = info.Name,
            Author            = info.Author,
            Website           = info.Website,
            Description       = info.Description,
            Version           = info.Version,
            ImagePath         = info.ImagePath,
            Type              = info.Type,
            AffectedItems     = info.AffectedItems.Select(i => new CachedAffectedItem { Category = i.Category, Name = i.Name, ItemId = i.ItemId }).ToList(),
            Tags              = info.Tags.ToList(),
            AvailableSettings = info.AvailableSettings.ToDictionary(kv => kv.Key, kv => new CachedOptionGroup { Options = kv.Value.Options.ToList(), GroupType = kv.Value.GroupType }),
            SourceStamp       = stamp,
            ScannedAtUtc      = now,
        };

    private static ModInformation FromCache(CachedMod cached)
        => new()
        {
            ModDirectory      = cached.ModDirectory,
            Name              = cached.Name,
            Author            = cached.Author,
            Website           = cached.Website,
            Description       = cached.Description,
            Version           = cached.Version,
            ImagePath         = cached.ImagePath,
            Type              = cached.Type,
            AffectedItems     = cached.AffectedItems.Select(i => new AffectedItem(i.Category, i.Name, i.ItemId)).ToList(),
            Tags              = cached.Tags.ToList(),
            AvailableSettings = cached.AvailableSettings.ToDictionary(kv => kv.Key, kv => (kv.Value.Options.ToArray(), kv.Value.GroupType)),
        };

    private void RebuildSorted()
        => _sorted = _mods.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.ModDirectory, StringComparer.OrdinalIgnoreCase).ToList();

    private void TryQuarantine(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Move(path, $"{path}.broken-{_clock():yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch
        {
            // Nothing else to do; the next save overwrites it.
        }
    }
}
