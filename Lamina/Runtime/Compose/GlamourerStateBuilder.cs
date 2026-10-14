using Lamina.Models;
using Newtonsoft.Json.Linq;

namespace Lamina.Runtime.Compose;

/// <summary>
/// Turns a <see cref="VirtualDesign"/> into the JObject Glamourer's ApplyState accepts.
/// Apply flags are true only for set fields, so everything unset passes through. Mods are never included:
/// Lamina applies mod associations through Penumbra itself.
/// <para>
/// Glamourer parses a state object without an "Identifier" through <c>DesignBase.LoadDesignBase</c>, which only accepts
/// <c>DesignBase.FileVersion</c> (1). Design files on disk use the full <c>Design</c> format (FileVersion 3) instead,
/// which is what the parser reads. Sending 3 here makes Glamourer answer InvalidState ("no valid Version").
/// </para>
/// </summary>
public static class GlamourerStateBuilder
{
    /// <summary> The design-base file version Glamourer's state loader accepts. </summary>
    public const int FileVersion = 1;

    /// <summary>
    /// Customization indices Glamourer validates every other value against. When the design leaves them unset they are copied
    /// from the actor's current state (Apply = false) so hair / face / colours are validated for the right clan and gender
    /// instead of Glamourer's zero defaults.
    /// </summary>
    private static readonly CustomizeIndex[] IdentityIndices = { CustomizeIndex.Race, CustomizeIndex.Clan, CustomizeIndex.Gender, CustomizeIndex.BodyType };

    private static readonly EquipSlot[] CrestSlots = { EquipSlot.Head, EquipSlot.Body, EquipSlot.OffHand };

    private static readonly (string Key, MetaIndex Index, string ValueKey)[] EquipmentMeta =
    {
        ("Hat",       MetaIndex.HatVisible,       "Show"),
        ("Visor",     MetaIndex.VisorToggled,     "IsToggled"),
        ("Weapon",    MetaIndex.WeaponVisible,    "Show"),
        ("VieraEars", MetaIndex.VieraEarsVisible, "Show"),
    };

    /// <summary> Builds the state JObject. Always contains every section, even when empty. </summary>
    public static JObject Build(VirtualDesign d)
        => Build(d, null);

    /// <summary> Glamourer race id for a clan id (clans come in pairs per race: 1,2 → Hyur (1); 3,4 → Elezen (2); ...). </summary>
    public static int RaceOfClan(int clan)
        => clan <= 0 ? 0 : (clan + 1) / 2;

    /// <summary> First clan id of a race. </summary>
    public static int FirstClanOfRace(int race)
        => race <= 0 ? 0 : race * 2 - 1;

    /// <summary>
    /// Glamourer validates Race against Clan and warns ("The race X does not correspond to the clan Y") whenever they disagree,
    /// e.g. a design that applies only the clan over a baseline of another race. Whichever of the two is applied wins and
    /// the other is rewritten to match, with the same Apply flag.
    /// </summary>
    private static void ReconcileRaceAndClan(JObject customize)
    {
        var raceObj = customize["Race"] as JObject;
        var clanObj = customize["Clan"] as JObject;
        var race    = raceObj?["Value"]?.Value<int>() ?? 0;
        var clan    = clanObj?["Value"]?.Value<int>() ?? 0;
        if (race == 0 && clan == 0)
            return;

        var raceApplied = raceObj?["Apply"]?.Value<bool>() ?? false;
        var clanApplied = clanObj?["Apply"]?.Value<bool>() ?? false;
        if (clan != 0 && (clanApplied || !raceApplied))
        {
            var wanted = RaceOfClan(clan);
            if (race != wanted)
                customize["Race"] = new JObject { ["Value"] = wanted, ["Apply"] = clanApplied };
        }
        else if (race != 0)
        {
            if (RaceOfClan(clan) != race)
                customize["Clan"] = new JObject { ["Value"] = FirstClanOfRace(race), ["Apply"] = raceApplied };
        }
    }

    /// <summary>
    /// Builds the state JObject, filling unset identity fields (race, clan, gender, body type, model id) from
    /// <paramref name="baseline"/>, the actor's current state as returned by Glamourer's GetState, with Apply = false.
    /// </summary>
    public static JObject Build(VirtualDesign d, JObject? baseline)
    {
        var equipment = new JObject();
        foreach (var slot in Enum.GetValues<EquipSlot>())
        {
            var hasItem  = d.Items.TryGetValue(slot, out var item);
            var hasStain = d.Stains.TryGetValue(slot, out var stains);
            var hasCrest = CrestSlots.Contains(slot) && d.Crests.TryGetValue(slot, out var crest);
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
            if (!d.Meta.TryGetValue(index, out var meta))
                continue;
            equipment[key] = new JObject { [valueKey] = meta.Value, ["Apply"] = true };
        }

        var bonus = new JObject();
        foreach (var (slot, field) in d.Bonus)
            bonus[slot.ToString()] = new JObject { ["BonusId"] = field.Value, ["Apply"] = true };

        var customize        = new JObject();
        var baselineCustomize = baseline?["Customize"] as JObject;
        if (d.ModelId is { } model)
            customize["ModelId"] = model.Value;
        else if (baselineCustomize?["ModelId"] is { Type: JTokenType.Integer } baseModel)
            customize["ModelId"] = baseModel.Value<uint>();
        foreach (var index in Enum.GetValues<CustomizeIndex>())
        {
            if (d.Customize.TryGetValue(index, out var field))
            {
                customize[index.ToString()] = new JObject { ["Value"] = field.Value, ["Apply"] = true };
                continue;
            }

            if (IdentityIndices.Contains(index) && baselineCustomize?[index.ToString()]?["Value"] is { Type: JTokenType.Integer } baseValue)
                customize[index.ToString()] = new JObject { ["Value"] = baseValue.Value<int>(), ["Apply"] = false };
        }

        ReconcileRaceAndClan(customize);

        if (d.Meta.TryGetValue(MetaIndex.Wetness, out var wet))
            customize["Wetness"] = new JObject { ["Value"] = wet.Value, ["Apply"] = true };

        var parameters = new JObject();
        foreach (var (key, field) in d.Parameters)
        {
            var copy = (JObject)field.Value.DeepClone();
            copy["Apply"] = true;
            parameters[key] = copy;
        }

        var materials = new JObject();
        foreach (var (key, field) in d.Materials)
            materials[key] = field.Value.DeepClone();

        return new JObject
        {
            ["FileVersion"] = FileVersion,
            ["Equipment"]   = equipment,
            ["Bonus"]       = bonus,
            ["Customize"]   = customize,
            ["Parameters"]  = parameters,
            ["Materials"]   = materials,
        };
    }
}
