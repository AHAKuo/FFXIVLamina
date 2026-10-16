using Lamina.Decals;
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
    public void Import_CopiesTheFile_UnderAGuidName_WithTheOriginalExtension()
    {
        using var dir = new TempDir();
        var source = WriteSource(dir, "sticker.png");
        var store  = new DecalImageStore(Path.Combine(dir.Path, "decals", "images"));

        var name = store.Import(source);

        Assert.EndsWith(".png", name);
        Assert.True(Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _), $"'{name}' is not <guid><ext>");
        Assert.Equal(name, Path.GetFileName(name));
        Assert.True(store.Exists(name));
        Assert.Equal(PngBytes, File.ReadAllBytes(store.FullPath(name)));
        Assert.True(File.Exists(source), "the source must be left in place");
    }

    [Fact]
    public void Import_CreatesTheFolder_AndGivesDistinctNames()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "decals", "images");
        var store  = new DecalImageStore(folder);
        Assert.False(Directory.Exists(folder));

        var a = store.Import(WriteSource(dir, "a.jpg"));
        var b = store.Import(WriteSource(dir, "b.JPEG"));

        Assert.True(Directory.Exists(folder));
        Assert.NotEqual(a, b);
        Assert.EndsWith(".jpg", a);
        Assert.EndsWith(".JPEG", b);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
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
