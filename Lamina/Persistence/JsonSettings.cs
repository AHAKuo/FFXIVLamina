using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Lamina.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Lamina.Persistence;

/// <summary>
/// Single source of truth for how Lamina writes JSON: camelCase names, enums as strings, indented, nulls omitted,
/// polymorphic conditions and layers by "kind". Every file Lamina writes or reads goes through <see cref="Create"/>.
/// </summary>
public static class JsonSettings
{
    /// <summary> Builds a fresh settings object. Settings are cheap; callers may cache the serializer. </summary>
    public static JsonSerializerSettings Create()
        => new()
        {
            ContractResolver       = new LaminaContractResolver(),
            Formatting             = Formatting.Indented,
            NullValueHandling      = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            MissingMemberHandling  = MissingMemberHandling.Ignore,
            DateParseHandling      = DateParseHandling.None,
            Converters =
            {
                new StringEnumConverter(),
                new DateOnlyConverter(),
                new Vector3Converter(),
                new ConditionStackConverter(),
                new ConditionNodeConverter(),
                new LayerConverter(),
            },
        };

    /// <summary> A serializer built from <see cref="Create"/>. </summary>
    public static JsonSerializer CreateSerializer()
        => JsonSerializer.Create(Create());

    /// <summary> Serializes with Lamina settings. </summary>
    public static string Serialize(object value)
        => JsonConvert.SerializeObject(value, Create());

    /// <summary> Deserializes with Lamina settings. Throws on malformed input. </summary>
    public static T Deserialize<T>(string json)
        => JsonConvert.DeserializeObject<T>(json, Create())
        ?? throw new JsonSerializationException($"JSON did not produce a {typeof(T).Name}.");

    /// <summary> Converts a parsed token with Lamina settings. </summary>
    public static T ToObject<T>(JToken token)
        => token.ToObject<T>(CreateSerializer())
        ?? throw new JsonSerializationException($"JSON did not produce a {typeof(T).Name}.");

    /// <summary> Converts an object to a token with Lamina settings. </summary>
    public static JToken FromObject(object value)
        => JToken.FromObject(value, CreateSerializer());
}

/// <summary>
/// camelCase property names, dictionary keys untouched (mod option group names must survive as written),
/// and get-only scalar properties (e.g. computed booleans, the "Kind" discriminator) are never written.
/// </summary>
public sealed class LaminaContractResolver : DefaultContractResolver
{
    /// <summary> Creates the resolver. </summary>
    public LaminaContractResolver()
    {
        NamingStrategy = new CamelCaseNamingStrategy
        {
            ProcessDictionaryKeys     = false,
            ProcessExtensionDataNames = false,
            OverrideSpecifiedNames    = true,
        };
    }

    /// <inheritdoc />
    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        var property = base.CreateProperty(member, memberSerialization);
        if (!property.Writable && member is PropertyInfo info && !IsCollection(info.PropertyType))
        {
            property.Ignored = true;
            property.ShouldSerialize = _ => false;
        }

        return property;
    }

    private static bool IsCollection(Type type)
        => type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);
}

/// <summary> DateOnly as "yyyy-MM-dd". </summary>
public sealed class DateOnlyConverter : JsonConverter
{
    private const string Format = "yyyy-MM-dd";

    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
        => objectType == typeof(DateOnly) || objectType == typeof(DateOnly?);

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is DateOnly date)
            writer.WriteValue(date.ToString(Format, CultureInfo.InvariantCulture));
        else
            writer.WriteNull();
    }

    /// <inheritdoc />
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return objectType == typeof(DateOnly?) ? null : default(DateOnly);
        if (reader.TokenType == JsonToken.Date && reader.Value is DateTime dt)
            return DateOnly.FromDateTime(dt);

        var text = reader.Value?.ToString() ?? string.Empty;
        return DateOnly.ParseExact(text, Format, CultureInfo.InvariantCulture);
    }
}

/// <summary> Vector3 as { "x": 0, "y": 0, "z": 0 }. </summary>
public sealed class Vector3Converter : JsonConverter
{
    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
        => objectType == typeof(Vector3) || objectType == typeof(Vector3?);

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not Vector3 v)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("x");
        writer.WriteValue(v.X);
        writer.WritePropertyName("y");
        writer.WriteValue(v.Y);
        writer.WritePropertyName("z");
        writer.WriteValue(v.Z);
        writer.WriteEndObject();
    }

    /// <inheritdoc />
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return objectType == typeof(Vector3?) ? null : default(Vector3);

        var token = JToken.Load(reader);
        if (token is JArray array && array.Count >= 3)
            return new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());

        if (token is JObject obj)
        {
            float Get(string lower, string upper)
                => (obj[lower] ?? obj[upper])?.Value<float>() ?? 0f;

            return new Vector3(Get("x", "X"), Get("y", "Y"), Get("z", "Z"));
        }

        throw new JsonSerializationException($"Cannot read a Vector3 from {token.Type}.");
    }
}

/// <summary> A ConditionStack is written as its root node (or null). An absent / null root reads as an empty, always-true stack. </summary>
public sealed class ConditionStackConverter : JsonConverter
{
    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
        => objectType == typeof(ConditionStack);

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not ConditionStack { Root: { } root })
        {
            writer.WriteNull();
            return;
        }

        serializer.Serialize(writer, root, typeof(ConditionNode));
    }

    /// <inheritdoc />
    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return new ConditionStack();

        var root = serializer.Deserialize<ConditionNode>(reader);
        return new ConditionStack { Root = root };
    }
}

/// <summary> Shared machinery for the "kind" based polymorphic converters. </summary>
public abstract class KindConverter<TBase> : JsonConverter where TBase : class
{
    /// <summary> Name of the discriminator property. </summary>
    public const string KindProperty = "kind";

    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
        => typeof(TBase).IsAssignableFrom(objectType);

    /// <summary> Resolves the discriminator for an instance. </summary>
    protected abstract string KindOf(TBase value);

    /// <summary> When the instance is an "unknown" placeholder, returns its raw JSON; otherwise null. </summary>
    protected abstract string? RawJsonOf(TBase value);

    /// <summary> Creates the instance for a parsed object, or null when the kind is not known. </summary>
    protected abstract TBase? Instantiate(JObject obj, string? kind);

    /// <summary> Creates the placeholder for a kind this build does not know. </summary>
    protected abstract TBase CreateUnknown(JObject obj, string kind);

    /// <summary> Hook for compatibility fix-ups after an instance was populated. </summary>
    protected virtual void AfterPopulate(TBase instance, JObject obj)
    {
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not TBase typed)
        {
            writer.WriteNull();
            return;
        }

        if (RawJsonOf(typed) is { } raw)
        {
            JToken.Parse(raw).WriteTo(writer);
            return;
        }

        if (serializer.ContractResolver.ResolveContract(typed.GetType()) is not JsonObjectContract contract)
            throw new JsonSerializationException($"{typed.GetType().Name} is not an object contract.");

        writer.WriteStartObject();
        writer.WritePropertyName(KindProperty);
        writer.WriteValue(KindOf(typed));
        foreach (var property in contract.Properties)
        {
            if (property.Ignored || !property.Readable || property.ValueProvider is null)
                continue;
            if (string.Equals(property.PropertyName, KindProperty, StringComparison.OrdinalIgnoreCase))
                continue;
            if (property.ShouldSerialize is { } shouldSerialize && !shouldSerialize(typed))
                continue;

            var propertyValue = property.ValueProvider.GetValue(typed);
            var nullHandling  = property.NullValueHandling ?? serializer.NullValueHandling;
            if (propertyValue is null && nullHandling == NullValueHandling.Ignore)
                continue;

            writer.WritePropertyName(property.PropertyName ?? property.UnderlyingName ?? string.Empty);
            if (propertyValue is null)
                writer.WriteNull();
            else
                serializer.Serialize(writer, propertyValue, property.PropertyType ?? propertyValue.GetType());
        }

        writer.WriteEndObject();
    }

    /// <inheritdoc />
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        var obj  = JObject.Load(reader);
        var kind = ReadKind(obj);
        var instance = Instantiate(obj, kind);
        if (instance is null)
            return CreateUnknown(obj, kind ?? "Unknown");

        using var objReader = obj.CreateReader();
        serializer.Populate(objReader, instance);
        AfterPopulate(instance, obj);
        return instance;
    }

    /// <summary> Reads the "kind" discriminator, case-insensitive on the property name. </summary>
    protected static string? ReadKind(JObject obj)
    {
        foreach (var property in obj.Properties())
            if (string.Equals(property.Name, KindProperty, StringComparison.OrdinalIgnoreCase))
                return property.Value.Type == JTokenType.String ? property.Value.Value<string>() : null;
        return null;
    }

    /// <summary> Case-insensitive property lookup. </summary>
    protected static JToken? Find(JObject obj, string name)
    {
        foreach (var property in obj.Properties())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }
}

/// <summary>
/// Conditions and groups by "kind". Groups use "Group" (and are also recognized without a kind when they carry
/// "op" or "children", matching the TDD example). Unknown kinds become <see cref="UnknownCondition"/> and are written back verbatim.
/// </summary>
public sealed class ConditionNodeConverter : KindConverter<ConditionNode>
{
    /// <inheritdoc />
    protected override string KindOf(ConditionNode value)
        => value switch
        {
            ConditionGroup => ConditionGroup.KindName,
            Condition c    => c.Kind,
            _              => "Unknown",
        };

    /// <inheritdoc />
    protected override string? RawJsonOf(ConditionNode value)
        => value is UnknownCondition unknown ? unknown.RawJson : null;

    /// <inheritdoc />
    protected override ConditionNode? Instantiate(JObject obj, string? kind)
    {
        if (string.Equals(kind, ConditionGroup.KindName, StringComparison.OrdinalIgnoreCase))
            return new ConditionGroup();
        if (kind is null && (Find(obj, "op") is not null || Find(obj, "children") is not null))
            return new ConditionGroup();
        if (kind is null)
            return null;

        foreach (var (k, _, create) in ConditionKinds.All)
            if (string.Equals(k, kind, StringComparison.OrdinalIgnoreCase))
                return create();
        return null;
    }

    /// <inheritdoc />
    protected override ConditionNode CreateUnknown(JObject obj, string kind)
        => new UnknownCondition { UnknownKind = kind, RawJson = obj.ToString(Formatting.None) };

    /// <inheritdoc />
    protected override void AfterPopulate(ConditionNode instance, JObject obj)
    {
        // Compatibility: the TDD example writes "from": "18:00", "to": "06:00" on Time conditions.
        if (instance is not TimeCondition time)
            return;
        if (Find(obj, "fromMinutes") is null && Find(obj, "from") is { Type: JTokenType.String } from && TryParseClock(from.Value<string>(), out var fromMinutes))
            time.FromMinutes = fromMinutes;
        if (Find(obj, "toMinutes") is null && Find(obj, "to") is { Type: JTokenType.String } to && TryParseClock(to.Value<string>(), out var toMinutes))
            time.ToMinutes = toMinutes;
    }

    private static bool TryParseClock(string? text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Split(':');
        if (parts.Length != 2
         || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
         || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
            return false;
        if (h is < 0 or > 24 || m is < 0 or > 59)
            return false;
        minutes = (h * 60 + m) % 1440;
        return true;
    }
}

/// <summary> Layers by "kind". Unknown kinds become <see cref="UnknownLayer"/> and are written back verbatim. </summary>
public sealed class LayerConverter : KindConverter<Layer>
{
    /// <inheritdoc />
    protected override string KindOf(Layer value)
        => value.Kind;

    /// <inheritdoc />
    protected override string? RawJsonOf(Layer value)
        => value is UnknownLayer unknown ? unknown.RawJson : null;

    /// <inheritdoc />
    protected override Layer? Instantiate(JObject obj, string? kind)
    {
        if (kind is null)
            return null;
        foreach (var (k, _, create) in LayerKinds.All)
            if (string.Equals(k, kind, StringComparison.OrdinalIgnoreCase))
                return create();
        return null;
    }

    /// <inheritdoc />
    protected override Layer CreateUnknown(JObject obj, string kind)
        => new UnknownLayer { UnknownKind = kind, RawJson = obj.ToString(Formatting.None) };
}
