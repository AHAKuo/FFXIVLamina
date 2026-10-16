using System.IO;
using System.Numerics;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.STD;
using InteropGenerator.Runtime;
using Lamina.Ipc;
using Penumbra.Api;
using Penumbra.Api.Enums;
using XivLiveMesh;

namespace Lamina.Decals.Mesh;

/// <summary>
/// Builds the local player's rest-pose skin meshes from the models the draw object renders right now
/// (<c>CharacterBase.ModelsSpan</c>): every loaded .mdl is parsed from the bytes the game holds (a mod file or the game's
/// own), its enabled shape keys are applied, the submeshes the game hides through attributes (a top hiding the body's neck or
/// wrists, a body mod's "extra hidden" parts) are dropped, and the meshes using the surface's skin material are merged. Gear is authored
/// for one race (read from its game path) and bent onto the character's with the racial deformer: the merged mesh carries
/// the deformed rest pose (the draw object's model space) while each <see cref="LiveSource"/> keeps the authored geometry
/// for the picker to pose. Material and texture paths come from Penumbra's resource tree. Rebuilt when the set of
/// (model file, enabled shapes, enabled attributes) or the body race changes, or after <see cref="Invalidate"/>. Framework thread only. Never
/// throws out of the public members.
/// </summary>
public sealed unsafe class SkinMeshProvider : ISkinMeshProvider
{
    /// <summary> Object table index of the local player. </summary>
    public const int LocalPlayerIndex = LivePlayer.ObjectIndex;

    private const int GeometryCacheSize = 32;

    /// <summary>
    /// <c>ResourceHandle.LoadState</c> of a resource whose data has arrived. FFXIVClientStructs keeps the field as a raw byte;
    /// its documentation (DrawObject bounds remarks) names 7 as "loaded", and lower values are the stages before it.
    /// </summary>
    private const byte ResourceLoaded = 7;

    private static readonly IReadOnlyList<SkinMesh>   Empty     = Array.Empty<SkinMesh>();
    private static readonly IReadOnlyList<LiveSource> NoSources = Array.Empty<LiveSource>();

    private readonly IPenumbraBridge _penumbra;
    private readonly IDataManager    _data;
    private readonly IObjectTable    _objects;
    private readonly RacialDeformer  _deformer;
    private readonly IPluginLog      _log;
    private readonly HashSet<string> _logged   = new(StringComparer.Ordinal);
    private readonly LivePose        _pose     = new();
    private readonly Dictionary<string, MdlGeometry>               _geometry = new(StringComparer.Ordinal);
    private readonly Dictionary<SkinMesh, IReadOnlyList<LiveSource>> _sources  = new();

    private IReadOnlyList<SkinMesh>? _meshes;
    private string?                  _key;

    /// <param name="penumbra"> Penumbra bridge (resource tree: material and texture paths). </param>
    /// <param name="dataManager"> Dalamud data manager (unmodified game files). </param>
    /// <param name="objects"> Object table (the local player's draw object). </param>
    /// <param name="deformer"> The racial deformer, shared with the picker. </param>
    /// <param name="log"> Plugin log. </param>
    public SkinMeshProvider(IPenumbraBridge penumbra, IDataManager dataManager, IObjectTable objects, RacialDeformer deformer, IPluginLog log)
    {
        _penumbra = penumbra;
        _data     = dataManager;
        _objects  = objects;
        _deformer = deformer;
        _log      = log;
    }

    /// <summary> Human readable summary of the last build (surfaces, models, counts, diffuse paths), for a debug window. </summary>
    public string LastBuildSummary { get; private set; } = "not built yet";

    /// <summary> The per-model sources a surface was merged from (empty for a mesh this provider did not build). </summary>
    public IReadOnlyList<LiveSource> SourcesOf(SkinMesh mesh)
        => _sources.TryGetValue(mesh, out var sources) ? sources : NoSources;

    /// <inheritdoc/>
    public IReadOnlyList<SkinMesh> GetLocalPlayerMeshes()
    {
        try
        {
            var character = LivePlayer.Character(_objects);
            if (character == null)
            {
                Reset("no local player draw object");
                return Empty;
            }

            var models = LoadedModels(character);
            if (models.Count == 0)
            {
                Reset("the player draws no models yet");
                return Empty;
            }

            var key = CacheKey(LivePlayer.Race(character), models);
            if (_meshes is not null && key == _key)
                return _meshes;

            _meshes = Build(character, models);
            _key    = key;
            return _meshes;
        }
        catch (Exception ex)
        {
            WarnOnce("get", ex, "Lamina: building the skin meshes failed.");
            return _meshes ?? Empty;
        }
    }

    /// <inheritdoc/>
    public DrawObjectIdentity? DrawObjectState()
    {
        try
        {
            var character = LivePlayer.Character(_objects);
            if (character == null)
                return null;

            var models = LoadedModels(character);
            var loaded = models.Count > 0 && models.All(m => m.LoadState == ResourceLoaded);
            return new DrawObjectIdentity(CacheKey(LivePlayer.Race(character), models), loaded);
        }
        catch (Exception ex)
        {
            WarnOnce("state", ex, "Lamina: reading the player's draw object failed.");
            return null;
        }
    }

    /// <inheritdoc/>
    public void Invalidate()
    {
        _meshes = null;
        _key    = null;
        _sources.Clear();
        _geometry.Clear();
    }

    private void Reset(string reason)
    {
        _materials.Clear();
        _meshes          = null;
        _key             = null;
        LastBuildSummary = reason;
        _sources.Clear();
    }

    // ---- the draw object's models ----

    /// <summary>
    /// One model the draw object renders, with the game's enabled shape key and attribute bit masks (bits are indices into the
    /// handle's name maps). <see cref="Handle"/> is only valid during the call that read it; <see cref="LoadState"/> changes when its data arrives.
    /// </summary>
    private readonly record struct LoadedModel(int Slot, string FileName, uint ShapeMask, uint AttributeMask, byte LoadState, nint Handle);

    /// <summary> A drawn model with its geometry as the game draws it (shapes applied, hidden submeshes dropped) and what the game enabled on it. </summary>
    private sealed record ModelInfo(LoadedModel Model, string FilePath, string? GamePath, ushort Race, MdlGeometry Geometry,
        IReadOnlySet<string> Shapes, IReadOnlySet<string> Attributes, ResourceNodeDto? Node);

    private static List<LoadedModel> LoadedModels(CharacterBase* character)
    {
        var result = new List<LoadedModel>();
        var slot   = -1;
        foreach (var pointer in character->ModelsSpan)
        {
            slot++;
            var model = pointer.Value;
            if (model == null || model->ModelResourceHandle == null)
                continue;

            var handle = model->ModelResourceHandle;
            var name   = handle->FileName.ToString();
            if (string.IsNullOrEmpty(name))
                continue;

            result.Add(new LoadedModel(slot, name, model->EnabledShapeKeyIndexMask, model->EnabledAttributeIndexMask, ((ResourceHandle*)handle)->LoadState, (nint)handle));
        }

        return result;
    }

    private static string CacheKey(ushort bodyRace, List<LoadedModel> models)
    {
        var sb = new StringBuilder();
        sb.Append('c').Append(bodyRace);
        foreach (var model in models)
            sb.Append('\n').Append(model.Slot).Append('|').Append(model.FileName).Append('|').Append(model.ShapeMask).Append('|').Append(model.AttributeMask).Append('|').Append(model.LoadState);
        return sb.ToString();
    }

    /// <summary>
    /// Names the game has enabled on a model: the handle's name → index map (<c>Shapes</c> or <c>Attributes</c>) filtered by
    /// the model's bit mask (<c>EnabledShapeKeyIndexMask</c> / <c>EnabledAttributeIndexMask</c>). Going through the names,
    /// not the indices, keeps a runtime map that disagrees with the parsed file from hiding or showing the wrong part.
    /// </summary>
    private static HashSet<string> EnabledNames(StdMap<CStringPointer, short> map, uint mask)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (mask == 0)
            return names;

        foreach (var pair in map)
        {
            if (pair.Item2 is >= 0 and < 32 && (mask & (1u << pair.Item2)) != 0)
                names.Add(pair.Item1.ToString());
        }

        return names;
    }

    /// <summary> A loaded resource's file path with Penumbra's <c>|…|</c> prefix removed. </summary>
    public static string FilePath(string resourceName)
    {
        var path = resourceName.Trim();
        if (path.StartsWith('|') && path.IndexOf('|', 1) is var close and > 0)
            path = path[(close + 1)..];
        return path;
    }

    /// <summary> A path as a lookup key: no redirect prefix, forward slashes, lower case, so live and resolved paths match. </summary>
    public static string PathKey(string? path)
        => string.IsNullOrEmpty(path) ? string.Empty : FilePath(path).Trim('"').Replace('\\', '/').ToLowerInvariant();

    // ---- building ----

    private IReadOnlyList<SkinMesh> Build(CharacterBase* character, List<LoadedModel> models)
    {
        _sources.Clear();
        var summary  = new StringBuilder();
        var poseRead = _pose.Read(character);
        var bodyRace = poseRead ? _pose.GenderRace : LivePlayer.Race(character);
        var tree     = _penumbra.Available ? _penumbra.GetGameObjectResourceTree(LocalPlayerIndex, withUiData: true) : null;
        var nodes    = IndexModelNodes(tree);

        var infos = new List<ModelInfo>(models.Count);
        foreach (var model in models)
        {
            var filePath = FilePath(model.FileName);
            var node     = nodes.GetValueOrDefault(PathKey(model.FileName));
            var geometry = LoadGeometry(model, filePath, node?.ActualPath);
            if (geometry is null)
                continue;

            var handle     = (ModelResourceHandle*)model.Handle;
            var shapes     = EnabledNames(handle->Shapes, model.ShapeMask);
            var attributes = EnabledNames(handle->Attributes, model.AttributeMask);
            // "atr_lod" marks the detail parts drawn at LOD 0, the level the player is looked at from up close; the renderer
            // handles it outside the attribute mask, so it counts as enabled here or the main skin of many outfits vanishes.
            attributes.Add("atr_lod");
            var gamePath   = (node is not null ? GamePathOf(node) : null) ?? (LooksLikeGamePath(filePath) ? filePath : null);
            infos.Add(new ModelInfo(model, filePath, gamePath, SkinMeshBuilder.RaceOf(gamePath ?? filePath), MdlReader.Apply(geometry, shapes, attributes), shapes, attributes, node));
        }

        // Every material of every drawn model, judged by its shader (skin.shpk = skin, whatever the file is called) and
        // resolved to the diffuse texture it samples. Skin materials are then grouped by (surface, diffuse): a body that draws
        // its torso from a body mod's texture and its hands from the vanilla one has two Body meshes, baked separately.
        var materials = new List<SkinMaterial>();
        var seen      = new HashSet<string>(StringComparer.Ordinal);
        var census    = new List<string>();
        foreach (var info in infos)
        {
            census.Add(ModelCensus(info));
            foreach (var name in info.Geometry.Materials)
            {
                if (string.IsNullOrEmpty(name) || !seen.Add($"{info.FilePath}|{name}"))
                    continue;

                var node     = FindMaterialNode(info.Node, name);
                var gamePath = node is not null ? GamePathOf(node) : null;
                var parsed   = node is not null && gamePath is not null ? ReadMaterial(gamePath, node) : null;
                var surface  = SkinMeshBuilder.ClassifySkinMaterial(gamePath ?? name, parsed?.ShaderPackage);
                var diffuse  = surface is not null && node is not null && gamePath is not null ? ResolveDiffuse(gamePath, node, parsed) : null;
                census.Add($"{info.FilePath} [{_byteSources.GetValueOrDefault(info.Model.FileName, "?")}]: {name} -> {(gamePath ?? "no tree node")}, shader {parsed?.ShaderPackage ?? "?"}, {(surface is { } s ? $"{s} skin, diffuse {diffuse ?? "none"}" : "not skin")}");
                if (surface is null)
                    continue;

                var key = SkinMeshBuilder.SkinMaterialKey(name);
                materials.Add(new SkinMaterial(surface.Value, key, gamePath ?? name, diffuse, info));
            }
        }

        if (!_logged.Contains(string.Join("\n", census)))
        {
            _logged.Add(string.Join("\n", census));
            _log.Information("Decal skin census (every material of every drawn model):\n  " + string.Join("\n  ", census));
        }

        var result = new List<SkinMesh>();
        foreach (var group in materials.Where(m => m.Diffuse is not null)
                     .GroupBy(m => (m.Surface, Diffuse: m.Diffuse!), new SurfaceDiffuseComparer())
                     .OrderBy(g => g.Key.Surface).ThenBy(g => g.Key.Diffuse, StringComparer.OrdinalIgnoreCase))
        {
            var surface = group.Key.Surface;
            var diffuse = group.Key.Diffuse;
            var keys    = group.Select(m => m.Key).Distinct(StringComparer.Ordinal).ToList();
            var skinKey = string.Join("|", keys);
            var materialGamePath = group.First().MaterialGamePath;

            var contributing = infos.Where(i => i.Geometry.Lod0Meshes.Any(m => SkinMeshBuilder.MaterialMatches(MaterialOf(i.Geometry, m), skinKey))).ToList();
            var sources = contributing
                .Select(i => new SkinMeshSource(i.GamePath ?? i.FilePath, i.FilePath, i.Geometry, skinKey, i.Race, Deform(i.Race, bodyRace, i.Geometry.Bones, poseRead)))
                .ToList();
            var build = SkinMeshBuilder.Build(surface, materialGamePath, diffuse, sources, m => WarnOnce(m, null, m));
            if (build is null)
            {
                summary.Append($"{surface} ({diffuse}): no mesh uses {skinKey}; ");
                continue;
            }

            result.Add(build.Mesh);
            _sources[build.Mesh] = build.Sources;
            summary.Append($"{surface} ({MtrlReader.FileName(diffuse)}): {build.Sources.Count} model(s), {build.Mesh.VertexCount} vertices, {build.Mesh.TriangleCount} triangles, {build.Mesh.BoneNames.Length} bones; ");
            _log.Information($"Decal skin {surface}: body c{bodyRace:D4}, diffuse {diffuse}, materials {skinKey}, models: "
                           + string.Join(" | ", contributing.Select(i => $"{i.FilePath} (game path {i.GamePath ?? "?"}, c{i.Race:D4}"
                               + $"{(i.Race != 0 && bodyRace != 0 && i.Race != bodyRace ? (_deformer.Loaded ? " deformed" : " NOT deformed, pbd missing") : "")}, "
                               + $"shapes [{string.Join(",", i.Shapes)}], attributes [{string.Join(",", i.Attributes)}])")));
        }

        foreach (var skipped in materials.Where(m => m.Diffuse is null))
            summary.Append($"{skipped.Surface}: {skipped.Key} has no diffuse texture; ");
        if (materials.Count == 0)
            summary.Append("no skin material among the drawn models; ");

        LastBuildSummary = summary.Length > 0 ? summary.ToString().TrimEnd(' ', ';') : "no skin materials among the drawn models";
        _log.Debug($"Lamina: skin meshes rebuilt: {LastBuildSummary}");
        return result;
    }

    /// <summary> The census line of a drawn model: where its bytes came from, what the game enables on it and how many submeshes that hides. </summary>
    private string ModelCensus(ModelInfo info)
    {
        var submeshes = info.Geometry.Lod0Meshes.Sum(m => m.Submeshes.Length);
        var hidden    = info.Geometry.Lod0Meshes.Sum(m => m.HiddenSubmeshes.Length);
        return $"{info.FilePath} [{_byteSources.GetValueOrDefault(info.Model.FileName, "?")}]: shapes [{string.Join(",", info.Shapes)}], "
             + $"attributes [{string.Join(",", info.Attributes)}] of [{string.Join(",", info.Geometry.AttributeNames)}], "
             + (hidden > 0 ? $"hidden {hidden}/{submeshes} submeshes" : $"all {submeshes} submeshes drawn");
    }

    /// <summary> One skin material of one drawn model. </summary>
    private sealed record SkinMaterial(DecalSurface Surface, string Key, string MaterialGamePath, string? Diffuse, ModelInfo Model);

    private sealed class SurfaceDiffuseComparer : IEqualityComparer<(DecalSurface Surface, string Diffuse)>
    {
        public bool Equals((DecalSurface Surface, string Diffuse) a, (DecalSurface Surface, string Diffuse) b)
            => a.Surface == b.Surface && string.Equals(a.Diffuse, b.Diffuse, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((DecalSurface Surface, string Diffuse) x)
            => HashCode.Combine(x.Surface, x.Diffuse.ToLowerInvariant());
    }

    /// <summary> The material node under a model's tree node whose file name matches the model's material entry (race code ignored). </summary>
    private static ResourceNodeDto? FindMaterialNode(ResourceNodeDto? modelNode, string materialName)
    {
        if (modelNode is null)
            return null;

        foreach (var child in modelNode.Children)
        {
            if (child.Type != ResourceType.Mtrl)
                continue;

            var gamePath = GamePathOf(child) ?? child.ActualPath;
            if (!string.IsNullOrEmpty(gamePath) && SkinMeshBuilder.MaterialMatches(materialName, gamePath))
                return child;
        }

        return null;
    }

    private readonly Dictionary<string, MtrlInfo?> _materials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> The parsed material behind a tree node, cached by its actual path. Null when it cannot be read. </summary>
    private MtrlInfo? ReadMaterial(string materialGamePath, ResourceNodeDto node)
    {
        var cacheKey = node.ActualPath ?? materialGamePath;
        if (_materials.TryGetValue(cacheKey, out var cached))
            return cached;

        MtrlInfo? material = null;
        try
        {
            var bytes = LoadFile(materialGamePath, node.ActualPath);
            if (bytes is not null)
                material = MtrlReader.Read(bytes);
        }
        catch (Exception ex)
        {
            WarnOnce($"mtrl|{node.ActualPath}", ex, $"Lamina: could not parse material {node.ActualPath}; classifying it by name.");
        }

        _materials[cacheKey] = material;
        return material;
    }

    private static string MaterialOf(MdlGeometry geometry, MdlMesh mesh)
        => mesh.MaterialIndex < geometry.Materials.Length ? geometry.Materials[mesh.MaterialIndex] : string.Empty;

    /// <summary>
    /// The deformer matrix per bone of a model authored for <paramref name="modelRace"/> worn by a <paramref name="bodyRace"/>
    /// body: the pbd chain resolved per bone with the live skeleton's parents. Null when no deformation applies or the pbd is missing.
    /// </summary>
    private IReadOnlyDictionary<string, Matrix4x4>? Deform(ushort modelRace, ushort bodyRace, string[] bones, bool poseRead)
    {
        if (!poseRead || modelRace == 0 || bodyRace == 0 || modelRace == bodyRace)
            return null;

        var pbd = _deformer.Pbd;
        if (pbd is null)
            return null;

        var chain = pbd.Chain(modelRace, bodyRace);
        if (chain.Count == 0)
            return null;

        var result = new Dictionary<string, Matrix4x4>(bones.Length, StringComparer.Ordinal);
        foreach (var bone in bones)
        {
            if (!string.IsNullOrEmpty(bone))
                result[bone] = PbdFile.Resolve(chain, bone, _pose.ParentOf);
        }

        return result;
    }

    /// <summary> Where each loaded model's bytes came from, keyed by resource name, for the census. </summary>
    private readonly Dictionary<string, string> _byteSources = new(StringComparer.Ordinal);

    /// <summary>
    /// A model's geometry. Bytes come from the loaded resource when the game kept them (what it draws, whatever the name);
    /// else from the file Penumbra reports it loaded (<paramref name="actualPath"/>: a resource named by its game path can
    /// still have been served from a mod file); else from a rooted resource name; else from the game's own file. Falling
    /// straight to the game file for a game-path name would read the vanilla model under a mod's outfit.
    /// </summary>
    private MdlGeometry? LoadGeometry(LoadedModel model, string filePath, string? actualPath)
    {
        if (_geometry.TryGetValue(model.FileName, out var cached))
            return cached;

        MdlGeometry? geometry = null;
        try
        {
            byte[]? bytes  = null;
            var     source = "none";
            var resource = (ResourceHandle*)model.Handle;
            var data     = resource->GetData();
            var length   = resource->GetLength();
            if (data != null && length > 0 && length < int.MaxValue)
            {
                bytes  = new ReadOnlySpan<byte>(data, (int)length).ToArray();
                source = "resource data";
            }
            else if (!string.IsNullOrEmpty(actualPath) && Path.IsPathRooted(actualPath) && File.Exists(actualPath))
            {
                bytes  = File.ReadAllBytes(actualPath);
                source = $"file {actualPath}";
            }
            else if (Path.IsPathRooted(filePath) && File.Exists(filePath))
            {
                bytes  = File.ReadAllBytes(filePath);
                source = $"file {filePath}";
            }
            else if (LooksLikeGamePath(filePath))
            {
                bytes  = _data.GetFile(filePath)?.Data;
                source = "game data";
            }

            _byteSources[model.FileName] = source;
            if (bytes is null)
                WarnOnce($"mdl|{model.FileName}", null, $"Lamina: could not read model {model.FileName} (slot {model.Slot}).");
            else
                geometry = MdlReader.Read(bytes);
        }
        catch (Exception ex)
        {
            WarnOnce($"mdl|{model.FileName}", ex, $"Lamina: could not parse model {model.FileName} (slot {model.Slot}).");
        }

        if (geometry is not null)
        {
            if (_geometry.Count >= GeometryCacheSize)
                _geometry.Clear();
            _geometry[model.FileName] = geometry;
        }

        return geometry;
    }

    // ---- resource tree: materials and textures ----

    /// <summary> Penumbra's Mdl nodes by the key of their actual path and of their game path. </summary>
    private static Dictionary<string, ResourceNodeDto> IndexModelNodes(ResourceTreeDto? tree)
    {
        var result = new Dictionary<string, ResourceNodeDto>(StringComparer.Ordinal);
        if (tree is null)
            return result;

        Visit(tree.Nodes);
        return result;

        void Visit(IEnumerable<ResourceNodeDto> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Type == ResourceType.Mdl)
                {
                    var actual = PathKey(node.ActualPath);
                    var game   = PathKey(node.GamePath);
                    if (actual.Length > 0)
                        result.TryAdd(actual, node);
                    if (game.Length > 0)
                        result.TryAdd(game, node);
                }

                Visit(node.Children);
            }
        }
    }

    /// <summary> The skin material's game path and diffuse, from the material nodes under the contributing models (first model that exposes a diffuse wins). </summary>
    private (string? MaterialGamePath, string? Diffuse) ResolveSkinMaterial(List<ModelInfo> models, string skinKey)
    {
        string? first = null;
        foreach (var info in models)
        {
            if (info.Node is null)
                continue;

            foreach (var child in info.Node.Children)
            {
                if (child.Type != ResourceType.Mtrl)
                    continue;

                var gamePath = GamePathOf(child);
                if (gamePath is null || !string.Equals(SkinMeshBuilder.SkinMaterialKey(gamePath), skinKey, StringComparison.Ordinal))
                    continue;

                first ??= gamePath;
                var diffuse = ResolveDiffuse(gamePath, child);
                if (diffuse is not null)
                    return (gamePath, diffuse);
            }
        }

        return (first, null);
    }

    /// <summary>
    /// The game path of the material's diffuse texture, preferring the texture node Penumbra reports as loaded (that is the
    /// path the game requests and the Publish part must redirect), identified through the material's sampler table.
    /// </summary>
    private string? ResolveDiffuse(string materialGamePath, ResourceNodeDto node, MtrlInfo? material = null)
    {
        material ??= ReadMaterial(materialGamePath, node);

        var textures = node.Children.Where(t => t.Type == ResourceType.Tex).ToList();
        var wanted   = material?.Diffuse;
        if (wanted is not null)
        {
            foreach (var texture in textures)
            {
                var gamePath = GamePathOf(texture);
                if (MtrlReader.SameTexture(gamePath, wanted.Value.Path))
                    return gamePath;
            }

            return wanted.Value.LoadPath;
        }

        foreach (var texture in textures)
        {
            var gamePath = GamePathOf(texture);
            if (MtrlReader.LooksLikeDiffuse(gamePath))
                return gamePath;
        }

        return null;
    }

    /// <summary> Penumbra leaves GamePath null when several game paths could have produced a resource; fall back to a path-shaped ActualPath. </summary>
    private static string? GamePathOf(ResourceNodeDto node)
    {
        if (!string.IsNullOrEmpty(node.GamePath))
            return node.GamePath;

        return LooksLikeGamePath(node.ActualPath) ? node.ActualPath : null;
    }

    private static bool LooksLikeGamePath(string? path)
        => !string.IsNullOrEmpty(path) && !Path.IsPathRooted(path) && path.Contains('/') && !path.Contains('\\');

    /// <summary> Reads a file from disk when Penumbra redirects it, else from the game data. </summary>
    private byte[]? LoadFile(string gamePath, string? actualPath)
    {
        var redirected = !string.IsNullOrEmpty(actualPath) && (!string.Equals(gamePath, actualPath, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(actualPath));
        if (redirected && File.Exists(actualPath))
            return File.ReadAllBytes(actualPath!);

        return _data.GetFile(gamePath)?.Data;
    }

    private void WarnOnce(string key, Exception? ex, string message)
    {
        if (!_logged.Add(key))
            return;

        if (ex is null)
            _log.Warning(message);
        else
            _log.Warning(ex, message);
    }
}
