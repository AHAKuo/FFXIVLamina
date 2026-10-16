using System.Numerics;
using Lamina.Decals;
using Lamina.Decals.Mesh;
using Xunit;

namespace Lamina.Tests.Decals;

public class SkinMeshBuilderTests
{
    private const string SkinMaterial  = "/mt_c0101b0001_a.mtrl";
    private const string GearSkin      = "/mt_c0201b0001_a.mtrl"; // the same skin material as gear names it (Midlander female)
    private const string OtherMaterial = "/mt_c0101e0001_top_a.mtrl";
    private const string MaterialPath  = "chara/human/c0801/obj/body/b0001/material/v0001/mt_c0801b0001_a.mtrl";

    private static readonly string SkinKey = SkinMeshBuilder.SkinMaterialKey(MaterialPath);

    /// <summary> A quad (two triangles) whose four vertices are bound to local bone 0 (first two) and local bone 1 (last two), weight 1 in slot 0. </summary>
    private static MdlMesh Quad(int meshIndex, int materialIndex, string[] boneNames, Vector3 offset, int startIndex = 0)
    {
        var positions = new[] { offset, offset + Vector3.UnitX, offset + Vector3.UnitY, offset + Vector3.UnitX + Vector3.UnitY };
        var skinned   = boneNames.Length > 0;
        var bones     = new byte[4 * MdlMesh.Influences];
        var weights   = new float[4 * MdlMesh.Influences];
        for (var v = 0; v < 4; v++)
        {
            bones[v * MdlMesh.Influences]   = (byte)(skinned && v >= 2 ? 1 : 0);
            weights[v * MdlMesh.Influences] = skinned ? 1f : 0f;
        }

        var indices = new[] { 0, 1, 2, 2, 1, 3 };
        return new MdlMesh
        {
            MeshIndex     = meshIndex,
            MaterialIndex = materialIndex,
            StartIndex    = startIndex,
            BoneNames     = boneNames,
            Skinned       = skinned,
            Positions     = positions,
            Normals       = positions.Select(_ => Vector3.UnitZ).ToArray(),
            Uvs           = positions.Select(p => new Vector2(p.X, p.Y)).ToArray(),
            BoneIndices   = bones,
            BoneWeights   = weights,
            Indices       = indices,
            BaseIndices   = indices,
        };
    }

    private static MdlGeometry Model(string[] materials, string[] bones, params MdlMesh[] meshes)
        => new()
        {
            Version    = MdlReader.Version6,
            Materials  = materials,
            Bones      = bones,
            Lod0Meshes = meshes,
            Shapes     = [],
        };

    private static SkinMeshSource Source(string gamePath, MdlGeometry geometry, ushort race = 0, IReadOnlyDictionary<string, Matrix4x4>? deform = null)
        => new(gamePath, gamePath, geometry, SkinKey, race, deform);

    [Fact]
    public void Merges_MatchingMeshes_AcrossRaces_RemapsBones_And_OffsetsIndices()
    {
        var body = Model([SkinMaterial], ["j_a", "j_b"], Quad(0, 0, ["j_a", "j_b"], Vector3.Zero));
        var gear = Model([OtherMaterial, GearSkin], ["j_b", "j_c"],
            Quad(0, 0, ["j_b", "j_c"], new Vector3(5, 0, 0)),
            Quad(1, 1, ["j_b", "j_c"], new Vector3(10, 0, 0), startIndex: 6));

        var build = SkinMeshBuilder.Build(DecalSurface.Body, MaterialPath, "chara/x/--c0801b0001_base.tex",
        [
            Source("chara/human/c0801/obj/body/b0001/model/c0801b0001_top.mdl", body, 801),
            Source("chara/equipment/e0001/model/c0201e0001_top.mdl", gear, 201),
        ]);

        Assert.NotNull(build);
        var mesh = build.Mesh;
        Assert.Equal(DecalSurface.Body, mesh.Surface);
        Assert.Equal("chara/human/c0801/obj/body/b0001/model/c0801b0001_top.mdl", mesh.ModelGamePath);
        Assert.Equal(MaterialPath, mesh.MaterialGamePath);
        Assert.Equal("chara/x/--c0801b0001_base.tex", mesh.DiffuseGamePath);
        Assert.Equal(8, mesh.VertexCount);
        Assert.Equal(4, mesh.TriangleCount);
        Assert.Equal(new[] { "j_a", "j_b", "j_c" }, mesh.BoneNames);

        // The gear quad's indices are offset by the body's four vertices; the gear's non-skin quad is left out.
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3, 4, 5, 6, 6, 5, 7 }, mesh.Indices);
        Assert.Equal(new Vector3(10, 0, 0), mesh.Positions[4]);

        // Body vertex 2 → local bone 1 (j_b) → merged 1; gear vertex 0 → local bone 0 (j_b) → merged 1; gear vertex 2 → j_c → merged 2.
        Assert.Equal(1, mesh.BoneIndices[4 * 2]);
        Assert.Equal(1, mesh.BoneIndices[4 * 4]);
        Assert.Equal(2, mesh.BoneIndices[4 * 6]);
        Assert.Equal(8 * 4, mesh.BoneWeights.Length);
        Assert.All(Enumerable.Range(0, 8), v => Assert.Equal(1f, mesh.BoneWeights.AsSpan(4 * v, 4).ToArray().Sum(), 4));

        // One source per model, in order, with its offsets into the merged mesh and its own (local) geometry.
        Assert.Equal(2, build.Sources.Count);
        var bodySource = build.Sources[0];
        var gearSource = build.Sources[1];
        Assert.Equal(0, bodySource.VertexOffset);
        Assert.Equal(0, bodySource.TriangleOffset);
        Assert.Equal((ushort)801, bodySource.Race);
        Assert.Equal(4, gearSource.VertexOffset);
        Assert.Equal(2, gearSource.TriangleOffset);
        Assert.Equal((ushort)201, gearSource.Race);
        Assert.Equal((ushort)201, gearSource.Mesh.GenderRace);
        Assert.Equal("chara/equipment/e0001/model/c0201e0001_top.mdl", gearSource.ModelGamePath);
        Assert.Equal(4, gearSource.Mesh.VertexCount);
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3 }, gearSource.Mesh.Triangles);
        Assert.Equal(gearSource.Mesh.Triangles, gearSource.Mesh.BaseTriangles);
        Assert.Equal(new[] { "j_b", "j_c" }, gearSource.Mesh.BoneNames);
        Assert.Equal(new ushort[] { 1, 1 }, gearSource.Mesh.TriangleMaterials);
        Assert.Same(gear.Materials, gearSource.Mesh.MaterialNames);
        Assert.Equal(4 * MdlMesh.Influences, gearSource.Mesh.BoneIndices.Length);
        Assert.Equal(1, gearSource.Mesh.BoneIndices[2 * MdlMesh.Influences]); // gear vertex 2 → j_c, the source's local bone 1
        Assert.Equal(1f, gearSource.Mesh.BoneWeights[2 * MdlMesh.Influences]);

        // A hit on source triangle t is merged triangle TriangleOffset + t over the same vertices (VertexOffset + local).
        for (var t = 0; t < 2; t++)
        {
            for (var corner = 0; corner < 3; corner++)
                Assert.Equal(mesh.Indices[3 * (gearSource.TriangleOffset + t) + corner], gearSource.VertexOffset + gearSource.Mesh.Triangles[3 * t + corner]);
        }
    }

    [Fact]
    public void MergedMesh_KeepsTheFourLargestInfluences_SourceKeepsAllEight()
    {
        var names   = Enumerable.Range(0, 8).Select(i => $"j_{i}").ToArray();
        var weights = new float[3 * MdlMesh.Influences];
        var bones   = new byte[3 * MdlMesh.Influences];
        for (var k = 0; k < 8; k++)
        {
            weights[k] = (10 + 10 * k) / 255f;
            bones[k]   = (byte)k;
        }

        weights[MdlMesh.Influences]     = 1f; // vertex 1: bone 0
        weights[2 * MdlMesh.Influences] = 1f; // vertex 2: bone 0

        var positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var mesh = new MdlMesh
        {
            MeshIndex     = 0,
            MaterialIndex = 0,
            StartIndex    = 0,
            BoneNames     = names,
            Skinned       = true,
            Positions     = positions,
            Normals       = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Uvs           = [Vector2.Zero, Vector2.Zero, Vector2.Zero],
            BoneIndices   = bones,
            BoneWeights   = weights,
            Indices       = [0, 1, 2],
            BaseIndices   = [0, 1, 2],
        };

        var build = SkinMeshBuilder.Build(DecalSurface.Body, MaterialPath, "x.tex", [Source("chara/body.mdl", Model([SkinMaterial], names, mesh))]);
        Assert.NotNull(build);

        // Merged: the four largest (80, 70, 60, 50 of 10..80), renormalised.
        Assert.Equal(new byte[] { 7, 6, 5, 4 }, build.Mesh.BoneIndices.AsSpan(0, 4).ToArray());
        Assert.Equal(80f / 260f, build.Mesh.BoneWeights[0], 4);
        Assert.Equal(50f / 260f, build.Mesh.BoneWeights[3], 4);
        Assert.Equal(1f, build.Mesh.BoneWeights.AsSpan(0, 4).ToArray().Sum(), 4);
        Assert.Equal(1f, build.Mesh.BoneWeights[4]);
        Assert.Equal(0, build.Mesh.BoneIndices[4]);

        // Source: all eight as stored.
        var source = Assert.Single(build.Sources);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (ushort)i).ToArray(), source.Mesh.BoneIndices.AsSpan(0, 8).ToArray());
        for (var k = 0; k < 8; k++)
            Assert.Equal((10 + 10 * k) / 255f, source.Mesh.BoneWeights[k], 5);
        Assert.Equal(1f, source.Mesh.BoneWeights[MdlMesh.Influences]);
        Assert.Equal(0f, source.Mesh.BoneWeights[MdlMesh.Influences + 1]);
    }

    [Fact]
    public void Deform_MovesTheMergedRestPose_AndLeavesTheSourceAuthored()
    {
        var body   = Model([SkinMaterial], ["j_a", "j_b"], Quad(0, 0, ["j_a", "j_b"], Vector3.Zero));
        var deform = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal) { ["j_a"] = Matrix4x4.CreateTranslation(0, 0, 1) };
        var build  = SkinMeshBuilder.Build(DecalSurface.Body, MaterialPath, "x.tex", [Source("chara/equipment/e0001/model/c0201e0001_top.mdl", body, 201, deform)]);

        Assert.NotNull(build);
        Assert.Equal(new Vector3(0, 0, 1), build.Mesh.Positions[0]);
        Assert.Equal(new Vector3(1, 0, 1), build.Mesh.Positions[1]);
        Assert.Equal(new Vector3(0, 1, 0), build.Mesh.Positions[2]); // j_b is not listed by the deformer: identity
        Assert.All(build.Mesh.Normals, n => Assert.Equal(Vector3.UnitZ, n));

        var source = Assert.Single(build.Sources);
        Assert.Equal(Vector3.Zero, source.Mesh.Positions[0]);
        Assert.Equal((ushort)201, source.Race);
    }

    [Fact]
    public void ReturnsNull_WhenNoMeshUsesTheMaterial()
    {
        var gear = Model([OtherMaterial], ["j_a"], Quad(0, 0, ["j_a"], Vector3.Zero));
        Assert.Null(SkinMeshBuilder.Build(DecalSurface.Body, MaterialPath, "x.tex", [Source("chara/gear.mdl", gear)]));
        Assert.Null(SkinMeshBuilder.Build(DecalSurface.Body, MaterialPath, "x.tex", []));
    }

    [Fact]
    public void UnskinnedMesh_FollowsTheFallbackBone_InTheMergedMesh_AndTheRootInTheSource()
    {
        var model = Model([SkinMaterial], [], Quad(0, 0, [], Vector3.Zero));
        var build = SkinMeshBuilder.Build(DecalSurface.Face, MaterialPath, "x.tex", [Source("chara/face.mdl", model)]);
        Assert.NotNull(build);
        Assert.Equal(new[] { SkinMeshBuilder.FallbackBone }, build.Mesh.BoneNames);
        Assert.All(Enumerable.Range(0, 4), v => Assert.Equal(1f, build.Mesh.BoneWeights[4 * v]));

        var source = Assert.Single(build.Sources);
        Assert.Equal(new[] { SkinMeshBuilder.FallbackBone }, source.Mesh.BoneNames);
        Assert.All(source.Mesh.BoneWeights, w => Assert.Equal(0f, w));
    }

    [Theory]
    [InlineData("/mt_c0101b0001_a.mtrl", "mt_c0101b0001_a.mtrl", true)]
    [InlineData("/mt_c0201b0001_a.mtrl", "mt_c0801b0001_a.mtrl", true)] // gear names the Midlander material; the game swaps the race code
    [InlineData("/mt_c0101b0001_a.mtrl", "mt_c****b0001_a.mtrl", true)] // a key
    [InlineData("chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl", "MT_C0101B0001_A.mtrl", true)]
    [InlineData("C:\\mods\\body\\mt_c0101b0001_a.mtrl", "mt_c0101b0001_a.mtrl", true)]
    [InlineData("/mt_c0101b0001_b.mtrl", "mt_c0101b0001_a.mtrl", false)]
    [InlineData("/mt_c0101b0001_bibo.mtrl", "mt_c0801b0001_a.mtrl", false)]
    [InlineData("", "mt_c0101b0001_a.mtrl", false)]
    public void MaterialMatches_ComparesFileNames_IgnoringTheRaceCode(string modelMaterial, string fileName, bool expected)
        => Assert.Equal(expected, SkinMeshBuilder.MaterialMatches(modelMaterial, fileName));

    [Theory]
    [InlineData("chara/human/c0801/obj/body/b0001/material/v0001/mt_c0801b0001_a.mtrl", "mt_c****b0001_a.mtrl")]
    [InlineData("/mt_c0201b0001_bibo.mtrl", "mt_c****b0001_bibo.mtrl")]
    [InlineData("MT_C0101F0001_FAC_A.mtrl", "mt_c****f0001_fac_a.mtrl")]
    [InlineData("mt_c****b0001_a.mtrl", "mt_c****b0001_a.mtrl")]
    public void SkinMaterialKey_NeutralisesTheRaceCode(string material, string expected)
        => Assert.Equal(expected, SkinMeshBuilder.SkinMaterialKey(material));

    [Theory]
    [InlineData("/mt_c0201b0001_b.mtrl", "skin.shpk", DecalSurface.Body)]
    [InlineData("/skin.mtrl", "skin.shpk", DecalSurface.Body)]                                   // freely named outfit skin
    [InlineData("chara/equipment/e6193/material/v0001/mt_c0201e6193_top_skin.mtrl", "skin.shpk", DecalSurface.Body)]
    [InlineData("/mt_c0201f0001_fac_a.mtrl", "skin.shpk", DecalSurface.Face)]
    [InlineData("chara/human/c0801/obj/face/f0002/material/custom.mtrl", "skin.shpk", DecalSurface.Face)]
    [InlineData("/mt_c0201b0001_eve-piercing.mtrl", "character.shpk", null)]                      // named like skin, not skin
    [InlineData("/mt_c0201e6193_top_a.mtrl", "characterlegacy.shpk", null)]
    [InlineData("/mt_c0201b0001_b.mtrl", null, DecalSurface.Body)]                                // no shader known: by name
    [InlineData("/skin.mtrl", null, null)]
    public void ClassifySkinMaterial_UsesTheShaderWhenKnown(string material, string? shader, DecalSurface? expected)
        => Assert.Equal(expected, SkinMeshBuilder.ClassifySkinMaterial(material, shader));

    [Theory]
    [InlineData("/mt_c0201b0001_b.mtrl", "mt_c****b0001_b.mtrl|skin.mtrl", true)]
    [InlineData("/skin.mtrl", "mt_c****b0001_b.mtrl|skin.mtrl", true)]
    [InlineData("/mt_c0201b0001_bibo.mtrl", "mt_c****b0001_b.mtrl|skin.mtrl", false)]
    public void MaterialMatches_AcceptsSeveralKeys(string modelMaterial, string keys, bool expected)
        => Assert.Equal(expected, SkinMeshBuilder.MaterialMatches(modelMaterial, keys));

    [Theory]
    [InlineData("chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl", DecalSurface.Body)]
    [InlineData("chara/human/c1801/obj/body/b0001/material/v0001/mt_c1801b0001_bibo.mtrl", DecalSurface.Body)]
    [InlineData("/mt_c0201b0001_a.mtrl", DecalSurface.Body)]
    [InlineData("chara/human/c0101/obj/face/f0001/material/mt_c0101f0001_fac_a.mtrl", DecalSurface.Face)]
    [InlineData("/mt_c0801f0001_fac_a.mtrl", DecalSurface.Face)]
    [InlineData("chara/human/c0101/obj/face/f0001/material/mt_c0101f0001_iri_a.mtrl", null)]
    [InlineData("chara/human/c0101/obj/face/f0001/material/mt_c0101f0001_etc_a.mtrl", null)]
    [InlineData("chara/human/c0101/obj/hair/h0001/material/v0001/mt_c0101h0001_hir_a.mtrl", null)]
    [InlineData("chara/human/c0101/obj/tail/t0001/material/v0001/mt_c0101t0001_a.mtrl", null)]
    [InlineData("chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl", null)]
    [InlineData("/mt_c0201e0233_top_a.mtrl", null)]
    [InlineData("chara/weapon/w0401/obj/body/b0066/material/v0001/mt_w0401b0066_a.mtrl", null)]
    [InlineData("chara/monster/m0001/obj/body/b0001/material/v0001/mt_m0001b0001_a.mtrl", null)]
    [InlineData("chara/human/c0101/obj/body/b0001/texture/c0101b0001_d.tex", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ClassifySkinMaterial_FindsBodyAndFaceSkin(string? path, DecalSurface? expected)
        => Assert.Equal(expected, SkinMeshBuilder.ClassifySkinMaterial(path));

    [Theory]
    [InlineData("chara/equipment/e0233/model/c0201e0233_top.mdl", 201)]
    [InlineData("chara/human/c0801/obj/face/f0001/model/c0801f0001_fac.mdl", 801)]
    [InlineData("chara/accessory/a0101/model/c1801a0101_ear.mdl", 1801)]
    [InlineData("C:\\mods\\Eve\\Body - Default.mdl", 0)]
    [InlineData("chara/xls/boneDeformer/human.pbd", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void RaceOf_ReadsTheRaceCode(string? path, int expected)
        => Assert.Equal((ushort)expected, SkinMeshBuilder.RaceOf(path));
}
