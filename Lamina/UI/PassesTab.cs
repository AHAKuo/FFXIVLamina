using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;

namespace Lamina.UI;

public sealed partial class LaminaWindow
{
    private const float GroupsWidth = 290f;
    private const float PassesWidth = 300f;

    /// <summary> Width the four row icon buttons (up, down, duplicate, delete) need, so the name never pushes them out of the list. </summary>
    private static float RowButtonsWidth()
        => 4 * ImGui.GetFrameHeight() + 3 * 2 + ImGui.GetStyle().ItemSpacing.X * 2;

    private PreviewResult? _livePreview;
    private DateTime _livePreviewAt = DateTime.MinValue;
    private Guid? _livePreviewProfile;
    private (Guid? GroupId, Guid? PassId) _livePreviewFocus;

    private string _renameBuffer = string.Empty;
    private Guid? _renamingId;

    private string _saveDesignName = string.Empty;
    private bool _saveDesignIncludeMods = true;

    /// <summary> Routes the layer editor's copy / paste / move feedback into the status line; created once. </summary>
    private Action<string, Vector4>? _layerStatus;

    private void DrawPassesTab()
    {
        var profile = EditProfile;
        DrawProfilePicker(profile);
        if (profile is null)
        {
            Widgets.Hint("No profiles. Create one in the Profiles tab.");
            return;
        }

        RefreshLivePreview(profile);

        var scale = Widgets.Scale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        using (var left = ImRaii.Child("##groups", new Vector2(GroupsWidth * scale, 0), true))
        {
            if (left)
                DrawGroupList(profile);
        }
        ImGui.SameLine(0, spacing);
        using (var middle = ImRaii.Child("##passes", new Vector2(PassesWidth * scale, 0), true))
        {
            if (middle)
                DrawPassList(profile);
        }
        ImGui.SameLine(0, spacing);
        using (var right = ImRaii.Child("##editor", new Vector2(0, 0), true))
        {
            if (right)
            {
                if (SelectedPass is { } pass && SelectedGroup is { } group)
                    DrawPassEditor(profile, group, pass);
                else if (SelectedGroup is { } g)
                    DrawGroupEditor(profile, g);
                else
                    Widgets.Hint("Select a pass group or a pass to edit it.");
            }
        }
    }

    private void DrawProfilePicker(Profile? profile)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Editing profile:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        using (var combo = ImRaii.Combo("##editProfile", profile?.Name ?? "(none)"))
        {
            if (combo)
                foreach (var p in _s.Profiles.Profiles)
                    if (ImGui.Selectable(p.Enabled ? $"{p.Name}  (active)" : p.Name, p.Id == _editProfileId))
                        Select(p.Id);
        }
        if (profile is not null)
        {
            ImGui.SameLine();
            if (profile.Enabled)
                Widgets.Badge("● active", Widgets.Good, "This profile is applied to the local player.");
            else
                Widgets.Badge("○ inactive", Widgets.Muted, "Enable it in the Profiles tab to apply it.");
        }
        ImGui.SameLine();
        if (Widgets.IconButton(FontAwesomeIcon.Sync, "Re-evaluate and reapply now (/lam reapply)"))
            _s.Engine.RequestReapply();
        ImGui.SameLine();
        DrawStatusLine();
        ImGui.Separator();
    }

    private void RefreshLivePreview(Profile profile)
    {
        var now = DateTime.UtcNow;
        if (_livePreview is not null && _livePreviewProfile == profile.Id && now - _livePreviewAt < TimeSpan.FromMilliseconds(250))
            return;
        _livePreviewAt      = now;
        _livePreviewProfile = profile.Id;
        _livePreviewFocus   = (_groupId, _passId);
        _livePreview        = SafePreview(profile, _groupId, _passId);
    }

    private bool ConditionsTrueNow(ConditionStack stack)
    {
        try
        {
            return _s.Evaluator.EvaluateInstant(stack, _s.Engine.Snapshot);
        }
        catch
        {
            return false;
        }
    }

    // ---- groups --------------------------------------------------------------------------------

    private void DrawGroupList(Profile profile)
    {
        var groups = profile.PassGroups;
        Guid? remove = null, duplicate = null;
        var moved = false;

        using var list = ImRaii.Child("##groupList", new Vector2(0, 0), false);
        if (!list)
            return;

        DrawGroupSectionHeader(profile, "Pass Groups", "first match wins",
            "Normal groups are checked top to bottom; the first group whose conditions are true wins and the rest are skipped.", isFinal: false);
        if (!groups.Any(g => !g.IsFinal))
            Widgets.Hint("No groups.");
        moved |= DrawGroupRows(profile, isFinal: false, ref remove, ref duplicate);

        ImGui.Spacing();
        DrawGroupSectionHeader(profile, "Final Groups", "lower rows override",
            "Final groups do not take part in the switch. Every final group whose conditions are true is layered on top of the winning group, top to bottom, so lower rows win.", isFinal: true);
        if (!groups.Any(g => g.IsFinal))
            Widgets.Hint("No final groups. A final group layers on top of whichever normal group wins, e.g. \"take the helmet off while sitting\".");
        moved |= DrawGroupRows(profile, isFinal: true, ref remove, ref duplicate);

        if (moved)
            _s.Engine.Invalidate();
        if (duplicate is { } d && _s.Profiles.DuplicateGroup(profile, d) is { } copy)
        {
            Select(profile.Id, copy.Id);
            _s.Engine.Invalidate();
        }
        if (remove is { } r && _s.Profiles.RemoveGroup(profile, r))
        {
            if (_groupId == r) Select(profile.Id);
            _s.Engine.Invalidate();
        }
    }

    private void DrawGroupSectionHeader(Profile profile, string title, string hint, string tooltip, bool isFinal)
    {
        ImGui.TextUnformatted(title);
        ImGui.SameLine();
        Widgets.Hint(hint);
        Widgets.Tooltip(tooltip);
        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 60 * Widgets.Scale);
        using var id = ImRaii.PushId(isFinal ? "final" : "normal");
        if (Widgets.IconButton(FontAwesomeIcon.Plus, isFinal ? "Add final group" : "Add pass group"))
        {
            var g = _s.Profiles.AddGroup(profile, isFinal: isFinal);
            Select(profile.Id, g.Id);
            _s.Engine.Invalidate();
        }
        ImGui.Separator();
    }

    /// <summary>
    /// Draws the rows of one section (normal or final). Rows move only within their section: the list stays one ordered
    /// list in the model, so "up" and "down" swap with the nearest group of the same kind.
    /// </summary>
    private bool DrawGroupRows(Profile profile, bool isFinal, ref Guid? remove, ref Guid? duplicate)
    {
        var report  = _livePreview?.Report;
        var groups  = profile.PassGroups;
        var indices = new List<int>();
        for (var i = 0; i < groups.Count; i++)
            if (groups[i].IsFinal == isFinal)
                indices.Add(i);

        int? moveFrom = null, moveTo = null;
        for (var n = 0; n < indices.Count; n++)
        {
            var i = indices[n];
            var g = groups[i];
            using var id = ImRaii.PushId(g.Id.ToString());
            var enabled = g.Enabled;
            if (ImGui.Checkbox("##en", ref enabled))
            {
                g.Enabled = enabled;
                Touch(profile);
            }
            Widgets.Tooltip(enabled ? "Enabled" : "Disabled");
            ImGui.SameLine();

            var isActive = report is not null && (report.ActiveGroupId == g.Id || report.ActiveFinalGroupIds.Contains(g.Id));
            var selected = _groupId == g.Id;
            using (ImRaii.PushColor(ImGuiCol.Text, isActive ? Widgets.Good : g.Enabled ? new Vector4(1, 1, 1, 1) : Widgets.Muted))
            {
                if (ImGui.Selectable(g.Name + "##sel", selected, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(ImGui.GetContentRegionAvail().X - RowButtonsWidth(), 0)))
                {
                    Select(profile.Id, g.Id);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        _renamingId   = g.Id;
                        _renameBuffer = g.Name;
                    }
                }
            }
            Widgets.Tooltip($"{g.Passes.Count} pass(es), {g.Conditions.Leaves().Count()} condition(s)" + (isActive ? "\nActive right now" : string.Empty)
                          + (g.Conditions.IsEmpty ? "\nNo conditions: always true" : ConditionsTrueNow(g.Conditions) ? "\nConditions: true" : "\nConditions: false"));

            ImGui.SameLine();
            if (Widgets.IconButton(FontAwesomeIcon.ArrowUp, "Move up", n > 0)) { moveFrom = i; moveTo = indices[n - 1]; }
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.ArrowDown, "Move down", n < indices.Count - 1)) { moveFrom = i; moveTo = indices[n + 1]; }
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.Clone, "Duplicate")) duplicate = g.Id;
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.Trash, "Delete (hold Ctrl)", ImGui.GetIO().KeyCtrl)) remove = g.Id;
        }

        return moveFrom is { } f && moveTo is { } t && _s.Profiles.MoveGroup(profile, f, t);
    }

    private void DrawGroupEditor(Profile profile, PassGroup group)
    {
        ImGui.TextColored(Widgets.Accent, "Pass Group");
        ImGui.SameLine();
        if (_livePreview?.Report is { } report && (report.ActiveGroupId == group.Id || report.ActiveFinalGroupIds.Contains(group.Id)))
            Widgets.Badge("active now", Widgets.Good);
        else
            Widgets.Badge(group.Conditions.IsEmpty || ConditionsTrueNow(group.Conditions) ? "conditions true (another group wins or disabled)" : "conditions false", Widgets.Muted);
        ImGui.Separator();

        var name = group.Name;
        ImGui.SetNextItemWidth(300 * Widgets.Scale);
        if (ImGui.InputText("Name##group", ref name, 128))
        {
            group.Name = name;
            Touch(profile);
        }
        var enabled = group.Enabled;
        if (ImGui.Checkbox("Enabled##group", ref enabled))
        {
            group.Enabled = enabled;
            Touch(profile);
        }
        ImGui.SameLine();
        var isFinal = group.IsFinal;
        if (ImGui.Checkbox("Final Pass##group", ref isFinal))
        {
            group.IsFinal = isFinal;
            Touch(profile);
        }
        Widgets.Tooltip("Final groups do not take part in the switch. Every final group whose conditions are true is layered on top of the winning group, in list order: lower rows win.");

        ImGui.SameLine();
        DrawTransferButtons(profile, group, null);

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Group conditions", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Widgets.Hint("All passes of this group are only evaluated when these conditions are true.");
            if (_conditions.Draw(group.Conditions, "groupConds"))
                Touch(profile);
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Preview##group"))
            _preview.Draw(profile, group.Id, null, _livePreview, GroupName, PassName);
    }

    // ---- passes --------------------------------------------------------------------------------

    private void DrawPassList(Profile profile)
    {
        var group = SelectedGroup;
        ImGui.TextUnformatted("Passes");
        ImGui.SameLine();
        var bottomWins = _s.Profiles.Config.Direction == EvaluationDirection.TopToBottom;
        Widgets.Hint(bottomWins ? "lower rows override" : "upper rows override");
        Widgets.Tooltip(bottomWins
            ? "Every pass whose conditions are true contributes. Passes are merged top to bottom, so a lower row overrides what an upper row set."
            : "Every pass whose conditions are true contributes. Passes are merged bottom to top, so an upper row overrides what a lower row set.");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 60 * Widgets.Scale);
        if (Widgets.IconButton(FontAwesomeIcon.Plus, "Add pass", group is not null) && group is not null)
        {
            var p = _s.Profiles.AddPass(profile, group);
            Select(profile.Id, group.Id, p.Id);
            _s.Engine.Invalidate();
        }
        ImGui.Separator();
        if (group is null)
        {
            Widgets.Hint("Select a pass group.");
            return;
        }

        var matched = _livePreview?.Report.MatchedPassIds;
        var passes  = group.Passes;
        int? moveFrom = null, moveTo = null;
        Guid? remove = null, duplicate = null;

        using var list = ImRaii.Child("##passList", new Vector2(0, 0), false);
        if (!list)
            return;
        for (var i = 0; i < passes.Count; i++)
        {
            var p = passes[i];
            using var id = ImRaii.PushId(p.Id.ToString());
            var enabled = p.Enabled;
            if (ImGui.Checkbox("##en", ref enabled))
            {
                p.Enabled = enabled;
                Touch(profile);
            }
            ImGui.SameLine();

            var condTrue  = p.Conditions.IsEmpty || ConditionsTrueNow(p.Conditions);
            var isMatched = matched?.Contains(p.Id) == true;
            ImGui.TextColored(isMatched ? Widgets.Good : condTrue ? Widgets.Warn : Widgets.Muted, isMatched ? "✓" : condTrue ? "✓" : "✗");
            Widgets.Tooltip(isMatched ? "Matching and contributing to the current design"
                : condTrue ? "Conditions are true, but the group is not active (or the pass is disabled)" : "Conditions are false right now");
            ImGui.SameLine();

            var hasRandom = p.RandomLayers.Count > 0;
            var label     = $"{p.Name}  ({p.Layers.Count}{(hasRandom ? "+1" : string.Empty)})";
            using (ImRaii.PushColor(ImGuiCol.Text, p.Enabled ? new Vector4(1, 1, 1, 1) : Widgets.Muted))
            {
                if (ImGui.Selectable(label + "##sel", _passId == p.Id, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - RowButtonsWidth(), 0)))
                    Select(profile.Id, group.Id, p.Id);
            }
            Widgets.Tooltip($"{p.Layers.Count} layer(s){(hasRandom ? $" + one of {p.RandomLayers.Count} random layers" : string.Empty)}, {p.Conditions.Leaves().Count()} condition(s)");

            ImGui.SameLine();
            if (Widgets.IconButton(FontAwesomeIcon.ArrowUp, "Move up", i > 0)) { moveFrom = i; moveTo = i - 1; }
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.ArrowDown, "Move down", i < passes.Count - 1)) { moveFrom = i; moveTo = i + 1; }
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.Clone, "Duplicate")) duplicate = p.Id;
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.Trash, "Delete (hold Ctrl)", ImGui.GetIO().KeyCtrl)) remove = p.Id;
        }

        if (moveFrom is { } f && moveTo is { } t && _s.Profiles.MovePass(profile, group, f, t))
            _s.Engine.Invalidate();
        if (duplicate is { } d && _s.Profiles.DuplicatePass(profile, group, d) is { } copy)
        {
            Select(profile.Id, group.Id, copy.Id);
            _s.Engine.Invalidate();
        }
        if (remove is { } r && _s.Profiles.RemovePass(profile, group, r))
        {
            if (_passId == r) Select(profile.Id, group.Id);
            _s.Engine.Invalidate();
        }
    }

    private void DrawPassEditor(Profile profile, PassGroup group, Pass pass)
    {
        ImGui.TextColored(Widgets.Accent, "Pass");
        ImGui.SameLine();
        Widgets.Hint($"in {group.Name}");
        ImGui.SameLine();
        var isMatched = _livePreview?.Report.MatchedPassIds.Contains(pass.Id) == true;
        Widgets.Badge(isMatched ? "matching now" : pass.Conditions.IsEmpty || ConditionsTrueNow(pass.Conditions) ? "conditions true" : "conditions false",
            isMatched ? Widgets.Good : Widgets.Muted);
        ImGui.Separator();

        var name = pass.Name;
        ImGui.SetNextItemWidth(300 * Widgets.Scale);
        if (ImGui.InputText("Name##pass", ref name, 128))
        {
            pass.Name = name;
            Touch(profile);
        }
        var enabled = pass.Enabled;
        if (ImGui.Checkbox("Enabled##pass", ref enabled))
        {
            pass.Enabled = enabled;
            Touch(profile);
        }
        ImGui.SameLine();
        DrawTransferButtons(profile, group, pass);

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Conditions", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (_conditions.Draw(pass.Conditions, "passConds"))
                Touch(profile);
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Layers", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (_layers.Draw(profile, pass, _layerStatus ??= (text, color) => SetStatus(text, color)))
                _s.Engine.Invalidate();
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Random Layers", pass.RandomLayers.Count > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
        {
            if (_layers.Draw(profile, pass, LayerSection.Random, _layerStatus ??= (text, color) => SetStatus(text, color)))
                _s.Engine.Invalidate();
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Macro Override"))
        {
            if (DrawMacroOverride(pass))
                Touch(profile);
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Preview##pass"))
            _preview.Draw(profile, group.Id, pass.Id, _livePreview, GroupName, PassName);
    }

    // ---- macro override ------------------------------------------------------------------------

    private enum MaskPreset
    {
        Off,
        OnlyEquipment,
        OnlyCustomization,
        OnlyMods,
        Custom,
    }

    private static MaskPreset PresetOf(ApplyMask? mask)
    {
        if (mask is null || mask.IsEverything)
            return MaskPreset.Off;
        if (Same(mask, ApplyMask.OnlyEquipment()))
            return MaskPreset.OnlyEquipment;
        if (Same(mask, ApplyMask.OnlyCustomization()))
            return MaskPreset.OnlyCustomization;
        if (Same(mask, ApplyMask.OnlyMods()))
            return MaskPreset.OnlyMods;
        return MaskPreset.Custom;

        static bool Same(ApplyMask a, ApplyMask b)
            => a.Equipment == b.Equipment && a.Customization == b.Customization && a.Meta == b.Meta && a.Advanced == b.Advanced && a.Mods == b.Mods;
    }

    private bool DrawMacroOverride(Pass pass)
    {
        Widgets.Paragraph("Restricts what this pass's Glamourer, Vitrine, Equipment Changes and Appearance Changes layers may change. Honorific, Moodles, Customize+, Simple Heels and Penumbra Adjust layers are never restricted.");
        var preset  = PresetOf(pass.MacroOverride);
        var changed = false;
        foreach (var p in Enum.GetValues<MaskPreset>())
        {
            if (p != MaskPreset.Off)
                ImGui.SameLine();
            if (ImGui.RadioButton(Widgets.Humanize(p.ToString()), preset == p) && preset != p)
            {
                pass.MacroOverride = p switch
                {
                    MaskPreset.Off               => null,
                    MaskPreset.OnlyEquipment     => ApplyMask.OnlyEquipment(),
                    MaskPreset.OnlyCustomization => ApplyMask.OnlyCustomization(),
                    MaskPreset.OnlyMods          => ApplyMask.OnlyMods(),
                    _                            => pass.MacroOverride ?? new ApplyMask { Meta = false },
                };
                preset  = p;
                changed = true;
            }
        }
        if (preset != MaskPreset.Custom || pass.MacroOverride is not { } mask)
            return changed;

        ImGui.Spacing();
        ImGui.TextUnformatted("Equipment slots");
        using (var table = ImRaii.Table("##slots", 4, ImGuiTableFlags.SizingStretchSame))
        {
            if (table)
                foreach (var slot in Enum.GetValues<EquipSlot>())
                {
                    ImGui.TableNextColumn();
                    var on = mask.Equipment.Has(slot);
                    if (ImGui.Checkbox(Widgets.Humanize(slot.ToString()), ref on))
                    {
                        mask.Equipment = on ? mask.Equipment | slot.ToFlag() : mask.Equipment & ~slot.ToFlag();
                        changed = true;
                    }
                }
            if (table)
            {
                ImGui.TableNextColumn();
                var glasses = mask.Equipment.Has(BonusSlot.Glasses);
                if (ImGui.Checkbox("Facewear", ref glasses))
                {
                    mask.Equipment = glasses ? mask.Equipment | EquipSlotFlags.Glasses : mask.Equipment & ~EquipSlotFlags.Glasses;
                    changed = true;
                }
            }
        }
        if (ImGui.SmallButton("All slots")) { mask.Equipment = EquipSlotFlags.All; changed = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("No slots")) { mask.Equipment = EquipSlotFlags.None; changed = true; }

        ImGui.Spacing();
        ImGui.TextUnformatted("Customization fields");
        using (var table = ImRaii.Table("##customize", 4, ImGuiTableFlags.SizingStretchSame))
        {
            if (table)
                foreach (var idx in Enum.GetValues<CustomizeIndex>())
                {
                    ImGui.TableNextColumn();
                    var on = mask.Customization.Has(idx);
                    if (ImGui.Checkbox(Widgets.Humanize(idx.ToString()), ref on))
                    {
                        mask.Customization = on ? mask.Customization | idx.ToFlag() : mask.Customization & ~idx.ToFlag();
                        changed = true;
                    }
                }
        }
        if (ImGui.SmallButton("All fields")) { mask.Customization = CustomizeFlags.All; changed = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("No fields")) { mask.Customization = CustomizeFlags.None; changed = true; }

        ImGui.Spacing();
        var meta = mask.Meta;
        if (ImGui.Checkbox("Meta (hat, visor, weapon, ears, wetness)", ref meta)) { mask.Meta = meta; changed = true; }
        var adv = mask.Advanced;
        if (ImGui.Checkbox("Advanced (customization colors, advanced dyes)", ref adv)) { mask.Advanced = adv; changed = true; }
        var mods = mask.Mods;
        if (ImGui.Checkbox("Mods (design mod associations, Vitrine mods)", ref mods)) { mask.Mods = mods; changed = true; }
        return changed;
    }

    // ---- copy / move to another profile ----------------------------------------------------------

    private void DrawTransferButtons(Profile profile, PassGroup group, Pass? pass)
    {
        var noun = pass is null ? "group" : "pass";
        if (ImGui.Button($"Copy to…##{noun}"))
            ImGui.OpenPopup($"##copy{noun}");
        ImGui.SameLine();
        if (ImGui.Button($"Move to…##{noun}"))
            ImGui.OpenPopup($"##move{noun}");
        ImGui.SameLine();
        if (ImGui.Button($"Export##{noun}"))
        {
            ImGui.SetClipboardText(pass is null ? Persistence.ProfileTransfer.ExportClipboard(group) : Persistence.ProfileTransfer.ExportClipboard(pass));
            SetStatus($"{(pass is null ? "Group" : "Pass")} copied to the clipboard.", Widgets.Good);
        }
        Widgets.Tooltip("Copies a shareable string to the clipboard. Import it in the Profiles tab.");
        ImGui.SameLine();
        DrawSaveDesignButton(profile, group, pass, noun);

        DrawTransferPopup($"##copy{noun}", profile, group, pass, move: false);
        DrawTransferPopup($"##move{noun}", profile, group, pass, move: true);
    }

    // ---- save as Glamourer design ----------------------------------------------------------------

    /// <summary> What the pass / group produces right now: the live preview's focus when it already focuses it, else a fresh preview. </summary>
    private VirtualDesign? FocusDesign(Profile profile, PassGroup group, Pass? pass)
    {
        if (_livePreview is { Focus: { } focus } && _livePreviewProfile == profile.Id && _livePreviewFocus == (group.Id, pass?.Id))
            return focus;
        return SafePreview(profile, group.Id, pass?.Id)?.Focus;
    }

    private void DrawSaveDesignButton(Profile profile, PassGroup group, Pass? pass, string noun)
    {
        var popupId = $"##saveDesign{noun}";
        var focus   = FocusDesign(profile, group, pass);
        var reason  = !_s.Glamourer.Available ? "Glamourer is not available."
            : focus is null || focus.IsEmpty ? $"This {noun} produces nothing right now."
            : !focus.HasGlamourerContent && !focus.HasMods ? $"This {noun} produces nothing a Glamourer design can hold (only Honorific, Moodles, Customize+, Simple Heels or decals)."
            : null;

        using (ImRaii.Disabled(reason is not null))
        {
            if (ImGui.Button($"Save as Glamourer design…##{noun}"))
            {
                _saveDesignName        = pass is null ? $"{profile.Name} · {group.Name}" : $"{profile.Name} · {group.Name} · {pass.Name}";
                _saveDesignIncludeMods = true;
                ImGui.OpenPopup(popupId);
            }
        }
        Widgets.Tooltip(reason ?? $"Creates a design inside Glamourer from what this {noun} produces right now, so it can be used outside Lamina.");

        DrawSaveDesignPopup(popupId, profile, group, pass, noun);
    }

    private void DrawSaveDesignPopup(string popupId, Profile profile, PassGroup group, Pass? pass, string noun)
    {
        using var popup = ImRaii.Popup(popupId);
        if (!popup)
            return;

        var focus = FocusDesign(profile, group, pass);
        if (focus is null || focus.IsEmpty)
        {
            Widgets.Hint($"This {noun} produces nothing right now.");
            if (ImGui.Button("Close"))
                ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.TextUnformatted(pass is null ? "Save this group as a Glamourer design" : "Save this pass as a Glamourer design");
        ImGui.Separator();

        ImGui.SetNextItemWidth(360 * Widgets.Scale);
        var submit = ImGui.InputText("Name##saveDesign", ref _saveDesignName, 128, ImGuiInputTextFlags.EnterReturnsTrue);

        var inheritMods = focus.Mods.Values.Count(m => m.State is null);
        using (ImRaii.Disabled(!focus.HasMods))
            ImGui.Checkbox("Include mod associations", ref _saveDesignIncludeMods);
        Widgets.Tooltip(focus.HasMods
            ? $"{focus.Mods.Count} mod(s) are set by this {noun}."
            + (inheritMods > 0 ? $"\n{inheritMods} of them leave enabled / disabled inherited; a Glamourer design cannot express that, so they are left out." : string.Empty)
            : $"This {noun} sets no mods.");

        Widgets.Paragraph($"Saves what this {noun} produces right now: equipment, customization, advanced settings and mod associations. "
                        + "Honorific, Moodles, Customize+, Simple Heels and decals are not part of a Glamourer design.");

        var canSave = !string.IsNullOrWhiteSpace(_saveDesignName);
        using (ImRaii.Disabled(!canSave))
        {
            if (ImGui.Button("Save##saveDesign", new Vector2(120 * Widgets.Scale, 0)) || (submit && canSave))
            {
                SaveAsGlamourerDesign(profile, group, pass, focus);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##saveDesign", new Vector2(120 * Widgets.Scale, 0)))
            ImGui.CloseCurrentPopup();
    }

    private void SaveAsGlamourerDesign(Profile profile, PassGroup group, Pass? pass, VirtualDesign focus)
    {
        var name        = _saveDesignName.Trim();
        var origin      = pass is null ? $"{profile.Name} / {group.Name}" : $"{profile.Name} / {group.Name} / {pass.Name}";
        var includeMods = _saveDesignIncludeMods && focus.HasMods;
        try
        {
            var written = GlamourerDesignWriter.BuildWithReport(focus, name, $"Saved from Lamina ({origin}).", includeMods, _caches.ModSettings);
            var (code, id) = _s.Glamourer.AddDesign(written.Design, name);
            if (code != Glamourer.Api.Enums.GlamourerApiEc.Success || id == Guid.Empty)
            {
                SetStatus($"Glamourer did not accept the design: {code}.", Widgets.Bad);
                return;
            }

            // The new design must be pickable right away: re-read Glamourer's list and rebuild the UI lists.
            _s.DesignCache.RefreshNames();
            _caches.MarkStale();

            var note = written.SkippedMods.Count > 0
                ? $" {written.SkippedMods.Count} mod(s) left out (enabled state inherited): {string.Join(", ", written.SkippedMods)}."
                : string.Empty;
            SetStatus($"Saved Glamourer design '{name}'.{note}", Widgets.Good, 10f);
        }
        catch (Exception ex)
        {
            _s.Log.Error(ex, "UI: saving a Glamourer design failed.");
            SetStatus($"Saving the Glamourer design failed: {ex.Message}", Widgets.Bad);
        }
    }

    private void DrawTransferPopup(string id, Profile profile, PassGroup group, Pass? pass, bool move)
    {
        using var popup = ImRaii.Popup(id);
        if (!popup)
            return;
        ImGui.TextUnformatted(move ? "Move to:" : "Copy to:");
        ImGui.Separator();
        foreach (var target in _s.Profiles.Profiles)
        {
            using var pid = ImRaii.PushId(target.Id.ToString());
            if (pass is null)
            {
                if (target.Id == profile.Id)
                    continue;
                if (!ImGui.Selectable(target.Name))
                    continue;
                if (move)
                {
                    if (_s.Profiles.MoveGroupTo(profile, group, target))
                    {
                        Select(profile.Id);
                        SetStatus($"Moved group to {target.Name}.", Widgets.Good);
                    }
                }
                else
                {
                    _s.Profiles.CopyGroupTo(group, target);
                    SetStatus($"Copied group to {target.Name}.", Widgets.Good);
                }
                _s.Engine.Invalidate();
                continue;
            }

            if (target.PassGroups.Count == 0)
                continue;
            using var menu = ImRaii.Menu(target.Name);
            if (!menu)
                continue;
            foreach (var targetGroup in target.PassGroups)
            {
                if (targetGroup.Id == group.Id)
                    continue;
                using var gid = ImRaii.PushId(targetGroup.Id.ToString());
                if (!ImGui.MenuItem(targetGroup.Name))
                    continue;
                if (move)
                {
                    if (_s.Profiles.MovePassTo(profile, group, pass, target, targetGroup))
                    {
                        Select(profile.Id, group.Id);
                        SetStatus($"Moved pass to {target.Name} / {targetGroup.Name}.", Widgets.Good);
                    }
                }
                else
                {
                    _s.Profiles.CopyPassTo(pass, target, targetGroup);
                    SetStatus($"Copied pass to {target.Name} / {targetGroup.Name}.", Widgets.Good);
                }
                _s.Engine.Invalidate();
            }
        }
    }
}
