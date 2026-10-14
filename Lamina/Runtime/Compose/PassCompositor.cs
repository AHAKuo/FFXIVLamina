using Lamina.Models;

namespace Lamina.Runtime.Compose;

/// <summary> Result of <see cref="PassCompositor.Preview"/>. </summary>
/// <param name="Full"> The design the profile produces right now (timers ignored). </param>
/// <param name="Report"> Which groups / passes matched for <paramref name="Full"/>. </param>
/// <param name="Focus"> What the focused pass or group alone contributes, regardless of whether it matches. Null without a focus. </param>
/// <param name="FocusMatches"> Whether the focused pass / group's own conditions are true right now. </param>
/// <param name="FocusSkipped"> Layers of the focus that were skipped, with reasons. </param>
public sealed record PreviewResult(
    VirtualDesign Full,
    EvaluationReport Report,
    VirtualDesign? Focus,
    bool FocusMatches,
    IReadOnlyList<(Guid LayerId, string Reason)> FocusSkipped);

/// <summary>
/// Walks a profile against a snapshot and merges everything the matching passes want into one <see cref="VirtualDesign"/>.
/// Implements TDD 5.9: first true normal group wins, then every true final group by priority; passes and layers are walked in
/// the evaluation direction and later merges override earlier ones (unset = passthrough).
/// Macro Override masks are applied per layer, to Glamourer and Vitrine layers only, before the layer merges.
/// </summary>
public sealed class PassCompositor
{
    /// <summary> Composes the final design for one profile. </summary>
    public (VirtualDesign Design, EvaluationReport Report) Compose(Profile profile, GameStateSnapshot snapshot,
        IConditionEvaluator evaluator, EvaluationDirection direction, LayerResolver resolver)
        => ComposeCore(profile, snapshot, direction, resolver, stack => evaluator.Evaluate(stack, snapshot));

    /// <summary> Smashes one pass's layers into its FinalLayer draft (TDD 5.3), with masks applied per layer. </summary>
    /// <param name="pass"> The pass. </param>
    /// <param name="basis"> Provenance carrying the group / pass ids; the layer id and note are filled per layer. </param>
    /// <param name="groupName"> Group name for the provenance note. </param>
    /// <param name="resolver"> Layer resolver. </param>
    /// <param name="direction"> Evaluation direction. </param>
    /// <param name="skipped"> Layers that contributed nothing, with reasons. </param>
    public DesignDraft BuildPassLayer(Pass pass, Provenance basis, string groupName, LayerResolver resolver, EvaluationDirection direction,
        List<(Guid LayerId, string Reason)> skipped)
    {
        var final = new DesignDraft();
        foreach (var layer in Ordered(pass.Layers, direction))
        {
            var source = new Provenance(basis.GroupId, pass.Id, layer.Id, $"{groupName} / {pass.Name} / {layer.Describe()}");
            var draft  = resolver.Resolve(layer, source, out var reason);
            if (draft is null)
            {
                skipped.Add((layer.Id, reason ?? "skipped"));
                continue;
            }

            if (pass.MacroOverride is { } mask && layer is GlamourerLayer or VitrineDesignLayer)
                draft = draft.Filter(mask);

            final.MergeOnTop(draft);
        }

        return final;
    }

    /// <summary>
    /// Preview for the UI and /lam why: the full design with timers ignored, plus the focused pass or group's own contribution.
    /// A focused group is previewed as if it were active (its own condition stack is reported through FocusMatches).
    /// </summary>
    public PreviewResult Preview(Profile profile, GameStateSnapshot snapshot, IConditionEvaluator evaluator,
        EvaluationDirection direction, LayerResolver resolver, Guid? focusGroupId, Guid? focusPassId)
    {
        var (full, report) = ComposeCore(profile, snapshot, direction, resolver, stack => evaluator.EvaluateInstant(stack, snapshot));

        VirtualDesign? focus       = null;
        var            focusMatch  = false;
        var            focusSkipped = new List<(Guid, string)>();

        var group = focusGroupId is { } gid ? profile.PassGroups.FirstOrDefault(g => g.Id == gid) : null;
        var pass  = focusPassId is { } pid
            ? (group?.Passes ?? profile.PassGroups.SelectMany(g => g.Passes)).FirstOrDefault(p => p.Id == pid)
            : null;
        if (pass is not null && group is null)
            group = profile.PassGroups.FirstOrDefault(g => g.Passes.Contains(pass));

        if (pass is not null && group is not null)
        {
            var draft = BuildPassLayer(pass, new Provenance(group.Id, pass.Id, null, string.Empty), group.Name, resolver, direction, focusSkipped);
            focus      = draft.Freeze(profile.Id, group.Id, Array.Empty<Guid>(), new[] { pass.Id });
            focusMatch = pass.Enabled && evaluator.EvaluateInstant(pass.Conditions, snapshot)
                      && group.Enabled && evaluator.EvaluateInstant(group.Conditions, snapshot);
        }
        else if (group is not null)
        {
            var draft   = new DesignDraft();
            var matched = new List<Guid>();
            MergeGroup(group, draft, matched, focusSkipped, resolver, direction, stack => evaluator.EvaluateInstant(stack, snapshot));
            focus      = draft.Freeze(profile.Id, group.Id, Array.Empty<Guid>(), matched);
            focusMatch = group.Enabled && evaluator.EvaluateInstant(group.Conditions, snapshot);
        }

        return new PreviewResult(full, report, focus, focusMatch, focusSkipped);
    }

    private (VirtualDesign, EvaluationReport) ComposeCore(Profile profile, GameStateSnapshot snapshot, EvaluationDirection direction,
        LayerResolver resolver, Func<ConditionStack, bool> isTrue)
    {
        var draft   = new DesignDraft();
        var matched = new List<Guid>();
        var skipped = new List<(Guid, string)>();

        // 1. Normal groups: first true wins, then stop.
        PassGroup? active = null;
        foreach (var group in profile.PassGroups)
        {
            if (!group.Enabled || group.IsFinal)
                continue;
            if (isTrue(group.Conditions))
            {
                active = group;
                break;
            }
        }

        if (active is not null)
            MergeGroup(active, draft, matched, skipped, resolver, direction, isTrue);

        // 2. Final groups: every true one, by priority (lower first, higher wins).
        var finals = new List<Guid>();
        foreach (var group in profile.PassGroups.Where(g => g.Enabled && g.IsFinal).OrderBy(g => g.Priority))
        {
            if (!isTrue(group.Conditions))
                continue;
            finals.Add(group.Id);
            MergeGroup(group, draft, matched, skipped, resolver, direction, isTrue);
        }

        var design = draft.Freeze(profile.Id, active?.Id, finals, matched);
        var report = new EvaluationReport(profile.Id, active?.Id, finals, matched, skipped);
        return (design, report);
    }

    private void MergeGroup(PassGroup group, DesignDraft draft, List<Guid> matched, List<(Guid, string)> skipped,
        LayerResolver resolver, EvaluationDirection direction, Func<ConditionStack, bool> isTrue)
    {
        foreach (var pass in Ordered(group.Passes, direction))
        {
            if (!pass.Enabled || !isTrue(pass.Conditions))
                continue;
            matched.Add(pass.Id);
            var final = BuildPassLayer(pass, new Provenance(group.Id, pass.Id, null, string.Empty), group.Name, resolver, direction, skipped);
            draft.MergeOnTop(final);
        }
    }

    /// <summary> List order for TopToBottom (later rows merge on top and win); reversed for BottomToTop. </summary>
    public static IEnumerable<T> Ordered<T>(IReadOnlyList<T> items, EvaluationDirection direction)
        => direction == EvaluationDirection.TopToBottom ? items : items.Reverse();
}
