using Lamina.Ipc;
using Lamina.Models;
using Newtonsoft.Json.Linq;

namespace Lamina.Runtime.Compose;

/// <summary>
/// Turns one <see cref="Layer"/> into a <see cref="DesignDraft"/> contribution. Missing references are skipped with a reason
/// so the compositor can report them (broken link warnings in the UI, /lam why).
/// </summary>
public sealed class LayerResolver
{
    private readonly GlamourerDesignCache _cache;
    private readonly Func<IVitrineBridge?> _vitrine;
    private readonly Func<bool> _applyGlamourerModAssociations;
    private readonly Action<string>? _log;

    /// <param name="cache"> Glamourer design cache. </param>
    /// <param name="vitrine"> Vitrine bridge accessor; may return null when no bridge exists. Designs come from the separate Vitrine plugin over IPC. </param>
    /// <param name="applyGlamourerModAssociations"> Reads LaminaConfig.ApplyGlamourerModAssociations. </param>
    /// <param name="log"> Optional debug logger for non-fatal oddities (e.g. a Moodle both added and removed). </param>
    public LayerResolver(GlamourerDesignCache cache, Func<IVitrineBridge?> vitrine, Func<bool> applyGlamourerModAssociations, Action<string>? log = null)
    {
        _cache                         = cache;
        _vitrine                       = vitrine;
        _applyGlamourerModAssociations = applyGlamourerModAssociations;
        _log                           = log;
    }

    /// <summary> The design cache this resolver reads from. </summary>
    public GlamourerDesignCache Cache
        => _cache;

    /// <summary> Resolves a layer. Returns null with a reason when the layer contributes nothing. </summary>
    public DesignDraft? Resolve(Layer layer, Provenance source, out string? skipReason)
    {
        skipReason = null;
        if (!layer.Enabled)
        {
            skipReason = "disabled";
            return null;
        }

        switch (layer)
        {
            case GlamourerLayer g:
                return ResolveGlamourer(g, source, out skipReason);
            case VitrineDesignLayer v:
                return ResolveVitrine(v, source, out skipReason);
            case PenumbraAdjustLayer p:
                return ResolvePenumbraAdjust(p, source, out skipReason);
            case EquipmentLayer e:
                return ResolveEquipment(e, source, out skipReason);
            case AppearanceLayer a:
                return ResolveAppearance(a, source, out skipReason);
            case HonorificLayer h:
                return ResolveHonorific(h, source);
            case MoodlesLayer m:
                return ResolveMoodles(m, source);
            default:
                skipReason = $"unsupported layer kind '{layer.Kind}'";
                return null;
        }
    }

    private DesignDraft? ResolveGlamourer(GlamourerLayer layer, Provenance source, out string? skipReason)
    {
        skipReason = null;
        if (layer.DesignId == Guid.Empty)
        {
            skipReason = "no design selected";
            return null;
        }

        var draft = _cache.Get(layer.DesignId, _applyGlamourerModAssociations(), source);
        if (draft is null)
            skipReason = $"Glamourer design {layer.DesignId} not found";
        return draft;
    }

    private DesignDraft? ResolveVitrine(VitrineDesignLayer layer, Provenance source, out string? skipReason)
    {
        skipReason = null;
        var vitrine = _vitrine();
        if (vitrine is null || !vitrine.Available)
        {
            skipReason = "Vitrine is not installed";
            return null;
        }

        var design = vitrine.GetDesign(layer.VitrineDesignId);
        if (design is null)
        {
            skipReason = $"Vitrine design {layer.VitrineDesignId} not found";
            return null;
        }

        var draft = new DesignDraft();
        foreach (var mod in design.Mods)
        {
            if (string.IsNullOrWhiteSpace(mod.ModDirectory))
                continue;

            var settings = mod.Settings?.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.Ordinal);
            if (layer.SettingsOverride is { Count: > 0 } overrides)
            {
                settings ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var (group, options) in overrides)
                    settings[group] = new List<string>(options);
            }

            // A Vitrine design states the enabled flag and priority; its groups are set when the design carries settings,
            // otherwise they are inherited (the design "uses Penumbra's defaults for the mod").
            draft.AddMod(ModContribution.From(new ModState
            {
                ModDirectory = mod.ModDirectory,
                ModName      = mod.ModName,
                State        = mod.Enabled ? ModToggle.Enable : ModToggle.Disable,
                Priority     = mod.Priority,
                Settings     = settings,
            }, source));
        }

        return draft;
    }

    private static DesignDraft? ResolvePenumbraAdjust(PenumbraAdjustLayer layer, Provenance source, out string? skipReason)
    {
        skipReason = null;
        var draft = new DesignDraft();
        foreach (var mod in layer.Mods)
        {
            if (string.IsNullOrWhiteSpace(mod.ModDirectory) || mod.IsEmpty)
                continue;
            draft.AddMod(ModContribution.From(mod, source));
        }

        if (draft.Mods.Count == 0)
        {
            skipReason = layer.Mods.Count == 0 ? "no mods" : "no mod entry sets anything";
            return null;
        }

        return draft;
    }

    private static readonly EquipSlot[] CrestSlots = { EquipSlot.Head, EquipSlot.Body, EquipSlot.OffHand };

    private static DesignDraft? ResolveEquipment(EquipmentLayer layer, Provenance source, out string? skipReason)
    {
        skipReason = null;
        var draft = new DesignDraft();
        foreach (var (slot, edit) in layer.Slots)
        {
            if (edit.ItemId is { } item)
                draft.Items[slot] = new Field<EquipItem>(new EquipItem(item), source);
            if (edit.ApplyStains)
                draft.Stains[slot] = new Field<Stains>(new Stains(edit.Stain1, edit.Stain2), source);
            if (edit.Crest is { } crest && CrestSlots.Contains(slot))
                draft.Crests[slot] = new Field<bool>(crest, source);
        }
        foreach (var (slot, id) in layer.Bonus)
            draft.Bonus[slot] = new Field<ulong>(id, source);

        foreach (var (index, value) in layer.Meta)
            if (index != MetaIndex.Wetness)
                draft.Meta[index] = new Field<bool>(value, source);

        if (draft.IsEmpty)
        {
            skipReason = "nothing set";
            return null;
        }

        return draft;
    }

    private static DesignDraft? ResolveAppearance(AppearanceLayer layer, Provenance source, out string? skipReason)
    {
        skipReason = null;
        var draft = new DesignDraft();
        foreach (var (index, value) in layer.Customize)
        {
            // Body types other than the player one (1) are NPC models and crash the game; the editor no longer offers
            // the field and values saved by older builds are ignored.
            if (index == CustomizeIndex.BodyType)
                continue;
            draft.Customize[index] = new Field<int>(value, source);
        }
        if (layer.Wetness is { } wet)
            draft.Meta[MetaIndex.Wetness] = new Field<bool>(wet, source);
        foreach (var (key, value) in layer.Parameters)
        {
            if (AdvancedParameters.KindOf(key) is not { } kind)
                continue;
            draft.Parameters[key] = new Field<JObject>(ToParameterJson(kind, value), source);
        }

        if (draft.IsEmpty)
        {
            skipReason = "nothing set";
            return null;
        }

        return draft;
    }

    /// <summary> Glamourer's "Parameters" value object for one advanced parameter (without the Apply flag). </summary>
    public static JObject ToParameterJson(AdvancedParameterKind kind, AdvancedParameter value)
        => kind switch
        {
            AdvancedParameterKind.Rgb        => new JObject { ["Red"] = value.Color.X, ["Green"] = value.Color.Y, ["Blue"] = value.Color.Z },
            AdvancedParameterKind.Rgba       => new JObject { ["Red"] = value.Color.X, ["Green"] = value.Color.Y, ["Blue"] = value.Color.Z, ["Alpha"] = value.Alpha },
            AdvancedParameterKind.Percentage => new JObject { ["Percentage"] = value.Scalar },
            _                                => new JObject { ["Value"] = value.Scalar },
        };

    private static DesignDraft ResolveHonorific(HonorificLayer layer, Provenance source)
    {
        var title = layer.Clear
            ? HonorificTitle.Cleared
            : new HonorificTitle(false, layer.Title ?? string.Empty, layer.IsPrefix, layer.Color, layer.Glow);
        return new DesignDraft { Honorific = new Field<HonorificTitle>(title, source) };
    }

    private DesignDraft ResolveMoodles(MoodlesLayer layer, Provenance source)
    {
        var draft = new DesignDraft();
        foreach (var id in layer.Add)
            draft.Moodles[id] = new Field<MoodleOp>(MoodleOp.Add, source);
        foreach (var id in layer.AddPresets)
            draft.Moodles[id] = new Field<MoodleOp>(MoodleOp.AddPreset, source);
        foreach (var id in layer.Remove)
        {
            if (draft.Moodles.ContainsKey(id))
                _log?.Invoke($"Moodles layer {layer.Id}: {id} is both added and removed; remove wins.");
            draft.Moodles[id] = new Field<MoodleOp>(MoodleOp.Remove, source);
        }

        return draft;
    }
}
