using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lamina.Runtime;

namespace Lamina.Ipc;

/// <summary>
/// Lamina's own IPC surface, consumed by Vitrine (see docs/IPC.md in the Vitrine repository, "Lamina IPC").
/// Lets another plugin ask Lamina to re-assert its design after it changed the character (e.g. when a Vitrine preview ends).
/// </summary>
public sealed class LaminaIpc : IDisposable
{
    /// <summary> Version of this surface. Major changes break the contract. </summary>
    public static readonly (int Major, int Minor) Version = (1, 0);

    public const string ApiVersionLabel = "Lamina.ApiVersion";
    public const string ReapplyLabel    = "Lamina.Reapply";
    public const string IsEnabledLabel  = "Lamina.IsEnabled";
    public const string ReadyLabel      = "Lamina.Ready";
    public const string DisposedLabel   = "Lamina.Disposed";

    private readonly ICallGateProvider<(int Major, int Minor)> _apiVersion;
    private readonly ICallGateProvider<object>                 _reapply;
    private readonly ICallGateProvider<bool>                   _isEnabled;
    private readonly ICallGateProvider<object>                 _ready;
    private readonly ICallGateProvider<object>                 _disposed;
    private readonly IFramework                                _framework;
    private readonly ILaminaEngine                             _engine;
    private readonly IPluginLog                                _log;

    public LaminaIpc(IDalamudPluginInterface pi, IFramework framework, ILaminaEngine engine, IPluginLog log)
    {
        _framework = framework;
        _engine    = engine;
        _log       = log;

        _apiVersion = pi.GetIpcProvider<(int Major, int Minor)>(ApiVersionLabel);
        _reapply    = pi.GetIpcProvider<object>(ReapplyLabel);
        _isEnabled  = pi.GetIpcProvider<bool>(IsEnabledLabel);
        _ready      = pi.GetIpcProvider<object>(ReadyLabel);
        _disposed   = pi.GetIpcProvider<object>(DisposedLabel);

        _apiVersion.RegisterFunc(() => Version);
        _reapply.RegisterAction(Reapply);
        _isEnabled.RegisterFunc(() => _engine.Enabled);

        _ready.SendMessage();
    }

    /// <summary> Re-evaluates and re-applies the active design, on the framework thread. </summary>
    private void Reapply()
    {
        if (_framework.IsInFrameworkUpdateThread)
        {
            _engine.RequestReapply();
            return;
        }

        _framework.RunOnFrameworkThread(_engine.RequestReapply).ContinueWith(
            t => _log.Warning($"Reapply over IPC failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Dispose()
    {
        try
        {
            _disposed.SendMessage();
        }
        catch (Exception ex)
        {
            _log.Debug($"Lamina.Disposed notification failed: {ex.Message}");
        }

        _apiVersion.UnregisterFunc();
        _reapply.UnregisterAction();
        _isEnabled.UnregisterFunc();
    }
}
