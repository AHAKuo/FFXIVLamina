using Lamina.Ipc;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Lamina.Tests.Fakes;
using Xunit;

namespace Lamina.Tests.Compose;

/// <summary> Vitrine designs contribute their pieces (IPC 1.2) as equipment, narrowed by the layer's slot mask. </summary>
public class VitrinePiecesTests
{
    private static readonly GameStateSnapshot Snap = GameStateSnapshot.Empty;
    private readonly InstantEvaluator _eval = new();
    private readonly PassCompositor _compositor = new();

    private static VitrineDesignInfo Design(string name, string mod, params (string Slot, ulong Item)[] pieces)
    {
        var design = new VitrineDesignInfo
        {
            Id     = Guid.NewGuid(),
            Name   = name,
            Mods   = { new VitrineModInfo { ModDirectory = mod, ModName = mod, Priority = 1 } },
            Pieces = new Dictionary<string, VitrinePieceInfo>(),
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
    public void Pieces_BecomeEquipment_AndTheModIsApplied()
    {
        var vitrine = new FakeVitrineBridge();
        var cow = Design("Cowkini", "Cowkini", ("Body", 1001), ("Legs", 1002), ("Feet", 1003));
        vitrine.DesignList.Add(cow);

        var (design, _) = Compose(vitrine, new VitrineDesignLayer { VitrineDesignId = cow.Id });

        Assert.Equal(1001UL, design.Items[EquipSlot.Body].Value.ItemId);
        Assert.Equal(1002UL, design.Items[EquipSlot.Legs].Value.ItemId);
        Assert.Equal(1003UL, design.Items[EquipSlot.Feet].Value.ItemId);
        Assert.Equal(3, design.Items.Count);
        Assert.Equal(ModToggle.Enable, design.Mods["Cowkini"].State!.Value.Value);
    }

    [Fact]
    public void SlotOverride_NarrowsPieces_SoTwoDesignsCombine()
    {
        var vitrine = new FakeVitrineBridge();
        var cow   = Design("Cowkini", "Cowkini", ("Body", 1001), ("Legs", 1002), ("Feet", 1003));
        var boots = Design("Boots", "BootsMod", ("Body", 2001), ("Feet", 2003));
        vitrine.DesignList.Add(cow);
        vitrine.DesignList.Add(boots);

        var (design, _) = Compose(vitrine,
            new VitrineDesignLayer { VitrineDesignId = cow.Id,   SlotOverride = EquipSlotFlags.Body | EquipSlotFlags.Legs },
            new VitrineDesignLayer { VitrineDesignId = boots.Id, SlotOverride = EquipSlotFlags.Feet });

        Assert.Equal(1001UL, design.Items[EquipSlot.Body].Value.ItemId); // cow body kept: the boots layer masks its own body out
        Assert.Equal(1002UL, design.Items[EquipSlot.Legs].Value.ItemId);
        Assert.Equal(2003UL, design.Items[EquipSlot.Feet].Value.ItemId);
        Assert.Equal(2, design.Mods.Count);                              // both mods applied in full
    }

    [Fact]
    public void EmptyMask_EquipsNothing_ButStillAppliesTheMod()
    {
        var vitrine = new FakeVitrineBridge();
        var cow = Design("Cowkini", "Cowkini", ("Body", 1001));
        vitrine.DesignList.Add(cow);

        var (design, _) = Compose(vitrine, new VitrineDesignLayer { VitrineDesignId = cow.Id, SlotOverride = EquipSlotFlags.None });

        Assert.Empty(design.Items);
        Assert.Single(design.Mods);
    }

    [Fact]
    public void UnknownSlotNames_AndMissingPieces_AreIgnored()
    {
        var vitrine = new FakeVitrineBridge();
        var odd = Design("Odd", "OddMod", ("Body", 1001), ("Glasses", 42), ("Tentacles", 7));
        var old = new VitrineDesignInfo { Id = Guid.NewGuid(), Name = "Old", Mods = { new VitrineModInfo { ModDirectory = "OldMod" } } };
        vitrine.DesignList.Add(odd);
        vitrine.DesignList.Add(old);

        var (design, _) = Compose(vitrine, new VitrineDesignLayer { VitrineDesignId = odd.Id }, new VitrineDesignLayer { VitrineDesignId = old.Id });

        Assert.Single(design.Items);
        Assert.Equal(1001UL, design.Items[EquipSlot.Body].Value.ItemId);
    }

    [Fact]
    public void Pieces_ParseFromTheDocumentedJson()
    {
        const string json = """
            [{ "id": "a0f95146-309c-544e-9225-4702f2c86cb1", "name": "Cowkini", "category": "Gear", "isAuto": true, "baseDesignId": null,
               "mods": [ { "modDirectory": "Cowkini", "modName": "Cowkini", "enabled": true, "priority": 0, "settings": null } ],
               "applyAction": { "slots": { "Body": { "enabled": true, "itemId": 1001, "itemName": "Cow Top" } } },
               "pieces": { "Body": { "itemId": 1001, "itemName": "Cow Top" } } }]
            """;

        var design = Assert.Single(VitrineBridge.ParseDesigns(json));
        var piece  = Assert.Single(design.ResolvedPieces());
        Assert.Equal(EquipSlot.Body, piece.Slot);
        Assert.Equal(1001UL, piece.Piece.ItemId);
        Assert.Equal("Cow Top", piece.Piece.ItemName);
    }
}
