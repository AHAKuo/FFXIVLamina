using Lamina.Models;

namespace Lamina.Runtime.Engine;

/// <summary> The parts of a design, as the integrations receive them. </summary>
public enum DesignSection
{
    Glamourer,
    Mods,
    Decals,
    Honorific,
    Moodles,
    CustomizePlus,
    SimpleHeels,
}

/// <summary>
/// What each kind of target can actually take. A target only receives what is valid for that actor; the rest is dropped before
/// diffing, with a reason the UI and /lam why show, so a profile shared between your own character and a friend never reports
/// a failure for a section that was never going to apply to them.
/// <list type="bullet">
/// <item><b>Local player</b>: everything.</item>
/// <item><b>Other player</b>: Glamourer (<c>ApplyState</c> by object index), mods (its own Penumbra collection), Honorific and
/// Simple Heels (both accept any player character), Moodles (any rendered character), Customize+ (any character). No decals:
/// the bake is projected onto your own body meshes. A relative Simple Heels offset is applied as absolute, since another
/// player's own offset is not readable over IPC.</item>
/// <item><b>Humanoid NPC</b>: Glamourer, Customize+ and mods. Honorific, Moodles and Simple Heels only accept player characters.</item>
/// </list>
/// </summary>
public static class TargetCapabilities
{
    /// <summary> Every section, in apply order. </summary>
    public static readonly IReadOnlyList<DesignSection> AllSections = Enum.GetValues<DesignSection>();

    /// <summary> Whether a target of this kind can receive the section at all. </summary>
    public static bool Supports(TargetKind kind, DesignSection section)
        => kind switch
        {
            TargetKind.LocalPlayer => true,
            TargetKind.OtherPlayer => section != DesignSection.Decals,
            TargetKind.Character   => section is DesignSection.Glamourer or DesignSection.Mods or DesignSection.CustomizePlus,
            _                      => false,
        };

    /// <summary> Why a section is dropped for this kind, or null when it is supported. </summary>
    public static string? Reason(TargetKind kind, DesignSection section)
    {
        if (Supports(kind, section))
            return null;

        return section switch
        {
            DesignSection.Decals      => "decals are baked onto your own body meshes",
            DesignSection.Honorific   => "Honorific only titles player characters",
            DesignSection.Moodles     => "Moodles only applies to player characters",
            DesignSection.SimpleHeels => "Simple Heels only registers player characters",
            _                         => $"{Describe(section)} does not apply to this target",
        };
    }

    /// <summary> Display name of a section. </summary>
    public static string Describe(DesignSection section)
        => section switch
        {
            DesignSection.Glamourer     => "Glamourer",
            DesignSection.Mods          => "Penumbra mods",
            DesignSection.Decals        => "Decals",
            DesignSection.Honorific     => "Honorific",
            DesignSection.Moodles       => "Moodles",
            DesignSection.CustomizePlus => "Customize+",
            DesignSection.SimpleHeels   => "Simple Heels",
            _                           => section.ToString(),
        };

    /// <summary> The sections a target of this kind receives. </summary>
    public static IEnumerable<DesignSection> Supported(TargetKind kind)
        => AllSections.Where(s => Supports(kind, s));

    /// <summary> The sections a target of this kind never receives. </summary>
    public static IEnumerable<DesignSection> Unsupported(TargetKind kind)
        => AllSections.Where(s => !Supports(kind, s));

    /// <summary>
    /// Removes from <paramref name="design"/> every section the target cannot take and lists what was dropped (only sections the
    /// design actually carried). Returns the same instance when nothing changes, so hashes stay stable for the local player.
    /// </summary>
    public static VirtualDesign Trim(VirtualDesign design, TargetKind kind, List<string> dropped)
    {
        if (kind == TargetKind.LocalPlayer)
            return design;

        var draft   = DesignDraft.CopyOf(design);
        var changed = false;

        if (!Supports(kind, DesignSection.Glamourer) && design.HasGlamourerContent)
        {
            draft.ClearGlamourer();
            changed = Drop(dropped, kind, DesignSection.Glamourer);
        }
        if (!Supports(kind, DesignSection.Mods) && design.HasMods)
        {
            draft.Mods.Clear();
            changed = Drop(dropped, kind, DesignSection.Mods);
        }
        if (!Supports(kind, DesignSection.Decals) && design.HasDecals)
        {
            draft.Decals.Clear();
            changed = Drop(dropped, kind, DesignSection.Decals);
        }
        if (!Supports(kind, DesignSection.Honorific) && design.Honorific is not null)
        {
            draft.Honorific = null;
            changed = Drop(dropped, kind, DesignSection.Honorific);
        }
        if (!Supports(kind, DesignSection.Moodles) && design.Moodles.Count > 0)
        {
            draft.Moodles.Clear();
            changed = Drop(dropped, kind, DesignSection.Moodles);
        }
        if (!Supports(kind, DesignSection.CustomizePlus) && design.HasCustomizePlus)
        {
            draft.CustomizePlusBones.Clear();
            changed = Drop(dropped, kind, DesignSection.CustomizePlus);
        }
        if (design.SimpleHeels is { } heels)
        {
            if (!Supports(kind, DesignSection.SimpleHeels))
            {
                draft.SimpleHeels = null;
                changed = Drop(dropped, kind, DesignSection.SimpleHeels);
            }
            else if (heels.Value.Relative)
            {
                // Another character's own Simple Heels offset is not readable over IPC; the design's offset is the whole offset.
                draft.SimpleHeels = new Field<HeelsOffset>(new HeelsOffset(heels.Value.Offset, false), heels.Source);
                dropped.Add("Simple Heels: relative offset applied as absolute (another character's own offset is unknown)");
                changed = true;
            }
        }

        return changed ? draft.Freeze(design.ProfileId, design.ActiveGroupId, design.ActiveFinalGroupIds, design.MatchedPassIds) : design;
    }

    private static bool Drop(List<string> dropped, TargetKind kind, DesignSection section)
    {
        dropped.Add($"{Describe(section)} skipped: {Reason(kind, section)}");
        return true;
    }
}
