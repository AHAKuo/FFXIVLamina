namespace Lamina.Models;

/// <summary> Global plugin settings. Saved to config.json. </summary>
public sealed class LaminaConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary> Master toggle. Off = full Cleanup / Revert and nothing is applied. </summary>
    public bool Enabled { get; set; } = true;

    public EvaluationDirection Direction { get; set; } = EvaluationDirection.TopToBottom;

    /// <summary> Lock the Glamourer state with Lamina's key so Glamourer automation cannot override it. </summary>
    public bool HoldGlamourerState { get; set; } = true;

    /// <summary> Name of the Penumbra collection Lamina writes to. Lamina never writes anywhere else. </summary>
    public string CollectionName { get; set; } = "Lamina";

    /// <summary> Seconds a condition must stay true before it counts. Per condition override possible. </summary>
    public float DefaultHoldSeconds { get; set; } = 0.5f;

    /// <summary> Seconds a condition stays true after it stopped being true. Per condition override possible. </summary>
    public float DefaultLingerSeconds { get; set; } = 1.0f;

    public RedrawPolicy RedrawPolicy { get; set; } = RedrawPolicy.DeferWhenUnsafe;

    public bool DebugOverlay { get; set; }

    /// <summary> How many backup folders to keep under backups/. </summary>
    public int BackupsToKeep { get; set; } = 10;

    /// <summary> Glamourer designs carry their own mod associations. When true Lamina applies them into its collection. </summary>
    public bool ApplyGlamourerModAssociations { get; set; } = true;

    /// <summary> Set after the first-run collection prompt was shown. </summary>
    public bool FirstRunDone { get; set; }

    /// <summary>
    /// What a Glamourer field falls back to when no rule sets it any more: the plain game state (default) or Glamourer
    /// automation. The full cleanup (master toggle off, unload, logout) reverts the same way. Absent in older config files = GameState.
    /// </summary>
    public NoRuleFallback NoRuleFallback { get; set; } = NoRuleFallback.GameState;

    /// <summary> Server info bar (DTR) entry "Lamina Reapply": click re-evaluates and applies. Off by default. </summary>
    public bool DtrReapply { get; set; }

    /// <summary> Server info bar (DTR) entry "Lamina On/Off": shows the master toggle, click flips it. Off by default. </summary>
    public bool DtrToggle { get; set; }
}

/// <summary> User overrides for the territory → biome mapping. Saved to biomes.json. </summary>
public sealed class BiomeMap
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public Dictionary<uint, string> TerritoryToBiome { get; set; } = new();
}
