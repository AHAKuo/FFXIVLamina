using System.Numerics;
using Lamina.Decals;
using Lamina.Decals.Mesh;
using Xunit;

namespace Lamina.Tests.Decals;

public class MeshMathTests
{
    private const float Eps = 1e-4f;

    private static readonly Vector3 A = new(0, 0, 0);
    private static readonly Vector3 B = new(1, 0, 0);
    private static readonly Vector3 C = new(0, 1, 0);

    private static void AssertVector(Vector3 expected, Vector3 actual, float eps = Eps)
        => Assert.True(Vector3.Distance(expected, actual) < eps, $"expected {expected}, got {actual}");

    // ---- triangles ----

    [Fact]
    public void Interpolate_WeighsCorners()
    {
        var bary = new Vector3(0.5f, 0.25f, 0.25f);
        AssertVector(new Vector3(0.25f, 0.25f, 0), MeshMath.Interpolate(A, B, C, bary));
        var uv = MeshMath.Interpolate(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), bary);
        Assert.Equal(0.25f, uv.X, 4);
        Assert.Equal(0.25f, uv.Y, 4);
        AssertVector(new Vector3(0, 0, 1), MeshMath.TriangleNormal(A, B, C));
        AssertVector(Vector3.Zero, MeshMath.TriangleNormal(A, A, B));
        AssertVector(Vector3.Zero, MeshMath.SafeNormalize(Vector3.Zero));
    }

    // ---- skinning data ----

    private static SkinMesh Mesh(Vector3[] positions, int[] indices, byte[] boneIndices, float[] boneWeights, string[] boneNames)
        => new()
        {
            Surface          = DecalSurface.Body,
            ModelGamePath    = "chara/test.mdl",
            MaterialGamePath = "chara/test.mtrl",
            DiffuseGamePath  = "chara/test_d.tex",
            Positions        = positions,
            Normals          = positions.Select(_ => Vector3.UnitY).ToArray(),
            Uvs              = positions.Select(_ => Vector2.Zero).ToArray(),
            Indices          = indices,
            BoneIndices      = boneIndices,
            BoneWeights      = boneWeights,
            BoneNames        = boneNames,
        };

    [Fact]
    public void SelectTopFour_KeepsTheLargestAndNormalises()
    {
        var weights = new float[] { 10, 20, 30, 40, 50, 60, 70, 80 };
        var bones   = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var outW    = new float[4];
        var outB    = new byte[4];
        Assert.True(MeshMath.SelectTopFour(weights, bones, outW, outB));
        Assert.Equal(new byte[] { 7, 6, 5, 4 }, outB);
        Assert.Equal(1f, outW.Sum(), 4);
        Assert.Equal(80f / 260f, outW[0], 4);
        Assert.Equal(50f / 260f, outW[3], 4);

        Assert.False(MeshMath.SelectTopFour(new float[8], bones, outW, outB));

        // Fewer than four influences: the rest are padded with weight 0 / bone 0.
        Assert.True(MeshMath.SelectTopFour(new float[] { 0.25f, 0.75f }, new byte[] { 3, 5 }, outW, outB));
        Assert.Equal(new byte[] { 5, 3, 0, 0 }, outB);
        Assert.Equal(0.75f, outW[0], 4);
        Assert.Equal(0.25f, outW[1], 4);
        Assert.Equal(0f, outW[2]);
    }

    [Fact]
    public void DominantBone_WeighsCornersByBarycentrics()
    {
        var mesh = Mesh([A, B, C], [0, 1, 2],
            [0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
            [1, 0, 0, 0, 1, 0, 0, 0, 0.6f, 0.4f, 0, 0],
            ["j_a", "j_b"]);
        Assert.Equal(1, MeshMath.DominantBone(mesh, 0, new Vector3(1 / 3f, 1 / 3f, 1 / 3f)));
        Assert.Equal(0, MeshMath.DominantBone(mesh, 0, new Vector3(0.9f, 0.05f, 0.05f)));
        Assert.Equal(-1, MeshMath.DominantBone(mesh, 3, Vector3.One));
    }

    // ---- camera ----

    private static (Matrix4x4 View, Matrix4x4 Projection, Vector3 Eye) Camera()
    {
        var eye  = new Vector3(3, 2, 5);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0, 1, 0), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 16f / 9f, 0.1f, 500f);
        return (view, proj, eye);
    }

    [Fact]
    public void Project_Unproject_RoundTrip()
    {
        var (view, projection, eye) = Camera();
        var viewport = new Vector2(1920, 1080);
        var world    = new Vector3(0.3f, 1.2f, -0.4f);

        Assert.True(MeshMath.TryProject(world, viewport, view * projection, out var screen));
        Assert.InRange(screen.X, 0, viewport.X);
        Assert.InRange(screen.Y, 0, viewport.Y);

        Assert.True(MeshMath.TryUnproject(screen, viewport, view, projection, out var ray));
        AssertVector(eye, ray.Origin, 1e-3f);
        Assert.True(ray.DistanceTo(world) < 1e-3f, $"ray misses the point by {ray.DistanceTo(world)}");
        Assert.True(Vector3.Dot(ray.Direction, world - eye) > 0);
        Assert.Equal(1f, ray.Direction.Length(), 4);
    }

    [Fact]
    public void Unproject_WorksWithReversedDepth()
    {
        var (view, projection, eye) = Camera();
        var flip     = new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 1, 1); // z' = 1 - z
        var reversed = projection * flip;
        var viewport = new Vector2(2560, 1440);
        var world    = new Vector3(-0.7f, 0.4f, 1.1f);

        Assert.True(MeshMath.TryProject(world, viewport, view * projection, out var standard));
        Assert.True(MeshMath.TryProject(world, viewport, view * reversed, out var screen));
        Assert.Equal(standard.X, screen.X, 2);
        Assert.Equal(standard.Y, screen.Y, 2);

        Assert.True(MeshMath.TryUnproject(screen, viewport, view, reversed, out var ray));
        AssertVector(eye, ray.Origin, 1e-3f);
        Assert.True(ray.DistanceTo(world) < 1e-3f, $"ray misses the point by {ray.DistanceTo(world)}");
        Assert.True(Vector3.Dot(ray.Direction, world - eye) > 0);
    }

    [Fact]
    public void Project_RejectsPointsBehindTheCamera()
    {
        var (view, projection, eye) = Camera();
        var behind = eye + (eye - new Vector3(0, 1, 0));
        Assert.False(MeshMath.TryProject(behind, new Vector2(100, 100), view * projection, out _));
        Assert.False(MeshMath.TryUnproject(new Vector2(1, 1), Vector2.Zero, view, projection, out _));
    }
}
