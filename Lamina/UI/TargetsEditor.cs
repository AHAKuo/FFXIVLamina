using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Engine;
using Lamina.Runtime.State;

namespace Lamina.UI;

/// <summary>
/// The target picker of a profile (Profiles tab, "Targets" column): who the profile applies to. Besides your own character a
/// profile can follow another player or a humanoid NPC rendered near you; the list of candidates comes from the engine and only
/// ever contains characters that can take on a look (no minions, mounts, furniture). Each non-local target can name the Penumbra
/// collection its mods go into. What a target kind can receive is shown next to it (see <see cref="TargetCapabilities"/>).
/// </summary>
public sealed partial class LaminaWindow
{
    private static readonly TimeSpan CandidateRefresh = TimeSpan.FromSeconds(1);

    private IReadOnlyList<TargetCandidate> _candidates = Array.Empty<TargetCandidate>();
    private DateTime _candidatesAt = DateTime.MinValue;
    private Guid? _targetsPopupProfile;
    private readonly Dictionary<Target, string> _collectionEdits = new();

    /// <summary> The "Targets" cell of a profile row: the targets with a presence dot each, and the button that opens the editor. </summary>
    private void DrawTargetsCell(Profile profile, IReadOnlyList<TargetStatus> statuses)
    {
        if (Widgets.IconButton(FontAwesomeIcon.Crosshairs, "Choose who this profile applies to"))
        {
            _targetsPopupProfile = profile.Id;
            _collectionEdits.Clear();
            ImGui.OpenPopup("##targets");
        }
        ImGui.SameLine(0, 4);

        if (profile.Targets.Count == 0)
        {
            Widgets.Badge("no target", Widgets.Warn, "This profile applies to nobody. Add a target.");
        }
        else
        {
            var first = true;
            foreach (var target in profile.Targets)
            {
                if (!first)
                {
                    ImGui.SameLine(0, 0);
                    ImGui.TextUnformatted(", ");
                    ImGui.SameLine(0, 0);
                }
                first = false;

                var status = profile.Enabled ? statuses.FirstOrDefault(s => s.Target.Equals(target)) : null;
                if (status is not null)
                {
                    var (color, tip) = PresenceOf(status);
                    ImGui.TextColored(color, "●");
                    Widgets.Tooltip(tip);
                    ImGui.SameLine(0, 2);
                }
                ImGui.TextUnformatted(DescribeTarget(target));
                if (target.Kind != TargetKind.LocalPlayer)
                    Widgets.Tooltip(CapabilityText(target.Kind));
            }
        }

        if (_targetsPopupProfile == profile.Id)
            DrawTargetsPopup(profile, statuses);
    }

    private void DrawTargetsPopup(Profile profile, IReadOnlyList<TargetStatus> statuses)
    {
        using var popup = ImRaii.Popup("##targets");
        if (!popup)
        {
            _targetsPopupProfile = null;
            return;
        }

        ImGui.TextColored(Widgets.Accent, $"Targets of \"{profile.Name}\"");
        Widgets.Hint("A profile applies to every target in this list while that character is rendered near you. Conditions are evaluated against your own state; the target receives the result.");
        ImGui.Separator();

        Target? remove  = null;
        var     changed = false;
        using (var table = ImRaii.Table("##targetRows", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("Receives", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("Mods collection", ImGuiTableColumnFlags.WidthStretch, 4f);
                ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
                ImGui.TableHeadersRow();

                foreach (var target in profile.Targets.ToList())
                {
                    using var id = ImRaii.PushId(target.GetHashCode());
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var status = profile.Enabled ? statuses.FirstOrDefault(s => s.Target.Equals(target)) : null;
                    if (status is not null)
                    {
                        var (color, tip) = PresenceOf(status);
                        ImGui.TextColored(color, "●");
                        Widgets.Tooltip(tip);
                        ImGui.SameLine(0, 4);
                    }
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(DescribeTarget(target));

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    var supported = string.Join(", ", TargetCapabilities.Supported(target.Kind).Select(TargetCapabilities.Describe));
                    Widgets.Hint(target.Kind == TargetKind.LocalPlayer ? "everything" : supported);
                    Widgets.Tooltip(CapabilityText(target.Kind));
                    if (status is { Dropped.Count: > 0 })
                    {
                        ImGui.SameLine();
                        Widgets.Badge($"{status.Dropped.Count} skipped", Widgets.Warn, string.Join("\n", status.Dropped));
                    }

                    ImGui.TableNextColumn();
                    if (target.Kind == TargetKind.LocalPlayer)
                    {
                        ImGui.AlignTextToFramePadding();
                        Widgets.Hint($"\"{_s.Profiles.Config.CollectionName}\" (Settings)");
                    }
                    else
                    {
                        changed |= DrawTargetCollection(profile, target, status);
                    }

                    ImGui.TableNextColumn();
                    if (Widgets.IconButton(FontAwesomeIcon.Times, "Remove this target"))
                        remove = target;
                }
            }
        }

        if (remove is not null)
        {
            profile.Targets.RemoveAll(t => t.Equals(remove));
            _collectionEdits.Remove(remove);
            changed = true;
        }

        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Add:");
        ImGui.SameLine();
        var hasLocal = profile.Targets.Any(t => t.IsLocal);
        if (!hasLocal && ImGui.Button("Your character"))
        {
            profile.Targets.Insert(0, Target.LocalPlayer());
            changed = true;
        }
        if (!hasLocal)
            ImGui.SameLine();

        RefreshCandidates();
        var items = _candidates
            .Where(c => !profile.Targets.Contains(c.Target))
            .Select(c => new Widgets.PickItem<Target>(c.Target, c.Name, $"{c.Detail}, {c.Distance:0} y"))
            .ToList();
        if (Widgets.SearchCombo("##addTarget", items.Count == 0 ? "No targetable character nearby" : "Nearby character…", items, out var picked, 300f))
        {
            profile.Targets.Add(picked);
            changed = true;
        }
        Widgets.Tooltip("Players and humanoid NPCs rendered near you, nearest first. Minions, mounts, monsters and objects cannot take on a look and are not listed.");

        if (changed)
        {
            _s.Profiles.MarkDirty(profile);
            // Adding a target to an enabled profile may collide with another enabled profile: the one being edited keeps the target.
            if (profile.Enabled)
            {
                var disabled = _s.Profiles.SetProfileEnabled(profile, true);
                if (disabled.Count > 0)
                    SetStatus($"Disabled {string.Join(", ", disabled.Select(d => d.Name))}: one profile per target.", Widgets.Warn);
            }
            _s.Engine.Invalidate();
        }
    }

    /// <summary> The collection field of a non-local target: name input (Enter applies; empty = Lamina's own collection from Settings) plus the status of that collection for the character. </summary>
    private bool DrawTargetCollection(Profile profile, Target target, TargetStatus? status)
    {
        var changed = false;
        if (!_collectionEdits.TryGetValue(target, out var text))
            text = target.CollectionName ?? string.Empty;

        ImGui.SetNextItemWidth(170 * Widgets.Scale);
        var fallback = _s.Profiles.Config.CollectionName.Trim();
        if (ImGui.InputTextWithHint("##collection", fallback.Length == 0 ? "none (mods off)" : $"{fallback} (Settings)", ref text, 64, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            target.CollectionName = text.Trim().Length == 0 ? null : text.Trim();
            _collectionEdits.Remove(target);
            _s.Engine.RefreshCollection();
            changed = true;
        }
        else
        {
            _collectionEdits[target] = text;
            if (ImGui.IsItemDeactivatedAfterEdit() && (text.Trim().Length == 0 ? null : text.Trim()) != target.CollectionName)
            {
                target.CollectionName = text.Trim().Length == 0 ? null : text.Trim();
                _collectionEdits.Remove(target);
                _s.Engine.RefreshCollection();
                changed = true;
            }
        }
        Widgets.Tooltip("The Penumbra collection this character's mods are written into. Empty: Lamina's own collection from Settings, the same one your "
                      + "character uses. To see the mods on the character, assign it to that collection in Penumbra under Individual Assignments; Lamina "
                      + "never assigns collections itself. Each profile only removes the mods it wrote, so profiles can share a collection; if two set the "
                      + "same mod differently, the one applied last wins.");

        if (status is not null && (target.CollectionName is not null || fallback.Length > 0))
        {
            ImGui.SameLine();
            var (color, label, tip) = status.Collection switch
            {
                CollectionStatus.Assigned        => (Widgets.Good, "assigned", "The character uses this collection; mods apply."),
                CollectionStatus.NotAssigned     => (Widgets.Warn, "not assigned", "The mods are written into the collection, but this character is not assigned to it in Penumbra, so they do not show on them. Assign it under Individual Assignments if you want that."),
                CollectionStatus.NotFound        => (Widgets.Warn, "not found", "No collection of this name exists in Penumbra. Create it there."),
                CollectionStatus.PenumbraMissing => (Widgets.Bad, "no Penumbra", "Penumbra is not loaded."),
                _                                => (Widgets.Muted, "mods off", "No collection chosen."),
            };
            Widgets.Badge(label, color, tip);
        }

        return changed;
    }

    private void RefreshCandidates()
    {
        var now = DateTime.UtcNow;
        if (now - _candidatesAt < CandidateRefresh)
            return;
        _candidatesAt = now;
        _candidates   = _s.Engine.NearbyCandidates();
    }

    private string DescribeTarget(Target target)
        => target.Kind switch
        {
            TargetKind.LocalPlayer => "Your character",
            TargetKind.OtherPlayer => $"{target.Name}@{_s.Catalog.WorldName(target.WorldId ?? 0)}",
            _                      => $"{target.Name} (NPC)",
        };

    private static (Vector4 Color, string Tooltip) PresenceOf(TargetStatus status)
    {
        if (status.Retiring)
            return (Widgets.Warn, "Removed; Lamina is still undoing what it applied.");
        if (status.Shadowed)
            return (Widgets.Warn, "This is you, and a profile for your character is active; that one wins.");
        if (!status.Present)
            return (Widgets.Muted, status.Target.IsLocal ? "Not logged in." : "Not rendered near you right now; the profile applies when the character appears.");
        if (status.LastApply is { Problems.Count: > 0 } apply)
            return (Widgets.Warn, "Applied with problems:\n" + string.Join("\n", apply.Problems));
        return (Widgets.Good, status.Target.IsLocal ? "Applied to your character right now." : $"Rendered (object #{status.ObjectIndex}); the profile is applied.");
    }

    private static string CapabilityText(TargetKind kind)
    {
        var yes = TargetCapabilities.Supported(kind).Select(TargetCapabilities.Describe);
        var no  = TargetCapabilities.Unsupported(kind).Select(s => $"{TargetCapabilities.Describe(s)} ({TargetCapabilities.Reason(kind, s)})");
        var text = "Receives: " + string.Join(", ", yes);
        if (kind != TargetKind.LocalPlayer)
            text += "\nSkipped: " + string.Join("; ", no);
        if (kind == TargetKind.OtherPlayer)
            text += "\nA relative Simple Heels offset is applied as absolute for another player.";
        return text;
    }
}
