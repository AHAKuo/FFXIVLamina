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
    /// <summary> Collection exists but the player has not assigned themselves to it in Penumbra: mods are skipped (opt out). Lamina never assigns on its own. </summary>
    NotAssigned,
    /// <summary> Collection exists and is the player's effective collection; mods will be applied. </summary>
    Assigned,
}

/// <summary> Outcome of the last apply, for the Settings / debug overlay. </summary>
/// <param name="DecalsPublished"> A decal publish was started (its bake and redirect complete asynchronously). </param>
public sealed record ApplyInfo(
    DateTime AtUtc,
    string DesignHash,
    bool GlamourerApplied,
    bool ModsApplied,
    int ModsWritten,
    bool HonorificApplied,
    bool MoodlesApplied,
    bool DecalsPublished,
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

    /// <summary> Preview for the UI: what the profile would produce right now, optionally focused on a group or pass. </summary>
    PreviewResult Preview(Profile profile, Guid? focusGroupId, Guid? focusPassId);

    /// <summary> Human readable explanation of the current state (/lam why). </summary>
    string Why();

    /// <summary> Raised on the framework thread after every apply attempt. </summary>
    event Action? Applied;
}
