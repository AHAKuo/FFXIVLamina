using System.Numerics;

namespace Lamina.Decals.Mesh;

/// <summary>
/// Pure geometry for the mesh part: barycentric interpolation, influence reduction, the dominant bone of a hit and the
/// screen / world projection the camera fallback uses. Posing and ray casting live in XivLiveMesh (LiveMeshPoser,
/// LiveMeshPicker). Nothing in here touches game memory, so all of it is covered by unit tests (MeshMathTests).
/// Conventions: System.Numerics row-vector matrices (<c>p' = p * M</c>, translation in row 4), the game's D3D style view /
/// projection matrices, screen positions in pixels with the origin in the top-left corner.
/// </summary>
public static class MeshMath
{
    /// <summary> A ray. <see cref="Direction"/> need not be unit length; <c>t</c> values scale with it. </summary>
    public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
    {
        /// <summary> The point at parameter <paramref name="t"/>. </summary>
        public Vector3 At(float t)
            => Origin + Direction * t;

        /// <summary> Shortest distance from <paramref name="point"/> to the infinite line through the ray. </summary>
        public float DistanceTo(Vector3 point)
        {
            var length = Direction.Length();
            if (length <= 0)
                return Vector3.Distance(Origin, point);

            return Vector3.Cross(point - Origin, Direction).Length() / length;
        }
    }

    // ---- triangles ----

    /// <summary>
    /// Barycentric interpolation of three vertex attributes. Component n of <paramref name="barycentric"/> weighs the vertex at
    /// <c>Indices[3 * triangle + n]</c>, the convention of <see cref="DecalPick.Barycentric"/>.
    /// </summary>
    public static Vector3 Interpolate(in Vector3 a, in Vector3 b, in Vector3 c, in Vector3 barycentric)
        => a * barycentric.X + b * barycentric.Y + c * barycentric.Z;

    /// <summary> Barycentric interpolation of three vertex attributes. </summary>
    public static Vector2 Interpolate(in Vector2 a, in Vector2 b, in Vector2 c, in Vector3 barycentric)
        => a * barycentric.X + b * barycentric.Y + c * barycentric.Z;

    /// <summary> Unit normal of a triangle from its winding (counter-clockwise seen from the front). Zero for a degenerate triangle. </summary>
    public static Vector3 TriangleNormal(in Vector3 a, in Vector3 b, in Vector3 c)
        => SafeNormalize(Vector3.Cross(b - a, c - a));

    /// <summary> Normalises, or returns zero for a (near) zero vector instead of NaN. </summary>
    public static Vector3 SafeNormalize(Vector3 v)
    {
        var lengthSquared = v.LengthSquared();
        return lengthSquared > 1e-20f ? v / MathF.Sqrt(lengthSquared) : Vector3.Zero;
    }

    // ---- skinning data ----

    /// <summary>
    /// Keeps the four largest of up to eight (weight, bone) pairs and renormalises them to sum 1; unused output slots get
    /// weight 0 and bone 0. False when no weight is positive (the caller decides what unskinned means).
    /// </summary>
    public static bool SelectTopFour(ReadOnlySpan<float> weights, ReadOnlySpan<byte> bones, Span<float> outWeights, Span<byte> outBones)
    {
        var        count = Math.Min(Math.Min(weights.Length, bones.Length), 8);
        Span<bool> used  = stackalloc bool[8];
        var        total = 0f;
        for (var slot = 0; slot < 4; slot++)
        {
            var best  = -1;
            var bestW = 0f;
            for (var k = 0; k < count; k++)
            {
                if (!used[k] && weights[k] > bestW)
                {
                    bestW = weights[k];
                    best  = k;
                }
            }

            if (best < 0)
            {
                outWeights[slot] = 0;
                outBones[slot]   = 0;
                continue;
            }

            used[best]       = true;
            outWeights[slot] = bestW;
            outBones[slot]   = bones[best];
            total            += bestW;
        }

        if (total <= 0)
            return false;

        for (var slot = 0; slot < 4; slot++)
            outWeights[slot] /= total;
        return true;
    }

    /// <summary>
    /// Bone (index into <see cref="SkinMesh.BoneNames"/>) with the largest barycentric-weighted skin weight over the hit
    /// triangle, or -1.
    /// </summary>
    public static int DominantBone(SkinMesh mesh, int triangle, in Vector3 barycentric)
    {
        if (triangle < 0 || 3 * triangle + 2 >= mesh.Indices.Length)
            return -1;

        var totals = new Dictionary<byte, float>();
        for (var corner = 0; corner < 3; corner++)
        {
            var vertex = mesh.Indices[3 * triangle + corner];
            if ((uint)vertex >= (uint)mesh.VertexCount)
                continue;

            var factor = corner switch
            {
                0 => barycentric.X,
                1 => barycentric.Y,
                _ => barycentric.Z,
            };
            for (var k = 0; k < 4; k++)
            {
                var w = mesh.BoneWeights[4 * vertex + k] * factor;
                if (w <= 0)
                    continue;

                var bone = mesh.BoneIndices[4 * vertex + k];
                totals[bone] = totals.GetValueOrDefault(bone) + w;
            }
        }

        var bestBone = -1;
        var best     = 0f;
        foreach (var (bone, weight) in totals)
        {
            if (weight > best)
            {
                best     = weight;
                bestBone = bone;
            }
        }

        return bestBone;
    }

    // ---- camera ----

    /// <summary>
    /// Projects a world point to pixels the way the game (and IGameGui.WorldToScreen) does: NDC x/y from the view-projection
    /// matrix, y flipped, origin top-left. False when the point is behind the camera.
    /// </summary>
    public static bool TryProject(Vector3 world, Vector2 viewport, in Matrix4x4 viewProjection, out Vector2 screen)
    {
        screen = default;
        var h = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (h.W <= 1e-9f)
            return false;

        screen = new Vector2((h.X / h.W + 1f) * 0.5f * viewport.X, (1f - h.Y / h.W) * 0.5f * viewport.Y);
        return true;
    }

    /// <summary>
    /// Builds the world-space ray through a pixel. Two interior depths are unprojected and the ray is oriented away from the
    /// eye, so it does not matter whether the projection uses standard or reversed Z, or an infinite far plane. The origin
    /// is the eye (the translation of the inverse view matrix); the direction is unit length.
    /// </summary>
    public static bool TryUnproject(Vector2 screen, Vector2 viewport, in Matrix4x4 view, in Matrix4x4 projection, out Ray ray)
    {
        ray = default;
        if (viewport.X <= 0 || viewport.Y <= 0)
            return false;

        if (!Matrix4x4.Invert(view * projection, out var inverse) || !Matrix4x4.Invert(view, out var inverseView))
            return false;

        var ndc = new Vector2(screen.X / viewport.X * 2f - 1f, 1f - screen.Y / viewport.Y * 2f);
        if (!UnprojectDepth(ndc, 0.05f, inverse, out var p0) || !UnprojectDepth(ndc, 0.95f, inverse, out var p1))
            return false;

        var eye       = inverseView.Translation;
        var direction = p1 - p0;
        if (direction.LengthSquared() < 1e-18f)
            return false;

        direction = Vector3.Normalize(direction);
        if (Vector3.Dot(direction, p0 - eye) < 0)
            direction = -direction;

        ray = new Ray(eye, direction);
        return true;
    }

    private static bool UnprojectDepth(Vector2 ndc, float depth, in Matrix4x4 inverseViewProjection, out Vector3 world)
    {
        world = default;
        var h = Vector4.Transform(new Vector4(ndc.X, ndc.Y, depth, 1f), inverseViewProjection);
        if (MathF.Abs(h.W) < 1e-12f)
            return false;

        world = new Vector3(h.X, h.Y, h.Z) / h.W;
        return true;
    }
}
