using System.Text;
using Lamina.Decals;
using Lamina.Models;

namespace Lamina.Runtime.Compose;

/// <summary> Renders a VirtualDesign and its provenance as text for /lam why and the debug overlay. </summary>
public static class DesignExplainer
{
    /// <summary> Multi-line explanation: active group, final groups, matched passes, then every set field with its source. </summary>
    /// <param name="d"> The design. </param>
    /// <param name="groupName"> Resolves a group id to a name. </param>
    /// <param name="passName"> Resolves a pass id to a name. </param>
    /// <param name="skipped"> Optional skipped layers from the evaluation report. </param>
    public static string Explain(VirtualDesign d, Func<Guid, string> groupName, Func<Guid, string> passName,
        IReadOnlyList<(Guid LayerId, string Reason)>? skipped = null)
    {
        var sb = new StringBuilder();
        sb.Append("Active group: ").AppendLine(d.ActiveGroupId is { } g ? groupName(g) : "(none)");
        sb.Append("Final groups: ").AppendLine(d.ActiveFinalGroupIds.Count == 0 ? "(none)" : string.Join(", ", d.ActiveFinalGroupIds.Select(groupName)));
        sb.Append("Matched passes: ").AppendLine(d.MatchedPassIds.Count == 0 ? "(none)" : string.Join(", ", d.MatchedPassIds.Select(passName)));
        sb.Append("Hash: ").AppendLine(d.ContentHash);

        if (d.IsEmpty)
        {
            sb.AppendLine("Design is empty: everything passes through.");
        }
        else
        {
            sb.AppendLine("Fields:");
            foreach (var (slot, f) in d.Items.OrderBy(kv => kv.Key))
                Line(sb, $"Item {slot}", f.Value.ItemId.ToString(), f.Source);
            foreach (var (slot, f) in d.Stains.OrderBy(kv => kv.Key))
                Line(sb, $"Dyes {slot}", $"{f.Value.Stain1}, {f.Value.Stain2}", f.Source);
            foreach (var (slot, f) in d.Crests.OrderBy(kv => kv.Key))
                Line(sb, $"Crest {slot}", f.Value ? "on" : "off", f.Source);
            foreach (var (slot, f) in d.Bonus.OrderBy(kv => kv.Key))
                Line(sb, $"Bonus {slot}", f.Value.ToString(), f.Source);
            if (d.ModelId is { } model)
                Line(sb, "Model", model.Value.ToString(), model.Source);
            foreach (var (idx, f) in d.Customize.OrderBy(kv => kv.Key))
                Line(sb, $"Customize {idx}", f.Value.ToString(), f.Source);
            foreach (var (idx, f) in d.Meta.OrderBy(kv => kv.Key))
                Line(sb, $"Meta {idx}", f.Value ? "on" : "off", f.Source);
            foreach (var (key, f) in d.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Line(sb, $"Parameter {key}", f.Value.ToString(Newtonsoft.Json.Formatting.None), f.Source);
            foreach (var (key, f) in d.Materials.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Line(sb, $"Material {key}", "(advanced dye)", f.Source);
            foreach (var (dir, c) in d.Mods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                Line(sb, $"Mod {dir}", c.State is { } s ? (s.Value == ModToggle.Enable ? "enabled" : "disabled") : "(enabled flag inherited)", c.State?.Source ?? c.PrimarySource);
                if (c.Priority is { } p)
                    Line(sb, $"Mod {dir} priority", p.Value.ToString(), p.Source);
                foreach (var (group, f) in c.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    Line(sb, $"Mod {dir} / {group}", f.Value.Count == 0 ? "(none)" : string.Join(" | ", f.Value), f.Source);
            }
            if (d.Honorific is { } h)
                Line(sb, "Honorific", h.Value.Clear ? "clear" : $"\"{h.Value.Title}\" ({(h.Value.IsPrefix ? "prefix" : "suffix")})", h.Source);
            foreach (var (id, f) in d.Moodles.OrderBy(kv => kv.Key))
                Line(sb, $"Moodle {id}", f.Value.ToString(), f.Source);
            foreach (var f in d.Decals)
                Line(sb, $"Decal {f.Value.ImageFile}", DescribeDecal(f.Value), f.Source);
        }

        if (skipped is { Count: > 0 })
        {
            sb.AppendLine("Skipped layers:");
            foreach (var (id, reason) in skipped)
                sb.Append("  ").Append(id).Append(": ").AppendLine(reason);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary> Surface (and picked part when known), size and opacity of a decal. </summary>
    private static string DescribeDecal(DecalSpec spec)
    {
        var p     = spec.Placement;
        var where = string.IsNullOrEmpty(p.PartName) ? p.Surface.ToString() : $"{p.Surface} / {p.PartName}";
        return FormattableString.Invariant($"{where} · size {p.Size:0.###} m · opacity {spec.Opacity:P0}");
    }

    private static void Line(StringBuilder sb, string field, string value, Provenance source)
        => sb.Append("  ").Append(field).Append(" = ").Append(value).Append("    ← ").AppendLine(source.Note.Length == 0 ? "(unknown)" : source.Note);
}
