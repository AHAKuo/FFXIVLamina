using System.Text;
using Lamina.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lamina.Persistence;

/// <summary> What an exported / imported blob contains. </summary>
public enum TransferKind
{
    Profile,
    PassGroup,
    Pass,
}

/// <summary> Result of an import: exactly one of <see cref="Profile"/>, <see cref="Group"/>, <see cref="Pass"/> is set. </summary>
public sealed class ImportResult
{
    internal ImportResult(TransferKind kind, object item, IReadOnlyList<string> warnings)
    {
        Kind     = kind;
        Item     = item;
        Warnings = warnings;
    }

    /// <summary> Which kind of object was imported. </summary>
    public TransferKind Kind { get; }

    /// <summary> The imported object, with fresh ids. </summary>
    public object Item { get; }

    /// <summary> Set when <see cref="Kind"/> is Profile. </summary>
    public Profile? Profile
        => Item as Profile;

    /// <summary> Set when <see cref="Kind"/> is PassGroup. </summary>
    public PassGroup? Group
        => Item as PassGroup;

    /// <summary> Set when <see cref="Kind"/> is Pass. </summary>
    public Pass? Pass
        => Item as Pass;

    /// <summary> Missing references and unsupported kinds found in the import. The object still imported. </summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary> Optional lookups used to flag broken references on import. A null delegate skips that check. </summary>
/// <param name="GlamourerDesignExists"> Glamourer design Guid → exists. </param>
/// <param name="VitrineDesignExists"> Vitrine design Guid → exists. </param>
/// <param name="ModExists"> Penumbra mod directory → exists. </param>
public sealed record ReferenceResolvers(
    Func<Guid, bool>? GlamourerDesignExists = null,
    Func<Guid, bool>? VitrineDesignExists = null,
    Func<string, bool>? ModExists = null);

/// <summary>
/// Export a profile, group or pass as pretty JSON or as a <c>LAMINA1:</c> prefixed base64 clipboard string, and import either.
/// Imports get fresh ids and targets reset to the local player (TDD 10.4).
/// </summary>
public static class ProfileTransfer
{
    /// <summary> Prefix of the clipboard format. </summary>
    public const string ClipboardPrefix = "LAMINA1:";

    // ---- export ----

    /// <summary> Pretty JSON, same layout as the profile file. </summary>
    public static string ExportJson(Profile profile)
        => JsonSettings.Serialize(profile);

    /// <summary> Pretty JSON of one group. </summary>
    public static string ExportJson(PassGroup group)
        => JsonSettings.Serialize(group);

    /// <summary> Pretty JSON of one pass. </summary>
    public static string ExportJson(Pass pass)
        => JsonSettings.Serialize(pass);

    /// <summary> Clipboard string: <c>LAMINA1:</c> + base64 of the UTF-8 JSON. </summary>
    public static string ExportClipboard(Profile profile)
        => ToClipboard(ExportJson(profile));

    /// <summary> Clipboard string of one group. </summary>
    public static string ExportClipboard(PassGroup group)
        => ToClipboard(ExportJson(group));

    /// <summary> Clipboard string of one pass. </summary>
    public static string ExportClipboard(Pass pass)
        => ToClipboard(ExportJson(pass));

    /// <summary> Wraps JSON into the clipboard format. </summary>
    public static string ToClipboard(string json)
        => ClipboardPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    // ---- import ----

    /// <summary>
    /// Imports JSON or a clipboard string. The kind is detected from the content: a profile has "passGroups",
    /// a group has "passes", a pass has "layers". Throws <see cref="FormatException"/> on unrecognizable input.
    /// </summary>
    public static ImportResult Import(string text, ReferenceResolvers? resolvers = null)
    {
        var json = Unwrap(text);
        JObject obj;
        try
        {
            obj = JObject.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not valid Lamina JSON: {ex.Message}", ex);
        }

        var kind = DetectKind(obj) ?? throw new FormatException("JSON is not a Lamina profile, pass group or pass.");
        var warnings = new List<string>();
        resolvers ??= new ReferenceResolvers();

        switch (kind)
        {
            case TransferKind.Profile:
            {
                Migrations.Upgrade(obj, FileKind.Profile);
                var profile = Cloner.CloneWithFreshIds(JsonSettings.ToObject<Profile>(obj));
                profile.Targets = new List<Target> { Target.LocalPlayer() };
                profile.Enabled = false;
                foreach (var group in profile.PassGroups)
                    Check(group, resolvers, warnings);
                return new ImportResult(kind, profile, warnings);
            }
            case TransferKind.PassGroup:
            {
                var group = Cloner.CloneWithFreshIds(JsonSettings.ToObject<PassGroup>(obj));
                Check(group, resolvers, warnings);
                return new ImportResult(kind, group, warnings);
            }
            default:
            {
                var pass = Cloner.CloneWithFreshIds(JsonSettings.ToObject<Pass>(obj));
                Check(pass, resolvers, warnings);
                return new ImportResult(kind, pass, warnings);
            }
        }
    }

    /// <summary> True when the text looks like something <see cref="Import"/> can take. Cheap check for paste buttons. </summary>
    public static bool LooksImportable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        return trimmed.StartsWith(ClipboardPrefix, StringComparison.Ordinal) || trimmed.StartsWith('{');
    }

    /// <summary> Reference warnings for an existing group (used by the UI's broken link badges). </summary>
    public static IReadOnlyList<string> CheckReferences(PassGroup group, ReferenceResolvers resolvers)
    {
        var warnings = new List<string>();
        Check(group, resolvers, warnings);
        return warnings;
    }

    /// <summary> Reference warnings for an existing pass. </summary>
    public static IReadOnlyList<string> CheckReferences(Pass pass, ReferenceResolvers resolvers)
    {
        var warnings = new List<string>();
        Check(pass, resolvers, warnings);
        return warnings;
    }

    private static string Unwrap(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
            return trimmed;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(trimmed[ClipboardPrefix.Length..].Trim()));
        }
        catch (FormatException ex)
        {
            throw new FormatException("Clipboard string is not valid base64.", ex);
        }
    }

    private static TransferKind? DetectKind(JObject obj)
    {
        if (Has(obj, "passGroups") || Has(obj, "targets"))
            return TransferKind.Profile;
        if (Has(obj, "passes") || Has(obj, "isFinal"))
            return TransferKind.PassGroup;
        if (Has(obj, "layers") || Has(obj, "macroOverride"))
            return TransferKind.Pass;
        return null;
    }

    private static bool Has(JObject obj, string name)
        => obj.Properties().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void Check(PassGroup group, ReferenceResolvers resolvers, List<string> warnings)
    {
        CheckConditions(group.Conditions, $"group '{group.Name}'", warnings);
        foreach (var pass in group.Passes)
            Check(pass, resolvers, warnings);
    }

    private static void Check(Pass pass, ReferenceResolvers resolvers, List<string> warnings)
    {
        CheckConditions(pass.Conditions, $"pass '{pass.Name}'", warnings);
        foreach (var layer in pass.Layers)
        {
            switch (layer)
            {
                case GlamourerLayer g when resolvers.GlamourerDesignExists is { } exists && !exists(g.DesignId):
                    warnings.Add($"Pass '{pass.Name}': Glamourer design {g.DesignId} was not found.");
                    break;
                case VitrineDesignLayer v when resolvers.VitrineDesignExists is { } exists && !exists(v.VitrineDesignId):
                    warnings.Add($"Pass '{pass.Name}': Vitrine design {v.VitrineDesignId} was not found.");
                    break;
                case PenumbraAdjustLayer p when resolvers.ModExists is { } exists:
                    foreach (var mod in p.Mods.Where(m => !exists(m.ModDirectory)))
                        warnings.Add($"Pass '{pass.Name}': mod '{mod.ModName ?? mod.ModDirectory}' ({mod.ModDirectory}) was not found.");
                    break;
                case UnknownLayer u:
                    warnings.Add($"Pass '{pass.Name}': layer kind '{u.UnknownKind}' is not supported by this version.");
                    break;
            }
        }
    }

    private static void CheckConditions(ConditionStack stack, string owner, List<string> warnings)
    {
        foreach (var unknown in stack.Leaves().OfType<UnknownCondition>())
            warnings.Add($"{owner}: condition kind '{unknown.UnknownKind}' is not supported by this version.");
    }
}
