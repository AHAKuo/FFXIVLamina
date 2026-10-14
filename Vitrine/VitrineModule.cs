using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Vitrine.Catalog;
using Vitrine.Designs;
using Vitrine.Penumbra;
using Vitrine.Photos;
using Vitrine.UI;

namespace Vitrine;

/// <summary>
/// Entry point of the Vitrine system: owns the Penumbra reader, the mod catalog, the design store, settings and the window.
/// Lamina constructs one instance, forwards <see cref="Draw"/> from its UiBuilder and disposes it on unload.
/// </summary>
public sealed class VitrineModule : IVitrineModule
{
    private static readonly TimeSpan CollectionRefreshInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CollectionFactsInterval   = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DesignRebuildInterval     = TimeSpan.FromSeconds(1);

    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IVitrineHost _host;
    private readonly WindowSystem _windowSystem = new("Vitrine");
    private readonly VitrineWindow _window;
    private readonly string _settingsPath;

    private DateTime _nextCollectionRefresh = DateTime.MinValue;
    private DateTime _nextCollectionFacts = DateTime.MinValue;
    private DateTime _nextDesignRebuild = DateTime.MinValue;
    private bool _collectionRefreshPending;
    private bool _designsDirty;
    private bool _suppressReapply;

    public VitrineModule(IDalamudPluginInterface pi, IPluginLog log, IFramework framework, ITextureProvider textures, IVitrineHost host)
    {
        _log       = log;
        _framework = framework;
        _host      = host;

        Directory.CreateDirectory(host.DataDirectory);
        _settingsPath = Path.Combine(host.DataDirectory, "settings.json");
        Settings      = VitrineSettings.Load(_settingsPath, m => log.Warning(m));

        Reader      = new PenumbraReader(pi, log);
        Catalog     = new ModCatalog(Reader, Path.Combine(host.DataDirectory, "mods.json"), m => log.Warning(m), m => log.Debug(m));
        DesignStore = new VitrineDesignStore(Path.Combine(host.DataDirectory, "designs.json"), m => log.Warning(m));

        Photos = new ModPhotoService(
            Path.Combine(host.DataDirectory, "photos"),
            new HttpPhotoHttp(),
            () => Reader.GetModRootDirectory(),
            () => Settings.FetchPhotosFromModPages,
            m => log.Warning(m),
            m => log.Debug(m));

        Catalog.LoadCache();
        DesignStore.Load();
        DesignStore.UpdateFromCatalog(Catalog.Mods);

        Catalog.Changed            += OnCatalogChanged;
        DesignStore.Changed        += OnDesignsChanged;
        Reader.AvailabilityChanged += OnAvailabilityChanged;
        Reader.ModAdded            += dir => Catalog.QueueMod(dir);
        Reader.ModDeleted          += Catalog.RemoveMod;
        Reader.ModMoved            += Catalog.MoveMod;

        _window = new VitrineWindow(this, textures);
        _windowSystem.AddWindow(_window);
        _framework.Update += OnUpdate;

        if (Reader.Available)
        {
            Catalog.BeginFullScan();
            _collectionRefreshPending = true;
        }
    }

    /// <summary> The Penumbra reading layer. </summary>
    public IPenumbraReader Reader { get; }

    /// <summary> The mod catalog. </summary>
    public ModCatalog Catalog { get; }

    /// <summary> Baselines, variants and auto designs. </summary>
    public VitrineDesignStore DesignStore { get; }

    /// <summary> Window settings. </summary>
    public VitrineSettings Settings { get; }

    /// <summary> Display photos per mod (local file or downloaded from the mod page). </summary>
    public ModPhotoService Photos { get; }

    /// <summary> The host (Lamina). </summary>
    public IVitrineHost Host
        => _host;

    /// <inheritdoc/>
    public bool Available
        => Reader.Available;

    /// <inheritdoc/>
    public IReadOnlyList<VitrineDesign> Designs
        => DesignStore.Designs;

    /// <inheritdoc/>
    public VitrineDesign? GetDesign(Guid id)
        => DesignStore.Get(id);

    /// <inheritdoc/>
    public IReadOnlyList<ModInformation> Mods
        => Catalog.Mods;

    /// <inheritdoc/>
    public event Action? CatalogChanged;

    /// <inheritdoc/>
    public void Refresh()
        => Refresh(force: false);

    /// <summary> Rescans; <paramref name="force"/> ignores the per-mod source stamps. </summary>
    public void Refresh(bool force)
    {
        Reader.Probe();
        if (!Reader.Available)
            return;

        Catalog.BeginFullScan(force);
        _collectionRefreshPending = true;
    }

    /// <summary> Asks for a per-collection state refresh on the next update (throttled). </summary>
    public void RequestCollectionRefresh()
        => _collectionRefreshPending = true;

    /// <inheritdoc/>
    public void ToggleWindow()
        => _window.Toggle();

    /// <inheritdoc/>
    public void Draw()
        => _windowSystem.Draw();

    /// <summary> Persists the settings file. </summary>
    public void SaveSettings()
        => Settings.Save(_settingsPath, m => _log.Warning(m));

    // ---- design edits (all raise CatalogChanged and ask Lamina to reapply) ---------------------------------------------

    /// <summary> Stores (or clears with null) the baseline of a mod's auto design. </summary>
    public void SetBaseline(string modDirectory, Baseline? baseline)
        => DesignStore.SetBaseline(modDirectory, baseline);

    /// <summary> Creates a variant from a mod's auto design. </summary>
    public Guid CreateVariant(string modDirectory, string name)
        => DesignStore.CreateVariant(modDirectory, name);

    /// <summary> Updates a variant. </summary>
    public bool UpdateVariant(Guid id, string? name = null, bool? enabled = null, int? priority = null, Dictionary<string, List<string>>? settings = null, bool clearSettings = false)
        => DesignStore.UpdateVariant(id, name, enabled, priority, settings, clearSettings);

    /// <summary> Deletes a variant. </summary>
    public bool DeleteVariant(Guid id)
        => DesignStore.DeleteVariant(id);

    // ---- preview -----------------------------------------------------------------------------------------------------

    /// <summary> Writes a design's mod state into the Lamina collection as a temporary setting with Lamina's key. Returns a message for the UI. </summary>
    public string PreviewDesign(VitrineDesign design)
    {
        if (_host.LaminaCollectionId is not { } collection)
            return "Lamina collection not found.";

        foreach (var mod in design.Mods)
        {
            var settings = (mod.Settings ?? new Dictionary<string, List<string>>())
                .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
            var code = Reader.SetTemporaryModSettings(collection, mod.ModDirectory, mod.Enabled, mod.Priority, settings, _host.TemporarySource, _host.TemporaryKey);
            if (code != 0)
                return $"Penumbra returned {(global::Penumbra.Api.Enums.PenumbraApiEc)code} for {mod.ModName}.";
        }

        RequestCollectionRefresh();
        Reader.RedrawObject(0);
        return "Preview written to the Lamina collection and redrawn. Lamina's next real change replaces it.";
    }

    /// <summary> Removes a previewed mod's temporary setting from the Lamina collection. </summary>
    public string ClearPreview(string modDirectory)
    {
        if (_host.LaminaCollectionId is not { } collection)
            return "Lamina collection not found.";

        var code = Reader.RemoveTemporaryModSettings(collection, modDirectory, _host.TemporaryKey);
        RequestCollectionRefresh();
        Reader.RedrawObject(0);
        return code == 0 ? "Preview removed and redrawn." : $"Penumbra returned {(global::Penumbra.Api.Enums.PenumbraApiEc)code}.";
    }

    // ---- internals ---------------------------------------------------------------------------------------------------

    private void OnUpdate(IFramework framework)
    {
        Catalog.Tick();
        Photos.Tick();

        var now = DateTime.UtcNow;
        if (_designsDirty && (!Catalog.IsScanning || now >= _nextDesignRebuild))
        {
            _designsDirty      = false;
            _nextDesignRebuild = now + DesignRebuildInterval;
            RebuildDesignsFromCatalog();
            if (!Catalog.IsScanning)
                _collectionRefreshPending = true;
        }

        if (_window.IsOpen && now >= _nextCollectionFacts)
        {
            _nextCollectionFacts = now + CollectionFactsInterval;
            _window.RefreshCollectionFacts();
        }

        if (!Catalog.IsScanning && (_collectionRefreshPending || (_window.IsOpen && now >= _nextCollectionRefresh)))
        {
            _collectionRefreshPending = false;
            _nextCollectionRefresh    = DateTime.UtcNow + CollectionRefreshInterval;
            if (Reader.Available)
            {
                Catalog.RefreshCollectionStates();
                _window.OnCollectionStatesRefreshed();
            }
        }
    }

    /// <summary> Catalog ticks can fire every frame during a scan; the design rebuild is throttled in <see cref="OnUpdate"/>. </summary>
    private void OnCatalogChanged()
        => _designsDirty = true;

    private void RebuildDesignsFromCatalog()
    {
        _suppressReapply = true;
        try
        {
            DesignStore.UpdateFromCatalog(Catalog.Mods);
        }
        finally
        {
            _suppressReapply = false;
        }
    }

    private void OnDesignsChanged()
    {
        CatalogChanged?.Invoke();
        if (!_suppressReapply)
            _host.RequestReapply();
    }

    private void OnAvailabilityChanged()
    {
        if (Reader.Available)
        {
            Catalog.BeginFullScan();
            _collectionRefreshPending = true;
        }

        CatalogChanged?.Invoke();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        Catalog.SaveCacheIfDirty();
        SaveSettings();
        _windowSystem.RemoveAllWindows();
        _window.Dispose();
        Photos.Dispose();
        Reader.Dispose();
    }
}
