using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lamina.Decals;
using Lamina.Ipc;
using Lamina.Persistence;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Lamina.Runtime.State;

namespace Lamina;

/// <summary>
/// Everything the UI and commands need, bundled so windows take one constructor argument.
/// Built by Plugin (wave 2 engine agent); consumed by the UI (wave 2 UI agent). Members are set once at startup.
/// </summary>
public sealed class LaminaServices
{
    public required IDalamudPluginInterface PluginInterface { get; init; }
    public required IPluginLog Log { get; init; }
    public required IFramework Framework { get; init; }
    public required IDataManager DataManager { get; init; }
    public required IClientState ClientState { get; init; }
    public required IObjectTable ObjectTable { get; init; }
    public required ITextureProvider TextureProvider { get; init; }
    public required IGameGui GameGui { get; init; }
    public required ICommandManager CommandManager { get; init; }
    public required IChatGui ChatGui { get; init; }

    public required ProfileStore Profiles { get; init; }
    public required GameCatalog Catalog { get; init; }
    public required StateTracker State { get; init; }
    public required ConditionEvaluator Evaluator { get; init; }
    public required GlamourerDesignCache DesignCache { get; init; }
    public required LayerResolver Resolver { get; init; }
    public required PassCompositor Compositor { get; init; }
    public required ILaminaEngine Engine { get; init; }

    public required IGlamourerBridge Glamourer { get; init; }
    public required IPenumbraBridge Penumbra { get; init; }
    public required IHonorificBridge Honorific { get; init; }
    public required IMoodlesBridge Moodles { get; init; }
    public required ICustomizePlusBridge CustomizePlus { get; init; }
    public required ISimpleHeelsBridge SimpleHeels { get; init; }
    public required CustomizePlusProfileCache CustomizePlusCache { get; init; }
    public required IVitrineBridge Vitrine { get; init; }

    /// <summary> Decal picking on the posed player. Null when the decal feature could not start. </summary>
    public IDecalPicker? DecalPicker { get; init; }

    /// <summary> Live skin meshes of the local player. Null when the decal feature could not start. </summary>
    public ISkinMeshProvider? SkinMeshes { get; init; }

    /// <summary> Decal baking / publishing. Null when the decal feature could not start. </summary>
    public IDecalPublisher? DecalPublisher { get; init; }

    /// <summary> Plugin version string for the About tab. </summary>
    public required string Version { get; init; }
}
