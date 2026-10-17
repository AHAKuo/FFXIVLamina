namespace Lamina.Dtr;

/// <summary> The strings shown in Lamina's server info bar entries. Pure, so the tests can pin them. </summary>
public static class DtrText
{
    /// <summary> Entry titles as they appear in Dalamud's own server info bar settings. </summary>
    public const string ReapplyTitle = "Lamina Reapply";
    public const string ToggleTitle  = "Lamina On/Off";

    public const string ReapplyLabel = "Lamina: Reapply";

    public static string ToggleLabel(bool enabled)
        => enabled ? "Lamina: On" : "Lamina: Off";

    /// <summary> Reapply tooltip: what the click does, the active profile and when Lamina last applied. </summary>
    public static string ReapplyTooltip(string? profileName, DateTime? lastApplyUtc, DateTime nowUtc)
    {
        var lines = new List<string>
        {
            "Left click: re-evaluate and apply now (/lam reapply).",
            "Right click: open Lamina.",
            $"Profile: {(string.IsNullOrEmpty(profileName) ? "(none enabled)" : profileName)}",
            lastApplyUtc is { } at
                ? $"Last apply: {Age(nowUtc - at)} ({at.ToLocalTime():HH:mm:ss})"
                : "Last apply: nothing applied yet",
        };
        return string.Join('\n', lines);
    }

    /// <summary> Toggle tooltip: the state and what flipping it does. </summary>
    public static string ToggleTooltip(bool enabled)
        => enabled
            ? "Lamina is on. Left click turns it off: a full cleanup removes Lamina's mod settings, reverts Glamourer and removes the title, "
              + "moodles, Customize+ profile and Simple Heels offset Lamina set.\nRight click: open Lamina."
            : "Lamina is off; nothing is applied. Left click turns it on and re-applies the enabled profile.\nRight click: open Lamina.";

    /// <summary> "12 s ago", "5 min ago", "3 h ago"; never negative. </summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalSeconds < 60)
            return $"{age.TotalSeconds:0} s ago";
        if (age.TotalMinutes < 60)
            return $"{age.TotalMinutes:0} min ago";
        return $"{age.TotalHours:0} h ago";
    }
}
