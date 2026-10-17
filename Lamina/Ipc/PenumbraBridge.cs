using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Luna;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using PenumbraApiVersion = Penumbra.Api.IpcSubscribers.ApiVersion;
using PenumbraInitialized = Penumbra.Api.IpcSubscribers.Initialized;
using PenumbraDisposed = Penumbra.Api.IpcSubscribers.Disposed;

namespace Lamina.Ipc;

/// <summary>
/// Penumbra IPC bridge built on the Penumbra.Api subscribers (TDD 11.1). Lamina only ever reads collection assignments;
/// the player assigns their character to the Lamina collection in Penumbra themselves.
/// </summary>
public sealed class PenumbraBridge : BridgeBase, IPenumbraBridge
{
    /// <summary> Breaking API version this bridge targets: Penumbra.Api 5.x (labels suffixed ".V5"). </summary>
    public const int RequiredBreakingVersion = 5;

    private readonly PenumbraApiVersion            _apiVersion;
    private readonly GetCollections                _getCollections;
    private readonly GetCollectionsByIdentifier    _getCollectionsByIdentifier;
    private readonly GetCollectionForObject        _getCollectionForObject;
    private readonly SetTemporaryModSettings       _setTemporaryModSettings;
    private readonly RemoveAllTemporaryModSettings _removeAllTemporaryModSettings;
    private readonly RemoveTemporaryModSettings    _removeTemporaryModSettings;
    private readonly RedrawObject                  _redrawObject;
    private readonly GetModList                    _getModList;
    private readonly GetAvailableModSettings       _getAvailableModSettings;
    private readonly GetChangedItems               _getChangedItems;
    private readonly GetCurrentModSettingsWithTemp _getCurrentModSettingsWithTemp;
    private readonly GetGameObjectResourceTrees    _getGameObjectResourceTrees;
    private readonly ResolveGameObjectPath         _resolveGameObjectPath;
    private readonly AddTemporaryMod               _addTemporaryMod;
    private readonly RemoveTemporaryMod            _removeTemporaryMod;
    private readonly ConvertTextureFile            _convertTextureFile;
    private readonly ConvertTextureData            _convertTextureData;

    private readonly EventSubscriber<string>         _modAdded;
    private readonly EventSubscriber<string>         _modDeleted;
    private readonly EventSubscriber<string, string> _modMoved;
    private readonly EventSubscriber<nint, int>      _gameObjectRedrawn;
    private readonly EventSubscriber                 _initialized;
    private readonly EventSubscriber                 _disposedEvent;

    /// <param name="pi"> The Dalamud plugin interface. </param>
    /// <param name="log"> The plugin log. </param>
    /// <param name="framework"> The framework service (events are delivered on its thread). </param>
    public PenumbraBridge(IDalamudPluginInterface pi, IPluginLog log, IFramework framework)
        : base(pi, log, framework, "Penumbra", RequiredBreakingVersion)
    {
        _apiVersion                    = new PenumbraApiVersion(pi);
        _getCollections                = new GetCollections(pi);
        _getCollectionsByIdentifier    = new GetCollectionsByIdentifier(pi);
        _getCollectionForObject        = new GetCollectionForObject(pi);
        _setTemporaryModSettings       = new SetTemporaryModSettings(pi);
        _removeAllTemporaryModSettings = new RemoveAllTemporaryModSettings(pi);
        _removeTemporaryModSettings    = new RemoveTemporaryModSettings(pi);
        _redrawObject                  = new RedrawObject(pi);
        _getModList                    = new GetModList(pi);
        _getAvailableModSettings       = new GetAvailableModSettings(pi);
        _getChangedItems               = new GetChangedItems(pi);
        _getCurrentModSettingsWithTemp = new GetCurrentModSettingsWithTemp(pi);
        _getGameObjectResourceTrees    = new GetGameObjectResourceTrees(pi);
        _resolveGameObjectPath         = new ResolveGameObjectPath(pi);
        _addTemporaryMod               = new AddTemporaryMod(pi);
        _removeTemporaryMod            = new RemoveTemporaryMod(pi);
        _convertTextureFile            = new ConvertTextureFile(pi);
        _convertTextureData            = new ConvertTextureData(pi);

        _modAdded          = Penumbra.Api.IpcSubscribers.ModAdded.Subscriber(pi, OnModAdded);
        _modDeleted        = Penumbra.Api.IpcSubscribers.ModDeleted.Subscriber(pi, OnModDeleted);
        _modMoved          = Penumbra.Api.IpcSubscribers.ModMoved.Subscriber(pi, OnModMoved);
        _gameObjectRedrawn = Penumbra.Api.IpcSubscribers.GameObjectRedrawn.Subscriber(pi, OnGameObjectRedrawn);
        _initialized       = PenumbraInitialized.Subscriber(pi, Probe);
        _disposedEvent     = PenumbraDisposed.Subscriber(pi, Probe);

        Probe();
    }

    /// <inheritdoc/>
    public event Action<string>? ModAdded;

    /// <inheritdoc/>
    public event Action<string>? ModDeleted;

    /// <inheritdoc/>
    public event Action<string, string>? ModMoved;

    /// <inheritdoc/>
    public event Action<nint, int>? GameObjectRedrawn;

    /// <inheritdoc/>
    protected override (int Major, int Minor) QueryVersion()
    {
        var (breaking, features) = _apiVersion.Invoke();
        return (breaking, features);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<Guid, string> GetCollections()
        => Call<IReadOnlyDictionary<Guid, string>>(() => _getCollections.Invoke(), new Dictionary<Guid, string>(), nameof(GetCollections));

    /// <inheritdoc/>
    public Guid? FindCollection(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var byIdentifier = Call(() => _getCollectionsByIdentifier.Invoke(name), null, nameof(FindCollection));
        if (byIdentifier is { Count: > 0 })
        {
            var exact = byIdentifier.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact.Id != Guid.Empty)
                return exact.Id;
        }

        foreach (var (id, collectionName) in GetCollections())
            if (string.Equals(collectionName, name, StringComparison.OrdinalIgnoreCase))
                return id;

        return null;
    }

    /// <inheritdoc/>
    public (bool ObjectValid, bool IndividualSet, Guid CollectionId, string CollectionName) GetCollectionForObject(int objectIndex)
    {
        var (valid, individual, (id, name)) = Call(() => _getCollectionForObject.Invoke(objectIndex),
            (false, false, (Guid.Empty, string.Empty)), nameof(GetCollectionForObject));
        return (valid, individual, id, name ?? string.Empty);
    }

    /// <inheritdoc/>
    public PenumbraApiEc SetTemporaryModSettings(Guid collectionId, string modDirectory, bool enabled, int priority,
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
        => Call(() => _setTemporaryModSettings.Invoke(collectionId, modDirectory, inherit: false, enabled, priority, settings,
                IpcConstants.PenumbraSource, IpcConstants.PenumbraKey),
            PenumbraApiEc.UnknownError, nameof(SetTemporaryModSettings));

    /// <inheritdoc/>
    public PenumbraApiEc RemoveAllTemporaryModSettings(Guid collectionId)
        => Call(() => _removeAllTemporaryModSettings.Invoke(collectionId, IpcConstants.PenumbraKey), PenumbraApiEc.UnknownError,
            nameof(RemoveAllTemporaryModSettings));

    /// <inheritdoc/>
    public PenumbraApiEc RemoveTemporaryModSettings(Guid collectionId, string modDirectory)
        => Call(() => _removeTemporaryModSettings.Invoke(collectionId, modDirectory, IpcConstants.PenumbraKey), PenumbraApiEc.UnknownError,
            nameof(RemoveTemporaryModSettings));

    /// <inheritdoc/>
    public void RedrawObject(int objectIndex)
        => Call(() => _redrawObject.Invoke(objectIndex, RedrawType.Redraw), nameof(RedrawObject));

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> GetModList()
        => Call<IReadOnlyDictionary<string, string>>(() => _getModList.Invoke(), new Dictionary<string, string>(), nameof(GetModList));

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, (string[] Options, GroupType Type)>? GetAvailableModSettings(string modDirectory)
        => Call(() => _getAvailableModSettings.Invoke(modDirectory), null, nameof(GetAvailableModSettings));

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> GetChangedItems(string modDirectory)
        => Call<IReadOnlyDictionary<string, object?>>(() => _getChangedItems.Invoke(modDirectory, string.Empty),
            new Dictionary<string, object?>(), nameof(GetChangedItems));

    /// <inheritdoc/>
    public (bool Enabled, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Settings, bool Inherited, bool Temporary)? GetCurrentModSettings(
        Guid collectionId, string modDirectory, bool ignoreTemporary = false)
        => Call<(bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>, bool, bool)?>(() =>
        {
            var (ec, tuple) = _getCurrentModSettingsWithTemp.Invoke(collectionId, modDirectory, string.Empty,
                ignoreInheritance: false, ignoreTemporary, IpcConstants.PenumbraKey);
            if (ec != PenumbraApiEc.Success || tuple is not { } t)
                return null;

            var (enabled, priority, settings, inherited, temporary) = t;
            var converted = settings.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
            return (enabled, priority, converted, inherited, temporary);
        }, null, nameof(GetCurrentModSettings));

    /// <inheritdoc/>
    public Penumbra.Api.ResourceTreeDto? GetGameObjectResourceTree(int objectIndex, bool withUiData = true)
        => Call<Penumbra.Api.ResourceTreeDto?>(() =>
        {
            var trees = _getGameObjectResourceTrees.Invoke(withUiData, (ushort)objectIndex);
            return trees.Length > 0 ? trees[0] : null;
        }, null, nameof(GetGameObjectResourceTree));

    /// <inheritdoc/>
    public string ResolveGameObjectPath(string gamePath, int objectIndex)
        => Call(() => _resolveGameObjectPath.Invoke(gamePath, objectIndex), gamePath, nameof(ResolveGameObjectPath));

    /// <inheritdoc/>
    public PenumbraApiEc AddTemporaryMod(string tag, Guid collectionId, IReadOnlyDictionary<string, string> redirects, int priority)
        => Call(() => _addTemporaryMod.Invoke(tag, collectionId, new Dictionary<string, string>(redirects), string.Empty, priority),
            PenumbraApiEc.UnknownError, nameof(AddTemporaryMod));

    /// <inheritdoc/>
    public PenumbraApiEc RemoveTemporaryMod(string tag, Guid collectionId, int priority)
        => Call(() => _removeTemporaryMod.Invoke(tag, collectionId, priority), PenumbraApiEc.UnknownError, nameof(RemoveTemporaryMod));

    /// <inheritdoc/>
    public Task ConvertTextureFile(string inputFile, string outputFile, TextureType textureType, bool mipMaps)
        => Call<Task?>(() => _convertTextureFile.Invoke(inputFile, outputFile, textureType, mipMaps), null, nameof(ConvertTextureFile))
        ?? Task.FromException(new InvalidOperationException("Penumbra unavailable"));

    /// <inheritdoc/>
    public Task ConvertTextureData(byte[] rgbaData, int width, string outputFile, TextureType textureType, bool mipMaps)
        => Call<Task?>(() => _convertTextureData.Invoke(rgbaData, width, outputFile, textureType, mipMaps), null, nameof(ConvertTextureData))
        ?? Task.FromException(new InvalidOperationException("Penumbra unavailable"));

    private void OnModAdded(string modDirectory)
        => Dispatch(() => ModAdded?.Invoke(modDirectory), nameof(ModAdded));

    private void OnModDeleted(string modDirectory)
        => Dispatch(() => ModDeleted?.Invoke(modDirectory), nameof(ModDeleted));

    private void OnModMoved(string oldDirectory, string newDirectory)
        => Dispatch(() => ModMoved?.Invoke(oldDirectory, newDirectory), nameof(ModMoved));

    private void OnGameObjectRedrawn(nint address, int objectIndex)
        => Dispatch(() => GameObjectRedrawn?.Invoke(address, objectIndex), nameof(GameObjectRedrawn));

    /// <inheritdoc/>
    protected override void DisposeCore()
    {
        _modAdded.Dispose();
        _modDeleted.Dispose();
        _modMoved.Dispose();
        _gameObjectRedrawn.Dispose();
        _initialized.Dispose();
        _disposedEvent.Dispose();
        ModAdded          = null;
        ModDeleted        = null;
        ModMoved          = null;
        GameObjectRedrawn = null;
    }
}
