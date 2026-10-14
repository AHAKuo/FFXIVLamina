using System.Security.Cryptography;
using System.Text;
using Lamina.Models;
using Newtonsoft.Json;

namespace Lamina.Runtime.Engine;

/// <summary> What changed between the applied state and the next design, per integration. </summary>
/// <param name="NoChange"> Nothing to do at all. </param>
/// <param name="Forced"> The caller asked for a full re-apply regardless of hashes. </param>
/// <param name="GlamourerChanged"> The Glamourer section differs (or the design lost its Glamourer content). </param>
/// <param name="ModsChanged"> The mod section differs. </param>
/// <param name="HonorificChanged"> The title differs. </param>
/// <param name="MoodlesChanged"> Something must be added or removed on Moodles. </param>
/// <param name="MoodleAdds"> Moodles / presets to add now (ids not yet added by Lamina). </param>
/// <param name="MoodleRemoves"> Moodles to remove now: previously added ones the design no longer wants plus explicit removes. </param>
/// <param name="PresetRemoves"> Presets Lamina applied that the design no longer wants. </param>
public sealed record DesignDelta(
    bool NoChange,
    bool Forced,
    bool GlamourerChanged,
    bool ModsChanged,
    bool HonorificChanged,
    bool MoodlesChanged,
    IReadOnlyList<(Guid Id, MoodleOp Op)> MoodleAdds,
    IReadOnlyList<Guid> MoodleRemoves,
    IReadOnlyList<Guid> PresetRemoves)
{
    /// <summary> Only Penumbra changes need a redraw (Glamourer, Honorific and Moodles apply live). </summary>
    public bool RequiresRedraw
        => ModsChanged;

    /// <summary> A delta that does nothing. </summary>
    public static readonly DesignDelta None = new(true, false, false, false, false, false,
        Array.Empty<(Guid, MoodleOp)>(), Array.Empty<Guid>(), Array.Empty<Guid>());
}

/// <summary> Compares a design with what Lamina last applied and computes the per-integration delta (TDD 7.1 #5). </summary>
public static class DesignDiffer
{
    /// <summary>
    /// Computes the delta. With an identical content hash and <paramref name="force"/> false the result is <see cref="DesignDelta.None"/>.
    /// </summary>
    /// <param name="applied"> What Lamina currently has applied. </param>
    /// <param name="next"> The design to apply. </param>
    /// <param name="force"> Re-apply every section even when hashes match. </param>
    public static DesignDelta Diff(AppliedStateStore applied, VirtualDesign next, bool force)
    {
        var sameHash = applied.DesignHash is not null && applied.DesignHash == next.ContentHash;
        if (sameHash && !force)
            return DesignDelta.None;

        var nextGlamourer = next.HasGlamourerContent ? GlamourerHash(next) : null;
        var nextMods      = next.HasMods ? ModsHash(next) : null;

        var glamourerChanged = force || nextGlamourer != applied.GlamourerHash;
        var modsChanged      = force || nextMods != applied.ModsHash;
        var honorificChanged = force || !HonorificEquals(applied.HonorificSet, next.Honorific?.Value);

        var wantedAdds = next.Moodles.Where(kv => kv.Value.Value != MoodleOp.Remove)
                             .Select(kv => (kv.Key, kv.Value.Value))
                             .ToList();
        var wantedStatuses = wantedAdds.Where(a => a.Value == MoodleOp.Add).Select(a => a.Key).ToHashSet();
        var wantedPresets  = wantedAdds.Where(a => a.Value == MoodleOp.AddPreset).Select(a => a.Key).ToHashSet();

        var adds = force
            ? wantedAdds
            : wantedAdds.Where(a => a.Value == MoodleOp.AddPreset ? !applied.PresetsApplied.Contains(a.Key) : !applied.MoodlesAdded.Contains(a.Key)).ToList();

        var removes = applied.MoodlesAdded.Where(id => !wantedStatuses.Contains(id))
                             .Concat(next.Moodles.Where(kv => kv.Value.Value == MoodleOp.Remove).Select(kv => kv.Key))
                             .Distinct()
                             .ToList();

        var presetRemoves = applied.PresetsApplied.Where(id => !wantedPresets.Contains(id)).ToList();

        var moodlesChanged = adds.Count > 0 || removes.Count > 0 || presetRemoves.Count > 0;
        var noChange       = !glamourerChanged && !modsChanged && !honorificChanged && !moodlesChanged;
        return new DesignDelta(noChange, force, glamourerChanged, modsChanged, honorificChanged, moodlesChanged, adds, removes, presetRemoves);
    }

    /// <summary> Stable hash of everything that goes to Glamourer. </summary>
    public static string GlamourerHash(VirtualDesign d)
    {
        var sb = new StringBuilder(512);
        foreach (var (k, v) in d.Items.OrderBy(kv => kv.Key)) sb.Append("I:").Append(k).Append('=').Append(v.Value.ItemId).Append(';');
        foreach (var (k, v) in d.Stains.OrderBy(kv => kv.Key)) sb.Append("S:").Append(k).Append('=').Append(v.Value.Stain1).Append(',').Append(v.Value.Stain2).Append(';');
        foreach (var (k, v) in d.Crests.OrderBy(kv => kv.Key)) sb.Append("C:").Append(k).Append('=').Append(v.Value).Append(';');
        foreach (var (k, v) in d.Bonus.OrderBy(kv => kv.Key)) sb.Append("B:").Append(k).Append('=').Append(v.Value).Append(';');
        foreach (var (k, v) in d.Customize.OrderBy(kv => kv.Key)) sb.Append("Z:").Append(k).Append('=').Append(v.Value).Append(';');
        if (d.ModelId is { } model) sb.Append("Model=").Append(model.Value).Append(';');
        foreach (var (k, v) in d.Meta.OrderBy(kv => kv.Key)) sb.Append("M:").Append(k).Append('=').Append(v.Value).Append(';');
        foreach (var (k, v) in d.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.Append("P:").Append(k).Append('=').Append(v.Value.ToString(Formatting.None)).Append(';');
        foreach (var (k, v) in d.Materials.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.Append("A:").Append(k).Append('=').Append(v.Value.ToString(Formatting.None)).Append(';');
        return Hash(sb);
    }

    /// <summary> Stable hash of everything that goes to Penumbra. </summary>
    public static string ModsHash(VirtualDesign d)
    {
        var sb = new StringBuilder(512);
        foreach (var (k, v) in d.Mods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var m = v.Value;
            sb.Append("D:").Append(k.ToLowerInvariant()).Append('=').Append(m.State).Append(',').Append(m.Priority ?? 0).Append(',');
            if (m.Settings is not null)
                foreach (var (g, opts) in m.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sb.Append(g).Append(':').Append(string.Join('|', opts.OrderBy(o => o, StringComparer.Ordinal))).Append('/');
            sb.Append(';');
        }

        return Hash(sb);
    }

    private static bool HonorificEquals(HonorificTitle? a, HonorificTitle? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return a.Clear == b.Clear && a.Title == b.Title && a.IsPrefix == b.IsPrefix && a.Color == b.Color && a.Glow == b.Glow;
    }

    private static string Hash(StringBuilder sb)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())), 0, 16);
}
