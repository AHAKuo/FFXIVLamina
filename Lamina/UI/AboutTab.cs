using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Lamina.Ipc;

namespace Lamina.UI;

public sealed partial class LaminaWindow
{
    private const string RepoUrl = "https://github.com/AHAKuo/FFXIVLamina";

    private void DrawAboutTab()
    {
        using var child = ImRaii.Child("##about", new System.Numerics.Vector2(0, 0), false);
        if (!child)
            return;

        ImGui.TextColored(Widgets.Accent, "Lamina");
        ImGui.SameLine();
        Widgets.Hint($"v{_s.Version}");
        ImGui.TextUnformatted("by ahadev");
        ImGui.Spacing();

        Widgets.Paragraph("Lamina automates your character's appearance through virtual designs. Instead of several rules each applying their own change and redrawing you every time, Lamina reads the game state, finds the passes that match, layers everything they want (Glamourer appearance, Penumbra mods, an Honorific title, Moodles) into one result, and applies it in a single batch with at most one redraw. No flicker, predictable layering, and a clean exit: turning Lamina off leaves your character exactly as Glamourer and Penumbra would have it.");
        ImGui.Spacing();

        ImGui.TextUnformatted("How it fits together");
        Widgets.Paragraph("Profile → Pass Groups (a switch: first true wins, plus Final groups layered on top by priority) → Passes (every matching one contributes; lower rows override) → Layers (a Glamourer design, a Vitrine design, Penumbra mods, a title, moodles). Lamina owns its own Penumbra collection and never touches your other collections. Vitrine (/vit) is the companion view that organizes your mods by what they change and turns them into designs you can layer.");
        ImGui.Spacing();

        ImGui.TextUnformatted("Requires");
        DrawAboutRow(_s.Glamourer);
        DrawAboutRow(_s.Penumbra);
        ImGui.TextUnformatted("Supports");
        DrawAboutRow(_s.Honorific);
        DrawAboutRow(_s.Moodles);
        ImGui.Spacing();

        ImGui.TextUnformatted("Commands");
        Widgets.Hint("/lam  open this window · /lam on | off · /lam profile <name> · /lam reapply · /lam redraw · /lam why · /vit  open Vitrine");
        ImGui.Spacing();

        if (ImGui.Button("Open repository"))
            Util.OpenLink(RepoUrl);
        Widgets.Tooltip(RepoUrl);
    }

    private static void DrawAboutRow(IPluginBridge bridge)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextUnformatted(bridge.PluginName);
        ImGui.SameLine();
        ImGui.TextColored(bridge.Available ? Widgets.Good : Widgets.Muted, bridge.Available ? "loaded" : "not loaded");
    }
}
