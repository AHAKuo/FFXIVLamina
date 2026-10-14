using Lamina.Models;
using Lamina.Persistence;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Lamina.Tests.Persistence;

public class JsonStoreTests
{
    [Fact]
    public void SaveProfile_WritesAtomically_AndLoadsBack()
    {
        using var dir = new TempDir();
        var store = new JsonStore(dir.Path);
        var profile = Fixtures.FullProfile();

        store.SaveProfile(profile);

        var files = Directory.GetFiles(store.ProfilesDirectory);
        Assert.Single(files);
        Assert.DoesNotContain(files, f => f.EndsWith(".tmp"));
        Assert.Equal(store.ProfilePath(profile.Id), files[0]);

        var (loaded, errors) = store.LoadProfiles();
        Assert.Empty(errors);
        var reloaded = Assert.Single(loaded);
        Assert.Equal(profile.Id, reloaded.Id);
        Assert.Equal(profile.Name, reloaded.Name);
        Assert.Equal(1, JObject.Parse(File.ReadAllText(files[0]))["version"]!.Value<int>());
    }

    [Fact]
    public void ConfigAndBiomes_RoundTrip_AndDefaultWhenMissing()
    {
        using var dir = new TempDir();
        var store = new JsonStore(dir.Path);

        var (defaults, error) = store.LoadConfig();
        Assert.Null(error);
        Assert.True(defaults.Enabled);
        Assert.Equal("Lamina", defaults.CollectionName);

        var config = new LaminaConfig { Enabled = false, Direction = EvaluationDirection.BottomToTop, CollectionName = "Mine", DefaultHoldSeconds = 2.5f, RedrawPolicy = RedrawPolicy.Never, BackupsToKeep = 3 };
        store.SaveConfig(config);
        var (reloaded, _) = store.LoadConfig();
        Assert.False(reloaded.Enabled);
        Assert.Equal(EvaluationDirection.BottomToTop, reloaded.Direction);
        Assert.Equal("Mine", reloaded.CollectionName);
        Assert.Equal(2.5f, reloaded.DefaultHoldSeconds);
        Assert.Equal(RedrawPolicy.Never, reloaded.RedrawPolicy);
        Assert.Contains("\"direction\": \"BottomToTop\"", File.ReadAllText(store.ConfigPath));

        var biomes = new BiomeMap { TerritoryToBiome = { [132] = "Urban", [146] = "Desert" } };
        store.SaveBiomes(biomes);
        var (reloadedBiomes, biomesError) = store.LoadBiomes();
        Assert.Null(biomesError);
        Assert.Equal("Desert", reloadedBiomes.TerritoryToBiome[146]);
    }

    [Fact]
    public void BrokenProfileFile_IsSetAside_AndOthersStillLoad()
    {
        using var dir = new TempDir();
        var store = new JsonStore(dir.Path);
        var good = new Profile { Name = "Good" };
        store.SaveProfile(good);
        var brokenPath = Path.Combine(store.ProfilesDirectory, "broken.json");
        File.WriteAllText(brokenPath, "{ this is not json");
        var wrongTypePath = Path.Combine(store.ProfilesDirectory, "wrong.json");
        File.WriteAllText(wrongTypePath, """{ "version": 1, "name": "x", "passGroups": "not-a-list" }""");

        var (profiles, errors) = store.LoadProfiles();

        Assert.Single(profiles);
        Assert.Equal("Good", profiles[0].Name);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.False(e.IsWarning));
        Assert.False(File.Exists(brokenPath));
        Assert.False(File.Exists(wrongTypePath));
        Assert.Equal(2, Directory.GetFiles(store.ProfilesDirectory, "*.broken-*").Length);

        // Set-aside files are ignored on the next load.
        var (again, errorsAgain) = store.LoadProfiles();
        Assert.Single(again);
        Assert.Empty(errorsAgain);
    }

    [Fact]
    public void NewerFileVersion_LoadsBestEffort_WithWarning()
    {
        using var dir = new TempDir();
        var store = new JsonStore(dir.Path);
        Directory.CreateDirectory(store.ProfilesDirectory);
        var id = Guid.NewGuid();
        File.WriteAllText(store.ProfilePath(id), $$"""{ "version": 99, "id": "{{id}}", "name": "Future", "passGroups": [] }""");

        var (profiles, errors) = store.LoadProfiles();

        Assert.Single(profiles);
        var warning = Assert.Single(errors);
        Assert.True(warning.IsWarning);
        Assert.Contains("newer", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Backups_RotateAndKeepN_OneFolderPerBurst()
    {
        using var dir = new TempDir();
        var now = new DateTime(2026, 10, 15, 12, 0, 0);
        var store = new JsonStore(dir.Path, () => now) { BackupsToKeep = 2 };
        var profile = new Profile { Name = "P" };

        store.SaveProfile(profile);                       // first write: nothing to back up
        Assert.Empty(store.ListBackups());

        for (var i = 0; i < 5; i++)
        {
            now = now.AddMinutes(5);
            profile.Name = $"P{i}";
            store.SaveProfile(profile);
        }

        var backups = store.ListBackups();
        Assert.Equal(2, backups.Count);
        Assert.Contains("20261015-122500", backups[0]);
        Assert.Contains("20261015-122000", backups[1]);
        var backedUp = Path.Combine(backups[0], "profiles", profile.Id.ToString("D") + ".json");
        Assert.True(File.Exists(backedUp));
        Assert.Contains("\"name\": \"P3\"", File.ReadAllText(backedUp));

        // Two saves within the burst window share one folder and keep the first (oldest) copy.
        now = now.AddMinutes(5);
        profile.Name = "Burst1";
        store.SaveProfile(profile);
        now = now.AddSeconds(10);
        profile.Name = "Burst2";
        store.SaveProfile(profile);
        Assert.Equal(2, store.ListBackups().Count);
        Assert.Contains("\"name\": \"P4\"", File.ReadAllText(Path.Combine(store.ListBackups()[0], "profiles", profile.Id.ToString("D") + ".json")));
    }

    [Fact]
    public void DeleteProfile_RemovesFile_AndBacksItUp()
    {
        using var dir = new TempDir();
        var store = new JsonStore(dir.Path);
        var profile = new Profile { Name = "Gone" };
        store.SaveProfile(profile);

        store.DeleteProfile(profile.Id);

        Assert.False(File.Exists(store.ProfilePath(profile.Id)));
        var backup = Assert.Single(store.ListBackups());
        Assert.True(File.Exists(Path.Combine(backup, "profiles", profile.Id.ToString("D") + ".json")));
        store.DeleteProfile(Guid.NewGuid()); // missing: no throw
    }

    [Fact]
    public void Migrations_RunStepByStep_AndStampVersion()
    {
        var steps = new List<MigrationStep>
        {
            new(FileKind.Profile, 1, o => o["renamed"] = o["old"]),
            new(FileKind.Profile, 2, o => o["added"] = true),
            new(FileKind.Config, 1, o => throw new InvalidOperationException("wrong kind")),
        };
        var file = JObject.Parse("""{ "version": 1, "old": "value" }""");

        var result = Migrations.Upgrade(file, FileKind.Profile, 3, steps);

        Assert.Equal(1, result.FromVersion);
        Assert.Equal(3, result.ToVersion);
        Assert.True(result.Migrated);
        Assert.False(result.IsNewerThanSupported);
        Assert.Equal(3, file["version"]!.Value<int>());
        Assert.Equal("value", file["renamed"]!.Value<string>());
        Assert.True(file["added"]!.Value<bool>());

        var missingVersion = JObject.Parse("""{ "name": "x" }""");
        Assert.Equal(1, Migrations.ReadVersion(missingVersion));
        var current = Migrations.Upgrade(missingVersion, FileKind.Profile);
        Assert.False(current.Migrated);
        Assert.Equal(Profile.CurrentVersion, missingVersion["version"]!.Value<int>());

        var newer = JObject.Parse("""{ "version": 7 }""");
        Assert.True(Migrations.Upgrade(newer, FileKind.Profile, 3, steps).IsNewerThanSupported);
        Assert.Equal(7, newer["version"]!.Value<int>());

        Assert.Throws<InvalidOperationException>(() => Migrations.Upgrade(JObject.Parse("""{ "version": 1 }"""), FileKind.Biomes, 2, steps));
    }
}
