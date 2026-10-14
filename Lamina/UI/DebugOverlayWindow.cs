using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Lamina.Models;

namespace Lamina.UI;

/// <summary> Small always-on-top "Lamina Debug" window: active group, matched passes, last apply, snapshot basics. </summary>
public sealed class DebugOverlayWindow : Window
{
    private readonly LaminaServices _s;

    public DebugOverlayWindow(LaminaServices services)
        : base("Lamina Debug###LaminaDebugOverlay", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        _s = services;
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(260, 120),
            MaximumSize = new Vector2(600, 800),
        };
    }

    public override void OnClose()
    {
        if (!_s.Profiles.Config.DebugOverlay)
            return;
        _s.Profiles.Config.DebugOverlay = false;
        _s.Profiles.MarkConfigDirty();
    }

    public override void Draw()
    {
        var engine = _s.Engine;
        var s      = engine.Snapshot;
        var local  = _s.Profiles.ActiveProfileFor(Target.LocalPlayer());

        ImGui.TextColored(engine.Enabled ? Widgets.Good : Widgets.Bad, engine.Enabled ? "Lamina ON" : "Lamina OFF");
        ImGui.SameLine();
        Widgets.Hint(local is null ? "no active profile" : $"profile: {local.Name}");
        ImGui.SameLine();
        Widgets.Hint($"collection: {Widgets.Humanize(engine.CollectionStatus.ToString())}");

        ImGui.Separator();
        var design = engine.LastDesign;
        if (design is null)
            Widgets.Hint("No design evaluated yet.");
        else
        {
            ImGui.TextUnformatted($"Active group: {Name(design.ActiveGroupId)}");
            if (design.ActiveFinalGroupIds.Count > 0)
                ImGui.TextUnformatted($"Final groups: {string.Join(", ", design.ActiveFinalGroupIds.Select(g => Name(g)))}");
            ImGui.TextUnformatted($"Matched passes: {(design.MatchedPassIds.Count == 0 ? "(none)" : string.Join(", ", design.MatchedPassIds.Select(PassName)))}");
            Widgets.Hint($"{design.Items.Count + design.Stains.Count + design.Customize.Count + design.Meta.Count + design.Parameters.Count} glamourer field(s) · {design.Mods.Count} mod(s)"
                       + (design.Honorific is not null ? " · title" : string.Empty) + (design.Moodles.Count > 0 ? $" · {design.Moodles.Count} moodle op(s)" : string.Empty)
                       + $" · hash {design.ContentHash[..8]}");
        }
        if (engine.LastReport is { SkippedLayers.Count: > 0 } report)
            ImGui.TextColored(Widgets.Warn, $"Skipped layers: {report.SkippedLayers.Count}");

        ImGui.Separator();
        if (engine.LastApply is { } apply)
        {
            var age = DateTime.UtcNow - apply.AtUtc;
            ImGui.TextUnformatted($"Last apply {age.TotalSeconds:0}s ago: "
                                + (apply.GlamourerApplied ? "glamourer " : string.Empty)
                                + (apply.ModsApplied ? $"mods({apply.ModsWritten}) " : string.Empty)
                                + (apply.HonorificApplied ? "honorific " : string.Empty)
                                + (apply.MoodlesApplied ? "moodles " : string.Empty)
                                + (apply.RedrawRequested ? (apply.RedrawDeferred ? "redraw-deferred" : "redraw") : "no-redraw"));
            foreach (var p in apply.Problems)
                ImGui.TextColored(Widgets.Warn, "• " + p);
        }
        else
            Widgets.Hint("Nothing applied yet.");
        if (engine.RedrawPending)
            ImGui.TextColored(Widgets.Warn, "Redraw pending (waiting for a safe moment)");

        ImGui.Separator();
        Widgets.Hint($"#{s.Sequence} · {_s.Catalog.TerritoryName(s.TerritoryId)} · {_s.Catalog.WeatherName(s.WeatherId)} · ET {s.EorzeaTime:hh\\:mm}");
        Widgets.Hint($"{_s.Catalog.JobAbbreviation(s.ClassJobId)} {s.Level} · gearset {(s.GearsetId < 0 ? "?" : (s.GearsetId + 1).ToString())} · {_s.Catalog.OnlineStatusName(s.OnlineStatusId)} · HP {s.HpPercent}%");
        var flags = s.Movement.Select(m => m.ToString()).Concat(s.Body.Select(b => b.ToString())).Concat(s.Activities.Select(a => a.ToString())).ToList();
        if (s.Pose is { } pose) flags.Add(pose.ToString());
        if (s.EmoteId != 0) flags.Add($"emote {s.EmoteId}{(s.EmoteLooping ? " (loop)" : string.Empty)}");
        if (s.InDuty) flags.Add($"duty {Widgets.Humanize(s.ContentKind.ToString())}");
        if (s.IsSanctuary) flags.Add("sanctuary");
        if (s.IsIndoors) flags.Add("indoors");
        if (s.Housing is not null) flags.Add("housing");
        Widgets.Hint(flags.Count == 0 ? "idle" : string.Join(" · ", flags));
        Widgets.Hint($"party {s.PartySize}{(s.InAlliance ? " (alliance)" : string.Empty)} · {s.Statuses.Count} status effect(s)");
    }

    private string Name(Guid? id)
        => id is { } g ? _s.Profiles.Profiles.SelectMany(p => p.PassGroups).FirstOrDefault(x => x.Id == g)?.Name ?? "?" : "(none)";

    private string PassName(Guid id)
        => _s.Profiles.Profiles.SelectMany(p => p.PassGroups).SelectMany(g => g.Passes).FirstOrDefault(x => x.Id == id)?.Name ?? "?";
}
