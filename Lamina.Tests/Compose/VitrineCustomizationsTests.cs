using Lamina.Ipc;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Lamina.Tests.Fakes;
using Xunit;

namespace Lamina.Tests.Compose;

/// <summary>
/// Vitrine designs of hair / face / tail mods (IPC 1.4 customizations) set the customize value fitting the player's clan and
/// gender, since the mod shows nothing unless that value is selected.
/// </summary>
public class VitrineCustomizationsTests
{
    private static readonly GameStateSnapshot MidlanderFemale = new() { LoggedIn = true, RaceId = 1, ClanId = 1,  Gender = 1 };
    private static readonly GameStateSnapshot XaelaFemale     = new() { LoggedIn = true, RaceId = 6, ClanId = 12, Gender = 1 };
    private static readonly GameStateSnapshot MidlanderMale   = new() { LoggedIn = true, RaceId = 1, ClanId = 1,  Gender = 0 };

    private readonly InstantEvaluator _eval = new();
    private readonly PassCompositor _compositor = new();

    private static VitrineCustomizationInfo Entry(string race, int gender, string key, int value)
        => new() { Race = race, Gender = gender, Key = key, Value = value, Name = $"Customization: {race} {(gender == 0 ? "Male" : "Female")} {key} {value}" };

    /// <summary> A hair mod shipping the same style for Midlander and Au Ra women, with a Viera ears entry on the side. </summary>
    private static VitrineDesignInfo HairDesign()
        => new()
        {
            Id       = Guid.NewGuid(),
            Name     = "Hair 194",
            Category = "Hair",
            Mods     = { new VitrineModInfo { ModDirectory = "Hair194", ModName = "Hair 194", Priority = 1 } },
            Customizations = new()
            {
                Entry("Midlander", 1, "Hairstyle", 194),
                Entry("AuRa",      1, "Hairstyle", 195),
                Entry("Viera",     0, "TailShape", 2),
            },
        };

    private (VirtualDesign, EvaluationReport) Compose(FakeVitrineBridge vitrine, GameStateSnapshot snapshot, Pass pass)
    {
        var p = new Profile { PassGroups = { Build.Group("G", true, pass) } };
        return _compositor.Compose(p, snapshot, _eval, EvaluationDirection.TopToBottom, Build.Resolver(new FakeGlamourerBridge(), vitrine));
    }

    private (VirtualDesign, EvaluationReport) Compose(FakeVitrineBridge vitrine, GameStateSnapshot snapshot, params Layer[] layers)
        => Compose(vitrine, snapshot, Build.Pass("p", true, layers));

    [Fact]
    public void HairDesign_SetsHairstyle_ForTheMatchingMidlanderFemale()
    {
        var vitrine = new FakeVitrineBridge();
        var hair    = HairDesign();
        vitrine.DesignList.Add(hair);

        var (design, report) = Compose(vitrine, MidlanderFemale, new VitrineDesignLayer { VitrineDesignId = hair.Id });

        var field = Assert.Single(design.Customize);
        Assert.Equal(CustomizeIndex.Hairstyle, field.Key);
        Assert.Equal(194, field.Value.Value);
        Assert.Equal(ModToggle.Enable, design.Mods["Hair194"].State!.Value.Value);
        Assert.Empty(report.SkippedLayers);
    }

    [Fact]
    public void HairDesign_PicksTheAuRaEntry_ForAnAuRaCharacter()
    {
        var vitrine = new FakeVitrineBridge();
        var hair    = HairDesign();
        vitrine.DesignList.Add(hair);

        var (design, _) = Compose(vitrine, XaelaFemale, new VitrineDesignLayer { VitrineDesignId = hair.Id });

        Assert.Equal(195, design.Customize[CustomizeIndex.Hairstyle].Value);
        Assert.False(design.Customize.ContainsKey(CustomizeIndex.TailShape));
    }

    [Fact]
    public void NoMatchingEntry_SetsNothing_ButTheModStillApplies()
    {
        var vitrine = new FakeVitrineBridge();
        var hair    = HairDesign();
        vitrine.DesignList.Add(hair);

        var (forMale, report) = Compose(vitrine, MidlanderMale, new VitrineDesignLayer { VitrineDesignId = hair.Id });
        Assert.Empty(forMale.Customize);
        Assert.Single(forMale.Mods);
        Assert.Empty(report.SkippedLayers);

        var (noCharacter, _) = Compose(vitrine, GameStateSnapshot.Empty, new VitrineDesignLayer { VitrineDesignId = hair.Id });
        Assert.Empty(noCharacter.Customize);
        Assert.Single(noCharacter.Mods);
    }

    [Fact]
    public void ApplyCustomizationsOff_SetsNothing()
    {
        var vitrine = new FakeVitrineBridge();
        var hair    = HairDesign();
        vitrine.DesignList.Add(hair);

        var (design, _) = Compose(vitrine, MidlanderFemale, new VitrineDesignLayer { VitrineDesignId = hair.Id, ApplyCustomizations = false });

        Assert.Empty(design.Customize);
        Assert.Single(design.Mods);
    }

    [Fact]
    public void MacroOverrideWithoutCustomization_StripsTheHairValue()
    {
        var vitrine = new FakeVitrineBridge();
        var hair    = HairDesign();
        hair.Pieces = new Dictionary<string, VitrinePieceInfo> { ["Head"] = new() { ItemId = 7, ItemName = "Hat" } };
        vitrine.DesignList.Add(hair);
        var pass = Build.Pass("p", true, new VitrineDesignLayer { VitrineDesignId = hair.Id });
        pass.MacroOverride = ApplyMask.OnlyEquipment();  // Customization = None

        var (design, _) = Compose(vitrine, MidlanderFemale, pass);

        Assert.Empty(design.Customize);
        Assert.Equal(7UL, design.Items[EquipSlot.Head].Value.ItemId);
    }

    [Fact]
    public void FirstEntryPerIndexWins_AndUnknownOrIdentityKeysAreIgnored()
    {
        var vitrine = new FakeVitrineBridge();
        var odd = new VitrineDesignInfo
        {
            Id   = Guid.NewGuid(),
            Name = "Odd",
            Mods = { new VitrineModInfo { ModDirectory = "OddMod" } },
            Customizations = new()
            {
                Entry("Midlander", 1, "Face",      3),
                Entry("Midlander", 1, "Face",      4),      // second face for the same race: the first wins, as in Vitrine's try-on
                Entry("Midlander", 1, "Horns",     1),      // key this version does not know
                Entry("Midlander", 1, "BodyType",  2),      // identity field: never a part a mod replaces
                Entry("midlander", 1, "hairstyle", 50),     // case-insensitive race and key
            },
        };
        vitrine.DesignList.Add(odd);

        var (design, _) = Compose(vitrine, MidlanderFemale, new VitrineDesignLayer { VitrineDesignId = odd.Id });

        Assert.Equal(2, design.Customize.Count);
        Assert.Equal(3, design.Customize[CustomizeIndex.Face].Value);
        Assert.Equal(50, design.Customize[CustomizeIndex.Hairstyle].Value);
    }

    [Fact]
    public void LaterLayers_OverrideTheDesignsHair()
    {
        var vitrine = new FakeVitrineBridge();
        var hair    = HairDesign();
        vitrine.DesignList.Add(hair);

        var (design, _) = Compose(vitrine, MidlanderFemale,
            new VitrineDesignLayer { VitrineDesignId = hair.Id },
            new AppearanceLayer { Customize = { [CustomizeIndex.Hairstyle] = 10 } });

        Assert.Equal(10, design.Customize[CustomizeIndex.Hairstyle].Value);
    }

    [Fact]
    public void Preview_ThreadsTheSnapshot_SoTheFocusedPassShowsTheHair()
    {
        var vitrine = new FakeVitrineBridge();
        var hair    = HairDesign();
        vitrine.DesignList.Add(hair);
        var pass = Build.Pass("p", true, new VitrineDesignLayer { VitrineDesignId = hair.Id });
        var p    = new Profile { PassGroups = { Build.Group("G", true, pass) } };

        var preview = _compositor.Preview(p, XaelaFemale, _eval, EvaluationDirection.TopToBottom, Build.Resolver(new FakeGlamourerBridge(), vitrine), null, pass.Id);

        Assert.Equal(195, preview.Full.Customize[CustomizeIndex.Hairstyle].Value);
        Assert.Equal(195, preview.Focus!.Customize[CustomizeIndex.Hairstyle].Value);
    }

    [Fact]
    public void Resolver_WithoutSnapshot_SetsNoCustomization()
    {
        var vitrine = new FakeVitrineBridge();
        var hair    = HairDesign();
        vitrine.DesignList.Add(hair);
        var resolver = Build.Resolver(new FakeGlamourerBridge(), vitrine);

        var draft = resolver.Resolve(new VitrineDesignLayer { VitrineDesignId = hair.Id }, Provenance.None, out var reason);

        Assert.Null(reason);
        Assert.Empty(draft!.Customize);
        Assert.Single(draft.Mods);
    }

    [Fact]
    public void LayerJson_DefaultsApplyCustomizationsOn_ForLayersSavedBeforeTheField()
    {
        const string json = """{ "Kind": "Vitrine", "VitrineDesignId": "6c0c7d3e-1b2a-5e4f-9a8b-7c6d5e4f3a2b" }""";

        var layer = Assert.IsType<VitrineDesignLayer>(Lamina.Persistence.JsonSettings.Deserialize<Layer>(json));
        Assert.True(layer.ApplyCustomizations);

        layer.ApplyCustomizations = false;
        var reloaded = Assert.IsType<VitrineDesignLayer>(Lamina.Persistence.JsonSettings.Deserialize<Layer>(Lamina.Persistence.JsonSettings.Serialize(layer)));
        Assert.False(reloaded.ApplyCustomizations);
    }
}
