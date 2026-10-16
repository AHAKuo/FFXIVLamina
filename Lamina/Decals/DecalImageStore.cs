using System.IO;

namespace Lamina.Decals;

/// <summary>
/// Imported decal images. The UI copies a picked file into Lamina's decal image folder under a fresh name and the layer
/// keeps only that file name, so a profile never points outside the plugin's config folder and survives the original
/// file being moved. Pure file-system code, no game access.
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
    /// Copies <paramref name="sourcePath"/> into the image folder as "&lt;guid&gt;&lt;original extension&gt;" and returns
    /// that file name. The source is left untouched.
    /// </summary>
    /// <exception cref="FileNotFoundException"> The source does not exist. </exception>
    /// <exception cref="ArgumentException"> The source is not a supported image type. </exception>
    public string Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The image to import does not exist.", sourcePath);
        if (!IsSupported(sourcePath))
            throw new ArgumentException($"Unsupported image type '{Path.GetExtension(sourcePath)}'. Use {string.Join(", ", SupportedExtensions)}.", nameof(sourcePath));

        Directory.CreateDirectory(ImageDirectory);
        var fileName = Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath);
        File.Copy(sourcePath, FullPath(fileName), overwrite: false);
        return fileName;
    }

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
}
