using Glamourer.Api.Enums;
using Lamina.Ipc;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Newtonsoft.Json.Linq;

namespace Lamina.Tests.Compose;

/// <summary> Walks the condition tree with no hold / linger timers. Mirrors the semantics the real evaluator applies instantly. </summary>
public sealed class InstantEvaluator : IConditionEvaluator
{
    public bool Evaluate(ConditionStack stack, GameStateSnapshot snapshot)
        => EvaluateInstant(stack, snapshot);

    public bool EvaluateInstant(ConditionStack stack, GameStateSnapshot snapshot)
        => stack.Root is null || Walk(stack.Root, snapshot);

    public TimeSpan? TimeUntilNextChange => null;

    public void Reset() { }

    private static bool Walk(ConditionNode node, GameStateSnapshot s)
    {
        bool result;
        switch (node)
        {
            case ConditionGroup g:
                var enabled = g.Children.Where(c => c is not Condition { Enabled: false }).ToList();
                result = enabled.Count == 0
                    || (g.Op == GroupOp.All ? enabled.All(c => Walk(c, s)) : enabled.Any(c => Walk(c, s)));
                break;
            case Condition c:
                result = c.Matches(s);
                break;
            default:
                result = false;
                break;
        }

        return node.Negate ? !result : result;
    }
}

/// <summary> A condition that returns a fixed value, for compositor tests. </summary>
public sealed class FixedCondition : Condition
{
    public FixedCondition(bool value) => Value = value;

    public bool Value { get; }

    public override string Kind => "Fixed";

    public override bool Matches(GameStateSnapshot s) => Value;

    public override string Describe() => $"Fixed {Value}";
}

/// <summary> In-memory Glamourer bridge for compositor tests. </summary>
public sealed class FakeGlamourerBridge : IGlamourerBridge
{
    public Dictionary<Guid, JObject> Designs { get; } = new();
    public Dictionary<Guid, string> Names { get; } = new();
    public int GetDesignCalls { get; private set; }

    public string PluginName => "Glamourer";
    public bool Available { get; set; } = true;
    public (int Major, int Minor)? ApiVersion => Available ? (1, 4) : null;
    public event Action? AvailabilityChanged;
    public event Action<nint, StateFinalizationType>? StateFinalized;

    public IReadOnlyDictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor)> GetDesigns()
        => Names.ToDictionary(kv => kv.Key, kv => (kv.Value, kv.Value, 0u));

    public JObject? GetDesign(Guid designId)
    {
        GetDesignCalls++;
        return Designs.TryGetValue(designId, out var d) ? (JObject)d.DeepClone() : null;
    }

    public (GlamourerApiEc Code, JObject? State) GetState(int objectIndex) => (GlamourerApiEc.ActorNotFound, null);
    public GlamourerApiEc ApplyState(JObject state, int objectIndex, bool lockState) => GlamourerApiEc.Success;
    public GlamourerApiEc UnlockAndRevertToAutomation(int objectIndex) => GlamourerApiEc.Success;
    public GlamourerApiEc SetItem(int objectIndex, ApiEquipSlot slot, ulong itemId, IReadOnlyList<byte> stains) => GlamourerApiEc.Success;

    public void RaiseAvailability() => AvailabilityChanged?.Invoke();
    public void RaiseFinalized(nint a, StateFinalizationType t) => StateFinalized?.Invoke(a, t);
    public GlamourerApiEc UnlockAndRevertToGame(int objectIndex) => GlamourerApiEc.Success;


    /// <summary> Designs created through <see cref="AddDesign"/>: id → (name, the design JObject as sent). </summary>
    public Dictionary<Guid, (string Name, JObject Design)> AddedDesigns { get; } = new();

    public (GlamourerApiEc Code, Guid Id) AddDesign(JObject design, string name)
    {
        if (!Available)
            return (GlamourerApiEc.UnknownError, Guid.Empty);
        var id   = Guid.NewGuid();
        var copy = (JObject)design.DeepClone();
        AddedDesigns[id] = (name, copy);
        Designs[id]      = copy;
        Names[id]        = name;
        return (GlamourerApiEc.Success, id);
    }
}

/// <summary> Builders for compact test setups. </summary>
public static class Build
{
    public static JObject Design(Action<JObject>? customize = null)
    {
        var d = new JObject
        {
            ["FileVersion"] = 3,
            ["Equipment"]   = new JObject(),
            ["Bonus"]       = new JObject(),
            ["Customize"]   = new JObject(),
            ["Parameters"]  = new JObject(),
        };
        customize?.Invoke(d);
        return d;
    }

    public static JObject Slot(ulong item, byte stain = 0, byte stain2 = 0, bool apply = true, bool applyStain = false)
        => new() { ["ItemId"] = item, ["Stain"] = stain, ["Stain2"] = stain2, ["Apply"] = apply, ["ApplyStain"] = applyStain };

    public static JObject Cust(int value, bool apply = true)
        => new() { ["Value"] = value, ["Apply"] = apply };

    public static Pass Pass(string name, bool matches, params Layer[] layers)
        => new()
        {
            Name       = name,
            Conditions = new ConditionStack { Root = new FixedCondition(matches) },
            Layers     = layers.ToList(),
        };

    public static PassGroup Group(string name, bool matches, params Pass[] passes)
        => new()
        {
            Name       = name,
            Conditions = new ConditionStack { Root = new FixedCondition(matches) },
            Passes     = passes.ToList(),
        };

    public static PassGroup FinalGroup(string name, bool matches, params Pass[] passes)
    {
        var g = Group(name, matches, passes);
        g.IsFinal = true;
        return g;
    }

    public static LayerResolver Resolver(FakeGlamourerBridge bridge, Lamina.Tests.Fakes.FakeVitrineBridge? vitrine = null, bool applyMods = true,
        Lamina.Tests.Fakes.FakeCustomizePlusBridge? customizePlus = null)
        => new(new GlamourerDesignCache(bridge), () => vitrine, () => applyMods, null, customizePlus is null ? null : new CustomizePlusProfileCache(customizePlus));

    public static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Compose", "Fixtures", name);

    public static JObject Fixture(string name)
        => JObject.Parse(File.ReadAllText(FixturePath(name)));
}
