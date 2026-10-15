using Newtonsoft.Json.Linq;

namespace Lamina.Persistence;

/// <summary> Which file a version applies to. Each file kind has its own version line. </summary>
public enum FileKind
{
    Config,
    Biomes,
    Profile,
}

/// <summary> One upgrade step: transforms a file from <see cref="FromVersion"/> to <see cref="FromVersion"/> + 1 in place. </summary>
/// <param name="Kind"> The file kind this step applies to. </param>
/// <param name="FromVersion"> The version the step expects as input. </param>
/// <param name="Apply"> Mutates the parsed JSON object. </param>
public sealed record MigrationStep(FileKind Kind, int FromVersion, Action<JObject> Apply);

/// <summary>
/// Step by step migration chain (v1 → v2 → v3 …). Every file carries a "version"; <see cref="Upgrade"/> applies the
/// steps that start at the file's version until the target version is reached, then stamps the new version.
/// </summary>
public static class Migrations
{
    /// <summary> Name of the version property in every file. </summary>
    public const string VersionProperty = "version";

    /// <summary> Steps shipped with this build. Append new steps here, never reorder. </summary>
    public static readonly IReadOnlyList<MigrationStep> Steps = new List<MigrationStep>
    {
        // Profile v1 → v2: Penumbra Adjust mod entries gained "applies" semantics (state / priority / each option group may be
        // null = inherited) and the Equipment / Appearance layer kinds appeared. The JSON shape of v1 files is still valid:
        // their entries set the state and every listed group, so nothing is rewritten; the bump only tells older builds
        // (which cannot read a null state) that the file is newer.
        new(FileKind.Profile, 1, _ => { }),
    };

    /// <summary> Current version of each file kind. </summary>
    public static int CurrentVersion(FileKind kind)
        => kind switch
        {
            FileKind.Config  => Models.LaminaConfig.CurrentVersion,
            FileKind.Biomes  => Models.BiomeMap.CurrentVersion,
            FileKind.Profile => Models.Profile.CurrentVersion,
            _                => 1,
        };

    /// <summary> Reads the version of a parsed file. Missing or malformed = 1. </summary>
    public static int ReadVersion(JObject file)
        => file[VersionProperty] is { Type: JTokenType.Integer } v ? v.Value<int>() : 1;

    /// <summary> Upgrades a file in place using the shipped steps. Returns the version the file ended at. </summary>
    public static MigrationResult Upgrade(JObject file, FileKind kind)
        => Upgrade(file, kind, CurrentVersion(kind), Steps);

    /// <summary>
    /// Upgrades a file in place to <paramref name="targetVersion"/> using <paramref name="steps"/>.
    /// Files newer than the target are left untouched and flagged so the caller can warn.
    /// </summary>
    public static MigrationResult Upgrade(JObject file, FileKind kind, int targetVersion, IReadOnlyList<MigrationStep> steps)
    {
        var from    = ReadVersion(file);
        var version = from;
        if (version > targetVersion)
            return new MigrationResult(from, version, IsNewerThanSupported: true);

        while (version < targetVersion)
        {
            var current = version;
            var step    = steps.FirstOrDefault(s => s.Kind == kind && s.FromVersion == current);
            if (step is null)
                throw new InvalidOperationException($"No migration step from {kind} version {current} to {current + 1}.");
            step.Apply(file);
            version++;
        }

        file[VersionProperty] = version;
        return new MigrationResult(from, version, IsNewerThanSupported: false);
    }
}

/// <summary> Outcome of a migration pass. </summary>
/// <param name="FromVersion"> Version found in the file. </param>
/// <param name="ToVersion"> Version after migration. </param>
/// <param name="IsNewerThanSupported"> True when the file came from a newer build and was loaded best-effort. </param>
public sealed record MigrationResult(int FromVersion, int ToVersion, bool IsNewerThanSupported)
{
    /// <summary> True when at least one step ran. </summary>
    public bool Migrated
        => ToVersion != FromVersion;
}
