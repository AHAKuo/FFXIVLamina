using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Lamina.Runtime.Engine;
using Lamina.Tests.Fakes;

namespace Lamina.Tests.Engine;

/// <summary> One wired-up engine with fake bridges, for applier / cleanup / core tests. </summary>
public sealed class EngineRig
{
    public FakeGlamourerBridge Glamourer { get; } = new();
    public FakePenumbraBridge Penumbra { get; } = new();
    public FakeHonorificBridge Honorific { get; } = new();
    public FakeMoodlesBridge Moodles { get; } = new();
    public AppliedStateStore Store { get; } = new();
    public LaminaConfig Config { get; } = new();
    public Guid Collection { get; }
    public CollectionStatus Status { get; set; } = CollectionStatus.Assigned;
    public int? PlayerIndex { get; set; } = 0;
    public nint PlayerAddress { get; set; } = 0x1000;
    public DateTime Now { get; set; } = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    public List<string> Log { get; } = new();

    public RedrawScheduler Redraw { get; }
    public Applier Applier { get; }
    public CleanupService Cleanup { get; }
    public CountingEvaluator Evaluator { get; } = new();
    public EngineCore Core { get; }
    public Compose.FakeGlamourerBridge ComposeGlamourer { get; } = new();

    public EngineRig()
    {
        Collection = Penumbra.AddCollection("Lamina");
        Penumbra.ObjectCollections[0] = Collection;

        Redraw  = new RedrawScheduler(Penumbra, () => PlayerIndex, Log.Add);
        Applier = new Applier(Glamourer, Penumbra, Honorific, Moodles, Store, Redraw, () => Config, () => Collection, () => Status,
            () => PlayerIndex, () => PlayerAddress, () => Now, Log.Add);
        Cleanup = new CleanupService(Glamourer, Penumbra, Honorific, Moodles, Store, Redraw, Evaluator, () => Config, () => Collection,
            () => PlayerIndex, () => PlayerAddress, () => Now, Log.Add);

        var resolver = Compose.Build.Resolver(ComposeGlamourer);
        Core = new EngineCore(new PassCompositor(), resolver, Evaluator, Applier, Cleanup, Store, () => Config);
    }

    /// <summary> Names of the Penumbra calls made so far. </summary>
    public List<string> PenumbraCalls
        => Penumbra.Calls.Select(c => c.Name).ToList();
}

/// <summary> Instant evaluator that counts <see cref="Reset"/> calls, so cleanup tests can tell which cleanups reset timers. </summary>
public sealed class CountingEvaluator : IConditionEvaluator
{
    private readonly Compose.InstantEvaluator _inner = new();

    public int Resets { get; private set; }

    public bool Evaluate(ConditionStack stack, GameStateSnapshot snapshot)
        => _inner.Evaluate(stack, snapshot);

    public bool EvaluateInstant(ConditionStack stack, GameStateSnapshot snapshot)
        => _inner.EvaluateInstant(stack, snapshot);

    public TimeSpan? TimeUntilNextChange
        => null;

    public void Reset()
        => Resets++;
}

/// <summary> Builders for designs used by the engine tests. </summary>
public static class Designs
{
    public static readonly Provenance Src = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test");

    public static VirtualDesign Empty()
        => new DesignDraft().Freeze(Guid.Empty, null, Array.Empty<Guid>(), Array.Empty<Guid>());

    public static VirtualDesign Of(Action<DesignDraft> fill)
    {
        var draft = new DesignDraft();
        fill(draft);
        return draft.Freeze(Guid.NewGuid(), null, Array.Empty<Guid>(), Array.Empty<Guid>());
    }

    public static void Head(DesignDraft d, ulong item)
        => d.Items[EquipSlot.Head] = new Field<EquipItem>(new EquipItem(item), Src);

    public static void Mod(DesignDraft d, string dir, bool enabled = true, int priority = 0, Dictionary<string, List<string>>? settings = null)
        => d.AddMod(ModContribution.From(new ModState
        {
            ModDirectory = dir,
            State        = enabled ? ModToggle.Enable : ModToggle.Disable,
            Priority     = priority,
            Settings     = settings,
        }, Src));

    public static void Title(DesignDraft d, string title)
        => d.Honorific = new Field<HonorificTitle>(new HonorificTitle(false, title, false, null, null), Src);

    public static void Moodle(DesignDraft d, Guid id, MoodleOp op = MoodleOp.Add)
        => d.Moodles[id] = new Field<MoodleOp>(op, Src);

    public static GameStateSnapshot Safe()
        => new() { Sequence = 1, LoggedIn = true };

    public static GameStateSnapshot Unsafe()
        => new() { Sequence = 2, LoggedIn = true, Body = new HashSet<BodyState> { BodyState.InCombat } };
}
