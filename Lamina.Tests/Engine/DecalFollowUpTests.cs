using Lamina.Decals;
using Lamina.Runtime.Engine;
using Xunit;

namespace Lamina.Tests.Engine;

/// <summary> The follow-up state machine: trigger → wait for loaded models → publish pending / republish on outfit change, and its loop and busy guards. </summary>
public class DecalFollowUpTests
{
    private sealed class Harness
    {
        public AppliedStateStore Store { get; } = new();
        public DateTime Now { get; set; } = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        public DrawObjectIdentity? Draw { get; set; } = new("outfit-a", true);
        public bool Ready { get; set; } = true;
        public bool Busy { get; set; }
        public List<IReadOnlyList<DecalSpec>> Published { get; } = new();
        public List<string> Log { get; } = new();
        public DecalFollowUp FollowUp { get; }

        public Harness()
            => FollowUp = new DecalFollowUp(Store, () => Now, () => Draw, () => Ready, () => Busy, d => Published.Add(d), Log.Add);

        /// <summary> Decals applied with the given draw key recorded; LastDesign carries one decal. </summary>
        public void ApplyDecals(string? drawKey = "outfit-a")
        {
            var design = Designs.Of(d => Designs.Decal(d));
            Store.LastDesign    = design;
            Store.DesignHash    = design.ContentHash;
            Store.DecalsHash    = DesignDiffer.DecalsHash(design);
            Store.DecalsDrawKey = drawKey;
        }

        public void Ticks(int count)
        {
            for (var i = 0; i < count; i++)
                FollowUp.Tick();
        }
    }

    [Fact]
    public void Trigger_WaitsForLoadedModels_ThenPublishesPending()
    {
        var h = new Harness { Draw = new DrawObjectIdentity("loading-1", false) };
        h.ApplyDecals(drawKey: null);
        h.Store.PendingDecals = h.Store.LastDesign!.Decals.Select(f => f.Value).ToList();

        h.FollowUp.Notice("redrawn");
        Assert.True(h.FollowUp.Waiting);
        h.Ticks(3);
        Assert.Empty(h.Published);

        h.Draw = new DrawObjectIdentity("outfit-b", true);
        h.FollowUp.Tick();
        // One tick of stability is required before the identity counts as loaded.
        Assert.Empty(h.Published);
        h.FollowUp.Tick();

        var published = Assert.Single(h.Published);
        Assert.Single(published);
        Assert.Null(h.Store.PendingDecals);
        Assert.Equal("outfit-b", h.Store.DecalsDrawKey);
        Assert.False(h.FollowUp.Waiting);
        Assert.Equal(1, h.FollowUp.Publishes);
    }

    [Fact]
    public void NothingDrawn_KeepsWaiting_WithoutTimingOut()
    {
        var h = new Harness { Draw = null };
        h.ApplyDecals();
        h.Store.PendingDecals = h.Store.LastDesign!.Decals.Select(f => f.Value).ToList();

        h.FollowUp.Notice("redrawn");
        h.Now += TimeSpan.FromSeconds(30);
        h.Ticks(3);
        Assert.True(h.FollowUp.Waiting);
        Assert.Empty(h.Published);

        h.Draw = new DrawObjectIdentity("outfit-a", true);
        h.Ticks(2);
        Assert.Single(h.Published);
    }

    [Fact]
    public void ModelsNeverLoad_PublishesAfterTheTimeout()
    {
        var h = new Harness { Draw = new DrawObjectIdentity("stuck", false) };
        h.ApplyDecals(drawKey: null);

        h.FollowUp.Notice("redrawn");
        h.Ticks(2);
        h.Now += DecalFollowUp.LoadTimeout - TimeSpan.FromMilliseconds(1);
        h.FollowUp.Tick();
        Assert.Empty(h.Published);

        h.Now += TimeSpan.FromMilliseconds(2);
        h.FollowUp.Tick();
        Assert.Single(h.Published);
        Assert.Equal("stuck", h.Store.DecalsDrawKey);
        Assert.Contains(h.Log, m => m.Contains("timeout"));
    }

    [Fact]
    public void SameKeyAsRecorded_DoesNotRepublish()
    {
        var h = new Harness();
        h.ApplyDecals(drawKey: "outfit-a");

        h.FollowUp.Notice("our own completion redraw");
        h.Ticks(3);

        Assert.Empty(h.Published);
        Assert.False(h.FollowUp.Waiting);
    }

    [Fact]
    public void KeyChanged_WithDecalsApplied_Republishes_AndConverges()
    {
        var h = new Harness();
        h.ApplyDecals(drawKey: "outfit-a");

        h.Draw = new DrawObjectIdentity("outfit-b", true);
        h.FollowUp.Poll();
        Assert.True(h.FollowUp.Waiting);
        h.Ticks(2);
        Assert.Single(h.Published);
        Assert.Equal("outfit-b", h.Store.DecalsDrawKey);

        // The completion redraw triggers again; the key now equals the recorded one, so nothing more happens.
        h.FollowUp.OnPublishCompleted(new DecalPublishResult(true, 1, Array.Empty<string>()));
        h.FollowUp.Notice("redrawn");
        h.FollowUp.Poll();
        h.Ticks(3);
        Assert.Single(h.Published);
    }

    [Fact]
    public void Poll_NoticesOnlyChanges()
    {
        var h = new Harness();
        h.ApplyDecals(drawKey: "outfit-a");

        h.FollowUp.Poll();
        h.Ticks(3);
        h.FollowUp.Poll();
        Assert.Empty(h.Log.Where(m => m.Contains("armed") && m.Contains("draw object changed")).Skip(1));
        Assert.Empty(h.Published);

        h.Draw = new DrawObjectIdentity("outfit-b", true);
        h.FollowUp.Poll();
        Assert.True(h.FollowUp.Waiting);
    }

    [Fact]
    public void NoDecalsApplied_NeverArms()
    {
        var h = new Harness();
        h.FollowUp.Notice("redrawn");
        h.FollowUp.Poll();
        Assert.False(h.FollowUp.Waiting);
        h.Ticks(3);
        Assert.Empty(h.Published);
    }

    [Fact]
    public void CleanedUpWhileWaiting_Stops()
    {
        var h = new Harness { Draw = new DrawObjectIdentity("loading", false) };
        h.ApplyDecals(drawKey: null);
        h.FollowUp.Notice("redrawn");
        Assert.True(h.FollowUp.Waiting);

        h.Store.Reset();
        h.Draw = new DrawObjectIdentity("outfit-a", true);
        h.Ticks(3);
        Assert.False(h.FollowUp.Waiting);
        Assert.Empty(h.Published);
    }

    [Fact]
    public void NotReady_LeavesPendingForTheNextTrigger()
    {
        var h = new Harness { Ready = false };
        h.ApplyDecals(drawKey: null);
        var pending = h.Store.LastDesign!.Decals.Select(f => f.Value).ToList();
        h.Store.PendingDecals = pending;

        h.FollowUp.Notice("redrawn");
        h.Ticks(3);
        Assert.Empty(h.Published);
        Assert.Same(pending, h.Store.PendingDecals);
        Assert.False(h.FollowUp.Waiting);

        h.Ready = true;
        h.FollowUp.Notice("collection assigned");
        h.Ticks(3);
        Assert.Single(h.Published);
    }

    [Fact]
    public void Busy_DefersTheDecision_UntilThePublishCompletes()
    {
        var h = new Harness { Busy = true };
        h.ApplyDecals(drawKey: "outfit-a");

        h.Draw = new DrawObjectIdentity("outfit-b", true);
        h.FollowUp.Poll();
        h.Ticks(3);
        Assert.Empty(h.Published);
        Assert.False(h.FollowUp.Waiting);

        // The publish in flight was prepared against the old outfit: its completion wants a redraw even for unchanged redirects.
        h.Busy = false;
        var redraw = h.FollowUp.OnPublishCompleted(new DecalPublishResult(true, 1, Array.Empty<string>(), RedirectsChanged: false));
        Assert.True(redraw);
        Assert.True(h.FollowUp.Waiting);
        h.Ticks(3);
        Assert.Single(h.Published);
        Assert.Equal("outfit-b", h.Store.DecalsDrawKey);
    }

    [Fact]
    public void Reset_DropsTheWait()
    {
        var h = new Harness { Draw = new DrawObjectIdentity("loading", false) };
        h.ApplyDecals(drawKey: null);
        h.FollowUp.Notice("redrawn");
        h.FollowUp.Reset();
        Assert.False(h.FollowUp.Waiting);
        h.Draw = new DrawObjectIdentity("outfit-a", true);
        h.Ticks(3);
        Assert.Empty(h.Published);
    }

    [Theory]
    [InlineData(true, 1, true, false, true)]
    [InlineData(true, 1, false, false, false)]
    [InlineData(true, 1, false, true, true)]
    [InlineData(false, 0, true, false, false)]
    [InlineData(false, 0, true, true, false)]
    [InlineData(false, 2, false, false, false)]
    [InlineData(false, 2, true, false, true)]
    public void RedrawWanted_OnlyWhenSomethingRedirected_AndChangedOrReloaded(bool success, int published, bool changed, bool reloaded, bool expected)
        => Assert.Equal(expected, DecalFollowUp.RedrawWanted(new DecalPublishResult(success, published, Array.Empty<string>(), changed), reloaded));
}
