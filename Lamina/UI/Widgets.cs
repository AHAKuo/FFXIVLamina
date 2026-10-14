using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Lamina.UI;

/// <summary> Small reusable ImGui widgets shared by every tab: searchable pickers, tri-state toggles, time inputs, confirm modals. </summary>
internal static class Widgets
{
    public static readonly Vector4 Muted  = new(0.62f, 0.62f, 0.66f, 1f);
    public static readonly Vector4 Warn   = new(0.91f, 0.71f, 0.35f, 1f);
    public static readonly Vector4 Bad    = new(0.93f, 0.38f, 0.38f, 1f);
    public static readonly Vector4 Good   = new(0.45f, 0.86f, 0.55f, 1f);
    public static readonly Vector4 Accent = new(0.72f, 0.60f, 0.98f, 1f);

    private static readonly Dictionary<string, string> SearchTexts = new(StringComparer.Ordinal);

    public static float Scale => ImGuiHelpers.GlobalScale;

    /// <summary> Shows a tooltip when the last item is hovered (including while disabled). </summary>
    public static void Tooltip(string text)
    {
        if (!string.IsNullOrEmpty(text) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }

    /// <summary> Icon button with a tooltip. </summary>
    public static bool IconButton(FontAwesomeIcon icon, string tooltip, bool enabled = true)
    {
        bool clicked;
        using (ImRaii.Disabled(!enabled))
            clicked = ImGuiComponents.IconButton(icon);
        Tooltip(tooltip);
        return clicked && enabled;
    }

    /// <summary> Muted help text. </summary>
    public static void Hint(string text)
        => ImGui.TextColored(Muted, text);

    /// <summary> Wrapped muted paragraph. </summary>
    public static void Paragraph(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
    }

    /// <summary> "Any / Yes / No" combo for a nullable bool. </summary>
    public static bool TriState(string label, ref bool? value, string anyLabel = "Any")
    {
        var index = value switch { null => 0, true => 1, false => 2 };
        var names = new[] { anyLabel, "Yes", "No" };
        ImGui.SetNextItemWidth(110 * Scale);
        if (!ImGui.Combo(label, ref index, names, names.Length))
            return false;
        value = index switch { 1 => true, 2 => false, _ => null };
        return true;
    }

    /// <summary> Combo over every value of an enum. </summary>
    public static bool EnumCombo<T>(string label, ref T value, float width = 160f) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        var names  = EnumNames<T>();
        var index  = Array.IndexOf(values, value);
        if (index < 0) index = 0;
        ImGui.SetNextItemWidth(width * Scale);
        if (!ImGui.Combo(label, ref index, names, names.Length))
            return false;
        value = values[index];
        return true;
    }

    private static readonly Dictionary<Type, string[]> EnumNameCache = new();

    private static string[] EnumNames<T>() where T : struct, Enum
    {
        if (EnumNameCache.TryGetValue(typeof(T), out var cached))
            return cached;
        var names = Enum.GetValues<T>().Select(v => Humanize(v.ToString())).ToArray();
        EnumNameCache[typeof(T)] = names;
        return names;
    }

    /// <summary> "LavenderBeds" → "Lavender Beds". </summary>
    public static string Humanize(string pascal)
    {
        if (string.IsNullOrEmpty(pascal))
            return pascal;
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(pascal[i - 1]))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary> Multi-select of enum values rendered as a button that opens a checkbox popup. </summary>
    public static bool EnumMultiSelect<T>(string label, List<T> selected, string emptyText = "Any") where T : struct, Enum
    {
        var values  = Enum.GetValues<T>();
        var names   = EnumNames<T>();
        var preview = selected.Count == 0 ? emptyText : string.Join(", ", selected.Select(v => names[Math.Max(0, Array.IndexOf(values, v))]));
        var changed = false;
        using var id = ImRaii.PushId(label);
        var popupId = "##popup";
        if (ImGui.Button(Truncate(preview, 40) + "##btn"))
            ImGui.OpenPopup(popupId);
        Tooltip(preview);
        ImGui.SameLine();
        ImGui.TextUnformatted(StripId(label));
        using var popup = ImRaii.Popup(popupId);
        if (!popup)
            return false;
        if (ImGui.SmallButton("Clear"))
        {
            selected.Clear();
            changed = true;
        }
        for (var i = 0; i < values.Length; i++)
        {
            var on = selected.Contains(values[i]);
            if (ImGui.Checkbox(names[i], ref on))
            {
                if (on) selected.Add(values[i]);
                else selected.Remove(values[i]);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary> One entry of a searchable picker. </summary>
    public readonly record struct PickItem<TId>(TId Id, string Name, string? Sub = null);

    /// <summary>
    /// Searchable multi-select popup over a list of items. Renders a button with the current selection as preview;
    /// the popup has a search box, a clear button and selectable rows. Returns true when the selection changed.
    /// </summary>
    public static bool MultiSelect<TId>(string label, IReadOnlyList<PickItem<TId>> items, List<TId> selected, Func<TId, string>? fallbackName = null, string emptyText = "Any")
        where TId : notnull
    {
        var changed = false;
        using var id = ImRaii.PushId(label);
        string preview;
        if (selected.Count == 0)
            preview = emptyText;
        else
        {
            var names = selected.Select(s => items.FirstOrDefault(i => EqualityComparer<TId>.Default.Equals(i.Id, s)) is { Name: { } n } ? n : fallbackName?.Invoke(s) ?? $"#{s}");
            preview = string.Join(", ", names);
        }
        if (ImGui.Button(Truncate(preview, 40) + "##btn"))
        {
            ImGui.OpenPopup("##popup");
            SearchTexts["##popup" + label] = string.Empty;
        }
        Tooltip(preview);
        ImGui.SameLine();
        ImGui.TextUnformatted(StripId(label));

        using var popup = ImRaii.Popup("##popup");
        if (!popup)
            return false;
        var key    = "##popup" + label;
        var search = SearchTexts.GetValueOrDefault(key, string.Empty);
        ImGui.SetNextItemWidth(260 * Scale);
        if (ImGui.InputTextWithHint("##search", "Search…", ref search, 128))
            SearchTexts[key] = search;
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear") && selected.Count > 0)
        {
            selected.Clear();
            changed = true;
        }
        using var child = ImRaii.Child("##list", new Vector2(340 * Scale, 300 * Scale), true);
        if (!child)
            return changed;
        var filter = search.Trim();
        var shown  = 0;
        foreach (var item in items)
        {
            if (filter.Length > 0 && !item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
             && (item.Sub is null || !item.Sub.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (++shown > 400)
            {
                Hint("… refine the search to see more");
                break;
            }
            var on   = selected.Contains(item.Id);
            var text = item.Sub is null ? item.Name : $"{item.Name}  ({item.Sub})";
            if (ImGui.Selectable(text, on, ImGuiSelectableFlags.DontClosePopups))
            {
                if (on) selected.Remove(item.Id);
                else selected.Add(item.Id);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary> Searchable single-select combo. Returns true and sets <paramref name="picked"/> when a new item was chosen. </summary>
    public static bool SearchCombo<TId>(string label, string preview, IReadOnlyList<PickItem<TId>> items, out TId picked, float width = 320f)
        where TId : notnull
    {
        picked = default!;
        using var id = ImRaii.PushId(label);
        ImGui.SetNextItemWidth(width * Scale);
        using var combo = ImRaii.Combo("##combo", Truncate(preview, 60));
        if (!string.IsNullOrEmpty(StripId(label)))
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(StripId(label));
        }
        if (!combo)
            return false;
        var key    = "##combo" + label;
        var search = SearchTexts.GetValueOrDefault(key, string.Empty);
        if (ImGui.IsWindowAppearing())
        {
            search = string.Empty;
            ImGui.SetKeyboardFocusHere();
        }
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##search", "Search…", ref search, 128))
            SearchTexts[key] = search;
        var filter  = search.Trim();
        var shown   = 0;
        var changed = false;
        using var child = ImRaii.Child("##list", new Vector2(0, 260 * Scale), false);
        if (!child)
            return false;
        foreach (var item in items)
        {
            if (filter.Length > 0 && !item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
             && (item.Sub is null || !item.Sub.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (++shown > 400)
            {
                Hint("… refine the search to see more");
                break;
            }
            var text = item.Sub is null ? item.Name : $"{item.Name}  ({item.Sub})";
            if (ImGui.Selectable(text))
            {
                picked  = item.Id;
                changed = true;
                ImGui.CloseCurrentPopup();
            }
        }
        return changed;
    }

    /// <summary> HH:MM editor stored as minutes since midnight. </summary>
    public static bool TimeInput(string label, ref int minutes)
    {
        var hours = Math.Clamp(minutes / 60, 0, 23);
        var mins  = Math.Clamp(minutes % 60, 0, 59);
        var changed = false;
        using var id = ImRaii.PushId(label);
        ImGui.SetNextItemWidth(70 * Scale);
        if (ImGui.InputInt("##h", ref hours, 1, 6))
        {
            hours   = (hours % 24 + 24) % 24;
            changed = true;
        }
        ImGui.SameLine(0, 2);
        ImGui.TextUnformatted(":");
        ImGui.SameLine(0, 2);
        ImGui.SetNextItemWidth(70 * Scale);
        if (ImGui.InputInt("##m", ref mins, 5, 15))
        {
            mins    = (mins % 60 + 60) % 60;
            changed = true;
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(StripId(label));
        if (changed)
            minutes = hours * 60 + mins;
        return changed;
    }

    /// <summary> Nullable int: a checkbox that enables an InputInt. </summary>
    public static bool NullableInt(string label, ref int? value, int defaultValue = 0, int min = int.MinValue, int max = int.MaxValue)
    {
        using var id = ImRaii.PushId(label);
        var on      = value.HasValue;
        var changed = false;
        if (ImGui.Checkbox("##on", ref on))
        {
            value   = on ? defaultValue : null;
            changed = true;
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!on))
        {
            var v = value ?? defaultValue;
            ImGui.SetNextItemWidth(110 * Scale);
            if (ImGui.InputInt(label, ref v))
            {
                value   = Math.Clamp(v, min, max);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary> Nullable float in seconds: a checkbox that enables an InputFloat; unchecked shows "default". </summary>
    public static bool NullableSeconds(string label, ref float? value, float defaultValue)
    {
        using var id = ImRaii.PushId(label);
        var on      = value.HasValue;
        var changed = false;
        if (ImGui.Checkbox("##on", ref on))
        {
            value   = on ? defaultValue : null;
            changed = true;
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!on))
        {
            var v = value ?? defaultValue;
            ImGui.SetNextItemWidth(110 * Scale);
            if (ImGui.InputFloat(label, ref v, 0.1f, 1f, "%.1f s"))
            {
                value   = MathF.Max(0, v);
                changed = true;
            }
        }
        if (!on)
        {
            ImGui.SameLine();
            Hint($"(default {defaultValue:0.0} s)");
        }
        return changed;
    }

    /// <summary> Nullable color: checkbox + ColorEdit3. </summary>
    public static bool NullableColor(string label, ref Vector3? color, Vector3 defaultColor)
    {
        using var id = ImRaii.PushId(label);
        var on      = color.HasValue;
        var changed = false;
        if (ImGui.Checkbox("##on", ref on))
        {
            color   = on ? defaultColor : null;
            changed = true;
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!on))
        {
            var c = color ?? defaultColor;
            ImGui.SetNextItemWidth(220 * Scale);
            if (ImGui.ColorEdit3(label, ref c))
            {
                color   = c;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary> Editable list of strings with an add box. </summary>
    public static bool StringList(string label, List<string> items, string hint, IReadOnlyList<string>? suggestions = null)
    {
        using var id = ImRaii.PushId(label);
        var changed  = false;
        var removeAt = -1;
        ImGui.TextUnformatted(StripId(label));
        for (var i = 0; i < items.Count; i++)
        {
            using var row = ImRaii.PushId(i);
            var text = items[i];
            ImGui.SetNextItemWidth(240 * Scale);
            if (ImGui.InputText("##v", ref text, 128))
            {
                items[i] = text;
                changed  = true;
            }
            ImGui.SameLine();
            if (IconButton(FontAwesomeIcon.Trash, "Remove"))
                removeAt = i;
        }
        if (removeAt >= 0)
        {
            items.RemoveAt(removeAt);
            changed = true;
        }
        var key   = "##new" + label;
        var entry = SearchTexts.GetValueOrDefault(key, string.Empty);
        ImGui.SetNextItemWidth(240 * Scale);
        var submitted = ImGui.InputTextWithHint("##new", hint, ref entry, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        SearchTexts[key] = entry;
        ImGui.SameLine();
        if ((IconButton(FontAwesomeIcon.Plus, "Add") || submitted) && entry.Trim().Length > 0)
        {
            items.Add(entry.Trim());
            SearchTexts[key] = string.Empty;
            changed = true;
        }
        if (suggestions is { Count: > 0 })
        {
            ImGui.SameLine();
            using var combo = ImRaii.Combo("##suggest", "Suggestions", ImGuiComboFlags.NoPreview);
            if (combo)
                foreach (var s in suggestions)
                    if (!items.Contains(s, StringComparer.OrdinalIgnoreCase) && ImGui.Selectable(s))
                    {
                        items.Add(s);
                        changed = true;
                    }
        }
        return changed;
    }

    /// <summary> Draws a confirmation modal previously opened with <see cref="ImGui.OpenPopup(string, ImGuiPopupFlags)"/>. Returns true on confirm. </summary>
    public static bool ConfirmModal(string id, string text, string confirmLabel = "Delete")
    {
        var result = false;
        var open   = true;
        using var modal = ImRaii.PopupModal(id, ref open, ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal)
            return false;
        ImGui.TextWrapped(text);
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f)))
        {
            if (ImGui.Button(confirmLabel, new Vector2(120 * Scale, 0)))
            {
                result = true;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120 * Scale, 0)))
            ImGui.CloseCurrentPopup();
        return result;
    }

    /// <summary> Status badge: colored text with a tooltip. </summary>
    public static void Badge(string text, Vector4 color, string? tooltip = null)
    {
        ImGui.TextColored(color, text);
        if (tooltip is not null)
            Tooltip(tooltip);
    }

    /// <summary> Shortens text with an ellipsis. </summary>
    public static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary> Removes the "##id" suffix of a label. </summary>
    public static string StripId(string label)
    {
        var idx = label.IndexOf("##", StringComparison.Ordinal);
        return idx < 0 ? label : label[..idx];
    }
}
