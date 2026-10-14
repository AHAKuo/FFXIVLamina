using Lamina.Runtime;
using Lamina.Runtime.Engine;
using Xunit;

namespace Lamina.Tests.Engine;

public class DesignDifferTests
{
    [Fact]
    public void SameHash_NoChange()
    {
        var store  = new AppliedStateStore();
        var design = Designs.Of(d => Designs.Head(d, 5));
        store.DesignHash    = design.ContentHash;
        store.GlamourerHash = DesignDiffer.GlamourerHash(design);

        var delta = DesignDiffer.Diff(store, design, force: false);

        Assert.True(delta.NoChange);
        Assert.False(delta.RequiresRedraw);
    }

    [Fact]
    public void SameHash_Forced_ReappliesEverything()
    {
        var store  = new AppliedStateStore();
        var design = Designs.Of(d => Designs.Head(d, 5));
        store.DesignHash = design.ContentHash;

        var delta = DesignDiffer.Diff(store, design, force: true);

        Assert.False(delta.NoChange);
        Assert.True(delta.Forced);
        Assert.True(delta.GlamourerChanged);
        Assert.True(delta.ModsChanged);
        Assert.True(delta.HonorificChanged);
    }

    [Fact]
    public void ModsOnlyChange_RequiresRedraw_NotGlamourer()
    {
        var store    = new AppliedStateStore();
        var previous = Designs.Of(d => { Designs.Head(d, 5); Designs.Mod(d, "ModA"); });
        store.DesignHash    = previous.ContentHash;
        store.GlamourerHash = DesignDiffer.GlamourerHash(previous);
        store.ModsHash      = DesignDiffer.ModsHash(previous);

        var next  = Designs.Of(d => { Designs.Head(d, 5); Designs.Mod(d, "ModB"); });
        var delta = DesignDiffer.Diff(store, next, force: false);

        Assert.False(delta.NoChange);
        Assert.True(delta.ModsChanged);
        Assert.True(delta.RequiresRedraw);
        Assert.False(delta.GlamourerChanged);
        Assert.False(delta.HonorificChanged);
    }

    [Fact]
    public void GlamourerOnlyChange_NoRedraw()
    {
        var store    = new AppliedStateStore();
        var previous = Designs.Of(d => { Designs.Head(d, 5); Designs.Mod(d, "ModA"); });
        store.DesignHash    = previous.ContentHash;
        store.GlamourerHash = DesignDiffer.GlamourerHash(previous);
        store.ModsHash      = DesignDiffer.ModsHash(previous);

        var next  = Designs.Of(d => { Designs.Head(d, 6); Designs.Mod(d, "ModA"); });
        var delta = DesignDiffer.Diff(store, next, force: false);

        Assert.True(delta.GlamourerChanged);
        Assert.False(delta.ModsChanged);
        Assert.False(delta.RequiresRedraw);
    }

    [Fact]
    public void LosingGlamourerContent_IsAChange()
    {
        var store    = new AppliedStateStore();
        var previous = Designs.Of(d => Designs.Head(d, 5));
        store.DesignHash    = previous.ContentHash;
        store.GlamourerHash = DesignDiffer.GlamourerHash(previous);

        var delta = DesignDiffer.Diff(store, Designs.Empty(), force: false);

        Assert.True(delta.GlamourerChanged);
        Assert.False(delta.ModsChanged);
    }

    [Fact]
    public void Moodles_AddsOnlyNew_RemovesDropped_AndExplicit()
    {
        var keep    = Guid.NewGuid();
        var dropped = Guid.NewGuid();
        var fresh   = Guid.NewGuid();
        var explicitRemove = Guid.NewGuid();
        var preset  = Guid.NewGuid();

        var store = new AppliedStateStore();
        store.MoodlesAdded.Add(keep);
        store.MoodlesAdded.Add(dropped);

        var next = Designs.Of(d =>
        {
            Designs.Moodle(d, keep);
            Designs.Moodle(d, fresh);
            Designs.Moodle(d, preset, MoodleOp.AddPreset);
            Designs.Moodle(d, explicitRemove, MoodleOp.Remove);
        });

        var delta = DesignDiffer.Diff(store, next, force: false);

        Assert.True(delta.MoodlesChanged);
        Assert.Equal(new[] { fresh, preset }.OrderBy(g => g), delta.MoodleAdds.Select(a => a.Id).OrderBy(g => g));
        Assert.Contains(fresh, delta.MoodleAdds.Where(a => a.Op == MoodleOp.Add).Select(a => a.Id));
        Assert.Contains(preset, delta.MoodleAdds.Where(a => a.Op == MoodleOp.AddPreset).Select(a => a.Id));
        Assert.Equal(new[] { dropped, explicitRemove }.OrderBy(g => g), delta.MoodleRemoves.OrderBy(g => g));
    }

    [Fact]
    public void Honorific_ChangeDetected_ByValue()
    {
        var store = new AppliedStateStore { HonorificSet = new HonorificTitle(false, "Rain Walker", false, null, null) };
        var same  = Designs.Of(d => Designs.Title(d, "Rain Walker"));
        var other = Designs.Of(d => Designs.Title(d, "Sun Walker"));

        Assert.False(DesignDiffer.Diff(store, same, false).HonorificChanged);
        Assert.True(DesignDiffer.Diff(store, other, false).HonorificChanged);
    }

    [Fact]
    public void SubHashes_IgnoreProvenance_AndOrder()
    {
        var a = Designs.Of(d => { Designs.Mod(d, "B"); Designs.Mod(d, "A"); Designs.Head(d, 1); });
        var b = Designs.Of(d => { Designs.Head(d, 1); Designs.Mod(d, "A"); Designs.Mod(d, "B"); });

        Assert.Equal(DesignDiffer.ModsHash(a), DesignDiffer.ModsHash(b));
        Assert.Equal(DesignDiffer.GlamourerHash(a), DesignDiffer.GlamourerHash(b));
        Assert.NotEqual(DesignDiffer.ModsHash(a), DesignDiffer.GlamourerHash(a));
    }
}
