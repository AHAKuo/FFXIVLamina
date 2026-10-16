using System.Numerics;
using System.Text;
using Lamina.Decals.Mesh;
using Xunit;

namespace Lamina.Tests.Decals;

/// <summary>
/// Writes a minimal but layout-exact .mdl (one LOD, one mesh of three vertices, two bones, one material, one submesh) in the
/// version 5 (fixed bone tables, 4 weights) or version 6 (Dawntrail bone tables, 8 weights, half floats) layout. With
/// <c>withShape</c> the mesh gets a fourth, spare vertex and one shape key ("shpx_test") that rewires index slot 2 to it. With
/// <c>withAttributes</c> the mesh gets a fourth vertex and a second triangle (2, 1, 3) in a second submesh that requires the
/// attribute "atr_nek" (bit 0); the first submesh has mask 0.
/// </summary>
internal static class SyntheticMdl
{
    public const string ShapeName     = "shpx_test";
    public const string AttributeName = "atr_nek";

    public static readonly Vector3[] Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)];
    public static readonly Vector2[] Uvs       = [new(0.1f, 0.2f), new(0.9f, 0.2f), new(0.1f, 0.8f), new(0.9f, 0.8f)];
    public static readonly Vector3   Normal    = new(0, 0, 1);

    /// <summary> Eight-weight payload of vertex 2: weights 10..80 alternating between bone 0 and bone 1. </summary>
    public static readonly byte[] EightWeights = [10, 20, 30, 40, 50, 60, 70, 80];
    public static readonly byte[] EightBones   = [0, 1, 0, 1, 0, 1, 0, 1];

    public static byte[] Build(uint version, bool eightWeights, bool withShape = false, bool withAttributes = false)
    {
        var  vertexCount = withShape || withAttributes ? 4 : 3;
        var  indexCount  = withAttributes ? 6 : 3;
        var  weightSize  = eightWeights ? 8 : 4;
        var  stride0     = 12 + 2 * weightSize;             // position + weights + indices
        var  stride1     = eightWeights ? 12 : 20;          // half4 normal + half2 uv  |  single3 normal + single2 uv
        byte weightType  = eightWeights ? (byte)17 : (byte)8;
        byte indexType   = eightWeights ? (byte)17 : (byte)5;
        byte normalType  = eightWeights ? (byte)14 : (byte)2;
        byte uvType      = eightWeights ? (byte)13 : (byte)1;
        var  normalSize  = eightWeights ? 8 : 12;

        // ---- runtime block ----
        using var runtime = new MemoryStream();
        var       w       = new BinaryWriter(runtime);

        // String block grouped the way the game writes it: attributes, bones, material, shapes.
        var strings = new List<string>();
        if (withAttributes)
            strings.Add(AttributeName);
        strings.AddRange(["j_a", "j_b", "/mt_test_a.mtrl"]);
        if (withShape)
            strings.Add(ShapeName);
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var bytes   = 0;
        foreach (var s in strings)
        {
            offsets[s] = (uint)bytes;
            bytes     += Encoding.UTF8.GetByteCount(s) + 1;
        }

        w.Write((ushort)strings.Count);
        w.Write((ushort)0);
        w.Write((uint)bytes);
        foreach (var s in strings)
        {
            w.Write(Encoding.UTF8.GetBytes(s));
            w.Write((byte)0);
        }

        // model header (56 bytes)
        w.Write(1f);          // radius
        w.Write((ushort)1);   // mesh count
        w.Write((ushort)(withAttributes ? 1 : 0)); // attribute count
        w.Write((ushort)(withAttributes ? 2 : 1)); // submesh count
        w.Write((ushort)1);   // material count
        w.Write((ushort)2);   // bone count
        w.Write((ushort)1);   // bone table count
        w.Write((ushort)(withShape ? 1 : 0)); // shape count
        w.Write((ushort)(withShape ? 1 : 0)); // shape mesh count
        w.Write((ushort)(withShape ? 1 : 0)); // shape value count
        w.Write((byte)1);     // lod count
        w.Write((byte)0);     // flags1
        w.Write((ushort)0);   // element id count
        w.Write((byte)0);     // terrain shadow mesh count
        w.Write((byte)0);     // flags2
        w.Write(0f);          // model clip out distance
        w.Write(0f);          // shadow clip out distance
        w.Write((ushort)0);   // culling grid count
        w.Write((ushort)0);   // terrain shadow submesh count
        w.Write((byte)0);     // flags3
        w.Write((byte)0);     // bg change material index
        w.Write((byte)0);     // bg crest change material index
        w.Write((byte)0);     // neck morph count
        w.Write((ushort)(version == MdlReader.Version6 ? 2 : 0)); // bone table array count total
        w.Write((ushort)0);   // unknown8
        w.Write((ushort)0);   // unknown9
        w.Write(new byte[6]); // padding

        // lods: 3 x 60 bytes, only LOD 0 populated
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write(new byte[56]);
        w.Write(new byte[60]);
        w.Write(new byte[60]);

        // mesh (36 bytes)
        w.Write((ushort)vertexCount);
        w.Write((ushort)0);   // padding
        w.Write((uint)indexCount);
        w.Write((ushort)0);   // material index
        w.Write((ushort)0);   // submesh index
        w.Write((ushort)(withAttributes ? 2 : 1)); // submesh count
        w.Write((ushort)0);   // bone table index
        w.Write((uint)0);     // start index
        w.Write((uint)0);                           // vertex buffer offset, stream 0
        w.Write((uint)(vertexCount * stride0));     // stream 1
        w.Write((uint)0);                           // stream 2
        w.Write((byte)stride0);
        w.Write((byte)stride1);
        w.Write((byte)0);
        w.Write((byte)2);     // stream count

        if (withAttributes)
            w.Write(offsets[AttributeName]); // attribute name offsets

        // Submeshes: IndexOffset (absolute in the LOD's index buffer), IndexCount, AttributeIndexMask, BoneStartIndex, BoneCount.
        WriteSubmesh(w, 0, 3, 0);            // the first triangle, always drawn
        if (withAttributes)
            WriteSubmesh(w, 3, 3, 1);        // the second needs attribute bit 0 (atr_nek)

        w.Write(offsets["/mt_test_a.mtrl"]); // material name offset
        w.Write(offsets["j_a"]);             // bone name offsets
        w.Write(offsets["j_b"]);

        if (version == MdlReader.Version5)
        {
            for (var k = 0; k < 64; k++)
                w.Write((ushort)(k < 2 ? k : 0));
            w.Write((uint)2);
        }
        else
        {
            w.Write((ushort)1); // offset in 4-byte units from this header
            w.Write((ushort)2); // count
            w.Write((ushort)0);
            w.Write((ushort)1);
        }

        if (withShape)
        {
            // Shape: string offset, ShapeMeshStartIndex[3], ShapeMeshCount[3] (LOD 0 first).
            w.Write(offsets[ShapeName]);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)0);
            w.Write((ushort)0);

            // ShapeMesh: MeshIndexOffset (the mesh's start index), ShapeValueCount, ShapeValueOffset.
            w.Write((uint)0);
            w.Write((uint)1);
            w.Write((uint)0);

            // ShapeValue: BaseIndicesIndex 2 → ReplacingVertexIndex 3.
            w.Write((ushort)2);
            w.Write((ushort)3);
        }

        // tail the reader never touches: submesh bone map, padding, bounding boxes (4 + 2 bones)
        w.Write((uint)0);
        w.Write((byte)0);
        w.Write(new byte[6 * 32]);

        var runtimeBytes = runtime.ToArray();

        // ---- vertex / index data ----
        using var data = new MemoryStream();
        var       d    = new BinaryWriter(data);
        for (var v = 0; v < vertexCount; v++)
        {
            d.Write(Positions[v].X);
            d.Write(Positions[v].Y);
            d.Write(Positions[v].Z);
            var (weights, bones) = Skin(v, eightWeights);
            d.Write(weights);
            d.Write(bones);
        }

        for (var v = 0; v < vertexCount; v++)
        {
            if (eightWeights)
            {
                WriteHalf(d, Normal.X);
                WriteHalf(d, Normal.Y);
                WriteHalf(d, Normal.Z);
                WriteHalf(d, 0f);
                WriteHalf(d, Uvs[v].X);
                WriteHalf(d, Uvs[v].Y);
            }
            else
            {
                d.Write(Normal.X);
                d.Write(Normal.Y);
                d.Write(Normal.Z);
                d.Write(Uvs[v].X);
                d.Write(Uvs[v].Y);
            }
        }

        var vertexBufferSize = (uint)data.Length;
        d.Write((ushort)0);
        d.Write((ushort)1);
        d.Write((ushort)2);
        if (withAttributes)
        {
            d.Write((ushort)2);
            d.Write((ushort)1);
            d.Write((ushort)3);
        }

        var dataBytes = data.ToArray();

        // ---- file ----
        const int stackSize = 17 * 8;
        var       dataStart = (uint)(0x44 + stackSize + runtimeBytes.Length);
        using var file      = new MemoryStream();
        var       f         = new BinaryWriter(file);
        f.Write(version);
        f.Write((uint)stackSize);
        f.Write((uint)runtimeBytes.Length);
        f.Write((ushort)1); // vertex declaration count
        f.Write((ushort)1); // material count
        f.Write(dataStart);
        f.Write((uint)0);
        f.Write((uint)0);
        f.Write(dataStart + vertexBufferSize);
        f.Write((uint)0);
        f.Write((uint)0);
        f.Write(vertexBufferSize);
        f.Write((uint)0);
        f.Write((uint)0);
        f.Write((uint)(indexCount * 2));
        f.Write((uint)0);
        f.Write((uint)0);
        f.Write((byte)1); // lod count
        f.Write((byte)0);
        f.Write((byte)0);
        f.Write((byte)0);
        Assert.Equal(0x44, file.Length);

        // vertex declaration: 17 slots of 8 bytes
        WriteElement(f, 0, 0, 2, 0);                                // position, single3
        WriteElement(f, 0, 12, weightType, 1);                      // blend weights
        WriteElement(f, 0, (byte)(12 + weightSize), indexType, 2);  // blend indices
        WriteElement(f, 1, 0, normalType, 3);                       // normal
        WriteElement(f, 1, (byte)normalSize, uvType, 4);            // uv
        f.Write((byte)0xFF);
        f.Write(new byte[stackSize - 5 * 8 - 1]);
        Assert.Equal(0x44 + stackSize, file.Length);

        f.Write(runtimeBytes);
        Assert.Equal(dataStart, (uint)file.Length);
        f.Write(dataBytes);
        return file.ToArray();
    }

    private static (byte[] Weights, byte[] Bones) Skin(int vertex, bool eight)
    {
        if (eight)
        {
            return vertex switch
            {
                0 => ([255, 0, 0, 0, 0, 0, 0, 0], [1, 0, 0, 0, 0, 0, 0, 0]),
                1 => ([128, 128, 0, 0, 0, 0, 0, 0], [0, 1, 0, 0, 0, 0, 0, 0]),
                2 => (EightWeights, EightBones),
                _ => ([255, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]),
            };
        }

        return vertex switch
        {
            0 => ([255, 0, 0, 0], [1, 0, 0, 0]),
            1 => ([128, 128, 0, 0], [0, 1, 0, 0]),
            2 => ([64, 191, 0, 0], [0, 1, 0, 0]),
            _ => ([255, 0, 0, 0], [0, 0, 0, 0]),
        };
    }

    private static void WriteElement(BinaryWriter w, byte stream, byte offset, byte type, byte usage)
    {
        w.Write(stream);
        w.Write(offset);
        w.Write(type);
        w.Write(usage);
        w.Write((byte)0); // usage index
        w.Write(new byte[3]);
    }

    private static void WriteHalf(BinaryWriter w, float value)
        => w.Write(BitConverter.HalfToInt16Bits((Half)value));

    private static void WriteSubmesh(BinaryWriter w, uint indexOffset, uint indexCount, uint attributeMask)
    {
        w.Write(indexOffset);
        w.Write(indexCount);
        w.Write(attributeMask);
        w.Write((ushort)0); // bone start index
        w.Write((ushort)0); // bone count
    }
}

public class MdlReaderTests
{
    [Theory]
    [InlineData(MdlReader.Version6, true)]
    [InlineData(MdlReader.Version5, false)]
    public void Reads_Geometry_Materials_Bones_And_EightInfluences(uint version, bool eightWeights)
    {
        var geometry = MdlReader.Read(SyntheticMdl.Build(version, eightWeights));

        Assert.Equal(version, geometry.Version);
        Assert.Equal(new[] { "/mt_test_a.mtrl" }, geometry.Materials);
        Assert.Equal(new[] { "j_a", "j_b" }, geometry.Bones);
        Assert.Empty(geometry.Shapes);

        var mesh = Assert.Single(geometry.Lod0Meshes);
        Assert.Equal(0, mesh.MeshIndex);
        Assert.Equal(0, mesh.MaterialIndex);
        Assert.Equal(0, mesh.StartIndex);
        Assert.True(mesh.Skinned);
        Assert.Equal(new[] { "j_a", "j_b" }, mesh.BoneNames);
        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(new[] { 0, 1, 2 }, mesh.Indices);
        Assert.Same(mesh.Indices, mesh.BaseIndices);
        Assert.Equal(3 * MdlMesh.Influences, mesh.BoneIndices.Length);
        Assert.Equal(3 * MdlMesh.Influences, mesh.BoneWeights.Length);

        for (var v = 0; v < 3; v++)
        {
            Assert.True(Vector3.Distance(SyntheticMdl.Positions[v], mesh.Positions[v]) < 1e-5f);
            Assert.True(Vector3.Distance(SyntheticMdl.Normal, mesh.Normals[v]) < 1e-3f);
            Assert.True(Vector2.Distance(SyntheticMdl.Uvs[v], mesh.Uvs[v]) < 1e-3f, $"uv {v}: {mesh.Uvs[v]}");
        }

        // vertex 0: bone j_b only, the other slots padded with zero weight
        Assert.Equal(1f, mesh.BoneWeights[0], 3);
        Assert.Equal(1, mesh.BoneIndices[0]);
        Assert.All(Enumerable.Range(1, MdlMesh.Influences - 1), k => Assert.Equal(0f, mesh.BoneWeights[k]));

        // vertex 1: half / half, as stored
        var v1 = Slots(mesh, 1);
        Assert.Equal(2, v1.Length);
        Assert.Equal(0, v1[0].Bone);
        Assert.Equal(128f / 255f, v1[0].Weight, 3);
        Assert.Equal(1, v1[1].Bone);
        Assert.Equal(128f / 255f, v1[1].Weight, 3);

        // vertex 2
        var offset = 2 * MdlMesh.Influences;
        if (eightWeights)
        {
            // all eight influences, in slot order, raw weights
            Assert.Equal(SyntheticMdl.EightBones, mesh.BoneIndices.AsSpan(offset, 8).ToArray());
            for (var k = 0; k < 8; k++)
                Assert.Equal(SyntheticMdl.EightWeights[k] / 255f, mesh.BoneWeights[offset + k], 3);
        }
        else
        {
            var v2 = Slots(mesh, 2);
            Assert.Equal(2, v2.Length);
            Assert.Equal(0, v2[0].Bone);
            Assert.Equal(64f / 255f, v2[0].Weight, 3);
            Assert.Equal(1, v2[1].Bone);
            Assert.Equal(191f / 255f, v2[1].Weight, 3);
            Assert.All(Enumerable.Range(2, MdlMesh.Influences - 2), k => Assert.Equal(0f, mesh.BoneWeights[offset + k]));
        }
    }

    [Theory]
    [InlineData(MdlReader.Version6, true)]
    [InlineData(MdlReader.Version5, false)]
    public void Reads_Shapes_And_ApplyShapes_RewiresEnabledSlots(uint version, bool eightWeights)
    {
        var geometry = MdlReader.Read(SyntheticMdl.Build(version, eightWeights, withShape: true));

        var shape = Assert.Single(geometry.Shapes);
        Assert.Equal(SyntheticMdl.ShapeName, shape.Name);
        var entry = Assert.Single(shape.Meshes);
        Assert.Equal(0u, entry.MeshIndexOffset);
        Assert.Equal(new MdlShapeValue(2, 3), Assert.Single(entry.Values));

        // The spare vertex is part of the mesh, the base triangle does not use it.
        var mesh = Assert.Single(geometry.Lod0Meshes);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(new[] { 0, 1, 2 }, mesh.Indices);
        Assert.True(Vector3.Distance(SyntheticMdl.Positions[3], mesh.Positions[3]) < 1e-5f);

        var shaped     = MdlReader.ApplyShapes(geometry, new[] { SyntheticMdl.ShapeName });
        var shapedMesh = Assert.Single(shaped.Lod0Meshes);
        Assert.NotSame(geometry, shaped);
        Assert.Equal(new[] { 0, 1, 3 }, shapedMesh.Indices);
        Assert.Equal(new[] { 0, 1, 2 }, shapedMesh.BaseIndices);
        Assert.Same(mesh.Positions, shapedMesh.Positions);
        Assert.Same(geometry.Shapes, shaped.Shapes);

        // Nothing enabled, or an unknown shape: the same geometry comes back.
        Assert.Same(geometry, MdlReader.ApplyShapes(geometry, null));
        Assert.Same(geometry, MdlReader.ApplyShapes(geometry, Array.Empty<string>()));
        Assert.Same(geometry, MdlReader.ApplyShapes(geometry, new[] { "shpx_other" }));

        // Applying to a shaped geometry starts over from the base triangles.
        var unshaped = MdlReader.ApplyShapes(shaped, Array.Empty<string>());
        Assert.Equal(new[] { 0, 1, 2 }, Assert.Single(unshaped.Lod0Meshes).Indices);
    }

    [Fact]
    public void ApplyShapes_IgnoresValuesOutsideTheMesh()
    {
        var geometry = MdlReader.Read(SyntheticMdl.Build(MdlReader.Version6, true, withShape: true));
        var broken   = new MdlGeometry
        {
            Version    = geometry.Version,
            Materials  = geometry.Materials,
            Bones      = geometry.Bones,
            Lod0Meshes = geometry.Lod0Meshes,
            Shapes     = [new MdlShape(SyntheticMdl.ShapeName, [new MdlShapeMesh(0, [new MdlShapeValue(2, 99), new MdlShapeValue(40, 3)])])],
        };

        Assert.Same(broken, MdlReader.ApplyShapes(broken, new[] { SyntheticMdl.ShapeName }));
    }

    [Fact]
    public void Rejects_UnknownVersion()
    {
        var data = SyntheticMdl.Build(MdlReader.Version6, true);
        data[0] = 0x07;
        Assert.Throws<InvalidDataException>(() => MdlReader.Read(data));
    }

    [Fact]
    public void Rejects_TruncatedFile()
    {
        var data = SyntheticMdl.Build(MdlReader.Version6, true);
        Assert.Throws<InvalidDataException>(() => MdlReader.Read(data[..(data.Length - 40)]));
        Assert.Throws<InvalidDataException>(() => MdlReader.Read(data[..0x30]));
    }

    [Theory]
    [InlineData(MdlReader.Version6, true)]
    [InlineData(MdlReader.Version5, false)]
    public void Reads_AttributeNames_And_Submeshes(uint version, bool eightWeights)
    {
        var geometry = MdlReader.Read(SyntheticMdl.Build(version, eightWeights, withAttributes: true));

        Assert.Equal(new[] { SyntheticMdl.AttributeName }, geometry.AttributeNames);
        Assert.Null(geometry.EnabledAttributes);
        Assert.Null(geometry.EnabledShapes);

        var mesh = Assert.Single(geometry.Lod0Meshes);
        Assert.Equal(new[] { new MdlSubmesh(0, 3, 0), new MdlSubmesh(3, 3, 1) }, mesh.Submeshes);
        Assert.Empty(mesh.HiddenSubmeshes);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3 }, mesh.Indices);
        Assert.Same(mesh.Indices, mesh.BaseIndices);
        Assert.Same(mesh.Indices, mesh.FileIndices);

        // A model without attributes still carries its (always drawn) submesh table.
        var plain = MdlReader.Read(SyntheticMdl.Build(version, eightWeights));
        Assert.Empty(plain.AttributeNames);
        Assert.Equal(new[] { new MdlSubmesh(0, 3, 0) }, Assert.Single(plain.Lod0Meshes).Submeshes);
    }

    [Theory]
    [InlineData(MdlReader.Version6, true)]
    [InlineData(MdlReader.Version5, false)]
    public void ApplyAttributes_DropsTheTrianglesOfHiddenSubmeshes_AndKeepsTheVertices(uint version, bool eightWeights)
    {
        var geometry = MdlReader.Read(SyntheticMdl.Build(version, eightWeights, withAttributes: true));
        var mesh     = Assert.Single(geometry.Lod0Meshes);

        // Enabled (or no information): everything is drawn and the geometry comes back as is.
        Assert.Same(geometry, MdlReader.ApplyAttributes(geometry, new[] { SyntheticMdl.AttributeName }));
        Assert.Same(geometry, MdlReader.ApplyAttributes(geometry, new[] { SyntheticMdl.AttributeName, "atr_other" }));
        Assert.Same(geometry, MdlReader.ApplyAttributes(geometry, null));

        // Disabled: exactly the second submesh's triangle goes, from Indices and BaseIndices alike; the vertices stay.
        var hidden     = MdlReader.ApplyAttributes(geometry, Array.Empty<string>());
        var hiddenMesh = Assert.Single(hidden.Lod0Meshes);
        Assert.NotSame(geometry, hidden);
        Assert.Equal(new[] { 0, 1, 2 }, hiddenMesh.Indices);
        Assert.Same(hiddenMesh.Indices, hiddenMesh.BaseIndices);
        Assert.Equal(new[] { 1 }, hiddenMesh.HiddenSubmeshes);
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3 }, hiddenMesh.FileIndices);
        Assert.Equal(4, hiddenMesh.VertexCount);
        Assert.Same(mesh.Positions, hiddenMesh.Positions);
        Assert.Same(mesh.Submeshes, hiddenMesh.Submeshes);
        Assert.NotNull(hidden.EnabledAttributes);
        Assert.Empty(hidden.EnabledAttributes);
        Assert.Same(geometry.Shapes, hidden.Shapes);
        Assert.Same(geometry.AttributeNames, hidden.AttributeNames);

        // The same set again, or one that still lacks the attribute: nothing to do.
        Assert.Same(hidden, MdlReader.ApplyAttributes(hidden, Array.Empty<string>()));
        Assert.Same(hidden, MdlReader.ApplyAttributes(hidden, new[] { "atr_other" }));

        // Enabling it again brings the triangle back.
        var restored = MdlReader.ApplyAttributes(hidden, new[] { SyntheticMdl.AttributeName });
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3 }, Assert.Single(restored.Lod0Meshes).Indices);
        Assert.Empty(Assert.Single(restored.Lod0Meshes).HiddenSubmeshes);
        Assert.Null(restored.EnabledAttributes);
    }

    [Theory]
    [InlineData(MdlReader.Version6, true)]
    [InlineData(MdlReader.Version5, false)]
    public void ApplyAttributes_And_ApplyShapes_Combine_InEitherOrder(uint version, bool eightWeights)
    {
        var geometry = MdlReader.Read(SyntheticMdl.Build(version, eightWeights, withShape: true, withAttributes: true));
        var shapes   = new[] { SyntheticMdl.ShapeName };
        var none     = Array.Empty<string>();

        // Shape first (slot 2 -> vertex 3), then hide the second submesh.
        var shaped = MdlReader.ApplyShapes(geometry, shapes);
        Assert.Equal(new[] { 0, 1, 3, 2, 1, 3 }, Assert.Single(shaped.Lod0Meshes).Indices);
        var both = MdlReader.ApplyAttributes(shaped, none);
        var mesh = Assert.Single(both.Lod0Meshes);
        Assert.Equal(new[] { 0, 1, 3 }, mesh.Indices);
        Assert.Equal(new[] { 0, 1, 2 }, mesh.BaseIndices);
        Assert.Equal(new[] { 1 }, mesh.HiddenSubmeshes);
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3 }, mesh.FileIndices);
        Assert.NotNull(both.EnabledShapes);
        Assert.True(both.EnabledShapes.SetEquals(shapes));
        Assert.NotNull(both.EnabledAttributes);
        Assert.Empty(both.EnabledAttributes);

        // Hide first, then shape, or both in one step: the same result.
        var other = MdlReader.ApplyShapes(MdlReader.ApplyAttributes(geometry, none), shapes);
        Assert.Equal(mesh.Indices, Assert.Single(other.Lod0Meshes).Indices);
        Assert.Equal(mesh.BaseIndices, Assert.Single(other.Lod0Meshes).BaseIndices);
        var once = MdlReader.Apply(geometry, shapes, none);
        Assert.Equal(mesh.Indices, Assert.Single(once.Lod0Meshes).Indices);
        Assert.Equal(mesh.BaseIndices, Assert.Single(once.Lod0Meshes).BaseIndices);
        Assert.Equal(mesh.HiddenSubmeshes, Assert.Single(once.Lod0Meshes).HiddenSubmeshes);

        // Each step keeps the other's state: unshaping keeps the part hidden, revealing keeps the shape.
        var unshaped = MdlReader.ApplyShapes(both, none);
        Assert.Equal(new[] { 0, 1, 2 }, Assert.Single(unshaped.Lod0Meshes).Indices);
        Assert.Equal(new[] { 1 }, Assert.Single(unshaped.Lod0Meshes).HiddenSubmeshes);
        Assert.Null(unshaped.EnabledShapes);
        var revealed = MdlReader.ApplyAttributes(both, new[] { SyntheticMdl.AttributeName });
        Assert.Equal(new[] { 0, 1, 3, 2, 1, 3 }, Assert.Single(revealed.Lod0Meshes).Indices);
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3 }, Assert.Single(revealed.Lod0Meshes).BaseIndices);
        Assert.Empty(Assert.Single(revealed.Lod0Meshes).HiddenSubmeshes);
        Assert.Null(revealed.EnabledAttributes);
    }

    [Fact]
    public void ApplyAttributes_AddressesSubmeshesByAbsoluteSlot_AndKeepsUncoveredTriangles()
    {
        // A mesh starting at slot 6 of the LOD's index buffer with three triangles: a part at slots 6..8 that is always drawn,
        // a part at 9..11 that needs "atr_nek", and a triangle at 12..14 no part covers.
        var source   = Assert.Single(MdlReader.Read(SyntheticMdl.Build(MdlReader.Version6, true, withAttributes: true)).Lod0Meshes);
        var indices  = new[] { 0, 1, 2, 2, 1, 3, 3, 1, 0 };
        var mesh     = Mesh(source, indices, [new MdlSubmesh(6, 3, 0), new MdlSubmesh(9, 3, 1)]);
        var geometry = Geometry(source, mesh);

        var hidden = Assert.Single(MdlReader.ApplyAttributes(geometry, Array.Empty<string>()).Lod0Meshes);
        Assert.Equal(new[] { 0, 1, 2, 3, 1, 0 }, hidden.Indices);
        Assert.Same(hidden.Indices, hidden.BaseIndices);
        Assert.Equal(new[] { 1 }, hidden.HiddenSubmeshes);
        Assert.Same(indices, hidden.FileIndices);
        Assert.Same(geometry, MdlReader.ApplyAttributes(geometry, new[] { SyntheticMdl.AttributeName }));

        // A mask bit without a name can never be enabled: that part is never drawn.
        var orphan = Geometry(source, Mesh(source, indices, [new MdlSubmesh(6, 3, 0), new MdlSubmesh(9, 3, 2)]));
        Assert.Equal(new[] { 0, 1, 2, 3, 1, 0 }, Assert.Single(MdlReader.ApplyAttributes(orphan, new[] { SyntheticMdl.AttributeName }).Lod0Meshes).Indices);

        // A part that lies outside the mesh hides nothing.
        var outside = Geometry(source, Mesh(source, indices, [new MdlSubmesh(0, 3, 1), new MdlSubmesh(60, 3, 1)]));
        var kept    = Assert.Single(MdlReader.ApplyAttributes(outside, Array.Empty<string>()).Lod0Meshes);
        Assert.Equal(indices, kept.Indices);
        Assert.Equal(new[] { 0, 1 }, kept.HiddenSubmeshes);
    }

    private static MdlMesh Mesh(MdlMesh source, int[] indices, MdlSubmesh[] submeshes)
        => new()
        {
            MeshIndex     = 0,
            MaterialIndex = 0,
            StartIndex    = 6,
            Submeshes     = submeshes,
            BoneNames     = source.BoneNames,
            Skinned       = source.Skinned,
            Positions     = source.Positions,
            Normals       = source.Normals,
            Uvs           = source.Uvs,
            BoneIndices   = source.BoneIndices,
            BoneWeights   = source.BoneWeights,
            Indices       = indices,
            BaseIndices   = indices,
        };

    private static MdlGeometry Geometry(MdlMesh source, MdlMesh mesh)
        => new()
        {
            Version        = MdlReader.Version6,
            Materials      = ["/mt_test_a.mtrl"],
            Bones          = source.BoneNames,
            AttributeNames = [SyntheticMdl.AttributeName],
            Lod0Meshes     = [mesh],
            Shapes         = [],
        };

    private static (int Bone, float Weight)[] Slots(MdlMesh mesh, int vertex)
        => Enumerable.Range(0, MdlMesh.Influences)
            .Select(k => ((int)mesh.BoneIndices[MdlMesh.Influences * vertex + k], mesh.BoneWeights[MdlMesh.Influences * vertex + k]))
            .Where(p => p.Item2 > 0)
            .OrderBy(p => p.Item1)
            .ToArray();
}
