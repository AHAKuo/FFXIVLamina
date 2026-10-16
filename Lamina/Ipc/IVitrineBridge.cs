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

/// <summary>
/// One hair / face / tail / ears part a Vitrine design's mod replaces (IPC 1.4 <c>customizations</c>). Enabling such a mod
/// shows nothing unless the character's customize value is the replaced one, so the resolver sets the entry fitting the player.
/// </summary>
public sealed class VitrineCustomizationInfo
{
    /// <summary> Model race name as Vitrine spells it (Midlander, Highlander, Elezen, Lalafell, Miqote, Roegadyn, AuRa, Hrothgar, Viera). </summary>
    [JsonProperty("race")]
    public string Race { get; set; } = string.Empty;

    /// <summary> 0 male, 1 female (game convention). </summary>
    [JsonProperty("gender")]
    public int Gender { get; set; }

    /// <summary> Glamourer customize key (Hairstyle, Face, TailShape). </summary>
    [JsonProperty("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary> The customize value that shows the part. </summary>
    [JsonProperty("value")]
    public int Value { get; set; }

    /// <summary> Penumbra's affected-item name, informational. </summary>
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The customize index the key names. Null for keys this version does not know and for the identity fields (race, gender,
    /// clan, body type), which are never a part a mod replaces.
    /// </summary>
    public CustomizeIndex? Index
        => Enum.TryParse<CustomizeIndex>(Key, true, out var index)
        && index is not (CustomizeIndex.Race or CustomizeIndex.Gender or CustomizeIndex.Clan or CustomizeIndex.BodyType)
            ? index
            : null;

    /// <summary> True when the part is made for a character of this clan (1..16) and gender (0 male, 1 female). </summary>
    public bool Matches(byte clan, byte gender)
        => Gender == gender && string.Equals(Race, ModelRaceOfClan(clan), StringComparison.OrdinalIgnoreCase);

    /// <summary> "Hair 194", "Face 3", "Tail 2". </summary>
    public string ShortLabel
        => Index switch
        {
            CustomizeIndex.Hairstyle => $"Hair {Value}",
            CustomizeIndex.Face      => $"Face {Value}",
            CustomizeIndex.TailShape => $"Tail {Value}",
            _                        => $"{Key} {Value}",
        };

    /// <summary> "Midlander female", "Au Ra male". </summary>
    public string Audience
        => $"{RaceDisplayName(Race)} {(Gender == 0 ? "male" : "female")}";

    /// <summary> Vitrine's model race name of a clan id (customize byte 4, 1..16); null outside that range. </summary>
    public static string? ModelRaceOfClan(int clan)
        => clan switch
        {
            1        => "Midlander",
            2        => "Highlander",
            3 or 4   => "Elezen",
            5 or 6   => "Lalafell",
            7 or 8   => "Miqote",
            9 or 10  => "Roegadyn",
            11 or 12 => "AuRa",
            13 or 14 => "Hrothgar",
            15 or 16 => "Viera",
            _        => null,
        };

    private static string RaceDisplayName(string race)
        => race.ToLowerInvariant() switch
        {
            "aura"   => "Au Ra",
            "miqote" => "Miqo'te",
            _        => race,
        };
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

    /// <summary>
    /// Every hair / face / tail / ears part the design's mod replaces, for every model race and gender it ships (IPC 1.4).
    /// Null or empty from older Vitrine versions and for mods without customizations.
    /// </summary>
    [JsonProperty("customizations")]
    public List<VitrineCustomizationInfo>? Customizations { get; set; }

    /// <summary> True when the design carries at least one customization this version can apply. </summary>
    public bool HasCustomizations
        => Customizations?.Any(c => c?.Index is not null) ?? false;

    /// <summary> The design's customizations this version can apply, with their customize index. </summary>
    public IEnumerable<(CustomizeIndex Index, VitrineCustomizationInfo Entry)> ResolvedCustomizations()
    {
        if (Customizations is null)
            yield break;
        foreach (var entry in Customizations)
            if (entry?.Index is { } index)
                yield return (index, entry);
    }

    /// <summary>
    /// The customizations fitting a character of this clan (1..16) and gender (0 male, 1 female): the first entry per customize
    /// index, as Vitrine's own try-on applies them. Empty for an unknown clan.
    /// </summary>
    public IEnumerable<(CustomizeIndex Index, VitrineCustomizationInfo Entry)> MatchingCustomizations(byte clan, byte gender)
    {
        var seen = new HashSet<CustomizeIndex>();
        foreach (var (index, entry) in ResolvedCustomizations())
            if (entry.Matches(clan, gender) && seen.Add(index))
                yield return (index, entry);
    }

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
