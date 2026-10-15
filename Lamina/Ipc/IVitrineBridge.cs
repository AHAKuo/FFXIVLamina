using Lamina.Models;
using Newtonsoft.Json;

namespace Lamina.Ipc;

/// <summary> One mod inside a Vitrine design, as Vitrine publishes it over IPC (see vitrine/docs/IPC.md). </summary>
public sealed class VitrineModInfo
{
    /// <summary> Penumbra mod directory (the stable identifier). </summary>
    [JsonProperty("modDirectory")]
    public string ModDirectory { get; set; } = string.Empty;

    /// <summary> Display name, informational. </summary>
    [JsonProperty("modName")]
    public string ModName { get; set; } = string.Empty;

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonProperty("priority")]
    public int Priority { get; set; }

    /// <summary> Option group → selected options. Null = Penumbra's defaults for the mod. </summary>
    [JsonProperty("settings")]
    public Dictionary<string, List<string>>? Settings { get; set; }
}

/// <summary> One piece a Vitrine design equips (IPC 1.2 `pieces`). </summary>
public sealed class VitrinePieceInfo
{
    [JsonProperty("itemId")]
    public ulong ItemId { get; set; }

    [JsonProperty("itemName")]
    public string ItemName { get; set; } = string.Empty;
}

/// <summary> A Vitrine design (a mod with baseline settings, or a user variant), as published over IPC. </summary>
public sealed class VitrineDesignInfo
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary> Vitrine category name (Gear, Hair, Emote, ...). Kept as text so new categories never break parsing. </summary>
    [JsonProperty("category")]
    public string Category { get; set; } = "Unknown";

    [JsonProperty("isAuto")]
    public bool IsAuto { get; set; }

    [JsonProperty("baseDesignId")]
    public Guid? BaseDesignId { get; set; }

    [JsonProperty("mods")]
    public List<VitrineModInfo> Mods { get; set; } = new();

    /// <summary>
    /// What the design equips, resolved by Vitrine: Glamourer slot name (Head, Body, ...) → item, one per slot the design
    /// affects (IPC 1.2). Null or empty from older Vitrine versions or for mods without equippable items.
    /// </summary>
    [JsonProperty("pieces")]
    public Dictionary<string, VitrinePieceInfo>? Pieces { get; set; }

    /// <summary> The design empties every gear slot its pieces do not fill (IPC 1.3). Weapons are never touched. </summary>
    [JsonProperty("clearOtherSlots")]
    public bool ClearOtherSlots { get; set; }

    /// <summary> The design's pieces keyed by slot, skipping names this version does not know. </summary>
    public IEnumerable<(EquipSlot Slot, VitrinePieceInfo Piece)> ResolvedPieces()
    {
        if (Pieces is null)
            yield break;
        foreach (var (name, piece) in Pieces)
            if (piece is not null && Enum.TryParse<EquipSlot>(name, true, out var slot))
                yield return (slot, piece);
    }
}

/// <summary>
/// Vitrine integration (optional plugin, reached over Dalamud IPC). Lamina references Vitrine designs by id through
/// <see cref="Models.VitrineDesignLayer"/>; this bridge supplies the designs and tells Lamina when they change.
/// </summary>
public interface IVitrineBridge : IPluginBridge
{
    /// <summary> Every design Vitrine knows, cached; empty while Vitrine is unavailable. </summary>
    IReadOnlyList<VitrineDesignInfo> Designs { get; }

    /// <summary> Looks a design up by id in the cache. Null when unknown or Vitrine is unavailable. </summary>
    VitrineDesignInfo? GetDesign(Guid id);

    /// <summary> Raised on the framework thread after the design cache was refreshed from Vitrine. </summary>
    event Action? DesignsChanged;

    /// <summary> Opens (true), closes (false) or toggles (null) the Vitrine window. No-op when unavailable. </summary>
    void OpenWindow(bool? open);

    /// <summary> Asks Vitrine to preview a design on the local player. Returns Vitrine's status message, or a reason when unavailable. </summary>
    string PreviewDesign(Guid id);

    /// <summary> Re-reads the design list from Vitrine (used after /lam reapply and by the UI refresh). </summary>
    void RefreshDesigns();
}
