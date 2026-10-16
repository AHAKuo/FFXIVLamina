using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Lamina.Decals;
using Lamina.Ipc;
using Lamina.Models;
using Lamina.Runtime;

namespace Lamina.UI;

public sealed partial class LaminaWindow
{
    private readonly DecalImageStore _images;

    private static readonly string[] DirectionNames = { "Top to bottom (bottom wins)", "Bottom to top (top wins)" };
    private static readonly string[] RedrawNames    = { "Defer when unsafe", "Immediate", "Never (manual /lam redraw)" };
    private static readonly string[] FallbackNames  = { "Game state", "Glamourer automation" };

    private void DrawSettingsTab()
    {
        var cfg = _s.Profiles.Config;
        using var child = ImRaii.Child("##settings", new Vector2(0, 0), false);
        if (!child)
            return;

        // ---- master ----
        var enabled = _s.Engine.Enabled;
        using (ImRaii.PushColor(ImGuiCol.Text, enabled ? Widgets.Good : Widgets.Bad))
        {
            if (ImGui.Checkbox(enabled ? "Lamina is ON##master" : "Lamina is OFF##master", ref enabled))
                _s.Engine.SetEnabled(enabled);
        }
        Widgets.Tooltip("Off runs a full cleanup: Lamina's mod settings are removed, Glamourer is reverted (to the game state or automation, see the Glamourer section), the title, moodles, temporary Customize+ profile and Simple Heels offset Lamina set are removed.");
        ImGui.SameLine();
        if (ImGui.Button("Reapply now"))
            _s.Engine.RequestReapply();
        ImGui.SameLine();
        DrawStatusLine();
        ImGui.Separator();

        // ---- evaluation ----
        ImGui.TextColored(Widgets.Accent, "Evaluation");
        var dir = (int)cfg.Direction;
        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        if (ImGui.Combo("Evaluation direction", ref dir, DirectionNames, DirectionNames.Length))
        {
            cfg.Direction = (EvaluationDirection)dir;
            ConfigChanged();
        }
        Widgets.Tooltip("Applies to layers inside a pass and passes inside a group. Default: later rows fall on top of earlier ones.");

        var hold = cfg.DefaultHoldSeconds;
        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        if (ImGui.SliderFloat("Default hold", ref hold, 0f, 10f, "%.1f s"))
        {
            cfg.DefaultHoldSeconds = hold;
            ConfigChanged();
        }
        Widgets.Tooltip("A condition must stay true this long before it counts. Each condition can override it.");
        var linger = cfg.DefaultLingerSeconds;
        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        if (ImGui.SliderFloat("Default linger", ref linger, 0f, 30f, "%.1f s"))
        {
            cfg.DefaultLingerSeconds = linger;
            ConfigChanged();
        }
        Widgets.Tooltip("A condition stays true this long after it stopped. Each condition can override it.");

        var redraw = (int)cfg.RedrawPolicy;
        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        if (ImGui.Combo("Redraw policy", ref redraw, RedrawNames, RedrawNames.Length))
        {
            cfg.RedrawPolicy = (RedrawPolicy)redraw;
            ConfigChanged();
        }
        Widgets.Tooltip("Only mod changes need a redraw. Deferring waits until you are out of combat, cutscenes, loading, mounts and GPose.");
        if (_s.Engine.RedrawPending)
        {
            ImGui.SameLine();
            Widgets.Badge("redraw pending", Widgets.Warn, "A redraw is waiting for a safe moment.");
        }
        ImGui.Spacing();

        // ---- glamourer ----
        ImGui.TextColored(Widgets.Accent, "Glamourer");
        var holdState = cfg.HoldGlamourerState;
        if (ImGui.Checkbox("Hold Glamourer state against automation", ref holdState))
        {
            cfg.HoldGlamourerState = holdState;
            ConfigChanged();
            _s.Engine.Invalidate();
        }
        Widgets.Tooltip("Applies Lamina's state with a lock so Glamourer automation cannot override it. Glamourer has no API to turn automation off; if automation still breaks through, disable it for this character in Glamourer.");
        var fallback = (int)cfg.NoRuleFallback;
        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        if (ImGui.Combo("When no rule sets a field", ref fallback, FallbackNames, FallbackNames.Length))
        {
            cfg.NoRuleFallback = (NoRuleFallback)fallback;
            ConfigChanged();
            _s.Engine.RequestReapply();
        }
        Widgets.Tooltip("What a Glamourer field shows once no rule provides a value for it (a rule stopped matching, or nothing matches at all).\n"
                      + "Game state: what the game itself has equipped / customized, as if Glamourer were not changing it.\n"
                      + "Glamourer automation: whatever Glamourer's automation would show for this character.\n"
                      + "Turning Lamina off, logging out and unloading the plugin revert the same way.");
        var assoc = cfg.ApplyGlamourerModAssociations;
        if (ImGui.Checkbox("Apply Glamourer design mod associations", ref assoc))
        {
            cfg.ApplyGlamourerModAssociations = assoc;
            ConfigChanged();
        }
        Widgets.Tooltip("Mods associated with a Glamourer design are written into the Lamina collection when that design is layered.");
        ImGui.Spacing();

        // ---- integrations ----
        ImGui.TextColored(Widgets.Accent, "Integrations");
        using (var table = ImRaii.Table("##integrations", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            if (table)
            {
                DrawIntegrationRow(_s.Glamourer, required: true);
                DrawIntegrationRow(_s.Penumbra, required: true);
                DrawIntegrationRow(_s.Honorific, required: false);
                DrawIntegrationRow(_s.Moodles, required: false);
                DrawIntegrationRow(_s.CustomizePlus, required: false);
                DrawIntegrationRow(_s.SimpleHeels, required: false);
                DrawIntegrationRow(_s.Vitrine, required: false);
            }
        }
        ImGui.Spacing();

        // ---- collection ----
        ImGui.TextColored(Widgets.Accent, "Penumbra collection");
        var name = cfg.CollectionName;
        ImGui.SetNextItemWidth(260 * Widgets.Scale);
        if (ImGui.InputText("Collection name", ref name, 64, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            cfg.CollectionName = name.Trim().Length == 0 ? "Lamina" : name.Trim();
            ConfigChanged();
            _s.Engine.RefreshCollection();
        }
        Widgets.Tooltip("Lamina writes mod states only into this collection, as temporary settings it can remove again in one call. Press Enter to apply.");
        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
        {
            _s.Engine.RefreshCollection();
            SetStatus("Collection status refreshed.");
        }
        switch (_s.Engine.CollectionStatus)
        {
            case CollectionStatus.PenumbraMissing:
                ImGui.TextColored(Widgets.Bad, "Penumbra is not loaded.");
                break;
            case CollectionStatus.NotFound:
                ImGui.TextColored(Widgets.Warn, $"Create a collection named \"{cfg.CollectionName}\" in Penumbra (it can be empty or inherit from your main collection), then press Refresh.");
                break;
            case CollectionStatus.NotAssigned:
                ImGui.TextColored(Widgets.Warn, $"Assign your character to the \"{cfg.CollectionName}\" collection in Penumbra (Collections → Individual Assignments, or Your Character), then press Refresh.");
                Widgets.Hint("Lamina never assigns or unassigns collections itself. Until the collection is assigned, mods are skipped; Glamourer, Honorific, Moodles, Customize+ and Simple Heels still apply.");
                break;
            case CollectionStatus.Assigned:
                ImGui.TextColored(Widgets.Good, $"\"{cfg.CollectionName}\" is assigned to your character. Mods will be applied.");
                Widgets.Hint("To opt out of mod changes, unassign it in Penumbra; Lamina notices within a second and clears what it wrote. The master toggle stops everything.");
                break;
        }
        ImGui.Spacing();

        // ---- debug ----
        ImGui.TextColored(Widgets.Accent, "Debug");
        var overlay = cfg.DebugOverlay;
        if (ImGui.Checkbox("Debug overlay", ref overlay))
        {
            cfg.DebugOverlay    = overlay;
            DebugOverlay.IsOpen = overlay;
            ConfigChanged();
        }
        Widgets.Tooltip("Small always-visible window with the active group, matched passes, last apply and the current game state.");
        ImGui.SameLine();
        if (ImGui.Button("Print /lam why to chat"))
            foreach (var line in _s.Engine.Why().Split('\n'))
                _s.ChatGui.Print(line.TrimEnd('\r'));
        ImGui.Spacing();

        // ---- data ----
        ImGui.TextColored(Widgets.Accent, "Data");
        var backups = cfg.BackupsToKeep;
        ImGui.SetNextItemWidth(120 * Widgets.Scale);
        if (ImGui.InputInt("Backups to keep", ref backups))
        {
            cfg.BackupsToKeep = Math.Clamp(backups, 0, 100);
            ConfigChanged();
        }
        var dirPath = _s.PluginInterface.GetPluginConfigDirectory();
        if (ImGui.Button("Open config folder"))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dirPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _s.Log.Warning(ex, "Could not open the config folder.");
                SetStatus("Could not open the folder.", Widgets.Bad);
            }
        }
        Widgets.Tooltip(dirPath);
        ImGui.SameLine();
        if (ImGui.Button("Save now"))
        {
            _s.Profiles.SaveNow();
            SetStatus("Saved.", Widgets.Good);
        }
        ImGui.SameLine();
        Widgets.Hint(_s.Profiles.HasPendingSaves ? "unsaved changes (auto-saves in a second)" : "all changes saved");
        ImGui.Spacing();

        // ---- decals ----
        ImGui.TextColored(Widgets.Accent, "Decals");
        if (_s.DecalPublisher is null)
        {
            Widgets.Hint("The decal feature is unavailable.");
        }
        else
        {
            var ctrl = ImGui.GetIO().KeyCtrl;
            using (ImRaii.Disabled(!ctrl))
            {
                if (ImGui.Button("Clean up decal leftovers") && ctrl)
                    CleanUpDecals(DecalCleanupMode.Unused);
            }
            Widgets.Tooltip("Hold Ctrl. Forgets remembered bakes other than the installed one, deletes bake files the installed decal mod does not use, "
                          + "deletes images no layer references, and removes a leftover decal mod from the collection when no decals are applied.");
            ImGui.SameLine();
            using (ImRaii.Disabled(!ctrl))
            using (ImRaii.PushColor(ImGuiCol.Text, Widgets.Bad))
            {
                if (ImGui.Button("Clear all decal data") && ctrl)
                    CleanUpDecals(DecalCleanupMode.Everything);
            }
            Widgets.Tooltip("Hold Ctrl. Destructive: removes the decal mod, every bake and every imported image, referenced or not. "
                          + "Decal layers keep their image name and show \"missing image\" until you pick the image again.");
            ImGui.SameLine();
            Widgets.Hint("hold Ctrl");
        }
        if (_s.Profiles.LoadErrors.Count > 0)
        {
            ImGui.TextColored(Widgets.Warn, "Problems while loading:");
            foreach (var e in _s.Profiles.LoadErrors)
                ImGui.TextColored(e.IsWarning ? Widgets.Warn : Widgets.Bad, $"• {e.File}: {e.Message}");
        }
    }

    private void ConfigChanged()
    {
        _s.Profiles.MarkConfigDirty();
        _s.Engine.Invalidate();
    }

    private static void DrawIntegrationRow(IPluginBridge bridge, bool required)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(bridge.PluginName);
        ImGui.TableNextColumn();
        if (bridge.Available)
            ImGui.TextColored(Widgets.Good, bridge.ApiVersion is { } v ? $"available (API {v.Major}.{v.Minor})" : "available");
        else
            ImGui.TextColored(required ? Widgets.Bad : Widgets.Muted, "not loaded");
        ImGui.TableNextColumn();
        Widgets.Hint(required ? "required" : "optional");
        if (required && !bridge.Available)
            Widgets.Tooltip($"{bridge.PluginName} is required. Install and enable it; Lamina picks it up automatically.");
    }

    /// <summary>
    /// The two decal maintenance buttons: leftovers only, or everything. Images are pruned here (the publisher does not know
    /// the profiles); after a full clear the engine forgets its decal bookkeeping so the next evaluation publishes anew.
    /// </summary>
    private void CleanUpDecals(DecalCleanupMode mode)
    {
        if (_s.DecalPublisher is not { } publisher)
            return;

        var engine    = _s.Engine;
        var removeMod = mode == DecalCleanupMode.Everything || !engine.DecalsApplied;
        var result    = publisher.CleanUp(engine.CollectionId, mode, removeMod);
        var images    = 0;
        try
        {
            images = _images.Prune(mode == DecalCleanupMode.Everything ? Array.Empty<string>() : DecalImageStore.ReferencedImages(_s.Profiles.Profiles)).Count;
        }
        catch (Exception ex)
        {
            _s.Log.Warning(ex, "Pruning decal images failed.");
        }

        if (mode == DecalCleanupMode.Everything)
            engine.ForgetDecals(redraw: result.TemporaryModRemoved);

        var text = $"Decal {(mode == DecalCleanupMode.Everything ? "clear" : "cleanup")}: {result.MapsForgotten} remembered map(s) forgotten, "
                 + $"{result.BakesDeleted} bake(s) and {result.TempFilesDeleted} temp file(s) deleted, {images} image(s) deleted, "
                 + $"decal mod {(result.TemporaryModRemoved ? "removed" : "kept")}.";
        if (result.Problems.Count > 0)
            text += " Problems: " + string.Join("; ", result.Problems);
        _s.Log.Information(text);
        SetStatus(text, result.Problems.Count > 0 ? Widgets.Warn : Widgets.Good, 10f);
    }
}
