using Lamina.Ipc;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Lamina.Tests.Fakes;
using Xunit;

namespace Lamina.Tests.Compose;

/// <summary> The Equipment Cleanse layer and the Vitrine design "clear other slots" flag empty gear slots with Glamourer's Nothing. </summary>
public class EquipmentCleanseTests
{
    private static readonly GameStateSnapshot Snap = GameStateSnapshot.Empty;
    private readonly InstantEvaluator _eval = new();
    private readonly PassCompositor _compositor = new();

    private static VitrineDesignInfo Design(string name, bool clear, params (string Slot, ulong Item)[] pieces)
    {
        var design = new VitrineDesignInfo
        {
            Id              = Guid.NewGuid(),
            Name            = name,
            Mods            = { new VitrineModInfo { ModDirectory = name, ModName = name } },
            Pieces          = new Dictionary<string, VitrinePieceInfo>(),
            ClearOtherSlots = clear,
        };
        foreach (var (slot, item) in pieces)
            design.Pieces[slot] = new VitrinePieceInfo { ItemId = item, ItemName = $"{name} {slot}" };
        return design;
    }

    private (VirtualDesign, EvaluationReport) Compose(FakeVitrineBridge vitrine, params Layer[] layers)
    {
        var bridge = new FakeGlamourerBridge();
        var p = new Profile { PassGroups = { Build.Group("G", true, Build.Pass("p", true, layers)) } };
        return _compositor.Compose(p, Snap, _eval, EvaluationDirection.TopToBottom, Build.Resolver(bridge, vitrine));
    }

    [Fact]
    public void CleanseLayer_EmptiesEveryGearSlot_ButNotWeapons()
    {
        var (design, report) = Compose(new FakeVitrineBridge(), new EquipmentCleanseLayer());

        Assert.Equal(10, design.Items.Count);
        Assert.False(design.Items.ContainsKey(EquipSlot.MainHand));
        Assert.False(design.Items.ContainsKey(EquipSlot.OffHand));
        Assert.All(design.Items.Values, f => Assert.True(NothingItems.IsNothing(f.Value.ItemId)));
        Assert.Equal(NothingItems.IdFor(EquipSlot.Head), design.Items[EquipSlot.Head].Value.ItemId);
        Assert.Empty(report.SkippedLayers);
    }

    [Fact]
    public void CleanseBeforeOutfit_LeavesOnlyTheOutfitSlotsFilled()
    {
        var vitrine = new FakeVitrineBridge();
        var house = Design("House", false, ("Body", 3001), ("Feet", 3003));
        vitrine.DesignList.Add(house);

        var (design, _) = Compose(vitrine, new EquipmentCleanseLayer(), new VitrineDesignLayer { VitrineDesignId = house.Id });

        Assert.Equal(3001UL, design.Items[EquipSlot.Body].Value.ItemId);
        Assert.Equal(3003UL, design.Items[EquipSlot.Feet].Value.ItemId);
        Assert.True(NothingItems.IsNothing(design.Items[EquipSlot.Head].Value.ItemId));
        Assert.True(NothingItems.IsNothing(design.Items[EquipSlot.Legs].Value.ItemId));
        Assert.Equal(10, design.Items.Count);
    }

    [Fact]
    public void DesignFlag_ClearsTheSlotsItsPiecesDoNotFill()
    {
        var vitrine = new FakeVitrineBridge();
        var warrior = Design("Warrior", false, ("Head", 1000), ("Body", 1001), ("Legs", 1002), ("Feet", 1003));
        var house   = Design("House", true, ("Body", 3001));
        vitrine.DesignList.Add(warrior);
        vitrine.DesignList.Add(house);

        var (design, _) = Compose(vitrine,
            new VitrineDesignLayer { VitrineDesignId = warrior.Id },
            new VitrineDesignLayer { VitrineDesignId = house.Id });

        Assert.Equal(3001UL, design.Items[EquipSlot.Body].Value.ItemId);
        Assert.True(NothingItems.IsNothing(design.Items[EquipSlot.Head].Value.ItemId));
        Assert.True(NothingItems.IsNothing(design.Items[EquipSlot.Legs].Value.ItemId));
        Assert.True(NothingItems.IsNothing(design.Items[EquipSlot.Feet].Value.ItemId));
    }

    [Fact]
    public void LayerOverride_WinsOverTheDesignFlag_BothWays()
    {
        var vitrine = new FakeVitrineBridge();
        var warrior = Design("Warrior", false, ("Head", 1000), ("Body", 1001));
        var clearing = Design("Clearing", true, ("Feet", 3003));
        var plain    = Design("Plain", false, ("Feet", 4003));
        vitrine.DesignList.Add(warrior);
        vitrine.DesignList.Add(clearing);
        vitrine.DesignList.Add(plain);

        // Design says clear, layer says no: the warrior head survives.
        var (keep, _) = Compose(vitrine,
            new VitrineDesignLayer { VitrineDesignId = warrior.Id },
            new VitrineDesignLayer { VitrineDesignId = clearing.Id, ClearOtherSlotsOverride = false });
        Assert.Equal(1000UL, keep.Items[EquipSlot.Head].Value.ItemId);
        Assert.Equal(3003UL, keep.Items[EquipSlot.Feet].Value.ItemId);

        // Design says keep, layer says clear: the warrior head is emptied.
        var (wipe, _) = Compose(vitrine,
            new VitrineDesignLayer { VitrineDesignId = warrior.Id },
            new VitrineDesignLayer { VitrineDesignId = plain.Id, ClearOtherSlotsOverride = true });
        Assert.True(NothingItems.IsNothing(wipe.Items[EquipSlot.Head].Value.ItemId));
        Assert.Equal(4003UL, wipe.Items[EquipSlot.Feet].Value.ItemId);
    }

    [Fact]
    public void MaskedOutPieces_CountAsNotFilled_WhenClearing()
    {
        var vitrine = new FakeVitrineBridge();
        var house = Design("House", true, ("Body", 3001), ("Feet", 3003));
        vitrine.DesignList.Add(house);

        var (design, _) = Compose(vitrine, new VitrineDesignLayer { VitrineDesignId = house.Id, SlotOverride = EquipSlotFlags.Body });

        Assert.Equal(3001UL, design.Items[EquipSlot.Body].Value.ItemId);
        Assert.True(NothingItems.IsNothing(design.Items[EquipSlot.Feet].Value.ItemId));
    }

    [Fact]
    public void ClearFlag_ParsesFromJson_AndDefaultsToFalse()
    {
        const string json = """
            [{ "id": "a0f95146-309c-544e-9225-4702f2c86cb1", "name": "A", "category": "Gear", "isAuto": true, "mods": [], "clearOtherSlots": true },
             { "id": "b0f95146-309c-544e-9225-4702f2c86cb1", "name": "B", "category": "Gear", "isAuto": true, "mods": [] }]
            """;

        var designs = VitrineBridge.ParseDesigns(json);
        Assert.True(designs[0].ClearOtherSlots);
        Assert.False(designs[1].ClearOtherSlots);
    }

    [Fact]
    public void CleanseLayer_RoundTripsThroughTheLayerRegistry()
    {
        var layer = LayerKinds.Create(EquipmentCleanseLayer.KindName);
        Assert.IsType<EquipmentCleanseLayer>(layer);
        Assert.True(LayerKinds.IsMaskable(layer!));
    }
}
