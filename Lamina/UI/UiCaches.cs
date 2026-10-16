using Lamina.Ipc;
using Lamina.Models;
using Penumbra.Api.Enums;

namespace Lamina.UI;

/// <summary>
/// Lists the pickers need (designs, mods, moodles, catalog entries), built once and refreshed on demand or when a
/// bridge flips availability. Keeps IPC calls out of the per-frame path.
/// </summary>
internal sealed class UiCaches : IDisposable
{
    private readonly LaminaServices _s;
    private bool _stale = true;
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly Dictionary<string, IReadOnlyDictionary<string, (string[] Options, GroupType Type)>?> _modSettings = new(StringComparer.OrdinalIgnoreCase);

    public UiCaches(LaminaServices services)
    {
        _s = services;
        _s.Glamourer.AvailabilityChanged += MarkStale;
        _s.Penumbra.AvailabilityChanged  += MarkStale;
        _s.Honorific.AvailabilityChanged += MarkStale;
        _s.Moodles.AvailabilityChanged   += MarkStale;
        _s.CustomizePlus.AvailabilityChanged += MarkStale;
        _s.CustomizePlus.ProfilesChanged     += MarkStale;
        _s.SimpleHeels.AvailabilityChanged   += MarkStale;
        _s.Vitrine.DesignsChanged        += MarkStale;
    }

    public void Dispose()
    {
        _s.Glamourer.AvailabilityChanged -= MarkStale;
        _s.Penumbra.AvailabilityChanged  -= MarkStale;
        _s.Honorific.AvailabilityChanged -= MarkStale;
        _s.Moodles.AvailabilityChanged   -= MarkStale;
        _s.CustomizePlus.AvailabilityChanged -= MarkStale;
        _s.CustomizePlus.ProfilesChanged     -= MarkStale;
        _s.SimpleHeels.AvailabilityChanged   -= MarkStale;
        _s.Vitrine.DesignsChanged        -= MarkStale;
    }

    /// <summary> Glamourer designs sorted by folder path. </summary>
    public IReadOnlyList<Widgets.PickItem<Guid>> Designs { get; private set; } = Array.Empty<Widgets.PickItem<Guid>>();

    /// <summary> Design id → display name. </summary>
    public IReadOnlyDictionary<Guid, string> DesignNames { get; private set; } = new Dictionary<Guid, string>();

    /// <summary> Penumbra mods (directory, name) sorted by name. </summary>
    public IReadOnlyList<Widgets.PickItem<string>> Mods { get; private set; } = Array.Empty<Widgets.PickItem<string>>();

    public IReadOnlyDictionary<string, string> ModNames { get; private set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Widgets.PickItem<Guid>> Moodles { get; private set; } = Array.Empty<Widgets.PickItem<Guid>>();

    public IReadOnlyList<Widgets.PickItem<Guid>> MoodlePresets { get; private set; } = Array.Empty<Widgets.PickItem<Guid>>();

    public IReadOnlyList<Widgets.PickItem<Guid>> VitrineDesigns { get; private set; } = Array.Empty<Widgets.PickItem<Guid>>();

    /// <summary> Customize+ saved profiles sorted by folder path. </summary>
    public IReadOnlyList<Widgets.PickItem<Guid>> CustomizePlusProfiles { get; private set; } = Array.Empty<Widgets.PickItem<Guid>>();

    /// <summary> Customize+ profile id → name. </summary>
    public IReadOnlyDictionary<Guid, string> CustomizePlusProfileNames { get; private set; } = new Dictionary<Guid, string>();

    /// <summary> Name of a Customize+ profile, or null when unknown (missing, or Customize+ unavailable). </summary>
    public string? CustomizePlusProfileName(Guid id)
        => CustomizePlusProfileNames.TryGetValue(id, out var name) ? name : null;

    // Catalog lists (built once; game data never changes at runtime).
    private IReadOnlyList<Widgets.PickItem<uint>>? _territories;
    private IReadOnlyList<Widgets.PickItem<uint>>? _regions;
    private IReadOnlyList<Widgets.PickItem<byte>>? _weathers;
    private IReadOnlyList<Widgets.PickItem<uint>>? _jobs;
    private IReadOnlyList<Widgets.PickItem<uint>>? _onlineStatuses;
    private IReadOnlyList<Widgets.PickItem<ushort>>? _emotes;
    private IReadOnlyList<Widgets.PickItem<uint>>? _statuses;
    private IReadOnlyList<Widgets.PickItem<uint>>? _worlds;
    private IReadOnlyList<Widgets.PickItem<uint>>? _duties;

    public IReadOnlyList<Widgets.PickItem<uint>> Territories
        => _territories ??= _s.Catalog.Territories.Select(t => new Widgets.PickItem<uint>(t.Id, t.Name, t.RegionName)).ToList();

    public IReadOnlyList<Widgets.PickItem<uint>> Regions
        => _regions ??= _s.Catalog.Regions.Select(r => new Widgets.PickItem<uint>(r.Id, r.Name)).ToList();

    public IReadOnlyList<Widgets.PickItem<byte>> Weathers
        => _weathers ??= _s.Catalog.Weathers.Select(w => new Widgets.PickItem<byte>(w.Id, w.Name)).ToList();

    public IReadOnlyList<Widgets.PickItem<uint>> Jobs
        => _jobs ??= _s.Catalog.Jobs.Select(j => new Widgets.PickItem<uint>(j.Id, $"{j.Abbreviation} {j.Name}", j.Role?.ToString() ?? j.Discipline.ToString())).ToList();

    public IReadOnlyList<Widgets.PickItem<uint>> OnlineStatuses
        => _onlineStatuses ??= _s.Catalog.OnlineStatuses.Select(o => new Widgets.PickItem<uint>(o.Id, o.Name)).ToList();

    public IReadOnlyList<Widgets.PickItem<ushort>> Emotes
        => _emotes ??= _s.Catalog.Emotes.Select(e => new Widgets.PickItem<ushort>(e.Id, e.Name)).ToList();

    public IReadOnlyList<Widgets.PickItem<uint>> Statuses
        => _statuses ??= _s.Catalog.Statuses.Select(e => new Widgets.PickItem<uint>(e.Id, e.Name, $"#{e.Id}")).ToList();

    public IReadOnlyList<Widgets.PickItem<uint>> Worlds
        => _worlds ??= _s.Catalog.Worlds.Select(w => new Widgets.PickItem<uint>(w.Id, w.Name)).ToList();

    public IReadOnlyList<Widgets.PickItem<uint>> Duties
        => _duties ??= _s.Catalog.Duties.Select(d => new Widgets.PickItem<uint>(d.Id, d.Name, Widgets.Humanize(d.Kind.ToString()))).ToList();

    /// <summary> Marks the plugin-backed lists stale; they are rebuilt on the next <see cref="EnsureFresh"/>. </summary>
    public void MarkStale()
        => _stale = true;

    /// <summary> Rebuilds the plugin-backed lists when stale or older than 60 s. Call once per frame from the window. </summary>
    public void EnsureFresh()
    {
        var now = DateTime.UtcNow;
        if (!_stale && now - _lastRefresh < TimeSpan.FromSeconds(60))
            return;
        _stale       = false;
        _lastRefresh = now;
        _modSettings.Clear();
        _currentModSettings.Clear();

        try
        {
            var names = _s.DesignCache.Names;
            Designs = names.Select(kv => new Widgets.PickItem<Guid>(kv.Key, kv.Value.DisplayName, Folder(kv.Value.FullPath)))
                .OrderBy(d => d.Sub, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            DesignNames = names.ToDictionary(kv => kv.Key, kv => kv.Value.DisplayName);
        }
        catch (Exception ex)
        {
            _s.Log.Debug(ex, "UI: design list refresh failed.");
        }

        try
        {
            var mods = _s.Penumbra.Available ? _s.Penumbra.GetModList() : new Dictionary<string, string>();
            Mods     = mods.Select(kv => new Widgets.PickItem<string>(kv.Key, kv.Value, kv.Key)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ModNames = new Dictionary<string, string>(mods, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _s.Log.Debug(ex, "UI: mod list refresh failed.");
        }

        try
        {
            Moodles       = _s.Moodles.Available ? _s.Moodles.GetMoodles().Select(m => new Widgets.PickItem<Guid>(m.Id, m.Title, m.FullPath)).OrderBy(m => m.Sub, StringComparer.OrdinalIgnoreCase).ToList() : Array.Empty<Widgets.PickItem<Guid>>();
            MoodlePresets = _s.Moodles.Available ? _s.Moodles.GetPresets().Select(p => new Widgets.PickItem<Guid>(p.Id, p.FullPath)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList() : Array.Empty<Widgets.PickItem<Guid>>();
        }
        catch (Exception ex)
        {
            _s.Log.Debug(ex, "UI: moodles refresh failed.");
        }

        try
        {
            // Customize+ sends no event for profiles created or edited without a character assigned, so re-read the list on
            // every refresh (stale or 60 s) while the window is open.
            _s.CustomizePlusCache.InvalidateProfiles();
            var profiles = _s.CustomizePlus.Available ? _s.CustomizePlusCache.Profiles : Array.Empty<CustomizePlusProfileInfo>();
            CustomizePlusProfiles = profiles.Select(p => new Widgets.PickItem<Guid>(p.Id, p.Name, Folder(p.Path)))
                .OrderBy(p => p.Sub, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            CustomizePlusProfileNames = profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Name);
        }
        catch (Exception ex)
        {
            _s.Log.Debug(ex, "UI: Customize+ profile refresh failed.");
        }

        try
        {
            VitrineDesigns = _s.Vitrine.Designs.Select(d => new Widgets.PickItem<Guid>(d.Id, d.Name, Widgets.Humanize(d.Category) + (d.IsAuto ? string.Empty : " · variant")))
                .OrderBy(d => d.Sub, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _s.Log.Debug(ex, "UI: vitrine designs refresh failed.");
        }
    }

    // ---- equipment / appearance pickers ---------------------------------------------------------

    private readonly Dictionary<EquipSlot, IReadOnlyList<Widgets.PickItem<ulong>>> _itemsBySlot = new();
    private byte[]? _customize;
    private DateTime _customizeRead = DateTime.MinValue;

    /// <summary> Items that go into a slot, with Glamourer's "Nothing" first for gear slots. Built once per slot. </summary>
    public IReadOnlyList<Widgets.PickItem<ulong>> ItemsFor(EquipSlot slot)
    {
        if (_itemsBySlot.TryGetValue(slot, out var cached))
            return cached;
        var list = new List<Widgets.PickItem<ulong>>();
        if (NothingItems.IdFor(slot) is { } nothing)
            list.Add(new Widgets.PickItem<ulong>(nothing, "Nothing", "empty slot"));
        // Rings: both ring slots take the same items.
        var wanted = slot == EquipSlot.LFinger ? EquipSlot.RFinger : slot;
        list.AddRange(_s.Catalog.EquipItems.Where(i => i.Slot == wanted || (wanted == EquipSlot.RFinger && i.Slot == EquipSlot.LFinger))
            .Select(i => new Widgets.PickItem<ulong>(i.Id, i.Name, i.Category)));
        _itemsBySlot[slot] = list;
        return list;
    }

    private IReadOnlyList<Widgets.PickItem<ulong>>? _glassesItems;

    /// <summary> Facewear choices for a bonus slot: "Nothing" first, then every Glasses row as a bonus item id. </summary>
    public IReadOnlyList<Widgets.PickItem<ulong>> BonusItemsFor(BonusSlot slot)
    {
        if (_glassesItems is not null)
            return _glassesItems;
        var list = new List<Widgets.PickItem<ulong>> { new(BonusItems.NothingFor(slot), "Nothing", "empty slot") };
        list.AddRange(_s.Catalog.Glasses.Select(g => new Widgets.PickItem<ulong>(BonusItems.IdFor(g.Id), g.Name, null)));
        return _glassesItems = list;
    }

    /// <summary> The local player's customize bytes, re-read at most once per second. Null when not logged in. </summary>
    public byte[]? CurrentCustomize()
    {
        var now = DateTime.UtcNow;
        if (now - _customizeRead < TimeSpan.FromSeconds(1))
            return _customize;
        _customizeRead = now;
        try
        {
            var player = _s.ObjectTable.LocalPlayer;
            _customize = player is null ? null : player.Customize.ToArray();
        }
        catch (Exception ex)
        {
            _s.Log.Debug(ex, "UI: reading the player's customize data failed.");
            _customize = null;
        }
        return _customize;
    }

    private readonly Dictionary<string, (bool Enabled, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Settings)?> _currentModSettings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The mod's permanent settings in the Lamina collection (what an unticked part inherits when no earlier pass set it).
    /// Null when the mod is not configured there, the collection is missing, or Penumbra is unavailable. Cached until the next refresh.
    /// </summary>
    public (bool Enabled, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Settings)? CurrentModSettings(string modDirectory)
    {
        if (_currentModSettings.TryGetValue(modDirectory, out var cached))
            return cached;
        (bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>)? result = null;
        try
        {
            if (_s.Penumbra.Available && _s.Engine.CollectionId is { } collection
             && _s.Penumbra.GetCurrentModSettings(collection, modDirectory, ignoreTemporary: true) is { } current)
                result = (current.Enabled, current.Priority, current.Settings);
        }
        catch (Exception ex)
        {
            _s.Log.Debug(ex, "UI: GetCurrentModSettings failed for {Mod}.", modDirectory);
        }
        _currentModSettings[modDirectory] = result;
        return result;
    }

    /// <summary> Forget cached current settings (after the user edits the collection in Penumbra). </summary>
    public void ForgetCurrentModSettings()
        => _currentModSettings.Clear();

    /// <summary> Option groups of a mod, cached per mod until the next refresh. Null when the mod is unknown. </summary>
    public IReadOnlyDictionary<string, (string[] Options, GroupType Type)>? ModSettings(string modDirectory)
    {
        if (_modSettings.TryGetValue(modDirectory, out var cached))
            return cached;
        IReadOnlyDictionary<string, (string[] Options, GroupType Type)>? result = null;
        try
        {
            if (_s.Penumbra.Available)
                result = _s.Penumbra.GetAvailableModSettings(modDirectory);
        }
        catch (Exception ex)
        {
            _s.Log.Debug(ex, "UI: GetAvailableModSettings failed for {Mod}.", modDirectory);
        }
        _modSettings[modDirectory] = result;
        return result;
    }

    public string DesignName(Guid id)
        => DesignNames.TryGetValue(id, out var n) ? n : $"missing design {id.ToString()[..8]}…";

    public string ModName(string dir)
        => ModNames.TryGetValue(dir, out var n) ? n : dir;

    public string VitrineDesignName(Guid id)
        => _s.Vitrine.GetDesign(id)?.Name ?? $"missing Vitrine design {id.ToString()[..8]}…";

    private static string Folder(string fullPath)
    {
        var idx = fullPath.LastIndexOf('/');
        return idx <= 0 ? "/" : fullPath[..idx];
    }
}
