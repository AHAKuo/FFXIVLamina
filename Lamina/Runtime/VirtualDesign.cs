using System.Numerics;
using Lamina.Decals;
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

/// <summary>
/// One Customize+ bone edit, as the Customize+ IPC profile JSON carries it (<c>IPCBoneTransform</c>): translation, rotation
/// (degrees), scaling, child scaling and the flags. Bones merge by name, so a later pass can override one bone of an earlier
/// pass's profile.
/// </summary>
public sealed record CustomizePlusBone(Vector3 Translation, Vector3 Rotation, Vector3 Scaling, Vector3 ChildScaling,
    bool ChildScaleIndependent, bool PropagateTranslation, bool PropagateRotation, bool PropagateScale);

/// <summary>
/// Simple Heels height offset to register for the player. <paramref name="Relative"/> adds the offset to the player's own
/// resolved Simple Heels offset (their per-footwear configuration) instead of replacing it.
/// </summary>
public sealed record HeelsOffset(float Offset, bool Relative);

public enum MoodleOp
{
    Add,
    AddPreset,
    Remove,
}

/// <summary>
/// What the design wants for one Penumbra mod, with provenance per part (TDD 5.7). Every part is optional: an unset part is
/// inherited at apply time from the mod's effective settings in the Lamina collection. Mod entries merge per part, so a
/// later pass can set one option group of a mod an earlier pass enabled without restating the rest.
/// </summary>
public sealed class ModContribution
{
    public ModContribution(string modDirectory, string? modName = null)
    {
        ModDirectory = modDirectory;
        ModName      = modName;
    }

    public string ModDirectory { get; }

    /// <summary> Display name when known (informational). </summary>
    public string? ModName { get; private set; }

    /// <summary> Enable / disable. Null = inherit. </summary>
    public Field<ModToggle>? State { get; set; }

    /// <summary> Priority. Null = inherit. </summary>
    public Field<int>? Priority { get; set; }

    /// <summary> Option group → enabled options, for the groups that are set. </summary>
    public Dictionary<string, Field<List<string>>> Settings { get; } = new(StringComparer.Ordinal);

    /// <summary> True when nothing is set (the entry would only inherit). </summary>
    public bool IsEmpty
        => State is null && Priority is null && Settings.Count == 0;

    /// <summary> The source of the most significant part, for single-line displays. </summary>
    public Provenance PrimarySource
        => State?.Source ?? Priority?.Source ?? (Settings.Count > 0 ? Settings.Values.First().Source : Provenance.None);

    /// <summary> Builds a contribution from a layer's mod state with one provenance for every set part. </summary>
    public static ModContribution From(ModState state, Provenance source)
    {
        var c = new ModContribution(state.ModDirectory, state.ModName);
        if (state.State is { } toggle)
            c.State = new Field<ModToggle>(toggle, source);
        if (state.Priority is { } priority)
            c.Priority = new Field<int>(priority, source);
        if (state.Settings is { } settings)
            foreach (var (group, options) in settings)
                c.Settings[group] = new Field<List<string>>(new List<string>(options), source);
        return c;
    }

    public ModContribution Clone()
    {
        var c = new ModContribution(ModDirectory, ModName) { State = State, Priority = Priority };
        foreach (var (group, field) in Settings)
            c.Settings[group] = new Field<List<string>>(new List<string>(field.Value), field.Source);
        return c;
    }

    /// <summary> Writes every set part of <paramref name="top"/> over this one, keeping the rest. </summary>
    public void MergeOnTop(ModContribution top)
    {
        if (top.ModName is not null)
            ModName = top.ModName;
        if (top.State is { } state)
            State = state;
        if (top.Priority is { } priority)
            Priority = priority;
        foreach (var (group, field) in top.Settings)
            Settings[group] = new Field<List<string>>(new List<string>(field.Value), field.Source);
    }

    /// <summary> The set parts as a plain <see cref="ModState"/> (unset parts stay null). </summary>
    public ModState ToModState()
        => new()
        {
            ModDirectory = ModDirectory,
            ModName      = ModName,
            State        = State?.Value,
            Priority     = Priority?.Value,
            Settings     = Settings.Count == 0 ? null : Settings.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value.Value), StringComparer.Ordinal),
        };

    /// <summary> Stable text of the content (no provenance), for hashing. </summary>
    public string ContentKey()
    {
        var sb = new System.Text.StringBuilder(128);
        sb.Append(ModDirectory.ToLowerInvariant()).Append('=');
        sb.Append(State is { } s ? s.Value.ToString() : "~").Append(',');
        sb.Append(Priority is { } p ? p.Value.ToString() : "~").Append(',');
        foreach (var (g, f) in Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(g).Append(':').Append(string.Join('|', f.Value.OrderBy(o => o, StringComparer.Ordinal))).Append('/');
        return sb.ToString();
    }
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
    /// <summary> Mod directory → contribution. Entries merge per part (see <see cref="ModContribution"/>). </summary>
    public Dictionary<string, ModContribution> Mods { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Field<HonorificTitle>? Honorific { get; set; }
    public Dictionary<Guid, Field<MoodleOp>> Moodles { get; } = new();
    /// <summary> Decals in bake order: earlier entries are painted first, later ones land on top. Not maskable, like Honorific and Moodles. </summary>
    public List<Field<DecalSpec>> Decals { get; } = new();
    /// <summary> Customize+ bone name → edit. Merged per bone; applied as one temporary profile. Not maskable. </summary>
    public Dictionary<string, Field<CustomizePlusBone>> CustomizePlusBones { get; } = new(StringComparer.Ordinal);
    /// <summary> Simple Heels offset; later passes replace earlier ones. Not maskable. </summary>
    public Field<HeelsOffset>? SimpleHeels { get; set; }

    public bool IsEmpty
        => Items.Count == 0 && Stains.Count == 0 && Crests.Count == 0 && Bonus.Count == 0 && Customize.Count == 0
         && ModelId is null && Meta.Count == 0 && Parameters.Count == 0 && Materials.Count == 0 && Mods.Count == 0
         && Honorific is null && Moodles.Count == 0 && Decals.Count == 0 && CustomizePlusBones.Count == 0 && SimpleHeels is null;

    /// <summary> Adds a mod contribution, merging per part when the mod is already present. </summary>
    public void AddMod(ModContribution contribution)
    {
        if (Mods.TryGetValue(contribution.ModDirectory, out var existing))
            existing.MergeOnTop(contribution);
        else
            Mods[contribution.ModDirectory] = contribution.Clone();
    }

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
        foreach (var (_, v) in top.Mods) AddMod(v);
        if (top.Honorific is { } title) Honorific = title;
        foreach (var (k, v) in top.Moodles) Moodles[k] = v;
        // Decals accumulate instead of replacing: a later pass paints its decals on top of the earlier ones.
        Decals.AddRange(top.Decals);
        foreach (var (k, v) in top.CustomizePlusBones) CustomizePlusBones[k] = v;
        if (top.SimpleHeels is { } heels) SimpleHeels = heels;
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
            foreach (var (_, v) in Mods) result.AddMod(v);
        // Honorific, Moodles, decals, Customize+ and Simple Heels are never masked (the mask only applies to Glamourer / Vitrine / Equipment / Appearance layers).
        if (Honorific is { } title) result.Honorific = title;
        foreach (var (k, v) in Moodles) result.Moodles[k] = v;
        result.Decals.AddRange(Decals);
        foreach (var (k, v) in CustomizePlusBones) result.CustomizePlusBones[k] = v;
        if (SimpleHeels is { } heels) result.SimpleHeels = heels;
        return result;
    }

    /// <summary> A draft carrying every field of a frozen design, so a design can be trimmed and frozen again. </summary>
    public static DesignDraft CopyOf(VirtualDesign design)
    {
        var d = new DesignDraft();
        foreach (var (k, v) in design.Items) d.Items[k] = v;
        foreach (var (k, v) in design.Stains) d.Stains[k] = v;
        foreach (var (k, v) in design.Crests) d.Crests[k] = v;
        foreach (var (k, v) in design.Bonus) d.Bonus[k] = v;
        foreach (var (k, v) in design.Customize) d.Customize[k] = v;
        d.ModelId = design.ModelId;
        foreach (var (k, v) in design.Meta) d.Meta[k] = v;
        foreach (var (k, v) in design.Parameters) d.Parameters[k] = v;
        foreach (var (k, v) in design.Materials) d.Materials[k] = v;
        foreach (var (_, v) in design.Mods) d.AddMod(v);
        d.Honorific = design.Honorific;
        foreach (var (k, v) in design.Moodles) d.Moodles[k] = v;
        d.Decals.AddRange(design.Decals);
        foreach (var (k, v) in design.CustomizePlusBones) d.CustomizePlusBones[k] = v;
        d.SimpleHeels = design.SimpleHeels;
        return d;
    }

    /// <summary> Drops every Glamourer field (items, stains, crests, bonus, customize, model, meta, parameters, materials). </summary>
    public void ClearGlamourer()
    {
        Items.Clear();
        Stains.Clear();
        Crests.Clear();
        Bonus.Clear();
        Customize.Clear();
        ModelId = null;
        Meta.Clear();
        Parameters.Clear();
        Materials.Clear();
    }

    /// <summary> Freezes the draft into an immutable VirtualDesign. </summary>
    public VirtualDesign Freeze(Guid profileId, Guid? activeGroupId, IReadOnlyList<Guid> activeFinalGroupIds, IReadOnlyList<Guid> matchedPassIds)
        => new(this, profileId, activeGroupId, activeFinalGroupIds, matchedPassIds);
}

/// <summary>
/// The final, immutable result for one target after all passes and final groups merged.
/// Everything that will be sent to Glamourer, Penumbra, Honorific, Moodles, Customize+ and Simple Heels. Every field is optional: unset = passthrough.
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
        Mods                = draft.Mods.Where(kv => !kv.Value.IsEmpty).ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        Honorific           = draft.Honorific;
        Moodles             = new Dictionary<Guid, Field<MoodleOp>>(draft.Moodles);
        Decals              = draft.Decals.ToList();
        CustomizePlusBones  = new Dictionary<string, Field<CustomizePlusBone>>(draft.CustomizePlusBones, StringComparer.Ordinal);
        SimpleHeels         = draft.SimpleHeels;
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
    /// <summary> Mod directory → contribution. Entries that set nothing are dropped at freeze time. </summary>
    public IReadOnlyDictionary<string, ModContribution> Mods { get; }
    public Field<HonorificTitle>? Honorific { get; }
    public IReadOnlyDictionary<Guid, Field<MoodleOp>> Moodles { get; }
    /// <summary> Decals in bake order (see <see cref="DesignDraft.Decals"/>). </summary>
    public IReadOnlyList<Field<DecalSpec>> Decals { get; }
    /// <summary> Customize+ bone name → edit, the content of the temporary profile Lamina sets. </summary>
    public IReadOnlyDictionary<string, Field<CustomizePlusBone>> CustomizePlusBones { get; }
    /// <summary> Simple Heels offset to register, or null to leave Simple Heels alone. </summary>
    public Field<HeelsOffset>? SimpleHeels { get; }

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

    public bool HasDecals
        => Decals.Count > 0;

    public bool HasCustomizePlus
        => CustomizePlusBones.Count > 0;

    public bool IsEmpty
        => !HasGlamourerContent && !HasMods && Honorific is null && Moodles.Count == 0 && !HasDecals && !HasCustomizePlus && SimpleHeels is null;
}

/// <summary>
/// Content hashing for VirtualDesign. Must ignore provenance and metadata and be order independent, except for decals, whose
/// paint order is content.
/// </summary>
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
        foreach (var (_, v) in d.Mods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append("D:").Append(v.ContentKey()).Append(';');
        if (d.Honorific is { } h)
            sb.Append("H:").Append(h.Value.Clear).Append(',').Append(h.Value.Title).Append(',').Append(h.Value.IsPrefix).Append(',').Append(h.Value.Color).Append(',').Append(h.Value.Glow).Append(';');
        foreach (var (k, v) in d.Moodles.OrderBy(kv => kv.Key)) sb.Append("O:").Append(k).Append('=').Append(v.Value).Append(';');
        foreach (var f in d.Decals) sb.Append("L:").Append(f.Value.ContentKey()).Append(';');
        foreach (var (k, v) in d.CustomizePlusBones.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.Append("B+:").Append(k).Append('=').Append(v.Value).Append(';');
        if (d.SimpleHeels is { } heels) sb.Append("SH:").Append(heels.Value.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(heels.Value.Relative).Append(';');

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes, 0, 16);
    }
}
