using System.Numerics;

namespace Lamina.Decals.Bake;

/// <summary>
/// Orthographic projector frame of one <see cref="DecalPlacement"/>: an oriented box centred on the placement position whose
/// local axes are the image's right (<see cref="Tangent"/>), the image's up (<see cref="Bitangent"/>) and the surface
/// <see cref="Normal"/>. Local coordinates are metres; the box reaches HalfWidth along the tangent, HalfHeight along the
/// bitangent and HalfDepth along the normal, either side of the origin.
/// At Rotation 0 the bitangent is world up (+Y) projected onto the surface, so an image stands upright on the body; when the
/// normal is nearly parallel to world up, +X takes over as the reference axis. A positive rotation turns the image
/// counter-clockwise as seen from the projector (looking along -Normal).
/// </summary>
public readonly struct Projector
{
    /// <summary> |dot(normal, up)| above which the normal counts as parallel to world up. </summary>
    public const float ParallelThreshold = 0.999f;

    private Projector(Vector3 origin, Vector3 tangent, Vector3 bitangent, Vector3 normal, float halfWidth, float halfHeight, float halfDepth)
    {
        Origin     = origin;
        Tangent    = tangent;
        Bitangent  = bitangent;
        Normal     = normal;
        HalfWidth  = halfWidth;
        HalfHeight = halfHeight;
        HalfDepth  = halfDepth;
    }

    /// <summary> Centre of the box: the placement position. </summary>
    public Vector3 Origin { get; }

    /// <summary> Image +x direction, unit length. </summary>
    public Vector3 Tangent { get; }

    /// <summary> Image up direction (row 0 of the image), unit length. </summary>
    public Vector3 Bitangent { get; }

    /// <summary> Unit surface normal; the projector looks along its negative. </summary>
    public Vector3 Normal { get; }

    /// <summary> Half of the placement size, metres. </summary>
    public float HalfWidth { get; }

    /// <summary> HalfWidth scaled by the image's height / width, metres. </summary>
    public float HalfHeight { get; }

    /// <summary> The placement depth, metres, either side of the surface point. </summary>
    public float HalfDepth { get; }

    /// <summary> Builds the frame of a placement for an image of the given pixel size. </summary>
    public static Projector FromPlacement(DecalPlacement placement, int imageWidth, int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image dimensions must be positive.");

        var normal    = placement.Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(placement.Normal) : Vector3.UnitZ;
        var reference = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > ParallelThreshold ? Vector3.UnitX : Vector3.UnitY;
        var tangent   = Vector3.Normalize(Vector3.Cross(reference, normal));
        var bitangent = Vector3.Cross(normal, tangent);

        // Rodrigues' rotation of the tangent around the normal. The tangent is perpendicular to the axis, so only the
        // cos / sin terms remain, and normal x tangent is the bitangent.
        var (sin, cos) = MathF.SinCos(placement.Rotation * (MathF.PI / 180f));
        tangent   = Vector3.Normalize(tangent * cos + bitangent * sin);
        bitangent = Vector3.Cross(normal, tangent);

        var halfWidth  = MathF.Max(placement.Size, 0f) * 0.5f;
        var halfHeight = halfWidth * imageHeight / imageWidth;
        var halfDepth  = MathF.Max(placement.Depth, 0f);
        return new Projector(placement.Position, tangent, bitangent, normal, halfWidth, halfHeight, halfDepth);
    }

    /// <summary> Model-space point to local (right, up, depth) coordinates. </summary>
    public Vector3 ToLocal(Vector3 position)
    {
        var d = position - Origin;
        return new Vector3(Vector3.Dot(d, Tangent), Vector3.Dot(d, Bitangent), Vector3.Dot(d, Normal));
    }

    /// <summary> True when a local point lies inside the box (bounds inclusive). </summary>
    public bool Contains(Vector3 local)
        => MathF.Abs(local.X) <= HalfWidth && MathF.Abs(local.Y) <= HalfHeight && MathF.Abs(local.Z) <= HalfDepth;

    /// <summary>
    /// One bit per box face the local point lies beyond (Cohen-Sutherland style). Three points whose codes share a bit all lie
    /// beyond the same face, so their triangle cannot touch the box.
    /// </summary>
    public byte Outcode(Vector3 local)
    {
        var code = 0;
        if (local.X < -HalfWidth)
            code |= 1;
        else if (local.X > HalfWidth)
            code |= 2;
        if (local.Y < -HalfHeight)
            code |= 4;
        else if (local.Y > HalfHeight)
            code |= 8;
        if (local.Z < -HalfDepth)
            code |= 16;
        else if (local.Z > HalfDepth)
            code |= 32;
        return (byte)code;
    }

    /// <summary>
    /// Image pixel position of a local point: local x from -HalfWidth to HalfWidth maps to 0..width left to right, local y
    /// from -HalfHeight to HalfHeight maps to height..0, so image row 0 (the top) lies toward +Bitangent.
    /// </summary>
    public Vector2 ToImage(Vector3 local, int imageWidth, int imageHeight)
        => new((local.X / HalfWidth + 1f) * 0.5f * imageWidth, (1f - (local.Y / HalfHeight + 1f) * 0.5f) * imageHeight);
}
