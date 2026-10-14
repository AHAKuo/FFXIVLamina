using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using Penumbra.Api.Wrappers;

namespace Vitrine.Penumbra;

/// <summary>
/// Real <see cref="IPenumbraReader"/> over Penumbra's IPC. Every call is guarded: when Penumbra is missing or an IPC throws,
/// the call logs at debug level and returns an empty result. Availability is probed on construction, on Penumbra's
/// Initialized / Disposed events and whenever the plugin list changes.
/// </summary>
public sealed class PenumbraReader : IPenumbraReader
{
    /// <summary> Breaking API version the bundled Penumbra.Api targets (the ".V5" IPC labels). </summary>
    public const int RequiredBreakingVersion = 5;

    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog _log;

    private readonly ApiVersion _apiVersion;
    private readonly GetModDirectory _getModDirectory;
    private readonly GetModList _getModList;
    private readonly GetChangedItems _getChangedItems;
    private readonly GetAvailableModSettings _getAvailableModSettings;
    private readonly GetCollections _getCollections;
    private readonly GetAllModSettings _getAllModSettings;
    private readonly GetCollectionForObject _getCollectionForObject;
    private readonly GetCollection _getCollection;
    private readonly SetTemporaryModSettings _setTemporaryModSettings;
    private readonly RemoveTemporaryModSettings _removeTemporaryModSettings;
    private readonly RedrawObject               _redrawObject;
    private readonly ModManagerWrapper _modManager = new();

    private readonly IDisposable _modAdded;
    private readonly IDisposable _modDeleted;
    private readonly IDisposable _modMoved;
    private readonly IDisposable _initialized;
    private readonly IDisposable _disposed;

    private readonly IDalamudPluginInterface.ActivePluginsChangedDelegate _onPluginsChanged;
    private bool _available;
    private bool _adapterConnected;
    private int _warningBudget = 5;

    public PenumbraReader(IDalamudPluginInterface pi, IPluginLog log)
    {
        _pi  = pi;
        _log = log;

        _apiVersion                 = new ApiVersion(pi);
        _getModDirectory            = new GetModDirectory(pi);
        _getModList                 = new GetModList(pi);
        _getChangedItems            = new GetChangedItems(pi);
        _getAvailableModSettings    = new GetAvailableModSettings(pi);
        _getCollections             = new GetCollections(pi);
        _getAllModSettings          = new GetAllModSettings(pi);
        _getCollectionForObject     = new GetCollectionForObject(pi);
        _getCollection              = new GetCollection(pi);
        _setTemporaryModSettings    = new SetTemporaryModSettings(pi);
        _removeTemporaryModSettings = new RemoveTemporaryModSettings(pi);
        _redrawObject               = new RedrawObject(pi);

        _modAdded    = ModAddedEvent.Subscriber(pi, dir => ModAdded?.Invoke(dir));
        _modDeleted  = ModDeletedEvent.Subscriber(pi, dir => ModDeleted?.Invoke(dir));
        _modMoved    = ModMovedEvent.Subscriber(pi, (o, n) => ModMoved?.Invoke(o, n));
        _initialized = Initialized.Subscriber(pi, Probe);
        _disposed    = Disposed.Subscriber(pi, OnPenumbraDisposed);
        _onPluginsChanged = _ => Probe();
        pi.ActivePluginsChanged += _onPluginsChanged;

        Probe();
    }

    /// <inheritdoc/>
    public bool Available
        => _available;

    /// <inheritdoc/>
    public (int Breaking, int Feature)? ApiVersion { get; private set; }

    /// <inheritdoc/>
    public event Action? AvailabilityChanged;

    /// <inheritdoc/>
    public event Action<string>? ModAdded;

    /// <inheritdoc/>
    public event Action<string>? ModDeleted;

    /// <inheritdoc/>
    public event Action<string, string>? ModMoved;

    /// <inheritdoc/>
    public void Probe()
    {
        bool now;
        try
        {
            var (breaking, feature) = _apiVersion.Invoke();
            ApiVersion = (breaking, feature);
            now        = breaking == RequiredBreakingVersion;
            if (!now)
                _log.Warning($"Vitrine: Penumbra API {breaking}.{feature} does not match the bundled API (breaking {RequiredBreakingVersion}); mod view disabled.");
        }
        catch
        {
            ApiVersion = null;
            now        = false;
        }

        if (now && !_adapterConnected)
        {
            try
            {
                _adapterConnected = _modManager.Reconnect(_pi);
                if (!_adapterConnected)
                    _log.Debug("Vitrine: Penumbra mod manager adapter unavailable, falling back to meta.json.");
            }
            catch (Exception ex)
            {
                _adapterConnected = false;
                _log.Debug($"Vitrine: mod manager adapter failed: {ex.Message}");
            }
        }

        if (now == _available)
            return;

        _available = now;
        _log.Information($"Vitrine: Penumbra {(now ? "available" : "unavailable")}.");
        AvailabilityChanged?.Invoke();
    }

    private void OnPenumbraDisposed()
    {
        _adapterConnected = false;
        try { _modManager.Disconnect(); }
        catch { /* adapter already gone */ }

        if (!_available)
            return;

        _available = false;
        ApiVersion = null;
        AvailabilityChanged?.Invoke();
    }

    /// <inheritdoc/>
    public string GetModRootDirectory()
        => Call(() => _getModDirectory.Invoke() ?? string.Empty, string.Empty, nameof(GetModRootDirectory));

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> GetModList()
        => Call<IReadOnlyDictionary<string, string>>(() => _getModList.Invoke(), new Dictionary<string, string>(), nameof(GetModList));

    /// <inheritdoc/>
    public ModMeta? GetModMeta(string modDirectory, string modName)
    {
        if (!_available || !_adapterConnected)
            return null;

        try
        {
            using var mod = _modManager.GetByName((modDirectory, modName));
            if (mod is null || !mod.HasAdapter)
                return null;

            var modPath = mod.ModPath ?? string.Empty;
            var image   = mod.Image;
            var imagePath = string.IsNullOrWhiteSpace(image)
                ? string.Empty
                : Path.IsPathRooted(image) ? image : Path.Combine(modPath, image);
            var tags = new List<string>();
            try { tags.AddRange(mod.ModTags); }
            catch { /* optional */ }

            return new ModMeta(mod.Name, mod.Author, mod.Description, mod.ModVersion, mod.Website, imagePath, tags, modPath);
        }
        catch (Exception ex)
        {
            Warn($"mod adapter for '{modDirectory}'", ex);
            _adapterConnected = false;
            return null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> GetChangedItems(string modDirectory)
        => Call<IReadOnlyDictionary<string, object?>>(() => _getChangedItems.Invoke(modDirectory, string.Empty) ?? new Dictionary<string, object?>(),
            new Dictionary<string, object?>(), nameof(GetChangedItems));

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, (string[] Options, int GroupType)>? GetAvailableModSettings(string modDirectory)
        => Call<IReadOnlyDictionary<string, (string[], int)>?>(() =>
        {
            var result = _getAvailableModSettings.Invoke(modDirectory);
            return result?.ToDictionary(kv => kv.Key, kv => (kv.Value.Item1, (int)kv.Value.Item2));
        }, null, nameof(GetAvailableModSettings));

    /// <inheritdoc/>
    public IReadOnlyDictionary<Guid, string> GetCollections()
        => Call<IReadOnlyDictionary<Guid, string>>(() => _getCollections.Invoke(), new Dictionary<Guid, string>(), nameof(GetCollections));

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, ModSettingsInCollection>? GetAllModSettings(Guid collectionId)
        => Call<IReadOnlyDictionary<string, ModSettingsInCollection>?>(() =>
        {
            var (code, dict) = _getAllModSettings.Invoke(collectionId);
            if (code != PenumbraApiEc.Success || dict is null)
                return null;

            return dict.ToDictionary(kv => kv.Key, kv => new ModSettingsInCollection(
                kv.Value.Item1, kv.Value.Item2,
                kv.Value.Item3.ToDictionary(s => s.Key, s => (IReadOnlyList<string>)s.Value),
                kv.Value.Item4, kv.Value.Item5), StringComparer.OrdinalIgnoreCase);
        }, null, nameof(GetAllModSettings));

    /// <inheritdoc/>
    public (Guid Id, string Name, bool IndividualSet)? GetCollectionForObject(int objectIndex)
        => Call<(Guid, string, bool)?>(() =>
        {
            var (valid, individual, collection) = _getCollectionForObject.Invoke(objectIndex);
            return valid ? (collection.Id, collection.Name, individual) : null;
        }, null, nameof(GetCollectionForObject));

    /// <inheritdoc/>
    public (Guid Id, string Name)? GetSpecialCollection(ApiCollectionType type)
        => Call<(Guid, string)?>(() => _getCollection.Invoke(type), null, nameof(GetSpecialCollection));

    /// <inheritdoc/>
    public int SetTemporaryModSettings(Guid collectionId, string modDirectory, bool enabled, int priority,
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings, string source, int key)
        => Call(() => (int)_setTemporaryModSettings.Invoke(collectionId, modDirectory, false, enabled, priority, settings, source, key),
            (int)PenumbraApiEc.UnknownError, nameof(SetTemporaryModSettings));

    /// <inheritdoc/>
    public int RemoveTemporaryModSettings(Guid collectionId, string modDirectory, int key)
        => Call(() => (int)_removeTemporaryModSettings.Invoke(collectionId, modDirectory, key),
            (int)PenumbraApiEc.UnknownError, nameof(RemoveTemporaryModSettings));

    /// <inheritdoc/>
    public void RedrawObject(int objectIndex)
        => Call(() => { _redrawObject.Invoke(objectIndex); return 0; }, 0, nameof(RedrawObject));

    private T Call<T>(Func<T> call, T fallback, string what)
    {
        if (!_available)
            return fallback;

        try
        {
            return call();
        }
        catch (Exception ex)
        {
            Warn(what, ex);
            return fallback;
        }
    }

    private void Warn(string what, Exception ex)
    {
        if (_warningBudget > 0)
        {
            _warningBudget--;
            _log.Warning($"Vitrine: Penumbra IPC {what} failed: {ex.Message}");
        }
        else
        {
            _log.Debug($"Vitrine: Penumbra IPC {what} failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _pi.ActivePluginsChanged -= _onPluginsChanged;
        _modAdded.Dispose();
        _modDeleted.Dispose();
        _modMoved.Dispose();
        _initialized.Dispose();
        _disposed.Dispose();
        try { _modManager.Dispose(); }
        catch { /* adapter may be gone */ }
    }

    // Aliases: the subscriber classes share names with this class's events.
    private static class ModAddedEvent
    {
        public static IDisposable Subscriber(IDalamudPluginInterface pi, Action<string> action)
            => global::Penumbra.Api.IpcSubscribers.ModAdded.Subscriber(pi, action);
    }

    private static class ModDeletedEvent
    {
        public static IDisposable Subscriber(IDalamudPluginInterface pi, Action<string> action)
            => global::Penumbra.Api.IpcSubscribers.ModDeleted.Subscriber(pi, action);
    }

    private static class ModMovedEvent
    {
        public static IDisposable Subscriber(IDalamudPluginInterface pi, Action<string, string> action)
            => global::Penumbra.Api.IpcSubscribers.ModMoved.Subscriber(pi, action);
    }
}
