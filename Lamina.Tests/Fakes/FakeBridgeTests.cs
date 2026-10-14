using System.Numerics;
using Glamourer.Api.Enums;
using Lamina.Ipc;
using Lamina.Runtime;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Enums;
using Xunit;

namespace Lamina.Tests.Fakes;

/// <summary> Sanity tests for the in-memory bridges so other suites can rely on their behavior. </summary>
public class FakeBridgeTests
{
    [Fact]
    public void Availability_FlipRaisesEventOnce()
    {
        var bridge = new FakePenumbraBridge();
        var raised = 0;
        bridge.AvailabilityChanged += () => raised++;

        bridge.Available = false;
        bridge.Available = false;
        bridge.Available = true;

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Penumbra_TemporarySettingsRoundTripAndClear()
    {
        var p   = new FakePenumbraBridge();
        var col = p.AddCollection("Lamina");
        p.AddMod("SomeMod");

        var settings = new Dictionary<string, IReadOnlyList<string>> { ["Group"] = new List<string> { "A" } };
        Assert.Equal(PenumbraApiEc.Success, p.SetTemporaryModSettings(col, "SomeMod", true, 5, settings));
        Assert.Equal(PenumbraApiEc.ModMissing, p.SetTemporaryModSettings(col, "Missing", true, 0, settings));
        Assert.Equal(PenumbraApiEc.CollectionMissing, p.SetTemporaryModSettings(Guid.NewGuid(), "SomeMod", true, 0, settings));

        var temp = p.TempSettingsIn(col)["SomeMod"];
        Assert.True(temp.Enabled);
        Assert.Equal(5, temp.Priority);
        Assert.Equal("A", temp.Settings["Group"][0]);

        var current = p.GetCurrentModSettings(col, "SomeMod");
        Assert.NotNull(current);
        Assert.True(current.Value.Temporary);

        Assert.Equal(PenumbraApiEc.Success, p.RemoveAllTemporaryModSettings(col));
        Assert.Empty(p.TempSettingsIn(col));
        Assert.Equal(PenumbraApiEc.NothingChanged, p.RemoveAllTemporaryModSettings(col));
        Assert.Contains(p.Calls, c => c.Name == nameof(IPenumbraBridge.RemoveAllTemporaryModSettings));
    }

    [Fact]
    public void Penumbra_CollectionAssignmentAndLookup()
    {
        var p   = new FakePenumbraBridge();
        var col = p.AddCollection("Lamina");

        Assert.Equal(col, p.FindCollection("lamina"));
        Assert.Null(p.FindCollection("Nope"));

        var before = p.GetCollectionForObject(0);
        Assert.True(before.ObjectValid);
        Assert.False(before.IndividualSet);

        p.ObjectCollections[0] = col;
        var after = p.GetCollectionForObject(0);
        Assert.True(after.IndividualSet);
        Assert.Equal(col, after.CollectionId);

        p.ObjectCollections.Remove(0);
        Assert.False(p.GetCollectionForObject(0).IndividualSet);

        p.RedrawObject(0);
        Assert.Equal([0], p.Redraws);
    }

    [Fact]
    public void Penumbra_UnavailableReturnsFallbacks()
    {
        var p = new FakePenumbraBridge { Available = false };
        p.AddCollection("Lamina");
        Assert.Empty(p.GetCollections());
        Assert.Null(p.FindCollection("Lamina"));
        Assert.Equal(PenumbraApiEc.UnknownError, p.RemoveAllTemporaryModSettings(Guid.NewGuid()));
        p.RedrawObject(3);
        Assert.Empty(p.Redraws);
    }

    [Fact]
    public void Glamourer_DesignsStatesAndEvents()
    {
        var g      = new FakeGlamourerBridge();
        var design = new JObject { ["FileVersion"] = 3 };
        var id     = g.AddDesign("Test", design);

        Assert.Equal("Test", g.GetDesigns()[id].DisplayName);
        var fetched = g.GetDesign(id);
        Assert.NotNull(fetched);
        Assert.NotSame(design, fetched);
        Assert.Null(g.GetDesign(Guid.NewGuid()));

        Assert.Equal(GlamourerApiEc.Success, g.ApplyState(design, 0, lockState: true));
        Assert.True(g.Applied[0].Locked);
        Assert.Equal(GlamourerApiEc.Success, g.UnlockAndRevertToAutomation(0));
        Assert.False(g.Applied.ContainsKey(0));

        var received = new List<(nint, StateFinalizationType)>();
        g.StateFinalized += (a, t) => received.Add((a, t));
        g.RaiseStateFinalized(0x1234, StateFinalizationType.ReapplyAutomation);
        Assert.Single(received);
        Assert.Equal(StateFinalizationType.ReapplyAutomation, received[0].Item2);
    }

    [Fact]
    public void Honorific_SetAndClear()
    {
        var h     = new FakeHonorificBridge();
        var title = new HonorificTitle(false, "Rain Walker", true, new Vector3(1, 0, 0), null);

        Assert.True(h.SetTitle(0, title));
        Assert.Contains("Rain Walker", h.GetTitle(0));
        Assert.True(h.SetTitle(0, HonorificTitle.Cleared));
        Assert.Equal(string.Empty, h.GetTitle(0));
        Assert.True(h.SetTitle(0, title));
        Assert.True(h.SetTitle(0, null));
        Assert.Empty(h.Titles);
    }

    [Fact]
    public void Honorific_TitleJsonMatchesTitleDataLayout()
    {
        var json = HonorificBridge.BuildTitleJson(new HonorificTitle(false, "Hero", false, new Vector3(0.5f, 0.25f, 1f), new Vector3(0, 0, 0)));
        var obj  = JObject.Parse(json);
        Assert.Equal("Hero", obj["Title"]!.Value<string>());
        Assert.False(obj["IsPrefix"]!.Value<bool>());
        Assert.Equal(0.5f, obj["Color"]!["X"]!.Value<float>());
        Assert.Equal(0.25f, obj["Color"]!["Y"]!.Value<float>());
        Assert.Equal(1f, obj["Color"]!["Z"]!.Value<float>());
        Assert.NotNull(obj["Glow"]);

        var noColor = JObject.Parse(HonorificBridge.BuildTitleJson(new HonorificTitle(false, "Plain", true, null, null)));
        Assert.Null(noColor["Color"]);
        Assert.Null(noColor["Glow"]);
    }

    [Fact]
    public void Moodles_AddPresetRemove()
    {
        var m      = new FakeMoodlesBridge();
        var a      = m.AddMoodle("A");
        var b      = m.AddMoodle("B");
        var preset = m.AddPreset("Presets/AB", a, b);
        nint actor = 0x42;

        Assert.Equal(2, m.GetMoodles().Count);
        Assert.Single(m.GetPresets());

        m.AddMoodle(actor, a);
        Assert.Equal(new HashSet<Guid> { a }, m.ActiveOn(actor));
        m.ApplyPreset(actor, preset);
        Assert.Equal(new HashSet<Guid> { a, b }, m.ActiveOn(actor));
        m.RemoveMoodles(actor, [a]);
        Assert.Equal(new HashSet<Guid> { b }, m.ActiveOn(actor));

        Assert.Equal(3, m.Calls.Count(c => c.Name is nameof(IMoodlesBridge.AddMoodle) or nameof(IMoodlesBridge.ApplyPreset) or nameof(IMoodlesBridge.RemoveMoodles)));
    }

    [Fact]
    public void Glamourer_SetItem_RecordsSlotAndItem()
    {
        var bridge = new FakeGlamourerBridge();
        var code   = bridge.SetItem(0, ApiEquipSlot.Body, 12345, Array.Empty<byte>());

        Assert.Equal(GlamourerApiEc.Success, code);
        Assert.Equal(12345ul, bridge.Items[(0, ApiEquipSlot.Body)]);
        Assert.Contains(bridge.Calls, c => c.Name == "SetItem");
    }
}
