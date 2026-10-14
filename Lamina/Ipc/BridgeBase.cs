using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace Lamina.Ipc;

/// <summary>
/// Shared plumbing for every IPC bridge: an availability state machine driven by version probes and plugin
/// load / unload notifications, plus exception-safe call wrappers. Bridges never throw on a missing plugin.
/// Every event a bridge raises (<see cref="AvailabilityChanged"/> and the forwarded remote events) is delivered on the
/// framework thread through <see cref="Dispatch"/>, because probes and remote notifications may arrive from other threads.
/// </summary>
public abstract class BridgeBase : IPluginBridge, IDisposable
{
    /// <summary> After this many consecutive failed calls the next failure is logged at Warning instead of Debug. </summary>
    private const int WarnEveryNFailures = 10;

    /// <summary> The plugin interface used for IPC. </summary>
    protected readonly IDalamudPluginInterface PluginInterface;

    /// <summary> The plugin log. </summary>
    protected readonly IPluginLog Log;

    /// <summary> The framework, used to marshal events onto the framework thread. </summary>
    protected readonly IFramework Framework;

    private readonly int _requiredMajor;
    private readonly object _stateLock = new();
    private          bool _available;
    private          bool _versionWarned;
    private          int  _consecutiveFailures;
    private volatile bool _disposed;

    /// <param name="pluginInterface"> The Dalamud plugin interface. </param>
    /// <param name="log"> The plugin log. </param>
    /// <param name="framework"> The framework service (event marshaling). </param>
    /// <param name="pluginName"> Display / internal name of the remote plugin, used in log lines. </param>
    /// <param name="requiredMajor"> The exact major (breaking) API version this bridge was written against, or -1 to accept any. </param>
    protected BridgeBase(IDalamudPluginInterface pluginInterface, IPluginLog log, IFramework framework, string pluginName, int requiredMajor)
    {
        PluginInterface = pluginInterface;
        Log             = log;
        Framework       = framework;
        PluginName      = pluginName;
        _requiredMajor  = requiredMajor;

        PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
    }

    /// <inheritdoc/>
    public string PluginName { get; }

    /// <inheritdoc/>
    public bool Available
        => _available;

    /// <inheritdoc/>
    public (int Major, int Minor)? ApiVersion { get; private set; }

    /// <inheritdoc/>
    public event Action? AvailabilityChanged;

    /// <summary> Queries the remote plugin's API version. May throw (the probe catches). </summary>
    protected abstract (int Major, int Minor) QueryVersion();

    /// <summary> Whether a reported version is one this bridge can talk to. </summary>
    protected virtual bool VersionAccepted((int Major, int Minor) version)
        => _requiredMajor < 0 || version.Major == _requiredMajor;

    /// <summary>
    /// Re-checks whether the remote plugin answers and speaks a compatible API version. Safe to call at any time
    /// from any thread; <see cref="AvailabilityChanged"/> is raised on the framework thread, only when the state actually flips.
    /// </summary>
    public void Probe()
    {
        if (_disposed)
            return;

        try
        {
            var version = QueryVersion();
            ApiVersion = version;
            if (VersionAccepted(version))
            {
                _versionWarned = false;
                SetAvailable(true);
            }
            else
            {
                if (!_versionWarned)
                {
                    _versionWarned = true;
                    Log.Warning($"[{PluginName}] API version {version.Major}.{version.Minor} is not supported (Lamina expects major {_requiredMajor}). The integration is disabled until a compatible version is installed.");
                }

                SetAvailable(false);
            }
        }
        catch (IpcNotReadyError)
        {
            ApiVersion = null;
            SetAvailable(false);
        }
        catch (Exception e)
        {
            ApiVersion = null;
            Log.Debug($"[{PluginName}] version probe failed: {e.GetType().Name}: {e.Message}");
            SetAvailable(false);
        }
    }

    /// <summary> Runs an IPC call. Returns <paramref name="fallback"/> when the plugin is unavailable or the call throws. </summary>
    /// <param name="call"> The call. </param>
    /// <param name="fallback"> Value returned on failure. </param>
    /// <param name="what"> Short name of the call, for logging. </param>
    protected T Call<T>(Func<T> call, T fallback, string what)
    {
        if (!_available || _disposed)
            return fallback;

        try
        {
            var result = call();
            _consecutiveFailures = 0;
            return result;
        }
        catch (IpcNotReadyError)
        {
            Log.Debug($"[{PluginName}] {what}: IPC not ready, marking unavailable.");
            SetAvailable(false);
            return fallback;
        }
        catch (Exception e)
        {
            LogFailure(what, e);
            return fallback;
        }
    }

    /// <summary> Runs a void IPC call. Returns false when the plugin is unavailable or the call throws. </summary>
    protected bool Call(Action call, string what)
        => Call(() =>
        {
            call();
            return true;
        }, false, what);

    /// <summary> Runs an action that must never propagate exceptions (event forwarding). </summary>
    protected void Guard(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Error(e, $"[{PluginName}] exception in {what} handler.");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the framework thread: immediately when already there, otherwise queued through
    /// <see cref="IFramework.RunOnFrameworkThread(Action)"/>. Exceptions never escape. Nothing runs after disposal.
    /// </summary>
    /// <param name="action"> The handler invocation. </param>
    /// <param name="what"> Short name, for logging. </param>
    protected void Dispatch(Action action, string what)
    {
        if (_disposed)
            return;

        if (Framework.IsInFrameworkUpdateThread)
        {
            Guard(action, what);
            return;
        }

        try
        {
            _ = Framework.RunOnFrameworkThread(() =>
            {
                if (!_disposed)
                    Guard(action, what);
            });
        }
        catch (Exception e)
        {
            Log.Error(e, $"[{PluginName}] could not queue {what} on the framework thread.");
        }
    }

    private void LogFailure(string what, Exception e)
    {
        _consecutiveFailures++;
        var message = $"[{PluginName}] {what} failed ({_consecutiveFailures} consecutive): {e.GetType().Name}: {e.Message}";
        if (_consecutiveFailures % WarnEveryNFailures == 0)
            Log.Warning(message);
        else
            Log.Debug(message);
    }

    private void SetAvailable(bool value)
    {
        lock (_stateLock)
        {
            if (value == _available)
                return;

            _available = value;
        }

        if (value)
            Log.Information($"[{PluginName}] IPC available (API {ApiVersion?.Major}.{ApiVersion?.Minor}).");
        else
            Log.Information($"[{PluginName}] IPC unavailable.");

        Dispatch(() => AvailabilityChanged?.Invoke(), nameof(AvailabilityChanged));
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args)
    {
        // Cheap and rare: always re-probe rather than trusting the internal name list.
        Probe();
    }

    /// <summary> Subclasses release their subscribers here. Called once. </summary>
    protected virtual void DisposeCore()
    { }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        try
        {
            DisposeCore();
        }
        catch (Exception e)
        {
            Log.Error(e, $"[{PluginName}] error while disposing bridge.");
        }

        _available = false;
        GC.SuppressFinalize(this);
    }
}
