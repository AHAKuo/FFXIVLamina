using System.Security.Cryptography;
using System.Text;

namespace Vitrine.Catalog;

/// <summary> Stable Guids derived from strings, so auto designs keep their ids across rescans and reinstalls. </summary>
public static class DeterministicGuid
{
    private const string AutoDesignPrefix = "vitrine:auto:";

    /// <summary> The id of the auto design for a mod directory. Case-insensitive on the directory name. </summary>
    public static Guid ForAutoDesign(string modDirectory)
        => FromString(AutoDesignPrefix + modDirectory.Trim().ToLowerInvariant());

    /// <summary> SHA-256 of the UTF-8 text, truncated to 16 bytes, with the RFC 4122 version / variant bits set. </summary>
    public static Guid FromString(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        var bytes = hash[..16].ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version 5 style marker
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }
}
