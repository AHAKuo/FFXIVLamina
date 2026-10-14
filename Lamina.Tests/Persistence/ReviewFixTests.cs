using Lamina.Models;
using Lamina.Persistence;
using Xunit;

namespace Lamina.Tests.Persistence;

/// <summary> Regression tests for the review findings fixed in the persistence layer. </summary>
public class ReviewFixTests
{
    [Fact]
    public void Load_DisablesSecondEnabledProfileForTheSameTarget()
    {
        using var dir = new TempDir();
        var store = new JsonStore(dir.Path);
        var first  = new Profile { Name = "First", Enabled = true };
        var second = new Profile { Name = "Second", Enabled = true };
        var other  = new Profile { Name = "Other", Enabled = true, Targets = { new Target { Kind = TargetKind.Character, Name = "X", WorldId = 1 } } };
        other.Targets.RemoveAt(0);
        store.SaveProfile(first);
        store.SaveProfile(second);
        store.SaveProfile(other);

        var profiles = new ProfileStore(store);
        var errors   = profiles.Load();

        var enabledLocal = profiles.Profiles.Where(p => p.Enabled && p.Targets.Contains(Target.LocalPlayer())).ToList();
        Assert.Single(enabledLocal);
        Assert.True(profiles.GetProfile(other.Id)!.Enabled);
        Assert.Contains(errors, e => e.IsWarning && e.Message.Contains("was disabled"));
        Assert.True(profiles.HasPendingSaves);
        Assert.NotNull(profiles.ActiveProfileFor(Target.LocalPlayer()));
    }

    [Fact]
    public void SaveScheduler_CatchesAnyException_AndBacksOff()
    {
        var failures = new List<string>();
        var attempts = 0;
        var scheduler = new SaveScheduler(_ => { attempts++; throw new InvalidOperationException("converter"); }, () => { }, () => { }, TimeSpan.FromSeconds(1));
        scheduler.SaveFailed += (what, _) => failures.Add(what);
        var profile = new Profile { Name = "P" };
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        scheduler.MarkDirty(profile, t0);
        scheduler.Tick(t0.AddSeconds(2));
        scheduler.Tick(t0.AddSeconds(2.1));
        scheduler.Tick(t0.AddSeconds(5));

        Assert.Equal(1, attempts);
        Assert.Single(failures);
        Assert.True(scheduler.HasPending);

        scheduler.Tick(t0.AddSeconds(2) + SaveScheduler.RetryBackoff);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void SaveScheduler_FlushIgnoresBackoff()
    {
        var attempts = 0;
        var scheduler = new SaveScheduler(_ => { attempts++; if (attempts == 1) throw new InvalidOperationException(); }, () => { }, () => { }, TimeSpan.FromSeconds(1));
        var profile = new Profile { Name = "P" };
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        scheduler.MarkDirty(profile, t0);
        scheduler.Tick(t0.AddSeconds(2));
        Assert.Equal(1, attempts);

        scheduler.FlushAll();
        Assert.Equal(2, attempts);
        Assert.False(scheduler.HasPending);
    }
}
