using Lamina.Models;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Enums;

namespace Lamina.Runtime.Compose;

/// <summary>
/// Turns a <see cref="VirtualDesign"/> into a Glamourer design file JObject (FileVersion 3, the layout Glamourer writes to
/// disk and accepts through its AddDesign IPC), so what a pass or group produces can be kept as a real Glamourer design.
/// <para>
/// Apply flags are true only for set fields, like <see cref="GlamourerStateBuilder"/>. Mod associations become the design's
/// <c>Mods</c> array: a Glamourer association always states the whole mod (enabled flag, priority, listed groups), so a
/// contribution that leaves the enabled state unset (inherit) cannot be expressed and is skipped (see <see cref="Result.SkippedMods"/>);
/// an unset priority is written as 0. Honorific, Moodles and decals are not part of a Glamourer design and are ignored.
/// </para>
/// </summary>
public static class GlamourerDesignWriter
{
    /// <summary> Glamourer's design file version. </summary>
    public const int FileVersion = 3;

    /// <summary> Version of a mod association entry as Glamourer writes it. </summary>
    public const int ModAssociationVersion = 1;

    /// <summary> Option groups of a mod: group name → (option names, group type). Null when the mod is unknown. </summary>
    public delegate IReadOnlyDictionary<string, (string[] Options, GroupType Type)>? AvailableSettingsLookup(string modDirectory);

    /// <summary> The written design plus what could not be carried over. </summary>
    /// <param name="Design"> The FileVersion 3 design JObject. </param>
    /// <param name="SkippedMods"> Mod directories left out of the Mods array because their enabled state is unset (inherit). </param>
    public sealed record Result(JObject Design, IReadOnlyList<string> SkippedMods);

    private static readonly EquipSlot[] CrestSlots = { EquipSlot.Head, EquipSlot.Body, EquipSlot.OffHand };

    private static readonly (string Key, MetaIndex Index, string ValueKey)[] EquipmentMeta =
    {
        ("Hat",       MetaIndex.HatVisible,       "Show"),
        ("Visor",     MetaIndex.VisorToggled,     "IsToggled"),
        ("Weapon",    MetaIndex.WeaponVisible,    "Show"),
        ("VieraEars", MetaIndex.VieraEarsVisible, "Show"),
    };

    /// <summary> Builds the design JObject. </summary>
    /// <param name="design"> What to save. </param>
    /// <param name="name"> Design name. </param>
    /// <param name="description"> Design description. </param>
    /// <param name="includeMods"> Whether the design's mod contributions become a Mods array. </param>
    public static JObject Build(VirtualDesign design, string name, string description, bool includeMods)
        => BuildWithReport(design, name, description, includeMods, null).Design;

    /// <summary> Builds the design JObject and reports the mods that could not be carried over. </summary>
    /// <param name="availableSettings">
    /// Optional lookup of a mod's option groups. When given, every option of a set group is listed with its enabled / disabled
    /// state, as Glamourer writes it; otherwise only the enabled options are listed.
    /// </param>
    public static Result BuildWithReport(VirtualDesign design, string name, string description, bool includeMods, AvailableSettingsLookup? availableSettings)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var obj = new JObject
        {
            ["FileVersion"]    = FileVersion,
            ["Identifier"]     = Guid.NewGuid().ToString(),
            ["CreationDate"]   = now,
            ["LastEdit"]       = now,
            ["Name"]           = name,
            ["Description"]    = description,
            ["Color"]          = string.Empty,
            ["QuickDesign"]    = false,
            ["Tags"]           = new JArray(),
            ["WriteProtected"] = false,
            ["Equipment"]      = Equipment(design),
            ["Bonus"]          = Bonus(design),
            ["Customize"]      = Customize(design),
            ["Parameters"]     = Parameters(design),
            ["Materials"]      = Materials(design),
        };

        var skipped = new List<string>();
        if (includeMods)
            obj["Mods"] = Mods(design, availableSettings, skipped);
        return new Result(obj, skipped);
    }

    private static JObject Equipment(VirtualDesign d)
    {
        var equipment = new JObject();
        foreach (var slot in Enum.GetValues<EquipSlot>())
        {
            var hasItem  = d.Items.TryGetValue(slot, out var item);
            var hasStain = d.Stains.TryGetValue(slot, out var stains);
            var hasCrest = CrestSlots.Contains(slot) && d.Crests.ContainsKey(slot);
            if (!hasItem && !hasStain && !hasCrest)
                continue;

            var obj = new JObject();
            if (hasItem)
                obj["ItemId"] = item.Value.ItemId;
            obj["Stain"]      = hasStain ? stains.Value.Stain1 : (byte)0;
            obj["Stain2"]     = hasStain ? stains.Value.Stain2 : (byte)0;
            obj["Apply"]      = hasItem;
            obj["ApplyStain"] = hasStain;
            if (CrestSlots.Contains(slot))
            {
                obj["Crest"]      = hasCrest && d.Crests[slot].Value;
                obj["ApplyCrest"] = hasCrest;
            }

            equipment[slot.ToString()] = obj;
        }

        foreach (var (key, index, valueKey) in EquipmentMeta)
        {
            if (d.Meta.TryGetValue(index, out var meta))
                equipment[key] = new JObject { [valueKey] = meta.Value, ["Apply"] = true };
        }

        return equipment;
    }

    private static JObject Bonus(VirtualDesign d)
    {
        var bonus = new JObject();
        foreach (var slot in Enum.GetValues<BonusSlot>())
        {
            if (d.Bonus.TryGetValue(slot, out var field))
                bonus[slot.ToString()] = new JObject { ["BonusId"] = field.Value, ["Apply"] = true };
        }

        return bonus;
    }

    private static JObject Customize(VirtualDesign d)
    {
        var customize = new JObject();
        if (d.ModelId is { } model)
            customize["ModelId"] = model.Value;
        foreach (var index in Enum.GetValues<CustomizeIndex>())
        {
            if (d.Customize.TryGetValue(index, out var field))
                customize[index.ToString()] = new JObject { ["Value"] = field.Value, ["Apply"] = true };
        }

        if (d.Meta.TryGetValue(MetaIndex.Wetness, out var wet))
            customize["Wetness"] = new JObject { ["Value"] = wet.Value, ["Apply"] = true };
        return customize;
    }

    private static JObject Parameters(VirtualDesign d)
    {
        var parameters = new JObject();
        foreach (var (key, field) in d.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var copy = (JObject)field.Value.DeepClone();
            copy["Apply"] = true;
            parameters[key] = copy;
        }

        return parameters;
    }

    private static JObject Materials(VirtualDesign d)
    {
        var materials = new JObject();
        foreach (var (key, field) in d.Materials.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            materials[key] = field.Value.DeepClone();
        return materials;
    }

    private static JArray Mods(VirtualDesign d, AvailableSettingsLookup? availableSettings, List<string> skipped)
    {
        var mods = new JArray();
        foreach (var (_, mod) in d.Mods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (mod.State is not { } state)
            {
                skipped.Add(mod.ModDirectory);
                continue;
            }

            mods.Add(ModAssociation(mod, state.Value == ModToggle.Enable, availableSettings?.Invoke(mod.ModDirectory)));
        }

        return mods;
    }

    /// <summary>
    /// One entry of the Mods array as Glamourer writes it: name, directory, version, state, priority and the set option groups
    /// with each option enabled or disabled. Groups the contribution leaves unset are not listed (Glamourer treats them as
    /// the mod's current settings).
    /// </summary>
    public static JObject ModAssociation(ModContribution mod, bool enabled, IReadOnlyDictionary<string, (string[] Options, GroupType Type)>? available)
    {
        var obj = new JObject
        {
            ["Name"]      = mod.ModName ?? mod.ModDirectory,
            ["Directory"] = mod.ModDirectory,
            ["Version"]   = ModAssociationVersion,
            ["State"]     = enabled ? "Enabled" : "Disabled",
            ["Priority"]  = mod.Priority?.Value ?? 0,
        };

        var settings = new JArray();
        foreach (var (group, field) in mod.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var selected = field.Value;
            var names    = new List<string>();
            if (available is not null && available.TryGetValue(group, out var groupInfo))
                names.AddRange(groupInfo.Options);
            foreach (var option in selected)
            {
                if (!names.Contains(option, StringComparer.Ordinal))
                    names.Add(option);
            }

            var options = new JArray();
            foreach (var option in names)
                options.Add(new JObject { ["Name"] = option, ["State"] = selected.Contains(option, StringComparer.Ordinal) ? "Enabled" : "Disabled" });
            settings.Add(new JObject { ["Name"] = group, ["Options"] = options });
        }

        obj["Settings"] = settings;
        return obj;
    }
}
