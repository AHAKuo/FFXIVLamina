using System.Numerics;

namespace Lamina.Decals.Bake;

/// <summary>
/// Texture-space footprint of one triangle. UVs become texel coordinates as x = u * width, y = v * height (UV (0,0) is the
/// top-left texel and v grows downward, the way the game samples .tex files). UVs outside 0..1 wrap: the triangle is shifted
/// by whole repeats so its centroid lies in 0..1, and whatever still falls outside the texture is clipped (a triangle
/// straddling the wrap boundary is not wrapped around). A texel counts as covered when its centre is inside the triangle or
/// within <see cref="DefaultEdgeTolerance"/> texels of one of its edges, so neighbouring triangles overlap by a sliver
/// instead of leaving unpainted seams; the barycentrics of such a texel are clamped back onto the triangle.
/// </summary>
public readonly struct UvRaster
{
    /// <summary> Distance outside an edge, in texels, within which a texel centre still counts as covered. </summary>
    public const float DefaultEdgeTolerance = 0.75f;

    /// <summary> UV extent, in texture repeats, above which a triangle is treated as garbage and skipped. </summary>
    private const float MaxExtent = 2f;

    // Texel-space vertices relative to (MinX, MinY) so the edge functions stay small and precise on large textures.
    private readonly Vector2 _a;
    private readonly Vector2 _b;
    private readonly Vector2 _c;
    private readonly float   _invArea2;
    private readonly float   _sign;
    private readonly float   _thresholdA;
    private readonly float   _thresholdB;
    private readonly float   _thresholdC;

    private UvRaster(Vector2 a, Vector2 b, Vector2 c, float area2, float tolerance, int minX, int minY, int maxX, int maxY)
    {
        _a          = a;
        _b          = b;
        _c          = c;
        _invArea2   = 1f / area2;
        _sign       = area2 > 0f ? 1f : -1f;
        _thresholdA = -tolerance * (c - b).Length();
        _thresholdB = -tolerance * (a - c).Length();
        _thresholdC = -tolerance * (b - a).Length();
        MinX        = minX;
        MinY        = minY;
        MaxX        = maxX;
        MaxY        = maxY;
    }

    /// <summary> First texel column to visit, inclusive. </summary>
    public int MinX { get; }

    /// <summary> First texel row to visit, inclusive. </summary>
    public int MinY { get; }

    /// <summary> Last texel column to visit, inclusive. </summary>
    public int MaxX { get; }

    /// <summary> Last texel row to visit, inclusive. </summary>
    public int MaxY { get; }

    /// <summary> True when no texel can be covered (zero texture area, non-finite or garbage UVs). </summary>
    public bool IsEmpty
        => MinX > MaxX || MinY > MaxY;

    private static UvRaster Empty
        => new(Vector2.Zero, Vector2.UnitX, Vector2.UnitY, 1f, 0f, 0, 0, -1, -1);

    /// <summary> Prepares the footprint of a triangle on a width x height texture. </summary>
    public static UvRaster ForTriangle(Vector2 uvA, Vector2 uvB, Vector2 uvC, int width, int height, float edgeTolerance = DefaultEdgeTolerance)
    {
        var centroid = (uvA + uvB + uvC) / 3f;
        if (!float.IsFinite(centroid.X) || !float.IsFinite(centroid.Y))
            return Empty;

        var shift = new Vector2(MathF.Floor(centroid.X), MathF.Floor(centroid.Y));
        var scale = new Vector2(width, height);
        var a     = (uvA - shift) * scale;
        var b     = (uvB - shift) * scale;
        var c     = (uvC - shift) * scale;

        var minU = MathF.Min(a.X, MathF.Min(b.X, c.X));
        var maxU = MathF.Max(a.X, MathF.Max(b.X, c.X));
        var minV = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
        var maxV = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        if (maxU - minU > MaxExtent * width || maxV - minV > MaxExtent * height)
            return Empty;

        var area2 = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area2) < 1e-9f)
            return Empty;

        // One texel of slack around the vertices so the edge tolerance has room; clamped to the texture.
        var minX   = (int)MathF.Max(0f, MathF.Floor(minU) - 1f);
        var minY   = (int)MathF.Max(0f, MathF.Floor(minV) - 1f);
        var maxX   = (int)MathF.Min(width - 1, MathF.Ceiling(maxU) + 1f);
        var maxY   = (int)MathF.Min(height - 1, MathF.Ceiling(maxV) + 1f);
        var origin = new Vector2(minX, minY);
        return new UvRaster(a - origin, b - origin, c - origin, area2, edgeTolerance, minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Barycentric weights (for the triangle's first, second and third vertex) of the texel's centre when the texel is
    /// covered. Texels accepted through the edge tolerance get weights clamped onto the triangle.
    /// </summary>
    public bool TryBarycentric(int x, int y, out Vector3 barycentric)
    {
        var px = x - MinX + 0.5f;
        var py = y - MinY + 0.5f;
        var eA = (_c.X - _b.X) * (py - _b.Y) - (_c.Y - _b.Y) * (px - _b.X);
        var eB = (_a.X - _c.X) * (py - _c.Y) - (_a.Y - _c.Y) * (px - _c.X);
        var eC = (_b.X - _a.X) * (py - _a.Y) - (_b.Y - _a.Y) * (px - _a.X);
        if (eA * _sign < _thresholdA || eB * _sign < _thresholdB || eC * _sign < _thresholdC)
        {
            barycentric = default;
            return false;
        }

        var wA  = MathF.Max(eA * _invArea2, 0f);
        var wB  = MathF.Max(eB * _invArea2, 0f);
        var wC  = MathF.Max(eC * _invArea2, 0f);
        var sum = wA + wB + wC;
        barycentric = sum > 0f ? new Vector3(wA, wB, wC) / sum : new Vector3(1f / 3f);
        return true;
    }
}
