using System.Numerics;

namespace Lamina.Decals;

// Shared contracts of the decal feature. Four parts build against these and nothing else:
//   Mesh   (Lamina/Decals/Mesh)    - ISkinMeshProvider, IDecalPicker: live skin meshes, pose, mouse ray.
//   Bake   (Lamina/Decals/Bake)    - DecalBaker, UvMirror: pure texture-space projection, no game access.
//   Publish(Lamina/Decals/Publish) - IDecalPublisher: base texture in, baked .tex out, Penumbra temporary mod.
//   UI     (Lamina/UI)             - DecalLayer card and the pick mode.
// Keep this file stable; extend by adding members, never by renaming.

/// <summary> Which skin surface a decal is baked into. Each surface is one skin material with its own diffuse texture. </summary>
public enum DecalSurface
{
    /// <summary> The body skin material (torso, arms, legs, hands, feet share one diffuse). </summary>
    Body = 0,

    /// <summary> The face skin material. </summary>
    Face = 1,
}

/// <summary>
/// Where a decal sits, stored in the character's rest-pose model space so the bake never depends on the current animation
/// and survives a change of body mod reasonably well (rest poses of different bodies roughly coincide).
/// </summary>
public sealed class DecalPlacement
{
    public DecalSurface Surface { get; set; }

    /// <summary> Rest-pose position of the decal centre, model space, metres. </summary>
    public Vector3 Position { get; set; }

    /// <summary> Rest-pose surface normal at <see cref="Position"/>, unit length. The projector looks along -Normal. </summary>
    public Vector3 Normal { get; set; } = Vector3.UnitZ;

    /// <summary> Rotation of the image around the normal, degrees. </summary>
    public float Rotation { get; set; }

    /// <summary> Width of the projected image, metres. Height follows the image aspect ratio. </summary>
    public float Size { get; set; } = 0.1f;

    /// <summary> Half depth of the projector box, metres, either side of the surface point. </summary>
    public float Depth { get; set; } = 0.05f;

    /// <summary> Texture coordinate of the clicked point. Informational. </summary>
    public Vector2 Uv { get; set; }

    /// <summary> True when the clicked spot's texture region is shared with a mirrored part of the mesh (decal shows twice). </summary>
    public bool MirroredUv { get; set; }

    /// <summary> Human readable part name from the pick (bone name based), informational. </summary>
    public string? PartName { get; set; }

    /// <summary>
    /// Game path of the skin diffuse the decal was placed on. A body can draw several skin textures at once (a body mod's
    /// torso, vanilla hands), each its own <see cref="SkinMesh"/> of the same <see cref="Surface"/>; this picks the right one.
    /// Null (older placements) = the surface's first mesh.
    /// </summary>
    public string? DiffuseGamePath { get; set; }

    public DecalPlacement Clone()
        => (DecalPlacement)MemberwiseClone();

    /// <summary> Stable text of the parts that affect the bake, for hashing. </summary>
    public string ContentKey()
        => FormattableString.Invariant(
            $"{Surface}|{DiffuseGamePath?.ToLowerInvariant()}|{Position.X:F4},{Position.Y:F4},{Position.Z:F4}|{Normal.X:F4},{Normal.Y:F4},{Normal.Z:F4}|{Rotation:F2}|{Size:F4}|{Depth:F4}");
}

/// <summary> Frozen content of one decal inside a VirtualDesign. </summary>
/// <param name="ImageFile"> File name of the decal image inside Lamina's decal image folder. </param>
/// <param name="Placement"> Where it goes. </param>
/// <param name="Opacity"> 0..1. </param>
public sealed record DecalSpec(string ImageFile, DecalPlacement Placement, float Opacity)
{
    /// <summary> Stable text of the content, for hashing. </summary>
    public string ContentKey()
        => FormattableString.Invariant($"{ImageFile.ToLowerInvariant()}|{Placement.ContentKey()}|{Opacity:F3}");
}

/// <summary> An 8-bit RGBA image, row major, no row padding (stride = Width * 4). </summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        if (pixels is not null && pixels.Length != width * height * 4)
            throw new ArgumentException("Pixel buffer size does not match the dimensions.", nameof(pixels));
        Width  = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary> RGBA8, row major, top row first. </summary>
    public byte[] Pixels { get; }

    public int Offset(int x, int y)
        => (y * Width + x) * 4;

    public RgbaImage Clone()
        => new(Width, Height, (byte[])Pixels.Clone());
}

/// <summary>
/// Rest-pose geometry of one skin surface of the local player: the LOD 0 meshes of every drawn model that use the surface's
/// skin material, merged into one index list. Positions / normals are in the draw object's model space (metres, rest pose).
/// </summary>
public sealed class SkinMesh
{
    public required DecalSurface Surface { get; init; }

    /// <summary> Game path of the .mdl the geometry came from (first model when several contribute). </summary>
    public required string ModelGamePath { get; init; }

    /// <summary> Game path of the skin .mtrl. </summary>
    public required string MaterialGamePath { get; init; }

    /// <summary> Game path of the material's diffuse texture: the path the bake output is redirected under. </summary>
    public required string DiffuseGamePath { get; init; }

    public required Vector3[] Positions { get; init; }

    public required Vector3[] Normals { get; init; }

    public required Vector2[] Uvs { get; init; }

    /// <summary> Triangle list, three indices per triangle. </summary>
    public required int[] Indices { get; init; }

    /// <summary> Four bone slots per vertex, indices into <see cref="BoneNames"/>. </summary>
    public required byte[] BoneIndices { get; init; }

    /// <summary> Four weights per vertex, matching <see cref="BoneIndices"/>; they sum to 1. </summary>
    public required float[] BoneWeights { get; init; }

    public required string[] BoneNames { get; init; }

    public int VertexCount
        => Positions.Length;

    public int TriangleCount
        => Indices.Length / 3;
}

/// <summary> The result of a pick on the posed local player. </summary>
/// <param name="Mesh"> The surface that was hit. </param>
/// <param name="Triangle"> Triangle index into <see cref="SkinMesh.Indices"/> (triangle n uses indices 3n..3n+2). </param>
/// <param name="Barycentric"> Barycentric weights of the hit inside the triangle. </param>
/// <param name="WorldPosition"> Hit point in world space (posed), for drawing the cursor. </param>
/// <param name="WorldNormal"> Surface normal at the hit in world space (posed). </param>
/// <param name="RestPosition"> The same point on the rest-pose mesh, model space. </param>
/// <param name="RestNormal"> The rest-pose normal there, unit length. </param>
/// <param name="Uv"> Interpolated texture coordinate. </param>
/// <param name="MirroredUv"> True when another, mirrored triangle maps onto the same texture region. </param>
/// <param name="PartName"> Human readable part, from the dominant bone name, or null. </param>
public readonly record struct DecalPick(
    SkinMesh Mesh,
    int Triangle,
    Vector3 Barycentric,
    Vector3 WorldPosition,
    Vector3 WorldNormal,
    Vector3 RestPosition,
    Vector3 RestNormal,
    Vector2 Uv,
    bool MirroredUv,
    string? PartName)
{
    /// <summary> A placement at this pick with the given size / rotation. </summary>
    public DecalPlacement ToPlacement(float size, float rotation, float depth = 0.05f)
        => new()
        {
            Surface         = Mesh.Surface,
            DiffuseGamePath = Mesh.DiffuseGamePath,
            Position        = RestPosition,
            Normal     = RestNormal,
            Size       = size,
            Rotation   = rotation,
            Depth      = depth,
            Uv         = Uv,
            MirroredUv = MirroredUv,
            PartName   = PartName,
        };
}

/// <summary> Live skin geometry of the local player. Framework thread only. </summary>
public interface ISkinMeshProvider
{
    /// <summary>
    /// Rest-pose skin meshes built from the models the local player is drawing right now (so a modded body is the body
    /// that is used). Cached until <see cref="Invalidate"/>. Empty when no player is drawn or Penumbra is unavailable.
    /// </summary>
    IReadOnlyList<SkinMesh> GetLocalPlayerMeshes();

    /// <summary> Drops the cache (after a redraw, body change or mod change). </summary>
    void Invalidate();

    /// <summary>
    /// A cheap identity of the player's draw object as it is right now, without building meshes: the body race and, per
    /// drawn model, its slot, file name, enabled shape / attribute masks and resource load state. Null while no player is
    /// drawn (logged out, mid-redraw, loading screen). Login, zone change, gear swap, redraw and a mod change all change it.
    /// </summary>
    DrawObjectIdentity? DrawObjectState();
}

/// <summary> What the player's draw object renders right now, as <see cref="ISkinMeshProvider.DrawObjectState"/> reads it. </summary>
/// <param name="Key"> Identity text of the drawn models; equal keys mean the same skin meshes and textures. </param>
/// <param name="Loaded"> True when every drawn model's resource reports loaded, so the key names the outfit on screen rather than one still arriving. </param>
public sealed record DrawObjectIdentity(string Key, bool Loaded);

/// <summary> Mouse picking on the posed local player. Framework thread only. </summary>
public interface IDecalPicker
{
    /// <summary> Casts a ray through the screen position and returns the nearest skin hit, or null. </summary>
    DecalPick? Pick(Vector2 screenPosition);
}

/// <summary> Outcome of a publish run. </summary>
/// <param name="Success"> Every decal was baked and the redirect is in place. </param>
/// <param name="Published"> Number of surfaces redirected. </param>
/// <param name="Problems"> Human readable problems (missing image, surface not drawn, conversion failed ...). </param>
/// <param name="RedirectsChanged">
/// True when the installed redirects (game path → bake file) differ from what the publisher last installed in the collection,
/// including the first install and any install after a <see cref="IDecalPublisher.Clear"/>. False for an identical re-install:
/// the game already loaded those files by path, so no redraw is needed for it.
/// </param>
public sealed record DecalPublishResult(bool Success, int Published, IReadOnlyList<string> Problems, bool RedirectsChanged = true);

/// <summary> What <see cref="IDecalPublisher.CleanUp"/> removes. </summary>
public enum DecalCleanupMode
{
    /// <summary> Leftovers only: remembered situations other than the installed one, bake files the installed map does not name, stale temporary files. </summary>
    Unused,

    /// <summary> Everything: the temporary mod, every remembered situation and upstream, every file in the cache folder. </summary>
    Everything,
}

/// <summary> Outcome of <see cref="IDecalPublisher.CleanUp"/>. </summary>
/// <param name="MapsForgotten"> Remembered situation → redirect maps dropped. </param>
/// <param name="BakesDeleted"> Bake (.tex) files deleted from the cache folder. </param>
/// <param name="TempFilesDeleted"> Temporary files of interrupted conversions deleted. </param>
/// <param name="TemporaryModRemoved"> Lamina's decal temporary mod was removed from the collection. </param>
/// <param name="Problems"> Human readable problems. </param>
public sealed record DecalCleanupResult(int MapsForgotten, int BakesDeleted, int TempFilesDeleted, bool TemporaryModRemoved, IReadOnlyList<string> Problems);

/// <summary>
/// Bakes decals into the player's skin textures and redirects those textures in a Penumbra collection with one temporary mod.
/// Work runs off the framework thread; <see cref="Completed"/> fires on the framework thread when the redirect is in place,
/// which is when the caller should request a redraw (when the result says the redirects changed).
/// </summary>
public interface IDecalPublisher
{
    /// <summary>
    /// Starts a publish. A run already in progress is superseded (its result is discarded). An empty list behaves like
    /// <see cref="Clear"/>. <paramref name="modsKey"/> identifies the mod state the caller put into the collection (null for
    /// none); a change of it means the skin textures may resolve differently, while an unchanged one lets the publisher reuse
    /// what it installed or remembered without baking. May complete synchronously (before returning) when nothing needs baking.
    /// </summary>
    void Publish(Guid collectionId, int objectIndex, IReadOnlyList<DecalSpec> decals, string? modsKey = null);

    /// <summary> Removes Lamina's decal temporary mod from the collection. Synchronous. </summary>
    void Clear(Guid collectionId);

    /// <summary>
    /// Deletes leftovers (<see cref="DecalCleanupMode.Unused"/>) or everything (<see cref="DecalCleanupMode.Everything"/>) the
    /// publisher owns: remembered maps, cache files, the remembered upstream textures. A publish in flight is superseded first.
    /// The temporary mod is removed from <paramref name="collectionId"/> when <paramref name="removeTemporaryMod"/> asks for it
    /// or the mode is Everything; forgetting what is installed, so the next publish installs anew. Synchronous.
    /// </summary>
    DecalCleanupResult CleanUp(Guid? collectionId, DecalCleanupMode mode, bool removeTemporaryMod);

    /// <summary> True while a publish is running. </summary>
    bool Busy { get; }

    event Action<DecalPublishResult>? Completed;
}

/// <summary> Folder conventions shared by the UI (writes images) and the publisher (reads images, writes bakes). </summary>
public static class DecalPaths
{
    /// <summary> Tag of Lamina's decal temporary mod in Penumbra. </summary>
    public const string TemporaryModTag = "Lamina.Decal";

    /// <summary> Priority of the decal temporary mod: above any body texture mod, whose texture the bake already contains. </summary>
    public const int TemporaryModPriority = 9000;

    /// <summary> Where imported decal images live: &lt;plugin config dir&gt;/decals/images. </summary>
    public static string ImageDirectory(string pluginConfigDirectory)
        => System.IO.Path.Combine(pluginConfigDirectory, "decals", "images");

    /// <summary> Where baked textures live: &lt;plugin config dir&gt;/decals/cache. </summary>
    public static string CacheDirectory(string pluginConfigDirectory)
        => System.IO.Path.Combine(pluginConfigDirectory, "decals", "cache");
}
