using System.IO;
using System.Security.Cryptography;
using System.Text;
using Vitrine.Catalog;

namespace Vitrine.Photos;

/// <summary> Where a mod's photo came from. </summary>
public enum PhotoSource
{
    /// <summary> No photo (lookup failed or nothing to look up). </summary>
    None,
    /// <summary> A file inside the mod folder (meta image or a conventional preview). </summary>
    Local,
    /// <summary> Downloaded from the mod page into the photo cache. </summary>
    Fetched,
}

/// <summary> One line of <c>photos/index.json</c>. </summary>
public sealed class PhotoIndexEntry
{
    public PhotoSource Source { get; set; }

    /// <summary> Absolute path of the image (local file or cached download). Empty when None. </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary> The mod's Website at the time of the lookup; a changed website retries a failed lookup. </summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary> The image URL extracted from the page, when fetched. </summary>
    public string ImageUrl { get; set; } = string.Empty;

    public DateTime FetchedAtUtc { get; set; }

    public string LastError { get; set; } = string.Empty;

    public int Attempts { get; set; }

    /// <summary> Failures are not retried before this time. </summary>
    public DateTime? NextRetryUtc { get; set; }
}

/// <summary> Serialized form of the photo index. </summary>
public sealed class PhotoIndexFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary> Mod directory → entry. </summary>
    public Dictionary<string, PhotoIndexEntry> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary> Framework-thread owner of the index file plus the cache path rules. </summary>
public sealed class PhotoIndex
{
    /// <summary> How long a failed lookup is left alone before it is retried. </summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromDays(7);

    private readonly string _path;
    private readonly Action<string> _logWarning;
    private PhotoIndexFile _file = new();
    private bool _dirty;

    public PhotoIndex(string path, Action<string> logWarning)
    {
        _path       = path;
        _logWarning = logWarning;
    }

    /// <summary> Every entry, read only. </summary>
    public IReadOnlyDictionary<string, PhotoIndexEntry> Entries
        => _file.Mods;

    public bool IsDirty
        => _dirty;

    /// <summary> Loads the file; a broken file is quarantined and the index starts empty. </summary>
    public void Load()
    {
        var file = JsonUtil.ReadOrNull<PhotoIndexFile>(_path, out var error);
        if (error is not null)
        {
            _logWarning($"Vitrine: photo index unreadable, starting fresh: {error.Message}");
            try
            {
                File.Move(_path, $"{_path}.broken-{DateTime.UtcNow:yyyyMMdd-HHmmss}", overwrite: true);
            }
            catch (Exception)
            {
                // The next save overwrites it.
            }
        }

        _file = file ?? new PhotoIndexFile();
        if (_file.Mods.Comparer != StringComparer.OrdinalIgnoreCase)
            _file.Mods = new Dictionary<string, PhotoIndexEntry>(_file.Mods, StringComparer.OrdinalIgnoreCase);
        _dirty = false;
    }

    /// <summary> Writes the file if anything changed. </summary>
    public void SaveIfDirty()
    {
        if (!_dirty)
            return;

        try
        {
            JsonUtil.WriteAtomic(_path, _file);
            _dirty = false;
        }
        catch (Exception ex)
        {
            _logWarning($"Vitrine: could not write photo index: {ex.Message}");
        }
    }

    public PhotoIndexEntry? Get(string modDirectory)
        => _file.Mods.TryGetValue(modDirectory, out var entry) ? entry : null;

    public void Set(string modDirectory, PhotoIndexEntry entry)
    {
        _file.Mods[modDirectory] = entry;
        _dirty = true;
    }

    public bool Remove(string modDirectory)
    {
        var removed = _file.Mods.Remove(modDirectory);
        _dirty |= removed;
        return removed;
    }

    public void Clear()
    {
        if (_file.Mods.Count == 0)
            return;
        _file.Mods.Clear();
        _dirty = true;
    }

    /// <summary> True when a failed entry may be retried now (retry window elapsed or the website changed). </summary>
    public static bool MayRetry(PhotoIndexEntry entry, string currentWebsite, DateTime nowUtc)
    {
        if (entry.Source != PhotoSource.None)
            return false;
        if (!string.Equals(entry.SourceUrl, currentWebsite, StringComparison.Ordinal))
            return true;
        return entry.NextRetryUtc is not { } next || nowUtc >= next;
    }

    /// <summary> Deterministic cache file name (without extension) for a mod directory: first 32 hex chars of SHA-256, case-insensitive. </summary>
    public static string CacheStem(string modDirectory)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(modDirectory.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }

    /// <summary> Picks the cache file extension from the content type, else the URL, else .jpg. </summary>
    public static string ExtensionFor(string? contentType, Uri? imageUrl)
    {
        switch (contentType?.ToLowerInvariant())
        {
            case "image/png":  return ".png";
            case "image/jpeg": return ".jpg";
            case "image/jpg":  return ".jpg";
            case "image/gif":  return ".gif";
            case "image/webp": return ".webp";
            case "image/bmp":  return ".bmp";
        }

        var fromUrl = imageUrl is null ? string.Empty : System.IO.Path.GetExtension(imageUrl.AbsolutePath).ToLowerInvariant();
        return fromUrl is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" ? (fromUrl == ".jpeg" ? ".jpg" : fromUrl) : ".jpg";
    }
}
