using System.Numerics;
using Lamina.Decals;

namespace Lamina.Tests.Decals;

/// <summary> Synthetic skin meshes and images for the bake tests. Metres; UV (0,0) is the top-left texel of the texture. </summary>
public static class MeshFixtures
{
    /// <summary>
    /// A 1 x 1 m quad in the XY plane centred on the origin at the given z, normal +Z (or -Z when back facing), two triangles
    /// sharing the diagonal from (-0.5, -0.5) to (0.5, 0.5). UVs cover the whole texture upright (u = x + 0.5, v = 0.5 - y), so
    /// texel (x, y) of a W x H target shows the point (x / W - 0.5, 0.5 - y / H). <paramref name="uvOffset"/> shifts every UV
    /// by whole repeats to exercise wrapping.
    /// </summary>
    public static SkinMesh Quad(float z = 0f, bool backFacing = false, Vector2 uvOffset = default)
    {
        var normal = backFacing ? -Vector3.UnitZ : Vector3.UnitZ;
        return Custom(QuadPositions(0f, z), new[] { normal, normal, normal, normal }, QuadUvs(uvOffset), new[] { 0, 1, 2, 0, 2, 3 });
    }

    /// <summary>
    /// Two quads 2 m apart along X, both mapped onto the whole texture: the left and right halves of a mirrored body.
    /// <paramref name="secondUvOffset"/> shifts the second quad's UVs by whole repeats.
    /// </summary>
    public static SkinMesh MirroredQuads(Vector2 secondUvOffset = default)
    {
        var positions = QuadPositions(-1f, 0f).Concat(QuadPositions(1f, 0f)).ToArray();
        var normals   = Enumerable.Repeat(Vector3.UnitZ, 8).ToArray();
        var uvs       = QuadUvs(default).Concat(QuadUvs(secondUvOffset)).ToArray();
        return Custom(positions, normals, uvs, new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 });
    }

    /// <summary> The plain quad with its two triangles listed twice, as merged LODs or materials produce: same geometry, not a mirror. </summary>
    public static SkinMesh DuplicatedQuad()
    {
        var quad = Quad();
        return Custom(quad.Positions, quad.Normals, quad.Uvs, quad.Indices.Concat(quad.Indices).ToArray());
    }

    /// <summary>
    /// Cylinder around the Y axis centred on the origin, unwrapped once around: u runs from 0 at -X through 0.5 at +X back to
    /// 1 at -X (the seam), v = 0 at the top. Normals are the exact radial directions. segments * rings * 2 triangles.
    /// </summary>
    public static SkinMesh Cylinder(int segments, int rings, float radius, float height)
    {
        var columns   = segments + 1;
        var rows      = rings + 1;
        var positions = new Vector3[columns * rows];
        var normals   = new Vector3[columns * rows];
        var uvs       = new Vector2[columns * rows];
        for (var r = 0; r < rows; r++)
        {
            var v = (float)r / rings;
            var y = height * (0.5f - v);
            for (var s = 0; s < columns; s++)
            {
                var u          = (float)s / segments;
                var (sin, cos) = MathF.SinCos(u * MathF.Tau - MathF.PI);
                var i          = r * columns + s;
                positions[i] = new Vector3(radius * cos, y, radius * sin);
                normals[i]   = new Vector3(cos, 0f, sin);
                uvs[i]       = new Vector2(u, v);
            }
        }

        var indices = new int[segments * rings * 6];
        var n       = 0;
        for (var r = 0; r < rings; r++)
        {
            for (var s = 0; s < segments; s++)
            {
                var v00 = r * columns + s;
                var v10 = v00 + 1;
                var v01 = v00 + columns;
                var v11 = v01 + 1;
                indices[n++] = v00;
                indices[n++] = v10;
                indices[n++] = v11;
                indices[n++] = v00;
                indices[n++] = v11;
                indices[n++] = v01;
            }
        }

        return Custom(positions, normals, uvs, indices);
    }

    /// <summary> A body mesh from raw arrays, with dummy skinning data. </summary>
    public static SkinMesh Custom(Vector3[] positions, Vector3[] normals, Vector2[] uvs, int[] indices)
    {
        var weights = new float[positions.Length * 4];
        for (var i = 0; i < positions.Length; i++)
            weights[i * 4] = 1f;
        return new SkinMesh
        {
            Surface          = DecalSurface.Body,
            ModelGamePath    = "chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl",
            MaterialGamePath = "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl",
            DiffuseGamePath  = "chara/human/c0101/obj/body/b0001/texture/--c0101b0001_base.tex",
            Positions        = positions,
            Normals          = normals,
            Uvs              = uvs,
            Indices          = indices,
            BoneIndices      = new byte[positions.Length * 4],
            BoneWeights      = weights,
            BoneNames        = new[] { "j_kosi" },
        };
    }

    /// <summary> Image of one colour. </summary>
    public static RgbaImage Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
        => Generate(width, height, (_, _) => (r, g, b, a));

    /// <summary> Image whose pixels come from a function of (x, y). </summary>
    public static RgbaImage Generate(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var image = new RgbaImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var o            = image.Offset(x, y);
                image.Pixels[o]     = r;
                image.Pixels[o + 1] = g;
                image.Pixels[o + 2] = b;
                image.Pixels[o + 3] = a;
            }
        }

        return image;
    }

    /// <summary> Tuple colour from int literals. </summary>
    public static (byte R, byte G, byte B, byte A) Rgba(byte r, byte g, byte b, byte a = 255)
        => (r, g, b, a);

    /// <summary> RGBA of one texel. </summary>
    public static (byte R, byte G, byte B, byte A) Texel(this RgbaImage image, int x, int y)
    {
        var o = image.Offset(x, y);
        return (image.Pixels[o], image.Pixels[o + 1], image.Pixels[o + 2], image.Pixels[o + 3]);
    }

    /// <summary> Number of texels that differ between two images of the same size. </summary>
    public static int CountDifferent(RgbaImage before, RgbaImage after)
    {
        var count = 0;
        for (var i = 0; i < before.Pixels.Length; i += 4)
        {
            if (before.Pixels[i] != after.Pixels[i] || before.Pixels[i + 1] != after.Pixels[i + 1]
             || before.Pixels[i + 2] != after.Pixels[i + 2] || before.Pixels[i + 3] != after.Pixels[i + 3])
                count++;
        }

        return count;
    }

    private static Vector3[] QuadPositions(float centreX, float z)
        => new[]
        {
            new Vector3(centreX - 0.5f, -0.5f, z),
            new Vector3(centreX + 0.5f, -0.5f, z),
            new Vector3(centreX + 0.5f, 0.5f, z),
            new Vector3(centreX - 0.5f, 0.5f, z),
        };

    private static Vector2[] QuadUvs(Vector2 offset)
        => new[] { new Vector2(0f, 1f) + offset, new Vector2(1f, 1f) + offset, new Vector2(1f, 0f) + offset, new Vector2(0f, 0f) + offset };
}
