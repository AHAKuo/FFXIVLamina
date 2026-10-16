using System.Diagnostics;
using System.Numerics;
using Lamina.Decals;
using Xunit;
using Xunit.Abstractions;

namespace Lamina.Tests.Decals;

/// <summary> UvMirror.IsMirrored; the query uv lies inside the given triangle in every case, as the picker guarantees. </summary>
public class UvMirrorTests
{
    private readonly ITestOutputHelper _output;

    public UvMirrorTests(ITestOutputHelper output)
        => _output = output;

    [Theory]
    [InlineData(0, 0.75f, 0.75f)] // triangle 0 of the first quad covers u + v >= 1
    [InlineData(1, 0.25f, 0.25f)]
    [InlineData(2, 0.75f, 0.75f)] // the second quad's triangles map onto the same region
    [InlineData(3, 0.1f, 0.6f)]
    [InlineData(0, 0.5f, 0.5f)]   // on the diagonal: the other quad still covers it
    public void MirroredQuads_ShareTheirTextureRegion(int triangle, float u, float v)
        => Assert.True(UvMirror.IsMirrored(MeshFixtures.MirroredQuads(), triangle, new Vector2(u, v)));

    [Theory]
    [InlineData(0, 0.75f, 0.75f)]
    [InlineData(1, 0.25f, 0.25f)]
    [InlineData(0, 0.5f, 0.5f)] // on the shared diagonal: the neighbour touches, it does not mirror
    [InlineData(1, 0.5f, 0.5f)]
    [InlineData(0, 0.999f, 0.999f)]
    public void PlainQuad_IsNotMirrored(int triangle, float u, float v)
        => Assert.False(UvMirror.IsMirrored(MeshFixtures.Quad(), triangle, new Vector2(u, v)));

    [Fact]
    public void Cylinder_IsNotMirrored()
    {
        var mesh = MeshFixtures.Cylinder(32, 4, 0.1f, 0.3f);
        foreach (var triangle in new[] { 0, 1, 17, 100, mesh.TriangleCount - 1 })
            Assert.False(UvMirror.IsMirrored(mesh, triangle, Centroid(mesh, triangle)));
    }

    [Fact]
    public void DuplicatedGeometry_IsNotMirrored()
    {
        var mesh = MeshFixtures.DuplicatedQuad();
        Assert.False(UvMirror.IsMirrored(mesh, 0, new Vector2(0.75f, 0.75f)));
        Assert.False(UvMirror.IsMirrored(mesh, 2, new Vector2(0.75f, 0.75f)));
        Assert.False(UvMirror.IsMirrored(mesh, 1, new Vector2(0.5f, 0.5f)));
    }

    [Fact]
    public void Uvs_AreWrappedBeforeTesting()
    {
        Assert.True(UvMirror.IsMirrored(MeshFixtures.MirroredQuads(), 0, new Vector2(1.75f, -1.25f)));
        Assert.True(UvMirror.IsMirrored(MeshFixtures.MirroredQuads(secondUvOffset: new Vector2(2f, -1f)), 0, new Vector2(0.75f, 0.75f)));
        Assert.False(UvMirror.IsMirrored(MeshFixtures.Quad(uvOffset: new Vector2(3f, 5f)), 0, new Vector2(0.75f, 0.75f)));
    }

    [Fact]
    public void InvalidTriangle_Throws()
    {
        var quad = MeshFixtures.Quad();
        Assert.Throws<ArgumentOutOfRangeException>(() => UvMirror.IsMirrored(quad, -1, Vector2.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => UvMirror.IsMirrored(quad, quad.TriangleCount, Vector2.Zero));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void IsMirrored_ScansALargeMeshQuickly()
    {
        var mesh = MeshFixtures.Cylinder(300, 100, 0.1f, 0.3f);
        Assert.Equal(60_000, mesh.TriangleCount);
        UvMirror.IsMirrored(mesh, 0, Centroid(mesh, 0)); // warm the JIT

        const int calls = 20;
        var       hits  = 0;
        var       watch = Stopwatch.StartNew();
        for (var i = 0; i < calls; i++)
        {
            var triangle = i * 2999 % mesh.TriangleCount;
            if (UvMirror.IsMirrored(mesh, triangle, Centroid(mesh, triangle)))
                hits++;
        }

        watch.Stop();
        _output.WriteLine($"IsMirrored over {mesh.TriangleCount} triangles: {watch.Elapsed.TotalMilliseconds / calls:F2} ms per call");
        Assert.Equal(0, hits);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{calls} calls took {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    private static Vector2 Centroid(SkinMesh mesh, int triangle)
        => (mesh.Uvs[mesh.Indices[triangle * 3]] + mesh.Uvs[mesh.Indices[triangle * 3 + 1]] + mesh.Uvs[mesh.Indices[triangle * 3 + 2]]) / 3f;
}
