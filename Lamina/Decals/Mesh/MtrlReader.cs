using System.IO;

namespace Lamina.Decals.Mesh;

/// <summary> A texture slot of a material. </summary>
/// <param name="Path"> The path as written in the .mtrl (never carries the DX11 "--" prefix). </param>
/// <param name="Flags"> Slot flags; 0x8000 marks a DX11 texture. </param>
public readonly record struct MtrlTexture(string Path, ushort Flags)
{
    public const ushort Dx11Flag = 0x8000;

    public bool Dx11
        => (Flags & Dx11Flag) != 0;

    /// <summary> The path the game actually requests: DX11 textures get "--" in front of the file name. </summary>
    public string LoadPath
        => Dx11 ? MtrlReader.WithDx11Prefix(Path) : Path;
}

/// <summary> A sampler entry of a material: which texture slot feeds which shader sampler. </summary>
public readonly record struct MtrlSampler(uint Id, uint Flags, byte TextureIndex);

/// <summary> The parts of a .mtrl the mesh part needs: texture slots and the sampler table. </summary>
public sealed class MtrlInfo
{
    public required uint Version { get; init; }

    public required string ShaderPackage { get; init; }

    public required MtrlTexture[] Textures { get; init; }

    public required MtrlSampler[] Samplers { get; init; }

    /// <summary> The texture bound to a sampler id, or null. </summary>
    public MtrlTexture? TextureForSampler(uint samplerId)
    {
        foreach (var sampler in Samplers)
        {
            if (sampler.Id == samplerId && sampler.TextureIndex < Textures.Length)
                return Textures[sampler.TextureIndex];
        }

        return null;
    }

    /// <summary>
    /// The diffuse texture: the one bound to g_SamplerDiffuse, else the first slot whose file name looks like a diffuse
    /// (<c>_d</c>, <c>_base</c>, <c>_diffuse</c>). Null when neither applies.
    /// </summary>
    public MtrlTexture? Diffuse
    {
        get
        {
            var bySampler = TextureForSampler(MtrlReader.SamplerDiffuse);
            if (bySampler is not null)
                return bySampler;

            foreach (var texture in Textures)
            {
                if (MtrlReader.LooksLikeDiffuse(texture.Path))
                    return texture;
            }

            return null;
        }
    }
}

/// <summary>
/// Minimal .mtrl reader (texture paths, shader package, samplers). Written against Penumbra.GameData's MtrlFile; it skips the
/// colour table / dye data wholesale, so legacy and Dawntrail materials parse alike.
/// </summary>
public static class MtrlReader
{
    /// <summary> Shader sampler ids of the skin shaders (Lumina.Data.Parsing.TextureUsage). </summary>
    public const uint SamplerDiffuse = 0x115306BE;
    public const uint SamplerNormal  = 0x0C5EC1F1;
    public const uint SamplerMask    = 0x8A4E82B6;

    private static readonly string[] DiffuseSuffixes = ["_d.tex", "_base.tex", "_diffuse.tex", "_diff.tex"];

    /// <exception cref="InvalidDataException"> The file is truncated. </exception>
    public static MtrlInfo Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var r       = new BinaryCursor(data);
        var version = r.U32();
        r.Skip(2); // file size
        var dataSetSize             = r.U16();
        var stringTableSize         = r.U16();
        var shaderPackageNameOffset = r.U16();
        var textureCount            = r.U8();
        var uvSetCount              = r.U8();
        var colorSetCount           = r.U8();
        var additionalDataSize      = r.U8();

        var textureOffsets = new (ushort Offset, ushort Flags)[textureCount];
        for (var i = 0; i < textureCount; i++)
            textureOffsets[i] = (r.U16(), r.U16());
        r.Skip(4 * uvSetCount);
        r.Skip(4 * colorSetCount);

        var stringsStart = r.Position;
        r.Skip(stringTableSize);
        r.Skip(additionalDataSize);
        r.Skip(dataSetSize);

        r.Skip(2); // shader value list size
        var shaderKeyCount = r.U16();
        var constantCount  = r.U16();
        var samplerCount   = r.U16();
        r.Skip(4); // flags
        r.Skip(8 * shaderKeyCount);
        r.Skip(8 * constantCount);

        var samplers = new MtrlSampler[samplerCount];
        for (var i = 0; i < samplerCount; i++)
        {
            var id    = r.U32();
            var flags = r.U32();
            var index = r.U8();
            r.Skip(3);
            samplers[i] = new MtrlSampler(id, flags, index);
        }

        var textures = new MtrlTexture[textureCount];
        for (var i = 0; i < textureCount; i++)
            textures[i] = new MtrlTexture(r.StringAt(stringsStart + textureOffsets[i].Offset), textureOffsets[i].Flags);

        return new MtrlInfo
        {
            Version       = version,
            ShaderPackage = r.StringAt(stringsStart + shaderPackageNameOffset),
            Textures      = textures,
            Samplers      = samplers,
        };
    }

    /// <summary> True when the file name (ignoring a "--" prefix) ends in a diffuse suffix: <c>_d</c> (pre-7.0), <c>_base</c> (Dawntrail), <c>_diffuse</c>. </summary>
    public static bool LooksLikeDiffuse(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var name = FileName(WithoutDx11Prefix(path)).ToLowerInvariant();
        foreach (var suffix in DiffuseSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary> Inserts the DX11 marker: <c>a/b/c.tex</c> → <c>a/b/--c.tex</c>. Unchanged when already present. </summary>
    public static string WithDx11Prefix(string path)
    {
        var slash = LastSeparator(path);
        var name  = path[(slash + 1)..];
        if (name.StartsWith("--", StringComparison.Ordinal))
            return path;

        return path[..(slash + 1)] + "--" + name;
    }

    /// <summary> Removes a DX11 marker from the file name, if any. </summary>
    public static string WithoutDx11Prefix(string path)
    {
        var slash = LastSeparator(path);
        var name  = path[(slash + 1)..];
        return name.StartsWith("--", StringComparison.Ordinal) ? path[..(slash + 1)] + name[2..] : path;
    }

    /// <summary> True when both paths name the same texture, ignoring case, separators and the DX11 marker. </summary>
    public static bool SameTexture(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        return string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

        static string Normalise(string path)
            => WithoutDx11Prefix(path.Replace('\\', '/'));
    }

    /// <summary> File name part of a path with either separator. </summary>
    public static string FileName(string path)
        => path[(LastSeparator(path) + 1)..];

    private static int LastSeparator(string path)
        => Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
}
