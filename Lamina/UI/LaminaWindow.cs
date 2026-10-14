using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lamina.Models;
using Lamina.Runtime.Compose;

namespace Lamina.UI;

/// <summary>
/// The Lamina window (/lam): Passes · Profiles · Settings · About.
/// Construct with the service bundle; register it (and <see cref="DebugOverlay"/>) in a WindowSystem, or call <see cref="RegisterWith"/>.
/// </summary>
public sealed partial class LaminaWindow : Window, IDisposable
{
    private readonly LaminaServices _s;
    private readonly UiCaches _caches;
    private readonly ConditionEditor _conditions;
    private readonly LayerEditor _layers;
    private readonly PreviewPanel _preview;

    // Selection state (validated every frame against the model).
    private Guid? _editProfileId;
    private Guid? _groupId;
    private Guid? _passId;

    // Transient status line.
    private string _status = string.Empty;
    private Vector4 _statusColor = Widgets.Muted;
    private DateTime _statusUntil = DateTime.MinValue;

    public LaminaWindow(LaminaServices services)
        : base("Lamina###LaminaMainWindow")
    {
        _s          = services;
        _caches     = new UiCaches(services);
        _conditions = new ConditionEditor(services, _caches);
        _layers     = new LayerEditor(services, _caches);
        _preview    = new PreviewPanel(services, _caches);
        DebugOverlay = new DebugOverlayWindow(services) { IsOpen = services.Profiles.Config.DebugOverlay };

        Size            = new Vector2(960, 640);
        SizeCondition   = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    /// <summary> The "Lamina Debug" overlay. Must be registered in the same WindowSystem as this window (see <see cref="RegisterWith"/>). </summary>
    public DebugOverlayWindow DebugOverlay { get; }

    /// <summary> Every window this UI owns, for WindowSystem registration. </summary>
    public IEnumerable<Window> Windows
    {
        get
        {
            yield return this;
            yield return DebugOverlay;
        }
    }

    /// <summary> Adds this window and the debug overlay to <paramref name="windowSystem"/>. </summary>
    public void RegisterWith(WindowSystem windowSystem)
    {
        foreach (var w in Windows)
            windowSystem.AddWindow(w);
    }

    /// <summary> Selects a profile / group / pass for editing in the Passes tab. </summary>
    public void Select(Guid? profileId, Guid? groupId = null, Guid? passId = null)
    {
        _editProfileId = profileId;
        _groupId       = groupId;
        _passId        = passId;
    }

    public void Dispose()
        => _caches.Dispose();

    public override void Draw()
    {
        _caches.EnsureFresh();
        ValidateSelection();

        using var tabs = ImRaii.TabBar("##laminaTabs");
        if (!tabs)
            return;

        using (var tab = ImRaii.TabItem("Passes"))
        {
            if (tab)
                DrawPassesTab();
        }
        using (var tab = ImRaii.TabItem("Profiles"))
        {
            if (tab)
                DrawProfilesTab();
        }
        using (var tab = ImRaii.TabItem("Settings"))
        {
            if (tab)
                DrawSettingsTab();
        }
        using (var tab = ImRaii.TabItem("About"))
        {
            if (tab)
                DrawAboutTab();
        }
    }

    // ---- selection helpers ---------------------------------------------------------------------

    private Profile? EditProfile
        => _editProfileId is { } id ? _s.Profiles.GetProfile(id) : null;

    private PassGroup? SelectedGroup
        => _groupId is { } id ? EditProfile?.PassGroups.FirstOrDefault(g => g.Id == id) : null;

    private Pass? SelectedPass
        => _passId is { } id ? SelectedGroup?.Passes.FirstOrDefault(p => p.Id == id) : null;

    private void ValidateSelection()
    {
        var profiles = _s.Profiles.Profiles;
        if (EditProfile is null)
        {
            _editProfileId = (_s.Profiles.ActiveProfileFor(Target.LocalPlayer()) ?? profiles.FirstOrDefault())?.Id;
            _groupId       = null;
            _passId        = null;
        }
        if (_groupId is not null && SelectedGroup is null)
        {
            _groupId = null;
            _passId  = null;
        }
        if (_passId is not null && SelectedPass is null)
            _passId = null;
    }

    private void SetStatus(string text, Vector4? color = null, float seconds = 6f)
    {
        _status      = text;
        _statusColor = color ?? Widgets.Muted;
        _statusUntil = DateTime.UtcNow.AddSeconds(seconds);
    }

    private void DrawStatusLine()
    {
        if (DateTime.UtcNow > _statusUntil || string.IsNullOrEmpty(_status))
            return;
        ImGui.TextColored(_statusColor, _status);
    }

    private void Touch(Profile profile)
    {
        _s.Profiles.MarkDirty(profile);
        _s.Engine.Invalidate();
    }

    private string GroupName(Guid id)
        => _s.Profiles.Profiles.SelectMany(p => p.PassGroups).FirstOrDefault(g => g.Id == id)?.Name ?? "?";

    private string PassName(Guid id)
        => _s.Profiles.Profiles.SelectMany(p => p.PassGroups).SelectMany(g => g.Passes).FirstOrDefault(p => p.Id == id)?.Name ?? "?";

    private PreviewResult? SafePreview(Profile profile, Guid? groupId, Guid? passId)
    {
        try
        {
            return _s.Engine.Preview(profile, groupId, passId);
        }
        catch (Exception ex)
        {
            _s.Log.Debug(ex, "UI: preview failed.");
            return null;
        }
    }
}
