using Lamina.Models;
using Newtonsoft.Json.Linq;

namespace Lamina.Runtime.Compose;

/// <summary>
/// Reads a Glamourer design (or state) JObject in the FileVersion 3 layout into a <see cref="DesignDraft"/>.
/// A field contributes only when its Apply flag is true. Glamourer omits Apply flags that are false, so an absent
/// flag means "not applied" for design files; <c>applyFlagsDefault</c> can flip that for state dumps.
/// </summary>
public static class GlamourerDesignParser
{
    /// <summary> Glamourer equipment slot keys that carry an FC crest. </summary>
    private static readonly EquipSlot[] CrestSlots = { EquipSlot.Head, EquipSlot.Body, EquipSlot.OffHand };

    /// <summary> JSON key under "Equipment" → meta index, plus the boolean key that holds the value. </summary>
    private static readonly (string Key, MetaIndex Index, string ValueKey)[] EquipmentMeta =
    {
        ("Hat",       MetaIndex.HatVisible,       "Show"),
        ("Visor",     MetaIndex.VisorToggled,     "IsToggled"),
        ("Weapon",    MetaIndex.WeaponVisible,    "Show"),
        ("VieraEars", MetaIndex.VieraEarsVisible, "Show"),
    };

    /// <summary> Parses a design JObject. </summary>
    /// <param name="design"> The JObject as returned by Glamourer's GetDesignJObject / GetState. </param>
    /// <param name="source"> Provenance stamped on every field. </param>
    /// <param name="includeMods"> Whether the design's mod associations become ModState entries. </param>
    /// <param name="applyFlagsDefault"> What an absent Apply flag means. False for design files, true for state dumps. </param>
    public static DesignDraft Parse(JObject design, Provenance source, bool includeMods, bool applyFlagsDefault = false)
    {
        var draft = new DesignDraft();
        ParseEquipment(design["Equipment"] as JObject, draft, source, applyFlagsDefault);
        ParseBonus(design["Bonus"] as JObject, draft, source, applyFlagsDefault);
        ParseCustomize(design["Customize"] as JObject, draft, source, applyFlagsDefault);
        ParseParameters(design["Parameters"] as JObject, draft, source, applyFlagsDefault);
        ParseMaterials(design["Materials"] as JObject, draft, source, applyFlagsDefault);
        if (includeMods)
            ParseMods(design["Mods"] as JArray, draft, source);
        return draft;
    }

    /// <summary> Reads the mod associations of a design on their own. </summary>
    public static List<ModState> ParseModAssociations(JObject design)
    {
        var draft = new DesignDraft();
        ParseMods(design["Mods"] as JArray, draft, Provenance.None);
        return draft.Mods.Values.Select(f => f.Value).ToList();
    }

    private static bool Flag(JObject obj, string key, bool fallback)
        => obj[key]?.Type == JTokenType.Boolean ? obj[key]!.Value<bool>() : fallback;

    private static void ParseEquipment(JObject? equipment, DesignDraft draft, Provenance source, bool applyDefault)
    {
        if (equipment is null)
            return;

        foreach (var slot in Enum.GetValues<EquipSlot>())
        {
            if (equipment[slot.ToString()] is not JObject slotObj)
                continue;

            if (Flag(slotObj, "Apply", applyDefault) && slotObj["ItemId"] is { Type: JTokenType.Integer } item)
                draft.Items[slot] = new Field<EquipItem>(new EquipItem(item.Value<ulong>()), source);

            if (Flag(slotObj, "ApplyStain", applyDefault))
            {
                var stain1 = slotObj["Stain"]?.Type == JTokenType.Integer ? slotObj["Stain"]!.Value<byte>() : (byte)0;
                var stain2 = slotObj["Stain2"]?.Type == JTokenType.Integer ? slotObj["Stain2"]!.Value<byte>() : (byte)0;
                draft.Stains[slot] = new Field<Stains>(new Stains(stain1, stain2), source);
            }

            if (CrestSlots.Contains(slot) && Flag(slotObj, "ApplyCrest", applyDefault))
                draft.Crests[slot] = new Field<bool>(Flag(slotObj, "Crest", false), source);
        }

        foreach (var (key, index, valueKey) in EquipmentMeta)
        {
            if (equipment[key] is not JObject metaObj || !Flag(metaObj, "Apply", applyDefault))
                continue;
            if (metaObj[valueKey]?.Type == JTokenType.Boolean)
                draft.Meta[index] = new Field<bool>(metaObj[valueKey]!.Value<bool>(), source);
        }
    }

    private static void ParseBonus(JObject? bonus, DesignDraft draft, Provenance source, bool applyDefault)
    {
        if (bonus is null)
            return;

        foreach (var slot in Enum.GetValues<BonusSlot>())
        {
            if (bonus[slot.ToString()] is not JObject slotObj || !Flag(slotObj, "Apply", applyDefault))
                continue;
            if (slotObj["BonusId"] is { Type: JTokenType.Integer } id)
                draft.Bonus[slot] = new Field<ulong>(id.Value<ulong>(), source);
        }
    }

    private static void ParseCustomize(JObject? customize, DesignDraft draft, Provenance source, bool applyDefault)
    {
        if (customize is null)
            return;

        foreach (var index in Enum.GetValues<CustomizeIndex>())
        {
            if (customize[index.ToString()] is not JObject obj || !Flag(obj, "Apply", applyDefault))
                continue;
            if (obj["Value"] is { Type: JTokenType.Integer } value)
                draft.Customize[index] = new Field<int>(value.Value<int>(), source);
        }

        if (customize["Wetness"] is JObject wet && Flag(wet, "Apply", applyDefault) && wet["Value"]?.Type == JTokenType.Boolean)
            draft.Meta[MetaIndex.Wetness] = new Field<bool>(wet["Value"]!.Value<bool>(), source);

        // ModelId has no apply flag of its own. A non-zero model id is always meaningful; zero only counts for state dumps.
        if (customize["ModelId"] is { Type: JTokenType.Integer } model)
        {
            var id = model.Value<uint>();
            if (id != 0 || applyDefault)
                draft.ModelId = new Field<uint>(id, source);
        }
    }

    private static void ParseParameters(JObject? parameters, DesignDraft draft, Provenance source, bool applyDefault)
    {
        if (parameters is null)
            return;

        foreach (var prop in parameters.Properties())
        {
            if (prop.Value is not JObject obj || !Flag(obj, "Apply", applyDefault))
                continue;
            var copy = (JObject)obj.DeepClone();
            copy.Remove("Apply");
            draft.Parameters[prop.Name] = new Field<JObject>(copy, source);
        }
    }

    private static void ParseMaterials(JObject? materials, DesignDraft draft, Provenance source, bool applyDefault)
    {
        if (materials is null)
            return;

        // Materials are kept opaque. When the entry carries an Apply flag it is honored; otherwise the entry is kept as is.
        foreach (var prop in materials.Properties())
        {
            if (prop.Value is JObject obj && obj["Apply"]?.Type == JTokenType.Boolean && !obj["Apply"]!.Value<bool>() && !applyDefault)
                continue;
            draft.Materials[prop.Name] = new Field<JToken>(prop.Value.DeepClone(), source);
        }
    }

    private static void ParseMods(JArray? mods, DesignDraft draft, Provenance source)
    {
        if (mods is null)
            return;

        foreach (var token in mods)
        {
            if (token is not JObject mod)
                continue;
            var directory = mod["Directory"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var remove  = mod["Remove"]?.Type == JTokenType.Boolean && mod["Remove"]!.Value<bool>();
            var enabled = !remove && !string.Equals(mod["State"]?.Value<string>(), "Disabled", StringComparison.OrdinalIgnoreCase);

            var state = new ModState
            {
                ModDirectory = directory,
                ModName      = mod["Name"]?.Value<string>(),
                State        = enabled ? ModToggle.Enable : ModToggle.Disable,
                Priority     = mod["Priority"]?.Type == JTokenType.Integer ? mod["Priority"]!.Value<int>() : null,
                Settings     = ParseModSettings(mod["Settings"] as JArray),
            };
            draft.Mods[directory] = new Field<ModState>(state, source);
        }
    }

    /// <summary> Glamourer stores settings as [{Name, Options:[{Name, State}]}]. Returns group name → enabled option names. </summary>
    private static Dictionary<string, List<string>>? ParseModSettings(JArray? settings)
    {
        if (settings is null)
            return null;

        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var token in settings)
        {
            if (token is not JObject group)
                continue;
            var groupName = group["Name"]?.Value<string>();
            if (string.IsNullOrEmpty(groupName))
                continue;

            var enabled = new List<string>();
            if (group["Options"] is JArray options)
                foreach (var opt in options.OfType<JObject>())
                {
                    var name = opt["Name"]?.Value<string>();
                    if (!string.IsNullOrEmpty(name)
                     && string.Equals(opt["State"]?.Value<string>(), "Enabled", StringComparison.OrdinalIgnoreCase))
                        enabled.Add(name);
                }

            result[groupName] = enabled;
        }

        return result;
    }
}
