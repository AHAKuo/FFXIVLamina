using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Xunit;

namespace Lamina.Tests.Compose;

/// <summary> Penumbra Adjust "applies" semantics: mod entries merge per part with provenance per part (TDD 5.7). </summary>
public class ModMergeTests
{
    private readonly PassCompositor _compositor = new();
    private readonly InstantEvaluator _eval = new();

    private static Provenance Src(string note)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), note);

    [Fact]
    public void LaterEntrySetsOneGroup_RestOfTheModIsKeptWithItsOwnProvenance()
    {
        var first  = Src("Home / Look / Eve");
        var second = Src("Final / Humid / Eve");
        var draft  = new DesignDraft();
        draft.AddMod(ModContribution.From(new ModState
        {
            ModDirectory = "Eve",
            ModName      = "Eve Body",
            State        = ModToggle.Enable,
            Priority     = 5,
            Settings     = new() { ["Body"] = new() { "Curvy" }, ["Skin"] = new() { "Matte" } },
        }, first));
        draft.AddMod(ModContribution.From(new ModState
        {
            ModDirectory = "eve", // case-insensitive key
            State        = null,
            Priority     = null,
            Settings     = new() { ["Skin"] = new() { "Glossy" } },
        }, second));

        var mod = Assert.Single(draft.Mods).Value;
        Assert.Equal("Eve", mod.ModDirectory);
        Assert.Equal("Eve Body", mod.ModName);
        Assert.Equal(ModToggle.Enable, mod.State!.Value.Value);
        Assert.Equal(first, mod.State!.Value.Source);
        Assert.Equal(5, mod.Priority!.Value.Value);
        Assert.Equal(new[] { "Curvy" }, mod.Settings["Body"].Value);
        Assert.Equal(first, mod.Settings["Body"].Source);
        Assert.Equal(new[] { "Glossy" }, mod.Settings["Skin"].Value);
        Assert.Equal(second, mod.Settings["Skin"].Source);
    }

    [Fact]
    public void InheritedPartsStayInheritedWhenNothingEarlierSetThem()
    {
        var draft = new DesignDraft();
        draft.AddMod(ModContribution.From(new ModState { ModDirectory = "Eve", State = null, Settings = new() { ["Skin"] = new() { "Glossy" } } }, Src("a")));

        var design = draft.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>());
        var mod    = design.Mods["Eve"];

        Assert.Null(mod.State);
        Assert.Null(mod.Priority);
        Assert.Single(mod.Settings);
        var state = mod.ToModState();
        Assert.Null(state.State);
        Assert.Null(state.Priority);
        Assert.Equal(new[] { "Glossy" }, state.Settings!["Skin"]);
    }

    [Fact]
    public void EmptyEntriesAreDroppedAndSkippedLayersReported()
    {
        var layer = new PenumbraAdjustLayer { Mods = { new ModState { ModDirectory = "Eve", State = null, Priority = null, Settings = null } } };
        var draft = Build.Resolver(new FakeGlamourerBridge()).Resolve(layer, Src("x"), out var reason);

        Assert.Null(draft);
        Assert.Equal("no mod entry sets anything", reason);

        var d = new DesignDraft();
        d.Mods["Eve"] = new ModContribution("Eve");
        Assert.Empty(d.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>()).Mods);
    }

    [Fact]
    public void HashDistinguishesInheritedFromSetParts()
    {
        VirtualDesign Of(ModState s)
        {
            var d = new DesignDraft();
            d.AddMod(ModContribution.From(s, Src("x")));
            return d.Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>());
        }

        var inherit = Of(new ModState { ModDirectory = "Eve", State = null, Settings = new() { ["Skin"] = new() { "Glossy" } } });
        var enabled = Of(new ModState { ModDirectory = "Eve", State = ModToggle.Enable, Settings = new() { ["Skin"] = new() { "Glossy" } } });
        var same    = Of(new ModState { ModDirectory = "EVE", State = null, Settings = new() { ["Skin"] = new() { "Glossy" } } });

        Assert.NotEqual(inherit.ContentHash, enabled.ContentHash);
        Assert.Equal(inherit.ContentHash, same.ContentHash);
    }

    [Fact]
    public void PassesMergeModsPerGroupAcrossNormalAndFinalGroups()
    {
        var bridge = new FakeGlamourerBridge();
        var look  = new PenumbraAdjustLayer { Mods = { new ModState { ModDirectory = "Eve", State = ModToggle.Enable, Priority = 2, Settings = new() { ["Body"] = new() { "Curvy" }, ["Skin"] = new() { "Matte" } } } } };
        var humid = new PenumbraAdjustLayer { Mods = { new ModState { ModDirectory = "Eve", State = null, Priority = null, Settings = new() { ["Skin"] = new() { "Glossy" } } } } };
        var profile = new Profile
        {
            PassGroups =
            {
                Build.Group("Home", true, Build.Pass("Look", true, look)),
                Build.FinalGroup("Weather", true, 1, Build.Pass("Humid", true, humid)),
            },
        };

        var (design, _) = _compositor.Compose(profile, GameStateSnapshot.Empty, _eval, EvaluationDirection.TopToBottom, Build.Resolver(bridge));

        var mod = design.Mods["Eve"];
        Assert.Equal(ModToggle.Enable, mod.State!.Value.Value);
        Assert.Equal(look.Id, mod.State!.Value.Source.LayerId);
        Assert.Equal(2, mod.Priority!.Value.Value);
        Assert.Equal(new[] { "Curvy" }, mod.Settings["Body"].Value);
        Assert.Equal(new[] { "Glossy" }, mod.Settings["Skin"].Value);
        Assert.Equal(humid.Id, mod.Settings["Skin"].Source.LayerId);

        var text = DesignExplainer.Explain(design, _ => "G", _ => "P");
        Assert.Contains("Mod Eve / Skin = Glossy", text);
        Assert.Contains("Humid / ", text);
    }

    [Fact]
    public void FilterKeepsMergedModsIntact()
    {
        var draft = new DesignDraft();
        draft.AddMod(ModContribution.From(new ModState { ModDirectory = "Eve", Settings = new() { ["Body"] = new() { "Curvy" } } }, Src("a")));
        draft.AddMod(ModContribution.From(new ModState { ModDirectory = "Eve", State = null, Settings = new() { ["Skin"] = new() { "Glossy" } } }, Src("b")));

        var kept    = draft.Filter(new ApplyMask { Equipment = EquipSlotFlags.None });
        var dropped = draft.Filter(ApplyMask.OnlyEquipment());

        Assert.Equal(2, kept.Mods["Eve"].Settings.Count);
        Assert.Empty(dropped.Mods);
    }
}
