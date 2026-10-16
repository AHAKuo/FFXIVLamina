namespace Lamina.Decals.Publish;

/// <summary> Turns a GPU readback (row pitch, DXGI format) into a tight <see cref="RgbaImage"/>. Pure, so it is unit tested without Dalamud. </summary>
public static class RawImageRepacker
{
    /// <summary> DXGI_FORMAT_R8G8B8A8_UNORM: the format the publisher asks every readback for. </summary>
    public const int R8G8B8A8Unorm = 28;

    /// <summary> DXGI_FORMAT_R8G8B8A8_UNORM_SRGB. </summary>
    public const int R8G8B8A8UnormSrgb = 29;

    /// <summary> DXGI_FORMAT_B8G8R8A8_UNORM. </summary>
    public const int B8G8R8A8Unorm = 87;

    /// <summary> DXGI_FORMAT_B8G8R8A8_UNORM_SRGB. </summary>
    public const int B8G8R8A8UnormSrgb = 91;

    /// <summary>
    /// Copies <paramref name="width"/> * 4 bytes per row out of a buffer whose rows start <paramref name="pitch"/> bytes apart
    /// (the pitch may exceed the row, and the buffer may be longer than height * pitch), swapping B and R for BGRA formats.
    /// </summary>
    /// <exception cref="NotSupportedException"> The format is not an 8-bit RGBA / BGRA layout. </exception>
    /// <exception cref="ArgumentException"> The pitch is smaller than a row or the buffer is shorter than the described image. </exception>
    public static RgbaImage ToRgba(int width, int height, int pitch, int dxgiFormat, ReadOnlySpan<byte> data)
    {
        var swap = dxgiFormat switch
        {
            R8G8B8A8Unorm or R8G8B8A8UnormSrgb => false,
            B8G8R8A8Unorm or B8G8R8A8UnormSrgb => true,
            _                                  => throw new NotSupportedException($"Unsupported texture format {dxgiFormat}; expected an 8-bit RGBA layout."),
        };

        var image = new RgbaImage(width, height);
        var row   = width * 4;
        if (pitch < row)
            throw new ArgumentException($"Pitch {pitch} is smaller than a row of {row} bytes.", nameof(pitch));
        if (data.Length < (long)(height - 1) * pitch + row)
            throw new ArgumentException("The pixel buffer is shorter than the described image.", nameof(data));

        var pixels = image.Pixels.AsSpan();
        for (var y = 0; y < height; y++)
        {
            var destination = pixels.Slice(y * row, row);
            data.Slice(y * pitch, row).CopyTo(destination);
            if (swap)
                for (var x = 0; x < row; x += 4)
                    (destination[x], destination[x + 2]) = (destination[x + 2], destination[x]);
        }

        return image;
    }
}
