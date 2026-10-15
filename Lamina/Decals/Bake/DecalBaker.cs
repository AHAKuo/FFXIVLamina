using System.Numerics;

namespace Lamina.Decals;

/// <summary>
/// Texture-space projection of a decal image onto a skin surface. Pure CPU code with no game access.
/// STUB: the Bake part replaces the bodies; the signatures are the contract.
/// </summary>
public static class DecalBaker
{
    /// <summary>
    /// Projects <paramref name="decal"/> onto <paramref name="mesh"/> at <paramref name="placement"/> and blends the result
    /// into <paramref name="target"/> (the surface's diffuse, modified in place). Texels outside the projector box, on
    /// surfaces facing away from the projector, or outside the image are left untouched.
    /// </summary>
    public static void Bake(RgbaImage target, SkinMesh mesh, RgbaImage decal, DecalPlacement placement, float opacity)
        => throw new NotImplementedException("DecalBaker.Bake is implemented by the Bake part.");
}

/// <summary> Detects texture regions shared between mirrored halves of a mesh. </summary>
public static class UvMirror
{
    /// <summary>
    /// True when a triangle other than <paramref name="triangle"/> (not sharing its vertices by position) also covers
    /// <paramref name="uv"/> in texture space, i.e. the decal would appear in two places.
    /// </summary>
    public static bool IsMirrored(SkinMesh mesh, int triangle, Vector2 uv)
        => throw new NotImplementedException("UvMirror.IsMirrored is implemented by the Bake part.");
}
