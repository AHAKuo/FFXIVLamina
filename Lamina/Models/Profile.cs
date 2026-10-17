namespace Lamina.Models;

/// <summary> A complete Lamina setup: pass groups, passes, layers, ending with its targets. Saved to profiles/&lt;Id&gt;.json. </summary>
public sealed class Profile
{
    /// <summary> 2 since the Penumbra Adjust "applies" semantics and the Equipment / Appearance layers (see Migrations). </summary>
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New Profile";

    /// <summary> Only one enabled profile per target. Enabling one disables others sharing a target. </summary>
    public bool Enabled { get; set; }

    /// <summary> Ordered. Normal groups: first true wins. Final groups: every true one layers on top, in list order (later wins). </summary>
    public List<PassGroup> PassGroups { get; set; } = new();

    public List<Target> Targets { get; set; } = new() { Target.LocalPlayer() };
}

public sealed class Target : IEquatable<Target>
{
    public TargetKind Kind { get; set; } = TargetKind.LocalPlayer;

    /// <summary> Only when Kind is Character / OtherPlayer. </summary>
    public string? Name { get; set; }

    /// <summary> Only when Kind is Character / OtherPlayer. </summary>
    public uint? WorldId { get; set; }

    public static Target LocalPlayer()
        => new() { Kind = TargetKind.LocalPlayer };

    public bool Equals(Target? other)
        => other is not null && Kind == other.Kind
         && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
         && WorldId == other.WorldId;

    public override bool Equals(object? obj)
        => Equals(obj as Target);

    public override int GetHashCode()
        => HashCode.Combine(Kind, Name?.ToLowerInvariant(), WorldId);

    public override string ToString()
        => Kind switch
        {
            TargetKind.LocalPlayer => "Local Player",
            _                      => $"{Name}@{WorldId}",
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

    /// <summary> Optional Macro Override: restricts what this pass may change. Null = everything. </summary>
    public ApplyMask? MacroOverride { get; set; }
}
