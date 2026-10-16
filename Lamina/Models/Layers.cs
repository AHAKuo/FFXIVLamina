using System.Numerics;
using Lamina.Decals;
using Newtonsoft.Json;

namespace Lamina.Models;

/// <summary> One contribution inside a pass. Serialized polymorphically with a "kind" field equal to <see cref="Kind"/>. </summary>
public abstract class Layer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public bool Enabled { get; set; } = true;

    /// <summary> UI state: the layer card shows only its header row. Saved with the profile; not part of the design. </summary>
    public bool Collapsed { get; set; }

    /// <summary> The "kind" discriminator used in JSON. Stable, never renamed. </summary>
    public abstract string Kind { get; }

    /// <summary> One line summary for the UI. Resolved names (design names etc.) are looked up by the UI, not here. </summary>
    public abstract string Describe();
}

/// <summary> Kept when a file from a newer version contains a kind this build does not know. Skipped at runtime. </summary>
public sealed class UnknownLayer : Layer
{
    public override string Kind => UnknownKind;

    public string UnknownKind { get; set; } = "Unknown";

    public string RawJson { get; set; } = "{}";

    public override string Describe() => $"Unsupported layer ({UnknownKind})";
}

/// <summary> A Vitrine design (auto created from a Penumbra mod, or a user variant), referenced by Id. </summary>
public sealed class VitrineDesignLayer : Layer
{
    public const string KindName = "Vitrine";
    public override string Kind => KindName;

    public Guid VitrineDesignId { get; set; }

    /// <summary> Option group → selected options. Overrides the design's own settings when set. </summary>
    public Dictionary<string, List<string>>? SettingsOverride { get; set; }

    /// <summary>
    /// Which of the design's pieces this layer equips. Null = every slot the design affects. A mask lets two Vitrine
    /// designs combine (the body of one mod, the feet of another); the mods themselves are always applied in full.
    /// </summary>
    public EquipSlotFlags? SlotOverride { get; set; }

    /// <summary>
    /// Whether the layer empties every gear slot it does not fill before its pieces land. Null follows the design's own
    /// <c>clearOtherSlots</c> flag; true / false override it for this layer.
    /// </summary>
    public bool? ClearOtherSlotsOverride { get; set; }

    /// <summary>
    /// Also sets the design's hair / face / tail value for the character: the entry of the design's customizations made for
    /// the player's clan and gender, since a hair mod shows nothing unless that customize value is selected. Default on;
    /// layers saved before the field existed gain the behaviour.
    /// </summary>
    public bool ApplyCustomizations { get; set; } = true;

    public override string Describe() => $"Vitrine design {VitrineDesignId}";
}

/// <summary>
/// Empties every gear slot (head to rings; weapons are left alone) so later layers of the pass start from a bare body
/// instead of inheriting whatever outfit was worn before. Put it before the outfit layers.
/// </summary>
public sealed class EquipmentCleanseLayer : Layer
{
    public const string KindName = "EquipmentCleanse";
    public override string Kind => KindName;

    public override string Describe() => "Equipment cleanse";
}

/// <summary> A Glamourer design by Guid. Its mod associations carry over as is. </summary>
public sealed class GlamourerLayer : Layer
{
    public const string KindName = "Glamourer";
    public override string Kind => KindName;

    public Guid DesignId { get; set; }

    public override string Describe() => $"Glamourer design {DesignId}";
}

/// <summary>
/// Direct control of Penumbra mods in the Lamina collection. Every part of a <see cref="ModState"/> (enabled flag, priority,
/// each option group) is either set by this layer or inherited: from the mod state an earlier pass already put into the
/// running design, else from the mod's effective settings in the Lamina collection (TDD 5.7, per-group merge).
/// </summary>
public sealed class PenumbraAdjustLayer : Layer
{
    public const string KindName = "PenumbraAdjust";
    public override string Kind => KindName;

    public List<ModState> Mods { get; set; } = new();

    public override string Describe() => $"Penumbra adjust ({Mods.Count} mods)";
}

/// <summary>
/// Native equipment edits written straight into the virtual design: per slot an item ("Nothing" allowed for gear slots),
/// a dye pair ("keep item, change dyes only" when no item is set) and an FC crest toggle, plus the hat / visor / weapon /
/// viera ears visibility toggles. Unset slots pass through. Cascades and masks like a Glamourer design.
/// </summary>
public sealed class EquipmentLayer : Layer
{
    public const string KindName = "Equipment";
    public override string Kind => KindName;

    /// <summary> Slot → edit. Only slots present are touched. </summary>
    public Dictionary<EquipSlot, SlotEdit> Slots { get; set; } = new();

    /// <summary> Bonus slot (facewear) → Glamourer bonus item id (see <see cref="BonusItems"/>). Only slots present are touched. </summary>
    public Dictionary<BonusSlot, ulong> Bonus { get; set; } = new();

    /// <summary> Visibility toggles (HatVisible, VisorToggled, WeaponVisible, VieraEarsVisible). Wetness belongs to the Appearance layer. </summary>
    public Dictionary<MetaIndex, bool> Meta { get; set; } = new();

    public override string Describe()
        => $"Equipment ({Slots.Count + Bonus.Count} slot(s){(Meta.Count > 0 ? $", {Meta.Count} toggle(s)" : string.Empty)})";
}

/// <summary> One slot of an <see cref="EquipmentLayer"/>. Each part is optional. </summary>
public sealed class SlotEdit
{
    /// <summary> Game item id, or Glamourer's "Nothing" id for the slot (see <see cref="NothingItems"/>). Null = keep the current item. </summary>
    public ulong? ItemId { get; set; }

    /// <summary> When true, <see cref="Stain1"/> / <see cref="Stain2"/> are applied (to the set item, or to whatever item is in the slot). </summary>
    public bool ApplyStains { get; set; }

    public byte Stain1 { get; set; }

    public byte Stain2 { get; set; }

    /// <summary> FC crest on / off. Only Head, Body and OffHand carry crests. Null = keep. </summary>
    public bool? Crest { get; set; }

    public bool IsEmpty
        => ItemId is null && !ApplyStains && Crest is null;
}

/// <summary>
/// Glamourer's custom item ids for an empty slot: <c>uint.MaxValue - 128 - penumbraSlotIndex</c>, where the index is
/// Penumbra's EquipSlot value (Head 3, Body 4, Hands 5, Legs 7, Feet 8, Ears 9, Neck 10, Wrists 11, both rings 12).
/// Weapons have no fixed "Nothing" id (it depends on the weapon type), so they are not offered one.
/// </summary>
public static class NothingItems
{
    public static ulong? IdFor(EquipSlot slot)
        => slot switch
        {
            EquipSlot.Head    => uint.MaxValue - 128 - 3,
            EquipSlot.Body    => uint.MaxValue - 128 - 4,
            EquipSlot.Hands   => uint.MaxValue - 128 - 5,
            EquipSlot.Legs    => uint.MaxValue - 128 - 7,
            EquipSlot.Feet    => uint.MaxValue - 128 - 8,
            EquipSlot.Ears    => uint.MaxValue - 128 - 9,
            EquipSlot.Neck    => uint.MaxValue - 128 - 10,
            EquipSlot.Wrists  => uint.MaxValue - 128 - 11,
            EquipSlot.RFinger => uint.MaxValue - 128 - 12,
            EquipSlot.LFinger => uint.MaxValue - 128 - 12,
            _                 => null,
        };

    /// <summary> True for any of the ids above (any slot). </summary>
    public static bool IsNothing(ulong itemId)
        => itemId is >= uint.MaxValue - 128 - 14 and <= uint.MaxValue - 128;
}

/// <summary>
/// Glamourer's ids for bonus items (facewear), from Penumbra.GameData's CustomItemId: a real pair of glasses is its
/// Glasses sheet row with bit 49 set; "Nothing" is a custom bonus item (bits 48 and 49) with model 0 and the slot flag
/// (Glasses = 1) in bits 24..31.
/// </summary>
public static class BonusItems
{
    private const ulong CustomFlag = 1ul << 48;
    private const ulong BonusFlag  = 1ul << 49;

    private static ulong SlotFlag(BonusSlot slot)
        => slot switch { BonusSlot.Glasses => 1, _ => 0 };

    /// <summary> Bonus item id of a Glasses sheet row. </summary>
    public static ulong IdFor(ushort glassesRow)
        => glassesRow | BonusFlag;

    /// <summary> Glamourer's "Nothing" for the slot. </summary>
    public static ulong NothingFor(BonusSlot slot)
        => CustomFlag | BonusFlag | (SlotFlag(slot) << 24);

    public static bool IsNothing(ulong id)
        => (id & (CustomFlag | BonusFlag)) == (CustomFlag | BonusFlag) && (id & 0xFFFFFF) == 0;

    /// <summary> The Glasses sheet row of a bonus item id, or null for custom ids ("Nothing") and plain item ids. </summary>
    public static ushort? RowOf(ulong id)
        => (id & BonusFlag) != 0 && (id & CustomFlag) == 0 ? (ushort)(id & 0xFFFF) : null;
}

/// <summary>
/// Native appearance edits written straight into the virtual design: customization values (hair, colours, face, shapes,
/// height ...), wetness, and Glamourer's advanced parameters (exact RGB colours, muscle tone ...). Unset fields pass through.
/// Values use Glamourer's conventions: faces and tails 1-based, shape lists 0-based, flag fields 0 / 128, facial feature
/// N = bit N-1, lip / face paint colours 0-95 (dark) or 128-223 (light).
/// </summary>
public sealed class AppearanceLayer : Layer
{
    public const string KindName = "Appearance";
    public override string Kind => KindName;

    /// <summary> Customization index → value. Only indices present are applied. </summary>
    public Dictionary<CustomizeIndex, int> Customize { get; set; } = new();

    /// <summary> Wet look on / off. Null = keep. </summary>
    public bool? Wetness { get; set; }

    /// <summary> Advanced parameter key (Glamourer "Parameters" name) → value. Only keys present are applied. </summary>
    public Dictionary<string, AdvancedParameter> Parameters { get; set; } = new(StringComparer.Ordinal);

    public override string Describe()
        => $"Appearance ({Customize.Count + (Wetness is null ? 0 : 1) + Parameters.Count} field(s))";
}

/// <summary> Value of one Glamourer advanced parameter. Which members matter depends on the parameter's kind (<see cref="AdvancedParameters"/>). </summary>
public sealed class AdvancedParameter
{
    /// <summary> Linear RGB in 0..1 (may exceed 1 for glow). Used by Rgb and Rgba parameters. </summary>
    public Vector3 Color { get; set; } = Vector3.One;

    /// <summary> Alpha for Rgba parameters. </summary>
    public float Alpha { get; set; } = 1f;

    /// <summary> Percentage (0..1) or plain value, for Percentage / Value parameters. </summary>
    public float Scalar { get; set; }
}

public enum AdvancedParameterKind
{
    Rgb,
    Rgba,
    Percentage,
    Value,
}

/// <summary> The advanced parameters Glamourer exposes, with the JSON key it stores them under. </summary>
public static class AdvancedParameters
{
    public static readonly IReadOnlyList<(string Key, string DisplayName, AdvancedParameterKind Kind)> All = new List<(string, string, AdvancedParameterKind)>
    {
        ("SkinDiffuse",           "Skin colour",                      AdvancedParameterKind.Rgb),
        ("MuscleTone",            "Muscle tone",                      AdvancedParameterKind.Percentage),
        ("LipDiffuse",            "Lip colour",                       AdvancedParameterKind.Rgba),
        ("HairDiffuse",           "Hair colour",                      AdvancedParameterKind.Rgb),
        ("HairHighlight",         "Hair highlights",                  AdvancedParameterKind.Rgb),
        ("LeftEye",               "Left eye colour",                  AdvancedParameterKind.Rgb),
        ("RightEye",              "Right eye colour",                 AdvancedParameterKind.Rgb),
        ("FeatureColor",          "Feature colour (tattoo / limbal)", AdvancedParameterKind.Rgb),
        ("FacePaintUvMultiplier", "Face paint multiplier",            AdvancedParameterKind.Value),
        ("FacePaintUvOffset",     "Face paint offset",                AdvancedParameterKind.Value),
        ("DecalColor",            "Face paint colour",                AdvancedParameterKind.Rgba),
        ("LeftLimbalIntensity",   "Left limbal ring intensity",       AdvancedParameterKind.Percentage),
        ("RightLimbalIntensity",  "Right limbal ring intensity",      AdvancedParameterKind.Percentage),
    };

    public static AdvancedParameterKind? KindOf(string key)
    {
        foreach (var (k, _, kind) in All)
            if (k == key)
                return kind;
        return null;
    }
}

/// <summary>
/// A PNG projected onto the player's skin (TDD decal layer): the image is baked into the body or face diffuse texture at a
/// rest-pose placement picked by clicking the character, and the result is redirected in the Lamina collection with a
/// temporary Penumbra mod. Only visible where skin is drawn (gear covers it).
/// </summary>
public sealed class DecalLayer : Layer
{
    public const string KindName = "Decal";
    public override string Kind => KindName;

    /// <summary> File name of the image inside Lamina's decal image folder (the UI copies the picked file there). Null = nothing chosen. </summary>
    public string? ImageFile { get; set; }

    /// <summary> Where the decal sits. Null = not placed yet (the layer contributes nothing). </summary>
    public DecalPlacement? Placement { get; set; }

    /// <summary> 0..1. </summary>
    public float Opacity { get; set; } = 1f;

    public bool IsComplete
        => !string.IsNullOrEmpty(ImageFile) && Placement is not null;

    public override string Describe()
        => IsComplete ? $"Decal {ImageFile} on {Placement!.Surface}" : "Decal (not placed)";
}

public sealed class HonorificLayer : Layer
{
    public const string KindName = "Honorific";
    public override string Kind => KindName;

    /// <summary> When true the pass clears the title instead of setting one. </summary>
    public bool Clear { get; set; }

    public string? Title { get; set; }

    public bool IsPrefix { get; set; }

    public Vector3? Color { get; set; }

    public Vector3? Glow { get; set; }

    public override string Describe() => Clear ? "Honorific: clear" : $"Honorific: {Title}";
}

public sealed class MoodlesLayer : Layer
{
    public const string KindName = "Moodles";
    public override string Kind => KindName;

    /// <summary> Moodle Guids to add. </summary>
    public List<Guid> Add { get; set; } = new();

    /// <summary> Preset Guids to apply. </summary>
    public List<Guid> AddPresets { get; set; } = new();

    /// <summary> Moodle Guids to remove. </summary>
    public List<Guid> Remove { get; set; } = new();

    public override string Describe() => $"Moodles (+{Add.Count + AddPresets.Count} / -{Remove.Count})";
}

/// <summary>
/// A Customize+ profile, referenced by its unique id. Its bone edits are read over IPC and applied to the player as one
/// temporary Customize+ profile (nothing is written to the Customize+ configuration); bones of several layers merge by name.
/// </summary>
public sealed class CustomizePlusLayer : Layer
{
    public const string KindName = "CustomizePlus";
    public override string Kind => KindName;

    public Guid ProfileId { get; set; }

    /// <summary> Display name at pick time, informational only (shown when the profile is missing). </summary>
    public string? ProfileName { get; set; }

    public override string Describe() => $"Customize+ profile {ProfileName ?? ProfileId.ToString()}";
}

/// <summary>
/// A Simple Heels height offset registered for the player over IPC while the pass matches. <see cref="Relative"/> adds the
/// offset to the player's own resolved Simple Heels offset (their per-footwear setting) instead of replacing it.
/// </summary>
public sealed class SimpleHeelsLayer : Layer
{
    public const string KindName = "SimpleHeels";
    public override string Kind => KindName;

    /// <summary> Height offset in game units (metres); positive raises the character. </summary>
    public float Offset { get; set; }

    /// <summary> Add to the player's current Simple Heels offset instead of replacing it. Default on. </summary>
    public bool Relative { get; set; } = true;

    public override string Describe() => $"Simple Heels {(Relative && Offset >= 0 ? "+" : string.Empty)}{Offset:0.###}";
}

/// <summary>
/// State of one Penumbra mod inside the Lamina collection, keyed by mod directory. Every part is optional ("applies"
/// checkboxes in the editor): null / absent = inherited from the running design or the collection, not set by this entry.
/// </summary>
public sealed class ModState
{
    /// <summary> Penumbra mod directory name (the stable identifier). </summary>
    public string ModDirectory { get; set; } = string.Empty;

    /// <summary> Display name, informational only (helps the user when the mod is missing). </summary>
    public string? ModName { get; set; }

    /// <summary>
    /// Enable / disable the mod. Null = inherit. Always written (even when null) so an inherited state survives a save;
    /// an absent property (files written before the "applies" semantics) means Enable, as it did then.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public ModToggle? State { get; set; } = ModToggle.Enable;

    /// <summary> Priority. Null = inherit (the collection's priority, 0 when the mod is not configured there). </summary>
    public int? Priority { get; set; }

    /// <summary> Option group name → enabled option names, for the groups this entry sets. Groups not listed are inherited. </summary>
    public Dictionary<string, List<string>>? Settings { get; set; }

    /// <summary> True when the entry sets nothing at all. </summary>
    public bool IsEmpty
        => State is null && Priority is null && (Settings is null || Settings.Count == 0);

    public ModState Clone()
        => new()
        {
            ModDirectory = ModDirectory,
            ModName      = ModName,
            State        = State,
            Priority     = Priority,
            Settings     = Settings?.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
        };
}

/// <summary> Macro Override: restricts what a pass may change. A layer is filtered through its mask before it merges. </summary>
public sealed class ApplyMask
{
    public EquipSlotFlags Equipment { get; set; } = EquipSlotFlags.All;

    public CustomizeFlags Customization { get; set; } = CustomizeFlags.All;

    /// <summary> Hat / visor / weapon / viera ears / wetness. </summary>
    public bool Meta { get; set; } = true;

    /// <summary> Advanced customizations (Glamourer "Parameters") and advanced dyes ("Materials"). </summary>
    public bool Advanced { get; set; } = true;

    /// <summary> Penumbra mods (including Glamourer design mod associations). </summary>
    public bool Mods { get; set; } = true;

    public static ApplyMask Everything() => new();

    public static ApplyMask OnlyEquipment()
        => new() { Customization = CustomizeFlags.None, Advanced = false, Mods = false, Meta = true };

    public static ApplyMask OnlyCustomization()
        => new() { Equipment = EquipSlotFlags.None, Meta = false, Mods = false, Advanced = true };

    public static ApplyMask OnlyMods()
        => new() { Equipment = EquipSlotFlags.None, Customization = CustomizeFlags.None, Meta = false, Advanced = false };

    public bool IsEverything
        => Equipment == EquipSlotFlags.All && Customization == CustomizeFlags.All && Meta && Advanced && Mods;
}

/// <summary> Registry of every layer kind this build knows. Used by the JSON converter and the Add Layer menu. </summary>
public static class LayerKinds
{
    public static readonly IReadOnlyList<(string Kind, string DisplayName, Func<Layer> Create)> All = new List<(string, string, Func<Layer>)>
    {
        (GlamourerLayer.KindName,      "Glamourer",          () => new GlamourerLayer()),
        (EquipmentLayer.KindName,      "Equipment Changes",  () => new EquipmentLayer()),
        (EquipmentCleanseLayer.KindName, "Equipment Cleanse", () => new EquipmentCleanseLayer()),
        (AppearanceLayer.KindName,     "Appearance Changes", () => new AppearanceLayer()),
        (PenumbraAdjustLayer.KindName, "Penumbra Adjust",    () => new PenumbraAdjustLayer()),
        (VitrineDesignLayer.KindName,  "Vitrine Design",     () => new VitrineDesignLayer()),
        (HonorificLayer.KindName,      "Honorific",          () => new HonorificLayer()),
        (MoodlesLayer.KindName,        "Moodles",            () => new MoodlesLayer()),
        (CustomizePlusLayer.KindName,  "Customize+",         () => new CustomizePlusLayer()),
        (SimpleHeelsLayer.KindName,    "Simple Heels",       () => new SimpleHeelsLayer()),
        (DecalLayer.KindName,          "Decal",              () => new DecalLayer()),
    };

    /// <summary> Kinds whose contribution is filtered through the pass's Macro Override (they write Glamourer / mod fields). </summary>
    public static bool IsMaskable(Layer layer)
        => layer is GlamourerLayer or VitrineDesignLayer or EquipmentLayer or EquipmentCleanseLayer or AppearanceLayer;

    public static Layer? Create(string kind)
    {
        foreach (var (k, _, create) in All)
            if (k == kind)
                return create();
        return null;
    }
}
