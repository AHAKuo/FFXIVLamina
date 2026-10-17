using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Lamina.Decals;
using Lamina.Models;
using XivLiveMesh;

namespace Lamina.UI;

/// <summary>
/// Pick mode of the Decal layer. "Place" on a decal card enters it; the player then clicks their character in the game
/// world. Every frame the mouse is cast against the local player's skin (only while it is not over an ImGui window), a
/// circle the size of the decal is drawn at the hovered point on the foreground draw list, a left click commits the
/// placement into the layer, Escape (or "Cancel placing") leaves without a change. Dalamud draws on the framework
/// thread, so the picker may be called from <see cref="Draw"/>.
/// </summary>
internal sealed class DecalPlacementMode
{
    private const string Banner = "Click your character to place the decal. Esc cancels.";

    /// <summary> Most triangles the mesh overlay draws per frame; denser skin is drawn with every n-th triangle. </summary>
    private const int MaxDrawnTriangles = 30_000;

    private readonly LaminaServices _s;
    private Profile? _profile;
    private DecalLayer? _layer;
    private float _size;
    private float _rotation;
    private bool _pickFailed;
    private bool _mouseWasDown;
    private bool _escapeWasDown;
    private string _lastLogged = string.Empty;

    private const int VkLeftButton = 0x01;
    private const int VkEscape     = 0x1B;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    /// <summary>
    /// Escape went down this frame. ImGui only sees keys while one of its windows has keyboard focus; over the game world
    /// the key goes to the game, so the OS key state is polled as well.
    /// </summary>
    private bool EscapePressedThisFrame()
    {
        var down = ImGui.IsKeyDown(ImGuiKey.Escape);
        try
        {
            down |= (GetAsyncKeyState(VkEscape) & 0x8000) != 0;
        }
        catch
        {
            // P/Invoke unavailable: ImGui's state is all we have.
        }

        var pressed    = down && !_escapeWasDown;
        _escapeWasDown = down;
        return pressed || ImGui.IsKeyPressed(ImGuiKey.Escape);
    }

    /// <summary>
    /// Left button went down this frame. ImGui only sees the press when the cursor is over one of its windows (Dalamud
    /// leaves clicks on the game world to the game), so the OS button state is polled as well.
    /// </summary>
    private bool LeftClickedThisFrame()
    {
        var down = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        try
        {
            down |= (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;
        }
        catch
        {
            // P/Invoke unavailable: ImGui's state is all we have.
        }

        var clicked   = down && !_mouseWasDown;
        _mouseWasDown = down;
        return clicked || ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }

    /// <summary> What the pick pipeline did on the last frame of pick mode, shown in the layer card and logged once per distinct text. </summary>
    public string Diagnostics { get; private set; } = string.Empty;

    /// <summary> Draw the posed skin Lamina picks against as a wireframe, with a status block, to compare it with the rendered character. </summary>
    public bool ShowMesh { get; set; }

    /// <summary> A/B check: pose gear skin without the racial deformer (the way a Midlander would wear it). </summary>
    public bool DeformerOff
    {
        get => _s.DecalPicker is Decals.Mesh.DecalPicker picker && !picker.ApplyDeformer;
        set
        {
            if (_s.DecalPicker is Decals.Mesh.DecalPicker picker)
                picker.ApplyDeformer = !value;
        }
    }

    public DecalPlacementMode(LaminaServices services)
        => _s = services;

    /// <summary> True while a placement is being picked. </summary>
    public bool IsActive
        => _layer is not null;

    /// <summary> True while <paramref name="layer"/> is the layer being placed. </summary>
    public bool IsActiveFor(DecalLayer layer)
        => _layer is not null && _layer.Id == layer.Id;

    /// <summary>
    /// Enters pick mode for <paramref name="layer"/>. <paramref name="size"/> (metres) and <paramref name="rotation"/>
    /// (degrees) are what the new placement gets on commit. Does nothing while the decal feature is unavailable.
    /// </summary>
    public void Begin(Profile profile, DecalLayer layer, float size, float rotation)
    {
        if (_s.DecalPicker is null)
            return;
        _profile      = profile;
        _layer        = layer;
        _size         = size;
        _rotation     = rotation;
        _pickFailed   = false;
        _mouseWasDown   = true;   // the press on the Place button must not count as the placing click
        _escapeWasDown  = (GetAsyncKeyState(VkEscape) & 0x8000) != 0;
    }

    /// <summary> Leaves pick mode without changing the layer. </summary>
    public void Cancel()
    {
        _profile = null;
        _layer   = null;
    }

    /// <summary> Call once per frame from the window's draw: overlay, hover pick, click and Escape handling. </summary>
    public void Draw()
    {
        if (_layer is not { } layer || _profile is not { } profile)
            return;
        if (_s.DecalPicker is not { } picker || !LayerStillExists(profile, layer) || EscapePressedThisFrame())
        {
            Cancel();
            return;
        }

        // The main viewport's list, explicitly: called from inside the Lamina window, the parameterless overload returns the
        // foreground list of that window's own viewport, and the overlay ends up clipped to the window instead of over the game.
        var draw = ImGui.GetForegroundDrawList(ImGui.GetMainViewport());
        DrawBanner(draw);

        // Sampled every frame so the edge is not missed while the cursor is over a window.
        var clicked = LeftClickedThisFrame();

        // Over an ImGui window the mouse belongs to the UI: no hover, no click. The mesh overlay stays up either way.
        var overWindow = ImGui.GetIO().WantCaptureMouse;
        var mouse      = ImGui.GetMousePos();
        var pick       = overWindow ? null : SafePick(picker, mouse);
        if (ShowMesh)
            DrawMeshOverlay(draw);
        if (overWindow)
        {
            Report("mouse over a window: move it over your character");
            return;
        }

        if (pick is null)
        {
            DrawCrosshair(draw, mouse, ImGui.GetColorU32(Widgets.Muted));
            DrawDiagnostics(draw, mouse);
            Report(PickerDiagnostics() ?? "no pick");
            return;
        }

        var p = pick.Value;
        Report($"hit: {PickerDiagnostics()} · world ({p.WorldPosition.X:F2}, {p.WorldPosition.Y:F2}, {p.WorldPosition.Z:F2})"
             + (_s.GameGui.WorldToScreen(p.WorldPosition, out var screen) ? $" → screen ({screen.X:F0}, {screen.Y:F0}), mouse ({mouse.X:F0}, {mouse.Y:F0})" : " → off screen"));
        DrawPick(draw, p, mouse);
        if (clicked)
        {
            _s.Log.Information($"Decal pick: click accepted, placing on {p.Mesh.Surface} ({p.PartName ?? "unknown part"}).");
            Commit(profile, layer, p);
        }
    }

    private string? PickerDiagnostics()
    {
        var text = (_s.DecalPicker as Decals.Mesh.DecalPicker)?.LastDiagnostics;
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.StartsWith("no skin meshes", StringComparison.Ordinal) && _s.SkinMeshes is Decals.Mesh.SkinMeshProvider provider
         && !string.IsNullOrEmpty(provider.LastBuildSummary))
            text += $" · {provider.LastBuildSummary}";
        return text;
    }

    private void Report(string text)
    {
        Diagnostics = text;
        if (text == _lastLogged)
            return;
        _lastLogged = text;
        _s.Log.Information($"Decal pick: {text}");
    }

    private void Commit(Profile profile, DecalLayer layer, DecalPick pick)
    {
        layer.Placement = pick.ToPlacement(_size, _rotation);
        _s.Profiles.MarkDirty(profile);
        _s.Engine.Invalidate();
        Cancel();
    }

    /// <summary> The layer may be removed (or its profile deleted) while placing; then the mode ends by itself. </summary>
    private bool LayerStillExists(Profile profile, DecalLayer layer)
        => _s.Profiles.GetProfile(profile.Id) is { } current
        && current.PassGroups.Any(g => g.Passes.Any(p => p.AllLayers.Any(l => l.Id == layer.Id)));

    private DecalPick? SafePick(IDecalPicker picker, Vector2 mouse)
    {
        try
        {
            return picker.Pick(mouse);
        }
        catch (Exception ex)
        {
            if (!_pickFailed)
                _s.Log.Warning(ex, "Decal pick failed; hovering is ignored until pick mode is entered again.");
            _pickFailed = true;
            return null;
        }
    }

    // ---- drawing ------------------------------------------------------------------------------

    private void DrawPick(ImDrawListPtr draw, DecalPick pick, Vector2 mouse)
    {
        if (!_s.GameGui.WorldToScreen(pick.WorldPosition, out var centre))
        {
            // The hit exists but the game cannot project it: say so at the mouse instead of showing nothing.
            var at = mouse + new Vector2(14f, 10f) * Widgets.Scale;
            draw.AddText(at, ImGui.GetColorU32(Widgets.Warn), "hit, but off screen (see the layer card)");
            return;
        }

        var scale  = Widgets.Scale;
        var accent = ImGui.GetColorU32(Widgets.Accent);
        var radius = ScreenRadius(pick, centre);
        draw.AddCircle(centre, radius, accent, 48, 2f * scale);
        DrawCrosshair(draw, centre, accent);

        var label = pick.PartName ?? Widgets.Humanize(pick.Mesh.Surface.ToString());
        if (pick.MirroredUv)
            label += " · mirrored texture";
        var pos = centre + new Vector2(radius + 8f * scale, -ImGui.GetTextLineHeight() / 2f);
        draw.AddText(pos + new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.9f)), label);
        draw.AddText(pos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), label);
    }

    /// <summary>
    /// Screen radius of the decal at the hit: half the size projected along four directions perpendicular to the surface
    /// normal, averaged (an oblique view shows an ellipse; its mean radius makes a fair circle).
    /// </summary>
    private float ScreenRadius(DecalPick pick, Vector2 centre)
    {
        var normal = pick.WorldNormal.LengthSquared() < 1e-6f ? Vector3.UnitY : Vector3.Normalize(pick.WorldNormal);
        var helper = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var t      = Vector3.Normalize(Vector3.Cross(normal, helper));
        var b      = Vector3.Cross(normal, t);
        var half   = _size / 2f;
        var sum    = 0f;
        var count  = 0;
        Add(t);
        Add(-t);
        Add(b);
        Add(-b);
        var radius = count > 0 ? sum / count : 12f * Widgets.Scale;
        return Math.Clamp(radius, 3f, 600f);

        void Add(Vector3 dir)
        {
            if (!_s.GameGui.WorldToScreen(pick.WorldPosition + dir * half, out var p))
                return;
            sum += Vector2.Distance(centre, p);
            count++;
        }
    }

    private static void DrawCrosshair(ImDrawListPtr draw, Vector2 at, uint color)
    {
        var arm = 6f * Widgets.Scale;
        draw.AddLine(at - new Vector2(arm, 0f), at + new Vector2(arm, 0f), color, 1.5f);
        draw.AddLine(at - new Vector2(0f, arm), at + new Vector2(0f, arm), color, 1.5f);
    }

    /// <summary>
    /// Why the mouse is not over skin, next to the cursor: the picker's last diagnostics ("miss", "no skin meshes" ...) and,
    /// when no mesh was built, the provider's build summary. This is the only in-game view of the pick pipeline.
    /// </summary>
    private void DrawDiagnostics(ImDrawListPtr draw, Vector2 mouse)
    {
        var text = PickerDiagnostics();
        if (string.IsNullOrEmpty(text))
            return;

        var pos = mouse + new Vector2(14f, 10f) * Widgets.Scale;
        draw.AddText(pos + new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.9f)), text);
        draw.AddText(pos, ImGui.GetColorU32(Widgets.Muted), text);
    }

    /// <summary>
    /// The posed skin of every source as a wireframe (every n-th triangle within a budget), projected with the game camera
    /// the way the pick ray is built, plus a status block that is drawn no matter what: an empty overlay must never be
    /// silent. Where the lines sit off the rendered skin, the status says which input to suspect (race codes, deformer
    /// steps, bones the skeleton lacks, the pbd).
    /// </summary>
    private void DrawMeshOverlay(ImDrawListPtr draw)
    {
        if (_s.DecalPicker is not Decals.Mesh.DecalPicker picker)
            return;

        var scale     = Widgets.Scale;
        var origin    = ImGui.GetMainViewport().Pos;
        var posed     = picker.LastPosed;
        var hasCamera = ScreenProjection.TryCapture(out var projection);
        var total     = posed.Sum(p => p.Source.Mesh.TriangleCount);
        var stride    = Math.Max(1, (total + MaxDrawnTriangles - 1) / MaxDrawnTriangles);
        if (hasCamera)
        {
            var body = ImGui.GetColorU32(new Vector4(0.2f, 0.9f, 1f, 0.55f));
            var face = ImGui.GetColorU32(new Vector4(1f, 0.8f, 0.2f, 0.55f));
            foreach (var p in posed)
            {
                var colour    = p.Surface.Surface == DecalSurface.Face ? face : body;
                var triangles = p.Source.Mesh.Triangles;
                var world     = p.World;
                var count     = p.Source.Mesh.TriangleCount;
                for (var t = 0; t < count; t += stride)
                {
                    var o = t * 3;
                    if (!projection.WorldToScreen(world[triangles[o]], out var a)
                     || !projection.WorldToScreen(world[triangles[o + 1]], out var b)
                     || !projection.WorldToScreen(world[triangles[o + 2]], out var c))
                        continue;

                    a += origin;
                    b += origin;
                    c += origin;
                    draw.AddLine(a, b, colour);
                    draw.AddLine(b, c, colour);
                    draw.AddLine(c, a, colour);
                }
            }
        }

        var surfaces = _s.SkinMeshes?.GetLocalPlayerMeshes().Count ?? 0;
        var lines    = new List<string>
        {
            $"Lamina mesh overlay: {surfaces} surface(s), {posed.Count} source(s) posed; body c{picker.LastBodyRace:D4}; "
          + $"racial deformer {(picker.ApplyDeformer ? "on" : "OFF")}; pbd {(picker.Deformer.Loaded ? "loaded" : "missing")} ({picker.Deformer.Source}); "
          + $"ray via {picker.LastRaySource}; drawing 1/{stride} triangles",
        };
        if (!hasCamera)
            lines.Add("no active camera: the wireframe cannot be projected");
        if (posed.Count == 0)
            lines.Add($"nothing posed yet: {PickerDiagnostics() ?? "move the mouse over the game world"}");
        foreach (var p in posed)
        {
            lines.Add($"{p.Surface.Surface} {Path.GetFileName(p.Source.FileName)}: model c{p.Source.Race:D4} -> body c{picker.LastBodyRace:D4}, "
                    + $"deformer steps {p.DeformerSteps}, missing bones {p.MissingBones}/{p.Source.Mesh.BoneNames.Length}, "
                    + $"{p.Source.Mesh.VertexCount} verts, {p.Source.Mesh.TriangleCount} tris");
        }

        var text = string.Join("\n", lines);
        var at   = origin + new Vector2(20f, 80f) * scale;
        var size = ImGui.CalcTextSize(text);
        var pad  = new Vector2(8f, 6f) * scale;
        draw.AddRectFilled(at - pad, at + size + pad, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.65f)), 4f * scale);
        draw.AddText(at, ImGui.GetColorU32(new Vector4(1f, 1f, 0.6f, 1f)), text);
    }

    /// <summary> One line near the top of the screen while placing. </summary>
    private static void DrawBanner(ImDrawListPtr draw)
    {
        var scale    = Widgets.Scale;
        var viewport = ImGui.GetMainViewport();
        var textSize = ImGui.CalcTextSize(Banner);
        var padding  = new Vector2(12f, 6f) * scale;
        var min      = new Vector2(viewport.Pos.X + (viewport.Size.X - textSize.X) / 2f - padding.X, viewport.Pos.Y + 40f * scale);
        var max      = min + textSize + padding * 2f;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.10f, 0.85f)), 6f * scale);
        draw.AddRect(min, max, ImGui.GetColorU32(Widgets.Accent), 6f * scale, ImDrawFlags.None, 1f);
        draw.AddText(min + padding, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), Banner);
    }
}
