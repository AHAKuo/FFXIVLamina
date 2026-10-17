namespace Lamina.Models;

/// <summary> A complete Lamina setup: pass groups, passes, layers, ending with its targets. Saved to profiles/&lt;Id&gt;.json. </summary>
public sealed class Profile
{
    /// <summary> 2 since the Penumbra Adjust "applies" semantics and the Equipment / Appearance layers, 4 since the random layers of a pass, 5 since targets other than the local player (see Migrations). </summary>
    public const int CurrentVersion = 5;

    public int Version { get; set; } = CurrentVersion;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New Profile";

    /// <summary> Only one enabled profile per target. Enabling one disables others sharing a target. </summary>
    public bool Enabled { get; set; }

    /// <summary> Ordered. Normal groups: first true wins. Final groups: every true one layers on top, in list order (later wins). </summary>
    public List<PassGroup> PassGroups { get; set; } = new();

    public List<Target> Targets { get; set; } = new() { Target.LocalPlayer() };
}

/// <summary>
/// Who a profile applies to. The local player is one target; a profile can also follow another player (name + home world)
/// or a humanoid non-player character (name + data id) while that character is rendered near you. Equality is identity
/// only: <see cref="CollectionName"/> is a setting of the target, not part of who it is.
/// </summary>
public sealed class Target : IEquatable<Target>
{
    public TargetKind Kind { get; set; } = TargetKind.LocalPlayer;

    /// <summary> The character's name. Null for the local player. </summary>
    public string? Name { get; set; }

    /// <summary> Home world id of an <see cref="TargetKind.OtherPlayer"/>. Null otherwise. </summary>
    public uint? WorldId { get; set; }

    /// <summary> Data id (ENpcBase / BNpcBase row) of a <see cref="TargetKind.Character"/>, which tells apart NPCs sharing a name. Null when unknown. </summary>
    public uint? DataId { get; set; }

    /// <summary>
    /// Penumbra collection Lamina may write mod states into for this character, by name. The player creates it in Penumbra and
    /// assigns the character to it (Individual Assignments); Lamina only writes temporary settings there. Null = no mods for
    /// this target. Ignored for the local player, who uses the collection from the settings.
    /// </summary>
    public string? CollectionName { get; set; }

    /// <summary> True for the local player target. </summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool IsLocal
        => Kind == TargetKind.LocalPlayer;

    public static Target LocalPlayer()
        => new() { Kind = TargetKind.LocalPlayer };

    /// <summary> Another player, by name and home world. </summary>
    public static Target Player(string name, uint worldId)
        => new() { Kind = TargetKind.OtherPlayer, Name = name, WorldId = worldId };

    /// <summary> A humanoid non-player character, by name and data id. </summary>
    public static Target Npc(string name, uint? dataId)
        => new() { Kind = TargetKind.Character, Name = name, DataId = dataId };

    public bool Equals(Target? other)
        => other is not null && Kind == other.Kind
         && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
         && WorldId == other.WorldId
         && DataId == other.DataId;

    public override bool Equals(object? obj)
        => Equals(obj as Target);

    public override int GetHashCode()
        => HashCode.Combine(Kind, Name?.ToLowerInvariant(), WorldId, DataId);

    /// <summary> Short label without world names (the UI resolves the world through the catalog). </summary>
    public override string ToString()
        => Kind switch
        {
            TargetKind.LocalPlayer => "Local Player",
            TargetKind.Character   => $"{Name} (NPC)",
            _                      => WorldId is { } w ? $"{Name}@{w}" : Name ?? "?",
        };
}

public sealed class PassGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New Group";

    public bool Enabled { get; set; } = true;

    public ConditionStack Conditions { get; set; } = new();

    /// <summary> Final groups are evaluated after the winning normal group and layered on top in list order: later merges later and wins. </summary>
    public bool IsFinal { get; set; }

    /// <summary> Ordered. Every matching pass contributes; evaluation direction decides who wins. </summary>
    public List<Pass> Passes { get; set; } = new();
}

public sealed class Pass
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New Pass";

    public bool Enabled { get; set; } = true;

    public ConditionStack Conditions { get; set; } = new();

    /// <summary> Ordered. Smashed into one FinalLayer at runtime in the evaluation direction. </summary>
    public List<Layer> Layers { get; set; } = new();

    /// <summary>
    /// The random section: one enabled layer of this list is chosen with equal chance and merges after <see cref="Layers"/>
    /// (in list terms: as if it were the last row). The pick is made when the pass starts contributing and kept until it
    /// stops, so a changing game state does not reshuffle it. Use case: several idle mods, one at random.
    /// </summary>
    public List<Layer> RandomLayers { get; set; } = new();

    /// <summary> Optional Macro Override: restricts what this pass may change. Null = everything. </summary>
    public ApplyMask? MacroOverride { get; set; }

    /// <summary> Every layer of the pass, ordered layers first, then the random section. </summary>
    [Newtonsoft.Json.JsonIgnore]
    public IEnumerable<Layer> AllLayers
        => Layers.Concat(RandomLayers);

    /// <summary> The list behind a section. </summary>
    public List<Layer> LayersIn(LayerSection section)
        => section == LayerSection.Random ? RandomLayers : Layers;

    /// <summary> The section a layer of this pass sits in, or null when the pass does not hold it. </summary>
    public LayerSection? SectionOf(Layer layer)
        => Layers.Contains(layer) ? LayerSection.Layers : RandomLayers.Contains(layer) ? LayerSection.Random : null;
}

/// <summary> The two layer lists of a pass. </summary>
public enum LayerSection
{
    /// <summary> <see cref="Pass.Layers"/>: every enabled layer merges, in the evaluation direction. </summary>
    Layers,
    /// <summary> <see cref="Pass.RandomLayers"/>: one enabled layer is picked at random. </summary>
    Random,
}
