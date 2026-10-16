using System.Numerics;
using Dalamud.Plugin.Services;
using XivLiveMesh;

namespace Lamina.Decals.Mesh;

/// <summary> One model source as the last pick posed it, for the overlay. </summary>
/// <param name="Surface"> The surface the source belongs to. </param>
/// <param name="Source"> The source (authored geometry, race, offsets into the surface). </param>
/// <param name="World"> Posed world-space position per vertex of <see cref="LiveSource.Mesh"/>. The buffer is reused between picks. </param>
/// <param name="DeformerSteps"> Deformers applied to pose it (0 when the model was authored for the character's race). </param>
/// <param name="MissingBones"> Bones of the model the skeleton lacks; their vertices only follow the root. </param>
public sealed record PosedSource(SkinMesh Surface, LiveSource Source, Vector3[] World, int DeformerSteps, int MissingBones);

/// <summary>
/// Mouse picking on the posed local player: every surface's sources are posed with XivLiveMesh (racial deformer chain, then
/// linear blend skinning with up to 8 influences, straight into world space), the camera ray is cast through them and the
/// nearest hit is mapped back to the merged surface mesh and its rest pose. Framework thread only. Never throws out of
/// <see cref="Pick"/>.
/// </summary>
public sealed unsafe class DecalPicker : IDecalPicker
{
    private const int SelfChecks = 5;

    private readonly SkinMeshProvider _meshes;
    private readonly IObjectTable     _objects;
    private readonly IGameGui         _gameGui;
    private readonly RacialDeformer   _deformer;
    private readonly IPluginLog       _log;
    private readonly HashSet<string>  _logged = new(StringComparer.Ordinal);
    private readonly LivePose         _pose   = new();
    private readonly LiveMeshPoser    _poser  = new();

    private readonly Dictionary<SkinnedMesh, Vector3[]> _world = new();

    private int _selfChecks;

    /// <param name="meshes"> Rest-pose skin meshes of the local player and their sources. </param>
    /// <param name="objects"> Object table (the local player's draw object). </param>
    /// <param name="gameGui"> Game GUI, used to cross-check the ray against WorldToScreen. </param>
    /// <param name="deformer"> The racial deformer, shared with the provider. </param>
    /// <param name="log"> Plugin log. </param>
    public DecalPicker(SkinMeshProvider meshes, IObjectTable objects, IGameGui gameGui, RacialDeformer deformer, IPluginLog log)
    {
        _meshes   = meshes;
        _objects  = objects;
        _gameGui  = gameGui;
        _deformer = deformer;
        _log      = log;
    }

    /// <summary> Apply the racial deformer while posing. Off only for an A/B comparison in the overlay. </summary>
    public bool ApplyDeformer
    {
        get => _poser.ApplyDeformer;
        set => _poser.ApplyDeformer = value;
    }

    /// <summary> The racial deformer (loaded state and source, for the overlay). </summary>
    public RacialDeformer Deformer
        => _deformer;

    /// <summary> What happened during the last <see cref="Pick"/>, for a debug window. </summary>
    public string LastDiagnostics { get; private set; } = string.Empty;

    /// <summary> Every source as the last <see cref="Pick"/> posed it, world space, for an overlay. </summary>
    public IReadOnlyList<PosedSource> LastPosed { get; private set; } = Array.Empty<PosedSource>();

    /// <summary> Model → world transform (the skeleton's root) of the last <see cref="Pick"/>; the posed positions already include it. </summary>
    public Matrix4x4 LastModelToWorld { get; private set; } = Matrix4x4.Identity;

    /// <summary> The character's body shape family (cXXXX) at the last <see cref="Pick"/>, 0 when not a human. </summary>
    public ushort LastBodyRace { get; private set; }

    /// <summary> How the last <see cref="Pick"/> built its ray. </summary>
    public GameCamera.RaySource LastRaySource { get; private set; }

    /// <inheritdoc/>
    public DecalPick? Pick(Vector2 screenPosition)
    {
        try
        {
            return PickCore(screenPosition);
        }
        catch (Exception ex)
        {
            LastDiagnostics = $"error: {ex.Message}";
            LogOnce("pick", ex, "Lamina: decal pick failed.");
            return null;
        }
    }

    private DecalPick? PickCore(Vector2 screen)
    {
        var meshes = _meshes.GetLocalPlayerMeshes();
        if (meshes.Count == 0)
        {
            LastPosed       = Array.Empty<PosedSource>();
            LastDiagnostics = "no skin meshes";
            return null;
        }

        var character = LivePlayer.Character(_objects);
        if (character == null)
        {
            LastDiagnostics = "no local player";
            return null;
        }

        if (!_pose.Read(character))
        {
            LastDiagnostics = "no character skeleton";
            return null;
        }

        LastModelToWorld = _pose.Root;
        LastBodyRace     = _pose.GenderRace;

        if (!GameCamera.TryGetRay(screen, out var ray, out var source))
        {
            LastRaySource   = GameCamera.RaySource.None;
            LastDiagnostics = "no camera";
            return null;
        }

        LastRaySource = source;

        var         pbd          = _deformer.Pbd;
        var         posed        = new List<PosedSource>();
        SkinMesh?   bestMesh     = null;
        LiveSource? bestSource   = null;
        LiveMeshHit best         = default;
        var         bestDistance = float.PositiveInfinity;
        var         steps        = 0;
        var         missing      = 0;
        foreach (var mesh in meshes)
        {
            foreach (var liveSource in _meshes.SourcesOf(mesh))
            {
                var world = Buffer(liveSource.Mesh);
                _poser.Pose(liveSource.Mesh, _pose, pbd, world, liveSource.Race);
                posed.Add(new PosedSource(mesh, liveSource, world, _poser.DeformerSteps, _poser.MissingBones));
                steps   =  Math.Max(steps, _poser.DeformerSteps);
                missing += _poser.MissingBones;

                var hit = LiveMeshPicker.Raycast(world, liveSource.Mesh.Triangles, ray.Origin, ray.Direction);
                if (hit is { } h && h.Distance < bestDistance)
                {
                    bestDistance = h.Distance;
                    best         = h;
                    bestMesh     = mesh;
                    bestSource   = liveSource;
                }
            }
        }

        LastPosed = posed;
        var summary = $"{meshes.Count} surface(s), {posed.Count} source(s), body c{LastBodyRace:D4}, deformer {(ApplyDeformer ? $"{steps} step(s)" : "OFF")}, "
                    + $"{missing} missing bone(s), pbd {(pbd is not null ? "loaded" : "missing")}";
        if (bestMesh is null || bestSource is null)
        {
            LastDiagnostics = $"miss (ray via {source}; {summary})";
            return null;
        }

        var triangle = bestSource.TriangleOffset + best.Triangle;
        if (3 * triangle + 2 >= bestMesh.Indices.Length)
        {
            LastDiagnostics = $"hit maps outside the merged mesh (triangle {triangle}); {summary}";
            return null;
        }

        var barycentric = new Vector3(1f - best.U - best.V, best.U, best.V);
        var i0          = bestMesh.Indices[3 * triangle];
        var i1          = bestMesh.Indices[3 * triangle + 1];
        var i2          = bestMesh.Indices[3 * triangle + 2];

        var restPosition = MeshMath.Interpolate(bestMesh.Positions[i0], bestMesh.Positions[i1], bestMesh.Positions[i2], barycentric);
        var restNormal   = MeshMath.SafeNormalize(MeshMath.Interpolate(bestMesh.Normals[i0], bestMesh.Normals[i1], bestMesh.Normals[i2], barycentric));
        if (restNormal == Vector3.Zero)
            restNormal = MeshMath.TriangleNormal(bestMesh.Positions[i0], bestMesh.Positions[i1], bestMesh.Positions[i2]);

        var uv   = MeshMath.Interpolate(bestMesh.Uvs[i0], bestMesh.Uvs[i1], bestMesh.Uvs[i2], barycentric);
        var bone = MeshMath.DominantBone(bestMesh, triangle, barycentric);
        var part = bone >= 0 && bone < bestMesh.BoneNames.Length ? BoneNames.Friendly(bestMesh.BoneNames[bone]) : null;

        var mirrored = false;
        try
        {
            mirrored = UvMirror.IsMirrored(bestMesh, triangle, uv);
        }
        catch (Exception ex)
        {
            LogOnce("mirror", ex, "Lamina: UvMirror.IsMirrored failed; reporting no mirroring.");
        }

        SelfCheck(screen, best.World, source);
        LastDiagnostics = $"{bestMesh.Surface} triangle {triangle} at {best.Distance:F2} m via {source}, uv ({uv.X:F3}, {uv.Y:F3}), {part ?? "unknown part"}, "
                        + $"model c{bestSource.Race:D4}; {summary}";
        return new DecalPick(bestMesh, triangle, barycentric, best.World, best.Normal, restPosition, restNormal, uv, mirrored, part);
    }

    private Vector3[] Buffer(SkinnedMesh mesh)
    {
        if (_world.TryGetValue(mesh, out var buffer) && buffer.Length == mesh.VertexCount)
            return buffer;

        if (_world.Count > 16)
            _world.Clear();

        buffer       = new Vector3[mesh.VertexCount];
        _world[mesh] = buffer;
        return buffer;
    }

    /// <summary>
    /// For the first few picks: projects the hit back with IGameGui.WorldToScreen and compares it to the clicked pixel, and
    /// compares the camera's view * projection with the game's view-projection matrix, so an in-game log shows whether the
    /// ray construction (and the matrix fallback) agree with the game.
    /// </summary>
    private void SelfCheck(Vector2 screen, Vector3 worldPosition, GameCamera.RaySource source)
    {
        if (_selfChecks >= SelfChecks)
            return;

        _selfChecks++;
        try
        {
            if (_gameGui.WorldToScreen(worldPosition, out var back))
            {
                var error = Vector2.Distance(back, screen);
                var text  = $"Lamina: decal pick self-check {_selfChecks}/{SelfChecks}: ray via {source}, WorldToScreen round trip is off by {error:F1} px.";
                if (error > 3f)
                    _log.Warning(text);
                else
                    _log.Debug(text);
            }

            if (GameCamera.TryGetMatrices(out var view, out var projection, out _) && GameCamera.TryGetViewProjection(out var gameViewProjection))
            {
                var ours = view * projection;
                var diff = 0f;
                for (var row = 1; row <= 4; row++)
                    for (var col = 1; col <= 4; col++)
                        diff = MathF.Max(diff, MathF.Abs(ours[row - 1, col - 1] - gameViewProjection[row - 1, col - 1]));
                _log.Debug($"Lamina: decal camera self-check: max |view*projection - game ViewProjectionMatrix| = {diff:E2}.");
            }
        }
        catch (Exception ex)
        {
            LogOnce("selfcheck", ex, "Lamina: decal pick self-check failed.");
        }
    }

    private void LogOnce(string key, Exception ex, string message)
    {
        if (_logged.Add(key))
            _log.Warning(ex, message);
    }
}
