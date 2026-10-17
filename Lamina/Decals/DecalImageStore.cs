using System.IO;
using System.Security.Cryptography;
using Lamina.Models;

namespace Lamina.Decals;

/// <summary>
/// Imported decal images. The UI copies a picked file into Lamina's decal image folder under a name derived from its content
/// (SHA-256 of the bytes plus the extension), so picking the same image twice yields the same file and the layer keeps only
/// that file name: a profile never points outside the plugin's config folder and survives the original file being moved.
/// <see cref="Prune"/> deletes images no layer references any more. Pure file-system code, no game access.
/// </summary>
public sealed class DecalImageStore
{
    /// <summary> Extensions the image picker offers, lower case with the dot. </summary>
    public static readonly IReadOnlyList<string> SupportedExtensions = new[] { ".png", ".jpg", ".jpeg" };

    public DecalImageStore(string imageDirectory)
    {
        if (string.IsNullOrWhiteSpace(imageDirectory))
            throw new ArgumentException("The image directory must be set.", nameof(imageDirectory));
        ImageDirectory = imageDirectory;
    }

    /// <summary> The folder imported images live in (<see cref="DecalPaths.ImageDirectory"/>). Created on the first import. </summary>
    public string ImageDirectory { get; }

    /// <summary> True for a file the picker accepts, by extension (case-insensitive). </summary>
    public static bool IsSupported(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary> A bare file name: not empty, no directory part, not "." or "..". </summary>
    public static bool IsValidName(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName)
        && fileName.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, Path.VolumeSeparatorChar }) < 0
        && fileName != "." && fileName != "..";

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into the image folder as "&lt;sha256 of the bytes&gt;&lt;extension, lower case&gt;"
    /// and returns that file name. An image with the same bytes imported before is found again and not copied twice. The
    /// source is left untouched.
    /// </summary>
    /// <exception cref="FileNotFoundException"> The source does not exist. </exception>
    /// <exception cref="ArgumentException"> The source is not a supported image type. </exception>
    public string Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The image to import does not exist.", sourcePath);
        if (!IsSupported(sourcePath))
            throw new ArgumentException($"Unsupported image type '{Path.GetExtension(sourcePath)}'. Use {string.Join(", ", SupportedExtensions)}.", nameof(sourcePath));

        var bytes    = File.ReadAllBytes(sourcePath);
        var fileName = ContentName(bytes, Path.GetExtension(sourcePath));
        var target   = FullPath(fileName);
        if (File.Exists(target))
            return fileName;

        Directory.CreateDirectory(ImageDirectory);
        File.WriteAllBytes(target, bytes);
        return fileName;
    }

    /// <summary> The content-derived file name an image with these bytes gets: 64 hex characters of SHA-256 plus the lower-case extension. </summary>
    public static string ContentName(ReadOnlySpan<byte> bytes, string extension)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + extension.ToLowerInvariant();

    /// <summary> Absolute path of an imported image. </summary>
    /// <exception cref="ArgumentException"> <paramref name="fileName"/> is empty or has a directory part. </exception>
    public string FullPath(string fileName)
    {
        if (!IsValidName(fileName))
            throw new ArgumentException("A decal image is referenced by its bare file name.", nameof(fileName));
        return Path.Combine(ImageDirectory, fileName);
    }

    /// <summary> True when the named image exists in the folder. False for null, empty or path-like names. </summary>
    public bool Exists(string? fileName)
        => IsValidName(fileName) && File.Exists(Path.Combine(ImageDirectory, fileName!));

    /// <summary>
    /// Deletes every supported image in the folder that <paramref name="referencedFileNames"/> does not name (case-insensitive).
    /// A referenced file is never deleted; an empty set deletes every image. Files that cannot be deleted are skipped. Returns
    /// the deleted file names.
    /// </summary>
    public IReadOnlyList<string> Prune(IEnumerable<string> referencedFileNames)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(ImageDirectory))
            return deleted;

        var referenced = new HashSet<string>(referencedFileNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        foreach (var file in new DirectoryInfo(ImageDirectory).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            if (!IsSupported(file.Name) || referenced.Contains(file.Name))
                continue;

            try
            {
                file.Delete();
                deleted.Add(file.Name);
            }
            catch (IOException)
            {
                // Still open (the texture provider may hold it); next time.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }

        return deleted;
    }

    /// <summary> Every image file name a decal layer in any of <paramref name="profiles"/> references (all groups, all passes, both layer sections). </summary>
    public static HashSet<string> ReferencedImages(IEnumerable<Profile> profiles)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
            foreach (var group in profile.PassGroups)
                foreach (var pass in group.Passes)
                    foreach (var layer in pass.AllLayers)
                        if (layer is DecalLayer { ImageFile: { } image } && !string.IsNullOrWhiteSpace(image))
                            names.Add(image);
        return names;
    }
}
