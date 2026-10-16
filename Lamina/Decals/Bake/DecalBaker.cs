using System.Numerics;
using Lamina.Decals.Bake;

namespace Lamina.Decals;

/// <summary>
/// Texture-space projection of a decal image onto a skin surface. Pure CPU code with no game access.
/// <para>
/// Conventions: UV (0,0) is the top-left texel and v grows downward (row = v * height), as the game samples .tex files. The
/// image's +x maps to the projector tangent and its top row to the bitangent, which at Rotation 0 is world up projected onto
/// the surface; a positive rotation turns the image counter-clockwise as seen from the projector. Each texel is blended as a
/// straight-alpha "over" with alpha = decal alpha * opacity * smoothstep(0, <see cref="FeatherEnd"/>, dot(surface normal,
/// projector normal)); the target alpha is only ever raised. Every texel is painted at most once per bake: the first triangle
/// that maps it into the projector box claims it, so shared edges (and mirrored UV regions) never blend twice.
/// </para>
/// </summary>
public static class DecalBaker
{
    /// <summary> Facing value (dot of the surface normal and the projector normal) at which the feather reaches full strength. </summary>
    public const float FeatherEnd = 0.35f;

    /// <summary>
    /// Projects <paramref name="decal"/> onto <paramref name="mesh"/> at <paramref name="placement"/> and blends the result
    /// into <paramref name="target"/> (the surface's diffuse, modified in place). Texels outside the projector box, on
    /// surfaces facing away from the projector, or outside the image are left untouched.
    /// </summary>
    public static void Bake(RgbaImage target, SkinMesh mesh, RgbaImage decal, DecalPlacement placement, float opacity)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(decal);
        ArgumentNullException.ThrowIfNull(placement);
        ValidateMesh(mesh);

        opacity = Math.Clamp(opacity, 0f, 1f);
        if (!(opacity > 0f) || !(placement.Size > 0f) || mesh.TriangleCount == 0)
            return;

        var projector = Projector.FromPlacement(placement, decal.Width, decal.Height);
        var positions = mesh.Positions;
        var normals   = mesh.Normals;
        var uvs       = mesh.Uvs;
        var indices   = mesh.Indices;
        var width     = target.Width;
        var height    = target.Height;

        // 1. Classify every vertex against the six box faces once. A triangle whose vertices all lie beyond the same face
        //    cannot touch the box, which rejects almost the whole mesh with three loads and two ANDs per triangle.
        var outcodes = new byte[positions.Length];
        for (var i = 0; i < positions.Length; i++)
            outcodes[i] = projector.Outcode(projector.ToLocal(positions[i]));

        // Texels already claimed by this bake, one bit each.
        var claimed = new ulong[(width * height + 63) >> 6];

        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++)
        {
            var i0 = indices[triangle * 3];
            var i1 = indices[triangle * 3 + 1];
            var i2 = indices[triangle * 3 + 2];
            if ((outcodes[i0] & outcodes[i1] & outcodes[i2]) != 0)
                continue;

            // 2. Rasterise the survivor in texture space.
            var raster = UvRaster.ForTriangle(uvs[i0], uvs[i1], uvs[i2], width, height);
            if (raster.IsEmpty)
                continue;

            var p0 = positions[i0];
            var p1 = positions[i1];
            var p2 = positions[i2];
            var n0 = normals[i0];
            var n1 = normals[i1];
            var n2 = normals[i2];

            for (var y = raster.MinY; y <= raster.MaxY; y++)
            {
                var row = y * width;
                for (var x = raster.MinX; x <= raster.MaxX; x++)
                {
                    if (!raster.TryBarycentric(x, y, out var bary))
                        continue;

                    // 3. Where is this texel on the body, and is that inside the projector box?
                    var local = projector.ToLocal(p0 * bary.X + p1 * bary.Y + p2 * bary.Z);
                    if (!projector.Contains(local))
                        continue;

                    var     index = row + x;
                    ref var word  = ref claimed[index >> 6];
                    var     bit   = 1UL << (index & 63);
                    if ((word & bit) != 0)
                        continue;
                    word |= bit;

                    // 4. Facing and feather: fade out as the surface turns away instead of smearing around the far side.
                    var normal   = n0 * bary.X + n1 * bary.Y + n2 * bary.Z;
                    var lengthSq = normal.LengthSquared();
                    if (lengthSq < 1e-12f)
                        continue;
                    var facing = Vector3.Dot(normal, projector.Normal) / MathF.Sqrt(lengthSq);
                    if (facing <= 0f)
                        continue;

                    // 5. Sample and composite.
                    var weight = Blend.SmoothStep(0f, FeatherEnd, facing);
                    var sample = projector.ToImage(local, decal.Width, decal.Height);
                    var colour = Blend.SampleBilinear(decal, sample.X, sample.Y);
                    Blend.Over(target, x, y, colour, colour.W * opacity * weight);
                }
            }
        }
    }

    private static void ValidateMesh(SkinMesh mesh)
    {
        var count = mesh.Positions.Length;
        if (mesh.Normals.Length != count || mesh.Uvs.Length != count)
            throw new ArgumentException("Positions, Normals and Uvs must have one entry per vertex.", nameof(mesh));
        if (mesh.Indices.Length % 3 != 0)
            throw new ArgumentException("Indices must hold three entries per triangle.", nameof(mesh));
        foreach (var index in mesh.Indices)
        {
            if ((uint)index >= (uint)count)
                throw new ArgumentException($"Index {index} is outside the {count} vertices.", nameof(mesh));
        }
    }
}

/// <summary> Detects texture regions shared between mirrored halves of a mesh. </summary>
public static class UvMirror
{
    /// <summary> Tolerance of the point-in-triangle test, texture units (a fifth of a texel at 2048). </summary>
    public const float UvEpsilon = 1e-4f;

    /// <summary> Two vertex positions closer than this (metres) count as the same vertex. </summary>
    public const float PositionEpsilon = 1e-5f;

    /// <summary>
    /// True when a triangle other than <paramref name="triangle"/> (not sharing its vertices by position) also covers
    /// <paramref name="uv"/> in texture space, i.e. the decal would appear in two places. <paramref name="uv"/> is expected
    /// to lie inside <paramref name="triangle"/>; UVs are wrapped into 0..1 first. Triangles with the same three vertex
    /// positions (geometry listed twice) do not count, and a neighbour that only touches the point along a shared edge does
    /// not count either.
    /// </summary>
    public static bool IsMirrored(SkinMesh mesh, int triangle, Vector2 uv)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if ((uint)triangle >= (uint)mesh.TriangleCount)
            throw new ArgumentOutOfRangeException(nameof(triangle));

        var indices   = mesh.Indices;
        var positions = mesh.Positions;
        var uvs       = mesh.Uvs;
        var p0        = positions[indices[triangle * 3]];
        var p1        = positions[indices[triangle * 3 + 1]];
        var p2        = positions[indices[triangle * 3 + 2]];
        var query     = new Vector2(uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y));

        var triangleCount = mesh.TriangleCount;
        for (var other = 0; other < triangleCount; other++)
        {
            if (other == triangle)
                continue;

            var j0 = indices[other * 3];
            var j1 = indices[other * 3 + 1];
            var j2 = indices[other * 3 + 2];
            var a  = uvs[j0];
            var b  = uvs[j1];
            var c  = uvs[j2];

            // Shift the triangle by whole repeats so its centroid lies in 0..1, then test the query and its neighbouring
            // repeats: that covers any triangle narrower than one repeat wherever it sits. Almost every triangle leaves at
            // the bounding box.
            var centroid = (a + b + c) / 3f;
            var shift    = new Vector2(MathF.Floor(centroid.X), MathF.Floor(centroid.Y));
            a -= shift;
            b -= shift;
            c -= shift;

            var minU = MathF.Min(a.X, MathF.Min(b.X, c.X)) - UvEpsilon;
            var maxU = MathF.Max(a.X, MathF.Max(b.X, c.X)) + UvEpsilon;
            var minV = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - UvEpsilon;
            var maxV = MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) + UvEpsilon;
            for (var du = -1; du <= 1; du++)
            {
                var pu = query.X + du;
                if (pu < minU || pu > maxU)
                    continue;
                for (var dv = -1; dv <= 1; dv++)
                {
                    var pv = query.Y + dv;
                    if (pv < minV || pv > maxV)
                        continue;

                    var depth = InsideDistance(a, b, c, new Vector2(pu, pv));
                    if (depth < -UvEpsilon)
                        continue;

                    // The same geometry listed twice is not a mirror. A neighbour sharing vertices that merely touches the
                    // query along the shared edge is not one either; one that contains the point properly is folded over.
                    var shared = SharedVertices(positions[j0], positions[j1], positions[j2], p0, p1, p2);
                    if (shared == 3 || (shared > 0 && depth < UvEpsilon))
                        continue;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary> Smallest signed distance from the point to the triangle's edges, positive inside; -infinity for a degenerate triangle. </summary>
    private static float InsideDistance(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
    {
        var area2 = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area2) < 1e-12f)
            return float.NegativeInfinity;

        var sign = area2 > 0f ? 1f : -1f;
        var dA   = sign * ((c.X - b.X) * (p.Y - b.Y) - (c.Y - b.Y) * (p.X - b.X)) / (c - b).Length();
        var dB   = sign * ((a.X - c.X) * (p.Y - c.Y) - (a.Y - c.Y) * (p.X - c.X)) / (a - c).Length();
        var dC   = sign * ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) / (b - a).Length();
        return MathF.Min(dA, MathF.Min(dB, dC));
    }

    private static int SharedVertices(Vector3 a, Vector3 b, Vector3 c, Vector3 p0, Vector3 p1, Vector3 p2)
        => (Matches(a, p0, p1, p2) ? 1 : 0) + (Matches(b, p0, p1, p2) ? 1 : 0) + (Matches(c, p0, p1, p2) ? 1 : 0);

    private static bool Matches(Vector3 v, Vector3 p0, Vector3 p1, Vector3 p2)
    {
        const float epsilonSq = PositionEpsilon * PositionEpsilon;
        return Vector3.DistanceSquared(v, p0) <= epsilonSq
            || Vector3.DistanceSquared(v, p1) <= epsilonSq
            || Vector3.DistanceSquared(v, p2) <= epsilonSq;
    }
}
