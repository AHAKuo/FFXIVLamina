using System.Numerics;
using Lamina.Models;
using Newtonsoft.Json.Linq;

namespace Lamina.Runtime;

/// <summary> Which group / pass / layer set a field. Powers Preview and /lam why. </summary>
public readonly record struct Provenance(Guid? GroupId, Guid? PassId, Guid? LayerId, string Note)
{
    public static readonly Provenance None = new(null, null, null, string.Empty);
}

/// <summary> A set value with its source. </summary>
public readonly record struct Field<T>(T Value, Provenance Source);

/// <summary> Equipment slot value. </summary>
public readonly record struct EquipItem(ulong ItemId);

/// <summary> Dye pair for one slot. </summary>
public readonly record struct Stains(byte Stain1, byte Stain2);

/// <summary> Honorific title to set (Clear = true means clear whatever is there). </summary>
public sealed record HonorificTitle(bool Clear, string? Title, bool IsPrefix, Vector3? Color, Vector3? Glow)
{
    public static readonly HonorificTitle Cleared = new(true, null, false, null, null);
}

public enum MoodleOp
{
    Add,
    AddPreset,
    Remove,
}

/// <summary>
/// Mutable builder with the same shape as <see cref="VirtualDesign"/>. Every field is optional: unset = passthrough.
/// A pass's smashed layers (Pass.FinalLayer) are a DesignDraft, and the compositor merges drafts on top of each other.
/// Mods are keyed by mod directory, case-insensitive. Parameters / Materials are keyed by the Glamourer JSON key.
/// </summary>
public sealed class DesignDraft
{
    public Dictionary<EquipSlot, Field<EquipItem>> Items { get; } = new();
    public Dictionary<EquipSlot, Field<Stains>> Stains { get; } = new();
    /// <summary> FC crest on / off. Only Head, Body and OffHand carry crests. </summary>
    public Dictionary<EquipSlot, Field<bool>> Crests { get; } = new();
    public Dictionary<BonusSlot, Field<ulong>> Bonus { get; } = new();
    public Dictionary<CustomizeIndex, Field<int>> Customize { get; } = new();
    /// <summary> Non-human model id. Rarely set. </summary>
    public Field<uint>? ModelId { get; set; }
    public Dictionary<MetaIndex, Field<bool>> Meta { get; } = new();
    /// <summary> Advanced customizations: Glamourer "Parameters" entries, value object kept as is (without the Apply flag). </summary>
    public Dictionary<string, Field<JObject>> Parameters { get; } = new();
    /// <summary> Advanced dyes: Glamourer "Materials" entries, kept opaque. </summary>
    public Dictionary<string, Field<JToken>> Materials { get; } = new();
    public Dictionary<string, Field<ModState>> Mods { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Field<HonorificTitle>? Honorific { get; set; }
    public Dictionary<Guid, Field<MoodleOp>> Moodles { get; } = new();

    public bool IsEmpty
        => Items.Count == 0 && Stains.Count == 0 && Crests.Count == 0 && Bonus.Count == 0 && Customize.Count == 0
         && ModelId is null && Meta.Count == 0 && Parameters.Count == 0 && Materials.Count == 0 && Mods.Count == 0
         && Honorific is null && Moodles.Count == 0;

    /// <summary> Writes every set field of <paramref name="top"/> over this draft. Unset fields in <paramref name="top"/> pass through. </summary>
    public void MergeOnTop(DesignDraft top)
    {
        foreach (var (k, v) in top.Items) Items[k] = v;
        foreach (var (k, v) in top.Stains) Stains[k] = v;
        foreach (var (k, v) in top.Crests) Crests[k] = v;
        foreach (var (k, v) in top.Bonus) Bonus[k] = v;
        foreach (var (k, v) in top.Customize) Customize[k] = v;
        if (top.ModelId is { } model) ModelId = model;
        foreach (var (k, v) in top.Meta) Meta[k] = v;
        foreach (var (k, v) in top.Parameters) Parameters[k] = v;
        foreach (var (k, v) in top.Materials) Materials[k] = v;
        foreach (var (k, v) in top.Mods) Mods[k] = v;
        if (top.Honorific is { } title) Honorific = title;
        foreach (var (k, v) in top.Moodles) Moodles[k] = v;
    }

    /// <summary> Returns a copy with everything the mask does not allow removed. </summary>
    public DesignDraft Filter(ApplyMask? mask)
    {
        if (mask is null || mask.IsEverything)
            return this;

        var result = new DesignDraft();
        foreach (var (k, v) in Items) if (mask.Equipment.Has(k)) result.Items[k] = v;
        foreach (var (k, v) in Stains) if (mask.Equipment.Has(k)) result.Stains[k] = v;
        foreach (var (k, v) in Crests) if (mask.Equipment.Has(k)) result.Crests[k] = v;
        foreach (var (k, v) in Bonus) if (mask.Equipment.Has(k)) result.Bonus[k] = v;
        foreach (var (k, v) in Customize) if (mask.Customization.Has(k)) result.Customize[k] = v;
        if (mask.Customization != CustomizeFlags.None && ModelId is { } model) result.ModelId = model;
        if (mask.Meta)
            foreach (var (k, v) in Meta) result.Meta[k] = v;
        if (mask.Advanced)
        {
            foreach (var (k, v) in Parameters) result.Parameters[k] = v;
            foreach (var (k, v) in Materials) result.Materials[k] = v;
        }
        if (mask.Mods)
            foreach (var (k, v) in Mods) result.Mods[k] = v;
        // Honorific and Moodles are never masked (the mask only applies to Glamourer / Vitrine layers).
        if (Honorific is { } title) result.Honorific = title;
        foreach (var (k, v) in Moodles) result.Moodles[k] = v;
        return result;
    }

    /// <summary> Freezes the draft into an immutable VirtualDesign. </summary>
    public VirtualDesign Freeze(Guid profileId, Guid? activeGroupId, IReadOnlyList<Guid> activeFinalGroupIds, IReadOnlyList<Guid> matchedPassIds)
        => new(this, profileId, activeGroupId, activeFinalGroupIds, matchedPassIds);
}

/// <summary>
/// The final, immutable result for one target after all passes and final groups merged.
/// Everything that will be sent to Glamourer, Penumbra, Honorific and Moodles. Every field is optional: unset = passthrough.
/// </summary>
public sealed class VirtualDesign
{
    public static readonly VirtualDesign Empty = new(new DesignDraft(), Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>());

    internal VirtualDesign(DesignDraft draft, Guid profileId, Guid? activeGroupId, IReadOnlyList<Guid> activeFinalGroupIds, IReadOnlyList<Guid> matchedPassIds)
    {
        Items               = new Dictionary<EquipSlot, Field<EquipItem>>(draft.Items);
        Stains              = new Dictionary<EquipSlot, Field<Stains>>(draft.Stains);
        Crests              = new Dictionary<EquipSlot, Field<bool>>(draft.Crests);
        Bonus               = new Dictionary<BonusSlot, Field<ulong>>(draft.Bonus);
        Customize           = new Dictionary<CustomizeIndex, Field<int>>(draft.Customize);
        ModelId             = draft.ModelId;
        Meta                = new Dictionary<MetaIndex, Field<bool>>(draft.Meta);
        Parameters          = new Dictionary<string, Field<JObject>>(draft.Parameters);
        Materials           = new Dictionary<string, Field<JToken>>(draft.Materials);
        Mods                = new Dictionary<string, Field<ModState>>(draft.Mods, StringComparer.OrdinalIgnoreCase);
        Honorific           = draft.Honorific;
        Moodles             = new Dictionary<Guid, Field<MoodleOp>>(draft.Moodles);
        ProfileId           = profileId;
        ActiveGroupId       = activeGroupId;
        ActiveFinalGroupIds = activeFinalGroupIds;
        MatchedPassIds      = matchedPassIds;
        BuiltAtUtc          = DateTime.UtcNow;
        ContentHash         = DesignHasher.Compute(this);
    }

    public IReadOnlyDictionary<EquipSlot, Field<EquipItem>> Items { get; }
    public IReadOnlyDictionary<EquipSlot, Field<Stains>> Stains { get; }
    public IReadOnlyDictionary<EquipSlot, Field<bool>> Crests { get; }
    public IReadOnlyDictionary<BonusSlot, Field<ulong>> Bonus { get; }
    public IReadOnlyDictionary<CustomizeIndex, Field<int>> Customize { get; }
    public Field<uint>? ModelId { get; }
    public IReadOnlyDictionary<MetaIndex, Field<bool>> Meta { get; }
    public IReadOnlyDictionary<string, Field<JObject>> Parameters { get; }
    public IReadOnlyDictionary<string, Field<JToken>> Materials { get; }
    public IReadOnlyDictionary<string, Field<ModState>> Mods { get; }
    public Field<HonorificTitle>? Honorific { get; }
    public IReadOnlyDictionary<Guid, Field<MoodleOp>> Moodles { get; }

    // ---- metadata ----
    public Guid ProfileId { get; }
    public Guid? ActiveGroupId { get; }
    public IReadOnlyList<Guid> ActiveFinalGroupIds { get; }
    public IReadOnlyList<Guid> MatchedPassIds { get; }
    public DateTime BuiltAtUtc { get; }

    /// <summary> Stable hash of the content (not provenance / metadata). Equal hash = nothing to apply. </summary>
    public string ContentHash { get; }

    public bool HasGlamourerContent
        => Items.Count > 0 || Stains.Count > 0 || Crests.Count > 0 || Bonus.Count > 0 || Customize.Count > 0
         || ModelId is not null || Meta.Count > 0 || Parameters.Count > 0 || Materials.Count > 0;

    public bool HasMods
        => Mods.Count > 0;

    public bool IsEmpty
        => !HasGlamourerContent && !HasMods && Honorific is null && Moodles.Count == 0;
}

/// <summary> Content hashing for VirtualDesign. Must ignore provenance and metadata and be order independent. </summary>
public static class DesignHasher
{
    public static string Compute(VirtualDesign d)
    {
        var sb = new System.Text.StringBuilder(1024);
        foreach (var (k, v) in d.Items.OrderBy(kv => kv.Key)) sb.Append("I:").Append(k).Append('=').Append(v.Value.ItemId).Append(';');
        foreach (var (k, v) in d.Stains.OrderBy(kv => kv.Key)) sb.Append("S:").Append(k).Append('=').Append(v.Value.Stain1).Append(',').Append(v.Value.Stain2).Append(';');
        foreach (var (k, v) in d.Crests.OrderBy(kv => kv.Key)) sb.Append("C:").Append(k).Append('=').Append(v.Value).Append(';');
        foreach (var (k, v) in d.Bonus.OrderBy(kv => kv.Key)) sb.Append("B:").Append(k).Append('=').Append(v.Value).Append(';');
        foreach (var (k, v) in d.Customize.OrderBy(kv => kv.Key)) sb.Append("Z:").Append(k).Append('=').Append(v.Value).Append(';');
        if (d.ModelId is { } model) sb.Append("Model=").Append(model.Value).Append(';');
        foreach (var (k, v) in d.Meta.OrderBy(kv => kv.Key)) sb.Append("M:").Append(k).Append('=').Append(v.Value).Append(';');
        foreach (var (k, v) in d.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.Append("P:").Append(k).Append('=').Append(v.Value.ToString(Newtonsoft.Json.Formatting.None)).Append(';');
        foreach (var (k, v) in d.Materials.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.Append("A:").Append(k).Append('=').Append(v.Value.ToString(Newtonsoft.Json.Formatting.None)).Append(';');
        foreach (var (k, v) in d.Mods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var m = v.Value;
            sb.Append("D:").Append(k.ToLowerInvariant()).Append('=').Append(m.State).Append(',').Append(m.Priority ?? 0).Append(',');
            if (m.Settings is not null)
                foreach (var (g, opts) in m.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sb.Append(g).Append(':').Append(string.Join('|', opts.OrderBy(o => o, StringComparer.Ordinal))).Append('/');
            sb.Append(';');
        }
        if (d.Honorific is { } h)
            sb.Append("H:").Append(h.Value.Clear).Append(',').Append(h.Value.Title).Append(',').Append(h.Value.IsPrefix).Append(',').Append(h.Value.Color).Append(',').Append(h.Value.Glow).Append(';');
        foreach (var (k, v) in d.Moodles.OrderBy(kv => kv.Key)) sb.Append("O:").Append(k).Append('=').Append(v.Value).Append(';');

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes, 0, 16);
    }
}
