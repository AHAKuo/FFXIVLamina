using Lamina.Dtr;
using Xunit;

namespace Lamina.Tests.Dtr;

public class DtrTextTests
{
    [Fact]
    public void ToggleLabel_ShowsState()
    {
        Assert.Equal("Lamina: On", DtrText.ToggleLabel(true));
        Assert.Equal("Lamina: Off", DtrText.ToggleLabel(false));
    }

    [Fact]
    public void ToggleTooltip_ExplainsWhatOffDoes()
    {
        Assert.Contains("cleanup", DtrText.ToggleTooltip(true));
        Assert.Contains("turns it on", DtrText.ToggleTooltip(false));
    }

    [Fact]
    public void ReapplyTooltip_NamesProfileAndLastApply()
    {
        var now  = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var text = DtrText.ReapplyTooltip("Main", now.AddSeconds(-12), now);
        Assert.Contains("Profile: Main", text);
        Assert.Contains("Last apply: 12 s ago", text);
        Assert.Contains("/lam reapply", text);
    }

    [Fact]
    public void ReapplyTooltip_HandlesNoProfileAndNoApply()
    {
        var text = DtrText.ReapplyTooltip(null, null, DateTime.UtcNow);
        Assert.Contains("(none enabled)", text);
        Assert.Contains("nothing applied yet", text);
    }

    [Theory]
    [InlineData(-5, "0 s ago")]
    [InlineData(45, "45 s ago")]
    [InlineData(150, "3 min ago")]
    [InlineData(7200, "2 h ago")]
    public void Age_PicksUnit(int seconds, string expected)
        => Assert.Equal(expected, DtrText.Age(TimeSpan.FromSeconds(seconds)));
}
