using System.IO;
using System.Text;
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
/// Before any of that, a publish computes a cheap situation key (drawn outfit, mods, decals, image stamps, collection): the
/// situation already installed needs nothing at all, a remembered one re-installs its redirect map without baking
/// (<see cref="RedirectLedger"/>), and only a new situation walks the full path.
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

    /// <summary>
    /// Diffuse game path → the real upstream file it last resolved to and the mods key it was resolved under, so a redirect to
    /// our own bake can be bypassed without taking the mod out while the mods are unchanged. Framework thread.
    /// </summary>
    private readonly Dictionary<string, (string Upstream, string? ModsKey)> _upstream = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RgbaImage> _decalImages = new(StringComparer.Ordinal);

    /// <summary> What is installed per collection, to tell an identical re-install (no redraw) from a change. Framework thread. </summary>
    private readonly RedirectLedger _ledger = new();
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
        // Penumbra drops every temporary mod when it unloads: whatever was installed is gone, and the next install counts as a change.
        _penumbra.AvailabilityChanged += OnPenumbraAvailabilityChanged;
    }

    /// <inheritdoc/>
    public bool Busy
        => Volatile.Read(ref _inFlight) > 0;

    /// <inheritdoc/>
    public event Action<DecalPublishResult>? Completed;

    /// <inheritdoc/>
    public void Publish(Guid collectionId, int objectIndex, IReadOnlyList<DecalSpec> decals, string? modsKey = null)
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

            // Fast paths: the installed situation needs nothing; a remembered situation re-installs its bakes without baking.
            var situation = SituationKey(collectionId, decals, modsKey);
            if (situation is not null)
            {
                if (_ledger.IsInstalled(collectionId, situation))
                {
                    _log.Debug("Decals: the installed bakes already match the drawn outfit, mods and decals; nothing to do.");
                    return;
                }

                if (TryReinstall(collectionId, situation))
                    return;
            }

            var run      = Supersede();
            var problems = new List<string>();
            var jobs     = Prepare(collectionId, objectIndex, decals, modsKey, problems);
            var protect  = _ledger.ReferencedFiles();

            Interlocked.Increment(ref _inFlight);
            _ = Task.Run(() => RunAsync(run, collectionId, situation, jobs, protect, problems));
        }
        catch (Exception ex)
        {
            LogOnce($"Decal publish could not start: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Everything that decides a publish's result, read without building meshes: the collection, the mods key, the drawn
    /// models (files, shape / attribute masks, load states) and every decal with its image file's stamp. Null while nothing is
    /// drawn or the models are still loading (a transient key must not be remembered).
    /// </summary>
    private string? SituationKey(Guid collectionId, IReadOnlyList<DecalSpec> decals, string? modsKey)
    {
        var draw = _meshes.DrawObjectState();
        if (draw is null || !draw.Loaded)
            return null;

        var sb = new StringBuilder(256);
        sb.Append(collectionId.ToString("N")).Append('\n').Append(modsKey ?? string.Empty).Append('\n').Append(draw.Key).Append('\n');
        foreach (var spec in decals)
            sb.Append(spec.ContentKey()).Append('|').Append(ImageStamp(spec.ImageFile)).Append('\n');
        return sb.ToString();
    }

    /// <summary> Name, size and last write time of a decal image, so an edited image is a new situation. </summary>
    private string ImageStamp(string imageFile)
    {
        try
        {
            var info = new FileInfo(Path.Combine(_imageDirectory, imageFile));
            return info.Exists
                ? FormattableString.Invariant($"{imageFile.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")
                : imageFile.ToLowerInvariant() + "|missing";
        }
        catch (Exception)
        {
            return imageFile.ToLowerInvariant() + "|unreadable";
        }
    }

    /// <summary> Re-installs the redirect map remembered for the situation, when its bake files still exist; completes synchronously. </summary>
    private bool TryReinstall(Guid collectionId, string situation)
    {
        var remembered = _ledger.Recall(situation);
        if (remembered is null)
            return false;

        if (!remembered.Values.All(BakeCache.IsValidOutput))
        {
            _ledger.Drop(situation);
            return false;
        }

        Supersede();
        var code = _penumbra.AddTemporaryMod(DecalPaths.TemporaryModTag, collectionId, remembered, DecalPaths.TemporaryModPriority);
        if (code != PenumbraApiEc.Success)
        {
            LogOnce($"Could not re-install the remembered decal mod ({code}); baking instead.");
            _ledger.Forget(collectionId);
            return false;
        }

        foreach (var file in remembered.Values)
            BakeCache.Touch(file);
        var changed = _ledger.Install(collectionId, situation, remembered);
        _log.Information($"Decal redirects re-installed from memory in collection {collectionId}: {remembered.Count} surface(s), {(changed ? "changed" : "unchanged")}.");
        Completed?.Invoke(new DecalPublishResult(true, remembered.Count, Array.Empty<string>(), changed));
        return true;
    }

    /// <inheritdoc/>
    public void Clear(Guid collectionId)
    {
        if (_disposed)
            return;

        try
        {
            Supersede();
            _ledger.Forget(collectionId);
            var code = _penumbra.RemoveTemporaryMod(DecalPaths.TemporaryModTag, collectionId, DecalPaths.TemporaryModPriority);
            if (code is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
                LogOnce($"Could not remove the decal mod from collection {collectionId}: {code}");
        }
        catch (Exception ex)
        {
            LogOnce($"Decal clear failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public DecalCleanupResult CleanUp(Guid? collectionId, DecalCleanupMode mode, bool removeTemporaryMod)
    {
        var problems = new List<string>();
        var maps     = 0;
        var bakes    = 0;
        var temps    = 0;
        var removed  = false;
        try
        {
            // A run in flight would install into a collection this is about to clean; it is dropped (its temp file, still open, survives until next time).
            Supersede();

            if (mode == DecalCleanupMode.Everything || removeTemporaryMod)
            {
                if (collectionId is { } collection && _penumbra.Available)
                {
                    var code = _penumbra.RemoveTemporaryMod(DecalPaths.TemporaryModTag, collection, DecalPaths.TemporaryModPriority);
                    if (code is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
                        removed = true;
                    else
                        problems.Add($"could not remove the decal mod: {code}");
                    _ledger.Forget(collection);
                }
                else
                {
                    problems.Add("the temporary mod could not be removed: Penumbra or the collection is unavailable");
                }

                _upstream.Clear();
            }

            if (mode == DecalCleanupMode.Everything)
            {
                maps           = _ledger.ForgetAll();
                (bakes, temps) = BakeCache.Sweep(_cacheDirectory, Array.Empty<string>());
            }
            else
            {
                maps           = _ledger.KeepOnly(collectionId is { } c ? _ledger.InstalledSituation(c) : null);
                (bakes, temps) = BakeCache.Sweep(_cacheDirectory, _ledger.InstalledFiles());
            }
        }
        catch (Exception ex)
        {
            problems.Add($"cleanup failed: {ex.Message}");
            LogOnce($"Decal cleanup failed: {ex.Message}", ex);
        }

        return new DecalCleanupResult(maps, bakes, temps, removed, problems);
    }

    /// <summary> Cancels a running publish. Nothing is removed from Penumbra here: the engine's cleanup does that. </summary>
    public void Dispose()
    {
        _disposed = true;
        _penumbra.AvailabilityChanged -= OnPenumbraAvailabilityChanged;
        lock (_lock)
            _cts?.Cancel();
    }

    private void OnPenumbraAvailabilityChanged()
    {
        if (!_penumbra.Available)
            _ledger.ForgetInstalled();
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
    private List<SurfaceJob> Prepare(Guid collectionId, int objectIndex, IReadOnlyList<DecalSpec> decals, string? modsKey, List<string> problems)
    {
        var jobs   = new List<SurfaceJob>();
        var meshes = _meshes.GetLocalPlayerMeshes();

        var unplaceable = new List<DecalSpec>();
        var groups      = DecalSurfaceGroups.GroupByMesh(decals, meshes, unplaceable);
        foreach (var surface in unplaceable.Select(d => d.Placement.Surface).Distinct())
            problems.Add($"no {surface.ToString().ToLowerInvariant()} skin drawn");

        var removed = false;
        foreach (var (mesh, specs) in groups)
        {
            var surface  = mesh.Surface;
            var upstream = ResolveUpstream(collectionId, objectIndex, mesh.DiffuseGamePath, modsKey, ref removed, problems);
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
    /// inside the cache folder is one of our own bakes; the upstream remembered for that game path serves while the mods it
    /// was resolved under are unchanged, so an installed decal mod normally stays in place. Otherwise our mod is taken out of
    /// the collection (once per publish; re-added when the bake completes) and the path resolved again; if it still lands in
    /// the cache (another collection redirects there) the remembered upstream is the last resort, and without one the surface
    /// is reported.
    /// </summary>
    private string? ResolveUpstream(Guid collectionId, int objectIndex, string gamePath, string? modsKey, ref bool removed, List<string> problems)
    {
        var resolved = Resolve(gamePath, objectIndex);
        if (!BakeCache.IsCacheFile(_cacheDirectory, resolved))
            return Remember(gamePath, resolved, modsKey);

        if (_upstream.TryGetValue(gamePath, out var known) && string.Equals(known.ModsKey, modsKey, StringComparison.Ordinal))
            return known.Upstream;

        if (!removed)
        {
            removed = true;
            var code = _penumbra.RemoveTemporaryMod(DecalPaths.TemporaryModTag, collectionId, DecalPaths.TemporaryModPriority);
            if (code is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
                problems.Add($"could not remove the previous decal mod: {code}");
            _ledger.MarkRemoved(collectionId);

            resolved = Resolve(gamePath, objectIndex);
            if (!BakeCache.IsCacheFile(_cacheDirectory, resolved))
                return Remember(gamePath, resolved, modsKey);
        }

        return _upstream.TryGetValue(gamePath, out known) ? known.Upstream : null;
    }

    private string Resolve(string gamePath, int objectIndex)
    {
        var resolved = _penumbra.ResolveGameObjectPath(gamePath, objectIndex);
        return string.IsNullOrEmpty(resolved) ? gamePath : resolved;
    }

    private string Remember(string gamePath, string upstream, string? modsKey)
    {
        _upstream[gamePath] = (upstream, modsKey);
        return upstream;
    }

    // ---- thread pool ------------------------------------------------------------------------------

    private async Task RunAsync(Run run, Guid collectionId, string? situation, List<SurfaceJob> jobs, IReadOnlyCollection<string> protect, List<string> problems)
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

            // Housekeeping keeps what was just produced and every file an installed or remembered map still names.
            foreach (var deleted in BakeCache.Trim(_cacheDirectory, BakeCache.MaxFiles, protect.Concat(produced).ToList()))
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
            await _framework.RunOnFrameworkThread(() => Complete(run, collectionId, situation, redirects, problems));
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

    /// <summary>
    /// Installs the redirects of a run that is still current and raises <see cref="Completed"/>; a superseded run is dropped
    /// silently. When <see cref="Prepare"/> took the temporary mod out this re-adds it; when the map equals the one installed
    /// before, the game already loaded those files by path and the result says no redraw is needed.
    /// </summary>
    private void Complete(Run run, Guid collectionId, string? situation, Dictionary<string, string> redirects, List<string> problems)
    {
        try
        {
            if (IsStale(run))
                return;

            var published = 0;
            var changed   = true;
            if (redirects.Count > 0)
            {
                var code = _penumbra.AddTemporaryMod(DecalPaths.TemporaryModTag, collectionId, redirects, DecalPaths.TemporaryModPriority);
                foreach (var (gamePath, file) in redirects)
                    _log.Information($"Decal redirect ({code}) in collection {collectionId}: {gamePath} -> {file}");
                if (code == PenumbraApiEc.Success)
                {
                    published = redirects.Count;
                    changed   = _ledger.Install(collectionId, situation, redirects);
                    if (!changed)
                        _log.Debug("Decal redirects unchanged; the character already shows these bakes.");
                    // Only a complete result is worth remembering: a map missing a surface would be re-installed as is.
                    if (situation is not null && problems.Count == 0)
                        _ledger.Remember(situation, redirects);
                }
                else
                {
                    problems.Add($"Penumbra refused the decal mod: {code}");
                    _ledger.Forget(collectionId);
                }
            }
            else if (_ledger.InstalledSituation(collectionId) is null)
            {
                // Nothing produced, and Prepare took the previous mod out: the collection redirects nothing now.
                _ledger.Forget(collectionId);
            }

            Completed?.Invoke(new DecalPublishResult(problems.Count == 0 && published > 0, published, problems.AsReadOnly(), changed));
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
