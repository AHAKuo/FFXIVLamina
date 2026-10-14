using Vitrine;
using Vitrine.Catalog;
using Vitrine.Designs;
using Xunit;

namespace Lamina.Tests.Vitrine;

public class VitrineDesignStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LaminaTests", "vitrine-designs-" + Guid.NewGuid().ToString("N"));

    public VitrineDesignStoreTests()
        => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch { /* best effort */ }
    }

    private VitrineDesignStore NewStore()
    {
        var store = new VitrineDesignStore(Path.Combine(_dir, "designs.json"), _ => { });
        store.Load();
        return store;
    }

    private static List<ModInformation> Mods()
        => new()
        {
            new ModInformation
            {
                ModDirectory = "OutfitMod", Name = "Moonlit Sundress", Type = VitrineCategory.Gear,
                AvailableSettings = { ["Color"] = (new[] { "Midnight", "Ivory" }, 0) },
            },
            new ModInformation { ModDirectory = "HairMod", Name = "Pretty Hair", Type = VitrineCategory.Hair },
        };

    [Fact]
    public void DeterministicIdIsStableAndCaseInsensitive()
    {
        var a = DeterministicGuid.ForAutoDesign("Moonlit Sundress");
        var b = DeterministicGuid.ForAutoDesign("moonlit sundress");
        var c = DeterministicGuid.ForAutoDesign("Other");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(Guid.Empty, a);
        Assert.Equal(a, DeterministicGuid.ForAutoDesign("Moonlit Sundress"));
    }

    [Fact]
    public void AutoDesignsFollowTheCatalog()
    {
        var store = NewStore();
        store.UpdateFromCatalog(Mods());

        Assert.Equal(2, store.Designs.Count);
        var outfit = store.AutoDesignFor("OutfitMod")!;
        Assert.True(outfit.IsAuto);
        Assert.Equal(DeterministicGuid.ForAutoDesign("OutfitMod"), outfit.Id);
        Assert.Equal(VitrineCategory.Gear, outfit.Category);
        Assert.Single(outfit.Mods);
        Assert.Equal("OutfitMod", outfit.Mods[0].ModDirectory);
        Assert.Null(outfit.Mods[0].Settings);
        Assert.Same(outfit, store.Get(outfit.Id));
    }

    [Fact]
    public void BaselineChangesTheAutoDesignAndPersists()
    {
        var store = NewStore();
        store.UpdateFromCatalog(Mods());
        var changed = 0;
        store.Changed += () => changed++;

        store.SetBaseline("OutfitMod", new Baseline { Priority = 5, Settings = new() { ["Color"] = new() { "Ivory" } } });
        Assert.Equal(1, changed);
        var outfit = store.AutoDesignFor("OutfitMod")!;
        Assert.Equal(5, outfit.Mods[0].Priority);
        Assert.Equal(new[] { "Ivory" }, outfit.Mods[0].Settings!["Color"]);

        var reloaded = NewStore();
        reloaded.UpdateFromCatalog(Mods());
        Assert.Equal(5, reloaded.AutoDesignFor("OutfitMod")!.Mods[0].Priority);
        Assert.Equal(new[] { "Ivory" }, reloaded.AutoDesignFor("OutfitMod")!.Mods[0].Settings!["Color"]);

        reloaded.SetBaseline("OutfitMod", null);
        Assert.Null(reloaded.AutoDesignFor("OutfitMod")!.Mods[0].Settings);
        Assert.Null(reloaded.BaselineFor("OutfitMod"));
    }

    [Fact]
    public void VariantsStartFromBaselineAndRoundTrip()
    {
        var store = NewStore();
        store.UpdateFromCatalog(Mods());
        store.SetBaseline("OutfitMod", new Baseline { Priority = 2, Settings = new() { ["Color"] = new() { "Midnight" } } });

        var id = store.CreateVariant("OutfitMod", "Sleeveless");
        var variant = store.Get(id)!;
        Assert.False(variant.IsAuto);
        Assert.Equal(store.AutoDesignFor("OutfitMod")!.Id, variant.BaseDesignId);
        Assert.Equal("Sleeveless", variant.Name);
        Assert.Equal(2, variant.Mods[0].Priority);
        Assert.Equal(new[] { "Midnight" }, variant.Mods[0].Settings!["Color"]);
        Assert.Equal(VitrineCategory.Gear, variant.Category);
        Assert.Single(store.VariantsFor("OutfitMod"));

        Assert.True(store.UpdateVariant(id, name: "Sleeveless Ivory", enabled: false, priority: 9, settings: new() { ["Color"] = new() { "Ivory" } }));
        var reloaded = NewStore();
        reloaded.UpdateFromCatalog(Mods());
        var again = reloaded.Get(id)!;
        Assert.Equal("Sleeveless Ivory", again.Name);
        Assert.False(again.Mods[0].Enabled);
        Assert.Equal(9, again.Mods[0].Priority);
        Assert.Equal(new[] { "Ivory" }, again.Mods[0].Settings!["Color"]);

        // Auto designs are regenerated, variants come after them in the list.
        Assert.Equal(3, reloaded.Designs.Count);
        Assert.True(reloaded.Designs[0].IsAuto);
        Assert.False(reloaded.Designs[2].IsAuto);

        Assert.True(reloaded.DeleteVariant(id));
        Assert.Null(reloaded.Get(id));
        Assert.False(reloaded.DeleteVariant(id));
    }

    [Fact]
    public void VariantsOfMissingModsAreKeptAndFlagged()
    {
        var store = NewStore();
        store.UpdateFromCatalog(Mods());
        var id = store.CreateVariant("HairMod", "Braided");

        store.UpdateFromCatalog(Mods().Where(m => m.ModDirectory != "HairMod").ToList());
        var variant = store.Get(id);
        Assert.NotNull(variant);
        Assert.True(store.IsOrphan(variant!));
        Assert.False(store.IsOrphan(store.AutoDesignFor("OutfitMod")!));
        Assert.Null(store.AutoDesignFor("HairMod"));
    }

    [Fact]
    public void BrokenDesignsFileIsQuarantined()
    {
        File.WriteAllText(Path.Combine(_dir, "designs.json"), "{{{");
        var store = NewStore();
        Assert.Empty(store.Designs);
        Assert.Single(Directory.GetFiles(_dir, "designs.json.broken-*"));
    }

    [Fact]
    public void SettingsFileRoundTrips()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = new VitrineSettings { CardWidth = 300, ShowImages = false, Sort = ModSort.Category };
        settings.Save(path, _ => { });
        var loaded = VitrineSettings.Load(path, _ => { });
        Assert.Equal(300, loaded.CardWidth);
        Assert.False(loaded.ShowImages);
        Assert.Equal(ModSort.Category, loaded.Sort);
        Assert.Contains("\"category\"", File.ReadAllText(path).ToLowerInvariant());
    }
}
