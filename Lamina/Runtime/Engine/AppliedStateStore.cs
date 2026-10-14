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

    /// <summary> The Honorific title Lamina set (including an explicit clear), or null when Lamina never touched the title. </summary>
    public HonorificTitle? HonorificSet { get; set; }

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
         || MoodlesAdded.Count > 0 || PresetsApplied.Count > 0;

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
        HonorificSet  = null;
        MoodlesAdded.Clear();
        PresetsApplied.Clear();
        LastApplyUtc = null;
    }
}
