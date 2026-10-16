using System.Numerics;

namespace Lamina.Decals.Bake;

/// <summary> Pixel maths of the bake: the feather curve, bilinear decal sampling and straight-alpha compositing. </summary>
public static class Blend
{
    /// <summary> Hermite smoothstep: 0 at or below <paramref name="edge0"/>, 1 at or above <paramref name="edge1"/>. </summary>
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Bilinear sample at a pixel position (pixel centres sit at +0.5), clamped to the image edges. Colours are interpolated
    /// premultiplied so fully transparent pixels do not bleed their colour into their neighbours. Returns straight RGBA in 0..1.
    /// </summary>
    public static Vector4 SampleBilinear(RgbaImage image, float x, float y)
    {
        var fx = Math.Clamp(x - 0.5f, 0f, image.Width - 1f);
        var fy = Math.Clamp(y - 0.5f, 0f, image.Height - 1f);
        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = Math.Min(x0 + 1, image.Width - 1);
        var y1 = Math.Min(y0 + 1, image.Height - 1);
        var tx = fx - x0;
        var ty = fy - y0;

        var pixels = image.Pixels;
        var top    = Vector4.Lerp(Premultiplied(pixels, image.Offset(x0, y0)), Premultiplied(pixels, image.Offset(x1, y0)), tx);
        var bottom = Vector4.Lerp(Premultiplied(pixels, image.Offset(x0, y1)), Premultiplied(pixels, image.Offset(x1, y1)), tx);
        var c      = Vector4.Lerp(top, bottom, ty);
        return c.W > 1e-6f ? new Vector4(c.X / c.W, c.Y / c.W, c.Z / c.W, c.W) : Vector4.Zero;
    }

    /// <summary>
    /// Straight-alpha "over" of one texel: rgb = source * alpha + destination * (1 - alpha). The destination alpha is never
    /// reduced; it becomes max(destination alpha, alpha), because the skin diffuse alpha masks skin-tone application in game.
    /// </summary>
    public static void Over(RgbaImage target, int x, int y, Vector4 source, float alpha)
    {
        if (!(alpha > 0f))
            return;
        alpha = MathF.Min(alpha, 1f);

        var pixels = target.Pixels;
        var o      = target.Offset(x, y);
        var keep   = 1f - alpha;
        pixels[o]     = ToByte(source.X * 255f * alpha + pixels[o] * keep);
        pixels[o + 1] = ToByte(source.Y * 255f * alpha + pixels[o + 1] * keep);
        pixels[o + 2] = ToByte(source.Z * 255f * alpha + pixels[o + 2] * keep);
        var a = ToByte(alpha * 255f);
        if (a > pixels[o + 3])
            pixels[o + 3] = a;
    }

    private static Vector4 Premultiplied(byte[] pixels, int offset)
    {
        const float scale = 1f / 255f;
        var a = pixels[offset + 3] * scale;
        return new Vector4(pixels[offset] * scale * a, pixels[offset + 1] * scale * a, pixels[offset + 2] * scale * a, a);
    }

    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
}
