using System.IO;
using System.Globalization;
using System.Text;
using Lamina.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lamina.Persistence;

/// <summary> A problem found while loading. Warnings mean the data still loaded (best effort). </summary>
/// <param name="File"> Full path of the file concerned. </param>
/// <param name="Message"> What went wrong. </param>
/// <param name="IsWarning"> True when the file still produced an object. </param>
public sealed record LoadError(string File, string Message, bool IsWarning = false)
{
    /// <inheritdoc />
    public override string ToString()
        => $"{(IsWarning ? "Warning" : "Error")}: {Path.GetFileName(File)}: {Message}";
}

/// <summary>
/// Reads and writes every Lamina file (TDD 10.1):
/// <code>
/// root/
///   config.json            LaminaConfig
///   biomes.json            BiomeMap overrides
///   profiles/&lt;id&gt;.json    one Profile each
///   backups/&lt;timestamp&gt;/  rotating copies of overwritten files
///   vitrine/               reserved for the Vitrine module
/// </code>
/// Writes are atomic (temp file then move). A broken profile only breaks that profile.
/// Contains no Dalamud types; the clock is injectable for tests.
/// </summary>
public sealed class JsonStore
{
    private const string TempSuffix   = ".tmp";
    private const string BrokenMarker = ".broken-";

    private readonly Func<DateTime> clock;
    private readonly JsonSerializer serializer = JsonSettings.CreateSerializer();

    private string?  lastBackupFolder;
    private DateTime lastBackupAt = DateTime.MinValue;

    /// <summary> Creates a store rooted at <paramref name="rootDirectory"/>. Directories are created on first write. </summary>
    /// <param name="rootDirectory"> Usually the plugin config directory. </param>
    /// <param name="clock"> Local time source for backup folder names. Defaults to <see cref="DateTime.Now"/>. </param>
    public JsonStore(string rootDirectory, Func<DateTime>? clock = null)
    {
        RootDirectory     = Path.GetFullPath(rootDirectory);
        ProfilesDirectory = Path.Combine(RootDirectory, "profiles");
        BackupsDirectory  = Path.Combine(RootDirectory, "backups");
        VitrineDirectory  = Path.Combine(RootDirectory, "vitrine");
        ConfigPath        = Path.Combine(RootDirectory, "config.json");
        BiomesPath        = Path.Combine(RootDirectory, "biomes.json");
        this.clock        = clock ?? (() => DateTime.Now);
    }

    /// <summary> Root of all Lamina files. </summary>
    public string RootDirectory { get; }

    /// <summary> Folder holding one JSON file per profile. </summary>
    public string ProfilesDirectory { get; }

    /// <summary> Folder holding rotating backup folders. </summary>
    public string BackupsDirectory { get; }

    /// <summary> Folder reserved for Vitrine's cache and designs. </summary>
    public string VitrineDirectory { get; }

    /// <summary> Path of config.json. </summary>
    public string ConfigPath { get; }

    /// <summary> Path of biomes.json. </summary>
    public string BiomesPath { get; }

    /// <summary> How many backup folders to keep. Older ones are deleted after each backup. </summary>
    public int BackupsToKeep { get; set; } = 10;

    /// <summary> Backups made within this window share one folder, so a save burst costs one folder. </summary>
    public TimeSpan BackupBurstWindow { get; set; } = TimeSpan.FromSeconds(60);

    // ---------------------------------------------------------------------------------------------
    // Config
    // ---------------------------------------------------------------------------------------------

    /// <summary> Loads config.json. A missing file yields defaults; a broken file is set aside and defaults are returned with an error. </summary>
    public (LaminaConfig Config, LoadError? Error) LoadConfig()
    {
        var (config, error) = LoadSingle<LaminaConfig>(ConfigPath, FileKind.Config);
        return (config ?? new LaminaConfig(), error);
    }

    /// <summary> Writes config.json atomically, backing up the previous version. </summary>
    public void SaveConfig(LaminaConfig config)
    {
        config.Version = LaminaConfig.CurrentVersion;
        WriteAtomic(ConfigPath, config);
    }

    // ---------------------------------------------------------------------------------------------
    // Biomes
    // ---------------------------------------------------------------------------------------------

    /// <summary> Loads biomes.json. Missing = empty map. </summary>
    public (BiomeMap Biomes, LoadError? Error) LoadBiomes()
    {
        var (biomes, error) = LoadSingle<BiomeMap>(BiomesPath, FileKind.Biomes);
        return (biomes ?? new BiomeMap(), error);
    }

    /// <summary> Writes biomes.json atomically, backing up the previous version. </summary>
    public void SaveBiomes(BiomeMap biomes)
    {
        biomes.Version = BiomeMap.CurrentVersion;
        WriteAtomic(BiomesPath, biomes);
    }

    // ---------------------------------------------------------------------------------------------
    // Profiles
    // ---------------------------------------------------------------------------------------------

    /// <summary> Path of a profile's file. </summary>
    public string ProfilePath(Guid id)
        => Path.Combine(ProfilesDirectory, id.ToString("D") + ".json");

    /// <summary>
    /// Loads every profiles/*.json. A file that cannot be parsed is renamed to <c>&lt;name&gt;.broken-&lt;timestamp&gt;</c>
    /// and reported; every other profile still loads. Files from a newer version load best effort with a warning.
    /// </summary>
    public (List<Profile> Profiles, List<LoadError> Errors) LoadProfiles()
    {
        var profiles = new List<Profile>();
        var errors   = new List<LoadError>();
        if (!Directory.Exists(ProfilesDirectory))
            return (profiles, errors);

        foreach (var file in Directory.EnumerateFiles(ProfilesDirectory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (file.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase) || file.Contains(BrokenMarker, StringComparison.OrdinalIgnoreCase))
                continue;

            var (profile, error) = LoadSingle<Profile>(file, FileKind.Profile);
            if (error is not null)
                errors.Add(error);
            if (profile is null)
                continue;

            if (profile.Id == Guid.Empty)
            {
                profile.Id = Guid.NewGuid();
                errors.Add(new LoadError(file, "Profile had no id; a new one was assigned.", IsWarning: true));
            }

            if (profiles.Any(p => p.Id == profile.Id))
            {
                errors.Add(new LoadError(file, $"Duplicate profile id {profile.Id}; file skipped."));
                continue;
            }

            profiles.Add(profile);
        }

        return (profiles, errors);
    }

    /// <summary> Writes profiles/&lt;id&gt;.json atomically, backing up the previous version. </summary>
    public void SaveProfile(Profile profile)
    {
        profile.Version = Profile.CurrentVersion;
        WriteAtomic(ProfilePath(profile.Id), profile);
    }

    /// <summary> Deletes a profile file after backing it up. Missing files are ignored. </summary>
    public void DeleteProfile(Guid id)
    {
        var path = ProfilePath(id);
        if (!File.Exists(path))
            return;
        Backup(path);
        File.Delete(path);
    }

    // ---------------------------------------------------------------------------------------------
    // Backups
    // ---------------------------------------------------------------------------------------------

    /// <summary> Backup folders, newest first. </summary>
    public IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(BackupsDirectory))
            return Array.Empty<string>();
        return Directory.EnumerateDirectories(BackupsDirectory)
                        .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                        .ToList();
    }

    /// <summary> Copies a file into the current backup folder (one folder per burst) and prunes old folders. </summary>
    private void Backup(string path)
    {
        if (!File.Exists(path))
            return;

        var now = clock();
        if (lastBackupFolder is null || now - lastBackupAt > BackupBurstWindow || !Directory.Exists(lastBackupFolder))
        {
            var name = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var folder = Path.Combine(BackupsDirectory, name);
            var suffix = 1;
            while (Directory.Exists(folder))
                folder = Path.Combine(BackupsDirectory, $"{name}-{suffix++}");
            Directory.CreateDirectory(folder);
            lastBackupFolder = folder;
            lastBackupAt     = now;
        }

        // Mirror the relative path so profiles/<id>.json and config.json never collide.
        var relative = Path.GetRelativePath(RootDirectory, path);
        var target   = Path.Combine(lastBackupFolder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target))
            File.Copy(path, target);

        Prune();
    }

    private void Prune()
    {
        var keep = Math.Max(0, BackupsToKeep);
        foreach (var old in ListBackups().Skip(keep))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (IOException)
            {
                // A locked folder is pruned on the next backup.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------

    private (T? Value, LoadError? Error) LoadSingle<T>(string path, FileKind kind) where T : class
    {
        if (!File.Exists(path))
            return (null, null);

        JObject obj;
        try
        {
            obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, SetAside(path, $"Could not parse: {ex.Message}"));
        }

        LoadError? warning = null;
        try
        {
            var migration = Migrations.Upgrade(obj, kind);
            if (migration.IsNewerThanSupported)
                warning = new LoadError(path, $"File version {migration.FromVersion} is newer than this build supports ({Migrations.CurrentVersion(kind)}); loaded best effort.", IsWarning: true);

            var value = obj.ToObject<T>(serializer);
            return value is null
                ? (null, SetAside(path, "File did not contain an object."))
                : (value, warning);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            return (null, SetAside(path, $"Could not read: {ex.Message}"));
        }
    }

    private LoadError SetAside(string path, string message)
    {
        var stamp  = clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = path + BrokenMarker + stamp;
        var suffix = 1;
        while (File.Exists(target))
            target = $"{path}{BrokenMarker}{stamp}-{suffix++}";
        try
        {
            File.Move(path, target);
            return new LoadError(path, $"{message} The file was moved to {Path.GetFileName(target)}.");
        }
        catch (IOException ex)
        {
            return new LoadError(path, $"{message} It could not be moved aside: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new LoadError(path, $"{message} It could not be moved aside: {ex.Message}");
        }
    }

    /// <summary> Writes to a temp file next to the target, then moves it over the target. A crash never leaves half a file. </summary>
    private void WriteAtomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Backup(path);

        var temp = path + TempSuffix;
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var text = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        using (var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented, Indentation = 2 })
        {
            serializer.Serialize(writer, value);
            writer.Flush();
            text.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
