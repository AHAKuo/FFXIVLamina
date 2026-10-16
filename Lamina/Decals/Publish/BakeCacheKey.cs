using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Lamina.Decals.Publish;

/// <summary>
/// Names a bake output after everything that went into it, so changed content always gets a new file name (the game caches
/// textures by path and would keep showing an overwritten file's old pixels) and unchanged content is found again without
/// baking. Pure, so it is unit tested without Dalamud.
/// </summary>
public static class BakeCacheKey
{
    /// <summary> Bump when the bake math or the output format changes; every cached bake is then stale and re-baked. </summary>
    public const int BakeFormatVersion = 1;

    /// <summary>
    /// Identity of the upstream texture the bake starts from: the game path for an unmodified texture, full path + last
    /// write time + length for a file Penumbra redirects to (a body texture mod).
    /// </summary>
    public static string UpstreamStamp(string upstreamPath)
    {
        if (!Path.IsPathRooted(upstreamPath))
            return "game:" + upstreamPath.ToLowerInvariant();

        var info = new FileInfo(upstreamPath);
        return info.Exists
            ? FormattableString.Invariant($"file:{info.FullName.ToLowerInvariant()}|{info.LastWriteTimeUtc.Ticks}|{info.Length}")
            : "file:" + upstreamPath.ToLowerInvariant() + "|missing";
    }

    /// <summary> Geometry identity of a skin mesh (positions, texture coordinates, indices), so a body swap under the same model path re-bakes. </summary>
    public static string MeshFingerprint(SkinMesh mesh)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes(mesh.Positions.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(mesh.Uvs.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
        return Hex(hash.GetHashAndReset());
    }

    /// <summary> Identity of a decal image file's bytes. </summary>
    public static string BytesHash(ReadOnlySpan<byte> bytes)
        => Hex(SHA256.HashData(bytes));

    /// <summary>
    /// The key: upstream stamp, model path, mesh fingerprint, every decal's image hash and content key in bake order, and the
    /// format version, hashed to 16 hex characters.
    /// </summary>
    public static string Compute(string upstreamStamp, string modelGamePath, string meshFingerprint, IReadOnlyList<(string ImageHash, string ContentKey)> decals)
    {
        var sb = new StringBuilder(512);
        sb.Append("v=").Append(BakeFormatVersion).Append('\n');
        sb.Append("base=").Append(upstreamStamp).Append('\n');
        sb.Append("model=").Append(modelGamePath.ToLowerInvariant()).Append('\n');
        sb.Append("mesh=").Append(meshFingerprint).Append('\n');
        foreach (var (image, key) in decals)
            sb.Append("decal=").Append(image).Append('|').Append(key).Append('\n');
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary> Output file for a surface and key: &lt;cache&gt;/&lt;Surface&gt;_&lt;key&gt;.tex. </summary>
    public static string OutputFile(string cacheDirectory, DecalSurface surface, string key)
        => Path.Combine(cacheDirectory, $"{surface}_{key}.tex");

    private static string Hex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(bytes[..8]);
}
