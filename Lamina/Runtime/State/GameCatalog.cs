using Dalamud.Plugin.Services;
using Lamina.Models;
using Lumina.Excel.Sheets;
using WeatherGroup = Lamina.Models.WeatherGroup;

namespace Lamina.Runtime.State;

/// <summary> One territory as the catalog sees it. </summary>
public sealed record TerritoryEntry(uint Id, string Name, uint PlaceNameId, uint RegionPlaceNameId, string RegionName, uint ZonePlaceNameId, string ZoneName, byte IntendedUse);

/// <summary> One class / job as the catalog sees it. </summary>
public sealed record JobEntry(uint Id, string Abbreviation, string Name, JobRole? Role, Discipline Discipline);

/// <summary> One duty as the catalog sees it. </summary>
public sealed record DutyEntry(uint Id, string Name, ContentKind Kind, uint TerritoryId);

/// <summary> One equippable item as the catalog sees it. </summary>
public sealed record EquipItemEntry(uint Id, string Name, EquipSlot Slot, string Category, uint IconId);

/// <summary> One dye as the catalog sees it (Rgb is 0xRRGGBB). </summary>
public sealed record StainEntry(byte Id, string Name, uint Rgb);

/// <summary> One facewear row of the Glasses sheet. </summary>
public sealed record GlassesEntry(ushort Id, string Name, uint IconId);

/// <summary>
/// Game data lookups (Lumina sheets through <see cref="IDataManager"/>) with lazy caches. Used by the
/// <see cref="StateTracker"/> to classify state and by the UI pickers to show names. All lookups return "#id"
/// style placeholders when a row is missing so callers never need null checks for display.
/// </summary>
public sealed class GameCatalog
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;

    private readonly Lazy<IReadOnlyList<TerritoryEntry>> _territories;
    private readonly Lazy<IReadOnlyDictionary<uint, TerritoryEntry>> _territoryById;
    private readonly Lazy<IReadOnlyList<(byte Id, string Name)>> _weathers;
    private readonly Lazy<IReadOnlyDictionary<byte, string>> _weatherById;
    private readonly Lazy<IReadOnlyList<JobEntry>> _jobs;
    private readonly Lazy<IReadOnlyDictionary<uint, JobEntry>> _jobById;
    private readonly Lazy<IReadOnlyList<(uint Id, string Name)>> _onlineStatuses;
    private readonly Lazy<IReadOnlyList<(ushort Id, string Name)>> _emotes;
    private readonly Lazy<IReadOnlyList<(uint Id, string Name)>> _statuses;
    private readonly Lazy<IReadOnlyList<DutyEntry>> _duties;
    private readonly Lazy<IReadOnlyDictionary<uint, DutyEntry>> _dutyById;
    private readonly Lazy<IReadOnlyList<(uint Id, string Name)>> _regions;
    private readonly Lazy<IReadOnlyList<(uint Id, string Name)>> _worlds;
    private readonly Lazy<IReadOnlyList<EquipItemEntry>> _equipItems;
    private readonly Lazy<IReadOnlyDictionary<uint, EquipItemEntry>> _equipItemById;
    private readonly Lazy<IReadOnlyList<StainEntry>> _stains;
    private readonly Lazy<IReadOnlyList<GlassesEntry>> _glasses;
    private readonly Lazy<IReadOnlyDictionary<ushort, GlassesEntry>> _glassesById;

    public GameCatalog(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log  = log;
        Appearance = new AppearanceCatalog(data, log);
        _equipItems    = new(LoadEquipItems);
        _equipItemById = new(() => _equipItems.Value.ToDictionary(i => i.Id));
        _stains        = new(LoadStains);
        _glasses       = new(LoadGlasses);
        _glassesById   = new(() => _glasses.Value.ToDictionary(g => g.Id));

        _territories    = new(LoadTerritories);
        _territoryById  = new(() => _territories.Value.ToDictionary(t => t.Id));
        _weathers       = new(LoadWeathers);
        _weatherById    = new(() => _weathers.Value.ToDictionary(w => w.Id, w => w.Name));
        _jobs           = new(LoadJobs);
        _jobById        = new(() => _jobs.Value.ToDictionary(j => j.Id));
        _onlineStatuses = new(() => LoadNamed<OnlineStatus>(r => r.Name.ExtractText()));
        _emotes         = new(() => LoadNamed<Emote>(r => r.Name.ExtractText()).Select(e => ((ushort)e.Id, e.Name)).ToList());
        _statuses       = new(() => LoadNamed<Status>(r => r.Name.ExtractText()));
        _duties         = new(LoadDuties);
        _dutyById       = new(() => _duties.Value.ToDictionary(d => d.Id));
        _regions        = new(() => _territories.Value.Where(t => t.RegionPlaceNameId != 0).Select(t => (t.RegionPlaceNameId, t.RegionName)).Distinct().OrderBy(r => r.RegionName).ToList());
        _worlds         = new(() => LoadNamed<World>(r => r.Name.ExtractText()).Where(w => w.Id is > 0 and < 1000 && !w.Name.StartsWith('_')).ToList());
    }

    // ---- territories ----------------------------------------------------------------------------

    /// <summary> Every territory with a non-empty place name, sorted by name. </summary>
    public IReadOnlyList<TerritoryEntry> Territories => _territories.Value;

    /// <summary> Region place names (La Noscea, Thanalan, …). </summary>
    public IReadOnlyList<(uint Id, string Name)> Regions => _regions.Value;

    public TerritoryEntry? Territory(uint id)
        => _territoryById.Value.GetValueOrDefault(id);

    public string TerritoryName(uint id)
        => Territory(id)?.Name ?? $"#{id}";

    /// <summary> Housing district for a territory (ward, house, apartment or chambers), or null. </summary>
    public HousingDistrict? HousingDistrictOf(uint territoryId)
        => Territory(territoryId) is { } t ? StaticTables.DistrictForPlaceName(t.Name) : null;

    public bool IsInnRoom(uint territoryId)
        => Territory(territoryId)?.IntendedUse == StaticTables.IntendedUse.InnRoom;

    /// <summary> Default biome for a territory from the shipped table. </summary>
    public static string? DefaultBiome(uint territoryId)
        => StaticTables.DefaultBiomes.GetValueOrDefault(territoryId);

    // ---- weather --------------------------------------------------------------------------------

    public IReadOnlyList<(byte Id, string Name)> Weathers => _weathers.Value;

    public string WeatherName(byte id)
        => _weatherById.Value.GetValueOrDefault(id) ?? $"#{id}";

    public IReadOnlySet<WeatherGroup> WeatherGroups(byte id)
        => _weatherById.Value.TryGetValue(id, out var name) ? StaticTables.WeatherGroupsFor(name) : new HashSet<WeatherGroup>();

    // ---- jobs -----------------------------------------------------------------------------------

    /// <summary> Every class and job (row 0 "Adventurer" excluded), sorted by UI order. </summary>
    public IReadOnlyList<JobEntry> Jobs => _jobs.Value;

    public JobEntry? Job(uint id)
        => _jobById.Value.GetValueOrDefault(id);

    public string JobName(uint id)
        => Job(id)?.Name ?? $"#{id}";

    public string JobAbbreviation(uint id)
        => Job(id)?.Abbreviation ?? $"#{id}";

    // ---- misc sheets ----------------------------------------------------------------------------

    public IReadOnlyList<(uint Id, string Name)> OnlineStatuses => _onlineStatuses.Value;

    public string OnlineStatusName(uint id)
        => _onlineStatuses.Value.FirstOrDefault(s => s.Id == id).Name ?? $"#{id}";

    public IReadOnlyList<(ushort Id, string Name)> Emotes => _emotes.Value;

    public string EmoteName(ushort id)
        => _emotes.Value.FirstOrDefault(e => e.Id == id).Name ?? $"#{id}";

    public IReadOnlyList<(uint Id, string Name)> Statuses => _statuses.Value;

    public string StatusName(uint id)
        => _statuses.Value.FirstOrDefault(s => s.Id == id).Name ?? $"#{id}";

    public IReadOnlyList<(uint Id, string Name)> Worlds => _worlds.Value;

    public string WorldName(uint id)
        => _worlds.Value.FirstOrDefault(w => w.Id == id).Name ?? $"#{id}";

    // ---- equipment, dyes, appearance ------------------------------------------------------------

    /// <summary> Character-creation data for the Appearance layer editor. </summary>
    public AppearanceCatalog Appearance { get; }

    /// <summary> Every equippable item (one Glamourer slot each), sorted by name. </summary>
    public IReadOnlyList<EquipItemEntry> EquipItems => _equipItems.Value;

    public EquipItemEntry? EquipItem(uint id)
        => _equipItemById.Value.GetValueOrDefault(id);

    /// <summary> Display name of an item id, including Glamourer's "Nothing" ids. </summary>
    public string ItemName(ulong id)
        => NothingItems.IsNothing(id) ? "Nothing" : id <= uint.MaxValue && EquipItem((uint)id) is { } e ? e.Name : $"item #{id}";

    /// <summary> Every dye (row 0 = "No Color"), in sheet order. </summary>
    public IReadOnlyList<StainEntry> Stains => _stains.Value;

    /// <summary> Every facewear item, sorted by name. </summary>
    public IReadOnlyList<GlassesEntry> Glasses => _glasses.Value;

    /// <summary> Display name of a bonus item id, including Glamourer's "Nothing". </summary>
    public string BonusItemName(ulong id)
        => BonusItems.IsNothing(id) ? "Nothing"
         : BonusItems.RowOf(id) is { } row && _glassesById.Value.GetValueOrDefault(row) is { } g ? g.Name
         : $"facewear #{id}";

    public string StainName(byte id)
        => id == 0 ? "No dye" : _stains.Value.FirstOrDefault(s => s.Id == id)?.Name ?? $"#{id}";

    // ---- duties ---------------------------------------------------------------------------------

    public IReadOnlyList<DutyEntry> Duties => _duties.Value;

    public DutyEntry? Duty(uint contentFinderConditionId)
        => _dutyById.Value.GetValueOrDefault(contentFinderConditionId);

    public string DutyName(uint contentFinderConditionId)
        => Duty(contentFinderConditionId)?.Name ?? $"#{contentFinderConditionId}";

    public ContentKind ContentKindOf(uint contentFinderConditionId)
        => Duty(contentFinderConditionId)?.Kind ?? ContentKind.Other;

    // ---- loading --------------------------------------------------------------------------------

    private IReadOnlyList<TerritoryEntry> LoadTerritories()
    {
        var result = new List<TerritoryEntry>();
        try
        {
            var sheet = _data.GetExcelSheet<TerritoryType>();
            if (sheet is null)
                return result;
            foreach (var row in sheet)
            {
                var name = row.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var region = row.PlaceNameRegion.ValueNullable?.Name.ExtractText() ?? string.Empty;
                var zone   = row.PlaceNameZone.ValueNullable?.Name.ExtractText() ?? string.Empty;
                result.Add(new TerritoryEntry(row.RowId, name, row.PlaceName.RowId, row.PlaceNameRegion.RowId, region, row.PlaceNameZone.RowId, zone, (byte)row.TerritoryIntendedUse.RowId));
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Lamina: failed to load TerritoryType sheet.");
        }

        return result.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Id).ToList();
    }

    private IReadOnlyList<(byte Id, string Name)> LoadWeathers()
        => LoadNamed<Weather>(r => r.Name.ExtractText()).Select(w => ((byte)w.Id, w.Name)).ToList();

    private IReadOnlyList<JobEntry> LoadJobs()
    {
        var result = new List<(JobEntry Entry, byte Priority)>();
        try
        {
            var sheet = _data.GetExcelSheet<ClassJob>();
            if (sheet is null)
                return Array.Empty<JobEntry>();
            foreach (var row in sheet)
            {
                if (row.RowId == 0)
                    continue;
                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var role = StaticTables.RoleFor(row.RowId, row.Role, row.DohDolJobIndex, row.ClassJobCategory.RowId);
                var disc = StaticTables.DisciplineFor(row.RowId, role);
                result.Add((new JobEntry(row.RowId, row.Abbreviation.ExtractText(), name, role, disc), row.UIPriority));
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Lamina: failed to load ClassJob sheet.");
        }

        return result.OrderBy(r => r.Priority).ThenBy(r => r.Entry.Id).Select(r => r.Entry).ToList();
    }

    private IReadOnlyList<DutyEntry> LoadDuties()
    {
        var result = new List<DutyEntry>();
        try
        {
            var sheet = _data.GetExcelSheet<ContentFinderCondition>();
            if (sheet is null)
                return result;
            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var typeName = row.ContentType.ValueNullable?.Name.ExtractText() ?? string.Empty;
                var kind     = StaticTables.ContentKindFor(typeName, row.PvP, row.AllianceRoulette, row.HighEndDuty);
                result.Add(new DutyEntry(row.RowId, name, kind, row.TerritoryType.RowId));
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Lamina: failed to load ContentFinderCondition sheet.");
        }

        return result.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IReadOnlyList<EquipItemEntry> LoadEquipItems()
    {
        var result = new List<EquipItemEntry>();
        try
        {
            var sheet = _data.GetExcelSheet<Item>();
            if (sheet is null)
                return result;
            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name) || SlotOf(row) is not { } slot)
                    continue;
                var category = row.ItemUICategory.ValueNullable?.Name.ExtractText() ?? string.Empty;
                result.Add(new EquipItemEntry(row.RowId, name, slot, category, row.Icon));
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Lamina: failed to load the Item sheet.");
        }

        return result.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id).ToList();
    }

    /// <summary> The Glamourer slot an item goes into, or null when it is not equippable through Glamourer (waist, soul crystal, non-gear). </summary>
    private static EquipSlot? SlotOf(in Item item)
    {
        if (item.EquipSlotCategory.ValueNullable is not { } c)
            return null;
        if (c.MainHand != 0) return EquipSlot.MainHand;
        if (c.OffHand != 0)  return EquipSlot.OffHand;
        if (c.Head != 0)     return EquipSlot.Head;
        if (c.Body != 0)     return EquipSlot.Body;
        if (c.Gloves != 0)   return EquipSlot.Hands;
        if (c.Legs != 0)     return EquipSlot.Legs;
        if (c.Feet != 0)     return EquipSlot.Feet;
        if (c.Ears != 0)     return EquipSlot.Ears;
        if (c.Neck != 0)     return EquipSlot.Neck;
        if (c.Wrists != 0)   return EquipSlot.Wrists;
        if (c.FingerR != 0)  return EquipSlot.RFinger;
        if (c.FingerL != 0)  return EquipSlot.LFinger;
        return null;
    }

    private IReadOnlyList<GlassesEntry> LoadGlasses()
    {
        var result = new List<GlassesEntry>();
        try
        {
            var sheet = _data.GetExcelSheet<Lumina.Excel.Sheets.Glasses>();
            if (sheet is null)
                return result;
            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (row.RowId == 0 || row.RowId > ushort.MaxValue || string.IsNullOrWhiteSpace(name))
                    continue;
                result.Add(new GlassesEntry((ushort)row.RowId, name, (uint)row.Icon));
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Lamina: failed to load the Glasses sheet.");
        }

        return result.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id).ToList();
    }

    private IReadOnlyList<StainEntry> LoadStains()
    {
        var result = new List<StainEntry>();
        try
        {
            var sheet = _data.GetExcelSheet<Stain>();
            if (sheet is null)
                return result;
            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (row.RowId > byte.MaxValue || string.IsNullOrWhiteSpace(name))
                    continue;
                result.Add(new StainEntry((byte)row.RowId, name, row.Color));
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Lamina: failed to load the Stain sheet.");
        }

        return result;
    }

    private IReadOnlyList<(uint Id, string Name)> LoadNamed<T>(Func<T, string> nameOf) where T : struct, Lumina.Excel.IExcelRow<T>
    {
        var result = new List<(uint, string)>();
        try
        {
            var sheet = _data.GetExcelSheet<T>();
            if (sheet is null)
                return result;
            foreach (var row in sheet)
            {
                var name = nameOf(row);
                if (!string.IsNullOrWhiteSpace(name))
                    result.Add((row.RowId, name));
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"Lamina: failed to load {typeof(T).Name} sheet.");
        }

        return result;
    }
}
