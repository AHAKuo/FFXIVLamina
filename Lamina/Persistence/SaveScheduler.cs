using System.IO;
using Lamina.Models;

namespace Lamina.Persistence;

/// <summary>
/// Debounced saving: an item is written about one second after its last edit, not on every keystroke.
/// Dalamud-free; the owner calls <see cref="Tick"/> from its framework update and <see cref="FlushAll"/> on shutdown.
/// </summary>
public sealed class SaveScheduler
{
    /// <summary> Default delay after the last edit. </summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(1);

    /// <summary> How long a failed write waits before it is retried, so a persistent failure does not log every frame. </summary>
    public static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    private readonly Action<Profile> saveProfile;
    private readonly Action saveConfig;
    private readonly Action saveBiomes;
    private readonly Dictionary<Guid, (Profile Profile, DateTime LastMarked)> dirtyProfiles = new();

    private readonly Dictionary<string, DateTime> failedUntil = new();

    private DateTime? configMarked;
    private DateTime? biomesMarked;

    /// <summary> Creates a scheduler that writes through the given callbacks. </summary>
    /// <param name="saveProfile"> Writes one profile. </param>
    /// <param name="saveConfig"> Writes the config. </param>
    /// <param name="saveBiomes"> Writes the biome map. </param>
    /// <param name="delay"> Quiet time after the last edit before a write. Defaults to one second. </param>
    public SaveScheduler(Action<Profile> saveProfile, Action saveConfig, Action saveBiomes, TimeSpan? delay = null)
    {
        this.saveProfile = saveProfile;
        this.saveConfig  = saveConfig;
        this.saveBiomes  = saveBiomes;
        Delay            = delay ?? DefaultDelay;
    }

    /// <summary> Quiet time after the last edit before a write. </summary>
    public TimeSpan Delay { get; }

    /// <summary> True while anything is waiting to be written. </summary>
    public bool HasPending
        => dirtyProfiles.Count > 0 || configMarked is not null || biomesMarked is not null;

    /// <summary> Profiles waiting to be written. </summary>
    public IReadOnlyCollection<Guid> PendingProfiles
        => dirtyProfiles.Keys;

    /// <summary> Raised after every write, with a short description, so the host can log it. </summary>
    public event Action<string>? Saved;

    /// <summary> Raised when a write throws; the item stays dirty and is retried after <see cref="RetryBackoff"/>. </summary>
    public event Action<string, Exception>? SaveFailed;

    /// <summary> Marks a profile dirty, restarting its debounce window. </summary>
    public void MarkDirty(Profile profile, DateTime now)
        => dirtyProfiles[profile.Id] = (profile, now);

    /// <summary> Marks a profile dirty using the wall clock. </summary>
    public void MarkDirty(Profile profile)
        => MarkDirty(profile, DateTime.UtcNow);

    /// <summary> Marks the config dirty, restarting its debounce window. </summary>
    public void MarkConfigDirty(DateTime now)
        => configMarked = now;

    /// <summary> Marks the config dirty using the wall clock. </summary>
    public void MarkConfigDirty()
        => MarkConfigDirty(DateTime.UtcNow);

    /// <summary> Marks the biome map dirty, restarting its debounce window. </summary>
    public void MarkBiomesDirty(DateTime now)
        => biomesMarked = now;

    /// <summary> Marks the biome map dirty using the wall clock. </summary>
    public void MarkBiomesDirty()
        => MarkBiomesDirty(DateTime.UtcNow);

    /// <summary> Drops a pending profile write (the profile was deleted). </summary>
    public void Forget(Guid profileId)
        => dirtyProfiles.Remove(profileId);

    /// <summary> Writes every item whose quiet time elapsed. Call once per frame or so. </summary>
    public void Tick(DateTime now)
    {
        if (configMarked is { } c && now - c >= Delay)
            TryRun("config", now, () => { saveConfig(); configMarked = null; });

        if (biomesMarked is { } b && now - b >= Delay)
            TryRun("biomes", now, () => { saveBiomes(); biomesMarked = null; });

        if (dirtyProfiles.Count == 0)
            return;

        foreach (var id in dirtyProfiles.Keys.ToList())
        {
            var (profile, marked) = dirtyProfiles[id];
            if (now - marked < Delay)
                continue;
            TryRun($"profile {id}", now, () => { saveProfile(profile); dirtyProfiles.Remove(id); });
        }
    }

    /// <summary> Writes everything pending right now, regardless of the debounce window. </summary>
    public void FlushAll()
    {
        if (configMarked is not null)
            TryRun("config", null, () => { saveConfig(); configMarked = null; });
        if (biomesMarked is not null)
            TryRun("biomes", null, () => { saveBiomes(); biomesMarked = null; });
        foreach (var id in dirtyProfiles.Keys.ToList())
        {
            var profile = dirtyProfiles[id].Profile;
            TryRun($"profile {id}", null, () => { saveProfile(profile); dirtyProfiles.Remove(id); });
        }
    }

    /// <summary>
    /// Runs one write. Any exception is reported through <see cref="SaveFailed"/>; the item stays dirty. With a
    /// <paramref name="now"/>, an item that failed recently is skipped until its backoff elapsed (a flush ignores the backoff).
    /// </summary>
    private void TryRun(string what, DateTime? now, Action action)
    {
        if (now is { } n && failedUntil.TryGetValue(what, out var until) && n < until)
            return;

        try
        {
            action();
            failedUntil.Remove(what);
            Saved?.Invoke(what);
        }
        catch (Exception ex)
        {
            if (now is { } failedAt)
                failedUntil[what] = failedAt + RetryBackoff;
            SaveFailed?.Invoke(what, ex);
        }
    }
}
