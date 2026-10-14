using Vitrine.Catalog;

namespace Vitrine;

/// <summary> How the Vitrine window shows things. Saved to settings.json in the Vitrine data directory. </summary>
public sealed class VitrineSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary> Card width in unscaled pixels; columns are derived from the available width. </summary>
    public float CardWidth { get; set; } = 250f;

    /// <summary> Show mod preview images on cards. </summary>
    public bool ShowImages { get; set; } = true;

    /// <summary> Download the display photo from a mod's website (og:image) when the mod folder has none. </summary>
    public bool FetchPhotosFromModPages { get; set; } = true;

    /// <summary> Default sort of the Mods tab. </summary>
    public ModSort Sort { get; set; } = ModSort.Name;

    /// <summary> Show mods without any affected items. </summary>
    public bool ShowUnsorted { get; set; } = true;

    /// <summary> Loads settings or returns defaults. </summary>
    public static VitrineSettings Load(string path, Action<string> logWarning)
    {
        var settings = JsonUtil.ReadOrNull<VitrineSettings>(path, out var error);
        if (error is not null)
            logWarning($"Vitrine: could not read {path}: {error.Message}");
        return settings ?? new VitrineSettings();
    }

    /// <summary> Saves atomically. </summary>
    public void Save(string path, Action<string> logWarning)
    {
        try
        {
            JsonUtil.WriteAtomic(path, this);
        }
        catch (Exception ex)
        {
            logWarning($"Vitrine: could not write {path}: {ex.Message}");
        }
    }
}

/// <summary> Sort orders of the Mods tab. </summary>
public enum ModSort
{
    Name,
    Author,
    Category,
    RecentlyScanned,
}
