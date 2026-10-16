using Lamina.Models;

namespace Lamina.Persistence;

/// <summary>
/// The in-memory model the UI and the engine work on: config, biome overrides and every profile.
/// Edits made through this class mark the right file dirty and raise <see cref="Changed"/>; writes are debounced by a
/// <see cref="SaveScheduler"/> driven from <see cref="Tick"/>. In-place edits done directly on the models (e.g. the UI
/// toggling a checkbox) must be followed by <see cref="MarkDirty(Profile)"/>.
/// Dalamud-free so it can be driven from tests.
/// </summary>
public sealed class ProfileStore
{
    private const string SeedProfileName = "Main";
    private const string SeedGroupName   = "Default";

    private readonly JsonStore store;
    private readonly Func<DateTime> clock;
    private readonly SaveScheduler scheduler;
    private readonly List<Profile> profiles = new();

    /// <summary> Creates a store on top of a <see cref="JsonStore"/>. Nothing is read until <see cref="Load"/>. </summary>
    /// <param name="store"> File access. </param>
    /// <param name="clock"> UTC time source for the debounce. Defaults to <see cref="DateTime.UtcNow"/>. </param>
    /// <param name="saveDelay"> Debounce delay; defaults to one second. </param>
    public ProfileStore(JsonStore store, Func<DateTime>? clock = null, TimeSpan? saveDelay = null)
    {
        this.store = store;
        this.clock = clock ?? (() => DateTime.UtcNow);
        scheduler  = new SaveScheduler(store.SaveProfile, () => store.SaveConfig(Config), () => store.SaveBiomes(Biomes), saveDelay);
        scheduler.Saved      += what => Saved?.Invoke(what);
        scheduler.SaveFailed += (what, ex) => SaveFailed?.Invoke(what, ex);
    }

    /// <summary> Global settings. Edit in place, then call <see cref="MarkConfigDirty"/>. </summary>
    public LaminaConfig Config { get; private set; } = new();

    /// <summary> Territory → biome overrides. Edit in place, then call <see cref="MarkBiomesDirty"/>. </summary>
    public BiomeMap Biomes { get; private set; } = new();

    /// <summary> Every loaded profile, in load order (file name order). </summary>
    public IReadOnlyList<Profile> Profiles
        => profiles;

    /// <summary> True after <see cref="Load"/> ran. </summary>
    public bool IsLoaded { get; private set; }

    /// <summary> Errors and warnings from the last <see cref="Load"/>. </summary>
    public IReadOnlyList<LoadError> LoadErrors { get; private set; } = Array.Empty<LoadError>();

    /// <summary> Raised after Load and after every structural change (add / remove / move / enable). </summary>
    public event Action? Changed;

    /// <summary> Raised after a file was written. </summary>
    public event Action<string>? Saved;

    /// <summary> Raised when a write failed; the item is retried on the next tick. </summary>
    public event Action<string, Exception>? SaveFailed;

    /// <summary> True while something waits to be written. </summary>
    public bool HasPendingSaves
        => scheduler.HasPending;

    // ---------------------------------------------------------------------------------------------
    // Load / save
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads everything from disk. With no profiles at all, seeds a "Main" profile (enabled, one empty normal group) and saves it.
    /// Returns the errors found; broken files were set aside by the <see cref="JsonStore"/>.
    /// </summary>
    public IReadOnlyList<LoadError> Load()
    {
        var errors = new List<LoadError>();

        var (config, configError) = store.LoadConfig();
        if (configError is not null)
            errors.Add(configError);
        Config = config;
        store.BackupsToKeep = Config.BackupsToKeep;

        var (biomes, biomesError) = store.LoadBiomes();
        if (biomesError is not null)
            errors.Add(biomesError);
        Biomes = biomes;

        var (loaded, profileErrors) = store.LoadProfiles();
        errors.AddRange(profileErrors);
        profiles.Clear();
        profiles.AddRange(loaded);

        // One enabled profile per target: hand-edited or copied files may violate it; the first in file order wins.
        var claimed = new HashSet<Target>();
        foreach (var profile in profiles.Where(p => p.Enabled))
        {
            if (profile.Targets.Any(claimed.Contains))
            {
                profile.Enabled = false;
                errors.Add(new LoadError(store.ProfilePath(profile.Id),
                    $"Profile '{profile.Name}' was disabled: another enabled profile already targets the same character.", true));
                scheduler.MarkDirty(profile, clock());
            }
            else
            {
                foreach (var target in profile.Targets)
                    claimed.Add(target);
            }
        }

        if (profiles.Count == 0)
        {
            var seed = new Profile { Name = SeedProfileName, Enabled = true };
            seed.PassGroups.Add(new PassGroup { Name = SeedGroupName });
            profiles.Add(seed);
            store.SaveProfile(seed);
        }

        LoadErrors = errors;
        IsLoaded   = true;
        Changed?.Invoke();
        return errors;
    }

    /// <summary> Marks a profile for a debounced write. </summary>
    public void MarkDirty(Profile profile)
        => scheduler.MarkDirty(profile, clock());

    /// <summary> Marks the config for a debounced write. </summary>
    public void MarkConfigDirty()
    {
        store.BackupsToKeep = Config.BackupsToKeep;
        scheduler.MarkConfigDirty(clock());
    }

    /// <summary> Marks the biome map for a debounced write. </summary>
    public void MarkBiomesDirty()
        => scheduler.MarkBiomesDirty(clock());

    /// <summary> Writes everything pending immediately. </summary>
    public void SaveNow()
        => scheduler.FlushAll();

    /// <summary> Writes items whose debounce window elapsed. Call from the framework update. </summary>
    public void Tick(DateTime now)
        => scheduler.Tick(now);

    /// <summary> Writes items whose debounce window elapsed using the store clock. </summary>
    public void Tick()
        => scheduler.Tick(clock());

    // ---------------------------------------------------------------------------------------------
    // Profiles
    // ---------------------------------------------------------------------------------------------

    /// <summary> Finds a profile by id. </summary>
    public Profile? GetProfile(Guid id)
        => profiles.FirstOrDefault(p => p.Id == id);

    /// <summary> Finds a profile by name, case-insensitive. </summary>
    public Profile? FindProfile(string name)
        => profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary> Creates a disabled profile targeting the local player, with one empty normal group. </summary>
    public Profile CreateProfile(string name)
    {
        var profile = new Profile { Name = string.IsNullOrWhiteSpace(name) ? "New Profile" : name.Trim() };
        profile.PassGroups.Add(new PassGroup { Name = SeedGroupName });
        profiles.Add(profile);
        MarkDirty(profile);
        Changed?.Invoke();
        return profile;
    }

    /// <summary> Removes a profile and its file. Returns false when unknown. </summary>
    public bool DeleteProfile(Guid id)
    {
        var profile = GetProfile(id);
        if (profile is null)
            return false;
        profiles.Remove(profile);
        scheduler.Forget(id);
        store.DeleteProfile(id);
        Changed?.Invoke();
        return true;
    }

    /// <summary> Deep copies a profile (fresh ids everywhere, disabled) and inserts it after the original. </summary>
    public Profile? DuplicateProfile(Guid id)
    {
        var profile = GetProfile(id);
        if (profile is null)
            return null;
        var copy = Cloner.CloneWithFreshIds(profile);
        copy.Name    = UniqueName(profile.Name, profiles.Select(p => p.Name));
        copy.Enabled = false;
        profiles.Insert(profiles.IndexOf(profile) + 1, copy);
        MarkDirty(copy);
        Changed?.Invoke();
        return copy;
    }

    /// <summary> Renames a profile. </summary>
    public void RenameProfile(Profile profile, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || profile.Name == name)
            return;
        profile.Name = name.Trim();
        MarkDirty(profile);
        Changed?.Invoke();
    }

    /// <summary>
    /// Enables or disables a profile. Enabling disables every other enabled profile that shares a target
    /// (one active profile per target). Returns the profiles that were disabled as a side effect.
    /// </summary>
    public IReadOnlyList<Profile> SetProfileEnabled(Profile profile, bool enabled)
    {
        var disabled = new List<Profile>();
        if (enabled)
        {
            foreach (var other in profiles)
            {
                if (ReferenceEquals(other, profile) || !other.Enabled)
                    continue;
                if (!other.Targets.Any(t => profile.Targets.Contains(t)))
                    continue;
                other.Enabled = false;
                disabled.Add(other);
                MarkDirty(other);
            }
        }

        if (profile.Enabled != enabled)
        {
            profile.Enabled = enabled;
            MarkDirty(profile);
        }

        if (disabled.Count > 0 || profile.Enabled == enabled)
            Changed?.Invoke();
        return disabled;
    }

    /// <summary> The enabled profile that targets <paramref name="target"/>, if any. </summary>
    public Profile? ActiveProfileFor(Target target)
        => profiles.FirstOrDefault(p => p.Enabled && p.Targets.Contains(target));

    /// <summary> Profile containing a group, if any. </summary>
    public Profile? ProfileOf(PassGroup group)
        => profiles.FirstOrDefault(p => p.PassGroups.Contains(group));

    /// <summary> Profile containing a pass, if any. </summary>
    public Profile? ProfileOf(Pass pass)
        => profiles.FirstOrDefault(p => p.PassGroups.Any(g => g.Passes.Contains(pass)));

    /// <summary> Group containing a pass, if any. </summary>
    public PassGroup? GroupOf(Pass pass)
        => profiles.SelectMany(p => p.PassGroups).FirstOrDefault(g => g.Passes.Contains(pass));

    // ---------------------------------------------------------------------------------------------
    // Pass groups
    // ---------------------------------------------------------------------------------------------

    /// <summary> Appends a new normal group. </summary>
    public PassGroup AddGroup(Profile profile, string? name = null)
    {
        var group = new PassGroup { Name = string.IsNullOrWhiteSpace(name) ? UniqueName("New Group", profile.PassGroups.Select(g => g.Name)) : name.Trim() };
        profile.PassGroups.Add(group);
        Touch(profile);
        return group;
    }

    /// <summary> Removes a group. Returns false when it is not in the profile. </summary>
    public bool RemoveGroup(Profile profile, Guid groupId)
    {
        var index = profile.PassGroups.FindIndex(g => g.Id == groupId);
        if (index < 0)
            return false;
        profile.PassGroups.RemoveAt(index);
        Touch(profile);
        return true;
    }

    /// <summary> Moves a group from one list position to another. </summary>
    public bool MoveGroup(Profile profile, int fromIndex, int toIndex)
    {
        if (!Move(profile.PassGroups, fromIndex, toIndex))
            return false;
        Touch(profile);
        return true;
    }

    /// <summary> Inserts a deep copy (fresh ids) right after the original. </summary>
    public PassGroup? DuplicateGroup(Profile profile, Guid groupId)
    {
        var index = profile.PassGroups.FindIndex(g => g.Id == groupId);
        if (index < 0)
            return null;
        var copy = Cloner.CloneWithFreshIds(profile.PassGroups[index]);
        copy.Name = UniqueName(copy.Name, profile.PassGroups.Select(g => g.Name));
        profile.PassGroups.Insert(index + 1, copy);
        Touch(profile);
        return copy;
    }

    /// <summary> Appends a deep copy (fresh ids) of a group to another (or the same) profile. </summary>
    public PassGroup CopyGroupTo(PassGroup group, Profile targetProfile)
    {
        var copy = Cloner.CloneWithFreshIds(group);
        copy.Name = UniqueName(copy.Name, targetProfile.PassGroups.Select(g => g.Name));
        targetProfile.PassGroups.Add(copy);
        Touch(targetProfile);
        return copy;
    }

    /// <summary> Moves a group (same ids) from its profile to the end of another. Returns false when it is not in <paramref name="sourceProfile"/>. </summary>
    public bool MoveGroupTo(Profile sourceProfile, PassGroup group, Profile targetProfile)
    {
        if (ReferenceEquals(sourceProfile, targetProfile) || !sourceProfile.PassGroups.Remove(group))
            return false;
        targetProfile.PassGroups.Add(group);
        MarkDirty(sourceProfile);
        Touch(targetProfile);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Passes
    // ---------------------------------------------------------------------------------------------

    /// <summary> Appends a new pass to a group. </summary>
    public Pass AddPass(Profile profile, PassGroup group, string? name = null)
    {
        var pass = new Pass { Name = string.IsNullOrWhiteSpace(name) ? UniqueName("New Pass", group.Passes.Select(p => p.Name)) : name.Trim() };
        group.Passes.Add(pass);
        Touch(profile);
        return pass;
    }

    /// <summary> Removes a pass from a group. </summary>
    public bool RemovePass(Profile profile, PassGroup group, Guid passId)
    {
        var index = group.Passes.FindIndex(p => p.Id == passId);
        if (index < 0)
            return false;
        group.Passes.RemoveAt(index);
        Touch(profile);
        return true;
    }

    /// <summary> Moves a pass from one list position to another inside its group. </summary>
    public bool MovePass(Profile profile, PassGroup group, int fromIndex, int toIndex)
    {
        if (!Move(group.Passes, fromIndex, toIndex))
            return false;
        Touch(profile);
        return true;
    }

    /// <summary> Inserts a deep copy (fresh ids) right after the original. </summary>
    public Pass? DuplicatePass(Profile profile, PassGroup group, Guid passId)
    {
        var index = group.Passes.FindIndex(p => p.Id == passId);
        if (index < 0)
            return null;
        var copy = Cloner.CloneWithFreshIds(group.Passes[index]);
        copy.Name = UniqueName(copy.Name, group.Passes.Select(p => p.Name));
        group.Passes.Insert(index + 1, copy);
        Touch(profile);
        return copy;
    }

    /// <summary> Appends a deep copy (fresh ids) of a pass to another (or the same) group. </summary>
    public Pass CopyPassTo(Pass pass, Profile targetProfile, PassGroup targetGroup)
    {
        var copy = Cloner.CloneWithFreshIds(pass);
        copy.Name = UniqueName(copy.Name, targetGroup.Passes.Select(p => p.Name));
        targetGroup.Passes.Add(copy);
        Touch(targetProfile);
        return copy;
    }

    /// <summary> Moves a pass (same ids) to the end of another group, possibly in another profile. </summary>
    public bool MovePassTo(Profile sourceProfile, PassGroup sourceGroup, Pass pass, Profile targetProfile, PassGroup targetGroup)
    {
        if (ReferenceEquals(sourceGroup, targetGroup) || !sourceGroup.Passes.Remove(pass))
            return false;
        targetGroup.Passes.Add(pass);
        MarkDirty(sourceProfile);
        Touch(targetProfile);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Layers
    // ---------------------------------------------------------------------------------------------

    /// <summary> Appends a layer to a pass. </summary>
    public void AddLayer(Profile profile, Pass pass, Layer layer)
    {
        pass.Layers.Add(layer);
        Touch(profile);
    }

    /// <summary> Removes a layer from a pass. </summary>
    public bool RemoveLayer(Profile profile, Pass pass, Guid layerId)
    {
        var index = pass.Layers.FindIndex(l => l.Id == layerId);
        if (index < 0)
            return false;
        pass.Layers.RemoveAt(index);
        Touch(profile);
        return true;
    }

    /// <summary> Moves a layer from one list position to another inside its pass. </summary>
    public bool MoveLayer(Profile profile, Pass pass, int fromIndex, int toIndex)
    {
        if (!Move(pass.Layers, fromIndex, toIndex))
            return false;
        Touch(profile);
        return true;
    }

    /// <summary> Inserts a deep copy (fresh id) right after the original. Returns null when the layer is not in the pass. </summary>
    public Layer? DuplicateLayer(Profile profile, Pass pass, Guid layerId)
    {
        var index = pass.Layers.FindIndex(l => l.Id == layerId);
        if (index < 0)
            return null;
        var copy = Cloner.CloneWithFreshIds(pass.Layers[index]);
        pass.Layers.Insert(index + 1, copy);
        Touch(profile);
        return copy;
    }

    /// <summary> Appends a deep copy (fresh id) of a layer to another (or the same) pass. </summary>
    public Layer CopyLayerTo(Layer layer, Profile targetProfile, Pass targetPass)
    {
        var copy = Cloner.CloneWithFreshIds(layer);
        targetPass.Layers.Add(copy);
        Touch(targetProfile);
        return copy;
    }

    /// <summary>
    /// Moves a layer (same id) to the end of another pass, possibly in another profile. Returns false when both passes are
    /// the same or the layer is not in <paramref name="sourcePass"/>.
    /// </summary>
    public bool MoveLayerTo(Profile sourceProfile, Pass sourcePass, Layer layer, Profile targetProfile, Pass targetPass)
    {
        if (ReferenceEquals(sourcePass, targetPass) || !sourcePass.Layers.Remove(layer))
            return false;
        targetPass.Layers.Add(layer);
        MarkDirty(sourceProfile);
        Touch(targetProfile);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private void Touch(Profile profile)
    {
        MarkDirty(profile);
        Changed?.Invoke();
    }

    private static bool Move<T>(List<T> list, int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= list.Count || toIndex < 0 || toIndex >= list.Count || fromIndex == toIndex)
            return false;
        var item = list[fromIndex];
        list.RemoveAt(fromIndex);
        list.Insert(toIndex, item);
        return true;
    }

    /// <summary> "Name", then "Name (2)", "Name (3)" … until unused among <paramref name="existing"/>. </summary>
    public static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName))
            return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }
}
