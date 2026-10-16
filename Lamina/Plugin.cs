using System.Reflection;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lamina.Decals;
using Lamina.Decals.Mesh;
using Lamina.Decals.Publish;
using Lamina.Ipc;
using Lamina.Persistence;
using Lamina.Runtime.Compose;
using Lamina.Runtime.Engine;
using Lamina.Runtime.State;
using Lamina.UI;

namespace Lamina;

/// <summary> Plugin entry: builds every service in dependency order and tears them down in reverse. </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IFateTable FateTable { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ITextureReadbackProvider TextureReadback { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    private readonly List<IDisposable> _disposables = new();
    private readonly WindowSystem _windowSystem = new("Lamina");

    private LaminaEngine? _engine;
    private LaminaIpc? _ipc;
    private ProfileStore? _profiles;
    private LaminaWindow? _window;

    /// <summary> The service bundle, available after construction. </summary>
    public LaminaServices Services { get; private set; } = null!;

    public Plugin()
    {
        try
        {
            Construct();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Lamina failed to start; tearing down.");
            Dispose();
            throw;
        }
    }

    private void Construct()
    {
        var store    = new JsonStore(PluginInterface.GetPluginConfigDirectory());
        var profiles = new ProfileStore(store);
        _profiles = profiles;
        foreach (var error in profiles.Load())
        {
            if (error.IsWarning)
                Log.Warning($"Load: {error}");
            else
                Log.Error($"Load: {error}");
        }

        profiles.SaveFailed += (what, ex) => Log.Error(ex, $"Saving {what} failed.");

        var glamourer = Track(new GlamourerBridge(PluginInterface, Log, Framework));
        var penumbra  = Track(new PenumbraBridge(PluginInterface, Log, Framework));
        var honorific = Track(new HonorificBridge(PluginInterface, Log, Framework));
        var moodles   = Track(new MoodlesBridge(PluginInterface, Log, Framework, ObjectTable));

        var catalog   = new GameCatalog(DataManager, Log);
        var tracker   = Track(new StateTracker(ClientState, Condition, Framework, ObjectTable, PartyList, DutyState, PlayerState, FateTable, Log,
            catalog, () => profiles.Biomes));
        var clock     = new TickClock();
        var evaluator = new ConditionEvaluator(clock.Read, () => profiles.Config);
        var cache     = new GlamourerDesignCache(glamourer);

        // Vitrine is a separate, optional plugin reached over IPC.
        var vitrine = Track(new VitrineBridge(PluginInterface, Log, Framework));

        var resolver   = new LayerResolver(cache, () => vitrine, () => profiles.Config.ApplyGlamourerModAssociations, m => Log.Debug(m));
        var compositor = new PassCompositor();

        // Decals: live skin meshes + picking + the baking publisher. A failure here disables the layer kind, nothing else.
        SkinMeshProvider? skinMeshes = null;
        DecalPicker?      picker     = null;
        DecalPublisher?   decals     = null;
        try
        {
            var configDir = PluginInterface.GetPluginConfigDirectory();
            var deformer = new RacialDeformer(penumbra, DataManager, Log);
            skinMeshes = new SkinMeshProvider(penumbra, DataManager, ObjectTable, deformer, Log);
            picker     = new DecalPicker(skinMeshes, ObjectTable, GameGui, deformer, Log);
            decals     = Track(new DecalPublisher(penumbra, skinMeshes, TextureProvider, TextureReadback, Framework, Log,
                DecalPaths.ImageDirectory(configDir), DecalPaths.CacheDirectory(configDir)));
            var meshes = skinMeshes;
            penumbra.GameObjectRedrawn += (_, index) =>
            {
                if (index == 0)
                    meshes.Invalidate();
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Decal feature could not start; decal layers are disabled.");
            skinMeshes = null;
            picker     = null;
            decals     = null;
        }

        var engine = Track(new LaminaEngine(profiles, tracker, evaluator, compositor, resolver, cache, glamourer, penumbra, honorific, moodles,
            vitrine, Framework, ObjectTable, ClientState, Condition, catalog, Log, clock, decals));
        _engine = engine;
        _ipc    = new LaminaIpc(PluginInterface, Framework, engine, Log);

        Services = new LaminaServices
        {
            PluginInterface = PluginInterface,
            Log             = Log,
            Framework       = Framework,
            DataManager     = DataManager,
            ClientState     = ClientState,
            ObjectTable     = ObjectTable,
            TextureProvider = TextureProvider,
            GameGui         = GameGui,
            CommandManager  = CommandManager,
            ChatGui         = ChatGui,
            Profiles        = profiles,
            Catalog         = catalog,
            State           = tracker,
            Evaluator       = evaluator,
            DesignCache     = cache,
            Resolver        = resolver,
            Compositor      = compositor,
            Engine          = engine,
            Glamourer       = glamourer,
            Penumbra        = penumbra,
            Honorific       = honorific,
            Moodles         = moodles,
            Vitrine         = vitrine,
            DecalPicker     = picker,
            SkinMeshes      = skinMeshes,
            DecalPublisher  = decals,
            Version         = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
        };

        _window = new LaminaWindow(Services);
        _window.RegisterWith(_windowSystem);
        Track(new Commands(Services, () => _window.Toggle()));

        PluginInterface.UiBuilder.Draw         += DrawUi;
        PluginInterface.UiBuilder.OpenMainUi   += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;

        Log.Information($"Lamina {Services.Version} loaded: {profiles.Profiles.Count} profile(s), "
                      + $"Glamourer {(glamourer.Available ? "ok" : "missing")}, Penumbra {(penumbra.Available ? "ok" : "missing")}, "
                      + $"Honorific {(honorific.Available ? "ok" : "missing")}, Moodles {(moodles.Available ? "ok" : "missing")}.");
    }

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private void DrawUi()
    {
        _windowSystem.Draw();
    }

    private void OpenMainUi()
    {
        if (_window is not null)
            _window.IsOpen = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw         -= DrawUi;
        PluginInterface.UiBuilder.OpenMainUi   -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        _windowSystem.RemoveAllWindows();
        _window?.Dispose();

        // IPC before the engine: nobody may ask for a reapply while it is tearing down.
        try
        {
            _ipc?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "IPC dispose failed.");
        }

        // Engine first: it runs the unload cleanup while the bridges are still alive.
        try
        {
            _engine?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Engine dispose failed.");
        }

        try
        {
            _profiles?.SaveNow();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Final save failed.");
        }

        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_disposables[i], _engine))
                continue;
            try
            {
                _disposables[i].Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Disposing {_disposables[i].GetType().Name} failed.");
            }
        }

        _disposables.Clear();
    }
}
