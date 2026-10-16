using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Lamina.Ipc;
using Penumbra.Api.Enums;

namespace Lamina.Decals.Publish;

/// <summary>
/// Bakes a design's decals into the local player's skin diffuse textures and redirects those textures in the Lamina collection
/// with one Penumbra temporary mod (<see cref="DecalPaths.TemporaryModTag"/>, <see cref="DecalPaths.TemporaryModPriority"/>).
/// Nothing is written into Penumbra's mod directory: bakes live in Lamina's cache folder as BC7 .tex files named by a content
/// hash (<see cref="BakeCacheKey"/>), so the game, which caches textures by path, always loads changed content, and unchanged
/// content is found again without baking.
/// <see cref="Publish"/> and <see cref="Clear"/> are framework-thread calls; decoding, baking and conversion run on the thread
/// pool; <see cref="Completed"/> fires on the framework thread once the redirect is in place. A newer publish or a clear
/// supersedes a running publish: the stale run neither redirects nor completes.
/// </summary>
public sealed class DecalPublisher : IDecalPublisher, IDisposable
{
    /// <summary> Decoded decal images kept in memory (they are small; the skin texture itself is never cached). </summary>
    private const int DecalImageCacheSize = 32;

    private readonly IPenumbraBridge _penumbra;
    private readonly ISkinMeshProvider _meshes;
    private readonly ITextureProvider _textures;
    private readonly ITextureReadbackProvider _readback;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly string _imageDirectory;
    private readonly string _cacheDirectory;

    /// <summary> Diffuse game path → the real upstream file it last resolved to, so a stale redirect to our own bake can be bypassed. Framework thread. </summary>
    private readonly Dictionary<string, string> _upstream = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RgbaImage> _decalImages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private int _runId;
    private int _inFlight;
    private volatile bool _disposed;

    /// <param name="penumbra"> Penumbra bridge: path resolution, temporary mods, texture conversion. </param>
    /// <param name="meshes"> Live skin meshes of the local player (framework thread). </param>
    /// <param name="textures"> Texture provider: loads the upstream skin texture and the decal images onto the GPU. </param>
    /// <param name="readback"> Reads GPU textures back as RGBA8. </param>
    /// <param name="framework"> Framework, to complete on its thread. </param>
    /// <param name="log"> Plugin log. </param>
    /// <param name="imageDirectory"> Where decal images live (<see cref="DecalPaths.ImageDirectory"/>). </param>
    /// <param name="cacheDirectory"> Where bakes are written (<see cref="DecalPaths.CacheDirectory"/>). </param>
    public DecalPublisher(IPenumbraBridge penumbra, ISkinMeshProvider meshes, ITextureProvider textures, ITextureReadbackProvider readback,
        IFramework framework, IPluginLog log, string imageDirectory, string cacheDirectory)
    {
        _penumbra       = penumbra;
        _meshes         = meshes;
        _textures       = textures;
        _readback       = readback;
        _framework      = framework;
        _log            = log;
        _imageDirectory = imageDirectory;
        _cacheDirectory = cacheDirectory;
    }

    /// <inheritdoc/>
    public bool Busy
        => Volatile.Read(ref _inFlight) > 0;

    /// <inheritdoc/>
    public event Action<DecalPublishResult>? Completed;

    /// <inheritdoc/>
    public void Publish(Guid collectionId, int objectIndex, IReadOnlyList<DecalSpec> decals)
    {
        if (_disposed)
            return;

        try
        {
            if (decals.Count == 0)
            {
                Clear(collectionId);
                return;
            }

            if (!_framework.IsInFrameworkUpdateThread)
                LogOnce("Decal publish called off the framework thread; the mesh snapshot may be unavailable.");

            var run      = Supersede();
            var problems = new List<string>();
            var jobs     = Prepare(collectionId, objectIndex, decals, problems);

            Interlocked.Increment(ref _inFlight);
            _ = Task.Run(() => RunAsync(run, collectionId, jobs, problems));
        }
        catch (Exception ex)
        {
            LogOnce($"Decal publish could not start: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public void Clear(Guid collectionId)
    {
        if (_disposed)
            return;

        try
        {
            Supersede();
            var code = _penumbra.RemoveTemporaryMod(DecalPaths.TemporaryModTag, collectionId, DecalPaths.TemporaryModPriority);
            if (code is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
                LogOnce($"Could not remove the decal mod from collection {collectionId}: {code}");
        }
        catch (Exception ex)
        {
            LogOnce($"Decal clear failed: {ex.Message}", ex);
        }
    }

    /// <summary> Cancels a running publish. Nothing is removed from Penumbra here: the engine's cleanup does that. </summary>
    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
            _cts?.Cancel();
    }

    // ---- framework thread -------------------------------------------------------------------------

    private readonly record struct Run(int Id, CancellationToken Token);

    private sealed record SurfaceJob(DecalSurface Surface, SkinMesh Mesh, string Upstream, IReadOnlyList<DecalSpec> Decals);

    /// <summary> Marks every running publish stale and starts a new run. </summary>
    private Run Supersede()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            return new Run(++_runId, _cts.Token);
        }
    }

    private bool IsStale(Run run)
    {
        lock (_lock)
            return _disposed || run.Id != _runId;
    }

    /// <summary> Framework-thread part of a publish: the mesh snapshot, grouping, and the real upstream file of every surface. </summary>
    private List<SurfaceJob> Prepare(Guid collectionId, int objectIndex, IReadOnlyList<DecalSpec> decals, List<string> problems)
    {
        var jobs   = new List<SurfaceJob>();
        var meshes = _meshes.GetLocalPlayerMeshes();

        // Our own redirect must be gone before resolving, so the diffuse path resolves to the real upstream file and not to a previous bake.
        var removed = _penumbra.RemoveTemporaryMod(DecalPaths.TemporaryModTag, collectionId, DecalPaths.TemporaryModPriority);
        if (removed is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
            problems.Add($"could not remove the previous decal mod: {removed}");

        var unplaceable = new List<DecalSpec>();
        var groups      = DecalSurfaceGroups.GroupByMesh(decals, meshes, unplaceable);
        foreach (var surface in unplaceable.Select(d => d.Placement.Surface).Distinct())
            problems.Add($"no {surface.ToString().ToLowerInvariant()} skin drawn");

        foreach (var (mesh, specs) in groups)
        {
            var surface  = mesh.Surface;
            var upstream = ResolveUpstream(mesh.DiffuseGamePath, _penumbra.ResolveGameObjectPath(mesh.DiffuseGamePath, objectIndex));
            if (upstream is null)
            {
                problems.Add($"{surface}: the skin texture still resolves to a previous bake and no upstream file is known");
                continue;
            }

            jobs.Add(new SurfaceJob(surface, mesh, upstream, specs));
        }

        return jobs;
    }

    /// <summary>
    /// The file the bake starts from: Penumbra's resolution of the diffuse path (the game path itself when unmodified). A path
    /// inside the cache folder is one of our own bakes (the removal did not take, or another collection redirects there); the
    /// upstream remembered for that game path is used instead, and without one the surface is reported.
    /// </summary>
    private string? ResolveUpstream(string gamePath, string resolved)
    {
        if (string.IsNullOrEmpty(resolved))
            resolved = gamePath;

        if (BakeCache.IsCacheFile(_cacheDirectory, resolved))
            return _upstream.TryGetValue(gamePath, out var remembered) ? remembered : null;

        _upstream[gamePath] = resolved;
        return resolved;
    }

    // ---- thread pool ------------------------------------------------------------------------------

    private async Task RunAsync(Run run, Guid collectionId, List<SurfaceJob> jobs, List<string> problems)
    {
        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var produced  = new List<string>();
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            foreach (var job in jobs)
            {
                run.Token.ThrowIfCancellationRequested();
                var output = await BakeSurfaceAsync(run, job, problems);
                if (output is null)
                    continue;

                produced.Add(output);
                redirects[job.Mesh.DiffuseGamePath] = output;
            }

            foreach (var deleted in BakeCache.Trim(_cacheDirectory, BakeCache.MaxFiles, produced))
                _log.Debug($"Decal cache: deleted {Path.GetFileName(deleted)}.");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer publish or a clear, which owns the collection now.
            Interlocked.Decrement(ref _inFlight);
            return;
        }
        catch (Exception ex)
        {
            problems.Add($"decal publish failed: {ex.Message}");
            LogOnce($"Decal publish failed: {ex.Message}", ex);
        }

        try
        {
            await _framework.RunOnFrameworkThread(() => Complete(run, collectionId, redirects, problems));
        }
        catch (Exception ex)
        {
            // The framework refused the callback (unloading): Complete never ran.
            LogOnce($"Decal publish could not complete: {ex.Message}", ex);
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary> Bakes one surface, or finds its bake in the cache, and returns the output file; null when nothing could be produced (the problems say why). </summary>
    private async Task<string?> BakeSurfaceAsync(Run run, SurfaceJob job, List<string> problems)
    {
        var ct     = run.Token;
        var inputs = new List<(DecalSpec Spec, string ImageHash, byte[] Bytes)>();
        foreach (var spec in job.Decals)
        {
            var path = Path.Combine(_imageDirectory, spec.ImageFile);
            if (!File.Exists(path))
            {
                problems.Add($"decal image missing: {spec.ImageFile}");
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(path, ct);
            inputs.Add((spec, BakeCacheKey.BytesHash(bytes), bytes));
        }

        if (inputs.Count == 0)
            return null;

        // Everything that shapes the result names the output, and an existing output is the result.
        var key = BakeCacheKey.Compute(BakeCacheKey.UpstreamStamp(job.Upstream), job.Mesh.ModelGamePath, BakeCacheKey.MeshFingerprint(job.Mesh),
            inputs.Select(i => (i.ImageHash, i.Spec.ContentKey())).ToList());
        var output = BakeCacheKey.OutputFile(_cacheDirectory, job.Surface, key);
        if (BakeCache.IsValidOutput(output))
        {
            BakeCache.Touch(output);
            _log.Debug($"Decal cache hit for {job.Surface}: {Path.GetFileName(output)}.");
            return output;
        }

        var target = await DecodeUpstreamAsync(job.Upstream, ct);
        if (target is null)
        {
            problems.Add($"{job.Surface}: could not read the skin texture {job.Upstream}");
            return null;
        }

        var baked = 0;
        foreach (var (spec, hash, bytes) in inputs)
        {
            ct.ThrowIfCancellationRequested();
            var decal = await DecodeDecalAsync(hash, bytes, ct);
            if (decal is null)
            {
                problems.Add($"decal image unreadable: {spec.ImageFile}");
                continue;
            }

            try
            {
                DecalBaker.Bake(target, job.Mesh, decal, spec.Placement, spec.Opacity);
                baked++;
            }
            catch (Exception ex)
            {
                problems.Add($"{spec.ImageFile}: bake failed: {ex.Message}");
            }
        }

        if (baked == 0)
            return null;

        ct.ThrowIfCancellationRequested();
        return await ConvertAsync(run, job, target, output, problems);
    }

    /// <summary> Writes the bake as a BC7 .tex with mip maps through Penumbra, to a temporary name first so only complete files ever carry a cache name. </summary>
    private async Task<string?> ConvertAsync(Run run, SurfaceJob job, RgbaImage image, string output, List<string> problems)
    {
        var temp = BakeCache.TempFile(output, run.Id);
        try
        {
            await _penumbra.ConvertTextureData(image.Pixels, image.Width, temp, TextureType.Bc7Tex, mipMaps: true);
            if (!BakeCache.IsValidOutput(temp))
            {
                problems.Add($"{job.Surface}: texture conversion produced no file");
                return null;
            }

            File.Move(temp, output, overwrite: true);
            return output;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            problems.Add($"{job.Surface}: texture conversion failed: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (IOException)
            {
                // Left for housekeeping.
            }
            catch (UnauthorizedAccessException)
            {
                // Left for housekeeping.
            }
        }
    }

    /// <summary> The upstream skin texture as RGBA8: a rooted path is a file Penumbra redirects to, anything else is the game's own file. </summary>
    private async Task<RgbaImage?> DecodeUpstreamAsync(string upstream, CancellationToken ct)
    {
        try
        {
            var shared = Path.IsPathRooted(upstream) ? _textures.GetFromFileAbsolute(upstream) : _textures.GetFromGame(upstream);
            var wrap   = await shared.RentAsync(ct);
            return await ReadBackAsync(wrap, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogOnce($"Could not read the skin texture {upstream}: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary> A decal image (PNG or any format the texture provider reads) as RGBA8, cached by content. </summary>
    private async Task<RgbaImage?> DecodeDecalAsync(string hash, byte[] bytes, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_decalImages.TryGetValue(hash, out var cached))
                return cached;
        }

        try
        {
            var wrap  = await _textures.CreateFromImageAsync(bytes, "Lamina decal", ct);
            var image = await ReadBackAsync(wrap, ct);
            lock (_lock)
            {
                if (_decalImages.Count >= DecalImageCacheSize)
                    _decalImages.Clear();
                _decalImages[hash] = image;
            }

            return image;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogOnce($"Could not decode a decal image: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// The top mip as tight RGBA8. The readback converts on the GPU into the requested format (DXGI_FORMAT_R8G8B8A8_UNORM), so
    /// block compressed skin textures and PNG decals all arrive as plain 8-bit RGBA; the wrap is disposed by the readback.
    /// </summary>
    private async Task<RgbaImage> ReadBackAsync(IDalamudTextureWrap wrap, CancellationToken ct)
    {
        var (spec, data) = await _readback.GetRawImageAsync(wrap, new TextureModificationArgs { DxgiFormat = RawImageRepacker.R8G8B8A8Unorm }, leaveWrapOpen: false, ct);
        return RawImageRepacker.ToRgba(spec.Width, spec.Height, spec.Pitch, spec.DxgiFormat, data);
    }

    // ---- framework thread (completion) ------------------------------------------------------------

    /// <summary> Installs the redirects of a run that is still current and raises <see cref="Completed"/>; a superseded run is dropped silently. </summary>
    private void Complete(Run run, Guid collectionId, Dictionary<string, string> redirects, List<string> problems)
    {
        try
        {
            if (IsStale(run))
                return;

            var published = 0;
            if (redirects.Count > 0)
            {
                var code = _penumbra.AddTemporaryMod(DecalPaths.TemporaryModTag, collectionId, redirects, DecalPaths.TemporaryModPriority);
                foreach (var (gamePath, file) in redirects)
                    _log.Information($"Decal redirect ({code}) in collection {collectionId}: {gamePath} -> {file}");
                if (code == PenumbraApiEc.Success)
                    published = redirects.Count;
                else
                    problems.Add($"Penumbra refused the decal mod: {code}");
            }

            Completed?.Invoke(new DecalPublishResult(problems.Count == 0 && published > 0, published, problems.AsReadOnly()));
        }
        catch (Exception ex)
        {
            LogOnce($"Decal publish completion failed: {ex.Message}", ex);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary> Logs a message once (warning, or error with the exception); repeats of the same text are dropped. </summary>
    private void LogOnce(string message, Exception? exception = null)
    {
        lock (_lock)
        {
            if (_logged.Count > 256)
                _logged.Clear();
            if (!_logged.Add(message))
                return;
        }

        if (exception is null)
            _log.Warning(message);
        else
            _log.Error(exception, message);
    }
}
