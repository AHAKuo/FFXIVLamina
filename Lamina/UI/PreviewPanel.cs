using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;

namespace Lamina.UI;

/// <summary> Preview section of the pass / group editor: the focused contribution and the full design right now, with provenance. </summary>
internal sealed class PreviewPanel
{
    private readonly LaminaServices _s;
    private readonly UiCaches _caches;

    public PreviewPanel(LaminaServices services, UiCaches caches)
    {
        _s      = services;
        _caches = caches;
    }

    public void Draw(Profile profile, Guid? groupId, Guid? passId, PreviewResult? preview, Func<Guid, string> groupName, Func<Guid, string> passName)
    {
        if (preview is null)
        {
            Widgets.Hint("Preview unavailable (engine not ready).");
            return;
        }

        var noun = passId is null ? "group" : "pass";
        ImGui.TextUnformatted($"This {noun} currently matches:");
        ImGui.SameLine();
        ImGui.TextColored(preview.FocusMatches ? Widgets.Good : Widgets.Muted, preview.FocusMatches ? "yes" : "no");
        if (!_s.Engine.Enabled)
        {
            ImGui.SameLine();
            Widgets.Badge("(Lamina is off)", Widgets.Warn);
        }
        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 120 * Widgets.Scale);
        if (ImGui.SmallButton("Copy /lam why"))
            ImGui.SetClipboardText(_s.Engine.Why());
        Widgets.Tooltip("Copies the full explanation of the current state to the clipboard.");

        if (preview.FocusSkipped.Count > 0)
            foreach (var (layerId, reason) in preview.FocusSkipped)
                ImGui.TextColored(Widgets.Warn, $"Skipped layer {layerId.ToString()[..8]}…: {reason}");

        ImGui.Spacing();
        ImGui.TextColored(Widgets.Accent, $"Contribution of this {noun}");
        Widgets.Hint("What it would set on its own, ignoring hold / linger timers.");
        if (preview.Focus is { } focus && !focus.IsEmpty)
            DrawFieldTable("##focus", focus, groupName, passName, showSource: false);
        else
            Widgets.Hint("(nothing)");

        ImGui.Spacing();
        ImGui.TextColored(Widgets.Accent, "Full virtual design right now");
        Widgets.Hint($"Active group: {(preview.Full.ActiveGroupId is { } g ? groupName(g) : "(none)")}"
                   + (preview.Full.ActiveFinalGroupIds.Count > 0 ? $" · final: {string.Join(", ", preview.Full.ActiveFinalGroupIds.Select(groupName))}" : string.Empty)
                   + $" · passes: {(preview.Full.MatchedPassIds.Count == 0 ? "(none)" : string.Join(", ", preview.Full.MatchedPassIds.Select(passName)))}");
        if (preview.Full.IsEmpty)
            Widgets.Hint("(empty: everything passes through)");
        else
            DrawFieldTable("##full", preview.Full, groupName, passName, showSource: true);
    }

    private void DrawFieldTable(string id, VirtualDesign d, Func<Guid, string> groupName, Func<Guid, string> passName, bool showSource)
    {
        using var table = ImRaii.Table(id, showSource ? 3 : 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH);
        if (!table)
            return;
        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 3f);
        if (showSource)
            ImGui.TableSetupColumn("Set by", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableHeadersRow();

        foreach (var (field, value, source) in Rows(d))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(field);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(value);
            if (!showSource)
                continue;
            ImGui.TableNextColumn();
            var text = source.PassId is { } p ? passName(p) : source.GroupId is { } g ? groupName(g) : "?";
            ImGui.TextColored(Widgets.Muted, text);
            if (!string.IsNullOrEmpty(source.Note))
                Widgets.Tooltip(source.Note);
        }
    }

    private IEnumerable<(string Field, string Value, Provenance Source)> Rows(VirtualDesign d)
    {
        foreach (var (slot, f) in d.Items.OrderBy(kv => kv.Key))
            yield return ($"{Widgets.Humanize(slot.ToString())} item", _s.Catalog.ItemName(f.Value.ItemId), f.Source);
        foreach (var (slot, f) in d.Stains.OrderBy(kv => kv.Key))
            yield return ($"{Widgets.Humanize(slot.ToString())} dyes", $"{_s.Catalog.StainName(f.Value.Stain1)} / {_s.Catalog.StainName(f.Value.Stain2)}", f.Source);
        foreach (var (slot, f) in d.Crests.OrderBy(kv => kv.Key))
            yield return ($"{Widgets.Humanize(slot.ToString())} crest", f.Value ? "on" : "off", f.Source);
        foreach (var (slot, f) in d.Bonus.OrderBy(kv => kv.Key))
            yield return ($"{Widgets.Humanize(slot.ToString())}", $"bonus {f.Value}", f.Source);
        if (d.ModelId is { } model)
            yield return ("Model", model.Value.ToString(), model.Source);
        foreach (var (idx, f) in d.Customize.OrderBy(kv => kv.Key))
            yield return (Widgets.Humanize(idx.ToString()), f.Value.ToString(), f.Source);
        foreach (var (meta, f) in d.Meta.OrderBy(kv => kv.Key))
            yield return (Widgets.Humanize(meta.ToString()), f.Value ? "on" : "off", f.Source);
        foreach (var (key, f) in d.Parameters.OrderBy(kv => kv.Key))
            yield return ($"Advanced {Widgets.Humanize(key)}", Widgets.Truncate(f.Value.ToString(Newtonsoft.Json.Formatting.None), 48), f.Source);
        foreach (var (key, f) in d.Materials.OrderBy(kv => kv.Key))
            yield return ($"Material {key}", Widgets.Truncate(f.Value.ToString(Newtonsoft.Json.Formatting.None), 48), f.Source);
        foreach (var (dir, c) in d.Mods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = _caches.ModName(dir);
            yield return ($"Mod: {name}", c.State is { } s ? (s.Value == ModToggle.Enable ? "enable" : "disable") : "(enabled flag inherited)", c.State?.Source ?? c.PrimarySource);
            if (c.Priority is { } p)
                yield return ($"Mod: {name} · priority", p.Value.ToString(), p.Source);
            foreach (var (group, f) in c.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                yield return ($"Mod: {name} · {group}", f.Value.Count == 0 ? "(none)" : string.Join(" | ", f.Value), f.Source);
        }
        if (d.Honorific is { } h)
            yield return ("Honorific", h.Value.Clear ? "clear" : $"{h.Value.Title} ({(h.Value.IsPrefix ? "prefix" : "suffix")})", h.Source);
        foreach (var (id, f) in d.Moodles.OrderBy(kv => kv.Key))
        {
            var name = _caches.Moodles.FirstOrDefault(m => m.Id == id).Name ?? _caches.MoodlePresets.FirstOrDefault(m => m.Id == id).Name ?? id.ToString()[..8] + "…";
            yield return ($"Moodle: {name}", Widgets.Humanize(f.Value.ToString()), f.Source);
        }
    }
}
