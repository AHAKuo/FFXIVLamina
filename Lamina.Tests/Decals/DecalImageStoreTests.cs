using Lamina.Decals;
using Lamina.Models;
using Lamina.Tests.Persistence;
using Xunit;

namespace Lamina.Tests.Decals;

public class DecalImageStoreTests
{
    private static readonly byte[] PngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

    private static string WriteSource(TempDir dir, string name)
    {
        var path = Path.Combine(dir.Path, name);
        File.WriteAllBytes(path, PngBytes);
        return path;
    }

    [Fact]
    public void Import_CopiesTheFile_UnderItsContentHash_WithTheExtension()
    {
        using var dir = new TempDir();
        var source = WriteSource(dir, "sticker.PNG");
        var store  = new DecalImageStore(Path.Combine(dir.Path, "decals", "images"));

        var name = store.Import(source);

        Assert.Equal(DecalImageStore.ContentName(PngBytes, ".png"), name);
        Assert.EndsWith(".png", name);
        Assert.Equal(64, Path.GetFileNameWithoutExtension(name).Length);
        Assert.Equal(name, Path.GetFileName(name));
        Assert.True(store.Exists(name));
        Assert.Equal(PngBytes, File.ReadAllBytes(store.FullPath(name)));
        Assert.True(File.Exists(source), "the source must be left in place");
    }

    [Fact]
    public void Import_CreatesTheFolder_AndNamesDifferentContentDifferently()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "decals", "images");
        var store  = new DecalImageStore(folder);
        Assert.False(Directory.Exists(folder));

        var a = store.Import(WriteSource(dir, "a.jpg"));
        var other = Path.Combine(dir.Path, "b.JPEG");
        File.WriteAllBytes(other, PngBytes.Concat(new byte[] { 9 }).ToArray());
        var b = store.Import(other);

        Assert.True(Directory.Exists(folder));
        Assert.NotEqual(a, b);
        Assert.EndsWith(".jpg", a);
        Assert.EndsWith(".jpeg", b);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public void Import_SameBytesTwice_ReturnsTheExistingFile()
    {
        using var dir = new TempDir();
        var store = new DecalImageStore(Path.Combine(dir.Path, "images"));

        var first  = store.Import(WriteSource(dir, "one.png"));
        var second = store.Import(WriteSource(dir, "two.png"));
        var sameAsJpg = store.Import(WriteSource(dir, "three.jpg"));

        Assert.Equal(first, second);
        Assert.NotEqual(first, sameAsJpg);
        Assert.Equal(2, Directory.GetFiles(store.ImageDirectory).Length);
    }

    [Fact]
    public void Prune_DeletesUnreferencedImages_OnlyThose()
    {
        using var dir = new TempDir();
        var store = new DecalImageStore(Path.Combine(dir.Path, "images"));
        var kept    = store.Import(WriteSource(dir, "kept.png"));
        var orphan  = Path.Combine(store.ImageDirectory, "orphan.jpg");
        var foreign = Path.Combine(store.ImageDirectory, "notes.txt");
        File.WriteAllBytes(orphan, new byte[] { 1 });
        File.WriteAllText(foreign, "keep me");

        var deleted = store.Prune(new[] { kept.ToUpperInvariant(), "missing.png", "" });

        Assert.Equal(new[] { "orphan.jpg" }, deleted);
        Assert.True(store.Exists(kept));
        Assert.True(File.Exists(foreign));
        Assert.False(File.Exists(orphan));

        Assert.Equal(new[] { kept }, store.Prune(Array.Empty<string>()));
        Assert.Empty(store.Prune(Array.Empty<string>()));
        Assert.Empty(new DecalImageStore(Path.Combine(dir.Path, "nowhere")).Prune(Array.Empty<string>()));
    }

    [Fact]
    public void ReferencedImages_GathersEveryDecalLayer_AcrossProfiles()
    {
        var a = new Profile { Name = "A" };
        var b = new Profile { Name = "B" };
        a.PassGroups.Add(new PassGroup { Passes = { new Pass { Layers = { new DecalLayer { ImageFile = "x.png" }, new DecalLayer { ImageFile = null }, new DecalLayer { ImageFile = " " } } } } });
        b.PassGroups.Add(new PassGroup { Passes = { new Pass { Layers = { new DecalLayer { ImageFile = "X.PNG" } } }, new Pass { Layers = { new DecalLayer { ImageFile = "y.jpg" } } } } });

        var names = DecalImageStore.ReferencedImages(new[] { a, b });

        Assert.Equal(2, names.Count);
        Assert.Contains("x.png", names);
        Assert.Contains("y.jpg", names);
        Assert.Empty(DecalImageStore.ReferencedImages(Array.Empty<Profile>()));
    }

    [Fact]
    public void FullPath_RoundTrips_WithExists_AndRejectsPathLikeNames()
    {
        using var dir = new TempDir();
        var store = new DecalImageStore(dir.Path);
        var name  = store.Import(WriteSource(dir, "c.png"));

        Assert.Equal(Path.Combine(dir.Path, name), store.FullPath(name));
        Assert.True(store.Exists(Path.GetFileName(store.FullPath(name))));

        Assert.Throws<ArgumentException>(() => store.FullPath(string.Empty));
        Assert.Throws<ArgumentException>(() => store.FullPath(Path.Combine("sub", "x.png")));
        Assert.Throws<ArgumentException>(() => store.FullPath(".." + Path.DirectorySeparatorChar + "x.png"));
        Assert.Throws<ArgumentException>(() => store.FullPath(".."));
    }

    [Fact]
    public void Exists_IsFalse_ForUnknownNullOrPathLikeNames()
    {
        using var dir = new TempDir();
        var store = new DecalImageStore(dir.Path);
        WriteSource(dir, "loose.png");

        Assert.False(store.Exists(null));
        Assert.False(store.Exists(string.Empty));
        Assert.False(store.Exists("missing.png"));
        Assert.False(store.Exists(Path.Combine("sub", "loose.png")));
        Assert.True(store.Exists("loose.png"));
    }

    [Fact]
    public void Import_RejectsMissingFiles_AndUnsupportedTypes()
    {
        using var dir = new TempDir();
        var store = new DecalImageStore(Path.Combine(dir.Path, "images"));

        Assert.Throws<FileNotFoundException>(() => store.Import(Path.Combine(dir.Path, "nope.png")));

        var text = Path.Combine(dir.Path, "notes.txt");
        File.WriteAllText(text, "hi");
        Assert.Throws<ArgumentException>(() => store.Import(text));
        Assert.False(Directory.Exists(store.ImageDirectory));
    }

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("A.PNG", true)]
    [InlineData("b.jpg", true)]
    [InlineData("c.Jpeg", true)]
    [InlineData("d.gif", false)]
    [InlineData("e.dds", false)]
    [InlineData("noext", false)]
    public void IsSupported_ChecksTheExtension_IgnoringCase(string path, bool expected)
        => Assert.Equal(expected, DecalImageStore.IsSupported(path));

    [Fact]
    public void Constructor_RejectsAnEmptyDirectory()
        => Assert.Throws<ArgumentException>(() => new DecalImageStore(" "));
}
