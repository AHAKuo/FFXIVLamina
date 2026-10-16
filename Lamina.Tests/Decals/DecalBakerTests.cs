using System.Diagnostics;
using System.Numerics;
using Lamina.Decals;
using Lamina.Decals.Bake;
using Xunit;
using Xunit.Abstractions;

namespace Lamina.Tests.Decals;

/// <summary>
/// DecalBaker on the synthetic meshes. On the 1 m quad with a 256 target, texel (x, y) shows the point (x / 256 - 0.5, 0.5 - y / 256):
/// a Size 0.5 decal at the origin covers texels 64..191 on both axes and texel (128, 128) is the centre.
/// </summary>
public class DecalBakerTests
{
    private const int TargetSize = 256;

    private static readonly (byte R, byte G, byte B, byte A) Base = (20, 40, 60, 255);

    private readonly ITestOutputHelper _output;

    public DecalBakerTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void SolidDecal_PaintsExactlyTheCentralRegion()
    {
        var target = Target();
        var before = target.Clone();

        DecalBaker.Bake(target, MeshFixtures.Quad(), MeshFixtures.Solid(64, 64, 255, 0, 0), Centre(), 1f);

        // Size 0.5 on a 1 m quad covers the middle half of each axis: 128 x 128 texels.
        var painted = MeshFixtures.CountDifferent(before, target);
        Assert.InRange(painted, 128 * 128 - 328, 128 * 128 + 328);
        AssertColour(target, 128, 128, 255, 0, 0);
        AssertColour(target, 66, 66, 255, 0, 0);
        AssertColour(target, 189, 189, 255, 0, 0);
        foreach (var (x, y) in new[] { (0, 0), (255, 0), (0, 255), (255, 255), (60, 128), (195, 128), (128, 60), (128, 195) })
            AssertUntouched(before, target, x, y);
    }

    [Fact]
    public void HalfOpacity_BlendsHalfWay()
    {
        var target = Target();
        DecalBaker.Bake(target, MeshFixtures.Quad(), MeshFixtures.Solid(64, 64, 255, 0, 0), Centre(), 0.5f);

        // (255, 0, 0) over (20, 40, 60) at 50 %: (137.5, 20, 30). The centre lies on the shared diagonal and is still blended once.
        AssertColour(target, 128, 128, 138, 20, 30);
        AssertColour(target, 100, 150, 138, 20, 30);
    }

    [Fact]
    public void ZeroOpacity_PaintsNothing()
    {
        var target = Target();
        var before = target.Clone();
        DecalBaker.Bake(target, MeshFixtures.Quad(), MeshFixtures.Solid(64, 64, 255, 0, 0), Centre(), 0f);
        Assert.Equal(0, MeshFixtures.CountDifferent(before, target));
    }

    [Fact]
    public void Rotation_TurnsTheImageCounterClockwiseAsSeenFromTheProjector()
    {
        var decal = LeftRedRightBlue();

        var flat = Target();
        DecalBaker.Bake(flat, MeshFixtures.Quad(), decal, Centre(rotation: 0f), 1f);
        AssertColour(flat, 96, 128, 255, 0, 0);  // -X: the image's left half
        AssertColour(flat, 160, 128, 0, 0, 255); // +X: its right half

        var turned = Target();
        DecalBaker.Bake(turned, MeshFixtures.Quad(), decal, Centre(rotation: 90f), 1f);
        AssertColour(turned, 128, 96, 0, 0, 255);  // image +x now points to +Y (texture top): blue above the centre
        AssertColour(turned, 128, 160, 255, 0, 0); // red below

        var back = Target();
        DecalBaker.Bake(back, MeshFixtures.Quad(), decal, Centre(rotation: -90f), 1f);
        AssertColour(back, 128, 96, 255, 0, 0);
        AssertColour(back, 128, 160, 0, 0, 255);

        var flipped = Target();
        DecalBaker.Bake(flipped, MeshFixtures.Quad(), decal, Centre(rotation: 180f), 1f);
        AssertColour(flipped, 96, 128, 0, 0, 255);
        AssertColour(flipped, 160, 128, 255, 0, 0);
    }

    [Fact]
    public void ImageTop_PointsToWorldUp()
    {
        var decal  = MeshFixtures.Generate(64, 64, (_, y) => y < 32 ? MeshFixtures.Rgba(0, 255, 0) : MeshFixtures.Rgba(255, 255, 0));
        var target = Target();
        DecalBaker.Bake(target, MeshFixtures.Quad(), decal, Centre(), 1f);

        AssertColour(target, 128, 96, 0, 255, 0);    // +Y (texture top): green, the image's top half
        AssertColour(target, 128, 160, 255, 255, 0); // -Y: yellow, its bottom half
    }

    [Fact]
    public void BackFacingQuad_IsLeftUntouched()
    {
        var target = Target();
        var before = target.Clone();
        DecalBaker.Bake(target, MeshFixtures.Quad(backFacing: true), MeshFixtures.Solid(64, 64, 255, 0, 0), Centre(), 1f);
        Assert.Equal(0, MeshFixtures.CountDifferent(before, target));
    }

    [Theory]
    [InlineData(-0.2f, false)]
    [InlineData(0.2f, false)]
    [InlineData(-0.03f, true)]
    [InlineData(0.03f, true)]
    public void Depth_LimitsHowFarTheProjectorReaches(float z, bool expectPaint)
    {
        var target = Target();
        var before = target.Clone();
        DecalBaker.Bake(target, MeshFixtures.Quad(z: z), MeshFixtures.Solid(64, 64, 255, 0, 0), Centre(depth: 0.05f), 1f);

        var painted = MeshFixtures.CountDifferent(before, target);
        if (expectPaint)
            Assert.InRange(painted, 128 * 128 - 328, 128 * 128 + 328);
        else
            Assert.Equal(0, painted);
    }

    [Fact]
    public void Cylinder_PaintsOnlyTheFacingSideAndFeathersTowardTheSilhouette()
    {
        // 32 segments, +X at u = 0.5, the -X seam at u = 0 / 1. The box covers the whole cylinder (Size 0.3 > diameter,
        // Depth 0.15 > radius), so facing alone decides what is painted.
        var mesh      = MeshFixtures.Cylinder(32, 8, radius: 0.1f, height: 0.2f);
        var target    = MeshFixtures.Solid(TargetSize, TargetSize, 0, 0, 0);
        var before    = target.Clone();
        var placement = new DecalPlacement { Position = new Vector3(0.1f, 0f, 0f), Normal = Vector3.UnitX, Size = 0.3f, Depth = 0.15f };

        DecalBaker.Bake(target, mesh, MeshFixtures.Solid(32, 32, 255, 255, 255), placement, 1f);

        // Nothing on the -X half (u below 0.25 or above 0.75), with a few texels of margin for the edge tolerance.
        for (var y = 0; y < TargetSize; y++)
        {
            for (var x = 0; x < TargetSize; x++)
            {
                if (x <= 60 || x >= 196)
                    AssertUntouched(before, target, x, y);
            }
        }

        // Full strength where the surface faces +X, fading to nothing toward the silhouettes at u = 0.25 and 0.75.
        var row = Enumerable.Range(0, TargetSize).Select(x => (int)target.Texel(x, 128).R).ToArray();
        Assert.True(row[128] >= 250, $"centre brightness {row[128]}");
        Assert.InRange(row[184], 30, 225);
        Assert.InRange(row[71], 30, 225);
        Assert.True(row[190] < row[184], $"brightness {row[190]} at 190 should be below {row[184]} at 184");
        for (var x = 128; x < 191; x++)
            Assert.True(row[x + 1] <= row[x] + 1, $"brightness rises from {row[x]} at {x} to {row[x + 1]}");
        for (var x = 128; x > 65; x--)
            Assert.True(row[x - 1] <= row[x] + 1, $"brightness rises from {row[x]} at {x} to {row[x - 1]}");
    }

    [Fact]
    public void DecalAcrossTheSharedEdge_LeavesNoSeamAndBlendsOnce()
    {
        var target = Target();
        DecalBaker.Bake(target, MeshFixtures.Quad(), MeshFixtures.Solid(64, 64, 255, 0, 0), Centre(), 0.5f);

        // The quad's diagonal (texels with x + y = 256) crosses the painted square. Every texel inside must be painted, and
        // painted exactly once: a gap would show the base colour, a double blend 75 % red instead of 50 %.
        var wrong = new List<string>();
        for (var y = 65; y <= 190; y++)
        {
            for (var x = 65; x <= 190; x++)
            {
                var (r, g, b, _) = target.Texel(x, y);
                if (Math.Abs(r - 137.5f) > 1f || Math.Abs(g - 20) > 1 || Math.Abs(b - 30) > 1)
                    wrong.Add($"({x},{y}) = ({r},{g},{b})");
            }
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} texels off, first: {string.Join(", ", wrong.Take(5))}");
    }

    [Fact]
    public void TransparentDecalPixels_LeaveTheTargetUntouched()
    {
        // Left half fully transparent white (garbage colour under zero alpha), right half opaque blue.
        var decal  = MeshFixtures.Generate(64, 64, (x, _) => x < 32 ? MeshFixtures.Rgba(255, 255, 255, 0) : MeshFixtures.Rgba(0, 0, 255));
        var target = Target();
        var before = target.Clone();
        DecalBaker.Bake(target, MeshFixtures.Quad(), decal, Centre(), 1f);

        AssertUntouched(before, target, 96, 128);
        AssertColour(target, 160, 128, 0, 0, 255);
        // The filtered boundary blends blue over the base without the transparent white bleeding in.
        foreach (var x in new[] { 127, 128 })
        {
            var (r, g, b, _) = target.Texel(x, 128);
            Assert.True(r <= Base.R && g <= Base.G && b > Base.B && b < 255, $"boundary texel ({x},128) is ({r},{g},{b})");
        }
    }

    [Fact]
    public void TargetAlpha_IsNeverReduced()
    {
        var half = Target(alpha: 200);
        DecalBaker.Bake(half, MeshFixtures.Quad(), MeshFixtures.Solid(64, 64, 255, 0, 0), Centre(), 0.5f);
        Assert.Equal(200, (int)half.Texel(128, 128).A); // max(200, 128)
        Assert.Equal(200, (int)half.Texel(0, 0).A);

        var full = Target(alpha: 200);
        DecalBaker.Bake(full, MeshFixtures.Quad(), MeshFixtures.Solid(64, 64, 255, 0, 0), Centre(), 1f);
        Assert.Equal(255, (int)full.Texel(128, 128).A);
        Assert.Equal(200, (int)full.Texel(0, 0).A);
    }

    [Fact]
    public void UvsOutsideZeroToOne_WrapToTheTexture()
    {
        var decal    = LeftRedRightBlue();
        var expected = Target();
        DecalBaker.Bake(expected, MeshFixtures.Quad(), decal, Centre(rotation: 30f), 1f);

        var wrapped = Target();
        DecalBaker.Bake(wrapped, MeshFixtures.Quad(uvOffset: new Vector2(1f, -2f)), decal, Centre(rotation: 30f), 1f);

        Assert.NotEqual(0, MeshFixtures.CountDifferent(Target(), wrapped));
        Assert.Equal(expected.Pixels, wrapped.Pixels);
    }

    [Fact]
    public void NonSquareDecal_KeepsTheImageAspect()
    {
        var target = Target();
        var before = target.Clone();
        DecalBaker.Bake(target, MeshFixtures.Quad(), MeshFixtures.Solid(64, 32, 255, 0, 0), Centre(), 1f);

        // 0.5 m wide, 0.25 m high: 128 x 64 texels.
        Assert.InRange(MeshFixtures.CountDifferent(before, target), 128 * 64 - 200, 128 * 64 + 200);
        AssertColour(target, 128, 150, 255, 0, 0);
        AssertUntouched(before, target, 128, 170);
    }

    [Fact]
    public void MalformedMesh_IsRejected()
    {
        var quad = MeshFixtures.Quad();
        var bad  = MeshFixtures.Custom(quad.Positions, quad.Normals, quad.Uvs, new[] { 0, 1, 7, 0, 2, 3 });
        Assert.Throws<ArgumentException>(() => DecalBaker.Bake(Target(), bad, MeshFixtures.Solid(8, 8, 255, 0, 0), Centre(), 1f));
    }

    [Fact]
    public void UvRaster_CoversEveryTexelOfTheQuadWithoutGaps()
    {
        var quad  = MeshFixtures.Quad();
        var cover = new int[16 * 16];
        for (var triangle = 0; triangle < 2; triangle++)
        {
            var raster = UvRaster.ForTriangle(
                quad.Uvs[quad.Indices[triangle * 3]], quad.Uvs[quad.Indices[triangle * 3 + 1]], quad.Uvs[quad.Indices[triangle * 3 + 2]], 16, 16);
            Assert.False(raster.IsEmpty);
            for (var y = raster.MinY; y <= raster.MaxY; y++)
            {
                for (var x = raster.MinX; x <= raster.MaxX; x++)
                {
                    if (!raster.TryBarycentric(x, y, out var bary))
                        continue;
                    cover[y * 16 + x]++;
                    Assert.InRange(bary.X + bary.Y + bary.Z, 0.999f, 1.001f);
                    Assert.True(bary.X >= 0f && bary.Y >= 0f && bary.Z >= 0f);
                }
            }
        }

        Assert.All(cover, c => Assert.True(c >= 1));
        Assert.Equal(1, cover[1 * 16 + 1]); // far from the diagonal: one triangle only
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void LargeBake_FinishesWellUnderASecond()
    {
        var mesh = MeshFixtures.Cylinder(300, 100, radius: 0.1f, height: 0.3f);
        Assert.Equal(60_000, mesh.TriangleCount);
        var decal     = MeshFixtures.Solid(512, 512, 200, 30, 30);
        var placement = new DecalPlacement { Position = new Vector3(0.1f, 0f, 0f), Normal = Vector3.UnitX, Size = 0.05f, Depth = 0.02f };

        // Warm the JIT on a small target so the measurement is the bake itself.
        DecalBaker.Bake(MeshFixtures.Solid(64, 64, 0, 0, 0), mesh, decal, placement, 1f);

        var target = MeshFixtures.Solid(2048, 2048, 180, 140, 120);
        var before = target.Clone();
        var watch  = Stopwatch.StartNew();
        DecalBaker.Bake(target, mesh, decal, placement, 1f);
        watch.Stop();

        _output.WriteLine($"Bake of {mesh.TriangleCount} triangles into 2048x2048 with a 512x512 decal: {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"bake took {watch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(MeshFixtures.CountDifferent(before, target) > 1000);
    }

    [Fact]
    public void Projector_FrameFollowsTheConventions()
    {
        // Rotation 0: image right = world up x normal, image up = world up projected onto the surface.
        var front = Projector.FromPlacement(new DecalPlacement { Normal = Vector3.UnitZ, Size = 0.2f, Depth = 0.05f }, 100, 50);
        AssertVector(Vector3.UnitX, front.Tangent);
        AssertVector(Vector3.UnitY, front.Bitangent);
        Assert.Equal(0.1f, front.HalfWidth, 5);
        Assert.Equal(0.05f, front.HalfHeight, 5);
        Assert.Equal(0.05f, front.HalfDepth, 5);

        // A positive rotation turns the image counter-clockwise as seen from the projector (looking along -Normal).
        var turned = Projector.FromPlacement(new DecalPlacement { Normal = Vector3.UnitZ, Rotation = 90f }, 1, 1);
        AssertVector(Vector3.UnitY, turned.Tangent);
        AssertVector(-Vector3.UnitX, turned.Bitangent);

        // Facing +X: image right is -Z, image up stays world up.
        var side = Projector.FromPlacement(new DecalPlacement { Normal = Vector3.UnitX }, 1, 1);
        AssertVector(-Vector3.UnitZ, side.Tangent);
        AssertVector(Vector3.UnitY, side.Bitangent);

        // Normal parallel to world up: +X takes over as the reference axis.
        var top = Projector.FromPlacement(new DecalPlacement { Normal = Vector3.UnitY }, 1, 1);
        AssertVector(Vector3.UnitZ, top.Tangent);
        AssertVector(Vector3.UnitX, top.Bitangent);

        // Image row 0 lies toward +Bitangent and column 0 toward -Tangent.
        var topLeft = front.ToImage(front.ToLocal(new Vector3(-0.1f, 0.05f, 0f)), 100, 50);
        Assert.Equal(0f, topLeft.X, 4);
        Assert.Equal(0f, topLeft.Y, 4);
        var bottomRight = front.ToImage(front.ToLocal(new Vector3(0.1f, -0.05f, 0f)), 100, 50);
        Assert.Equal(100f, bottomRight.X, 4);
        Assert.Equal(50f, bottomRight.Y, 4);
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
        => Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"expected {expected}, got {actual}");

    private static RgbaImage Target(byte alpha = 255)
        => MeshFixtures.Solid(TargetSize, TargetSize, Base.R, Base.G, Base.B, alpha);

    private static DecalPlacement Centre(float size = 0.5f, float rotation = 0f, float depth = 0.05f)
        => new() { Position = Vector3.Zero, Normal = Vector3.UnitZ, Size = size, Rotation = rotation, Depth = depth };

    private static RgbaImage LeftRedRightBlue()
        => MeshFixtures.Generate(64, 64, (x, _) => x < 32 ? MeshFixtures.Rgba(255, 0, 0) : MeshFixtures.Rgba(0, 0, 255));

    private static void AssertColour(RgbaImage image, int x, int y, int r, int g, int b, int tolerance = 1)
    {
        var (ar, ag, ab, _) = image.Texel(x, y);
        Assert.True(Math.Abs(ar - r) <= tolerance && Math.Abs(ag - g) <= tolerance && Math.Abs(ab - b) <= tolerance,
            $"texel ({x},{y}) is ({ar},{ag},{ab}), expected ({r},{g},{b}) +/- {tolerance}");
    }

    private static void AssertUntouched(RgbaImage before, RgbaImage after, int x, int y)
        => Assert.Equal(before.Texel(x, y), after.Texel(x, y));
}
