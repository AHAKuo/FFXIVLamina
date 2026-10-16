using Lamina.Decals.Publish;
using Lamina.Tests.Persistence;
using Xunit;

namespace Lamina.Tests.Decals;

/// <summary> The publisher's pure memory (installed maps, situation memory, cleanup trimming) and the cache sweep behind the cleanup buttons. </summary>
public class RedirectLedgerTests
{
    private static readonly Guid Collection = Guid.NewGuid();

    private static Dictionary<string, string> Map(params (string Path, string File)[] entries)
        => entries.ToDictionary(e => e.Path, e => e.File, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Install_ReportsChange_OnlyWhenTheMapDiffers()
    {
        var ledger = new RedirectLedger();
        var map    = Map(("chara/body.tex", @"C:\cache\Body_1.tex"));

        Assert.True(ledger.Install(Collection, "s1", map));
        Assert.False(ledger.Install(Collection, "s1", Map(("CHARA/BODY.TEX", @"c:\cache\body_1.tex"))));
        Assert.True(ledger.Install(Collection, "s2", Map(("chara/body.tex", @"C:\cache\Body_2.tex"))));
        Assert.True(ledger.Install(Collection, "s3", Map(("chara/body.tex", @"C:\cache\Body_2.tex"), ("chara/face.tex", @"C:\cache\Face_1.tex"))));
        Assert.True(ledger.Install(Collection, "s4", Map()));

        Assert.True(ledger.IsInstalled(Collection, "s4"));
        Assert.False(ledger.IsInstalled(Collection, "s3"));
        Assert.False(ledger.IsInstalled(Guid.NewGuid(), "s4"));
    }

    [Fact]
    public void Forget_AndMarkRemoved_AffectTheInstalledSituation()
    {
        var ledger = new RedirectLedger();
        var map    = Map(("chara/body.tex", @"C:\cache\Body_1.tex"));
        ledger.Install(Collection, "s1", map);

        ledger.MarkRemoved(Collection);
        Assert.Null(ledger.InstalledSituation(Collection));
        Assert.False(ledger.IsInstalled(Collection, "s1"));
        // The map is kept for the change comparison: an identical re-add is no change.
        Assert.False(ledger.Install(Collection, "s1", map));

        ledger.Forget(Collection);
        Assert.Null(ledger.InstalledMap(Collection));
        Assert.True(ledger.Install(Collection, "s1", map));

        ledger.ForgetInstalled();
        Assert.Null(ledger.InstalledMap(Collection));
    }

    [Fact]
    public void Memory_RecallsRememberedSituations_MostRecentFirst_Bounded()
    {
        var ledger = new RedirectLedger();
        for (var i = 0; i < RedirectLedger.MemorySize + 2; i++)
            ledger.Remember($"s{i}", Map(("chara/body.tex", $@"C:\cache\Body_{i}.tex")));

        Assert.Equal(RedirectLedger.MemorySize, ledger.Remembered);
        Assert.Null(ledger.Recall("s0"));
        Assert.Null(ledger.Recall("s1"));
        Assert.Equal(@"C:\cache\Body_2.tex", ledger.Recall("s2")!["chara/body.tex"]);

        // Recalling s2 made it the most recent: the next overflow drops s3, not s2.
        ledger.Remember("new", Map(("chara/body.tex", @"C:\cache\Body_new.tex")));
        Assert.NotNull(ledger.Recall("s2"));
        Assert.Null(ledger.Recall("s3"));

        ledger.Drop("s2");
        Assert.Null(ledger.Recall("s2"));
    }

    [Fact]
    public void ReferencedFiles_UnionsInstalledAndRemembered()
    {
        var ledger = new RedirectLedger();
        ledger.Install(Collection, "s1", Map(("chara/body.tex", @"C:\cache\Body_1.tex")));
        ledger.Remember("s2", Map(("chara/body.tex", @"C:\cache\Body_2.tex"), ("chara/face.tex", @"C:\cache\Face_1.tex")));

        Assert.Equal(new[] { @"C:\cache\Body_1.tex", @"C:\cache\Body_2.tex", @"C:\cache\Face_1.tex" }, ledger.ReferencedFiles().OrderBy(f => f));
        Assert.Equal(new[] { @"C:\cache\Body_1.tex" }, ledger.InstalledFiles());
    }

    [Fact]
    public void KeepOnly_TrimsTheMemoryToTheInstalledSituation()
    {
        var ledger = new RedirectLedger();
        ledger.Remember("s1", Map(("chara/body.tex", @"C:\cache\Body_1.tex")));
        ledger.Remember("s2", Map(("chara/body.tex", @"C:\cache\Body_2.tex")));
        ledger.Remember("s3", Map(("chara/body.tex", @"C:\cache\Body_3.tex")));
        ledger.Install(Collection, "s2", Map(("chara/body.tex", @"C:\cache\Body_2.tex")));

        Assert.Equal(2, ledger.KeepOnly(ledger.InstalledSituation(Collection)));
        Assert.Equal(1, ledger.Remembered);
        Assert.NotNull(ledger.Recall("s2"));
        Assert.NotNull(ledger.InstalledMap(Collection));

        Assert.Equal(1, ledger.KeepOnly(null));
        Assert.Equal(0, ledger.Remembered);
    }

    [Fact]
    public void ForgetAll_DropsMemoryAndInstalled()
    {
        var ledger = new RedirectLedger();
        ledger.Remember("s1", Map(("chara/body.tex", @"C:\cache\Body_1.tex")));
        ledger.Install(Collection, "s1", Map(("chara/body.tex", @"C:\cache\Body_1.tex")));

        Assert.Equal(1, ledger.ForgetAll());
        Assert.Equal(0, ledger.Remembered);
        Assert.Null(ledger.InstalledMap(Collection));
        Assert.Empty(ledger.ReferencedFiles());
    }

    [Fact]
    public void Sweep_DeletesUnkeptBakesAndEveryTempFile()
    {
        using var dir = new TempDir();
        var keep  = Path.Combine(dir.Path, "Body_keep.tex");
        var stale = Path.Combine(dir.Path, "Body_stale.tex");
        var face  = Path.Combine(dir.Path, "Face_stale.tex");
        var temp  = Path.Combine(dir.Path, "Body_x.tex.3.tmp");
        var other = Path.Combine(dir.Path, "notes.txt");
        foreach (var f in new[] { keep, stale, face, temp, other })
            File.WriteAllBytes(f, new byte[] { 1, 2, 3 });

        var (bakes, temps) = BakeCache.Sweep(dir.Path, new[] { keep.ToUpperInvariant() });

        Assert.Equal(2, bakes);
        Assert.Equal(1, temps);
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(other));
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(face));
        Assert.False(File.Exists(temp));

        Assert.Equal((1, 0), BakeCache.Sweep(dir.Path, Array.Empty<string>()));
        Assert.False(File.Exists(keep));
        Assert.Equal((0, 0), BakeCache.Sweep(Path.Combine(dir.Path, "missing"), Array.Empty<string>()));
    }

    [Fact]
    public void Trim_ProtectsRememberedFiles_EvenBeyondTheLimit()
    {
        using var dir = new TempDir();
        var files = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var f = Path.Combine(dir.Path, $"Body_{i}.tex");
            File.WriteAllBytes(f, new byte[] { 1 });
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 1, 1, 0, i, 0, DateTimeKind.Utc));
            files.Add(f);
        }

        var ledger = new RedirectLedger();
        ledger.Remember("s", Map(("chara/body.tex", files[0]), ("chara/face.tex", files[1])));

        // Three in excess of the limit: the two oldest are protected, so the next three go and the newest survives.
        var deleted = BakeCache.Trim(dir.Path, keep: 3, ledger.ReferencedFiles());

        Assert.True(File.Exists(files[0]));
        Assert.True(File.Exists(files[1]));
        Assert.Equal(new[] { files[2], files[3], files[4] }, deleted.OrderBy(f => f));
        Assert.True(File.Exists(files[5]));
    }
}
