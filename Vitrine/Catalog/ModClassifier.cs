using System.Text.RegularExpressions;

namespace Vitrine.Catalog;

/// <summary>
/// Classifies Penumbra changed items into <see cref="VitrineCategory"/> values using the item display name and,
/// when available, the runtime type of the object Penumbra attached to it. Pure logic, unit tested.
/// </summary>
public static partial class ModClassifier
{
    [GeneratedRegex(@"\b(hair|hairstyle)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HairRegex();

    [GeneratedRegex(@"\b(face|facepaint|face paint|decal|eyes?|eyebrows?|lips?|mouth|nose)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FaceRegex();

    [GeneratedRegex(@"\btails?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TailRegex();

    [GeneratedRegex(@"\bears?\b", RegexOptions.IgnoreCase)]
    private static partial Regex EarsRegex();

    [GeneratedRegex(@"\b(skin|texture)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SkinRegex();

    [GeneratedRegex(@"\b(body|bust|legs?|arms?|hands?|feet|torso|chest|muscle|skeleton)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BodyRegex();

    [GeneratedRegex(@"^(action|skill|ability|animation|anim|idle|pose|cpose|weapon draw|sheathe)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActionRegex();

    [GeneratedRegex(@"^emote\b|\bemote\b|^/\w+$", RegexOptions.IgnoreCase)]
    private static partial Regex EmoteRegex();

    [GeneratedRegex(@"(^|[\\/])vfx([\\/]|$)|\.avfx$|\.atex$|\bvfx\b|\bparticle", RegexOptions.IgnoreCase)]
    private static partial Regex VfxRegex();

    [GeneratedRegex(@"(^|[\\/])sound([\\/]|$)|\.scd$|\bsfx\b|\bsound\b|\bbgm\b|\bmusic\b|\bvoice\b", RegexOptions.IgnoreCase)]
    private static partial Regex SfxRegex();

    [GeneratedRegex(@"(^|[\\/])ui([\\/]|$)|\bui\b|\bicon\b|\bhud\b|\bfont\b|\bloading screen\b|\btitle screen\b", RegexOptions.IgnoreCase)]
    private static partial Regex UiRegex();

    [GeneratedRegex(@"\b(minion|companion)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MinionRegex();

    [GeneratedRegex(@"\bmount\b", RegexOptions.IgnoreCase)]
    private static partial Regex MountRegex();

    [GeneratedRegex(@"\b(housing|furniture|furnishing|indoor|outdoor|garden|estate|apartment)\b|bgcommon[\\/]hou", RegexOptions.IgnoreCase)]
    private static partial Regex HousingRegex();

    [GeneratedRegex(@"\b(npc|enpc|bnpc|monster|demihuman|demi-human|boss)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NpcRegex();

    [GeneratedRegex(@"\b(earring|earrings|necklace|choker|bracelet|bracelets|ring|wristlets?|armillae?|pendant|brooch)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AccessoryRegex();

    [GeneratedRegex(@"\b(sword|blade|axe|lance|spear|bow|gun|rifle|cannon|dagger|daggers|knives|katana|rapier|scythe|staff|rod|cane|wand|grimoire|book|codex|tome|fists?|claws?|knuckles|shield|gunblade|nouliths?|brush|planisphere|astrometer|chakrams?|twinfangs?|twinblades?|greatsword|broadsword|halberd|trident|crossbow|pistol|musket|saw|hammer|frypan|knife|pickaxe|hatchet|fishing rod|gig|needle|alembic|mortar|file|chisel)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeaponRegex();

    [GeneratedRegex(@"\b(helm|helmet|hat|cap|hood|mask|circlet|crown|glasses|goggles|visor|headband|coat|jacket|robe|dress|tunic|shirt|top|blouse|corset|bodice|vest|armor|armour|plate|mail|gloves|gauntlets|mittens|bracers|vambraces|trousers|pants|skirt|shorts|slacks|breeches|leggings|hose|bottoms|boots|shoes|sandals|greaves|sabatons|heels|thighboots|sollerets|chausses|cuirass|haubergeon|doublet|tabard|surcoat|halfcoat|overcoat|poncho|cowl|bonnet|turban|beret|tricorne|kasa|eyepatch|monocle|spectacles|attire|suit|uniform|outfit|set|bustier|loincloth|corselet|tights|gaskins|culottes|kecks|sarouel|bliaud|gown|kimono|yukata|haori|hakama|tabi|geta|pumps|mules|clogs|waders|gaiters|socks|stockings|armguards|cuffs|wristguards|sleeves|half-gloves|hairpin|headdress|ribbon|bandana|scarf|cape|cloak|mantle|wrap|shawl|bikini|swimsuit|lingerie|camisole|bra|panties|thong|garter|garters|harness|apron|jerkin|coatee|hauberk|brigandine|lorica|chainmail|parka|anorak|hoodie|sweater|cardigan|smock|sash|belt|kilt|sarong|pareo|chaps|jeans|dungarees|overalls|leotard|bodysuit|catsuit|frock|sundress|pinafore)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GearRegex();

    [GeneratedRegex(@"\b(midlander|highlander|wildwood|duskwight|plainsfolk|dunesfolk|seeker|keeper|sea wolf|seawolf|hellsguard|raen|xaela|helion|lost|rava|veena|hyur|elezen|lalafell|miqo'te|miqote|roegadyn|au ra|aura|hrothgar|viera)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RaceRegex();

    /// <summary> Classifies one changed item. </summary>
    /// <param name="name"> The display name Penumbra uses as dictionary key. </param>
    /// <param name="value"> The object Penumbra attached (a game row, a tuple, or null). </param>
    public static VitrineCategory Classify(string name, object? value = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return VitrineCategory.Unknown;

        var fromValue = ClassifyByValue(value);
        var fromName  = ClassifyByName(name);

        // Names decide customization details (hair vs face) better than the value type (which only says "Customization").
        if (fromValue is VitrineCategory.Unknown or VitrineCategory.Other)
            return fromName;
        if (fromValue == VitrineCategory.Body && fromName is VitrineCategory.Hair or VitrineCategory.Face or VitrineCategory.Tail or VitrineCategory.Ears or VitrineCategory.Skin)
            return fromName;
        if (fromValue == VitrineCategory.Gear && fromName is VitrineCategory.Accessory or VitrineCategory.Weapon)
            return fromName;
        return fromValue;
    }

    /// <summary> Picks the category of a whole mod from its items: the most common category, ties broken by the order of <see cref="VitrineCategory"/>. Unknown / Other lose to anything else. </summary>
    public static VitrineCategory ClassifyMod(IEnumerable<AffectedItem> items)
    {
        var counts = new Dictionary<VitrineCategory, int>();
        foreach (var item in items)
            counts[item.Category] = counts.GetValueOrDefault(item.Category) + 1;

        if (counts.Count == 0)
            return VitrineCategory.Other;

        VitrineCategory best   = VitrineCategory.Unknown;
        var             bestN  = -1;
        foreach (var (category, n) in counts.OrderBy(kv => kv.Key))
        {
            var weak = category is VitrineCategory.Unknown or VitrineCategory.Other;
            if (weak && bestN >= 0 && best is not (VitrineCategory.Unknown or VitrineCategory.Other))
                continue;
            if (n > bestN || (bestN >= 0 && best is VitrineCategory.Unknown or VitrineCategory.Other && !weak))
            {
                best  = category;
                bestN = n;
            }
        }

        return best == VitrineCategory.Unknown ? VitrineCategory.Other : best;
    }

    /// <summary> Classification from the runtime type Penumbra attached to the changed item. </summary>
    /// <summary> Categories whose affected items are gear a character can wear (and therefore try on through Glamourer). </summary>
    public static bool IsEquippable(VitrineCategory category)
        => category is VitrineCategory.Gear or VitrineCategory.Weapon or VitrineCategory.Accessory;

    /// <summary>
    /// Reads the game item id from a changed-item value. Penumbra hands over (ChangedItemType, id) tuples for items; Lumina
    /// rows and item-like objects expose RowId / ItemId / Id. Null when nothing usable is found. Never throws.
    /// </summary>
    public static ulong? ExtractItemId(object? value)
    {
        try
        {
            switch (value)
            {
                case null:                                                           return null;
                case ValueTuple<global::Penumbra.Api.Enums.ChangedItemType, uint> t:  return t.Item2;
                case ValueTuple<global::Penumbra.Api.Enums.ChangedItemType, ulong> t2: return t2.Item2;
                case uint u:                                                         return u;
                case ulong l:                                                        return l;
            }

            var type = value.GetType();
            foreach (var propName in new[] { "RowId", "ItemId", "Id" })
            {
                var prop = type.GetProperty(propName);
                if (prop is null)
                    continue;
                var raw = prop.GetValue(value);
                var id = raw switch
                {
                    uint u   => (ulong?)u,
                    ulong l  => l,
                    int i    => i >= 0 ? (ulong?)i : null,
                    long l2  => l2 >= 0 ? (ulong?)l2 : null,
                    _        => UnwrapId(raw),
                };
                if (id is not null)
                    return id;
            }
        }
        catch
        {
            // Reflection is best effort only.
        }

        return null;
    }

    /// <summary> Ids wrapped in a struct (e.g. a CustomItemId with an Id property). </summary>
    private static ulong? UnwrapId(object? raw)
    {
        if (raw is null)
            return null;
        var inner = raw.GetType().GetProperty("Id")?.GetValue(raw);
        return inner switch
        {
            uint u  => u,
            ulong l => l,
            _       => null,
        };
    }

    public static VitrineCategory ClassifyByValue(object? value)
    {
        if (value is null)
            return VitrineCategory.Unknown;

        // Penumbra may hand over a (ChangedItemType, id) style tuple.
        if (value is ValueTuple<global::Penumbra.Api.Enums.ChangedItemType, uint> typed)
            return FromChangedItemType(typed.Item1);
        if (value is ValueTuple<global::Penumbra.Api.Enums.ChangedItemType, ulong> typed2)
            return FromChangedItemType(typed2.Item1);

        var typeName = value.GetType().Name;
        if (typeName.Contains("Emote", StringComparison.OrdinalIgnoreCase))
            return VitrineCategory.Emote;
        if (typeName.Contains("Action", StringComparison.OrdinalIgnoreCase))
            return VitrineCategory.Animation;
        if (typeName.Contains("Companion", StringComparison.OrdinalIgnoreCase) || typeName.Contains("Minion", StringComparison.OrdinalIgnoreCase))
            return VitrineCategory.Minion;
        if (typeName.Contains("Mount", StringComparison.OrdinalIgnoreCase))
            return VitrineCategory.Mount;
        if (typeName.Contains("Npc", StringComparison.OrdinalIgnoreCase) || typeName.Contains("Monster", StringComparison.OrdinalIgnoreCase))
            return VitrineCategory.Npc;
        if (typeName.Contains("Customiz", StringComparison.OrdinalIgnoreCase))
            return VitrineCategory.Body;
        if (typeName.Contains("Item", StringComparison.OrdinalIgnoreCase))
            return ClassifyItemObject(value);

        return VitrineCategory.Unknown;
    }

    private static VitrineCategory FromChangedItemType(global::Penumbra.Api.Enums.ChangedItemType type)
        => type switch
        {
            global::Penumbra.Api.Enums.ChangedItemType.Item          => VitrineCategory.Gear,
            global::Penumbra.Api.Enums.ChangedItemType.ItemOffhand   => VitrineCategory.Weapon,
            global::Penumbra.Api.Enums.ChangedItemType.Action        => VitrineCategory.Animation,
            global::Penumbra.Api.Enums.ChangedItemType.Customization => VitrineCategory.Body,
            global::Penumbra.Api.Enums.ChangedItemType.Emote         => VitrineCategory.Emote,
            global::Penumbra.Api.Enums.ChangedItemType.Model         => VitrineCategory.Npc,
            global::Penumbra.Api.Enums.ChangedItemType.CustomArmor   => VitrineCategory.Gear,
            _                                                => VitrineCategory.Unknown,
        };

    /// <summary> Inspects an item-like object for an equip slot via reflection (guarded; never throws). </summary>
    private static VitrineCategory ClassifyItemObject(object value)
    {
        try
        {
            var type = value.GetType();
            foreach (var propName in new[] { "EquipSlotCategory", "EquipSlot", "Slot", "Type" })
            {
                var prop = type.GetProperty(propName);
                if (prop is null)
                    continue;
                var slotValue = prop.GetValue(value);
                if (slotValue is null)
                    continue;

                var slotText = SlotText(slotValue);
                var mapped   = ClassifyBySlotText(slotText);
                if (mapped != VitrineCategory.Unknown)
                    return mapped;
            }
        }
        catch
        {
            // Reflection is best effort only.
        }

        return VitrineCategory.Gear;
    }

    private static string SlotText(object slotValue)
    {
        var t = slotValue.GetType();
        if (t.IsEnum || slotValue is string)
            return slotValue.ToString() ?? string.Empty;

        // Lumina EquipSlotCategory row: boolean-ish columns named after the slots.
        var set = new List<string>();
        foreach (var prop in t.GetProperties())
        {
            if (prop.PropertyType != typeof(bool) && prop.PropertyType != typeof(sbyte) && prop.PropertyType != typeof(byte))
                continue;
            var v = prop.GetValue(slotValue);
            var on = v switch
            {
                bool b  => b,
                sbyte s => s != 0,
                byte b2 => b2 != 0,
                _       => false,
            };
            if (on)
                set.Add(prop.Name);
        }

        return string.Join(' ', set);
    }

    private static VitrineCategory ClassifyBySlotText(string text)
    {
        if (text.Length == 0)
            return VitrineCategory.Unknown;
        if (text.Contains("MainHand", StringComparison.OrdinalIgnoreCase) || text.Contains("OffHand", StringComparison.OrdinalIgnoreCase)
         || text.Contains("Weapon", StringComparison.OrdinalIgnoreCase))
            return VitrineCategory.Weapon;
        if (text.Contains("Ears", StringComparison.OrdinalIgnoreCase) || text.Contains("Neck", StringComparison.OrdinalIgnoreCase)
         || text.Contains("Wrist", StringComparison.OrdinalIgnoreCase) || text.Contains("Finger", StringComparison.OrdinalIgnoreCase)
         || text.Contains("Ring", StringComparison.OrdinalIgnoreCase))
            return VitrineCategory.Accessory;
        if (text.Contains("Head", StringComparison.OrdinalIgnoreCase) || text.Contains("Body", StringComparison.OrdinalIgnoreCase)
         || text.Contains("Gloves", StringComparison.OrdinalIgnoreCase) || text.Contains("Legs", StringComparison.OrdinalIgnoreCase)
         || text.Contains("Feet", StringComparison.OrdinalIgnoreCase) || text.Contains("Hands", StringComparison.OrdinalIgnoreCase))
            return VitrineCategory.Gear;
        return VitrineCategory.Unknown;
    }

    /// <summary> Classification from the display name alone. </summary>
    public static VitrineCategory ClassifyByName(string name)
    {
        var n = name.Trim();

        // Paths and file-ish keys first.
        if (n.Contains('\\') || n.IndexOf('/') > 0 || n.Contains(".scd", StringComparison.OrdinalIgnoreCase)
         || n.Contains(".avfx", StringComparison.OrdinalIgnoreCase) || n.Contains(".atex", StringComparison.OrdinalIgnoreCase))
        {
            if (SfxRegex().IsMatch(n)) return VitrineCategory.Sfx;
            if (VfxRegex().IsMatch(n)) return VitrineCategory.Vfx;
            if (UiRegex().IsMatch(n)) return VitrineCategory.Ui;
            if (HousingRegex().IsMatch(n)) return VitrineCategory.Housing;
            if (n.Contains("chara/human", StringComparison.OrdinalIgnoreCase))
            {
                if (HairRegex().IsMatch(n) || n.Contains("/hair/", StringComparison.OrdinalIgnoreCase)) return VitrineCategory.Hair;
                if (n.Contains("/face/", StringComparison.OrdinalIgnoreCase)) return VitrineCategory.Face;
                if (n.Contains("/tail/", StringComparison.OrdinalIgnoreCase)) return VitrineCategory.Tail;
                if (n.Contains("/zear/", StringComparison.OrdinalIgnoreCase)) return VitrineCategory.Ears;
                if (n.Contains("/body/", StringComparison.OrdinalIgnoreCase)) return VitrineCategory.Body;
                return VitrineCategory.Skin;
            }
            if (n.Contains("chara/equipment", StringComparison.OrdinalIgnoreCase)) return VitrineCategory.Gear;
            if (n.Contains("chara/accessory", StringComparison.OrdinalIgnoreCase)) return VitrineCategory.Accessory;
            if (n.Contains("chara/weapon", StringComparison.OrdinalIgnoreCase)) return VitrineCategory.Weapon;
            if (n.Contains("chara/monster", StringComparison.OrdinalIgnoreCase) || n.Contains("chara/demihuman", StringComparison.OrdinalIgnoreCase)) return VitrineCategory.Npc;
            if (n.Contains("chara/action", StringComparison.OrdinalIgnoreCase) || n.Contains("/animation/", StringComparison.OrdinalIgnoreCase) || n.Contains(".pap", StringComparison.OrdinalIgnoreCase) || n.Contains(".tmb", StringComparison.OrdinalIgnoreCase)) return VitrineCategory.Animation;
            return VitrineCategory.Other;
        }

        // Explicit prefixes Penumbra / users like to use ("Emote: Sit", "Action: Fast Blade", "Mount: Chocobo").
        var colon = n.IndexOf(':');
        if (colon > 0 && colon < 16)
        {
            var prefix = n[..colon].Trim();
            if (EmoteRegex().IsMatch(prefix)) return VitrineCategory.Emote;
            if (ActionRegex().IsMatch(prefix)) return VitrineCategory.Animation;
            if (MinionRegex().IsMatch(prefix)) return VitrineCategory.Minion;
            if (MountRegex().IsMatch(prefix)) return VitrineCategory.Mount;
            if (NpcRegex().IsMatch(prefix)) return VitrineCategory.Npc;
            if (UiRegex().IsMatch(prefix)) return VitrineCategory.Ui;
            if (VfxRegex().IsMatch(prefix)) return VitrineCategory.Vfx;
            if (SfxRegex().IsMatch(prefix)) return VitrineCategory.Sfx;
            if (HousingRegex().IsMatch(prefix)) return VitrineCategory.Housing;
        }

        // Customization rows are named "<Race> <Clan> <Gender> <Part> <N>" by Penumbra.
        if (RaceRegex().IsMatch(n) || n.Contains("Customization", StringComparison.OrdinalIgnoreCase))
        {
            if (HairRegex().IsMatch(n)) return VitrineCategory.Hair;
            if (FaceRegex().IsMatch(n)) return VitrineCategory.Face;
            if (TailRegex().IsMatch(n)) return VitrineCategory.Tail;
            if (EarsRegex().IsMatch(n)) return VitrineCategory.Ears;
            if (SkinRegex().IsMatch(n)) return VitrineCategory.Skin;
            if (BodyRegex().IsMatch(n)) return VitrineCategory.Body;
            return VitrineCategory.Body;
        }

        if (EmoteRegex().IsMatch(n)) return VitrineCategory.Emote;
        if (MinionRegex().IsMatch(n)) return VitrineCategory.Minion;
        if (MountRegex().IsMatch(n)) return VitrineCategory.Mount;
        if (NpcRegex().IsMatch(n)) return VitrineCategory.Npc;
        if (HousingRegex().IsMatch(n)) return VitrineCategory.Housing;
        if (VfxRegex().IsMatch(n)) return VitrineCategory.Vfx;
        if (SfxRegex().IsMatch(n)) return VitrineCategory.Sfx;
        if (UiRegex().IsMatch(n)) return VitrineCategory.Ui;
        if (ActionRegex().IsMatch(n)) return VitrineCategory.Animation;
        if (AccessoryRegex().IsMatch(n)) return VitrineCategory.Accessory;
        if (WeaponRegex().IsMatch(n)) return VitrineCategory.Weapon;
        if (HairRegex().IsMatch(n)) return VitrineCategory.Hair;
        if (FaceRegex().IsMatch(n)) return VitrineCategory.Face;
        if (TailRegex().IsMatch(n)) return VitrineCategory.Tail;
        if (SkinRegex().IsMatch(n)) return VitrineCategory.Skin;
        if (GearRegex().IsMatch(n)) return VitrineCategory.Gear;
        if (BodyRegex().IsMatch(n)) return VitrineCategory.Body;

        return VitrineCategory.Other;
    }

    /// <summary> Human readable label for a category. </summary>
    public static string Label(VitrineCategory category)
        => category switch
        {
            VitrineCategory.Gear      => "Outfit",
            VitrineCategory.Weapon    => "Weapon",
            VitrineCategory.Accessory => "Accessory",
            VitrineCategory.Hair      => "Hair",
            VitrineCategory.Face      => "Face & Eyes",
            VitrineCategory.Body      => "Body",
            VitrineCategory.Skin      => "Skin",
            VitrineCategory.Tail      => "Tail",
            VitrineCategory.Ears      => "Ears",
            VitrineCategory.Animation => "Animation",
            VitrineCategory.Emote     => "Emote",
            VitrineCategory.Vfx       => "VFX",
            VitrineCategory.Sfx       => "Sound",
            VitrineCategory.Ui        => "UI",
            VitrineCategory.Minion    => "Minion",
            VitrineCategory.Mount     => "Mount",
            VitrineCategory.Housing   => "Housing",
            VitrineCategory.Npc       => "NPC",
            VitrineCategory.Other     => "Unsorted",
            _                         => "Unknown",
        };
}
