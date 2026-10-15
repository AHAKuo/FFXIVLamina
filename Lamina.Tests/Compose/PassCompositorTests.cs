using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Newtonsoft.Json.Linq;
using Lamina.Ipc;
using Lamina.Tests.Fakes;
using Xunit;

namespace Lamina.Tests.Compose;

public class PassCompositorTests
{
    private static readonly GameStateSnapshot Snap = GameStateSnapshot.Empty;
    private readonly InstantEvaluator _eval = new();
    private readonly PassCompositor _compositor = new();

    /// <summary> Registers a design that applies one head item (and optionally hairstyle) and returns its layer. </summary>
    private static GlamourerLayer Design(FakeGlamourerBridge bridge, string name, ulong head, int? hair = null, string? mod = null)
    {
        var id = Guid.NewGuid();
        bridge.Designs[id] = Build.Design(d =>
        {
            d["Equipment"]!["Head"] = Build.Slot(head);
            if (hair is { } h)
                d["Customize"]!["Hairstyle"] = Build.Cust(h);
            if (mod is not null)
                d["Mods"] = new JArray { new JObject { ["Name"] = mod, ["Directory"] = mod, ["State"] = "Enabled", ["Priority"] = 1 } };
        });
        bridge.Names[id] = name;
        return new GlamourerLayer { DesignId = id };
    }

    private (VirtualDesign, EvaluationReport) Compose(Profile p, FakeGlamourerBridge bridge, EvaluationDirection dir = EvaluationDirection.TopToBottom, FakeVitrineBridge? vitrine = null)
        => _compositor.Compose(p, Snap, _eval, dir, Build.Resolver(bridge, vitrine));

    [Fact]
    public void FirstTrueNormalGroupWins_AndScanStops()
    {
        var bridge = new FakeGlamourerBridge();
        var p = new Profile
        {
            PassGroups =
            {
                Build.Group("Off",    false, Build.Pass("a", true, Design(bridge, "A", 1))),
                Build.Group("First",  true,  Build.Pass("b", true, Design(bridge, "B", 2))),
                Build.Group("Second", true,  Build.Pass("c", true, Design(bridge, "C", 3))),
            },
        };

        var (design, report) = Compose(p, bridge);

        Assert.Equal(p.PassGroups[1].Id, design.ActiveGroupId);
        Assert.Equal(2ul, design.Items[EquipSlot.Head].Value.ItemId);
        Assert.Single(report.MatchedPassIds);
        Assert.Equal(p.PassGroups[1].Id, design.Items[EquipSlot.Head].Source.GroupId);
    }

    [Fact]
    public void DisabledGroupsAndPassesAreSkipped()
    {
        var bridge = new FakeGlamourerBridge();
        var disabledGroup = Build.Group("Disabled", true, Build.Pass("a", true, Design(bridge, "A", 1)));
        disabledGroup.Enabled = false;
        var disabledPass = Build.Pass("off", true, Design(bridge, "X", 9));
        disabledPass.Enabled = false;
        var p = new Profile
        {
            PassGroups =
            {
                disabledGroup,
                Build.Group("Live", true, disabledPass, Build.Pass("on", true, Design(bridge, "B", 2))),
            },
        };

        var (design, _) = Compose(p, bridge);

        Assert.Equal(p.PassGroups[1].Id, design.ActiveGroupId);
        Assert.Equal(2ul, design.Items[EquipSlot.Head].Value.ItemId);
    }

    [Fact]
    public void NoGroupActive_ProducesEmptyDesign()
    {
        var bridge = new FakeGlamourerBridge();
        var p = new Profile { PassGroups = { Build.Group("Off", false, Build.Pass("a", true, Design(bridge, "A", 1))) } };

        var (design, report) = Compose(p, bridge);

        Assert.True(design.IsEmpty);
        Assert.Null(design.ActiveGroupId);
        Assert.Empty(report.MatchedPassIds);
        Assert.Equal(VirtualDesign.Empty.ContentHash, design.ContentHash);
    }

    [Fact]
    public void LaterPassesOverrideEarlier_TopToBottom_AndFlipBottomToTop()
    {
        var bridge = new FakeGlamourerBridge();
        var p = new Profile
        {
            PassGroups =
            {
                Build.Group("G", true,
                    Build.Pass("upper", true, Design(bridge, "Upper", 1, hair: 10)),
                    Build.Pass("lower", true, Design(bridge, "Lower", 2))),
            },
        };

        var (topDown, _) = Compose(p, bridge, EvaluationDirection.TopToBottom);
        Assert.Equal(2ul, topDown.Items[EquipSlot.Head].Value.ItemId);
        Assert.Equal(10, topDown.Customize[CustomizeIndex.Hairstyle].Value);          // passthrough from upper
        Assert.Equal(p.PassGroups[0].Passes[1].Id, topDown.Items[EquipSlot.Head].Source.PassId);

        var (bottomUp, _) = Compose(p, bridge, EvaluationDirection.BottomToTop);
        Assert.Equal(1ul, bottomUp.Items[EquipSlot.Head].Value.ItemId);
        Assert.Equal(p.PassGroups[0].Passes[0].Id, bottomUp.Items[EquipSlot.Head].Source.PassId);
    }

    [Fact]
    public void LayersInsideAPassStackInDirection()
    {
        var bridge = new FakeGlamourerBridge();
        var pass = Build.Pass("p", true, Design(bridge, "A", 1, hair: 5), Design(bridge, "B", 2));
        var p = new Profile { PassGroups = { Build.Group("G", true, pass) } };

        var (topDown, _) = Compose(p, bridge);
        Assert.Equal(2ul, topDown.Items[EquipSlot.Head].Value.ItemId);
        Assert.Equal(5, topDown.Customize[CustomizeIndex.Hairstyle].Value);
        Assert.Equal(pass.Layers[1].Id, topDown.Items[EquipSlot.Head].Source.LayerId);

        var (bottomUp, _) = Compose(p, bridge, EvaluationDirection.BottomToTop);
        Assert.Equal(1ul, bottomUp.Items[EquipSlot.Head].Value.ItemId);
    }

    [Fact]
    public void FinalGroupsLayerOnTopByPriority()
    {
        var bridge = new FakeGlamourerBridge();
        var p = new Profile
        {
            PassGroups =
            {
                Build.Group("Normal",  true, Build.Pass("n", true, Design(bridge, "N", 1, hair: 1))),
                Build.FinalGroup("FinalHi", true, 10, Build.Pass("hi", true, Design(bridge, "Hi", 3))),
                Build.FinalGroup("FinalLo", true, 1, Build.Pass("lo", true, Design(bridge, "Lo", 2, hair: 2))),
                Build.FinalGroup("FinalOff", false, 99, Build.Pass("off", true, Design(bridge, "Off", 9))),
            },
        };

        var (design, report) = Compose(p, bridge);

        Assert.Equal(p.PassGroups[0].Id, design.ActiveGroupId);
        Assert.Equal(new[] { p.PassGroups[2].Id, p.PassGroups[1].Id }, design.ActiveFinalGroupIds);
        Assert.Equal(3ul, design.Items[EquipSlot.Head].Value.ItemId);                  // highest priority wins
        Assert.Equal(2, design.Customize[CustomizeIndex.Hairstyle].Value);              // FinalLo over Normal
        Assert.Equal(3, report.MatchedPassIds.Count);
    }

    [Fact]
    public void FinalGroupAppliesEvenWithoutNormalGroup()
    {
        var bridge = new FakeGlamourerBridge();
        var p = new Profile
        {
            PassGroups =
            {
                Build.Group("Normal", false, Build.Pass("n", true, Design(bridge, "N", 1))),
                Build.FinalGroup("Final", true, 0, Build.Pass("f", true, Design(bridge, "F", 5))),
            },
        };

        var (design, _) = Compose(p, bridge);

        Assert.Null(design.ActiveGroupId);
        Assert.Equal(5ul, design.Items[EquipSlot.Head].Value.ItemId);
    }

    [Fact]
    public void MacroOverrideFiltersGlamourerLayersOnly()
    {
        var bridge = new FakeGlamourerBridge();
        var pass = Build.Pass("p", true,
            Design(bridge, "A", 1, hair: 5, mod: "HairMod"),
            new PenumbraAdjustLayer { Mods = { new ModState { ModDirectory = "AdjustMod" } } },
            new HonorificLayer { Title = "T" });
        pass.MacroOverride = ApplyMask.OnlyEquipment();
        var p = new Profile { PassGroups = { Build.Group("G", true, pass) } };

        var (design, _) = Compose(p, bridge);

        Assert.Equal(1ul, design.Items[EquipSlot.Head].Value.ItemId);
        Assert.Empty(design.Customize);
        Assert.False(design.Mods.ContainsKey("HairMod"));
        Assert.True(design.Mods.ContainsKey("AdjustMod"));
        Assert.Equal("T", design.Honorific!.Value.Value.Title);
    }

    [Fact]
    public void MacroOverrideOnlyCustomizationDropsEquipmentAndMods()
    {
        var bridge = new FakeGlamourerBridge();
        var pass = Build.Pass("p", true, Design(bridge, "A", 1, hair: 5, mod: "M"));
        pass.MacroOverride = ApplyMask.OnlyCustomization();
        var p = new Profile { PassGroups = { Build.Group("G", true, pass) } };

        var (design, _) = Compose(p, bridge);

        Assert.Empty(design.Items);
        Assert.Empty(design.Mods);
        Assert.Equal(5, design.Customize[CustomizeIndex.Hairstyle].Value);
    }

    [Fact]
    public void ModsFromDesignsVitrineAndAdjustMerge_LaterWins()
    {
        var bridge  = new FakeGlamourerBridge();
        var vitrine = new FakeVitrineBridge();
        var vd = new VitrineDesignInfo
        {
            Id   = Guid.NewGuid(),
            Name = "V",
            Mods = { new VitrineModInfo { ModDirectory = "VMod", ModName = "V Mod", Priority = 2, Settings = new() { ["Group"] = new() { "A" } } } },
        };
        vitrine.DesignList.Add(vd);

        var pass = Build.Pass("p", true,
            Design(bridge, "A", 1, mod: "Shared"),
            new VitrineDesignLayer { VitrineDesignId = vd.Id, SettingsOverride = new() { ["Group"] = new() { "B" } } },
            new PenumbraAdjustLayer { Mods = { new ModState { ModDirectory = "shared", State = ModToggle.Disable, Priority = 7 } } });
        var p = new Profile { PassGroups = { Build.Group("G", true, pass) } };

        var (design, _) = Compose(p, bridge, vitrine: vitrine);

        Assert.Equal(2, design.Mods.Count);
        Assert.Equal(ModToggle.Disable, design.Mods["Shared"].Value.State);
        Assert.Equal(7, design.Mods["Shared"].Value.Priority);
        Assert.Equal(pass.Layers[2].Id, design.Mods["Shared"].Source.LayerId);
        Assert.Equal(new[] { "B" }, design.Mods["VMod"].Value.Settings!["Group"]);
        Assert.Equal("V Mod", design.Mods["VMod"].Value.ModName);
    }

    [Fact]
    public void GlamourerModAssociationsCanBeTurnedOff()
    {
        var bridge = new FakeGlamourerBridge();
        var p = new Profile { PassGroups = { Build.Group("G", true, Build.Pass("p", true, Design(bridge, "A", 1, mod: "M"))) } };

        var (design, _) = _compositor.Compose(p, Snap, _eval, EvaluationDirection.TopToBottom, Build.Resolver(bridge, applyMods: false));

        Assert.Empty(design.Mods);
        Assert.Single(design.Items);
    }

    [Fact]
    public void SkippedLayersAreReported()
    {
        var bridge = new FakeGlamourerBridge();
        var disabled = Design(bridge, "A", 1);
        disabled.Enabled = false;
        var missing = new GlamourerLayer { DesignId = Guid.NewGuid() };
        var vitrineMissing = new VitrineDesignLayer { VitrineDesignId = Guid.NewGuid() };
        var unknown = new UnknownLayer { UnknownKind = "Future" };
        var pass = Build.Pass("p", true, disabled, missing, vitrineMissing, unknown, Design(bridge, "B", 2));
        var p = new Profile { PassGroups = { Build.Group("G", true, pass) } };

        var (design, report) = Compose(p, bridge);

        Assert.Equal(2ul, design.Items[EquipSlot.Head].Value.ItemId);
        Assert.Equal(4, report.SkippedLayers.Count);
        Assert.Contains(report.SkippedLayers, s => s.LayerId == disabled.Id && s.Reason == "disabled");
        Assert.Contains(report.SkippedLayers, s => s.LayerId == missing.Id && s.Reason.Contains("not found"));
        Assert.Contains(report.SkippedLayers, s => s.LayerId == vitrineMissing.Id && s.Reason.Contains("Vitrine"));
        Assert.Contains(report.SkippedLayers, s => s.LayerId == unknown.Id && s.Reason.Contains("unsupported"));
    }

    [Fact]
    public void HonorificReplacesAndMoodlesAccumulate()
    {
        var bridge = new FakeGlamourerBridge();
        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();
        var preset = Guid.NewGuid();
        var p = new Profile
        {
            PassGroups =
            {
                Build.Group("G", true,
                    Build.Pass("a", true, new HonorificLayer { Title = "First" }, new MoodlesLayer { Add = { m1, m2 } }),
                    Build.Pass("b", true, new HonorificLayer { Clear = true }, new MoodlesLayer { Remove = { m2 }, AddPresets = { preset } })),
            },
        };

        var (design, _) = Compose(p, bridge);

        Assert.True(design.Honorific!.Value.Value.Clear);
        Assert.Equal(MoodleOp.Add, design.Moodles[m1].Value);
        Assert.Equal(MoodleOp.Remove, design.Moodles[m2].Value);
        Assert.Equal(MoodleOp.AddPreset, design.Moodles[preset].Value);
    }

    [Fact]
    public void ProvenanceNoteNamesGroupPassAndLayer()
    {
        var bridge = new FakeGlamourerBridge();
        var layer = Design(bridge, "A", 1);
        var pass = Build.Pass("Rainy night", true, layer);
        var p = new Profile { PassGroups = { Build.Group("Home", true, pass) } };

        var (design, _) = Compose(p, bridge);
        var src = design.Items[EquipSlot.Head].Source;

        Assert.Equal(p.PassGroups[0].Id, src.GroupId);
        Assert.Equal(pass.Id, src.PassId);
        Assert.Equal(layer.Id, src.LayerId);
        Assert.Equal($"Home / Rainy night / {layer.Describe()}", src.Note);
    }

    [Fact]
    public void HashIsIndependentOfBuildOrderAndProvenance()
    {
        var bridge1 = new FakeGlamourerBridge();
        var bridge2 = new FakeGlamourerBridge();
        var p1 = new Profile { PassGroups = { Build.Group("G", true, Build.Pass("a", true, Design(bridge1, "A", 1, hair: 3), Design(bridge1, "B", 2))) } };
        var p2 = new Profile { PassGroups = { Build.Group("Other", true, Build.Pass("x", true, Design(bridge2, "X", 2)), Build.Pass("y", true, Design(bridge2, "Y", 2, hair: 3))) } };

        var (d1, _) = Compose(p1, bridge1);
        var (d2, _) = Compose(p2, bridge2);

        Assert.Equal(d1.ContentHash, d2.ContentHash);
        Assert.NotEqual(d1.ActiveGroupId, d2.ActiveGroupId);
    }

    [Fact]
    public void DesignCacheFetchesOnce()
    {
        var bridge = new FakeGlamourerBridge();
        var layer = Design(bridge, "A", 1);
        var p = new Profile { PassGroups = { Build.Group("G", true, Build.Pass("a", true, layer)) } };
        var resolver = Build.Resolver(bridge);

        _compositor.Compose(p, Snap, _eval, EvaluationDirection.TopToBottom, resolver);
        _compositor.Compose(p, Snap, _eval, EvaluationDirection.TopToBottom, resolver);
        Assert.Equal(1, bridge.GetDesignCalls);

        resolver.Cache.Invalidate(layer.DesignId);
        _compositor.Compose(p, Snap, _eval, EvaluationDirection.TopToBottom, resolver);
        Assert.Equal(2, bridge.GetDesignCalls);
        Assert.Equal("A", resolver.Cache.TryGetDesignName(layer.DesignId));
    }

    [Fact]
    public void DesignCacheDoesNotRememberMissesWhileUnavailable()
    {
        var bridge = new FakeGlamourerBridge { Available = false };
        var layer = Design(bridge, "A", 1);
        var cache = new GlamourerDesignCache(bridge);

        Assert.Null(cache.GetRaw(layer.DesignId));
        bridge.Available = true;
        Assert.NotNull(cache.GetRaw(layer.DesignId));
        Assert.Equal(1, bridge.GetDesignCalls);
    }

    [Fact]
    public void PreviewShowsFocusedPassEvenWhenItDoesNotMatch()
    {
        var bridge = new FakeGlamourerBridge();
        var hidden = Build.Pass("hidden", false, Design(bridge, "H", 7, hair: 9));
        var live   = Build.Pass("live", true, Design(bridge, "L", 1));
        var group  = Build.Group("G", true, live, hidden);
        var p = new Profile { PassGroups = { group } };

        var preview = _compositor.Preview(p, Snap, _eval, EvaluationDirection.TopToBottom, Build.Resolver(bridge), null, hidden.Id);

        Assert.Equal(1ul, preview.Full.Items[EquipSlot.Head].Value.ItemId);
        Assert.NotNull(preview.Focus);
        Assert.Equal(7ul, preview.Focus!.Items[EquipSlot.Head].Value.ItemId);
        Assert.Equal(9, preview.Focus.Customize[CustomizeIndex.Hairstyle].Value);
        Assert.False(preview.FocusMatches);
        Assert.Equal(group.Id, preview.Focus.ActiveGroupId);
    }

    [Fact]
    public void PreviewOfGroupComposesItsPassesAsIfActive()
    {
        var bridge = new FakeGlamourerBridge();
        var inactive = Build.Group("Inactive", false, Build.Pass("a", true, Design(bridge, "A", 4)), Build.Pass("b", false, Design(bridge, "B", 5)));
        var active   = Build.Group("Active", true, Build.Pass("c", true, Design(bridge, "C", 1)));
        var p = new Profile { PassGroups = { active, inactive } };

        var preview = _compositor.Preview(p, Snap, _eval, EvaluationDirection.TopToBottom, Build.Resolver(bridge), inactive.Id, null);

        Assert.Equal(1ul, preview.Full.Items[EquipSlot.Head].Value.ItemId);
        Assert.Equal(4ul, preview.Focus!.Items[EquipSlot.Head].Value.ItemId);
        Assert.Single(preview.Focus.MatchedPassIds);
        Assert.False(preview.FocusMatches);
    }

    [Fact]
    public void ExplainListsGroupsPassesAndFields()
    {
        var bridge = new FakeGlamourerBridge();
        var pass = Build.Pass("Rainy", true, Design(bridge, "A", 1, mod: "Mod A"), new HonorificLayer { Title = "Rain Walker" });
        var group = Build.Group("Home", true, pass);
        var p = new Profile { PassGroups = { group } };
        var (design, report) = Compose(p, bridge);

        var text = DesignExplainer.Explain(design, _ => "Home", _ => "Rainy", report.SkippedLayers);

        Assert.Contains("Active group: Home", text);
        Assert.Contains("Matched passes: Rainy", text);
        Assert.Contains("Item Head = 1", text);
        Assert.Contains("Mod Mod A = enabled, priority 1", text);
        Assert.Contains("Honorific = \"Rain Walker\" (suffix)", text);
        Assert.Contains("Home / Rainy /", text);
        Assert.DoesNotContain("Skipped layers", text);
    }
}
