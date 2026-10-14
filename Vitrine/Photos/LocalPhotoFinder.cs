using System.IO;

namespace Vitrine.Photos;

/// <summary> Finds a display image inside a mod folder, without touching the network. </summary>
public static class LocalPhotoFinder
{
    /// <summary> Image extensions Dalamud's texture loader decodes reliably. WebP is skipped: it needs a system codec that is not always installed. </summary>
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };

    /// <summary> Conventional preview file names, in priority order (extension-less). </summary>
    public static readonly string[] ConventionalNames = { "preview", "thumbnail", "thumb", "cover", "image", "screenshot", "icon" };

    /// <summary> Sub folders that commonly hold screenshots. </summary>
    public static readonly string[] ImageFolders = { "images", "image", "previews", "preview", "screenshots" };

    /// <summary>
    /// Resolution order: the meta image when it exists, a conventional file in the mod folder, then the first image in a known sub folder.
    /// Returns an absolute path or null.
    /// </summary>
    public static string? Find(string modFolder, string? metaImagePath)
    {
        if (!string.IsNullOrWhiteSpace(metaImagePath) && HasSupportedExtension(metaImagePath) && File.Exists(metaImagePath))
            return metaImagePath;

        if (string.IsNullOrWhiteSpace(modFolder) || !Directory.Exists(modFolder))
            return null;

        try
        {
            foreach (var name in ConventionalNames)
                foreach (var ext in Extensions)
                {
                    var candidate = Path.Combine(modFolder, name + ext);
                    if (File.Exists(candidate))
                        return candidate;
                }

            // Case-insensitive pass over the top level (file systems that are case sensitive, odd casing).
            var top = FirstImage(Directory.EnumerateFiles(modFolder), preferConventional: true);
            if (top is not null)
                return top;

            foreach (var folder in ImageFolders)
            {
                var sub = Path.Combine(modFolder, folder);
                if (!Directory.Exists(sub))
                    continue;
                var inSub = FirstImage(Directory.EnumerateFiles(sub), preferConventional: false);
                if (inSub is not null)
                    return inSub;
            }
        }
        catch (Exception)
        {
            // Unreadable folder: no local photo.
        }

        return null;
    }

    /// <summary> True for png / jpg / jpeg. </summary>
    public static bool HasSupportedExtension(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string? FirstImage(IEnumerable<string> files, bool preferConventional)
    {
        var images = files.Where(HasSupportedExtension).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
        if (images.Count == 0)
            return null;

        if (preferConventional)
        {
            var conventional = images.FirstOrDefault(f => ConventionalNames.Contains(Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase));
            if (conventional is not null)
                return conventional;
            // Top level: only conventional names count, random pngs at the root are usually textures.
            return null;
        }

        return images[0];
    }
}
