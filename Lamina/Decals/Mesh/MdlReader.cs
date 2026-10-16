using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Lamina.Decals.Mesh;

/// <summary> One index-buffer edit of a shape key: slot <see cref="BaseIndex"/> of the owning mesh's index range names <see cref="ReplacingVertex"/> instead. </summary>
public readonly record struct MdlShapeValue(ushort BaseIndex, ushort ReplacingVertex);

/// <summary> The edits a shape key makes to one mesh. </summary>
/// <param name="MeshIndexOffset"> First index-buffer slot of the mesh the values belong to (its <see cref="MdlMesh.StartIndex"/>); the absolute slot of a value is MeshIndexOffset + BaseIndex. </param>
/// <param name="Values"> The edits. </param>
public sealed record MdlShapeMesh(uint MeshIndexOffset, MdlShapeValue[] Values);

/// <summary> A shape key of the model (bust size, muscle, ...), LOD 0 part only. </summary>
public sealed record MdlShape(string Name, MdlShapeMesh[] Meshes);

/// <summary>
/// One part of a mesh (Lumina's SubmeshStruct): a run of index-buffer slots the game draws or hides as a whole, depending on
/// attributes. <see cref="IndexOffset"/> is absolute in the LOD's index buffer, the numbering <see cref="MdlMesh.StartIndex"/>
/// uses, so the part covers the mesh's slots [IndexOffset - StartIndex, IndexOffset - StartIndex + IndexCount). Bit i of
/// <see cref="AttributeMask"/> requires attribute <see cref="MdlGeometry.AttributeNames"/>[i]; the part is drawn iff every
/// attribute it requires is enabled ((AttributeMask &amp; ~enabledBits) == 0), so a part with mask 0 is always drawn.
/// </summary>
public readonly record struct MdlSubmesh(uint IndexOffset, uint IndexCount, uint AttributeMask);

/// <summary> Geometry of one LOD 0 mesh of a .mdl, decoded to plain arrays. </summary>
public sealed class MdlMesh
{
    /// <summary> Bone slots per vertex. The format stores 4 or 8; unused slots carry weight 0. </summary>
    public const int Influences = 8;

    /// <summary> Index of the mesh in the file. </summary>
    public required int MeshIndex { get; init; }

    /// <summary> Index into <see cref="MdlGeometry.Materials"/>. </summary>
    public required int MaterialIndex { get; init; }

    /// <summary> First slot of the mesh in the LOD's index buffer. Shape keys and submeshes address slots in that buffer. </summary>
    public required int StartIndex { get; init; }

    /// <summary>
    /// The mesh's parts in file order: the runs of its index slots the game draws or hides together and the attributes each
    /// one needs to be drawn. Empty when the file lists none for the mesh, which is then drawn whole.
    /// </summary>
    public MdlSubmesh[] Submeshes { get; init; } = [];

    /// <summary>
    /// Indices into <see cref="Submeshes"/> of the parts <see cref="MdlReader.ApplyAttributes"/> took out of <see cref="Indices"/>,
    /// ascending; empty while every part is drawn.
    /// </summary>
    public int[] HiddenSubmeshes { get; init; } = [];

    /// <summary> The mesh's bone table resolved to bone names; empty for an unskinned mesh. </summary>
    public required string[] BoneNames { get; init; }

    /// <summary> True when the mesh carries bone weights into a non-empty bone table. </summary>
    public required bool Skinned { get; init; }

    public required Vector3[] Positions { get; init; }

    /// <summary> Unit normals; computed from the triangles when the file carries none. </summary>
    public required Vector3[] Normals { get; init; }

    /// <summary>
    /// First UV set (x, y) exactly as stored in the file: nothing is flipped or wrapped. Note that the game's body skin meshes
    /// (c0101b0001_top and the skin parts of gear) keep u in the 1..2 range and rely on the sampler's repeat mode, so
    /// consumers have to wrap (u - floor(u)) before addressing texels.
    /// </summary>
    public required Vector2[] Uvs { get; init; }

    /// <summary> <see cref="Influences"/> bone slots per vertex, indices into <see cref="BoneNames"/>, valid influences first; slots with weight 0 hold 0. </summary>
    public required byte[] BoneIndices { get; init; }

    /// <summary>
    /// <see cref="Influences"/> weights per vertex as stored (they sum to about 1), zero padded. All 0 for an unskinned mesh
    /// and for a vertex none of whose bones is in the bone table.
    /// </summary>
    public required float[] BoneWeights { get; init; }

    /// <summary>
    /// Triangle list as drawn: after <see cref="MdlReader.ApplyShapes"/> the enabled shape keys are applied, after
    /// <see cref="MdlReader.ApplyAttributes"/> only the triangles of the drawn submeshes remain.
    /// </summary>
    public required int[] Indices { get; init; }

    /// <summary>
    /// For each entry of <see cref="Indices"/> the vertex it named before any shape key; the same array instance as
    /// <see cref="Indices"/> while none applies. Hidden submeshes are taken out of both, so the two stay parallel.
    /// </summary>
    public required int[] BaseIndices { get; init; }

    private readonly int[]? _fileIndices;

    /// <summary>
    /// The whole mesh as stored in the file: no shape key applied, every submesh present, entry i being index-buffer slot
    /// <see cref="StartIndex"/> + i. <see cref="MdlReader.Apply"/> starts from here. Defaults to <see cref="BaseIndices"/>,
    /// which is right for a mesh nothing has been applied to.
    /// </summary>
    public int[] FileIndices
    {
        get  => _fileIndices ?? BaseIndices;
        init => _fileIndices = value;
    }

    public int VertexCount
        => Positions.Length;

    /// <summary> The same mesh drawing <paramref name="indices"/> (shared vertex arrays and file triangles). </summary>
    internal MdlMesh With(int[] indices, int[] baseIndices, int[] hiddenSubmeshes)
        => new()
        {
            MeshIndex       = MeshIndex,
            MaterialIndex   = MaterialIndex,
            StartIndex      = StartIndex,
            Submeshes       = Submeshes,
            HiddenSubmeshes = hiddenSubmeshes,
            BoneNames       = BoneNames,
            Skinned         = Skinned,
            Positions       = Positions,
            Normals         = Normals,
            Uvs             = Uvs,
            BoneIndices     = BoneIndices,
            BoneWeights     = BoneWeights,
            Indices         = indices,
            BaseIndices     = baseIndices,
            FileIndices     = FileIndices,
        };
}

/// <summary> The parts of a .mdl the mesh part needs. </summary>
public sealed class MdlGeometry
{
    public required uint Version { get; init; }

    /// <summary> Material paths as written in the file, e.g. "/mt_c0101b0001_a.mtrl" (relative) or a full game path. </summary>
    public required string[] Materials { get; init; }

    /// <summary> Bone names referenced by the bone tables. </summary>
    public required string[] Bones { get; init; }

    /// <summary>
    /// The model's attribute names in file order (atr_nek, atr_dv_a, ...): bit i of a submesh's <see cref="MdlSubmesh.AttributeMask"/>
    /// refers to entry i. The game keeps the same name → index map on the loaded resource.
    /// </summary>
    public string[] AttributeNames { get; init; } = [];

    /// <summary> The main (not water / shadow) meshes of LOD 0. </summary>
    public required MdlMesh[] Lod0Meshes { get; init; }

    /// <summary> The model's shape keys (LOD 0 parts), in file order. </summary>
    public required MdlShape[] Shapes { get; init; }

    /// <summary> The shape keys applied to <see cref="Lod0Meshes"/> (those that rewired a slot), null when none. Set by <see cref="MdlReader.Apply"/>. </summary>
    public IReadOnlySet<string>? EnabledShapes { get; init; }

    /// <summary> The attribute set <see cref="Lod0Meshes"/> were filtered with, null while every submesh is drawn. Set by <see cref="MdlReader.Apply"/>. </summary>
    public IReadOnlySet<string>? EnabledAttributes { get; init; }

    /// <summary> The same model with other LOD 0 meshes and the sets they were built with. </summary>
    internal MdlGeometry With(MdlMesh[] meshes, IReadOnlySet<string>? enabledShapes, IReadOnlySet<string>? enabledAttributes)
        => new()
        {
            Version           = Version,
            Materials         = Materials,
            Bones             = Bones,
            AttributeNames    = AttributeNames,
            Lod0Meshes        = meshes,
            Shapes            = Shapes,
            EnabledShapes     = enabledShapes,
            EnabledAttributes = enabledAttributes,
        };
}

/// <summary>
/// Minimal .mdl reader: LOD 0 main meshes with positions, normals, the first UV set, skin weights (up to 8 per vertex), the
/// shape key tables and the attribute / submesh tables. Handles model version 5 (pre-7.0: fixed 64-entry bone tables, 4 weights) and version 6 (Dawntrail:
/// variable bone tables, up to 8 weights per vertex). Written against Penumbra.GameData's MdlFile / VertexReader, Lumina's
/// MdlStructs (shape tables) and TexTools' Mdl.cs, because the Lumina build that ships with Dalamud only parses version 5
/// bone tables and throws on the Dawntrail vertex element types.
/// </summary>
public static class MdlReader
{
    public const uint Version5 = 0x01000005;
    public const uint Version6 = 0x01000006;

    private const int FileHeaderSize           = 0x44;
    private const int ElementsPerDeclaration   = 17;
    private const int ElementIdSize            = 32;
    private const int LodSize                  = 60;
    private const int ExtraLodSize             = 40;
    private const int TerrainShadowMeshSize    = 20;
    private const int SubmeshSize              = 16;
    private const int TerrainShadowSubmeshSize = 12;
    private const int ShapeSize                = 16;
    private const int ShapeMeshSize            = 12;
    private const int ShapeValueSize           = 4;
    private const byte ExtraLodFlag            = 0x10;

    // Vertex element usages (MdlFile.VertexUsage).
    private const byte UsagePosition     = 0;
    private const byte UsageBlendWeights = 1;
    private const byte UsageBlendIndices = 2;
    private const byte UsageNormal       = 3;
    private const byte UsageUv           = 4;

    // Vertex element types (MdlFile.VertexType in Penumbra.GameData; TexTools calls 17 "UByte8": 8 bytes, used for 8 weights / indices).
    private const byte TypeSingle1  = 0;
    private const byte TypeSingle2  = 1;
    private const byte TypeSingle3  = 2;
    private const byte TypeSingle4  = 3;
    private const byte TypeUByte4   = 5;
    private const byte TypeShort2   = 6;
    private const byte TypeShort4   = 7;
    private const byte TypeNByte4   = 8;
    private const byte TypeNShort2  = 9;
    private const byte TypeNShort4  = 10;
    private const byte TypeNUShort2 = 11;
    private const byte TypeNUShort4 = 12;
    private const byte TypeHalf2    = 13;
    private const byte TypeHalf4    = 14;
    private const byte TypeUShort2  = 16;
    private const byte TypeUByte8   = 17;

    private readonly record struct VertexElement(byte Stream, byte Offset, byte Type, byte Usage, byte UsageIndex);

    private sealed class MeshHeader
    {
        public int    VertexCount;
        public int    IndexCount;
        public int    MaterialIndex;
        public int    SubMeshIndex;
        public int    SubMeshCount;
        public int    BoneTableIndex;
        public int    StartIndex;
        public uint[] VertexBufferOffset = new uint[3];
        public byte[] VertexBufferStride = new byte[3];
    }

    /// <summary> Parses a complete .mdl file (as stored on disk, as the game keeps it in a loaded resource, or as returned by Lumina's GetFile). </summary>
    /// <exception cref="InvalidDataException"> The file is truncated, of an unknown version or uses an unknown vertex type. </exception>
    public static MdlGeometry Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var r       = new BinaryCursor(data);
        var version = r.U32();
        if (version != Version5 && version != Version6)
            throw new InvalidDataException($"Unsupported .mdl version 0x{version:X8}.");

        r.Skip(8); // StackSize, RuntimeSize
        var declarationCount = r.U16();
        r.Skip(2); // MaterialCount, repeated in the model header
        var vertexOffset = ReadU32s(r, 3);
        var indexOffset  = ReadU32s(r, 3);
        r.Skip(2 * 3 * 4); // vertex / index buffer sizes
        var lodCount = r.U8();
        r.Position = FileHeaderSize;

        var declarations = new VertexElement[declarationCount][];
        for (var d = 0; d < declarationCount; d++)
        {
            var start    = r.Position;
            var elements = new List<VertexElement>(ElementsPerDeclaration);
            for (var e = 0; e < ElementsPerDeclaration; e++)
            {
                var stream = r.U8();
                if (stream == 0xFF)
                    break;

                var offset     = r.U8();
                var type       = r.U8();
                var usage      = r.U8();
                var usageIndex = r.U8();
                r.Skip(3);
                elements.Add(new VertexElement(stream, offset, type, usage, usageIndex));
            }

            r.Position      = start + ElementsPerDeclaration * 8;
            declarations[d] = elements.ToArray();
        }

        // String table: count, padding, byte size, data.
        r.Skip(2);
        r.Skip(2);
        var stringSize  = (int)r.U32();
        var stringStart = r.Position;
        r.Skip(stringSize);

        // Model header (56 bytes, Lumina's MdlStructs.ModelHeader).
        r.Skip(4); // Radius
        var meshCount       = r.U16();
        var attributeCount  = r.U16();
        var submeshCount    = r.U16();
        var materialCount   = r.U16();
        var boneCount       = r.U16();
        var boneTableCount  = r.U16();
        var shapeCount      = r.U16();
        var shapeMeshCount  = r.U16();
        var shapeValueCount = r.U16();
        r.Skip(2); // LodCount, Flags1
        var elementIdCount         = r.U16();
        var terrainShadowMeshCount = r.U8();
        var flags2                 = r.U8();
        r.Skip(10); // ModelClipOutDistance, ShadowClipOutDistance, CullingGridCount
        var terrainShadowSubmeshCount = r.U16();
        r.Skip(4); // Flags3, BGChangeMaterialIndex, BGCrestChangeMaterialIndex, NeckMorphCount
        var boneTableArrayCountTotal = r.U16(); // V6: ushorts in the bone index pool behind the bone table headers
        r.Skip(10); // Unknown8, Unknown9, padding

        r.Skip(ElementIdSize * elementIdCount);

        var lods = new (int MeshIndex, int MeshCount)[3];
        for (var i = 0; i < 3; i++)
        {
            var start = r.Position;
            lods[i]    = (r.U16(), r.U16());
            r.Position = start + LodSize;
        }

        if ((flags2 & ExtraLodFlag) != 0)
            r.Skip(3 * ExtraLodSize);

        var meshes = new MeshHeader[meshCount];
        for (var m = 0; m < meshCount; m++)
        {
            var mesh = new MeshHeader();
            mesh.VertexCount = r.U16();
            r.Skip(2);
            mesh.IndexCount     = (int)r.U32();
            mesh.MaterialIndex  = r.U16();
            mesh.SubMeshIndex   = r.U16();
            mesh.SubMeshCount   = r.U16();
            mesh.BoneTableIndex = r.U16();
            mesh.StartIndex     = (int)r.U32();
            for (var s = 0; s < 3; s++)
                mesh.VertexBufferOffset[s] = r.U32();
            for (var s = 0; s < 3; s++)
                mesh.VertexBufferStride[s] = r.U8();
            r.Skip(1); // stream count + flags
            meshes[m] = mesh;
        }

        // Attribute names, then (behind the terrain shadow meshes) the submesh table: Lumina's MdlFile order, which Penumbra
        // and TexTools share. A submesh is IndexOffset u32, IndexCount u32, AttributeIndexMask u32, BoneStartIndex u16, BoneCount u16.
        var attributeNames = new string[attributeCount];
        for (var i = 0; i < attributeCount; i++)
            attributeNames[i] = r.StringAt(stringStart + r.U32());

        r.Skip(TerrainShadowMeshSize * terrainShadowMeshCount);

        var submeshes = new MdlSubmesh[submeshCount];
        for (var i = 0; i < submeshCount; i++)
        {
            submeshes[i] = new MdlSubmesh(r.U32(), r.U32(), r.U32());
            r.Skip(SubmeshSize - 12); // BoneStartIndex, BoneCount
        }

        r.Skip(TerrainShadowSubmeshSize * terrainShadowSubmeshCount);

        var materials = new string[materialCount];
        for (var i = 0; i < materialCount; i++)
            materials[i] = r.StringAt(stringStart + r.U32());

        var bones = new string[boneCount];
        for (var i = 0; i < boneCount; i++)
            bones[i] = r.StringAt(stringStart + r.U32());

        var boneTables = new ushort[boneTableCount][];
        if (version == Version5)
        {
            for (var t = 0; t < boneTableCount; t++)
            {
                var indices = new ushort[64];
                for (var k = 0; k < 64; k++)
                    indices[k] = r.U16();
                var count     = (int)Math.Min(r.U32() & 0xFF, 64u);
                boneTables[t] = indices[..count];
            }
        }
        else
        {
            // V6: an array of (offset, count) headers followed by one pool of indices; each table's indices sit at its header
            // start + offset * 4. The pool holds BoneTableArrayCountTotal ushorts (padding included).
            for (var t = 0; t < boneTableCount; t++)
            {
                var tableStart = r.Position;
                var offset     = r.U16();
                var count      = r.U16();
                var span       = r.Slice(tableStart + offset * 4L, count * 2);
                var indices    = new ushort[count];
                for (var k = 0; k < count; k++)
                    indices[k] = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2 * k, 2));
                boneTables[t] = indices;
            }

            r.Skip(boneTableArrayCountTotal * 2);
        }

        var shapes = ReadShapes(r, stringStart, shapeCount, shapeMeshCount, shapeValueCount);

        var result = new List<MdlMesh>();
        if (lodCount > 0)
        {
            var (firstMesh, lodMeshCount) = lods[0];
            for (var m = firstMesh; m < firstMesh + lodMeshCount && m < meshes.Length; m++)
            {
                var mesh = meshes[m];
                if (mesh.VertexCount == 0 || mesh.IndexCount < 3)
                    continue;

                if (m >= declarations.Length)
                    throw new InvalidDataException($"Mesh {m} has no vertex declaration.");

                var tableNames = mesh.BoneTableIndex < boneTables.Length
                    ? boneTables[mesh.BoneTableIndex].Select(i => i < bones.Length ? bones[i] : $"bone{i}").ToArray()
                    : Array.Empty<string>();
                var parts = mesh.SubMeshIndex + mesh.SubMeshCount <= submeshes.Length
                    ? submeshes[mesh.SubMeshIndex..(mesh.SubMeshIndex + mesh.SubMeshCount)]
                    : Array.Empty<MdlSubmesh>();
                result.Add(DecodeMesh(r, m, mesh, declarations[m], vertexOffset[0], indexOffset[0], tableNames, parts));
            }
        }

        return new MdlGeometry
        {
            Version        = version,
            Materials      = materials,
            Bones          = bones,
            AttributeNames = attributeNames,
            Lod0Meshes     = result.ToArray(),
            Shapes         = shapes,
        };
    }

    /// <summary>
    /// The geometry with the given shape keys applied to its LOD 0 meshes, the way the game draws them: every value of an
    /// enabled shape rewires one index-buffer slot (mesh start + base index) to its replacing vertex, bounds-checked; a slot two
    /// enabled shapes edit goes to the later shape in file order. Starts from the file triangles, so applying to an already
    /// shaped geometry replaces its shapes; submeshes hidden by <see cref="ApplyAttributes"/> stay hidden. Vertex arrays are
    /// shared with <paramref name="geometry"/>, untouched meshes are reused and the geometry itself comes back when nothing changes.
    /// </summary>
    public static MdlGeometry ApplyShapes(MdlGeometry geometry, IReadOnlyCollection<string>? enabledShapes)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return Apply(geometry, enabledShapes, geometry.EnabledAttributes);
    }

    /// <summary>
    /// The geometry whose LOD 0 meshes draw only the submeshes the game draws when exactly <paramref name="enabledAttributes"/>
    /// are enabled: a submesh stays iff every attribute its mask names is in the set (mask 0 always stays, a bit without a
    /// name never can be). The triangles of hidden submeshes are taken out of <see cref="MdlMesh.Indices"/> and
    /// <see cref="MdlMesh.BaseIndices"/> alike, the vertex arrays stay whole, and <see cref="MdlMesh.HiddenSubmeshes"/> says
    /// what went. Null means no information: every submesh is drawn. Starts from the file triangles (a submesh hidden earlier
    /// comes back once its attributes are enabled) and keeps the shape keys <see cref="ApplyShapes"/> applied.
    /// </summary>
    public static MdlGeometry ApplyAttributes(MdlGeometry geometry, IReadOnlyCollection<string>? enabledAttributes)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return Apply(geometry, geometry.EnabledShapes, enabledAttributes);
    }

    /// <summary>
    /// <see cref="ApplyShapes"/> and <see cref="ApplyAttributes"/> in one step, from the file triangles: shapes rewire slots
    /// first (a slot is a slot whether or not it is drawn), then the triangles of hidden submeshes are dropped.
    /// </summary>
    public static MdlGeometry Apply(MdlGeometry geometry, IReadOnlyCollection<string>? enabledShapes, IReadOnlyCollection<string>? enabledAttributes)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        // Shape edits by absolute slot; a slot two enabled shapes edit goes to the later shape in file order.
        var rewire = new Dictionary<long, (ushort Vertex, string Shape)>();
        if (enabledShapes is { Count: > 0 } && geometry.Shapes.Length > 0)
        {
            var enabled = enabledShapes as IReadOnlySet<string> ?? new HashSet<string>(enabledShapes, StringComparer.Ordinal);
            foreach (var shape in geometry.Shapes)
            {
                if (!enabled.Contains(shape.Name))
                    continue;

                foreach (var entry in shape.Meshes)
                {
                    foreach (var value in entry.Values)
                        rewire[entry.MeshIndexOffset + (long)value.BaseIndex] = (value.ReplacingVertex, shape.Name);
                }
            }
        }

        // The enabled attributes as bits in the file's numbering; a name the file does not have enables nothing.
        HashSet<string>? attributes  = null;
        var              enabledBits = 0u;
        if (enabledAttributes is not null)
        {
            attributes = new HashSet<string>(enabledAttributes, StringComparer.Ordinal);
            for (var i = 0; i < geometry.AttributeNames.Length && i < 32; i++)
            {
                if (attributes.Contains(geometry.AttributeNames[i]))
                    enabledBits |= 1u << i;
            }
        }

        var meshes    = new MdlMesh[geometry.Lod0Meshes.Length];
        var effective = new HashSet<string>(StringComparer.Ordinal); // the shapes that rewired a slot of some mesh
        var anyHidden = false;
        var changed   = false;
        for (var m = 0; m < meshes.Length; m++)
        {
            var    mesh   = geometry.Lod0Meshes[m];
            var    file   = mesh.FileIndices;
            int[]? shaped = null;
            if (rewire.Count > 0)
            {
                for (var i = 0; i < file.Length; i++)
                {
                    if (!rewire.TryGetValue(mesh.StartIndex + (long)i, out var edit) || edit.Vertex >= mesh.VertexCount || edit.Vertex == file[i])
                        continue;

                    shaped    ??= (int[])file.Clone();
                    shaped[i] =   edit.Vertex;
                    effective.Add(edit.Shape);
                }
            }

            var hidden      = attributes is null ? [] : HiddenSubmeshes(mesh, enabledBits);
            var baseIndices = file;
            var indices     = shaped ?? file;
            if (hidden.Length > 0)
            {
                anyHidden = true;
                var drawn = DrawnSlots(mesh, hidden, file.Length);
                if (drawn is not null)
                {
                    baseIndices = Filter(file, drawn);
                    indices     = shaped is null ? baseIndices : Filter(shaped, drawn);
                }
            }

            if (SameContent(indices, mesh.Indices) && SameContent(baseIndices, mesh.BaseIndices) && hidden.AsSpan().SequenceEqual(mesh.HiddenSubmeshes))
            {
                meshes[m] = mesh;
            }
            else
            {
                meshes[m] = mesh.With(indices, baseIndices, hidden);
                changed   = true;
            }
        }

        // The result remembers the shapes that did something (one that only touches hidden triangles counts, for when they
        // show again) and the attribute set only while it hides something: an all-drawn geometry is simply unfiltered.
        var recordedShapes = effective.Count > 0 ? effective : null;
        if (!changed && SameSet(recordedShapes, geometry.EnabledShapes))
            return geometry;

        return geometry.With(meshes, recordedShapes, anyHidden ? attributes : null);
    }

    /// <summary> Indices of the mesh's submeshes that require an attribute outside <paramref name="enabledBits"/>, ascending. </summary>
    private static int[] HiddenSubmeshes(MdlMesh mesh, uint enabledBits)
    {
        List<int>? hidden = null;
        for (var s = 0; s < mesh.Submeshes.Length; s++)
        {
            if ((mesh.Submeshes[s].AttributeMask & ~enabledBits) != 0)
                (hidden ??= new List<int>()).Add(s);
        }

        return hidden?.ToArray() ?? [];
    }

    /// <summary>
    /// Per index slot of the mesh whether it is drawn: a slot of a hidden submesh is not, unless a drawn submesh covers it too
    /// (the game draws submesh by submesh); a slot no submesh covers stays. Null when no slot goes.
    /// </summary>
    private static bool[]? DrawnSlots(MdlMesh mesh, int[] hidden, int slotCount)
    {
        var drawn = new bool[slotCount];
        Array.Fill(drawn, true);
        var dropped = false;
        foreach (var s in hidden)
        {
            var (start, end) = SlotRange(mesh, mesh.Submeshes[s], slotCount);
            for (var i = start; i < end; i++)
                drawn[i] = false;
            dropped |= start < end;
        }

        if (!dropped)
            return null;

        var next = 0;
        for (var s = 0; s < mesh.Submeshes.Length; s++)
        {
            if (next < hidden.Length && hidden[next] == s)
            {
                next++;
                continue;
            }

            var (start, end) = SlotRange(mesh, mesh.Submeshes[s], slotCount);
            for (var i = start; i < end; i++)
                drawn[i] = true;
        }

        return drawn;
    }

    /// <summary> The slots of the mesh a submesh covers, clamped to the mesh: its offset is absolute in the LOD's index buffer. </summary>
    private static (int Start, int End) SlotRange(MdlMesh mesh, MdlSubmesh submesh, int slotCount)
    {
        var start = (long)submesh.IndexOffset - mesh.StartIndex;
        var end   = start + submesh.IndexCount;
        return ((int)Math.Clamp(start, 0, slotCount), (int)Math.Clamp(end, 0, slotCount));
    }

    /// <summary> The triangles whose first slot is drawn; the array itself when all are. </summary>
    private static int[] Filter(int[] indices, bool[] drawn)
    {
        var kept = 0;
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            if (drawn[t])
                kept += 3;
        }

        if (kept == indices.Length)
            return indices;

        var result = new int[kept];
        var w      = 0;
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            if (!drawn[t])
                continue;

            result[w++] = indices[t];
            result[w++] = indices[t + 1];
            result[w++] = indices[t + 2];
        }

        return result;
    }

    private static bool SameContent(int[] a, int[] b)
        => ReferenceEquals(a, b) || a.AsSpan().SequenceEqual(b);

    /// <summary> Set equality with null standing for the empty set. </summary>
    private static bool SameSet(IReadOnlySet<string>? a, IReadOnlySet<string>? b)
        => (a?.Count ?? 0) == (b?.Count ?? 0) && (a is null || b is null || a.SetEquals(b));

    /// <summary>
    /// The shape tables (Lumina's ShapeStruct / ShapeMeshStruct / ShapeValueStruct), LOD 0 parts only. The reader is positioned
    /// at the first shape. Entries outside the file are dropped rather than failing the whole model.
    /// </summary>
    private static MdlShape[] ReadShapes(BinaryCursor r, int stringStart, int shapeCount, int shapeMeshCount, int shapeValueCount)
    {
        var shapeStart      = (long)r.Position;
        var shapeMeshStart  = shapeStart + shapeCount * ShapeSize;
        var shapeValueStart = shapeMeshStart + shapeMeshCount * ShapeMeshSize;
        if (shapeCount == 0 || shapeValueStart + shapeValueCount * ShapeValueSize > r.Length)
            return Array.Empty<MdlShape>();

        var shapes = new List<MdlShape>(shapeCount);
        for (var s = 0; s < shapeCount; s++)
        {
            var shape     = r.Slice(shapeStart + s * ShapeSize, ShapeSize);
            var name      = r.StringAt(stringStart + BinaryPrimitives.ReadUInt32LittleEndian(shape));
            var meshStart = BinaryPrimitives.ReadUInt16LittleEndian(shape.Slice(4, 2));  // ShapeMeshStartIndex[0]
            var meshCount = BinaryPrimitives.ReadUInt16LittleEndian(shape.Slice(10, 2)); // ShapeMeshCount[0]
            var entries   = new List<MdlShapeMesh>(meshCount);
            for (var m = 0; m < meshCount; m++)
            {
                var at = shapeMeshStart + (meshStart + m) * (long)ShapeMeshSize;
                if (at + ShapeMeshSize > shapeValueStart)
                    break;

                var entry       = r.Slice(at, ShapeMeshSize);
                var indexOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                var valueCount  = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
                var valueOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4));
                if (valueOffset + (long)valueCount > shapeValueCount)
                    continue;

                var values = new MdlShapeValue[valueCount];
                var span   = r.Slice(shapeValueStart + valueOffset * (long)ShapeValueSize, (int)valueCount * ShapeValueSize);
                for (var v = 0; v < values.Length; v++)
                {
                    values[v] = new MdlShapeValue(
                        BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4 * v, 2)),
                        BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4 * v + 2, 2)));
                }

                entries.Add(new MdlShapeMesh(indexOffset, values));
            }

            if (entries.Count > 0 && name.Length > 0)
                shapes.Add(new MdlShape(name, entries.ToArray()));
        }

        return shapes.ToArray();
    }

    private static MdlMesh DecodeMesh(BinaryCursor r, int meshIndex, MeshHeader mesh, VertexElement[] declaration, uint lodVertexOffset,
        uint lodIndexOffset, string[] boneNames, MdlSubmesh[] submeshes)
    {
        var         n           = mesh.VertexCount;
        var         positions   = new Vector3[n];
        var         normals     = new Vector3[n];
        var         uvs         = new Vector2[n];
        var         hasPosition = false;
        var         hasNormals  = false;
        float[]?    weights     = null;
        var         weightCount = 0;
        byte[]?     rawIndices  = null;
        var         indexCount  = 0;
        Span<float> values      = stackalloc float[8];

        foreach (var element in declaration)
        {
            if (element.Stream >= 3 || element.UsageIndex != 0)
                continue;

            var stride = mesh.VertexBufferStride[element.Stream];
            if (stride == 0)
                continue;

            var  size  = ElementSize(element.Type);
            long first = lodVertexOffset + mesh.VertexBufferOffset[element.Stream] + element.Offset;
            switch (element.Usage)
            {
                case UsagePosition:
                    for (var v = 0; v < n; v++)
                    {
                        var c = Decode(element.Type, r.Slice(first + (long)v * stride, size), false, values);
                        positions[v] = new Vector3(values[0], c > 1 ? values[1] : 0f, c > 2 ? values[2] : 0f);
                    }

                    hasPosition = true;
                    break;
                case UsageNormal:
                    for (var v = 0; v < n; v++)
                    {
                        var c      = Decode(element.Type, r.Slice(first + (long)v * stride, size), true, values);
                        var normal = new Vector3(values[0], c > 1 ? values[1] : 0f, c > 2 ? values[2] : 0f);
                        if (IsUnsignedNormalised(element.Type))
                            normal = normal * 2f - Vector3.One;
                        normals[v] = MeshMath.SafeNormalize(normal);
                    }

                    hasNormals = true;
                    break;
                case UsageUv:
                    for (var v = 0; v < n; v++)
                    {
                        var c = Decode(element.Type, r.Slice(first + (long)v * stride, size), true, values);
                        uvs[v] = new Vector2(values[0], c > 1 ? values[1] : 0f);
                    }

                    break;
                case UsageBlendWeights:
                    weightCount = ComponentCount(element.Type);
                    weights     = new float[n * weightCount];
                    for (var v = 0; v < n; v++)
                    {
                        var c = Decode(element.Type, r.Slice(first + (long)v * stride, size), true, values);
                        for (var k = 0; k < c; k++)
                            weights[v * weightCount + k] = values[k];
                    }

                    break;
                case UsageBlendIndices:
                    indexCount = ComponentCount(element.Type);
                    rawIndices = new byte[n * indexCount];
                    for (var v = 0; v < n; v++)
                    {
                        var c = Decode(element.Type, r.Slice(first + (long)v * stride, size), false, values);
                        for (var k = 0; k < c; k++)
                            rawIndices[v * indexCount + k] = (byte)Math.Clamp(values[k], 0f, 255f);
                    }

                    break;
            }
        }

        if (!hasPosition)
            throw new InvalidDataException($"Mesh {meshIndex} has no position data.");

        var triangles = new int[mesh.IndexCount - mesh.IndexCount % 3];
        var indexSpan = r.Slice(lodIndexOffset + (long)mesh.StartIndex * 2, triangles.Length * 2);
        for (var i = 0; i < triangles.Length; i++)
        {
            var index = BinaryPrimitives.ReadUInt16LittleEndian(indexSpan.Slice(2 * i, 2));
            if (index >= n)
                throw new InvalidDataException($"Mesh {meshIndex} index {index} exceeds its {n} vertices.");

            triangles[i] = index;
        }

        if (!hasNormals)
            ComputeNormals(positions, triangles, normals);

        // Influences: every stored (bone, weight) pair whose bone is in the table, packed to the front of the 8 slots.
        var boneIndices = new byte[n * MdlMesh.Influences];
        var boneWeights = new float[n * MdlMesh.Influences];
        var skinned     = weights is not null && rawIndices is not null && boneNames.Length > 0;
        if (skinned)
        {
            var slots = Math.Min(Math.Min(weightCount, indexCount), MdlMesh.Influences);
            for (var v = 0; v < n; v++)
            {
                var written = 0;
                for (var k = 0; k < slots; k++)
                {
                    var bone   = rawIndices![v * indexCount + k];
                    var weight = weights![v * weightCount + k];
                    if (weight <= 0f || bone >= boneNames.Length)
                        continue;

                    boneIndices[v * MdlMesh.Influences + written] = bone;
                    boneWeights[v * MdlMesh.Influences + written] = weight;
                    written++;
                }
            }
        }

        return new MdlMesh
        {
            MeshIndex     = meshIndex,
            MaterialIndex = mesh.MaterialIndex,
            StartIndex    = mesh.StartIndex,
            Submeshes     = submeshes,
            BoneNames     = boneNames,
            Skinned       = skinned,
            Positions     = positions,
            Normals       = normals,
            Uvs           = uvs,
            BoneIndices   = boneIndices,
            BoneWeights   = boneWeights,
            Indices       = triangles,
            BaseIndices   = triangles,
            FileIndices   = triangles,
        };
    }

    private static void ComputeNormals(Vector3[] positions, int[] indices, Vector3[] normals)
    {
        Array.Clear(normals);
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var a = positions[indices[i]];
            var b = positions[indices[i + 1]];
            var c = positions[indices[i + 2]];
            var n = Vector3.Cross(b - a, c - a);
            normals[indices[i]]     += n;
            normals[indices[i + 1]] += n;
            normals[indices[i + 2]] += n;
        }

        for (var v = 0; v < normals.Length; v++)
        {
            var normal = MeshMath.SafeNormalize(normals[v]);
            normals[v] = normal == Vector3.Zero ? Vector3.UnitY : normal;
        }
    }

    private static uint[] ReadU32s(BinaryCursor r, int count)
    {
        var result = new uint[count];
        for (var i = 0; i < count; i++)
            result[i] = r.U32();
        return result;
    }

    private static int ElementSize(byte type)
        => type switch
        {
            TypeSingle1  => 4,
            TypeSingle2  => 8,
            TypeSingle3  => 12,
            TypeSingle4  => 16,
            TypeUByte4   => 4,
            TypeShort2   => 4,
            TypeShort4   => 8,
            TypeNByte4   => 4,
            TypeNShort2  => 4,
            TypeNShort4  => 8,
            TypeNUShort2 => 4,
            TypeNUShort4 => 8,
            TypeHalf2    => 4,
            TypeHalf4    => 8,
            TypeUShort2  => 4,
            TypeUByte8   => 8,
            _            => throw new InvalidDataException($"Unsupported vertex element type {type}."),
        };

    private static int ComponentCount(byte type)
        => type switch
        {
            TypeSingle1  => 1,
            TypeSingle2  => 2,
            TypeSingle3  => 3,
            TypeSingle4  => 4,
            TypeUByte4   => 4,
            TypeShort2   => 2,
            TypeShort4   => 4,
            TypeNByte4   => 4,
            TypeNShort2  => 2,
            TypeNShort4  => 4,
            TypeNUShort2 => 2,
            TypeNUShort4 => 4,
            TypeHalf2    => 2,
            TypeHalf4    => 4,
            TypeUShort2  => 2,
            TypeUByte8   => 8,
            _            => throw new InvalidDataException($"Unsupported vertex element type {type}."),
        };

    private static bool IsUnsignedNormalised(byte type)
        => type is TypeNByte4 or TypeNUShort2 or TypeNUShort4 or TypeUByte8;

    /// <summary>
    /// Decodes one element into floats and returns the component count. Integer formats are mapped to 0..1 (unsigned) or
    /// -1..1 (signed) when <paramref name="normalise"/> is set, otherwise returned as raw numbers (bone indices).
    /// </summary>
    private static int Decode(byte type, ReadOnlySpan<byte> s, bool normalise, Span<float> values)
    {
        switch (type)
        {
            case TypeSingle1:
            case TypeSingle2:
            case TypeSingle3:
            case TypeSingle4:
            {
                var count = type + 1;
                for (var k = 0; k < count; k++)
                    values[k] = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(4 * k, 4));
                return count;
            }
            case TypeUByte4:
            case TypeNByte4:
                for (var k = 0; k < 4; k++)
                    values[k] = normalise ? s[k] / 255f : s[k];
                return 4;
            case TypeUByte8:
                for (var k = 0; k < 8; k++)
                    values[k] = normalise ? s[k] / 255f : s[k];
                return 8;
            case TypeShort2:
            case TypeShort4:
            case TypeNShort2:
            case TypeNShort4:
            {
                var count = type is TypeShort2 or TypeNShort2 ? 2 : 4;
                for (var k = 0; k < count; k++)
                {
                    var v = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(2 * k, 2));
                    values[k] = normalise ? Math.Max(v / 32767f, -1f) : v;
                }

                return count;
            }
            case TypeNUShort2:
            case TypeNUShort4:
            case TypeUShort2:
            {
                var count = type == TypeNUShort4 ? 4 : 2;
                for (var k = 0; k < count; k++)
                {
                    var v = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(2 * k, 2));
                    values[k] = normalise ? v / 65535f : v;
                }

                return count;
            }
            case TypeHalf2:
            case TypeHalf4:
            {
                var count = type == TypeHalf2 ? 2 : 4;
                for (var k = 0; k < count; k++)
                    values[k] = (float)BinaryPrimitives.ReadHalfLittleEndian(s.Slice(2 * k, 2));
                return count;
            }
            default:
                throw new InvalidDataException($"Unsupported vertex element type {type}.");
        }
    }
}
