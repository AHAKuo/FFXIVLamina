using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Engine;
using Xunit;

namespace Lamina.Tests.Engine;

/// <summary> The applier completes inherited mod parts from the collection's permanent settings before writing. </summary>
public class ApplierInheritTests
{
    private static DesignDelta Full(VirtualDesign design, AppliedStateStore store)
        => DesignDiffer.Diff(store, design, force: false);

    private static void Partial(DesignDraft d, string dir, ModToggle? state, int? priority, Dictionary<string, List<string>>? settings)
        => d.AddMod(ModContribution.From(new ModState { ModDirectory = dir, State = state, Priority = priority, Settings = settings }, Designs.Src));

    [Fact]
    public void InheritedPartsComeFromThePermanentCollectionSettings()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Eve");
        rig.Penumbra.PermanentSettings[rig.Collection] = new()
        {
            ["Eve"] = (true, 7, new() { ["Body"] = new() { "Curvy" }, ["Skin"] = new() { "Matte" }, ["Extras"] = new() { "A", "B" } }, false),
        };
        var design = Designs.Of(d => Partial(d, "Eve", null, null, new() { ["Skin"] = new() { "Glossy" } }));

        var info = rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        var written = rig.Penumbra.TempSettingsIn(rig.Collection)["Eve"];
        Assert.True(written.Enabled);
        Assert.Equal(7, written.Priority);
        Assert.Equal(new[] { "Curvy" }, written.Settings["Body"]);
        Assert.Equal(new[] { "Glossy" }, written.Settings["Skin"]);
        Assert.Equal(new[] { "A", "B" }, written.Settings["Extras"]);
        Assert.Empty(info.Problems);
        Assert.Contains(rig.Penumbra.Calls, c => c.Name == "GetCurrentModSettings" && c.Args.Contains(true));
    }

    [Fact]
    public void TemporarySettingsOfOtherPluginsAreNotInherited()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Eve");
        rig.Penumbra.PermanentSettings[rig.Collection] = new() { ["Eve"] = (true, 1, new() { ["Skin"] = new() { "Matte" } }, false) };
        // Something (a Vitrine preview, say) left temporary settings behind; the applier's clean slate removes Lamina's own.
        rig.Penumbra.SetTemporaryModSettings(rig.Collection, "Eve", true, 99, new Dictionary<string, IReadOnlyList<string>> { ["Skin"] = new[] { "Preview" } });
        var design = Designs.Of(d => Partial(d, "Eve", null, null, new() { ["Body"] = new() { "Curvy" } }));

        rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        var written = rig.Penumbra.TempSettingsIn(rig.Collection)["Eve"];
        Assert.Equal(1, written.Priority);
        Assert.Equal(new[] { "Matte" }, written.Settings["Skin"]);
        Assert.Equal(new[] { "Curvy" }, written.Settings["Body"]);
    }

    [Fact]
    public void UnconfiguredModInheritsDisabledZeroAndPenumbraDefaults()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Eve");
        var design = Designs.Of(d => Partial(d, "Eve", null, null, new() { ["Skin"] = new() { "Glossy" } }));

        rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        var written = rig.Penumbra.TempSettingsIn(rig.Collection)["Eve"];
        Assert.False(written.Enabled);
        Assert.Equal(0, written.Priority);
        Assert.Single(written.Settings);
        Assert.Equal(new[] { "Glossy" }, written.Settings["Skin"]);
    }

    [Fact]
    public void SetPartsWinOverTheCollection()
    {
        var rig = new EngineRig();
        rig.Penumbra.AddMod("Eve");
        rig.Penumbra.PermanentSettings[rig.Collection] = new() { ["Eve"] = (false, 7, new() { ["Skin"] = new() { "Matte" } }, false) };
        var design = Designs.Of(d => Partial(d, "Eve", ModToggle.Enable, 3, new() { ["Skin"] = new() { "Glossy" } }));

        rig.Applier.Apply(design, Full(design, rig.Store), Designs.Safe());

        var written = rig.Penumbra.TempSettingsIn(rig.Collection)["Eve"];
        Assert.True(written.Enabled);
        Assert.Equal(3, written.Priority);
        Assert.Equal(new[] { "Glossy" }, written.Settings["Skin"]);
    }
}
