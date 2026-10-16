using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Lamina.Decals;
using Lamina.Models;
using Lamina.Runtime.State;
using Penumbra.Api.Enums;

namespace Lamina.UI;

/// <summary> Layer list of a pass with the Add Layer mini menu and one inline editor per layer kind. </summary>
internal sealed class LayerEditor
{
    private readonly LaminaServices _s;
    private readonly UiCaches _caches;
    private readonly DecalPlacementMode _placement;
    private readonly FileDialogManager _fileDialogs;
    private readonly DecalImageStore _images;
    private readonly Dictionary<Guid, string> _decalErrors = new();
    private static readonly string[] ModToggleNames = { "Enable", "Disable" };
    private static readonly string[] GenderNames = { "Male", "Female" };
    private static readonly EquipSlot[] CrestSlots = { EquipSlot.Head, EquipSlot.Body, EquipSlot.OffHand };
    private static readonly (MetaIndex Index, string Label, string On, string Off)[] VisibilityToggles =
    {
        (MetaIndex.HatVisible,       "Hat",        "Shown",       "Hidden"),
        (MetaIndex.VisorToggled,     "Visor",      "Toggled",     "Not toggled"),
        (MetaIndex.WeaponVisible,    "Weapon",     "Shown",       "Hidden"),
        (MetaIndex.VieraEarsVisible, "Viera ears", "Shown",       "Hidden"),
    };

    public LayerEditor(LaminaServices services, UiCaches caches, DecalPlacementMode placement, FileDialogManager fileDialogs, DecalImageStore images)
    {
        _s           = services;
        _caches      = caches;
        _placement   = placement;
        _fileDialogs = fileDialogs;
        _images      = images;
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
            if (DrawLayerFields(profile, layer))
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
            EquipmentLayer.KindName      => (_s.Glamourer.Available, "Glamourer is not loaded."),
            EquipmentCleanseLayer.KindName => (_s.Glamourer.Available, "Glamourer is not loaded."),
            AppearanceLayer.KindName     => (_s.Glamourer.Available, "Glamourer is not loaded."),
            PenumbraAdjustLayer.KindName => (_s.Penumbra.Available, "Penumbra is not loaded."),
            VitrineDesignLayer.KindName  => (_s.Vitrine.Available, "Vitrine is not installed (separate plugin)."),
            HonorificLayer.KindName      => (_s.Honorific.Available, "Honorific is not loaded."),
            MoodlesLayer.KindName        => (_s.Moodles.Available, "Moodles is not loaded."),
            DecalLayer.KindName          => (_s.Penumbra.Available && _s.DecalPicker is not null && _s.DecalPublisher is not null, "Penumbra is not loaded or the decal feature could not start."),
            _                            => (false, "Unsupported layer kind."),
        };

    private string Summary(Layer layer)
        => layer switch
        {
            GlamourerLayer g      => _caches.DesignName(g.DesignId),
            VitrineDesignLayer v  => VitrineSummary(v),
            PenumbraAdjustLayer p => $"{p.Mods.Count} mod(s)",
            EquipmentLayer e      => EquipmentSummary(e),
            EquipmentCleanseLayer => "empties every gear slot (weapons stay)",
            AppearanceLayer a     => AppearanceSummary(a),
            HonorificLayer h      => h.Clear ? "clear title" : string.IsNullOrEmpty(h.Title) ? "(no title)" : h.Title,
            MoodlesLayer m        => $"+{m.Add.Count + m.AddPresets.Count} / −{m.Remove.Count}",
            DecalLayer d          => DecalSummary(d),
            _                     => layer.Describe(),
        };

    private string VitrineSummary(VitrineDesignLayer v)
    {
        var name   = _caches.VitrineDesignName(v.VitrineDesignId);
        var design = _s.Vitrine.GetDesign(v.VitrineDesignId);
        var text   = name;
        if (v.SlotOverride is { } mask)
        {
            var slots = design?.ResolvedPieces().Where(p => mask.Has(p.Slot)).Select(p => Widgets.Humanize(p.Slot.ToString())).ToList() ?? new List<string>();
            text = $"{name} · {(slots.Count == 0 ? "no slots" : string.Join(", ", slots))}";
        }
        if (v.ClearOtherSlotsOverride ?? design?.ClearOtherSlots ?? false)
            text += " · clears other slots";
        return Widgets.Truncate(text, 70);
    }

    private static bool DrawCleanse()
    {
        Widgets.Hint("Empties every gear slot (head, body, hands, legs, feet, ears, neck, wrists, rings). Weapons are left alone.\nPut it before the outfit layers so they start from a bare body; later layers fill the slots they set.");
        return false;
    }

    private string EquipmentSummary(EquipmentLayer e)
    {
        var parts = new List<string>();
        foreach (var (slot, edit) in e.Slots.OrderBy(kv => kv.Key))
        {
            if (edit.ItemId is { } item)
                parts.Add($"{Widgets.Humanize(slot.ToString())}: {_s.Catalog.ItemName(item)}");
            else if (edit.ApplyStains)
                parts.Add($"{Widgets.Humanize(slot.ToString())}: dyes");
            else if (edit.Crest is { } crest)
                parts.Add($"{Widgets.Humanize(slot.ToString())}: crest {(crest ? "on" : "off")}");
        }
        foreach (var (slot, id) in e.Bonus.OrderBy(kv => kv.Key))
        {
            parts.Add($"{slot}: {_s.Catalog.BonusItemName(id)}");
        }
        foreach (var (index, on) in e.Meta)
            parts.Add($"{Widgets.Humanize(index.ToString())} {(on ? "on" : "off")}");
        return parts.Count == 0 ? "(nothing set)" : Widgets.Truncate(string.Join(", ", parts), 70);
    }

    private static string AppearanceSummary(AppearanceLayer a)
    {
        var parts = a.Customize.Keys.OrderBy(k => k).Select(AppearanceCatalog.LabelOf).ToList();
        if (a.Wetness is { } wet)
            parts.Add($"wetness {(wet ? "on" : "off")}");
        parts.AddRange(a.Parameters.Keys.Select(k => AdvancedParameters.All.FirstOrDefault(p => p.Key == k).DisplayName ?? k));
        return parts.Count == 0 ? "(nothing set)" : Widgets.Truncate(string.Join(", ", parts), 70);
    }

    private bool DrawLayerFields(Profile profile, Layer layer)
        => layer switch
        {
            GlamourerLayer g      => DrawGlamourer(g),
            VitrineDesignLayer v  => DrawVitrine(v),
            PenumbraAdjustLayer p => DrawPenumbraAdjust(p),
            EquipmentLayer e      => DrawEquipment(e),
            EquipmentCleanseLayer => DrawCleanse(),
            AppearanceLayer a     => DrawAppearance(a),
            HonorificLayer h      => DrawHonorific(h),
            MoodlesLayer m        => DrawMoodles(m),
            DecalLayer d          => DrawDecal(profile, d),
            UnknownLayer u        => DrawUnknown(u),
            _                     => false,
        };

    // ---- Glamourer / Vitrine ------------------------------------------------------------------------

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
            v.SettingsOverride       = null;
            v.SlotOverride           = null;
            v.ClearOtherSlotsOverride = null;
            changed                  = true;
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
        if (available is { Count: > 0 })
        {
            var hasOverride = v.SettingsOverride is not null;
            if (ImGui.Checkbox("Override settings##vit", ref hasOverride))
            {
                v.SettingsOverride = hasOverride ? new Dictionary<string, List<string>>() : null;
                changed            = true;
            }
            Widgets.Tooltip("Replaces whole option groups of the design's settings for this pass only.");
            if (v.SettingsOverride is { } overrides && DrawSettingsEditor("vitset", available, overrides, null))
                changed = true;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Clear other slots");
        Widgets.Tooltip("Empties every gear slot this layer does not fill (weapons stay) before its pieces land, so an outfit worn before does not bleed through.\nMasked-out pieces count as not filled. Put the layer before anything it must not undo.");
        ImGui.SameLine();
        if (ImGui.RadioButton($"Design default ({(design.ClearOtherSlots ? "on" : "off")})##vitclear0", v.ClearOtherSlotsOverride is null) && v.ClearOtherSlotsOverride is not null)
        {
            v.ClearOtherSlotsOverride = null;
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("On##vitclear1", v.ClearOtherSlotsOverride == true) && v.ClearOtherSlotsOverride != true)
        {
            v.ClearOtherSlotsOverride = true;
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Off##vitclear2", v.ClearOtherSlotsOverride == false) && v.ClearOtherSlotsOverride != false)
        {
            v.ClearOtherSlotsOverride = false;
            changed = true;
        }

        var pieces = design.ResolvedPieces().ToList();
        if (pieces.Count == 0)
        {
            Widgets.Hint("This design equips no pieces (no equippable items, or Vitrine is older than 0.3): only its mod settings are layered.");
            return changed;
        }

        ImGui.Spacing();
        var hasSlots = v.SlotOverride is not null;
        if (ImGui.Checkbox("Override slots##vit", ref hasSlots))
        {
            v.SlotOverride = hasSlots ? pieces.Aggregate(EquipSlotFlags.None, (f, p) => f | p.Slot.ToFlag()) : null;
            changed        = true;
        }
        Widgets.Tooltip("Which of the design's pieces this layer equips. Untick slots to combine pieces from several Vitrine designs\n(the body of one mod, the feet of another). The mod itself is still applied in full.");
        if (v.SlotOverride is { } mask)
        {
            using var table = ImRaii.Table("##vitslots", 4, ImGuiTableFlags.SizingStretchSame);
            if (table)
                foreach (var (slot, piece) in pieces)
                {
                    ImGui.TableNextColumn();
                    var on = mask.Has(slot);
                    if (ImGui.Checkbox($"{Widgets.Humanize(slot.ToString())}##{slot}", ref on))
                    {
                        mask           = on ? mask | slot.ToFlag() : mask & ~slot.ToFlag();
                        v.SlotOverride = mask;
                        changed        = true;
                    }
                    Widgets.Tooltip(piece.ItemName);
                }
        }
        else
        {
            Widgets.Hint("Equips: " + Widgets.Truncate(string.Join(", ", pieces.Select(p => $"{Widgets.Humanize(p.Slot.ToString())}: {p.Piece.ItemName}")), 90));
        }

        return changed;
    }

    // ---- Penumbra Adjust ----------------------------------------------------------------------------

    private bool DrawPenumbraAdjust(PenumbraAdjustLayer p)
    {
        Widgets.Hint("Each part has an \"applies\" checkbox: unticked parts are inherited from an earlier pass, else from the Lamina collection.");
        var changed  = false;
        var removeAt = -1;
        using (var table = ImRaii.Table("##mods", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 120 * Widgets.Scale);
                ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 110 * Widgets.Scale);
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
                        PrefillFromPenumbra(m, stateAndPriority: true);
                        changed = true;
                    }
                    if (!known && !string.IsNullOrEmpty(m.ModDirectory))
                        Widgets.Tooltip(_s.Penumbra.Available ? "This mod is not installed any more; it is skipped." : "Penumbra is not loaded.");

                    var current = string.IsNullOrEmpty(m.ModDirectory) ? null : _caches.CurrentModSettings(m.ModDirectory);

                    ImGui.TableNextColumn();
                    var stateSet = m.State is not null;
                    if (ImGui.Checkbox("##stateSet", ref stateSet))
                    {
                        m.State = stateSet ? ModToggle.Enable : null;
                        changed = true;
                    }
                    Widgets.Tooltip(stateSet ? "This layer sets whether the mod is enabled." : $"Inherited ({(current?.Enabled == true ? "enabled" : "disabled")} in the collection).");
                    ImGui.SameLine();
                    using (ImRaii.Disabled(!stateSet))
                    {
                        var state = (int)(m.State ?? (current?.Enabled == true ? ModToggle.Enable : ModToggle.Disable));
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.Combo("##state", ref state, ModToggleNames, ModToggleNames.Length) && stateSet)
                        {
                            m.State = (ModToggle)state;
                            changed = true;
                        }
                    }

                    ImGui.TableNextColumn();
                    var prioSet = m.Priority is not null;
                    if (ImGui.Checkbox("##prioSet", ref prioSet))
                    {
                        m.Priority = prioSet ? current?.Priority ?? 0 : null;
                        changed    = true;
                    }
                    Widgets.Tooltip(prioSet ? "This layer sets the priority." : $"Inherited ({current?.Priority ?? 0} in the collection).");
                    ImGui.SameLine();
                    using (ImRaii.Disabled(!prioSet))
                    {
                        var prio = m.Priority ?? current?.Priority ?? 0;
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputInt("##prio", ref prio, 0, 0) && prioSet)
                        {
                            m.Priority = prio;
                            changed    = true;
                        }
                    }

                    ImGui.TableNextColumn();
                    if (Widgets.IconButton(FontAwesomeIcon.Trash, "Remove mod"))
                        removeAt = i;

                    var available = string.IsNullOrEmpty(m.ModDirectory) ? null : _caches.ModSettings(m.ModDirectory);
                    if (available is { Count: > 0 })
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        var setCount = m.Settings?.Count(kv => available.ContainsKey(kv.Key)) ?? 0;
                        using var node = ImRaii.TreeNode($"Settings ({setCount} of {available.Count} group(s) set)###set");
                        if (node)
                        {
                            if (ImGui.SmallButton("Apply all##ps"))
                            {
                                SetAllGroups(m, available, current?.Settings, allOptionsOn: true);
                                changed = true;
                            }
                            Widgets.Tooltip("Tick every group and turn every option on. Single-choice groups keep the collection's current choice.");
                            ImGui.SameLine();
                            if (ImGui.SmallButton("Disable all##ps"))
                            {
                                SetAllGroups(m, available, current?.Settings, allOptionsOn: false);
                                changed = true;
                            }
                            Widgets.Tooltip("Tick every group and turn every option off, so this layer switches the mod's options off. Single-choice groups keep the collection's current choice.");
                            ImGui.SameLine();
                            if (ImGui.SmallButton("Reset to Penumbra##ps"))
                            {
                                PrefillFromPenumbra(m, stateAndPriority: false);
                                changed = true;
                            }
                            Widgets.Tooltip("Tick every group with the collection's current values (Penumbra's defaults when the mod is not configured there).");
                            ImGui.SameLine();
                            if (ImGui.SmallButton("Inherit all##ps"))
                            {
                                m.Settings = null;
                                changed    = true;
                            }
                            Widgets.Tooltip("Untick every group: all of them are inherited (earlier pass, else the collection).");
                            m.Settings ??= new Dictionary<string, List<string>>();
                            if (DrawSettingsEditor("pset", available, m.Settings, current?.Settings))
                                changed = true;
                            if (m.Settings.Count == 0)
                                m.Settings = null;
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
        ImGui.SameLine();
        if (ImGui.SmallButton("Re-read collection"))
            _caches.ForgetCurrentModSettings();
        Widgets.Tooltip("Refreshes the inherited values shown here after you changed the mod in Penumbra.");
        return changed;
    }

    /// <summary>
    /// Fills a mod entry from the collection's current settings (Penumbra's defaults when the mod is not configured there):
    /// every option group ticked with the collection's value and, with <paramref name="stateAndPriority"/> (a freshly
    /// picked mod), enabled plus the collection's priority.
    /// </summary>
    private void PrefillFromPenumbra(ModState m, bool stateAndPriority)
    {
        var available = _caches.ModSettings(m.ModDirectory);
        var current   = _caches.CurrentModSettings(m.ModDirectory);
        if (stateAndPriority)
        {
            m.State    = ModToggle.Enable;
            m.Priority = current?.Priority ?? 0;
        }
        if (available is null)
            return;

        var settings = m.Settings ?? new Dictionary<string, List<string>>();
        foreach (var (group, (options, type)) in available)
            settings[group] = DefaultOptions(group, options, type, current?.Settings);

        m.Settings = settings.Count == 0 ? null : settings;
    }

    /// <summary>
    /// Ticks every group. Multi-select groups get every option on (<paramref name="allOptionsOn"/>) or off; single-select
    /// groups keep the collection's current choice (else Penumbra's default), since they cannot be "all" or "none".
    /// </summary>
    private static void SetAllGroups(ModState m, IReadOnlyDictionary<string, (string[] Options, GroupType Type)> available,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? current, bool allOptionsOn)
    {
        var settings = m.Settings ?? new Dictionary<string, List<string>>();
        foreach (var (group, (options, type)) in available)
            settings[group] = type == GroupType.Single
                ? DefaultOptions(group, options, type, current)
                : allOptionsOn ? options.ToList() : new List<string>();
        m.Settings = settings.Count == 0 ? null : settings;
    }

    /// <summary> The collection's current options for a group, else Penumbra's default (first option of a single-select group, nothing for multi-select). </summary>
    private static List<string> DefaultOptions(string group, string[] options, GroupType type, IReadOnlyDictionary<string, IReadOnlyList<string>>? current)
    {
        if (current is not null && current.TryGetValue(group, out var selected))
            return selected.Where(options.Contains).ToList();
        return type == GroupType.Single && options.Length > 0 ? new List<string> { options[0] } : new List<string>();
    }

    /// <summary>
    /// Option group → selected options editor built from Penumbra's available settings. Every group has an "applies"
    /// checkbox; unticked groups show the inherited value dimmed when <paramref name="inherited"/> is known.
    /// </summary>
    private static bool DrawSettingsEditor(string id, IReadOnlyDictionary<string, (string[] Options, GroupType Type)> available,
        Dictionary<string, List<string>> settings, IReadOnlyDictionary<string, IReadOnlyList<string>>? inherited)
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
                    settings[group] = DefaultOptions(group, options, type, inherited);
                else
                    settings.Remove(group);
                changed = true;
            }
            Widgets.Tooltip(isSet ? "This group is set by this layer" : "Unticked: the group is inherited (earlier pass, else the collection)");
            ImGui.SameLine();
            ImGui.TextUnformatted(group);
            ImGui.SameLine();
            Widgets.Hint($"[{Widgets.Humanize(type.ToString())}]");
            if (!isSet || selected is null)
            {
                if (inherited is not null && inherited.TryGetValue(group, out var inheritedOptions))
                {
                    ImGui.SameLine();
                    Widgets.Hint($"inherits: {(inheritedOptions.Count == 0 ? "(none)" : string.Join(", ", inheritedOptions))}");
                }
                continue;
            }

            var single = type == GroupType.Single;
            if (!single && options.Length > 1)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("all##g"))
                {
                    selected.Clear();
                    selected.AddRange(options);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("none##g"))
                {
                    selected.Clear();
                    changed = true;
                }
            }
            using var indent = ImRaii.PushIndent(20f * Widgets.Scale);
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

    // ---- Equipment Changes --------------------------------------------------------------------------

    private static string BonusSlotLabel(BonusSlot slot)
        => slot == BonusSlot.Glasses ? "Facewear" : slot.ToString();

    private bool DrawEquipment(EquipmentLayer e)
    {
        var changed = false;
        EquipSlot? removeSlot  = null;
        BonusSlot? removeBonus = null;
        using (var table = ImRaii.Table("##slots", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 80 * Widgets.Scale);
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("Dyes", ImGuiTableColumnFlags.WidthStretch, 2.4f);
                ImGui.TableSetupColumn("Crest", ImGuiTableColumnFlags.WidthFixed, 110 * Widgets.Scale);
                ImGui.TableSetupColumn("##x", ImGuiTableColumnFlags.WidthFixed, 30 * Widgets.Scale);
                ImGui.TableHeadersRow();

                foreach (var slot in Enum.GetValues<EquipSlot>())
                {
                    if (!e.Slots.TryGetValue(slot, out var edit))
                        continue;
                    using var id = ImRaii.PushId((int)slot);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(Widgets.Humanize(slot.ToString()));

                    ImGui.TableNextColumn();
                    var itemSet = edit.ItemId is not null;
                    if (ImGui.Checkbox("##itemSet", ref itemSet))
                    {
                        edit.ItemId = itemSet ? NothingItems.IdFor(slot) ?? 0 : null;
                        changed     = true;
                    }
                    Widgets.Tooltip(itemSet ? "This layer sets the item." : "Unticked: the item in the slot is kept.");
                    ImGui.SameLine();
                    using (ImRaii.Disabled(!itemSet))
                    {
                        var preview = edit.ItemId is { } current ? _s.Catalog.ItemName(current) : "(keep)";
                        if (Widgets.SearchCombo("##item", preview, _caches.ItemsFor(slot), out var picked, 240) && itemSet)
                        {
                            edit.ItemId = picked;
                            changed     = true;
                        }
                    }

                    ImGui.TableNextColumn();
                    var dyesSet = edit.ApplyStains;
                    if (ImGui.Checkbox("##dyesSet", ref dyesSet))
                    {
                        edit.ApplyStains = dyesSet;
                        changed          = true;
                    }
                    Widgets.Tooltip(dyesSet ? "This layer sets both dyes (on the item above, or on whatever is in the slot)." : "Unticked: dyes are kept.");
                    ImGui.SameLine();
                    using (ImRaii.Disabled(!dyesSet))
                    {
                        var s1 = edit.Stain1;
                        if (StainCombo("##s1", ref s1) && dyesSet) { edit.Stain1 = s1; changed = true; }
                        ImGui.SameLine(0, 4);
                        var s2 = edit.Stain2;
                        if (StainCombo("##s2", ref s2) && dyesSet) { edit.Stain2 = s2; changed = true; }
                    }

                    ImGui.TableNextColumn();
                    if (CrestSlots.Contains(slot))
                    {
                        var crest = edit.Crest;
                        ImGui.SetNextItemWidth(-1);
                        if (Widgets.TriState("##crest", ref crest, "Keep"))
                        {
                            edit.Crest = crest;
                            changed    = true;
                        }
                        Widgets.Tooltip("Free company crest on the piece.");
                    }

                    ImGui.TableNextColumn();
                    if (Widgets.IconButton(FontAwesomeIcon.Trash, "Remove slot"))
                        removeSlot = slot;
                }

                // Bonus slots (facewear): item only, no dyes or crest.
                foreach (var slot in Enum.GetValues<BonusSlot>())
                {
                    if (!e.Bonus.TryGetValue(slot, out var bonusId))
                        continue;
                    using var id = ImRaii.PushId(1000 + (int)slot);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(BonusSlotLabel(slot));

                    ImGui.TableNextColumn();
                    var on = true;
                    using (ImRaii.Disabled())
                        ImGui.Checkbox("##bonusSet", ref on);
                    Widgets.Tooltip("This layer sets the facewear (remove the row to keep it).");
                    ImGui.SameLine();
                    if (Widgets.SearchCombo("##bonus", _s.Catalog.BonusItemName(bonusId), _caches.BonusItemsFor(slot), out var picked, 240))
                    {
                        e.Bonus[slot] = picked;
                        changed       = true;
                    }

                    ImGui.TableNextColumn();
                    Widgets.Hint("no dyes");
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    if (Widgets.IconButton(FontAwesomeIcon.Trash, "Remove slot"))
                        removeBonus = slot;
                }
            }
        }
        if (removeSlot is { } rs)
        {
            e.Slots.Remove(rs);
            changed = true;
        }
        if (removeBonus is { } rb)
        {
            e.Bonus.Remove(rb);
            changed = true;
        }

        var free      = Enum.GetValues<EquipSlot>().Where(s => !e.Slots.ContainsKey(s)).ToList();
        var freeBonus = Enum.GetValues<BonusSlot>().Where(s => !e.Bonus.ContainsKey(s)).ToList();
        if (free.Count + freeBonus.Count > 0)
        {
            if (ImGui.SmallButton("+ Add slot"))
                ImGui.OpenPopup("##addSlot");
            using var popup = ImRaii.Popup("##addSlot");
            if (popup)
            {
                foreach (var slot in free)
                    if (ImGui.Selectable(Widgets.Humanize(slot.ToString())))
                    {
                        e.Slots[slot] = new SlotEdit();
                        changed       = true;
                    }
                foreach (var slot in freeBonus)
                    if (ImGui.Selectable(BonusSlotLabel(slot)))
                    {
                        e.Bonus[slot] = BonusItems.NothingFor(slot);
                        changed       = true;
                    }
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Visibility");
        foreach (var (index, label, on, off) in VisibilityToggles)
        {
            using var id = ImRaii.PushId((int)index);
            bool? value = e.Meta.TryGetValue(index, out var v) ? v : null;
            var   idx   = value switch { null => 0, true => 1, false => 2 };
            var   names = new[] { "Keep", on, off };
            ImGui.SetNextItemWidth(130 * Widgets.Scale);
            if (ImGui.Combo(label, ref idx, names, names.Length))
            {
                if (idx == 0) e.Meta.Remove(index);
                else e.Meta[index] = idx == 1;
                changed = true;
            }
            if (index != VisibilityToggles[^1].Index)
                ImGui.SameLine();
        }
        return changed;
    }

    /// <summary> Dye picker: colour swatch + name, all 128 dyes. </summary>
    private bool StainCombo(string id, ref byte stain)
    {
        var changed = false;
        var stains  = _s.Catalog.Stains;
        var wanted  = stain;
        var current = stains.FirstOrDefault(s => s.Id == wanted);
        using var scope = ImRaii.PushId(id);
        if (current is not null && current.Id != 0)
        {
            ImGui.ColorButton("##cur", RgbToVec(current.Rgb), ImGuiColorEditFlags.NoTooltip, new Vector2(ImGui.GetFrameHeight()));
            ImGui.SameLine(0, 2);
        }
        ImGui.SetNextItemWidth(130 * Widgets.Scale);
        using var combo = ImRaii.Combo("##combo", current?.Name ?? (stain == 0 ? "No dye" : $"#{stain}"));
        if (!combo)
            return false;
        if (ImGui.IsWindowAppearing())
        {
            _stainSearch = string.Empty;
            ImGui.SetKeyboardFocusHere();
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search…", ref _stainSearch, 64);
        var filter = _stainSearch.Trim();
        using var list = ImRaii.Child("##list", new Vector2(0, 240 * Widgets.Scale), false);
        if (!list)
            return false;
        foreach (var s in stains)
        {
            var name = s.Id == 0 ? "No dye" : s.Name;
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            using var sid = ImRaii.PushId(s.Id);
            if (s.Id != 0)
            {
                ImGui.ColorButton("##c", RgbToVec(s.Rgb), ImGuiColorEditFlags.NoTooltip, new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine();
            }
            if (ImGui.Selectable(name, s.Id == stain))
            {
                stain   = s.Id;
                changed = true;
                ImGui.CloseCurrentPopup();
            }
        }
        return changed;
    }

    private string _stainSearch = string.Empty;

    private static Vector4 RgbToVec(uint rgb)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    // ---- Appearance Changes -------------------------------------------------------------------------

    private bool DrawAppearance(AppearanceLayer a)
    {
        var changed = false;
        var current = AppearanceCatalog.FromCustomizeBytes(_caches.CurrentCustomize());

        // Values are listed for one clan + gender: the layer's own when it sets them, else the character's.
        var clan   = a.Customize.TryGetValue(CustomizeIndex.Clan, out var c) ? c : current?.GetValueOrDefault(CustomizeIndex.Clan) ?? 0;
        var gender = a.Customize.TryGetValue(CustomizeIndex.Gender, out var g) ? g : current?.GetValueOrDefault(CustomizeIndex.Gender) ?? 0;
        if (clan is < 1 or > 16)
        {
            Widgets.Hint("Not logged in: pickers show values for Midlander male until the character is known (or set Clan / Gender below).");
            clan = 1;
        }
        var set     = _s.Catalog.Appearance.For(clan, gender);
        var clanName = _s.Catalog.Appearance.Clans.FirstOrDefault(t => t.Id == clan).Name ?? $"clan {clan}";
        Widgets.Hint($"Values shown for {clanName} {GenderNames[gender == 0 ? 0 : 1]}. Unticked fields pass through.");

        using (var table = ImRaii.Table("##fields", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            if (table)
            {
                ImGui.TableSetupColumn("##set", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
                ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 190 * Widgets.Scale);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

                foreach (var index in AppearanceCatalog.EditorOrder)
                {
                    var kind = AppearanceCatalog.KindOf(index);
                    if (index == CustomizeIndex.TailShape && !set.HasTail && !a.Customize.ContainsKey(index))
                        continue;
                    if (kind == AppearanceFieldKind.List && set.ListSizes.GetValueOrDefault(index) == 0 && !a.Customize.ContainsKey(index) && current is null)
                        continue;

                    using var id = ImRaii.PushId((int)index);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var isSet = a.Customize.TryGetValue(index, out var value);
                    if (ImGui.Checkbox("##on", ref isSet))
                    {
                        if (isSet)
                            value = a.Customize[index] = current?.GetValueOrDefault(index) ?? DefaultValue(index, set);
                        else
                            a.Customize.Remove(index);
                        changed = true;
                    }
                    Widgets.Tooltip(isSet ? "Set by this layer." : "Unticked: passes through.");

                    ImGui.TableNextColumn();
                    using (ImRaii.PushColor(ImGuiCol.Text, Widgets.Muted, !isSet))
                        ImGui.TextUnformatted(AppearanceCatalog.LabelOf(index));

                    ImGui.TableNextColumn();
                    if (!isSet)
                    {
                        if (current is { } cur && cur.TryGetValue(index, out var now))
                            Widgets.Hint($"current: {DescribeValue(index, now, set)}");
                        continue;
                    }

                    var faceForIcons = a.Customize.TryGetValue(CustomizeIndex.Face, out var face) ? face : current?.GetValueOrDefault(CustomizeIndex.Face) ?? 1;
                    if (DrawCustomizeValue(index, kind, ref value, set, faceForIcons))
                    {
                        a.Customize[index] = value;
                        changed            = true;
                    }
                }

                // Wetness lives with the customization in Glamourer.
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var wetSet = a.Wetness is not null;
                if (ImGui.Checkbox("##wet", ref wetSet))
                {
                    a.Wetness = wetSet ? true : null;
                    changed   = true;
                }
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, Widgets.Muted, !wetSet))
                    ImGui.TextUnformatted("Wetness");
                ImGui.TableNextColumn();
                if (wetSet)
                {
                    var wet = a.Wetness == true;
                    if (ImGui.Checkbox(wet ? "wet##w" : "dry##w", ref wet))
                    {
                        a.Wetness = wet;
                        changed   = true;
                    }
                }
            }
        }

        ImGui.Spacing();
        using var adv = ImRaii.TreeNode($"Advanced colours and parameters ({a.Parameters.Count} set)###adv");
        if (!adv)
            return changed;
        Widgets.Hint("Exact colours (linear RGB, like Glamourer's advanced customization). Unticked parameters pass through.");
        foreach (var (key, name, kind) in AdvancedParameters.All)
        {
            using var id = ImRaii.PushId(key);
            var isSet = a.Parameters.TryGetValue(key, out var value);
            if (ImGui.Checkbox("##on", ref isSet))
            {
                if (isSet)
                    a.Parameters[key] = new AdvancedParameter { Scalar = kind == AdvancedParameterKind.Value ? 1f : 0f };
                else
                    a.Parameters.Remove(key);
                changed = true;
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(!isSet))
            {
                value ??= new AdvancedParameter();
                ImGui.SetNextItemWidth(220 * Widgets.Scale);
                switch (kind)
                {
                    case AdvancedParameterKind.Rgb:
                    {
                        var color = value.Color;
                        if (ImGui.ColorEdit3(name, ref color, ImGuiColorEditFlags.Float) && isSet) { value.Color = color; changed = true; }
                        break;
                    }
                    case AdvancedParameterKind.Rgba:
                    {
                        var color = new Vector4(value.Color, value.Alpha);
                        if (ImGui.ColorEdit4(name, ref color, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaBar) && isSet)
                        {
                            value.Color = new Vector3(color.X, color.Y, color.Z);
                            value.Alpha = color.W;
                            changed     = true;
                        }
                        break;
                    }
                    case AdvancedParameterKind.Percentage:
                    {
                        var pct = value.Scalar * 100f;
                        if (ImGui.SliderFloat(name, ref pct, 0f, 100f, "%.0f %%") && isSet) { value.Scalar = pct / 100f; changed = true; }
                        break;
                    }
                    default:
                    {
                        var v = value.Scalar;
                        if (ImGui.InputFloat(name, ref v, 0.1f, 1f, "%.2f") && isSet) { value.Scalar = v; changed = true; }
                        break;
                    }
                }
            }
        }
        return changed;
    }

    private static int DefaultValue(CustomizeIndex index, AppearanceSet set)
        => AppearanceCatalog.KindOf(index) switch
        {
            AppearanceFieldKind.Icons   => set.IconsFor(index).FirstOrDefault().Value,
            AppearanceFieldKind.Percentage => 50,
            AppearanceFieldKind.Number  => index == CustomizeIndex.Clan ? set.Clan : index == CustomizeIndex.Gender ? set.Gender : 1,
            _                           => 0,
        };

    private string DescribeValue(CustomizeIndex index, int value, AppearanceSet set)
        => AppearanceCatalog.KindOf(index) switch
        {
            AppearanceFieldKind.Flag          => value != 0 ? "on" : "off",
            AppearanceFieldKind.FacialFeature => value != 0 ? "on" : "off",
            AppearanceFieldKind.Percentage    => $"{value} %",
            AppearanceFieldKind.List          => $"{value + 1}",
            AppearanceFieldKind.Number when index == CustomizeIndex.Clan => _s.Catalog.Appearance.Clans.FirstOrDefault(t => t.Id == value).Name ?? value.ToString(),
            AppearanceFieldKind.Number when index == CustomizeIndex.Gender => GenderNames[value == 0 ? 0 : 1],
            AppearanceFieldKind.Icons         => set.IconsFor(index).FirstOrDefault(o => o.Value == value) is { Label.Length: > 0 } o ? o.Label : value.ToString(),
            _                                 => value.ToString(),
        };

    /// <summary> Editor for one customization value. </summary>
    private bool DrawCustomizeValue(CustomizeIndex index, AppearanceFieldKind kind, ref int value, AppearanceSet set, int faceForIcons)
    {
        switch (kind)
        {
            case AppearanceFieldKind.Flag:
            {
                var on = value != 0;
                if (!ImGui.Checkbox(on ? "on##f" : "off##f", ref on))
                    return false;
                value = on ? AppearanceCatalog.FlagOn : 0;
                return true;
            }
            case AppearanceFieldKind.FacialFeature:
            {
                var on = value != 0;
                if (set.FacialFeatureIcons.TryGetValue(faceForIcons, out var icons))
                {
                    DrawIcon(icons[index - CustomizeIndex.FacialFeature1], ImGui.GetFrameHeight() * 1.6f);
                    ImGui.SameLine();
                }
                if (!ImGui.Checkbox(on ? "on##f" : "off##f", ref on))
                    return false;
                value = on ? AppearanceCatalog.FacialFeatureBit(index) : 0;
                return true;
            }
            case AppearanceFieldKind.Percentage:
            {
                ImGui.SetNextItemWidth(220 * Widgets.Scale);
                return ImGui.SliderInt("##p", ref value, 0, 100, "%d %%");
            }
            case AppearanceFieldKind.List:
            {
                var size = set.ListSizes.GetValueOrDefault(index);
                if (size <= 0)
                    return NumberInput(ref value);
                var names = Enumerable.Range(1, size).Select(i => $"{i}").ToArray();
                var idx   = Math.Clamp(value, 0, size - 1);
                ImGui.SetNextItemWidth(120 * Widgets.Scale);
                if (!ImGui.Combo("##l", ref idx, names, names.Length) || idx == value)
                    return false;
                value = idx;
                return true;
            }
            case AppearanceFieldKind.Number when index == CustomizeIndex.Clan:
            {
                var clans   = _s.Catalog.Appearance.Clans;
                var names   = clans.Select(t => t.Name).ToArray();
                var wanted  = value;
                var idx     = Math.Max(0, clans.ToList().FindIndex(t => t.Id == wanted));
                ImGui.SetNextItemWidth(200 * Widgets.Scale);
                if (names.Length == 0 || !ImGui.Combo("##clan", ref idx, names, names.Length) || clans[idx].Id == value)
                    return false;
                value = clans[idx].Id;
                return true;
            }
            case AppearanceFieldKind.Number when index == CustomizeIndex.Gender:
            {
                var idx = value == 0 ? 0 : 1;
                ImGui.SetNextItemWidth(120 * Widgets.Scale);
                if (!ImGui.Combo("##g", ref idx, GenderNames, GenderNames.Length) || idx == value)
                    return false;
                value = idx;
                return true;
            }
            case AppearanceFieldKind.Number:
                return NumberInput(ref value);
            case AppearanceFieldKind.Colors:
                return ColorPicker(index, ref value, set.ColorsFor(index));
            case AppearanceFieldKind.Icons:
                return IconPicker(index, ref value, set.IconsFor(index));
            default:
                return NumberInput(ref value);
        }
    }

    private static bool NumberInput(ref int value)
    {
        ImGui.SetNextItemWidth(120 * Widgets.Scale);
        var v = value;
        if (!ImGui.InputInt("##n", ref v, 1, 10) || v == value)
            return false;
        value = Math.Clamp(v, 0, 255);
        return true;
    }

    private bool ColorPicker(CustomizeIndex index, ref int value, IReadOnlyList<ColorOption> colors)
    {
        if (colors.Count == 0)
            return NumberInput(ref value);
        var wanted  = value;
        var current = colors.FirstOrDefault(c => c.Value == wanted);
        var swatch  = current.Rgba == 0 && current.Value != value ? new Vector4(0.3f, 0.3f, 0.3f, 1f) : new Vector4(current.R, current.G, current.B, 1f);
        if (ImGui.ColorButton("##cur", swatch, ImGuiColorEditFlags.NoTooltip, new Vector2(ImGui.GetFrameHeight() * 1.6f, ImGui.GetFrameHeight())))
            ImGui.OpenPopup("##colors");
        ImGui.SameLine();
        ImGui.TextUnformatted($"#{value}");
        var changed = false;
        using var popup = ImRaii.Popup("##colors");
        if (!popup)
            return false;
        var size = ImGui.GetFrameHeight() * 1.25f;
        var perRow = 16;
        for (var i = 0; i < colors.Count; i++)
        {
            var c = colors[i];
            using var cid = ImRaii.PushId(c.Value);
            if (i % perRow != 0)
                ImGui.SameLine(0, 2);
            var flags = ImGuiColorEditFlags.NoTooltip | (c.Value == value ? ImGuiColorEditFlags.None : ImGuiColorEditFlags.NoBorder);
            if (ImGui.ColorButton("##c", new Vector4(c.R, c.G, c.B, 1f), flags, new Vector2(size)))
            {
                value   = c.Value;
                changed = true;
                ImGui.CloseCurrentPopup();
            }
            Widgets.Tooltip($"#{c.Value}");
        }
        return changed;
    }

    private bool IconPicker(CustomizeIndex index, ref int value, IReadOnlyList<AppearanceOption> options)
    {
        if (options.Count == 0)
            return NumberInput(ref value);
        var wanted  = value;
        var current = options.FirstOrDefault(o => o.Value == wanted);
        var size    = ImGui.GetFrameHeight() * 2f;
        if (current.IconId != 0)
            DrawIcon(current.IconId, size);
        else
            ImGui.Dummy(new Vector2(size));
        ImGui.SameLine();
        if (ImGui.Button(current.Label is { Length: > 0 } ? current.Label : $"#{value}"))
            ImGui.OpenPopup("##icons");
        var changed = false;
        using var popup = ImRaii.Popup("##icons");
        if (!popup)
            return false;
        var big    = ImGui.GetFrameHeight() * 2.6f;
        var perRow = 8;
        for (var i = 0; i < options.Count; i++)
        {
            var o = options[i];
            using var oid = ImRaii.PushId(o.Value);
            if (i % perRow != 0)
                ImGui.SameLine(0, 4);
            var pos = ImGui.GetCursorScreenPos();
            if (o.IconId != 0)
                DrawIcon(o.IconId, big);
            else
                ImGui.Button(o.Label, new Vector2(big));
            if (o.Value == value)
                ImGui.GetWindowDrawList().AddRect(pos, pos + new Vector2(big), ImGui.GetColorU32(Widgets.Accent), 3f, ImDrawFlags.None, 2f);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(o.Label);
            if (ImGui.IsItemClicked())
            {
                value   = o.Value;
                changed = true;
                ImGui.CloseCurrentPopup();
            }
        }
        return changed;
    }

    private void DrawIcon(uint iconId, float size)
    {
        var wrap = iconId == 0 ? null : _s.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault();
        if (wrap is null)
        {
            ImGui.Dummy(new Vector2(size));
            return;
        }
        ImGui.Image(wrap.Handle, new Vector2(size));
    }

    // ---- Honorific / Moodles / unknown --------------------------------------------------------------

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

    // ---- Decal --------------------------------------------------------------------------------------

    /// <summary> Size a decal gets when it is first placed: 10 cm. </summary>
    private const float DefaultDecalSize = 0.10f;

    private string DecalSummary(DecalLayer d)
    {
        var imageNote = string.IsNullOrEmpty(d.ImageFile) ? "no image" : _images.Exists(d.ImageFile) ? null : "missing image";
        if (d.Placement is not { } p)
            return imageNote is null ? "not placed yet" : $"{imageNote}, not placed";
        var text = $"on the {SurfaceName(p.Surface)}{(string.IsNullOrEmpty(p.PartName) ? string.Empty : $": {p.PartName}")} · {MathF.Round(p.Size * 100f):0} cm";
        if (d.Opacity < 0.995f)
            text += $" · {MathF.Round(d.Opacity * 100f):0} %";
        if (imageNote is not null)
            text = $"({imageNote}) {text}";
        return Widgets.Truncate(text, 70);
    }

    private static string SurfaceName(DecalSurface surface)
        => surface.ToString().ToLowerInvariant();

    /// <summary>
    /// One decal: image thumbnail with the picker, Place / Cancel placing with a status line, and the Size, Rotation and
    /// Opacity sliders. Slider values follow the drag; the profile is marked (and the decal re-baked) when the slider is released.
    /// </summary>
    private bool DrawDecal(Profile profile, DecalLayer d)
    {
        var changed = false;
        var scale   = Widgets.Scale;

        // Image: 64 px thumbnail, the picker and the file name.
        var box      = 64f * scale;
        var origin   = ImGui.GetCursorScreenPos();
        var hasImage = !string.IsNullOrEmpty(d.ImageFile);
        var exists   = hasImage && _images.Exists(d.ImageFile);
        ImGui.Dummy(new Vector2(box));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRect(origin, origin + new Vector2(box), ImGui.GetColorU32(ImGuiCol.Border), 3f * scale);
        if (exists)
        {
            var wrap = _s.TextureProvider.GetFromFileAbsolute(_images.FullPath(d.ImageFile!)).GetWrapOrEmpty();
            var fit  = FitInto(wrap.Size, box - 6f * scale);
            var min  = origin + (new Vector2(box) - fit) / 2f;
            draw.AddImage(wrap.Handle, min, min + fit);
        }
        Widgets.Tooltip(exists ? d.ImageFile! : "No image yet.");
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            if (ImGui.Button("Choose image…##decal"))
                OpenDecalImageDialog(profile, d);
            Widgets.Tooltip("PNG or JPG. The file is copied into Lamina's config folder, so the original may move afterwards.");
            if (!hasImage)
                Widgets.Hint("No image chosen.");
            else if (!exists)
                ImGui.TextColored(Widgets.Bad, $"Missing image: {d.ImageFile}");
            else
                Widgets.Hint(d.ImageFile!);
            if (_decalErrors.TryGetValue(d.Id, out var error))
                ImGui.TextColored(Widgets.Bad, error);
        }

        // Place / Cancel placing, with the status line next to it.
        ImGui.Spacing();
        var placing   = _placement.IsActiveFor(d);
        var hasPlayer = HasLocalPlayer();
        var canPlace  = _s.DecalPicker is not null && hasPlayer;
        using (ImRaii.Disabled(!canPlace && !placing))
        {
            if (ImGui.Button(placing ? "Cancel placing##decal" : "Place##decal", new Vector2(130f * scale, 0)))
            {
                if (placing)
                    _placement.Cancel();
                else
                    _placement.Begin(profile, d, d.Placement?.Size ?? DefaultDecalSize, d.Placement?.Rotation ?? 0f);
            }
        }
        Widgets.Tooltip(_s.DecalPicker is null ? "Penumbra is not loaded or the decal feature could not start."
            : !hasPlayer ? "Log in first: the decal is placed by clicking your character."
            : placing ? "Stop placing; the decal stays where it was."
            : d.Placement is null ? "Then click the spot on your character where the decal goes." : "Pick a new spot. Size and rotation are kept.");
        ImGui.SameLine();
        if (placing)
        {
            ImGui.TextColored(Widgets.Accent, "Click your character to place the decal. Esc cancels.");
            if (!string.IsNullOrEmpty(_placement.Diagnostics))
                ImGui.TextWrapped(_placement.Diagnostics);
            var showMesh = _placement.ShowMesh;
            if (ImGui.Checkbox("Show mesh (debug)##decal", ref showMesh))
                _placement.ShowMesh = showMesh;
            Widgets.Tooltip("Draws the skin Lamina clicks against as a wireframe (cyan body, yellow face) with a status block in the top-left corner. If the lines sit off your character, placement will be off too.");
            ImGui.SameLine();
            var deformerOff = _placement.DeformerOff;
            if (ImGui.Checkbox("Deformer off##decal", ref deformerOff))
                _placement.DeformerOff = deformerOff;
            Widgets.Tooltip("A/B check for the wireframe: poses the skin of your gear without the racial deformer, the way a Midlander would wear it. Leave it off unless the wireframe looks wrong.");
        }
        else if (d.Placement is { } placed)
            ImGui.TextUnformatted(string.IsNullOrEmpty(placed.PartName) ? $"On the {SurfaceName(placed.Surface)}" : $"On the {SurfaceName(placed.Surface)}: {placed.PartName}");
        else
            Widgets.Hint("Press Place, then click your character.");
        if (!placing && d.Placement is { MirroredUv: true } mirrored)
            Widgets.Hint($"This {SurfaceName(mirrored.Surface)} shares its texture between left and right, so the decal shows on both sides.");

        // A bake runs for a few seconds after every change (read the skin texture, project, encode BC7, redraw).
        if (_s.DecalPublisher is { Busy: true })
        {
            var dots = new string('.', 1 + (int)(ImGui.GetTime() * 2) % 3);
            ImGui.TextColored(Widgets.Accent, $"Baking the decal into the skin{dots}");
            Widgets.Tooltip("Reading the skin texture, projecting the image, encoding BC7 and redrawing. A 4K skin takes a few seconds.");
        }

        // Sliders: cm, degrees and percent in the UI; metres, degrees and 0..1 in the layer.
        var isPlaced = d.Placement is not null;
        var width    = 220f * scale;
        using (ImRaii.Disabled(!isPlaced))
        {
            var cm = (int)MathF.Round((d.Placement?.Size ?? DefaultDecalSize) * 100f);
            ImGui.SetNextItemWidth(width);
            if (ImGui.SliderInt("Size##decal", ref cm, 1, 60, "%d cm") && d.Placement is { } sized)
                sized.Size = cm / 100f;
            if (ImGui.IsItemDeactivatedAfterEdit() && isPlaced)
                changed = true;
            Widgets.Tooltip("Width of the decal on the skin; the height follows the image.");

            var degrees = (int)MathF.Round(d.Placement?.Rotation ?? 0f);
            ImGui.SetNextItemWidth(width);
            if (ImGui.SliderInt("Rotation##decal", ref degrees, -180, 180, "%d°") && d.Placement is { } rotated)
                rotated.Rotation = degrees;
            if (ImGui.IsItemDeactivatedAfterEdit() && isPlaced)
                changed = true;
        }
        var percent = (int)MathF.Round(d.Opacity * 100f);
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderInt("Opacity##decal", ref percent, 0, 100, "%d %%"))
            d.Opacity = percent / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit())
            changed = true;
        return changed;
    }

    /// <summary> Opens the image picker; the chosen file is imported into Lamina's decal folder when the dialog closes. </summary>
    private void OpenDecalImageDialog(Profile profile, DecalLayer d)
        => _fileDialogs.OpenFileDialog("Choose a decal image", "Images{.png,.jpg,.jpeg},.png,.jpg,.jpeg", (ok, path) =>
        {
            if (!ok || string.IsNullOrEmpty(path))
                return;
            try
            {
                d.ImageFile = _images.Import(path);
                _decalErrors.Remove(d.Id);
                _s.Profiles.MarkDirty(profile);
                _s.Engine.Invalidate();
            }
            catch (Exception ex)
            {
                _s.Log.Warning(ex, "UI: importing decal image {Path} failed.", path);
                _decalErrors[d.Id] = $"Could not import the image: {ex.Message}";
            }
        });

    private bool HasLocalPlayer()
    {
        try
        {
            return _s.ObjectTable.LocalPlayer is not null;
        }
        catch (Exception ex)
        {
            _s.Log.Debug(ex, "UI: reading the local player failed.");
            return false;
        }
    }

    /// <summary> Scales <paramref name="size"/> to fit a square box, keeping the aspect ratio. </summary>
    private static Vector2 FitInto(Vector2 size, float box)
    {
        if (size.X <= 0f || size.Y <= 0f)
            return new Vector2(box);
        return size * MathF.Min(box / size.X, box / size.Y);
    }

    private static bool DrawUnknown(UnknownLayer u)
    {
        ImGui.TextColored(Widgets.Warn, $"Unsupported layer kind '{u.UnknownKind}' from a newer Lamina. It is skipped and kept as is.");
        return false;
    }
}
