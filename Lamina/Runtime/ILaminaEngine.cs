using Lamina.Models;
using Lamina.Runtime.Compose;

namespace Lamina.Runtime;

/// <summary> Where Lamina stands with its Penumbra collection for the local player. </summary>
public enum CollectionStatus
{
    /// <summary> Penumbra unavailable. </summary>
    PenumbraMissing,
    /// <summary> No collection with the configured name exists; the user must create it in Penumbra. </summary>
    NotFound,
    /// <summary>
    /// Collection exists but the character is not assigned to it in Penumbra. For your own character that is the opt-out: mods are
    /// skipped. For another target the mods are still written into the collection; they only show once the character is assigned.
    /// Lamina never assigns on its own.
    /// </summary>
    NotAssigned,
    /// <summary> Collection exists and is the character's effective collection; mods apply. </summary>
    Assigned,
    /// <summary> No collection name anywhere (Settings empty and none on the target): mods (and decals) are skipped. </summary>
    NotConfigured,
}

/// <summary> Outcome of the last apply, for the Settings / debug overlay. </summary>
/// <param name="DecalsPublished"> A decal publish was started (its bake and redirect complete asynchronously). </param>
/// <param name="DecalsDeferred"> The decals wait in the store until the redraw this apply asked for has loaded the new outfit. </param>
public sealed record ApplyInfo(
    DateTime AtUtc,
    string DesignHash,
    bool GlamourerApplied,
    bool ModsApplied,
    int ModsWritten,
    bool HonorificApplied,
    bool MoodlesApplied,
    bool CustomizePlusApplied,
    bool HeelsApplied,
    bool DecalsPublished,
    bool DecalsDeferred,
    bool RedrawRequested,
    bool RedrawDeferred,
    IReadOnlyList<string> Problems);

/// <summary> The runtime pipeline as the UI and commands see it. Implemented by LaminaEngine (wave 2). </summary>
public interface ILaminaEngine
{
    /// <summary> Master toggle mirror (config.Enabled). Setting false runs Cleanup; true re-applies. </summary>
    bool Enabled { get; }
    void SetEnabled(bool enabled);

    GameStateSnapshot Snapshot { get; }
    VirtualDesign? LastDesign { get; }
    EvaluationReport? LastReport { get; }
    ApplyInfo? LastApply { get; }
    /// <summary> Set while a redraw is waiting for a safe moment. </summary>
    bool RedrawPending { get; }

    CollectionStatus CollectionStatus { get; }
    Guid? CollectionId { get; }
    /// <summary> Re-check the collection (after the user created, renamed or assigned it in Penumbra). </summary>
    void RefreshCollection();

    /// <summary> Force a full re-evaluation and apply, even if the design hash is unchanged (/lam reapply). </summary>
    void RequestReapply();
    /// <summary> Re-evaluate on the next tick (profiles or designs changed). Applies only if the design changed. </summary>
    void Invalidate();

    /// <summary> Redraw the local player now through the scheduler (clears any pending deferred redraw). Returns false when Penumbra or the player is unavailable. </summary>
    bool RedrawNow();

    /// <summary> True while Lamina has decals published or waiting to be published. </summary>
    bool DecalsApplied { get; }

    /// <summary> Forgets the decal bookkeeping (published, pending) so the next evaluation publishes anew; optionally asks for a redraw first. </summary>
    void ForgetDecals(bool redraw);

    /// <summary> Preview for the UI: what the profile would produce right now, optionally focused on a group or pass. </summary>
    PreviewResult Preview(Profile profile, Guid? focusGroupId, Guid? focusPassId);

    /// <summary> Human readable explanation of the current state (/lam why). </summary>
    string Why();

    /// <summary> Every target Lamina currently drives (the local player first), with where each one stands. </summary>
    IReadOnlyList<TargetStatus> Targets { get; }

    /// <summary> Targetable characters rendered near you right now (players and humanoid NPCs, nearest first), for the target picker. </summary>
    IReadOnlyList<Lamina.Runtime.State.TargetCandidate> NearbyCandidates();

    /// <summary> Raised on the framework thread after every apply attempt. </summary>
    event Action? Applied;
}

/// <summary> Where one target stands right now, for the UI and /lam why. </summary>
/// <param name="Target"> The target. </param>
/// <param name="Present"> The character is rendered (the local player: logged in). </param>
/// <param name="ObjectIndex"> Its object table index while present. </param>
/// <param name="ProfileName"> Name of the enabled profile for this target, or null. </param>
/// <param name="Collection"> Collection status for the target's mods. </param>
/// <param name="CollectionName"> The collection the target's mods go into (settings collection for the local player). </param>
/// <param name="Dropped"> Sections the design carried that this target cannot take, with reasons. </param>
/// <param name="LastApply"> Last apply outcome on this target. </param>
/// <param name="Shadowed"> A named player target that is you while a Local Player profile is active; it yields. </param>
/// <param name="Retiring"> No profile targets it any more; Lamina is still undoing what it applied. </param>
public sealed record TargetStatus(
    Target Target,
    bool Present,
    int? ObjectIndex,
    string? ProfileName,
    CollectionStatus Collection,
    string? CollectionName,
    IReadOnlyList<string> Dropped,
    ApplyInfo? LastApply,
    bool Shadowed,
    bool Retiring);
