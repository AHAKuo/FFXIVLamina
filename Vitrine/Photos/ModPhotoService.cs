using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Vitrine.Photos;

/// <summary> Whether a photo is available for a mod. </summary>
public enum PhotoStatus
{
    /// <summary> A file is ready at <see cref="PhotoState.Path"/>. </summary>
    Ready,
    /// <summary> A lookup is queued or in flight. </summary>
    Pending,
    /// <summary> Nothing found (yet); the category placeholder is shown. </summary>
    Missing,
}

/// <summary> Result of <see cref="ModPhotoService.GetPhoto"/>. </summary>
public readonly record struct PhotoState(PhotoStatus Status, string? Path)
{
    public static readonly PhotoState Pending = new(PhotoStatus.Pending, null);
    public static readonly PhotoState Missing = new(PhotoStatus.Missing, null);
}

/// <summary> Counters for the Settings tab. </summary>
public readonly record struct PhotoCounters(int Cached, int Local, int Pending, int Failed);

/// <summary>
/// Resolves one display photo per mod: the mod's own image, a conventional preview in its folder, else the mod page's
/// link-preview image (og:image and friends) downloaded into the photo cache. Lookups run on background tasks (at most
/// two at a time, with a polite delay between page fetches); every result is applied on the framework thread in <see cref="Tick"/>,
/// so the index, the <see cref="ModInformation"/> objects and the UI never race.
/// </summary>
public sealed class ModPhotoService : IDisposable
{
    private sealed record WorkItem(string ModDirectory, string Website);

    private sealed record WorkResult(string ModDirectory, PhotoSource Source, string Path, string Website, string ImageUrl, string Error);

    /// <summary> Delay between page fetches per worker, so mod sites are not hammered. </summary>
    public static readonly TimeSpan PoliteDelay = TimeSpan.FromMilliseconds(300);

    private readonly string _photosDirectory;
    private readonly IPhotoHttp _http;
    private readonly Func<string> _modRoot;
    private readonly Func<bool> _fetchEnabled;
    private readonly Action<string> _logWarning;
    private readonly Action<string> _logDebug;
    private readonly Func<DateTime> _clock;
    private readonly int _maxWorkers;
    private readonly PhotoIndex _index;

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly ConcurrentQueue<WorkResult> _results = new();
    private readonly HashSet<string> _inProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModInformation> _mods = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _workerLock = new();
    private int _activeWorkers;
    private DateTime _nextSave = DateTime.MinValue;
    private int _failed;
    private bool _disposed;

    /// <param name="photosDirectory"> Cache folder (…/vitrine/photos). </param>
    /// <param name="http"> Network access; a fake in tests. </param>
    /// <param name="modRoot"> Penumbra's mod root, for local discovery. </param>
    /// <param name="fetchEnabled"> The "fetch from mod pages" setting. </param>
    public ModPhotoService(string photosDirectory, IPhotoHttp http, Func<string> modRoot, Func<bool> fetchEnabled,
        Action<string> logWarning, Action<string> logDebug, Func<DateTime>? clock = null, int maxWorkers = 2)
    {
        _photosDirectory = photosDirectory;
        _http            = http;
        _modRoot         = modRoot;
        _fetchEnabled    = fetchEnabled;
        _logWarning      = logWarning;
        _logDebug        = logDebug;
        _clock           = clock ?? (() => DateTime.UtcNow);
        _maxWorkers      = Math.Max(1, maxWorkers);
        _index           = new PhotoIndex(Path.Combine(photosDirectory, "index.json"), logWarning);

        Directory.CreateDirectory(photosDirectory);
        _index.Load();
        _failed = _index.Entries.Values.Count(e => e.Source == PhotoSource.None && e.LastError.Length > 0);
    }

    /// <summary> The cache folder. </summary>
    public string PhotosDirectory
        => _photosDirectory;

    /// <summary> Raised on the framework thread after a lookup finished (ready or failed). </summary>
    public event Action<string>? PhotoResolved;

    /// <summary> Counters for the Settings tab. </summary>
    public PhotoCounters Counters
    {
        get
        {
            int cached = 0, local = 0;
            foreach (var entry in _index.Entries.Values)
            {
                if (entry.Source == PhotoSource.Fetched) cached++;
                else if (entry.Source == PhotoSource.Local) local++;
            }

            return new PhotoCounters(cached, local, _queue.Count + _inProgress.Count, _failed);
        }
    }

    /// <summary>
    /// Returns the photo state of a mod. When nothing is known yet and <paramref name="request"/> is true, resolves a local
    /// photo immediately (cheap file checks) or queues a page lookup. Safe to call every frame.
    /// </summary>
    public PhotoState GetPhoto(ModInformation mod, bool request = true)
    {
        if (string.IsNullOrEmpty(mod.ModDirectory))
            return PhotoState.Missing;

        _mods[mod.ModDirectory] = mod;
        if (_inProgress.Contains(mod.ModDirectory))
            return PhotoState.Pending;

        var now   = _clock();
        var entry = _index.Get(mod.ModDirectory);
        if (entry is not null)
        {
            if (entry.Source != PhotoSource.None)
            {
                if (File.Exists(entry.Path))
                {
                    if (!string.Equals(mod.ImagePath, entry.Path, StringComparison.Ordinal))
                        mod.ImagePath = entry.Path;
                    return new PhotoState(PhotoStatus.Ready, entry.Path);
                }

                // File vanished (cache cleared by hand, mod folder changed): look again.
                _index.Remove(mod.ModDirectory);
                entry = null;
            }
            else if (!request || !PhotoIndex.MayRetry(entry, mod.Website, now))
            {
                return PhotoState.Missing;
            }
        }

        if (!request)
            return PhotoState.Missing;

        return Enqueue(mod, force: false);
    }

    /// <summary> Forgets what is known about a mod and looks again (the "Refresh photo" button). </summary>
    public void RequestRefresh(ModInformation mod)
    {
        if (string.IsNullOrEmpty(mod.ModDirectory) || _inProgress.Contains(mod.ModDirectory))
            return;

        var entry = _index.Get(mod.ModDirectory);
        if (entry is not null)
        {
            if (entry.Source == PhotoSource.Fetched)
                TryDelete(entry.Path);
            _index.Remove(mod.ModDirectory);
            if (entry.Source == PhotoSource.None && entry.LastError.Length > 0)
                _failed = Math.Max(0, _failed - 1);
        }

        if (string.Equals(mod.ImagePath, entry?.Path, StringComparison.Ordinal))
            mod.ImagePath = string.Empty;

        _mods[mod.ModDirectory] = mod;
        Enqueue(mod, force: true);
    }

    /// <summary> Queues every mod that has no photo, ignoring the retry window (the "Fetch all missing photos" button). Returns how many were queued. </summary>
    public int FetchAllMissing(IEnumerable<ModInformation> mods)
    {
        var queued = 0;
        foreach (var mod in mods)
        {
            if (string.IsNullOrEmpty(mod.ModDirectory) || _inProgress.Contains(mod.ModDirectory))
                continue;
            var entry = _index.Get(mod.ModDirectory);
            if (entry is not null && entry.Source != PhotoSource.None && File.Exists(entry.Path))
                continue;

            _mods[mod.ModDirectory] = mod;
            if (Enqueue(mod, force: true).Status == PhotoStatus.Pending)
                queued++;
        }

        return queued;
    }

    /// <summary> Deletes every cached download and the index. Local photos are forgotten too and rediscovered on demand. </summary>
    public void ClearCache()
    {
        foreach (var (dir, entry) in _index.Entries)
        {
            if (entry.Source == PhotoSource.Fetched)
                TryDelete(entry.Path);
            if (_mods.TryGetValue(dir, out var mod) && string.Equals(mod.ImagePath, entry.Path, StringComparison.Ordinal))
                mod.ImagePath = string.Empty;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_photosDirectory))
                if (!file.EndsWith("index.json", StringComparison.OrdinalIgnoreCase))
                    TryDelete(file);
        }
        catch (Exception ex)
        {
            _logWarning($"Vitrine: could not clear the photo cache: {ex.Message}");
        }

        _index.Clear();
        _index.SaveIfDirty();
        _failed = 0;
    }

    /// <summary> Framework thread: applies finished lookups and saves the index (debounced). </summary>
    public void Tick()
    {
        var applied = false;
        while (_results.TryDequeue(out var result))
        {
            _inProgress.Remove(result.ModDirectory);
            var now      = _clock();
            var previous = _index.Get(result.ModDirectory);
            if (previous is { Source: PhotoSource.None } && previous.LastError.Length > 0)
                _failed = Math.Max(0, _failed - 1);
            var entry = new PhotoIndexEntry
            {
                Source       = result.Source,
                Path         = result.Path,
                SourceUrl    = result.Website,
                ImageUrl     = result.ImageUrl,
                FetchedAtUtc = now,
                LastError    = result.Error,
                Attempts     = (previous?.Attempts ?? 0) + 1,
                NextRetryUtc = result.Source == PhotoSource.None ? now + PhotoIndex.RetryWindow : null,
            };
            _index.Set(result.ModDirectory, entry);

            if (result.Source == PhotoSource.None && result.Error.Length > 0)
            {
                _failed++;
                _logDebug($"Vitrine: no photo for '{result.ModDirectory}': {result.Error}");
            }

            if (_mods.TryGetValue(result.ModDirectory, out var mod) && result.Source != PhotoSource.None)
                mod.ImagePath = result.Path;

            applied = true;
            PhotoResolved?.Invoke(result.ModDirectory);
        }

        var nowUtc = _clock();
        if (applied)
            _nextSave = nowUtc + TimeSpan.FromSeconds(2);
        if (_index.IsDirty && nowUtc >= _nextSave)
            _index.SaveIfDirty();
    }

    /// <summary>
    /// Resolves a local photo inline (Ready), queues a page lookup (Pending) or records that nothing can be done (Missing).
    /// <paramref name="force"/> only matters for bookkeeping of the entry; callers clear old entries themselves.
    /// </summary>
    public PhotoState Enqueue(ModInformation mod, bool force)
    {
        var allowFetch = _fetchEnabled() && IsHttpUrl(mod.Website);
        var folder     = ModFolder(mod.ModDirectory);

        // Local discovery is a handful of File.Exists calls: do it inline so cards fill instantly.
        var local = LocalPhotoFinder.Find(folder, mod.ImagePath);
        if (local is not null)
        {
            _index.Set(mod.ModDirectory, new PhotoIndexEntry
            {
                Source       = PhotoSource.Local,
                Path         = local,
                SourceUrl    = mod.Website,
                FetchedAtUtc = _clock(),
            });
            mod.ImagePath = local;
            PhotoResolved?.Invoke(mod.ModDirectory);
            return new PhotoState(PhotoStatus.Ready, local);
        }

        if (!allowFetch)
        {
            // Nothing to fetch: remember that so the file checks are not repeated every frame. A website appearing later retries.
            var existing = _index.Get(mod.ModDirectory);
            _index.Set(mod.ModDirectory, new PhotoIndexEntry
            {
                Source       = PhotoSource.None,
                SourceUrl    = mod.Website,
                FetchedAtUtc = _clock(),
                LastError    = string.Empty,
                Attempts     = force ? 0 : (existing?.Attempts ?? 0),
                NextRetryUtc = _clock() + PhotoIndex.RetryWindow,
            });
            return PhotoState.Missing;
        }

        if (!_inProgress.Add(mod.ModDirectory))
            return PhotoState.Pending;

        _queue.Enqueue(new WorkItem(mod.ModDirectory, mod.Website));
        StartWorkerIfNeeded();
        return PhotoState.Pending;
    }

    private void StartWorkerIfNeeded()
    {
        lock (_workerLock)
        {
            if (_disposed || _activeWorkers >= _maxWorkers)
                return;
            _activeWorkers++;
        }

        _ = Task.Run(WorkerLoopAsync, _cts.Token);
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && _queue.TryDequeue(out var item))
            {
                WorkResult result;
                try
                {
                    result = await ResolveAsync(item, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    result = new WorkResult(item.ModDirectory, PhotoSource.None, string.Empty, item.Website, string.Empty, ex.Message);
                }

                _results.Enqueue(result);
                try
                {
                    await Task.Delay(PoliteDelay, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_workerLock)
                _activeWorkers--;

            // Work queued while we were winding down.
            if (!_cts.IsCancellationRequested && !_queue.IsEmpty)
                StartWorkerIfNeeded();
        }
    }

    private async Task<WorkResult> ResolveAsync(WorkItem item, CancellationToken ct)
    {
        if (!Uri.TryCreate(item.Website, UriKind.Absolute, out var pageUrl))
            return new WorkResult(item.ModDirectory, PhotoSource.None, string.Empty, item.Website, string.Empty, "website is not a URL");

        Uri? imageUrl;
        var page = await _http.GetPageAsync(pageUrl, ct).ConfigureAwait(false);
        if (page.Error == "page is an image")
        {
            imageUrl = page.FinalUrl;
        }
        else
        {
            if (page.Html is null)
                return new WorkResult(item.ModDirectory, PhotoSource.None, string.Empty, item.Website, string.Empty, page.Error ?? "no page");

            imageUrl = PhotoPageParser.ExtractImageUrl(page.Html, page.FinalUrl);
            if (imageUrl is null)
                return new WorkResult(item.ModDirectory, PhotoSource.None, string.Empty, item.Website, string.Empty, "page declares no preview image");
        }

        var image = await _http.GetImageAsync(imageUrl, page.FinalUrl, ct).ConfigureAwait(false);
        if (image.Bytes is null || image.Bytes.Length == 0)
            return new WorkResult(item.ModDirectory, PhotoSource.None, string.Empty, item.Website, imageUrl.ToString(), image.Error ?? "empty image");

        var ext = PhotoIndex.ExtensionFor(image.ContentType, imageUrl);
        if (ext == ".webp")
            return new WorkResult(item.ModDirectory, PhotoSource.None, string.Empty, item.Website, imageUrl.ToString(), "unsupported format (webp)");

        var path = Path.Combine(_photosDirectory, PhotoIndex.CacheStem(item.ModDirectory) + ext);
        var tmp  = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, image.Bytes, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
        return new WorkResult(item.ModDirectory, PhotoSource.Fetched, path, item.Website, imageUrl.ToString(), string.Empty);
    }

    private string ModFolder(string modDirectory)
    {
        var root = _modRoot();
        return string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, modDirectory);
    }

    private static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    public void Dispose()
    {
        lock (_workerLock)
            _disposed = true;
        _cts.Cancel();
        _index.SaveIfDirty();
        _http.Dispose();
        _cts.Dispose();
    }
}
