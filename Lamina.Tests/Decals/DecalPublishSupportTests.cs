using System.Numerics;
using Lamina.Decals;
using Lamina.Decals.Publish;
using Xunit;

namespace Lamina.Tests.Decals;

/// <summary> The publisher's pure parts: surface grouping, cache keys, cache housekeeping and readback repacking. </summary>
public class DecalPublishSupportTests
{
    private static DecalSpec Spec(string image, DecalSurface surface, float x = 0f)
        => new(image, new DecalPlacement { Surface = surface, Position = new Vector3(x, 1f, 0f), Normal = Vector3.UnitZ }, 1f);

    private static SkinMesh Mesh(float scale = 1f, string model = "chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl")
        => new()
        {
            Surface          = DecalSurface.Body,
            ModelGamePath    = model,
            MaterialGamePath = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl",
            DiffuseGamePath  = "chara/human/c0201/obj/body/b0001/texture/--c0201b0001_base.tex",
            Positions        = new[] { Vector3.Zero, new Vector3(scale, 0, 0), new Vector3(0, scale, 0) },
            Normals          = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            Uvs              = new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY },
            Indices          = new[] { 0, 1, 2 },
            BoneIndices      = new byte[12],
            BoneWeights      = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f },
            BoneNames        = new[] { "j_sebo_b" },
        };

    /// <summary> A fresh temporary directory, deleted when disposed. </summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LaminaTests", Guid.NewGuid().ToString("N"));

        public TempDir()
            => Directory.CreateDirectory(Path);

        public string File(string name, int bytes, DateTime? written = null)
        {
            var path = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllBytes(path, new byte[bytes]);
            if (written is { } time)
                System.IO.File.SetLastWriteTimeUtc(path, time);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    // ---- grouping ----------------------------------------------------------------------------------

    [Fact]
    public void Group_BySurface_KeepsDesignOrderInsideEachSurface()
    {
        var a = Spec("a.png", DecalSurface.Body);
        var b = Spec("b.png", DecalSurface.Face);
        var c = Spec("c.png", DecalSurface.Body);

        var groups = DecalSurfaceGroups.Group(new[] { b, a, c });

        Assert.Equal(2, groups.Count);
        Assert.Equal(DecalSurface.Body, groups[0].Surface);
        Assert.Equal(new[] { a, c }, groups[0].Decals);
        Assert.Equal(DecalSurface.Face, groups[1].Surface);
        Assert.Equal(new[] { b }, groups[1].Decals);
        Assert.Empty(DecalSurfaceGroups.Group(Array.Empty<DecalSpec>()));
    }

    [Fact]
    public void GroupByMesh_PrefersThePlacementsTexture_ThenTheSurfacesFirstMesh()
    {
        var gen3  = Mesh();                                                     // diffuse --c0201b0001_base.tex
        var bibo  = Mesh(model: "chara/equipment/e6193/model/c0201e6193_top.mdl");
        bibo = new SkinMesh
        {
            Surface = bibo.Surface, ModelGamePath = bibo.ModelGamePath, MaterialGamePath = bibo.MaterialGamePath,
            DiffuseGamePath = "chara/nyaughty/eve/bozo_midlander_base.tex",
            Positions = bibo.Positions, Normals = bibo.Normals, Uvs = bibo.Uvs, Indices = bibo.Indices,
            BoneIndices = bibo.BoneIndices, BoneWeights = bibo.BoneWeights, BoneNames = bibo.BoneNames,
        };

        var onBibo   = Spec("a.png", DecalSurface.Body);
        onBibo.Placement.DiffuseGamePath = "CHARA/nyaughty/eve/bozo_midlander_base.tex";   // case-insensitive
        var onGen3   = Spec("b.png", DecalSurface.Body);
        onGen3.Placement.DiffuseGamePath = gen3.DiffuseGamePath;
        var legacy   = Spec("c.png", DecalSurface.Body);                        // no texture recorded: first Body mesh
        var noMesh   = Spec("d.png", DecalSurface.Body);
        noMesh.Placement.DiffuseGamePath = "chara/human/c0201/obj/body/b0001/texture/gone.tex";   // falls back too
        var face     = Spec("e.png", DecalSurface.Face);

        var unplaceable = new List<DecalSpec>();
        var groups      = DecalSurfaceGroups.GroupByMesh(new[] { onBibo, onGen3, legacy, noMesh, face }, new[] { gen3, bibo }, unplaceable);

        Assert.Equal(2, groups.Count);
        Assert.Same(bibo, groups[0].Mesh);
        Assert.Equal(new[] { onBibo }, groups[0].Decals);
        Assert.Same(gen3, groups[1].Mesh);
        Assert.Equal(new[] { onGen3, legacy, noMesh }, groups[1].Decals);
        Assert.Equal(new[] { face }, unplaceable);
    }

    // ---- cache key ---------------------------------------------------------------------------------

    [Fact]
    public void CacheKey_IsStable_AndSensitiveToEveryInput()
    {
        var decals = new List<(string, string)> { ("img1", Spec("a.png", DecalSurface.Body).ContentKey()), ("img2", Spec("b.png", DecalSurface.Body).ContentKey()) };
        var key    = BakeCacheKey.Compute("game:x", "model.mdl", "mesh1", decals);

        Assert.Equal(16, key.Length);
        Assert.Matches("^[0-9A-F]{16}$", key);
        Assert.Equal(key, BakeCacheKey.Compute("game:x", "model.mdl", "mesh1", decals));
        Assert.Equal(key, BakeCacheKey.Compute("game:x", "MODEL.mdl", "mesh1", decals));

        Assert.NotEqual(key, BakeCacheKey.Compute("file:y", "model.mdl", "mesh1", decals));
        Assert.NotEqual(key, BakeCacheKey.Compute("game:x", "other.mdl", "mesh1", decals));
        Assert.NotEqual(key, BakeCacheKey.Compute("game:x", "model.mdl", "mesh2", decals));
        Assert.NotEqual(key, BakeCacheKey.Compute("game:x", "model.mdl", "mesh1", new[] { decals[1], decals[0] }));
        Assert.NotEqual(key, BakeCacheKey.Compute("game:x", "model.mdl", "mesh1", new[] { ("img9", decals[0].Item2), decals[1] }));
        Assert.NotEqual(key, BakeCacheKey.Compute("game:x", "model.mdl", "mesh1", new[] { (decals[0].Item1, Spec("a.png", DecalSurface.Body, x: 0.5f).ContentKey()), decals[1] }));
        Assert.NotEqual(key, BakeCacheKey.Compute("game:x", "model.mdl", "mesh1", new[] { decals[0] }));
    }

    [Fact]
    public void OutputFile_NamesSurfaceAndKey()
    {
        var file = BakeCacheKey.OutputFile(@"C:\cache", DecalSurface.Face, "0123456789ABCDEF");

        Assert.Equal(@"C:\cache\Face_0123456789ABCDEF.tex", file);
    }

    [Fact]
    public void UpstreamStamp_GamePathIsThePath_FileIncludesWriteTimeAndLength()
    {
        Assert.Equal("game:chara/x/base.tex", BakeCacheKey.UpstreamStamp("chara/X/Base.tex"));

        using var dir  = new TempDir();
        var       file = dir.File("skin.tex", 10, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var       a    = BakeCacheKey.UpstreamStamp(file);
        Assert.StartsWith("file:", a);
        Assert.Contains("|10", a);
        Assert.Equal(a, BakeCacheKey.UpstreamStamp(file));

        dir.File("skin.tex", 12, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(a, BakeCacheKey.UpstreamStamp(file));

        Assert.EndsWith("|missing", BakeCacheKey.UpstreamStamp(Path.Combine(dir.Path, "nope.tex")));
    }

    [Fact]
    public void MeshFingerprint_FollowsGeometry_NotPaths()
    {
        var a = BakeCacheKey.MeshFingerprint(Mesh());

        Assert.Equal(16, a.Length);
        Assert.Equal(a, BakeCacheKey.MeshFingerprint(Mesh()));
        Assert.Equal(a, BakeCacheKey.MeshFingerprint(Mesh(model: "other.mdl")));
        Assert.NotEqual(a, BakeCacheKey.MeshFingerprint(Mesh(scale: 2f)));
    }

    [Fact]
    public void BytesHash_FollowsContent()
    {
        var a = BakeCacheKey.BytesHash(new byte[] { 1, 2, 3 });

        Assert.Equal(16, a.Length);
        Assert.Equal(a, BakeCacheKey.BytesHash(new byte[] { 1, 2, 3 }));
        Assert.NotEqual(a, BakeCacheKey.BytesHash(new byte[] { 1, 2, 4 }));
    }

    // ---- repacking ---------------------------------------------------------------------------------

    [Fact]
    public void Repack_DropsRowPadding_AndIgnoresTrailingBytes()
    {
        // 2x2 RGBA with a 16 byte pitch (row = 8 bytes) and a buffer longer than height * pitch.
        var data = new byte[40];
        for (var i = 0; i < 8; i++)
        {
            data[i]      = (byte)(i + 1);
            data[16 + i] = (byte)(i + 101);
        }
        for (var i = 8; i < 16; i++)
        {
            data[i]      = 0xEE;
            data[16 + i] = 0xEE;
        }

        var image = RawImageRepacker.ToRgba(2, 2, 16, RawImageRepacker.R8G8B8A8Unorm, data);

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 101, 102, 103, 104, 105, 106, 107, 108 }, image.Pixels);
    }

    [Fact]
    public void Repack_SwapsBlueAndRed_ForBgraFormats()
    {
        var data = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 };

        var image = RawImageRepacker.ToRgba(2, 1, 8, RawImageRepacker.B8G8R8A8Unorm, data);

        Assert.Equal(new byte[] { 30, 20, 10, 40, 70, 60, 50, 80 }, image.Pixels);
        Assert.Equal(data, RawImageRepacker.ToRgba(2, 1, 8, RawImageRepacker.R8G8B8A8UnormSrgb, data).Pixels);
    }

    [Fact]
    public void Repack_RejectsUnsupportedFormats_AndShortBuffers()
    {
        var data = new byte[8];

        Assert.Throws<NotSupportedException>(() => RawImageRepacker.ToRgba(2, 1, 8, 98, data));
        Assert.Throws<ArgumentException>(() => RawImageRepacker.ToRgba(2, 1, 4, RawImageRepacker.R8G8B8A8Unorm, data));
        Assert.Throws<ArgumentException>(() => RawImageRepacker.ToRgba(2, 2, 8, RawImageRepacker.R8G8B8A8Unorm, data));
    }

    // ---- cache housekeeping ------------------------------------------------------------------------

    [Fact]
    public void Trim_DeletesTheOldestBeyondTheLimit_NeverProtectedFiles()
    {
        using var dir   = new TempDir();
        var       start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var       files = Enumerable.Range(0, 30).Select(i => dir.File($"Body_{i:D2}.tex", 8, start.AddMinutes(i))).ToList();
        var       other = dir.File("notes.txt", 8, start);
        var       stale = dir.File("Body_old.tex.3.tmp", 8, start);
        var       fresh = dir.File("Body_new.tex.4.tmp", 8, DateTime.UtcNow);

        var deleted = BakeCache.Trim(dir.Path, BakeCache.MaxFiles, new[] { files[0], files[1] });

        // 30 bakes, the two oldest protected: the next six oldest go, 24 remain.
        Assert.Equal(files.Skip(2).Take(6).Select(Path.GetFullPath).Append(Path.GetFullPath(stale)).OrderBy(f => f), deleted.OrderBy(f => f));
        Assert.True(File.Exists(files[0]));
        Assert.True(File.Exists(files[1]));
        Assert.All(files.Skip(8), f => Assert.True(File.Exists(f)));
        Assert.Equal(24, Directory.EnumerateFiles(dir.Path, "*.tex").Count());
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(stale));

        Assert.Empty(BakeCache.Trim(dir.Path, BakeCache.MaxFiles, Array.Empty<string>()));
        Assert.Empty(BakeCache.Trim(Path.Combine(dir.Path, "missing"), BakeCache.MaxFiles, Array.Empty<string>()));
    }

    [Fact]
    public void IsValidOutput_RequiresANonEmptyFile()
    {
        using var dir = new TempDir();

        Assert.False(BakeCache.IsValidOutput(Path.Combine(dir.Path, "missing.tex")));
        Assert.False(BakeCache.IsValidOutput(dir.File("empty.tex", 0)));
        Assert.True(BakeCache.IsValidOutput(dir.File("ok.tex", 1)));
    }

    [Fact]
    public void IsCacheFile_DetectsOnlyFilesInsideTheCacheFolder()
    {
        var cache = @"C:\Lamina\decals\cache";

        Assert.True(BakeCache.IsCacheFile(cache, @"C:\Lamina\decals\cache\Body_0123.tex"));
        Assert.True(BakeCache.IsCacheFile(cache + @"\", @"c:\lamina\DECALS\cache\Face_0123.tex"));
        Assert.False(BakeCache.IsCacheFile(cache, @"C:\Lamina\decals\cache2\Body_0123.tex"));
        Assert.False(BakeCache.IsCacheFile(cache, @"C:\Penumbra\Mods\Skin\body.tex"));
        Assert.False(BakeCache.IsCacheFile(cache, "chara/human/c0201/obj/body/b0001/texture/--c0201b0001_base.tex"));
    }

    [Fact]
    public void TempFile_IsUniquePerRun()
    {
        var output = @"C:\cache\Body_0123.tex";

        Assert.Equal(@"C:\cache\Body_0123.tex.7.tmp", BakeCache.TempFile(output, 7));
        Assert.NotEqual(BakeCache.TempFile(output, 7), BakeCache.TempFile(output, 8));
    }
}
