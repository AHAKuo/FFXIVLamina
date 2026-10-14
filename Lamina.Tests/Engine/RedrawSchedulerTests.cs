using Lamina.Models;
using Lamina.Runtime.Engine;
using Lamina.Tests.Fakes;
using Xunit;

namespace Lamina.Tests.Engine;

public class RedrawSchedulerTests
{
    private static (RedrawScheduler Scheduler, FakePenumbraBridge Penumbra) Make(int? index = 0)
    {
        var penumbra = new FakePenumbraBridge();
        return (new RedrawScheduler(penumbra, () => index), penumbra);
    }

    [Fact]
    public void Coalesces_ManyRequests_IntoOneRedraw()
    {
        var (s, p) = Make();
        s.Request();
        s.Request();
        s.Request();

        Assert.True(s.Tick(Designs.Safe(), RedrawPolicy.Immediate));
        Assert.False(s.Tick(Designs.Safe(), RedrawPolicy.Immediate));
        Assert.Single(p.Redraws);
        Assert.False(s.Pending);
        Assert.Equal(1, s.RedrawsSent);
    }

    [Fact]
    public void DefersWhileUnsafe_FiresWhenSafe()
    {
        var (s, p) = Make();
        s.Request();

        Assert.False(s.Tick(Designs.Unsafe(), RedrawPolicy.DeferWhenUnsafe));
        Assert.True(s.Pending);
        Assert.Empty(p.Redraws);

        Assert.True(s.Tick(Designs.Safe(), RedrawPolicy.DeferWhenUnsafe));
        Assert.Single(p.Redraws);
        Assert.False(s.Pending);
    }

    [Fact]
    public void ImmediatePolicy_IgnoresUnsafe()
    {
        var (s, p) = Make();
        s.Request();
        Assert.True(s.Tick(Designs.Unsafe(), RedrawPolicy.Immediate));
        Assert.Single(p.Redraws);
    }

    [Fact]
    public void NeverPolicy_LeavesPending_UntilRedrawNow()
    {
        var (s, p) = Make();
        s.Request();
        Assert.False(s.Tick(Designs.Safe(), RedrawPolicy.Never));
        Assert.True(s.Pending);
        Assert.Empty(p.Redraws);

        Assert.True(s.RedrawNow());
        Assert.Single(p.Redraws);
        Assert.False(s.Pending);
    }

    [Fact]
    public void NoPlayer_KeepsRequest()
    {
        var (s, p) = Make(index: null);
        s.Request();
        Assert.False(s.Tick(Designs.Safe(), RedrawPolicy.Immediate));
        Assert.True(s.Pending);
        Assert.Empty(p.Redraws);
    }

    [Fact]
    public void PenumbraUnavailable_DropsRequest()
    {
        var (s, p) = Make();
        p.Available = false;
        s.Request();
        Assert.False(s.Tick(Designs.Safe(), RedrawPolicy.Immediate));
        Assert.False(s.Pending);
        Assert.Empty(p.Redraws);
    }

    [Fact]
    public void Cancel_ClearsPending()
    {
        var (s, _) = Make();
        s.Request();
        s.Cancel();
        Assert.False(s.Pending);
    }
}
