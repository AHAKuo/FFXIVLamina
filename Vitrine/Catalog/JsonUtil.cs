using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace Vitrine.Catalog;

/// <summary> Shared JSON settings and atomic file helpers for Vitrine's data files. </summary>
public static class JsonUtil
{
    /// <summary> camelCase properties (dictionary keys are kept verbatim: they are mod directories and option group names), enums as strings, indented, nulls ignored. </summary>
    public static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver  = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy(processDictionaryKeys: false, overrideSpecifiedNames: true) },
        Formatting        = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        Converters        = { new StringEnumConverter() },
    };

    /// <summary> Serializes with <see cref="Settings"/>. </summary>
    public static string Serialize<T>(T value)
        => JsonConvert.SerializeObject(value, Settings);

    /// <summary> Deserializes with <see cref="Settings"/>. </summary>
    public static T? Deserialize<T>(string json)
        => JsonConvert.DeserializeObject<T>(json, Settings);

    /// <summary> Writes text to a temp file next to the target and moves it over the target, so a crash never leaves half a file. </summary>
    public static void WriteAtomic(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary> Serializes and writes atomically. </summary>
    public static void WriteAtomic<T>(string path, T value)
        => WriteAtomic(path, Serialize(value));

    /// <summary> Reads and deserializes a file, returning null when the file is missing or unreadable. The caller logs. </summary>
    public static T? ReadOrNull<T>(string path, out Exception? error)
        where T : class
    {
        error = null;
        try
        {
            if (!File.Exists(path))
                return null;
            return Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            error = ex;
            return null;
        }
    }
}
