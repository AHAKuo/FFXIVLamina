using Lamina.Ipc;

namespace Lamina.Tests.Fakes;

/// <summary> One recorded IPC call: the method name and its arguments in order. </summary>
public sealed record CallRecord(string Name, object?[] Args)
{
    public override string ToString()
        => $"{Name}({string.Join(", ", Args.Select(a => a?.ToString() ?? "null"))})";
}

/// <summary> Shared state for the in-memory bridges: settable availability and a call log. </summary>
public abstract class FakeBridgeBase : IPluginBridge
{
    private bool _available = true;

    protected FakeBridgeBase(string pluginName)
    {
        PluginName = pluginName;
    }

    public string PluginName { get; }

    /// <summary> Setting this raises <see cref="AvailabilityChanged"/> when the value flips. </summary>
    public bool Available
    {
        get => _available;
        set
        {
            if (_available == value)
                return;

            _available = value;
            AvailabilityChanged?.Invoke();
        }
    }

    public (int Major, int Minor)? ApiVersion { get; set; } = (1, 0);

    public event Action? AvailabilityChanged;

    /// <summary> Every call made through the fake, in order. </summary>
    public List<CallRecord> Calls { get; } = new();

    /// <summary> Calls with the given name. </summary>
    public IEnumerable<CallRecord> CallsNamed(string name)
        => Calls.Where(c => c.Name == name);

    public void ClearCalls()
        => Calls.Clear();

    protected void Record(string name, params object?[] args)
        => Calls.Add(new CallRecord(name, args));
}
