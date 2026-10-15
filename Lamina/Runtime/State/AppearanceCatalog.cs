using Dalamud.Plugin.Services;
using Lamina.Models;
using Lumina.Excel.Sheets;

namespace Lamina.Runtime.State;

/// <summary> One pickable customization value with an optional icon. </summary>
public readonly record struct AppearanceOption(int Value, uint IconId, string Label);

/// <summary> One colour swatch: the customization value and its RGBA as stored by the game. </summary>
public readonly record struct ColorOption(int Value, uint Rgba)
{
    public float R => (Rgba & 0xFF) / 255f;
    public float G => ((Rgba >> 8) & 0xFF) / 255f;
    public float B => ((Rgba >> 16) & 0xFF) / 255f;
}

/// <summary> Kind of editor a customization field wants. </summary>
public enum AppearanceFieldKind
{
    /// <summary> 0-based list of N shapes (eyebrows, eye shape, nose, jaw, mouth). </summary>
    List,
    /// <summary> Icons with values (face, hairstyle, tail / ears, face paint). </summary>
    Icons,
    /// <summary> Colour swatches. </summary>
    Colors,
    /// <summary> 0..100 percentage (height, bust, muscle / tail length). </summary>
    Percentage,
    /// <summary> Flag field: 0 or 128 (highlights, small iris, lipstick, face paint reversed, legacy tattoo). </summary>
    Flag,
    /// <summary> Facial feature N: 0 or 1 &lt;&lt; (N - 1). </summary>
    FacialFeature,
    /// <summary> Plain number (race, clan, gender, body type). </summary>
    Number,
}

/// <summary>
/// Everything the Appearance editor needs for one clan + gender: the valid values of every customization field, built from
/// the CharaMakeType / HairMakeType / CharaMakeCustomize sheets and the colour table (chara/xls/charamake/human.cmp),
/// following Glamourer's conventions (faces and tails 1-based, shape lists 0-based, flags 0 / 128, lips and face paint
/// colours dark 0-95 / light 128-223).
/// </summary>
public sealed class AppearanceSet
{
    public AppearanceSet(int clan, int gender)
    {
        Clan   = clan;
        Gender = gender;
    }

    public int Clan { get; }
    public int Gender { get; }

    public List<AppearanceOption> Faces { get; } = new();
    public List<AppearanceOption> Hairstyles { get; } = new();
    public List<AppearanceOption> TailShapes { get; } = new();
    public List<AppearanceOption> FacePaints { get; } = new();

    /// <summary> Number of entries of 0-based shape lists (eyebrows, eye shape, nose, jaw, mouth); 0 when the race has no such menu. </summary>
    public Dictionary<CustomizeIndex, int> ListSizes { get; } = new();

    public List<ColorOption> SkinColors { get; } = new();
    public List<ColorOption> HairColors { get; } = new();
    public List<ColorOption> HighlightColors { get; } = new();
    public List<ColorOption> EyeColors { get; } = new();
    public List<ColorOption> TattooColors { get; } = new();
    public List<ColorOption> LipColors { get; } = new();
    public List<ColorOption> FacePaintColors { get; } = new();

    /// <summary> Face value (1-based) → icon ids of facial features 1..7. </summary>
    public Dictionary<int, uint[]> FacialFeatureIcons { get; } = new();

    public bool HasTail
        => TailShapes.Count > 0;

    public IReadOnlyList<ColorOption> ColorsFor(CustomizeIndex index)
        => index switch
        {
            CustomizeIndex.SkinColor       => SkinColors,
            CustomizeIndex.HairColor       => HairColors,
            CustomizeIndex.HighlightsColor => HighlightColors,
            CustomizeIndex.EyeColorRight   => EyeColors,
            CustomizeIndex.EyeColorLeft    => EyeColors,
            CustomizeIndex.TattooColor     => TattooColors,
            CustomizeIndex.LipColor        => LipColors,
            CustomizeIndex.FacePaintColor  => FacePaintColors,
            _                              => Array.Empty<ColorOption>(),
        };

    public IReadOnlyList<AppearanceOption> IconsFor(CustomizeIndex index)
        => index switch
        {
            CustomizeIndex.Face      => Faces,
            CustomizeIndex.Hairstyle => Hairstyles,
            CustomizeIndex.TailShape => TailShapes,
            CustomizeIndex.FacePaint => FacePaints,
            _                        => Array.Empty<AppearanceOption>(),
        };
}

/// <summary> Builds and caches <see cref="AppearanceSet"/>s per clan + gender. </summary>
public sealed class AppearanceCatalog
{
    /// <summary> Which editor every customization index uses. </summary>
    public static AppearanceFieldKind KindOf(CustomizeIndex index)
        => index switch
        {
            CustomizeIndex.Race or CustomizeIndex.Gender or CustomizeIndex.BodyType or CustomizeIndex.Clan => AppearanceFieldKind.Number,
            CustomizeIndex.Height or CustomizeIndex.BustSize or CustomizeIndex.MuscleMass                  => AppearanceFieldKind.Percentage,
            CustomizeIndex.Face or CustomizeIndex.Hairstyle or CustomizeIndex.TailShape or CustomizeIndex.FacePaint => AppearanceFieldKind.Icons,
            CustomizeIndex.SkinColor or CustomizeIndex.HairColor or CustomizeIndex.HighlightsColor or CustomizeIndex.EyeColorRight
             or CustomizeIndex.EyeColorLeft or CustomizeIndex.TattooColor or CustomizeIndex.LipColor or CustomizeIndex.FacePaintColor => AppearanceFieldKind.Colors,
            CustomizeIndex.Highlights or CustomizeIndex.SmallIris or CustomizeIndex.Lipstick or CustomizeIndex.FacePaintReversed
             or CustomizeIndex.LegacyTattoo => AppearanceFieldKind.Flag,
            >= CustomizeIndex.FacialFeature1 and <= CustomizeIndex.FacialFeature7 => AppearanceFieldKind.FacialFeature,
            _ => AppearanceFieldKind.List,
        };

    /// <summary> Value a flag field carries when on. </summary>
    public const int FlagOn = 128;

    /// <summary> Value of facial feature N (1..7) when on. </summary>
    public static int FacialFeatureBit(CustomizeIndex index)
        => 1 << (index - CustomizeIndex.FacialFeature1);

    /// <summary>
    /// Display order of the editor. Race is implied by Clan and Body Type is not offered: values other than the player
    /// body type (1) belong to NPC models and crash the game.
    /// </summary>
    public static readonly IReadOnlyList<CustomizeIndex> EditorOrder = new[]
    {
        CustomizeIndex.Clan, CustomizeIndex.Gender,
        CustomizeIndex.Height, CustomizeIndex.BustSize, CustomizeIndex.MuscleMass,
        CustomizeIndex.Face, CustomizeIndex.Hairstyle, CustomizeIndex.Highlights, CustomizeIndex.HairColor, CustomizeIndex.HighlightsColor,
        CustomizeIndex.SkinColor, CustomizeIndex.EyeShape, CustomizeIndex.SmallIris, CustomizeIndex.EyeColorRight, CustomizeIndex.EyeColorLeft,
        CustomizeIndex.Eyebrows, CustomizeIndex.Nose, CustomizeIndex.Jaw, CustomizeIndex.Mouth, CustomizeIndex.Lipstick, CustomizeIndex.LipColor,
        CustomizeIndex.TailShape,
        CustomizeIndex.FacialFeature1, CustomizeIndex.FacialFeature2, CustomizeIndex.FacialFeature3, CustomizeIndex.FacialFeature4,
        CustomizeIndex.FacialFeature5, CustomizeIndex.FacialFeature6, CustomizeIndex.FacialFeature7, CustomizeIndex.LegacyTattoo, CustomizeIndex.TattooColor,
        CustomizeIndex.FacePaint, CustomizeIndex.FacePaintReversed, CustomizeIndex.FacePaintColor,
    };

    /// <summary> Human labels where the enum name is not enough. </summary>
    public static string LabelOf(CustomizeIndex index)
        => index switch
        {
            CustomizeIndex.MuscleMass => "Muscle mass / tail length",
            CustomizeIndex.TailShape  => "Tail / ears shape",
            CustomizeIndex.SmallIris  => "Small iris",
            CustomizeIndex.LegacyTattoo => "Legacy tattoo",
            CustomizeIndex.FacePaintReversed => "Face paint reversed",
            CustomizeIndex.EyeColorRight => "Eye colour (right)",
            CustomizeIndex.EyeColorLeft  => "Eye colour (left, odd eyes)",
            _ => System.Text.RegularExpressions.Regex.Replace(index.ToString(), "(?<=[a-z0-9])([A-Z])", " $1").Replace("Color", "colour"),
        };

    // Offsets (in 4-byte colours) inside human.cmp, from Penumbra.GameData's CmpData layout: Parameters block, then an
    // identical Interface block, then 32 per-clan+gender blocks (Skin 256, Hair 512, SkinInterface 256, HairInterface 256).
    private const int FullColors    = 256;
    private const int TonedColors   = 128;
    private const int ColorBlock    = FullColors * 2 + TonedColors * 2 + FullColors + TonedColors * 2 + FullColors * 4; // 2304
    private const int InterfaceBase = ColorBlock;
    private const int RacesBase     = ColorBlock * 2;
    private const int RaceStride    = FullColors + FullColors * 2 + FullColors + FullColors;                            // 1280
    private const int EyesOffset          = 0;
    private const int HairHighlightOffset = FullColors;
    private const int LipsDarkOffset      = FullColors * 2;
    private const int FacePaintDarkOffset = FullColors * 2 + TonedColors;
    private const int FeaturesOffset      = FullColors * 2 + TonedColors * 2;
    private const int LipsLightOffset     = FullColors * 3 + TonedColors * 2;
    private const int FacePaintLightOffset = FullColors * 3 + TonedColors * 3;
    private const int SkinInterfaceOffset = FullColors * 3;
    private const int HairInterfaceOffset = FullColors * 4;
    private const int ColorCount = 192;
    private const int TonedCount = 96;

    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly object _lock = new();
    private readonly Dictionary<(int Clan, int Gender), AppearanceSet> _sets = new();
    private uint[]? _colors;
    private bool _colorsFailed;

    public AppearanceCatalog(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log  = log;
    }

    /// <summary> Clan names by id (1..16), from the Tribe sheet. </summary>
    public IReadOnlyList<(int Id, string Name)> Clans
        => _clans ??= LoadClans();

    private IReadOnlyList<(int Id, string Name)>? _clans;

    /// <summary> The set for a clan (1..16) and gender (0 male, 1 female). Never null; empty lists when the sheets are missing. </summary>
    public AppearanceSet For(int clan, int gender)
    {
        clan   = Math.Clamp(clan, 1, 16);
        gender = gender == 0 ? 0 : 1;
        lock (_lock)
        {
            if (_sets.TryGetValue((clan, gender), out var cached))
                return cached;
        }

        var set = Build(clan, gender);
        lock (_lock)
            _sets[(clan, gender)] = set;
        return set;
    }

    private AppearanceSet Build(int clan, int gender)
    {
        var set = new AppearanceSet(clan, gender);
        try
        {
            var charaMake = _data.GetExcelSheet<CharaMakeType>()?.FirstOrDefault(r => r.Tribe.RowId == (uint)clan && r.Gender == gender);
            var hairMake  = _data.GetExcelSheet<HairMakeType>()?.FirstOrDefault(r => r.Tribe.RowId == (uint)clan && r.Gender == gender);
            var customize = _data.GetExcelSheet<CharaMakeCustomize>();

            if (charaMake is { } cm)
            {
                foreach (var menu in cm.CharaMakeStruct)
                {
                    var index = (CustomizeIndex?)ToCustomizeIndex(menu.Customize);
                    if (index is null || menu.SubMenuNum == 0)
                        continue;

                    switch (index)
                    {
                        case CustomizeIndex.Face:
                            for (var i = 0; i < menu.SubMenuNum && i < menu.SubMenuParam.Count; i++)
                                set.Faces.Add(new AppearanceOption(i + 1, menu.SubMenuParam[i], $"Face {i + 1}"));
                            break;
                        case CustomizeIndex.TailShape:
                            for (var i = 0; i < menu.SubMenuNum && i < menu.SubMenuParam.Count; i++)
                                set.TailShapes.Add(new AppearanceOption(i + 1, menu.SubMenuParam[i], $"Shape {i + 1}"));
                            break;
                        case CustomizeIndex.Eyebrows or CustomizeIndex.EyeShape or CustomizeIndex.Nose or CustomizeIndex.Jaw or CustomizeIndex.Mouth:
                            set.ListSizes[index.Value] = menu.SubMenuNum;
                            break;
                    }
                }

                var faceIcons = cm.FacialFeatureOption;
                for (var face = 0; face < faceIcons.Count; face++)
                {
                    var row = faceIcons[face];
                    set.FacialFeatureIcons[face + 1] = new[]
                    {
                        (uint)row.Option1, (uint)row.Option2, (uint)row.Option3, (uint)row.Option4, (uint)row.Option5, (uint)row.Option6, (uint)row.Option7,
                    };
                }
            }

            if (hairMake is { } hm && customize is not null)
            {
                foreach (var menu in hm.CharaMakeStruct)
                {
                    var index = ToCustomizeIndex(menu.Customize);
                    if (index is not (CustomizeIndex.Hairstyle or CustomizeIndex.FacePaint) || menu.SubMenuNum == 0)
                        continue;
                    var target = index == CustomizeIndex.Hairstyle ? set.Hairstyles : set.FacePaints;
                    var prefix = index == CustomizeIndex.Hairstyle ? "Hair" : "Paint";
                    for (var i = 0; i < menu.SubMenuNum && i < menu.SubMenuParam.Count; i++)
                    {
                        var rowId = menu.SubMenuParam[i];
                        if (rowId == uint.MaxValue)
                            continue;
                        if (customize.GetRowOrDefault(rowId) is { } row)
                            target.Add(new AppearanceOption(row.FeatureID, row.Icon, $"{prefix} {row.FeatureID}"));
                        else
                            target.Add(new AppearanceOption(i, 0, $"{prefix} {i}"));
                    }
                    target.Sort((a, b) => a.Value.CompareTo(b.Value));
                }
            }

            if (Colors() is { } colors)
            {
                var race = RacesBase + (((clan - 1) * 2) + gender) * RaceStride;
                Fill(set.SkinColors, colors, race + SkinInterfaceOffset, ColorCount, 0);
                Fill(set.HairColors, colors, race + HairInterfaceOffset, ColorCount, 0);
                Fill(set.HighlightColors, colors, InterfaceBase + HairHighlightOffset, ColorCount, 0);
                Fill(set.EyeColors, colors, InterfaceBase + EyesOffset, ColorCount, 0);
                Fill(set.TattooColors, colors, InterfaceBase + FeaturesOffset, ColorCount, 0);
                Fill(set.LipColors, colors, LipsDarkOffset, TonedCount, 0);
                Fill(set.LipColors, colors, LipsLightOffset, TonedCount, 128);
                Fill(set.FacePaintColors, colors, FacePaintDarkOffset, TonedCount, 0);
                Fill(set.FacePaintColors, colors, FacePaintLightOffset, TonedCount, 128);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"Lamina: failed to build the appearance set for clan {clan} gender {gender}.");
        }

        return set;
    }

    private static void Fill(List<ColorOption> target, uint[] colors, int offset, int count, int valueBase)
    {
        for (var i = 0; i < count && offset + i < colors.Length; i++)
            target.Add(new ColorOption(valueBase + i, colors[offset + i]));
    }

    private uint[]? Colors()
    {
        lock (_lock)
        {
            if (_colors is not null || _colorsFailed)
                return _colors;
        }

        uint[]? result = null;
        try
        {
            var file = _data.GetFile("chara/xls/charamake/human.cmp");
            if (file is not null && file.Data.Length >= RacesBase * 4)
            {
                result = new uint[file.Data.Length / 4];
                Buffer.BlockCopy(file.Data, 0, result, 0, result.Length * 4);
            }
            else
            {
                _log.Warning("Lamina: human.cmp is missing or too short; colour pickers will show indices only.");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Lamina: failed to read human.cmp.");
        }

        lock (_lock)
        {
            _colors       = result;
            _colorsFailed = result is null;
        }

        return result;
    }

    private IReadOnlyList<(int Id, string Name)> LoadClans()
    {
        var result = new List<(int, string)>();
        try
        {
            var sheet = _data.GetExcelSheet<Tribe>();
            if (sheet is not null)
                foreach (var row in sheet)
                {
                    var name = row.Masculine.ExtractText();
                    if (row.RowId is >= 1 and <= 16 && !string.IsNullOrWhiteSpace(name))
                        result.Add(((int)row.RowId, name));
                }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Lamina: failed to load the Tribe sheet.");
        }

        return result;
    }

    /// <summary> The game's customize byte index → Glamourer's CustomizeIndex. </summary>
    private static CustomizeIndex? ToCustomizeIndex(uint gameIndex)
        => gameIndex switch
        {
            0  => CustomizeIndex.Race,
            1  => CustomizeIndex.Gender,
            2  => CustomizeIndex.BodyType,
            3  => CustomizeIndex.Height,
            4  => CustomizeIndex.Clan,
            5  => CustomizeIndex.Face,
            6  => CustomizeIndex.Hairstyle,
            7  => CustomizeIndex.Highlights,
            8  => CustomizeIndex.SkinColor,
            9  => CustomizeIndex.EyeColorRight,
            10 => CustomizeIndex.HairColor,
            11 => CustomizeIndex.HighlightsColor,
            12 => CustomizeIndex.FacialFeature1,
            13 => CustomizeIndex.TattooColor,
            14 => CustomizeIndex.Eyebrows,
            15 => CustomizeIndex.EyeColorLeft,
            16 => CustomizeIndex.EyeShape,
            17 => CustomizeIndex.Nose,
            18 => CustomizeIndex.Jaw,
            19 => CustomizeIndex.Mouth,
            20 => CustomizeIndex.LipColor,
            21 => CustomizeIndex.MuscleMass,
            22 => CustomizeIndex.TailShape,
            23 => CustomizeIndex.BustSize,
            24 => CustomizeIndex.FacePaint,
            25 => CustomizeIndex.FacePaintColor,
            _  => null,
        };

    /// <summary>
    /// Reads Glamourer-style values out of a character's 26 customize bytes (Dalamud's ICharacter.Customize), splitting the
    /// flag bits the way Glamourer does. Returns null for a malformed array.
    /// </summary>
    public static Dictionary<CustomizeIndex, int>? FromCustomizeBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 26)
            return null;

        var result = new Dictionary<CustomizeIndex, int>();
        for (var i = 0u; i < 26; i++)
        {
            if (ToCustomizeIndex(i) is not { } index)
                continue;
            var value = bytes[i];
            switch (index)
            {
                case CustomizeIndex.Hairstyle:
                    result[index] = value;
                    break;
                case CustomizeIndex.Highlights:
                    result[CustomizeIndex.Highlights] = value & 0x80;
                    break;
                case CustomizeIndex.FacialFeature1:
                    for (var bit = 0; bit < 7; bit++)
                        result[CustomizeIndex.FacialFeature1 + bit] = value & (1 << bit);
                    result[CustomizeIndex.LegacyTattoo] = value & 0x80;
                    break;
                case CustomizeIndex.EyeShape:
                    result[CustomizeIndex.EyeShape]  = value & 0x7F;
                    result[CustomizeIndex.SmallIris] = value & 0x80;
                    break;
                case CustomizeIndex.Mouth:
                    result[CustomizeIndex.Mouth]    = value & 0x7F;
                    result[CustomizeIndex.Lipstick] = value & 0x80;
                    break;
                case CustomizeIndex.FacePaint:
                    result[CustomizeIndex.FacePaint]         = value & 0x7F;
                    result[CustomizeIndex.FacePaintReversed] = value & 0x80;
                    break;
                default:
                    result[index] = value;
                    break;
            }
        }

        return result;
    }
}
