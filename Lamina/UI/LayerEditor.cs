using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Lamina.Models;
using Penumbra.Api.Enums;

namespace Lamina.UI;

/// <summary> Layer list of a pass with the Add Layer mini menu and one inline editor per layer kind. </summary>
internal sealed class LayerEditor
{
    private readonly LaminaServices _s;
    private readonly UiCaches _caches;
    private static readonly string[] ModToggleNames = { "Enable", "Disable" };

    public LayerEditor(LaminaServices services, UiCaches caches)
    {
        _s      = services;
        _caches = caches;
    }

    /// <summary> Draws the layer list. Returns true when the pass changed (already marked dirty). </summary>
    public bool Draw(Profile profile, Pass pass)
    {
        var changed = false;
        if (ImGui.Button("+ Add Layer"))
            ImGui.OpenPopup("##addLayer");
        ImGui.SameLine();
        Widgets.Hint(_s.Profiles.Config.Direction == EvaluationDirection.TopToBottom ? "lower layers override" : "upper layers override");

        using (var popup = ImRaii.Popup("##addLayer"))
        {
            if (popup)
                foreach (var (kind, display, create) in LayerKinds.All)
                {
                    var (available, why) = Availability(kind);
                    using var dis = ImRaii.Disabled(!available);
                    if (ImGui.Selectable(display) && available)
                    {
                        _s.Profiles.AddLayer(profile, pass, create());
                        changed = true;
                    }
                    if (!available)
                        Widgets.Tooltip(why);
                }
        }

        int? moveFrom = null, moveTo = null;
        Guid? remove = null;
        var layers = pass.Layers;
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            using var id = ImRaii.PushId(layer.Id.ToString());
            ImGui.Separator();

            var enabled = layer.Enabled;
            if (ImGui.Checkbox("##en", ref enabled))
            {
                layer.Enabled = enabled;
                _s.Profiles.MarkDirty(profile);
                changed = true;
            }
            ImGui.SameLine();
            var display = LayerKinds.All.FirstOrDefault(k => k.Kind == layer.Kind).DisplayName ?? layer.Kind;
            using (ImRaii.PushColor(ImGuiCol.Text, Widgets.Accent))
                ImGui.TextUnformatted(display);
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, layer.Enabled ? new Vector4(1, 1, 1, 1) : Widgets.Muted))
                ImGui.TextUnformatted(Summary(layer));
            var (avail, reason) = Availability(layer.Kind);
            if (!avail)
            {
                ImGui.SameLine();
                Widgets.Badge("(plugin missing)", Widgets.Warn, reason);
            }

            ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 84 * Widgets.Scale);
            if (Widgets.IconButton(FontAwesomeIcon.ArrowUp, "Move up", i > 0)) { moveFrom = i; moveTo = i - 1; }
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.ArrowDown, "Move down", i < layers.Count - 1)) { moveFrom = i; moveTo = i + 1; }
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(FontAwesomeIcon.Trash, "Remove layer")) remove = layer.Id;

            using var indent = ImRaii.PushIndent(22f * Widgets.Scale);
            if (DrawLayerFields(layer))
            {
                _s.Profiles.MarkDirty(profile);
                changed = true;
            }
        }
        if (layers.Count == 0)
            Widgets.Hint("No layers. A pass without layers changes nothing.");

        if (moveFrom is { } f && moveTo is { } t && _s.Profiles.MoveLayer(profile, pass, f, t))
            changed = true;
        if (remove is { } r && _s.Profiles.RemoveLayer(profile, pass, r))
            changed = true;
        return changed;
    }

    private (bool Available, string Reason) Availability(string kind)
        => kind switch
        {
            GlamourerLayer.KindName      => (_s.Glamourer.Available, "Glamourer is not loaded."),
            PenumbraAdjustLayer.KindName => (_s.Penumbra.Available, "Penumbra is not loaded."),
            VitrineDesignLayer.KindName  => (_s.Vitrine.Available, "Vitrine is not installed (separate plugin)."),
            HonorificLayer.KindName      => (_s.Honorific.Available, "Honorific is not loaded."),
            MoodlesLayer.KindName        => (_s.Moodles.Available, "Moodles is not loaded."),
            _                            => (false, "Unsupported layer kind."),
        };

    private string Summary(Layer layer)
        => layer switch
        {
            GlamourerLayer g     => _caches.DesignName(g.DesignId),
            VitrineDesignLayer v => _caches.VitrineDesignName(v.VitrineDesignId),
            PenumbraAdjustLayer p => $"{p.Mods.Count} mod(s)",
            HonorificLayer h     => h.Clear ? "clear title" : string.IsNullOrEmpty(h.Title) ? "(no title)" : h.Title,
            MoodlesLayer m       => $"+{m.Add.Count + m.AddPresets.Count} / −{m.Remove.Count}",
            _                    => layer.Describe(),
        };

    private bool DrawLayerFields(Layer layer)
        => layer switch
        {
            GlamourerLayer g      => DrawGlamourer(g),
            VitrineDesignLayer v  => DrawVitrine(v),
            PenumbraAdjustLayer p => DrawPenumbraAdjust(p),
            HonorificLayer h      => DrawHonorific(h),
            MoodlesLayer m        => DrawMoodles(m),
            UnknownLayer u        => DrawUnknown(u),
            _                     => false,
        };

    private bool DrawGlamourer(GlamourerLayer g)
    {
        var known   = _caches.DesignNames.ContainsKey(g.DesignId);
        var preview = g.DesignId == Guid.Empty ? "(pick a design)" : known ? _caches.DesignName(g.DesignId) : "(missing design)";
        var changed = false;
        if (Widgets.SearchCombo("Design##glam", preview, _caches.Designs, out var picked))
        {
            g.DesignId = picked;
            changed    = true;
        }
        if (g.DesignId != Guid.Empty && !known)
        {
            ImGui.TextColored(Widgets.Bad, _s.Glamourer.Available
                ? $"Broken link: design {g.DesignId} no longer exists in Glamourer. This layer is skipped."
                : "Glamourer is not loaded; the design cannot be verified.");
        }
        else if (_s.Profiles.Config.ApplyGlamourerModAssociations)
            Widgets.Hint("The design's mod associations are applied into the Lamina collection.");
        return changed;
    }

    private bool DrawVitrine(VitrineDesignLayer v)
    {
        var design  = _s.Vitrine.GetDesign(v.VitrineDesignId);
        var preview = v.VitrineDesignId == Guid.Empty ? "(pick a Vitrine design)" : design?.Name ?? "(missing design)";
        var changed = false;
        if (Widgets.SearchCombo("Vitrine design##vit", preview, _caches.VitrineDesigns, out var picked))
        {
            v.VitrineDesignId  = picked;
            v.SettingsOverride = null;
            changed            = true;
        }
        if (v.VitrineDesignId != Guid.Empty && design is null)
        {
            ImGui.TextColored(Widgets.Bad, _s.Vitrine.Available ? "Broken link: this Vitrine design no longer exists. The layer is skipped." : "Vitrine is not installed; the design cannot be verified and the layer is skipped.");
            return changed;
        }
        if (design is null)
            return changed;

        Widgets.Hint($"{Widgets.Humanize(design.Category)} · {design.Mods.Count} mod(s)" + (design.IsAuto ? string.Empty : " · variant"));
        var mod = design.Mods.FirstOrDefault();
        if (mod is null)
            return changed;
        var available = _caches.ModSettings(mod.ModDirectory);
        if (available is null || available.Count == 0)
            return changed;

        var hasOverride = v.SettingsOverride is not null;
        if (ImGui.Checkbox("Override settings##vit", ref hasOverride))
        {
            v.SettingsOverride = hasOverride ? new Dictionary<string, List<string>>() : null;
            changed            = true;
        }
        Widgets.Tooltip("Replaces whole option groups of the design's settings for this pass only.");
        if (v.SettingsOverride is { } overrides && DrawSettingsEditor("vitset", available, overrides))
            changed = true;
        return changed;
    }

    private bool DrawPenumbraAdjust(PenumbraAdjustLayer p)
    {
        var changed  = false;
        var removeAt = -1;
        using (var table = ImRaii.Table("##mods", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 90 * Widgets.Scale);
                ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 90 * Widgets.Scale);
                ImGui.TableSetupColumn("##x", ImGuiTableColumnFlags.WidthFixed, 30 * Widgets.Scale);
                ImGui.TableHeadersRow();
                for (var i = 0; i < p.Mods.Count; i++)
                {
                    var m = p.Mods[i];
                    using var id = ImRaii.PushId(i);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var known   = _caches.ModNames.ContainsKey(m.ModDirectory);
                    var preview = string.IsNullOrEmpty(m.ModDirectory) ? "(pick a mod)" : known ? _caches.ModName(m.ModDirectory) : $"(missing) {m.ModName ?? m.ModDirectory}";
                    if (Widgets.SearchCombo("##mod", preview, _caches.Mods, out var dir, 260))
                    {
                        m.ModDirectory = dir;
                        m.ModName      = _caches.ModName(dir);
                        m.Settings     = null;
                        changed        = true;
                    }
                    if (!known && !string.IsNullOrEmpty(m.ModDirectory))
                        Widgets.Tooltip(_s.Penumbra.Available ? "This mod is not installed any more; it is skipped." : "Penumbra is not loaded.");

                    ImGui.TableNextColumn();
                    var state = (int)m.State;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.Combo("##state", ref state, ModToggleNames, ModToggleNames.Length))
                    {
                        m.State = (ModToggle)state;
                        changed = true;
                    }

                    ImGui.TableNextColumn();
                    var prio = m.Priority ?? 0;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputInt("##prio", ref prio, 0, 0))
                    {
                        m.Priority = prio;
                        changed    = true;
                    }

                    ImGui.TableNextColumn();
                    if (Widgets.IconButton(FontAwesomeIcon.Trash, "Remove mod"))
                        removeAt = i;

                    var available = string.IsNullOrEmpty(m.ModDirectory) ? null : _caches.ModSettings(m.ModDirectory);
                    if (available is { Count: > 0 } && m.State == ModToggle.Enable)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        using var node = ImRaii.TreeNode(m.Settings is null ? "Settings (mod defaults)###set" : $"Settings ({m.Settings.Count} group(s) set)###set");
                        if (node)
                        {
                            var custom = m.Settings is not null;
                            if (ImGui.Checkbox("Custom settings##ps", ref custom))
                            {
                                m.Settings = custom ? new Dictionary<string, List<string>>() : null;
                                changed    = true;
                            }
                            if (m.Settings is { } settings && DrawSettingsEditor("pset", available, settings))
                                changed = true;
                        }
                    }
                }
            }
        }
        if (removeAt >= 0)
        {
            p.Mods.RemoveAt(removeAt);
            changed = true;
        }
        if (ImGui.SmallButton("+ Add mod"))
        {
            p.Mods.Add(new ModState());
            changed = true;
        }
        return changed;
    }

    /// <summary> Option group → selected options editor built from Penumbra's available settings. </summary>
    private static bool DrawSettingsEditor(string id, IReadOnlyDictionary<string, (string[] Options, GroupType Type)> available, Dictionary<string, List<string>> settings)
    {
        var changed = false;
        using var scope = ImRaii.PushId(id);
        foreach (var (group, (options, type)) in available.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            using var gid = ImRaii.PushId(group);
            var isSet = settings.TryGetValue(group, out var selected);
            if (ImGui.Checkbox("##set", ref isSet))
            {
                if (isSet)
                    settings[group] = new List<string>();
                else
                    settings.Remove(group);
                changed = true;
            }
            Widgets.Tooltip(isSet ? "This group is set by Lamina" : "Unchecked: the mod's own setting for this group is kept");
            ImGui.SameLine();
            ImGui.TextUnformatted(group);
            ImGui.SameLine();
            Widgets.Hint($"[{Widgets.Humanize(type.ToString())}]");
            if (!isSet || selected is null)
                continue;

            using var indent = ImRaii.PushIndent(20f * Widgets.Scale);
            var single = type == GroupType.Single;
            foreach (var option in options)
            {
                using var oid = ImRaii.PushId(option);
                var on = selected.Contains(option);
                if (single)
                {
                    if (ImGui.RadioButton(option, on) && !on)
                    {
                        selected.Clear();
                        selected.Add(option);
                        changed = true;
                    }
                }
                else if (ImGui.Checkbox(option, ref on))
                {
                    if (on) selected.Add(option);
                    else selected.Remove(option);
                    changed = true;
                }
            }
        }
        return changed;
    }

    private static bool DrawHonorific(HonorificLayer h)
    {
        var changed = false;
        var clear   = h.Clear;
        if (ImGui.Checkbox("Clear title##hon", ref clear))
        {
            h.Clear = clear;
            changed = true;
        }
        Widgets.Tooltip("Clears titles set through IPC (by Lamina or other plugins) instead of setting one; Honorific's own titles are not affected.");
        using var dis = ImRaii.Disabled(h.Clear);
        var title = h.Title ?? string.Empty;
        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        if (ImGui.InputText("Title##hon", ref title, 64))
        {
            h.Title = title;
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Prefix##hon", h.IsPrefix) && !h.IsPrefix) { h.IsPrefix = true; changed = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton("Suffix##hon", !h.IsPrefix) && h.IsPrefix) { h.IsPrefix = false; changed = true; }
        var color = h.Color;
        if (Widgets.NullableColor("Color##hon", ref color, new Vector3(1f, 1f, 1f))) { h.Color = color; changed = true; }
        var glow = h.Glow;
        if (Widgets.NullableColor("Glow##hon", ref glow, new Vector3(0.3f, 0.3f, 1f))) { h.Glow = glow; changed = true; }
        return changed;
    }

    private bool DrawMoodles(MoodlesLayer m)
    {
        var changed = Widgets.MultiSelect("Add moodles##md", _caches.Moodles, m.Add, id => id.ToString()[..8], "none");
        if (Widgets.MultiSelect("Apply presets##md", _caches.MoodlePresets, m.AddPresets, id => id.ToString()[..8], "none")) changed = true;
        if (Widgets.MultiSelect("Remove moodles##md", _caches.Moodles, m.Remove, id => id.ToString()[..8], "none")) changed = true;
        Widgets.Hint("Adds are kept while the pass matches and removed again when it stops matching.");
        return changed;
    }

    private static bool DrawUnknown(UnknownLayer u)
    {
        ImGui.TextColored(Widgets.Warn, $"Unsupported layer kind '{u.UnknownKind}' from a newer Lamina. It is skipped and kept as is.");
        return false;
    }
}
