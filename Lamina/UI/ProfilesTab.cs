using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Lamina.Models;
using Lamina.Persistence;

namespace Lamina.UI;

public sealed partial class LaminaWindow
{
    private string _newProfileName = string.Empty;
    private Guid? _profileRenameId;
    private string _profileRenameBuffer = string.Empty;
    private Guid? _profileDeleteId;
    private ImportResult? _pendingImport;
    private Guid? _importTargetProfile;
    private Guid? _importTargetGroup;

    private void DrawProfilesTab()
    {
        Widgets.Paragraph("A profile is a complete Lamina setup. Only one profile can be active per target; enabling one disables the others for the same target. First build: the only target is your own character.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        var submit = ImGui.InputTextWithHint("##newProfile", "New profile name", ref _newProfileName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Create profile") || submit) && _newProfileName.Trim().Length > 0)
        {
            var p = _s.Profiles.CreateProfile(_newProfileName.Trim());
            _newProfileName = string.Empty;
            Select(p.Id);
            SetStatus($"Created profile {p.Name}.", Widgets.Good);
        }
        ImGui.SameLine();
        if (ImGui.Button("Import from clipboard"))
            BeginImport();
        Widgets.Tooltip("Imports a profile, pass group or pass exported by Lamina (JSON or LAMINA1: string).");
        ImGui.SameLine();
        DrawStatusLine();

        DrawImportPopup();

        ImGui.Separator();
        var local = Target.LocalPlayer();
        var active = _s.Profiles.ActiveProfileFor(local);
        Guid? toDelete    = null;
        Guid? toDuplicate = null;

        using var table = ImRaii.Table("##profiles", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table)
            return;
        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 34 * Widgets.Scale);
        ImGui.TableSetupColumn("Profile", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Targets", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 190 * Widgets.Scale);
        ImGui.TableHeadersRow();

        // Iterate a snapshot: row actions below may add or remove profiles, which must happen after the loop.
        foreach (var p in _s.Profiles.Profiles.ToList())
        {
            using var id = ImRaii.PushId(p.Id.ToString());
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var enabled = p.Enabled;
            if (ImGui.Checkbox("##en", ref enabled))
            {
                var disabled = _s.Profiles.SetProfileEnabled(p, enabled);
                _s.Engine.Invalidate();
                SetStatus(disabled.Count > 0 ? $"Enabled {p.Name}; disabled {string.Join(", ", disabled.Select(d => d.Name))}." : enabled ? $"Enabled {p.Name}." : $"Disabled {p.Name}.", Widgets.Good);
            }
            Widgets.Tooltip(enabled ? "Active. Click to disable (Lamina reverts your character)." : "Click to enable this profile for its targets.");

            ImGui.TableNextColumn();
            if (_profileRenameId == p.Id)
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##rename", ref _profileRenameBuffer, 64, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
                {
                    if (_profileRenameBuffer.Trim().Length > 0)
                        _s.Profiles.RenameProfile(p, _profileRenameBuffer.Trim());
                    _profileRenameId = null;
                }
                if (ImGui.IsItemDeactivated() && !ImGui.IsItemDeactivatedAfterEdit())
                    _profileRenameId = null;
            }
            else
            {
                if (ReferenceEquals(p, active))
                {
                    ImGui.TextColored(Widgets.Good, "●");
                    Widgets.Tooltip("Applied to the local player right now");
                    ImGui.SameLine();
                }
                if (ImGui.Selectable(p.Name, _editProfileId == p.Id, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    Select(p.Id);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        _profileRenameId     = p.Id;
                        _profileRenameBuffer = p.Name;
                    }
                }
                Widgets.Tooltip("Click to edit in the Passes tab. Double click to rename.");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.Join(", ", p.Targets.Select(t => t.ToString())));

            ImGui.TableNextColumn();
            var passes = p.PassGroups.Sum(g => g.Passes.Count);
            Widgets.Hint($"{p.PassGroups.Count} group(s), {passes} pass(es)");

            ImGui.TableNextColumn();
            if (Widgets.IconButton(FontAwesomeIcon.Edit, "Edit in Passes tab"))
                Select(p.Id);
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.Pen, "Rename"))
            {
                _profileRenameId     = p.Id;
                _profileRenameBuffer = p.Name;
            }
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.Clone, "Duplicate"))
                toDuplicate = p.Id;
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.FileExport, "Export to clipboard"))
            {
                ImGui.SetClipboardText(ProfileTransfer.ExportClipboard(p));
                SetStatus($"Profile {p.Name} copied to the clipboard.", Widgets.Good);
            }
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.Trash, "Delete profile"))
            {
                _profileDeleteId = p.Id;
                ImGui.OpenPopup("Delete profile?");
            }
            if (_profileDeleteId == p.Id && Widgets.ConfirmModal("Delete profile?", $"Delete profile \"{p.Name}\" with all its groups and passes? A backup copy is kept in the backups folder."))
                toDelete = p.Id;
        }

        if (toDuplicate is { } dup && _s.Profiles.DuplicateProfile(dup) is { } copy)
        {
            Select(copy.Id);
            SetStatus($"Duplicated as {copy.Name}.", Widgets.Good);
        }

        if (toDelete is { } del && _s.Profiles.DeleteProfile(del))
        {
            if (_editProfileId == del)
                Select(null);
            _s.Engine.Invalidate();
            _profileDeleteId = null;
            SetStatus("Profile deleted.", Widgets.Good);
        }
    }

    // ---- import --------------------------------------------------------------------------------

    private ReferenceResolvers Resolvers()
        => new(
            id => !_s.Glamourer.Available || _caches.DesignNames.ContainsKey(id),
            id => !_s.Vitrine.Available || _s.Vitrine.GetDesign(id) is not null,
            dir => !_s.Penumbra.Available || _caches.ModNames.ContainsKey(dir));

    private void BeginImport()
    {
        string text;
        try
        {
            text = ImGui.GetClipboardText();
        }
        catch
        {
            text = string.Empty;
        }
        if (!ProfileTransfer.LooksImportable(text))
        {
            SetStatus("Clipboard does not contain a Lamina export.", Widgets.Warn);
            return;
        }
        try
        {
            _pendingImport       = ProfileTransfer.Import(text, Resolvers());
            _importTargetProfile = _editProfileId ?? _s.Profiles.Profiles.FirstOrDefault()?.Id;
            _importTargetGroup   = null;
            ImGui.OpenPopup("Import");
        }
        catch (Exception ex)
        {
            _s.Log.Warning(ex, "Import failed.");
            SetStatus($"Import failed: {ex.Message}", Widgets.Bad);
        }
    }

    private void DrawImportPopup()
    {
        var open = true;
        using var modal = ImRaii.PopupModal("Import", ref open, ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal)
        {
            if (!open)
                _pendingImport = null;
            return;
        }
        if (_pendingImport is not { } import)
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.TextUnformatted($"Clipboard contains a {Widgets.Humanize(import.Kind.ToString()).ToLowerInvariant()}.");
        foreach (var w in import.Warnings)
            ImGui.TextColored(Widgets.Warn, "• " + w);
        ImGui.Spacing();

        var profiles = _s.Profiles.Profiles;
        switch (import.Kind)
        {
            case TransferKind.Profile when import.Profile is { } prof:
                ImGui.TextUnformatted($"Profile \"{prof.Name}\": {prof.PassGroups.Count} group(s). It is imported disabled with the local player as target.");
                if (ImGui.Button("Import profile", new Vector2(160 * Widgets.Scale, 0)))
                {
                    var created = _s.Profiles.CreateProfile(ProfileStore.UniqueName(prof.Name, profiles.Select(p => p.Name)));
                    foreach (var g in prof.PassGroups)
                        _s.Profiles.CopyGroupTo(g, created);
                    Select(created.Id);
                    SetStatus($"Imported profile {created.Name}.", Widgets.Good);
                    _pendingImport = null;
                    ImGui.CloseCurrentPopup();
                }
                break;

            case TransferKind.PassGroup when import.Group is { } group:
                ImGui.TextUnformatted($"Group \"{group.Name}\": {group.Passes.Count} pass(es). Choose the profile to add it to:");
                DrawImportProfilePicker(profiles);
                if (_importTargetProfile is { } tp && _s.Profiles.GetProfile(tp) is { } targetProfile && ImGui.Button("Import group", new Vector2(160 * Widgets.Scale, 0)))
                {
                    var copy = _s.Profiles.CopyGroupTo(group, targetProfile);
                    Select(targetProfile.Id, copy.Id);
                    _s.Engine.Invalidate();
                    SetStatus($"Imported group {copy.Name} into {targetProfile.Name}.", Widgets.Good);
                    _pendingImport = null;
                    ImGui.CloseCurrentPopup();
                }
                break;

            case TransferKind.Pass when import.Pass is { } pass:
                ImGui.TextUnformatted($"Pass \"{pass.Name}\": {pass.Layers.Count} layer(s). Choose the profile and group to add it to:");
                DrawImportProfilePicker(profiles);
                var tprof = _importTargetProfile is { } tpid ? _s.Profiles.GetProfile(tpid) : null;
                if (tprof is not null)
                {
                    var tgroup = _importTargetGroup is { } tg ? tprof.PassGroups.FirstOrDefault(g => g.Id == tg) : null;
                    ImGui.SetNextItemWidth(260 * Widgets.Scale);
                    using (var combo = ImRaii.Combo("Group##import", tgroup?.Name ?? "(pick a group)"))
                    {
                        if (combo)
                            foreach (var g in tprof.PassGroups)
                                if (ImGui.Selectable(g.Name, g.Id == _importTargetGroup))
                                    _importTargetGroup = g.Id;
                    }
                    if (tgroup is not null && ImGui.Button("Import pass", new Vector2(160 * Widgets.Scale, 0)))
                    {
                        var copy = _s.Profiles.CopyPassTo(pass, tprof, tgroup);
                        Select(tprof.Id, tgroup.Id, copy.Id);
                        _s.Engine.Invalidate();
                        SetStatus($"Imported pass {copy.Name} into {tprof.Name} / {tgroup.Name}.", Widgets.Good);
                        _pendingImport = null;
                        ImGui.CloseCurrentPopup();
                    }
                }
                break;

            default:
                ImGui.TextColored(Widgets.Bad, "Nothing importable was found.");
                break;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120 * Widgets.Scale, 0)))
        {
            _pendingImport = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawImportProfilePicker(IReadOnlyList<Profile> profiles)
    {
        var current = _importTargetProfile is { } id ? _s.Profiles.GetProfile(id) : null;
        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        using var combo = ImRaii.Combo("Profile##import", current?.Name ?? "(pick a profile)");
        if (!combo)
            return;
        foreach (var p in profiles)
            if (ImGui.Selectable(p.Name, p.Id == _importTargetProfile))
            {
                _importTargetProfile = p.Id;
                _importTargetGroup   = null;
            }
    }
}
