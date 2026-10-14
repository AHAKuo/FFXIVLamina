using System.Numerics;

namespace Lamina.Models;

/// <summary> One contribution inside a pass. Serialized polymorphically with a "kind" field equal to <see cref="Kind"/>. </summary>
public abstract class Layer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public bool Enabled { get; set; } = true;

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

    public override string Describe() => $"Vitrine design {VitrineDesignId}";
}

/// <summary> A Glamourer design by Guid. Its mod associations carry over as is. </summary>
public sealed class GlamourerLayer : Layer
{
    public const string KindName = "Glamourer";
    public override string Kind => KindName;

    public Guid DesignId { get; set; }

    public override string Describe() => $"Glamourer design {DesignId}";
}

/// <summary> Direct control of Penumbra mods in the Lamina collection. </summary>
public sealed class PenumbraAdjustLayer : Layer
{
    public const string KindName = "PenumbraAdjust";
    public override string Kind => KindName;

    public List<ModState> Mods { get; set; } = new();

    public override string Describe() => $"Penumbra adjust ({Mods.Count} mods)";
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

/// <summary> State of one Penumbra mod inside the Lamina collection. Keyed by mod directory. </summary>
public sealed class ModState
{
    /// <summary> Penumbra mod directory name (the stable identifier). </summary>
    public string ModDirectory { get; set; } = string.Empty;

    /// <summary> Display name, informational only (helps the user when the mod is missing). </summary>
    public string? ModName { get; set; }

    public ModToggle State { get; set; } = ModToggle.Enable;

    /// <summary> Null = leave at 0 (Penumbra temporary settings always carry a priority). </summary>
    public int? Priority { get; set; }

    /// <summary> Option group name → enabled option names. Null = mod defaults. </summary>
    public Dictionary<string, List<string>>? Settings { get; set; }

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
        (VitrineDesignLayer.KindName,  "Vitrine Design",  () => new VitrineDesignLayer()),
        (GlamourerLayer.KindName,      "Glamourer",       () => new GlamourerLayer()),
        (PenumbraAdjustLayer.KindName, "Penumbra Adjust", () => new PenumbraAdjustLayer()),
        (HonorificLayer.KindName,      "Honorific",       () => new HonorificLayer()),
        (MoodlesLayer.KindName,        "Moodles",         () => new MoodlesLayer()),
    };

    public static Layer? Create(string kind)
    {
        foreach (var (k, _, create) in All)
            if (k == kind)
                return create();
        return null;
    }
}
