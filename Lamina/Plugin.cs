using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Lamina;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string MainCommand = "/lamina";

    public readonly WindowSystem WindowSystem = new("Lamina");

    public Plugin()
    {
        CommandManager.AddHandler(MainCommand, new CommandInfo(OnMainCommand)
        {
            HelpMessage = "Open Lamina.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        WindowSystem.RemoveAllWindows();
        CommandManager.RemoveHandler(MainCommand);
    }

    private void OnMainCommand(string command, string args)
    {
        Log.Information("Lamina: no windows yet.");
    }
}
