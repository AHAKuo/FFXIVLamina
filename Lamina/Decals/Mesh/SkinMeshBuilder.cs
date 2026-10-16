using System.Numerics;
using System.Text.RegularExpressions;
using XivLiveMesh;

namespace Lamina.Decals.Mesh;

/// <summary> One model that may contribute meshes to a surface. </summary>
/// <param name="ModelGamePath"> Game path of the .mdl (the race it was authored for is read from it). </param>
/// <param name="FileName"> The file the game loaded (a mod file or the game path), for the overlay. </param>
/// <param name="Geometry"> Its parsed geometry with the enabled shape keys applied. </param>
/// <param name="MaterialKey"> <see cref="SkinMeshBuilder.SkinMaterialKey"/> of the surface's skin material; meshes whose material has this key contribute. </param>
/// <param name="Race"> Body shape family (cXXXX number) the model was authored for, 0 when unknown. </param>
/// <param name="Deform"> Racial deformer matrix per bone name, taking the authored bind pose into the character's; null when none applies. </param>
public sealed record SkinMeshSource(string ModelGamePath, string FileName, MdlGeometry Geometry, string MaterialKey, ushort Race = 0,
    IReadOnlyDictionary<string, Matrix4x4>? Deform = null);

/// <summary> One model's contribution to a built surface, as XivLiveMesh poses it. </summary>
/// <param name="Mesh"> The model's skin meshes: authored (bind pose, authored race) positions, shaped triangles, 8 influences per vertex. </param>
/// <param name="Race"> The race the model was authored for, for the deformer chain; 0 when unknown. </param>
/// <param name="VertexOffset"> Index of the source's first vertex in the merged <see cref="SkinMesh"/> (local index + offset = merged index). </param>
/// <param name="TriangleOffset"> Index of the source's first triangle in the merged <see cref="SkinMesh"/>. </param>
/// <param name="FileName"> The model file, for the overlay. </param>
/// <param name="ModelGamePath"> The model's game path. </param>
public sealed record LiveSource(SkinnedMesh Mesh, ushort Race, int VertexOffset, int TriangleOffset, string FileName, string ModelGamePath);

/// <summary> A built surface: the merged contract mesh and the per-model sources it was merged from, in merge order. </summary>
public sealed record SkinMeshBuild(SkinMesh Mesh, IReadOnlyList<LiveSource> Sources);

/// <summary>
/// Merges the skin meshes of several models into one <see cref="SkinMesh"/> and keeps each model as a <see cref="LiveSource"/>
/// for posing. Pure code, unit-tested.
/// </summary>
public static partial class SkinMeshBuilder
{
    /// <summary> Bone slots of the merged mesh are bytes, so it can reference at most this many bones. </summary>
    public const int MaxBones = 256;

    /// <summary> Bone an unskinned mesh is bound to in the merged mesh, so it at least follows the character. </summary>
    public const string FallbackBone = "n_root";

    /// <summary>
    /// Merges the LOD 0 meshes of every source that use its skin material (matched by <see cref="SkinMaterialKey"/>) into one
    /// mesh: vertices are appended, indices offset, bone slots reduced to the four largest influences and remapped into one
    /// shared bone list. A source's <see cref="SkinMeshSource.Deform"/> is applied to the merged positions and normals, so they
    /// sit in the character's rest pose. Every source that contributed is returned as a <see cref="LiveSource"/> with the
    /// authored geometry and all eight influences. Null when nothing matched.
    /// </summary>
    public static SkinMeshBuild? Build(DecalSurface surface, string materialGamePath, string diffuseGamePath, IReadOnlyList<SkinMeshSource> sources,
        Action<string>? warn = null)
    {
        var positions   = new List<Vector3>();
        var normals     = new List<Vector3>();
        var uvs         = new List<Vector2>();
        var indices     = new List<int>();
        var boneIndices = new List<byte>();
        var boneWeights = new List<float>();
        var boneNames   = new List<string>();
        var boneLookup  = new Dictionary<string, byte>(StringComparer.Ordinal);
        var liveSources = new List<LiveSource>();
        string? firstModel = null;

        Span<float> candidateW = stackalloc float[MdlMesh.Influences];
        Span<byte>  candidateB = stackalloc byte[MdlMesh.Influences];
        Span<float> topW       = stackalloc float[4];
        Span<byte>  topB       = stackalloc byte[4];

        foreach (var source in sources)
        {
            var vertexOffset   = positions.Count;
            var triangleOffset = indices.Count / 3;

            var sourcePositions = new List<Vector3>();
            var sourceTriangles = new List<int>();
            var sourceBase      = new List<int>();
            var sourceMaterials = new List<ushort>();
            var sourceBoneIdx   = new List<ushort>();
            var sourceBoneW     = new List<float>();
            var sourceBones     = new List<string>();
            var sourceLookup    = new Dictionary<string, ushort>(StringComparer.Ordinal);

            foreach (var mesh in source.Geometry.Lod0Meshes)
            {
                var material = mesh.MaterialIndex < source.Geometry.Materials.Length ? source.Geometry.Materials[mesh.MaterialIndex] : string.Empty;
                if (!MaterialMatches(material, source.MaterialKey))
                    continue;

                var names = mesh.Skinned ? mesh.BoneNames : [FallbackBone];

                // Merged bone list (bytes, capped) and the source's own list (no cap).
                var remap = new byte[names.Length];
                var fits  = true;
                for (var b = 0; b < names.Length; b++)
                {
                    if (!boneLookup.TryGetValue(names[b], out var index))
                    {
                        if (boneNames.Count >= MaxBones)
                        {
                            fits = false;
                            break;
                        }

                        index = (byte)boneNames.Count;
                        boneNames.Add(names[b]);
                        boneLookup[names[b]] = index;
                    }

                    remap[b] = index;
                }

                if (!fits)
                {
                    warn?.Invoke($"Lamina: skin mesh {mesh.MeshIndex} of {source.ModelGamePath} is skipped, the {surface} surface already uses {MaxBones} bones.");
                    continue;
                }

                var sourceRemap = new ushort[names.Length];
                for (var b = 0; b < names.Length; b++)
                {
                    if (!sourceLookup.TryGetValue(names[b], out var index))
                    {
                        index = (ushort)sourceBones.Count;
                        sourceBones.Add(names[b]);
                        sourceLookup[names[b]] = index;
                    }

                    sourceRemap[b] = index;
                }

                var baseVertex       = positions.Count;
                var sourceBaseVertex = sourcePositions.Count;
                for (var v = 0; v < mesh.VertexCount; v++)
                {
                    // The vertex's valid influences (bone slot within the mesh's table, raw weight).
                    var count = 0;
                    if (mesh.Skinned)
                    {
                        for (var k = 0; k < MdlMesh.Influences; k++)
                        {
                            var w    = mesh.BoneWeights[v * MdlMesh.Influences + k];
                            var slot = mesh.BoneIndices[v * MdlMesh.Influences + k];
                            if (w <= 0f || slot >= names.Length)
                                continue;

                            candidateW[count] = w;
                            candidateB[count] = slot;
                            count++;
                        }
                    }

                    var position = mesh.Positions[v];
                    var normal   = mesh.Normals[v];
                    if (source.Deform is { } deform && count > 0)
                    {
                        var m = BlendDeform(deform, names, candidateB[..count], candidateW[..count]);
                        position = Vector3.Transform(position, m);
                        var deformed = MeshMath.SafeNormalize(Vector3.TransformNormal(normal, m));
                        if (deformed != Vector3.Zero)
                            normal = deformed;
                    }

                    positions.Add(position);
                    normals.Add(normal);
                    uvs.Add(mesh.Uvs[v]);
                    sourcePositions.Add(mesh.Positions[v]);

                    // Merged mesh: the four largest influences, renormalised; a vertex without any follows the first bone.
                    if (count > 0 && MeshMath.SelectTopFour(candidateW[..count], candidateB[..count], topW, topB))
                    {
                        for (var k = 0; k < 4; k++)
                        {
                            boneIndices.Add(topW[k] > 0f ? remap[topB[k]] : (byte)0);
                            boneWeights.Add(topW[k]);
                        }
                    }
                    else
                    {
                        boneIndices.Add(remap[0]);
                        boneWeights.Add(1f);
                        for (var k = 1; k < 4; k++)
                        {
                            boneIndices.Add(0);
                            boneWeights.Add(0f);
                        }
                    }

                    // Source mesh: all eight as stored (zero padded; an unskinned vertex only follows the root).
                    for (var k = 0; k < MdlMesh.Influences; k++)
                    {
                        if (k < count)
                        {
                            sourceBoneIdx.Add(sourceRemap[candidateB[k]]);
                            sourceBoneW.Add(candidateW[k]);
                        }
                        else
                        {
                            sourceBoneIdx.Add(0);
                            sourceBoneW.Add(0f);
                        }
                    }
                }

                foreach (var index in mesh.Indices)
                {
                    indices.Add(baseVertex + index);
                    sourceTriangles.Add(sourceBaseVertex + index);
                }

                foreach (var index in mesh.BaseIndices)
                    sourceBase.Add(sourceBaseVertex + index);

                for (var t = 0; t < mesh.Indices.Length / 3; t++)
                    sourceMaterials.Add((ushort)mesh.MaterialIndex);

                firstModel ??= source.ModelGamePath;
            }

            if (sourceTriangles.Count == 0)
                continue;

            var skinned = new SkinnedMesh
            {
                Positions         = sourcePositions.ToArray(),
                Triangles         = sourceTriangles.ToArray(),
                BaseTriangles     = sourceBase.ToArray(),
                MaterialNames     = source.Geometry.Materials,
                TriangleMaterials = sourceMaterials.ToArray(),
                BoneNames         = sourceBones.ToArray(),
                BoneIndices       = sourceBoneIdx.ToArray(),
                BoneWeights       = sourceBoneW.ToArray(),
                GenderRace        = source.Race,
            };
            skinned.Validate();
            liveSources.Add(new LiveSource(skinned, source.Race, vertexOffset, triangleOffset, source.FileName, source.ModelGamePath));
        }

        if (indices.Count == 0 || firstModel is null)
            return null;

        var merged = new SkinMesh
        {
            Surface          = surface,
            ModelGamePath    = firstModel,
            MaterialGamePath = materialGamePath,
            DiffuseGamePath  = diffuseGamePath,
            Positions        = positions.ToArray(),
            Normals          = normals.ToArray(),
            Uvs              = uvs.ToArray(),
            Indices          = indices.ToArray(),
            BoneIndices      = boneIndices.ToArray(),
            BoneWeights      = boneWeights.ToArray(),
            BoneNames        = boneNames.ToArray(),
        };
        return new SkinMeshBuild(merged, liveSources);
    }

    /// <summary> The influence-weighted blend of the bones' deformer matrices (identity for a bone the deformer does not list). </summary>
    private static Matrix4x4 BlendDeform(IReadOnlyDictionary<string, Matrix4x4> deform, string[] names, ReadOnlySpan<byte> bones, ReadOnlySpan<float> weights)
    {
        var sum   = default(Matrix4x4);
        var total = 0f;
        for (var k = 0; k < bones.Length; k++)
        {
            if (!deform.TryGetValue(names[bones[k]], out var m))
                m = Matrix4x4.Identity;
            sum   += m * weights[k];
            total += weights[k];
        }

        return total > 1e-6f ? sum * (1f / total) : Matrix4x4.Identity;
    }

    /// <summary>
    /// True when a model's material entry ("/mt_c0101b0001_a.mtrl" or a full path) names the given skin material, ignoring the
    /// race code: gear names the Midlander skin material and the game substitutes the character's race.
    /// </summary>
    public static bool MaterialMatches(string modelMaterial, string materialKeyOrName)
    {
        if (string.IsNullOrEmpty(modelMaterial) || string.IsNullOrEmpty(materialKeyOrName))
            return false;

        // A surface can be served by several materials (a body mod's "_b", an outfit's freely named skin material); the key
        // then lists them separated by '|'.
        var key = SkinMaterialKey(modelMaterial);
        foreach (var candidate in materialKeyOrName.Split('|', StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(key, SkinMaterialKey(candidate), StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>
    /// The surface a material belongs to, judged by what it is rather than what it is called: a material drawn with the
    /// skin shader is skin whatever its file name (outfits ship freely named skin materials), the face when its name or
    /// path says so, the body otherwise. Without a shader name the file-name rule decides.
    /// </summary>
    /// <param name="materialPathOrName"> The model's material entry or the resolved game path. </param>
    /// <param name="shaderPackage"> The material's shader package (<c>skin.shpk</c> for skin), or null when unknown. </param>
    public static DecalSurface? ClassifySkinMaterial(string? materialPathOrName, string? shaderPackage)
    {
        var byName = ClassifySkinMaterial(materialPathOrName);
        if (string.IsNullOrEmpty(shaderPackage))
            return byName;

        if (!shaderPackage.StartsWith("skin", StringComparison.OrdinalIgnoreCase))
            return null;

        if (byName is { } named)
            return named;

        var lower = (materialPathOrName ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        return lower.Contains("/obj/face/", StringComparison.Ordinal) || lower.Contains("_fac_", StringComparison.Ordinal)
            ? DecalSurface.Face
            : DecalSurface.Body;
    }

    /// <summary>
    /// Identity of a skin material across races: the lower-case file name with the cXXXX race code replaced by <c>c****</c>
    /// (<c>mt_c0801b0001_a.mtrl</c> → <c>mt_c****b0001_a.mtrl</c>). Idempotent.
    /// </summary>
    public static string SkinMaterialKey(string materialPathOrName)
        => RaceCode().Replace(MtrlReader.FileName(materialPathOrName.Replace('\\', '/')).ToLowerInvariant(), "c****");

    /// <summary>
    /// The surface a material belongs to, from its file name the way the game names skin materials: <c>mt_cXXXXbXXXX_*</c> is the
    /// body skin, <c>mt_cXXXXfXXXX_fac_*</c> the face skin. Null for everything else (hair, tail, ears, eyes, teeth, gear, weapons,
    /// monsters). Works on a model's material entry, a file name or a full path.
    /// </summary>
    public static DecalSurface? ClassifySkinMaterial(string? materialPathOrName)
    {
        if (string.IsNullOrEmpty(materialPathOrName))
            return null;

        var name = MtrlReader.FileName(materialPathOrName.Replace('\\', '/')).ToLowerInvariant();
        if (!name.EndsWith(".mtrl", StringComparison.Ordinal))
            return null;

        if (BodyMaterial().IsMatch(name))
            return DecalSurface.Body;

        if (FaceMaterial().IsMatch(name))
            return DecalSurface.Face;

        return null;
    }

    /// <summary> The cXXXX race code in a game path as a number (c0201 → 201), or 0 when it has none (a mod file named freely). </summary>
    public static ushort RaceOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return 0;

        var match = RaceCode().Match(path);
        return match.Success && ushort.TryParse(match.Groups[1].Value, out var race) ? race : (ushort)0;
    }

    // A 'c' and four digits not preceded by a letter, so "chara" and "acc0101" do not match.
    [GeneratedRegex(@"(?<![a-z])c(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex RaceCode();

    [GeneratedRegex(@"^mt_c\d{4}b\d{4}_")]
    private static partial Regex BodyMaterial();

    [GeneratedRegex(@"^mt_c\d{4}f\d{4}_fac_")]
    private static partial Regex FaceMaterial();
}
