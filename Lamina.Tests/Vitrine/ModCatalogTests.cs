using Vitrine;
using Vitrine.Catalog;
using Vitrine.Penumbra;
using Xunit;

namespace Lamina.Tests.Vitrine;

public class ModCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LaminaTests", "vitrine-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _warnings = new();

    public ModCatalogTests()
        => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch { /* best effort */ }
    }

    private ModCatalog NewCatalog(FakePenumbraReader reader)
        => new(reader, Path.Combine(_dir, "mods.json"), _warnings.Add, _ => { });

    private static FakePenumbraReader TwoMods()
    {
        var reader = new FakePenumbraReader();
        reader.ModList["HairMod"]   = "Pretty Hair";
        reader.ModList["OutfitMod"] = "Moonlit Sundress";
        reader.Metas["HairMod"] = new ModMeta("Pretty Hair", "Selune", "A hair.", "1.0", "https://x", "", new[] { "hair" }, "");
        reader.ChangedItems["HairMod"]   = new() { ["Hyur Midlander Female Hair 5"] = null };
        reader.ChangedItems["OutfitMod"] = new() { ["Thavnairian Bustier"] = null, ["Thavnairian Tights"] = null };
        reader.Settings["OutfitMod"] = new()
        {
            ["Color"]   = (new[] { "Midnight", "Ivory" }, 0),
            ["Options"] = (new[] { "Sleeves", "Ribbon" }, 1),
        };
        return reader;
    }

    [Fact]
    public void FullScanProcessesModsIncrementallyAndClassifies()
    {
        var reader  = TwoMods();
        var catalog = NewCatalog(reader);
        catalog.ModsPerTick = 1;

        catalog.BeginFullScan();
        Assert.True(catalog.IsScanning);
        Assert.Equal((0, 2), catalog.ScanProgress);

        catalog.Tick();
        Assert.True(catalog.IsScanning);
        Assert.Single(catalog.Mods);

        catalog.Tick();
        Assert.False(catalog.IsScanning);
        Assert.Equal(2, catalog.Mods.Count);

        var hair = catalog.Get("HairMod")!;
        Assert.Equal("Pretty Hair", hair.Name);
        Assert.Equal("Selune", hair.Author);
        Assert.Equal(VitrineCategory.Hair, hair.Type);
        Assert.Single(hair.AffectedItems);
        Assert.Equal(new[] { "hair" }, hair.Tags);

        var outfit = catalog.Get("OutfitMod")!;
        Assert.Equal(VitrineCategory.Gear, outfit.Type);
        Assert.Equal("Moonlit Sundress", outfit.Name); // falls back to Penumbra's list name without meta
        Assert.Equal(2, outfit.AvailableSettings.Count);
        Assert.Equal(0, outfit.AvailableSettings["Color"].GroupType);

        // Sorted by name.
        Assert.Equal(new[] { "Moonlit Sundress", "Pretty Hair" }, catalog.Mods.Select(m => m.Name).ToArray());
        Assert.True(File.Exists(Path.Combine(_dir, "mods.json")));
    }

    [Fact]
    public void CacheRoundTripRestoresEverything()
    {
        var reader  = TwoMods();
        var catalog = NewCatalog(reader);
        catalog.BeginFullScan();
        catalog.Tick();
        Assert.False(catalog.IsScanning);

        var reloaded = NewCatalog(new FakePenumbraReader { Available = false });
        Assert.True(reloaded.LoadCache());
        Assert.Equal(2, reloaded.Mods.Count);
        var outfit = reloaded.Get("OutfitMod")!;
        Assert.Equal(VitrineCategory.Gear, outfit.Type);
        Assert.Equal(2, outfit.AffectedItems.Count);
        Assert.Equal(new[] { "Midnight", "Ivory" }, outfit.AvailableSettings["Color"].Options);
        Assert.Equal(1, outfit.AvailableSettings["Options"].GroupType);
        Assert.Equal("Selune", reloaded.Get("HairMod")!.Author);
    }

    [Fact]
    public void UnchangedModsAreSkippedWhenStampMatches()
    {
        var reader = TwoMods();
        reader.ModRoot = _dir;
        Directory.CreateDirectory(Path.Combine(_dir, "HairMod"));
        File.WriteAllText(Path.Combine(_dir, "HairMod", "meta.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_dir, "OutfitMod"));
        File.WriteAllText(Path.Combine(_dir, "OutfitMod", "meta.json"), "{}");

        var catalog = NewCatalog(reader);
        catalog.BeginFullScan();
        catalog.Tick();
        Assert.Equal(2, reader.ChangedItemCalls);

        catalog.BeginFullScan();
        catalog.Tick();
        Assert.Equal(2, reader.ChangedItemCalls); // stamps match: no re-query

        catalog.BeginFullScan(force: true);
        catalog.Tick();
        Assert.Equal(4, reader.ChangedItemCalls);
    }

    [Fact]
    public void RemovedModsDisappearAndEventsQueueWork()
    {
        var reader  = TwoMods();
        var catalog = NewCatalog(reader);
        catalog.BeginFullScan();
        catalog.Tick();

        var changed = 0;
        catalog.Changed += () => changed++;

        reader.ModList.Remove("HairMod");
        catalog.RemoveMod("HairMod");
        Assert.Null(catalog.Get("HairMod"));
        Assert.Equal(1, changed);

        reader.ModList["NewMod"] = "New Mod";
        reader.ChangedItems["NewMod"] = new() { ["Emote: Sit"] = null };
        catalog.QueueMod("NewMod");
        catalog.Tick();
        Assert.Equal(VitrineCategory.Emote, catalog.Get("NewMod")!.Type);

        reader.ModList.Remove("NewMod");
        reader.ModList["MovedMod"] = "New Mod";
        reader.ChangedItems["MovedMod"] = reader.ChangedItems["NewMod"];
        catalog.MoveMod("NewMod", "MovedMod");
        catalog.Tick();
        Assert.Null(catalog.Get("NewMod"));
        Assert.NotNull(catalog.Get("MovedMod"));
    }

    [Fact]
    public void BrokenCacheIsQuarantined()
    {
        File.WriteAllText(Path.Combine(_dir, "mods.json"), "{ not json");
        var catalog = NewCatalog(new FakePenumbraReader());
        Assert.False(catalog.LoadCache());
        Assert.Single(_warnings);
        Assert.False(File.Exists(Path.Combine(_dir, "mods.json")));
        Assert.Single(Directory.GetFiles(_dir, "mods.json.broken-*"));
    }

    [Fact]
    public void CollectionStatesAreRefreshed()
    {
        var reader  = TwoMods();
        var lamina  = Guid.NewGuid();
        var other   = Guid.NewGuid();
        reader.Collections[lamina] = "Lamina";
        reader.Collections[other]  = "Default";
        reader.PlayerCollection    = (lamina, "Lamina", true);
        reader.CollectionSettings[lamina] = new()
        {
            ["HairMod"] = new ModSettingsInCollection(true, 3, new Dictionary<string, IReadOnlyList<string>>(), false, true),
        };
        reader.CollectionSettings[other] = new()
        {
            ["HairMod"]   = new ModSettingsInCollection(false, 0, new Dictionary<string, IReadOnlyList<string>>(), false, false),
            ["OutfitMod"] = new ModSettingsInCollection(true, 0, new Dictionary<string, IReadOnlyList<string>> { ["Color"] = new[] { "Ivory" } }, false, false),
        };

        var catalog = NewCatalog(reader);
        catalog.BeginFullScan();
        catalog.Tick();
        catalog.RefreshCollectionStates();

        var hair = catalog.Get("HairMod")!;
        Assert.Equal(lamina, hair.CurrentActiveCollection);
        Assert.True(hair.PerCollection[lamina].Enabled);
        Assert.Equal(3, hair.PerCollection[lamina].Priority);
        Assert.False(hair.PerCollection[other].Enabled);

        var outfit = catalog.Get("OutfitMod")!;
        Assert.Null(outfit.CurrentActiveCollection);
        Assert.Equal(new[] { "Ivory" }, outfit.PerCollection[other].Settings["Color"]);
    }

    [Fact]
    public void MetaJsonFallbackIsUsedWhenAdapterHasNothing()
    {
        var reader = TwoMods();
        reader.Metas.Clear();
        reader.ModRoot = _dir;
        Directory.CreateDirectory(Path.Combine(_dir, "OutfitMod"));
        File.WriteAllText(Path.Combine(_dir, "OutfitMod", "meta.json"),
            """{"FileVersion":3,"Name":"Moonlit Sundress","Author":"Selune Atelier","Description":"Flowing.","Version":"2.1","Website":"https://example.org","ModTags":["outfit","dress"],"Image":"preview.png"}""");

        var catalog = NewCatalog(reader);
        catalog.BeginFullScan();
        catalog.Tick();

        var outfit = catalog.Get("OutfitMod")!;
        Assert.Equal("Selune Atelier", outfit.Author);
        Assert.Equal("2.1", outfit.Version);
        Assert.Equal(new[] { "outfit", "dress" }, outfit.Tags);
        Assert.Equal(Path.Combine(_dir, "OutfitMod", "preview.png"), outfit.ImagePath);
    }
}
