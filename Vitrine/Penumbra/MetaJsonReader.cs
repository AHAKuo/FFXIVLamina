using System.IO;
using Newtonsoft.Json.Linq;

namespace Vitrine.Penumbra;

/// <summary> Fallback metadata source: Penumbra's <c>meta.json</c> inside the mod folder. </summary>
public static class MetaJsonReader
{
    /// <summary> Reads <c>&lt;root&gt;/&lt;modDirectory&gt;/meta.json</c>. Returns null when missing or unreadable. </summary>
    public static ModMeta? Read(string root, string modDirectory, Action<string>? logDebug = null)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(modDirectory))
            return null;

        var folder = Path.Combine(root, modDirectory);
        var path   = Path.Combine(folder, "meta.json");
        try
        {
            if (!File.Exists(path))
                return null;

            var obj = JObject.Parse(File.ReadAllText(path));
            return Parse(obj, folder);
        }
        catch (Exception ex)
        {
            logDebug?.Invoke($"Vitrine: could not read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary> Parses a meta.json object. <paramref name="folder"/> resolves the relative Image path. </summary>
    public static ModMeta Parse(JObject obj, string folder)
    {
        var image = obj.Value<string>("Image") ?? string.Empty;
        var imagePath = string.IsNullOrWhiteSpace(image)
            ? string.Empty
            : Path.IsPathRooted(image) ? image : Path.Combine(folder, image);

        var tags = obj["ModTags"] is JArray arr
            ? arr.Select(t => t.Type == JTokenType.String ? t.Value<string>() ?? string.Empty : string.Empty).Where(t => t.Length > 0).ToList()
            : new List<string>();

        return new ModMeta(
            obj.Value<string>("Name") ?? string.Empty,
            obj.Value<string>("Author") ?? string.Empty,
            obj.Value<string>("Description") ?? string.Empty,
            obj.Value<string>("Version") ?? string.Empty,
            obj.Value<string>("Website") ?? string.Empty,
            imagePath,
            tags,
            folder);
    }
}
