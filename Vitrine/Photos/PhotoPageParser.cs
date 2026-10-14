using System.Net;
using System.Text.RegularExpressions;

namespace Vitrine.Photos;

/// <summary>
/// Extracts the display image of a mod page from its HTML, the way link previews do:
/// <c>og:image:secure_url</c>, then <c>og:image</c>, then <c>twitter:image</c> (name or property), then <c>&lt;link rel="image_src"&gt;</c>.
/// Attribute order and quoting do not matter; entities are decoded; relative URLs resolve against the page URL.
/// </summary>
public static partial class PhotoPageParser
{
    [GeneratedRegex(@"<(meta|link)\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"([A-Za-z_:][\w:.-]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", RegexOptions.Singleline)]
    private static partial Regex AttributeRegex();

    /// <summary> Candidate keys in priority order (lower index wins). </summary>
    private static readonly string[] MetaPriority =
    {
        "og:image:secure_url",
        "og:image",
        "og:image:url",
        "twitter:image",
        "twitter:image:src",
    };

    /// <summary> Returns the absolute http(s) URL of the page's display image, or null when none is declared. </summary>
    public static Uri? ExtractImageUrl(string? html, Uri pageUrl)
    {
        if (string.IsNullOrEmpty(html))
            return null;

        Uri? best     = null;
        var  bestRank = int.MaxValue;

        foreach (Match tag in TagRegex().Matches(html))
        {
            var attributes = ParseAttributes(tag.Groups[2].Value);
            var isLink     = tag.Groups[1].Value.Equals("link", StringComparison.OrdinalIgnoreCase);

            string? value;
            int     rank;
            if (isLink)
            {
                if (!attributes.TryGetValue("rel", out var rel) || !rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("image_src", StringComparer.OrdinalIgnoreCase))
                    continue;
                attributes.TryGetValue("href", out value);
                rank = MetaPriority.Length;
            }
            else
            {
                var key = attributes.TryGetValue("property", out var property) ? property
                        : attributes.TryGetValue("name", out var name)         ? name
                        : null;
                if (key is null)
                    continue;

                rank = Array.FindIndex(MetaPriority, k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (rank < 0)
                    continue;
                attributes.TryGetValue("content", out value);
            }

            if (rank >= bestRank)
                continue;
            if (TryResolve(value, pageUrl) is not { } uri)
                continue;

            best     = uri;
            bestRank = rank;
            if (rank == 0)
                break;
        }

        return best;
    }

    /// <summary> Parses the attributes of one tag; keys are lower case, values entity-decoded. </summary>
    public static Dictionary<string, string> ParseAttributes(string tagBody)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttributeRegex().Matches(tagBody))
        {
            var key   = m.Groups[1].Value;
            var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            if (!result.ContainsKey(key))
                result[key] = WebUtility.HtmlDecode(value).Trim();
        }

        return result;
    }

    private static Uri? TryResolve(string? value, Uri pageUrl)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!Uri.TryCreate(pageUrl, value, out var uri))
            return null;

        return uri.Scheme is "http" or "https" ? uri : null;
    }
}
