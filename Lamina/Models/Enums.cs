namespace Lamina.Models;

/// <summary> Direction in which layers inside a pass and passes inside a group are walked. </summary>
public enum EvaluationDirection
{
    /// <summary> Default. Walk top to bottom; later (lower) rows override earlier ones. </summary>
    TopToBottom,
    /// <summary> Walk bottom to top; earlier (upper) rows override later ones. </summary>
    BottomToTop,
}

public enum RedrawPolicy
{
    /// <summary> Default. Defer the redraw while in combat, cutscene, loading, mounted / flying or GPose. </summary>
    DeferWhenUnsafe,
    /// <summary> Redraw immediately whenever mods changed. </summary>
    Immediate,
    /// <summary> Never redraw automatically (user runs /lam reapply or Penumbra redraw). </summary>
    Never,
}

public enum TargetKind
{
    LocalPlayer,
    Character,
    OtherPlayer,
}

public enum GroupOp
{
    All,
    Any,
}

public enum ModToggle
{
    Enable,
    Disable,
}

/// <summary> Equipment slots as Glamourer names them in its design JSON. </summary>
public enum EquipSlot
{
    MainHand,
    OffHand,
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Ears,
    Neck,
    Wrists,
    RFinger,
    LFinger,
}

/// <summary> Bonus slots as Glamourer names them. </summary>
public enum BonusSlot
{
    Glasses,
}

/// <summary> Customization fields as Glamourer names them in its design JSON ("Customize" object). </summary>
public enum CustomizeIndex
{
    Race,
    Gender,
    BodyType,
    Height,
    Clan,
    Face,
    Hairstyle,
    Highlights,
    SkinColor,
    EyeColorRight,
    HairColor,
    HighlightsColor,
    FacialFeature1,
    FacialFeature2,
    FacialFeature3,
    FacialFeature4,
    FacialFeature5,
    FacialFeature6,
    FacialFeature7,
    LegacyTattoo,
    TattooColor,
    Eyebrows,
    EyeColorLeft,
    EyeShape,
    SmallIris,
    Nose,
    Jaw,
    Mouth,
    Lipstick,
    LipColor,
    MuscleMass,
    TailShape,
    BustSize,
    FacePaint,
    FacePaintReversed,
    FacePaintColor,
}

/// <summary> Meta toggles. Hat / Visor / Weapon / VieraEars live under "Equipment" in Glamourer JSON, Wetness under "Customize". </summary>
public enum MetaIndex
{
    HatVisible,
    VisorToggled,
    WeaponVisible,
    VieraEarsVisible,
    Wetness,
}

[Flags]
public enum EquipSlotFlags : uint
{
    None     = 0,
    MainHand = 1 << 0,
    OffHand  = 1 << 1,
    Head     = 1 << 2,
    Body     = 1 << 3,
    Hands    = 1 << 4,
    Legs     = 1 << 5,
    Feet     = 1 << 6,
    Ears     = 1 << 7,
    Neck     = 1 << 8,
    Wrists   = 1 << 9,
    RFinger  = 1 << 10,
    LFinger  = 1 << 11,
    Glasses  = 1 << 12,
    All      = (1 << 13) - 1,
}

[Flags]
public enum CustomizeFlags : ulong
{
    None              = 0,
    Race              = 1ul << 0,
    Gender            = 1ul << 1,
    BodyType          = 1ul << 2,
    Height            = 1ul << 3,
    Clan              = 1ul << 4,
    Face              = 1ul << 5,
    Hairstyle         = 1ul << 6,
    Highlights        = 1ul << 7,
    SkinColor         = 1ul << 8,
    EyeColorRight     = 1ul << 9,
    HairColor         = 1ul << 10,
    HighlightsColor   = 1ul << 11,
    FacialFeature1    = 1ul << 12,
    FacialFeature2    = 1ul << 13,
    FacialFeature3    = 1ul << 14,
    FacialFeature4    = 1ul << 15,
    FacialFeature5    = 1ul << 16,
    FacialFeature6    = 1ul << 17,
    FacialFeature7    = 1ul << 18,
    LegacyTattoo      = 1ul << 19,
    TattooColor       = 1ul << 20,
    Eyebrows          = 1ul << 21,
    EyeColorLeft      = 1ul << 22,
    EyeShape          = 1ul << 23,
    SmallIris         = 1ul << 24,
    Nose              = 1ul << 25,
    Jaw               = 1ul << 26,
    Mouth             = 1ul << 27,
    Lipstick          = 1ul << 28,
    LipColor          = 1ul << 29,
    MuscleMass        = 1ul << 30,
    TailShape         = 1ul << 31,
    BustSize          = 1ul << 32,
    FacePaint         = 1ul << 33,
    FacePaintReversed = 1ul << 34,
    FacePaintColor    = 1ul << 35,
    All               = (1ul << 36) - 1,
}

/// <summary> Helpers to go between enum values and their flag bits. </summary>
public static class FlagExtensions
{
    public static EquipSlotFlags ToFlag(this EquipSlot slot)
        => (EquipSlotFlags)(1u << (int)slot);

    public static EquipSlotFlags ToFlag(this BonusSlot slot)
        => slot switch
        {
            BonusSlot.Glasses => EquipSlotFlags.Glasses,
            _                 => EquipSlotFlags.None,
        };

    public static CustomizeFlags ToFlag(this CustomizeIndex index)
        => (CustomizeFlags)(1ul << (int)index);

    public static bool Has(this EquipSlotFlags flags, EquipSlot slot)
        => (flags & slot.ToFlag()) != 0;

    public static bool Has(this EquipSlotFlags flags, BonusSlot slot)
        => (flags & slot.ToFlag()) != 0;

    public static bool Has(this CustomizeFlags flags, CustomizeIndex index)
        => (flags & index.ToFlag()) != 0;
}
