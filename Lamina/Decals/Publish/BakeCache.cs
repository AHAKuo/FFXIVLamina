using System.IO;

namespace Lamina.Decals.Publish;

/// <summary> The on-disk bake cache: validity and membership of outputs, temporary names, and housekeeping. Plain file-system code, unit tested without Dalamud. </summary>
public static class BakeCache
{
    /// <summary> How many baked textures are kept; the oldest beyond that are deleted after every publish. </summary>
    public const int MaxFiles = 24;

    /// <summary> Temporary files older than this are leftovers of an interrupted conversion and are deleted by <see cref="Trim"/>. </summary>
    public static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(10);

    /// <summary> True when the output exists and is not empty (a failed conversion can leave an empty file behind). </summary>
    public static bool IsValidOutput(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary> True when <paramref name="path"/> lies inside the cache directory, i.e. it is one of Lamina's own bakes and never a valid upstream. </summary>
    public static bool IsCacheFile(string cacheDirectory, string path)
    {
        if (!Path.IsPathRooted(path))
            return false;

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary> The name a conversion writes to before the complete file is moved onto <paramref name="output"/>; unique per run so superseded runs never collide. </summary>
    public static string TempFile(string output, int runId)
        => FormattableString.Invariant($"{output}.{runId}.tmp");

    /// <summary> Marks a cache hit as recently used so housekeeping deletes genuinely old bakes first. Failures are ignored. </summary>
    public static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception)
        {
            // The game may hold the file; it simply keeps its old time.
        }
    }

    /// <summary>
    /// Deletes the oldest .tex files (by last write time) until at most <paramref name="keep"/> remain, plus stale temporary files.
    /// Files in <paramref name="protect"/> (the outputs just produced) are never deleted, even when that leaves more than
    /// <paramref name="keep"/>. Files that cannot be deleted (still open) are skipped. Returns the deleted files.
    /// </summary>
    public static IReadOnlyList<string> Trim(string cacheDirectory, int keep, IReadOnlyCollection<string> protect)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(cacheDirectory))
            return deleted;

        var protectedFiles = new HashSet<string>(protect.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var directory      = new DirectoryInfo(cacheDirectory);

        var bakes  = directory.EnumerateFiles("*.tex", SearchOption.TopDirectoryOnly).OrderBy(f => f.LastWriteTimeUtc).ToList();
        var excess = bakes.Count - Math.Max(0, keep);
        foreach (var file in bakes)
        {
            if (excess <= 0)
                break;
            if (protectedFiles.Contains(file.FullName))
                continue;
            if (TryDelete(file))
            {
                deleted.Add(file.FullName);
                excess--;
            }
        }

        var staleBefore = DateTime.UtcNow - StaleTempAge;
        foreach (var temp in directory.EnumerateFiles("*.tmp", SearchOption.TopDirectoryOnly))
            if (temp.LastWriteTimeUtc < staleBefore && TryDelete(temp))
                deleted.Add(temp.FullName);

        return deleted;
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
