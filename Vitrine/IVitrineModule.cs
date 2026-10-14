namespace Vitrine;

/// <summary> What a mod changes, as Vitrine classifies it from Penumbra's changed items. </summary>
public enum VitrineCategory
{
    Unknown,
    Gear,
    Weapon,
    Accessory,
    Hair,
    Face,
    Body,
    Skin,
    Tail,
    Ears,
    Animation,
    Emote,
    Vfx,
    Sfx,
    Ui,
    Minion,
    Mount,
    Housing,
    Npc,
    Other,
}

/// <summary> Affected item entry on a ModInformation front box. </summary>
public sealed record AffectedItem(VitrineCategory Category, string Name, ulong? ItemId);

/// <summary> Mod state as a Vitrine design wants it. Converted to Lamina's ModState by the compositor. </summary>
public sealed class VitrineModState
{
    public string ModDirectory { get; set; } = string.Empty;
    public string ModName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    /// <summary> Option group → selected option names. Null = mod defaults. </summary>
    public Dictionary<string, List<string>>? Settings { get; set; }
}

/// <summary>
/// A design produced by Vitrine. Auto designs are created from every mod that changes in game things (one per mod, IsAuto = true).
/// Variants are user created from an auto design with different settings (IsAuto = false, BaseDesignId set).
/// Referenced by Lamina through VitrineDesignLayer.VitrineDesignId.
/// </summary>
public sealed class VitrineDesign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IsAuto { get; set; }
    public Guid? BaseDesignId { get; set; }
    public VitrineCategory Category { get; set; }
    /// <summary> Usually one mod (the design's own). </summary>
    public List<VitrineModState> Mods { get; set; } = new();
}

/// <summary> Read focused view of one Penumbra mod (TDD 12.2). </summary>
public sealed class ModInformation
{
    public string ModDirectory { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public VitrineCategory Type { get; set; }
    public List<AffectedItem> AffectedItems { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    /// <summary> Option group → (options, group type as Penumbra reports it). </summary>
    public Dictionary<string, (string[] Options, int GroupType)> AvailableSettings { get; set; } = new();
    /// <summary> Collection id → (enabled, priority, settings). Informational. </summary>
    public Dictionary<Guid, (bool Enabled, int Priority, Dictionary<string, List<string>> Settings)> PerCollection { get; set; } = new();
    /// <summary> Collection currently activating this mod for the local player, if any. </summary>
    public Guid? CurrentActiveCollection { get; set; }
}

/// <summary> What Vitrine needs from its host (Lamina). Lets Vitrine become its own plugin later. </summary>
public interface IVitrineHost
{
    /// <summary> Directory Vitrine may write its cache and designs into (…/pluginConfigs/Lamina/vitrine). </summary>
    string DataDirectory { get; }

    /// <summary> The Lamina collection id, when found. Vitrine only ever writes into this collection. </summary>
    Guid? LaminaCollectionId { get; }

    /// <summary> Penumbra temporary-settings key shared with Lamina so the clean slate removes Vitrine's writes too. </summary>
    int TemporaryKey { get; }

    /// <summary> Source string for temporary settings. </summary>
    string TemporarySource { get; }

    /// <summary> Ask Lamina to re-evaluate and reapply (after designs changed). </summary>
    void RequestReapply();

    /// <summary> True when the host can equip items on the local player (Glamourer available and a player exists). </summary>
    bool CanTryOn { get; }

    /// <summary> Equips an item on the local player through Glamourer so a gear mod can be seen. Returns a message for the UI. </summary>
    string TryOnItem(ulong itemId);

    /// <summary> Undoes try-ons: re-asserts Lamina's design, or reverts the Glamourer state when Lamina holds nothing. </summary>
    string RevertTryOn();
}

/// <summary> Internal API between Lamina and Vitrine. Read designs, open the window, listen for changes. </summary>
public interface IVitrineModule : IDisposable
{
    /// <summary> False while Penumbra is unavailable. </summary>
    bool Available { get; }

    /// <summary> All designs (auto + variants), stable order. </summary>
    IReadOnlyList<VitrineDesign> Designs { get; }

    VitrineDesign? GetDesign(Guid id);

    /// <summary> Full catalog. Empty until the first scan finished. </summary>
    IReadOnlyList<ModInformation> Mods { get; }

    /// <summary> Raised on the framework thread when designs or the catalog changed. </summary>
    event Action? CatalogChanged;

    /// <summary> Rescan Penumbra's mod list. </summary>
    void Refresh();

    /// <summary> Open / toggle the Vitrine window (/vit). </summary>
    void ToggleWindow();

    /// <summary> Draw hook: Lamina calls this from its UiBuilder.Draw. </summary>
    void Draw();
}
