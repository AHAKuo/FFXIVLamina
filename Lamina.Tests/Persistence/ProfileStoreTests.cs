using Lamina.Models;
using Lamina.Persistence;
using Xunit;

namespace Lamina.Tests.Persistence;

public class ProfileStoreTests
{
    private static (ProfileStore Store, JsonStore Json, Func<DateTime> Clock, Action<TimeSpan> Advance) Create(string dir)
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var json = new JsonStore(dir, () => now);
        var store = new ProfileStore(json, () => now);
        return (store, json, () => now, span => now += span);
    }

    [Fact]
    public void Load_SeedsMainProfile_WhenEmpty()
    {
        using var dir = new TempDir();
        var (store, json, _, _) = Create(dir.Path);
        var changed = 0;
        store.Changed += () => changed++;

        var errors = store.Load();

        Assert.Empty(errors);
        Assert.True(store.IsLoaded);
        Assert.Equal(1, changed);
        var main = Assert.Single(store.Profiles);
        Assert.Equal("Main", main.Name);
        Assert.True(main.Enabled);
        var group = Assert.Single(main.PassGroups);
        Assert.Equal("Default", group.Name);
        Assert.False(group.IsFinal);
        Assert.Empty(group.Passes);
        Assert.True(File.Exists(json.ProfilePath(main.Id)));
        Assert.Same(main, store.ActiveProfileFor(Target.LocalPlayer()));

        // Second load reads the seeded profile instead of seeding again.
        var (second, _, _, _) = Create(dir.Path);
        second.Load();
        Assert.Single(second.Profiles);
        Assert.Equal(main.Id, second.Profiles[0].Id);
    }

    [Fact]
    public void OneEnabledProfilePerTarget()
    {
        using var dir = new TempDir();
        var (store, _, _, _) = Create(dir.Path);
        store.Load();
        var main  = store.Profiles[0];
        var other = store.CreateProfile("Other");
        var alt   = store.CreateProfile("Alt character");
        alt.Targets = new List<Target> { new() { Kind = TargetKind.Character, Name = "Alt", WorldId = 1 } };
        Assert.False(other.Enabled);

        var disabled = store.SetProfileEnabled(other, true);

        Assert.True(other.Enabled);
        Assert.False(main.Enabled);
        Assert.Same(main, Assert.Single(disabled));
        Assert.Same(other, store.ActiveProfileFor(Target.LocalPlayer()));

        Assert.Empty(store.SetProfileEnabled(alt, true));
        Assert.True(alt.Enabled);
        Assert.True(other.Enabled);
        Assert.Same(alt, store.ActiveProfileFor(new Target { Kind = TargetKind.Character, Name = "alt", WorldId = 1 }));

        store.SetProfileEnabled(other, false);
        Assert.Null(store.ActiveProfileFor(Target.LocalPlayer()));
    }

    [Fact]
    public void DuplicateAndCopy_GiveFreshIdsEverywhere()
    {
        using var dir = new TempDir();
        var (store, _, _, _) = Create(dir.Path);
        store.Load();
        var full = Fixtures.FullProfile();
        full.Enabled = false;
        store.DeleteProfile(store.Profiles[0].Id);
        // Bring the fixture into the store through import so it is a tracked profile.
        var imported = ProfileTransfer.Import(ProfileTransfer.ExportJson(full)).Profile!;
        var target = store.CreateProfile("Target");
        var fromIds = Cloner.AllIds(imported).ToHashSet();
        Assert.Equal(fromIds.Count, Cloner.AllIds(imported).Count());

        var groupCopy = store.CopyGroupTo(imported.PassGroups[0], target);
        var groupIds  = Cloner.AllIds(groupCopy).ToList();
        Assert.Equal(groupIds.Count, groupIds.Distinct().Count());
        Assert.Empty(groupIds.Intersect(fromIds));
        Assert.Equal(imported.PassGroups[0].Passes.Count, groupCopy.Passes.Count);
        Assert.Equal(imported.PassGroups[0].Passes[0].Layers.Count, groupCopy.Passes[0].Layers.Count);
        Assert.Contains(groupCopy, target.PassGroups);

        var passCopy = store.CopyPassTo(imported.PassGroups[0].Passes[0], target, target.PassGroups[0]);
        var passIds  = Cloner.AllIds(passCopy).ToList();
        Assert.Equal(passIds.Count, passIds.Distinct().Count());
        Assert.Empty(passIds.Intersect(fromIds));
        Assert.Equal("Everything", passCopy.Name);
        Assert.Equal(EquipSlotFlags.Head | EquipSlotFlags.Body, passCopy.MacroOverride!.Equipment);

        var dup = store.DuplicateGroup(target, groupCopy.Id)!;
        Assert.Equal("Normal (2)", dup.Name);
        Assert.Equal(target.PassGroups.IndexOf(groupCopy) + 1, target.PassGroups.IndexOf(dup));
        Assert.Empty(Cloner.AllIds(dup).Intersect(Cloner.AllIds(groupCopy)));

        var passDup = store.DuplicatePass(target, dup, dup.Passes[0].Id)!;
        Assert.Equal("Everything (2)", passDup.Name);
        Assert.Empty(Cloner.AllIds(passDup).Intersect(Cloner.AllIds(dup.Passes[0])));

        var profileDup = store.DuplicateProfile(target.Id)!;
        Assert.Equal("Target (2)", profileDup.Name);
        Assert.False(profileDup.Enabled);
        Assert.Empty(Cloner.AllIds(profileDup).Intersect(Cloner.AllIds(target)));
        Assert.Equal(store.Profiles.ToList().IndexOf(target) + 1, store.Profiles.ToList().IndexOf(profileDup));
    }

    [Fact]
    public void MoveOperations_ReorderAndRelocate()
    {
        using var dir = new TempDir();
        var (store, _, _, _) = Create(dir.Path);
        store.Load();
        var profile = store.Profiles[0];
        var a = profile.PassGroups[0];
        var b = store.AddGroup(profile, "B");
        var c = store.AddGroup(profile);
        Assert.Equal("New Group", c.Name);

        Assert.True(store.MoveGroup(profile, 2, 0));
        Assert.Equal(new[] { c, a, b }, profile.PassGroups);
        Assert.False(store.MoveGroup(profile, 5, 0));

        var p1 = store.AddPass(profile, a, "P1");
        var p2 = store.AddPass(profile, a);
        Assert.Equal("New Pass", p2.Name);
        Assert.True(store.MovePass(profile, a, 1, 0));
        Assert.Equal(new[] { p2, p1 }, a.Passes);

        var l1 = new GlamourerLayer();
        var l2 = new HonorificLayer { Clear = true };
        store.AddLayer(profile, p1, l1);
        store.AddLayer(profile, p1, l2);
        Assert.True(store.MoveLayer(profile, p1, 0, 1));
        Assert.Equal(new Layer[] { l2, l1 }, p1.Layers);
        Assert.True(store.RemoveLayer(profile, p1, l2.Id));
        Assert.Single(p1.Layers);

        var other = store.CreateProfile("Other");
        Assert.True(store.MovePassTo(profile, a, p1, other, other.PassGroups[0]));
        Assert.DoesNotContain(p1, a.Passes);
        Assert.Contains(p1, other.PassGroups[0].Passes);
        Assert.Same(other, store.ProfileOf(p1));
        Assert.Same(other.PassGroups[0], store.GroupOf(p1));

        Assert.True(store.MoveGroupTo(profile, b, other));
        Assert.DoesNotContain(b, profile.PassGroups);
        Assert.Contains(b, other.PassGroups);
        Assert.Same(other, store.ProfileOf(b));

        Assert.True(store.RemovePass(profile, a, p2.Id));
        Assert.Empty(a.Passes);
        Assert.True(store.RemoveGroup(profile, c.Id));
        Assert.False(store.RemoveGroup(profile, c.Id));
    }

    [Fact]
    public void Saves_AreDebounced_AndFlushOnDemand()
    {
        using var dir = new TempDir();
        var (store, json, clock, advance) = Create(dir.Path);
        store.Load();
        var profile = store.Profiles[0];
        var saved = new List<string>();
        store.Saved += saved.Add;

        store.RenameProfile(profile, "Renamed");
        Assert.True(store.HasPendingSaves);
        advance(TimeSpan.FromMilliseconds(500));
        store.Tick(clock());
        Assert.Empty(saved);
        Assert.DoesNotContain("Renamed", File.ReadAllText(json.ProfilePath(profile.Id)));

        // Another edit restarts the window.
        store.MarkDirty(profile);
        advance(TimeSpan.FromMilliseconds(700));
        store.Tick(clock());
        Assert.Empty(saved);

        advance(TimeSpan.FromMilliseconds(400));
        store.Tick(clock());
        Assert.Single(saved);
        Assert.False(store.HasPendingSaves);
        Assert.Contains("\"name\": \"Renamed\"", File.ReadAllText(json.ProfilePath(profile.Id)));

        store.Config.Enabled = false;
        store.MarkConfigDirty();
        store.Biomes.TerritoryToBiome[1] = "Urban";
        store.MarkBiomesDirty();
        store.SaveNow();
        Assert.Equal(3, saved.Count);
        Assert.Contains("\"enabled\": false", File.ReadAllText(json.ConfigPath));
        Assert.Contains("\"1\": \"Urban\"", File.ReadAllText(json.BiomesPath));
    }

    [Fact]
    public void DeleteProfile_RemovesFile_AndForgetsPendingSave()
    {
        using var dir = new TempDir();
        var (store, json, clock, advance) = Create(dir.Path);
        store.Load();
        var extra = store.CreateProfile("Extra");
        store.SaveNow();
        Assert.True(File.Exists(json.ProfilePath(extra.Id)));

        store.RenameProfile(extra, "Pending");
        Assert.True(store.DeleteProfile(extra.Id));
        Assert.False(File.Exists(json.ProfilePath(extra.Id)));
        advance(TimeSpan.FromSeconds(5));
        store.Tick(clock());
        Assert.False(File.Exists(json.ProfilePath(extra.Id)));
        Assert.False(store.DeleteProfile(extra.Id));
        Assert.Null(store.GetProfile(extra.Id));
        Assert.NotNull(store.FindProfile("main"));
    }

    [Fact]
    public void SaveScheduler_RetriesAfterFailure()
    {
        var fail = true;
        var writes = 0;
        var failures = new List<string>();
        var scheduler = new SaveScheduler(_ => { if (fail) throw new IOException("locked"); writes++; }, () => { }, () => { }, TimeSpan.FromSeconds(1));
        scheduler.SaveFailed += (what, _) => failures.Add(what);
        var profile = new Profile { Name = "P" };
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        scheduler.MarkDirty(profile, t0);
        scheduler.Tick(t0.AddSeconds(2));
        Assert.Single(failures);
        Assert.True(scheduler.HasPending);
        Assert.Equal(0, writes);

        fail = false;
        // A failed item backs off: retried only after RetryBackoff, not on the very next tick.
        scheduler.Tick(t0.AddSeconds(3));
        Assert.Equal(0, writes);
        scheduler.Tick(t0.AddSeconds(2) + SaveScheduler.RetryBackoff);
        Assert.Equal(1, writes);
        Assert.False(scheduler.HasPending);

        scheduler.MarkDirty(profile, t0);
        scheduler.Forget(profile.Id);
        Assert.False(scheduler.HasPending);
    }
}

public class ProfileTransferTests
{
    [Fact]
    public void Profile_ExportImport_RoundTrips_WithFreshIds_AndLocalTarget()
    {
        var original = Fixtures.FullProfile();
        var json = ProfileTransfer.ExportJson(original);
        var clip = ProfileTransfer.ExportClipboard(original);
        Assert.StartsWith(ProfileTransfer.ClipboardPrefix, clip);
        Assert.True(ProfileTransfer.LooksImportable(clip));
        Assert.True(ProfileTransfer.LooksImportable(json));
        Assert.False(ProfileTransfer.LooksImportable("hello"));

        foreach (var text in new[] { json, clip })
        {
            var result = ProfileTransfer.Import(text);
            Assert.Equal(TransferKind.Profile, result.Kind);
            var imported = result.Profile!;
            Assert.Same(imported, result.Item);
            Assert.NotEqual(original.Id, imported.Id);
            Assert.Single(imported.Targets);
            Assert.Equal(TargetKind.LocalPlayer, imported.Targets[0].Kind);
            Assert.False(imported.Enabled);
            Assert.Equal(original.PassGroups.Count, imported.PassGroups.Count);
            Assert.Equal(original.PassGroups[0].Passes[0].Layers.Count, imported.PassGroups[0].Passes[0].Layers.Count);
            Assert.Empty(Cloner.AllIds(original).Intersect(Cloner.AllIds(imported)));
            Assert.Empty(result.Warnings);
        }
    }

    [Fact]
    public void GroupAndPass_ExportImport_DetectKind()
    {
        var profile = Fixtures.FullProfile();
        var group = profile.PassGroups[0];
        var pass  = group.Passes[0];

        var groupResult = ProfileTransfer.Import(ProfileTransfer.ExportClipboard(group));
        Assert.Equal(TransferKind.PassGroup, groupResult.Kind);
        Assert.NotNull(groupResult.Group);
        Assert.NotEqual(group.Id, groupResult.Group!.Id);
        Assert.Equal(group.Passes.Count, groupResult.Group.Passes.Count);

        var passResult = ProfileTransfer.Import(ProfileTransfer.ExportJson(pass));
        Assert.Equal(TransferKind.Pass, passResult.Kind);
        Assert.NotNull(passResult.Pass);
        Assert.NotEqual(pass.Id, passResult.Pass!.Id);
        Assert.Equal(pass.Layers.Count, passResult.Pass.Layers.Count);
    }

    [Fact]
    public void Import_FlagsMissingReferences_AndUnsupportedKinds()
    {
        var pass = new Pass
        {
            Name = "Refs",
            Conditions = new ConditionStack { Root = new UnknownCondition { UnknownKind = "Teleport", RawJson = """{"kind":"Teleport"}""" } },
            Layers =
            {
                new GlamourerLayer { DesignId = Guid.NewGuid() },
                new VitrineDesignLayer { VitrineDesignId = Guid.NewGuid() },
                new PenumbraAdjustLayer { Mods = { new ModState { ModDirectory = "Missing Mod" }, new ModState { ModDirectory = "Present" } } },
                new UnknownLayer { UnknownKind = "CustomizePlus", RawJson = """{"kind":"CustomizePlus"}""" },
            },
        };
        var resolvers = new ReferenceResolvers(
            GlamourerDesignExists: _ => false,
            VitrineDesignExists: _ => false,
            ModExists: m => m == "Present");

        var result = ProfileTransfer.Import(ProfileTransfer.ExportJson(pass), resolvers);

        Assert.Equal(5, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("Glamourer design"));
        Assert.Contains(result.Warnings, w => w.Contains("Vitrine design"));
        Assert.Contains(result.Warnings, w => w.Contains("Missing Mod"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("'Present'"));
        Assert.Contains(result.Warnings, w => w.Contains("CustomizePlus"));
        Assert.Contains(result.Warnings, w => w.Contains("Teleport"));

        Assert.Empty(ProfileTransfer.Import(ProfileTransfer.ExportJson(pass)).Warnings.Where(w => w.Contains("design")));
    }

    [Fact]
    public void Import_RejectsGarbage()
    {
        Assert.Throws<FormatException>(() => ProfileTransfer.Import("not json"));
        Assert.Throws<FormatException>(() => ProfileTransfer.Import("""{ "hello": 1 }"""));
        Assert.Throws<FormatException>(() => ProfileTransfer.Import(ProfileTransfer.ClipboardPrefix + "@@@"));
    }
}
