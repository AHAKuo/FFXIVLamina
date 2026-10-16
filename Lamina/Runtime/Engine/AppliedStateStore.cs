using Lamina.Decals;

namespace Lamina.Runtime.Engine;

/// <summary>
/// Remembers what Lamina itself pushed to each integration so the differ can skip no-op applies and cleanup undoes only
/// Lamina's own changes. Never persisted: the Penumbra collection is reset on every apply, so nothing needs recovering after a crash.
/// </summary>
public sealed class AppliedStateStore
{
    /// <summary>
    /// Content hash of the last design that was applied completely. Null before the first apply and after an apply in which
    /// some section failed, so the next evaluation diffs again and retries only the failed sections.
    /// </summary>
    public string? DesignHash { get; set; }

    /// <summary> The last design applied, kept for diffing and the UI. </summary>
    public VirtualDesign? LastDesign { get; set; }

    /// <summary> Hash of the Glamourer section Lamina last pushed through ApplyState. Null when Lamina holds no Glamourer state. </summary>
    public string? GlamourerHash { get; set; }

    /// <summary> True while Lamina has a Glamourer state applied (and possibly locked) on the target. </summary>
    public bool GlamourerApplied
        => GlamourerHash is not null;

    /// <summary> Hash of the mod section Lamina last wrote. Null when the collection carries nothing from Lamina. </summary>
    public string? ModsHash { get; set; }

    /// <summary> True while Lamina's temporary settings are in the collection. </summary>
    public bool ModsApplied
        => ModsHash is not null;

    /// <summary> How many mods were written on the last apply. </summary>
    public int ModsWritten { get; set; }

    /// <summary>
    /// Hash of the decal list Lamina last handed to the publisher. Null when nothing is published (no decal temporary mod in
    /// the collection); the engine clears it again when a publish fails so the next evaluation retries.
    /// </summary>
    public string? DecalsHash { get; set; }

    /// <summary> True while Lamina's decal temporary mod is (or is about to be) in the collection. </summary>
    public bool DecalsApplied
        => DecalsHash is not null;

    /// <summary>
    /// Decals the applier did not publish yet because the same apply (or an earlier one) asked for a redraw: the bake must be
    /// projected onto the outfit that redraw loads, not the one still on screen. <see cref="DecalFollowUp"/> publishes them
    /// once the character was redrawn and its models are loaded. Null when nothing waits. <see cref="DecalsHash"/> already
    /// names them, so the differ does not retrigger.
    /// </summary>
    public IReadOnlyList<DecalSpec>? PendingDecals { get; set; }

    /// <summary>
    /// Identity of the player's draw object (<see cref="ISkinMeshProvider.DrawObjectState"/>) the last publish was prepared
    /// against; a different identity later means another outfit is drawn and the bake may no longer fit. Null when unknown.
    /// </summary>
    public string? DecalsDrawKey { get; set; }

    /// <summary> Forgets the decal section: nothing published, nothing pending, no draw object recorded. </summary>
    public void ForgetDecals()
    {
        DecalsHash    = null;
        PendingDecals = null;
        DecalsDrawKey = null;
    }

    /// <summary> The Honorific title Lamina set (including an explicit clear), or null when Lamina never touched the title. </summary>
    public HonorificTitle? HonorificSet { get; set; }

    /// <summary> Hash of the Customize+ bone set Lamina last pushed as a temporary profile. Null when Lamina holds no temporary profile. </summary>
    public string? CustomizePlusHash { get; set; }

    /// <summary> True while Lamina's temporary Customize+ profile is on the target. </summary>
    public bool CustomizePlusApplied
        => CustomizePlusHash is not null;

    /// <summary> Id Customize+ gave the temporary profile, informational. </summary>
    public Guid? CustomizePlusTemporaryId { get; set; }

    /// <summary> The Simple Heels offset Lamina registered, or null when Lamina holds no registration. </summary>
    public HeelsOffset? HeelsSet { get; set; }

    /// <summary> The player's own offset a relative <see cref="HeelsSet"/> was added to; null for an absolute one. </summary>
    public float? HeelsBase { get; set; }

    /// <summary> Moodle ids Lamina added (statuses only). </summary>
    public HashSet<Guid> MoodlesAdded { get; } = new();

    /// <summary> Preset ids Lamina applied; removed through Moodles' per-player preset removal on cleanup or when the design drops them. </summary>
    public HashSet<Guid> PresetsApplied { get; } = new();

    /// <summary> When Lamina last called into Moodles, so its own status-manager notifications can be told apart from external ones. </summary>
    public DateTime? LastMoodlesCallUtc { get; set; }

    /// <summary> When the last apply happened. </summary>
    public DateTime? LastApplyUtc { get; set; }

    /// <summary> True while Lamina has anything applied anywhere that a cleanup would need to undo. </summary>
    public bool AnythingApplied
        => DesignHash is not null || ModsApplied || GlamourerApplied || HonorificSet is not null
         || MoodlesAdded.Count > 0 || PresetsApplied.Count > 0 || DecalsApplied || CustomizePlusApplied || HeelsSet is not null;

    /// <summary>
    /// Forgets everything applied. With <paramref name="keepGlamourer"/> the Glamourer hash survives: used on profile switches,
    /// where the Glamourer state is left in place and the next apply either replaces it or reverts it through the normal diff.
    /// </summary>
    public void Reset(bool keepGlamourer = false)
    {
        var glamourer = GlamourerHash;
        DesignHash    = null;
        LastDesign    = null;
        GlamourerHash = keepGlamourer ? glamourer : null;
        ModsHash      = null;
        ModsWritten   = 0;
        ForgetDecals();
        HonorificSet  = null;
        CustomizePlusHash        = null;
        CustomizePlusTemporaryId = null;
        HeelsSet      = null;
        HeelsBase     = null;
        MoodlesAdded.Clear();
        PresetsApplied.Clear();
        LastApplyUtc = null;
        if (!keepGlamourer)
            LastGlamourerDesign = null;
    }

    /// <summary>
    /// The design whose Glamourer section Lamina last pushed successfully through ApplyState, so the next apply can tell which
    /// fields it no longer sets and revert those first. Null while Lamina holds no Glamourer state; survives a profile switch
    /// together with <see cref="GlamourerHash"/>, so fields the new profile drops still fall back.
    /// </summary>
    public VirtualDesign? LastGlamourerDesign { get; set; }
}
