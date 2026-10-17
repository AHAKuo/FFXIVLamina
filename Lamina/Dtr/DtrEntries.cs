using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Lamina.Models;
using Lamina.Persistence;
using Lamina.Runtime;

namespace Lamina.Dtr;

/// <summary>
/// Lamina's clickable entries in the Dalamud server info bar. Each entry follows its own flag in <see cref="LaminaConfig"/>:
/// it is created when the flag turns on, removed when it turns off, and removed on dispose. Order and position are Dalamud's
/// business (its own server info bar settings). Texts refresh a few times a second and right after every apply.
/// </summary>
public sealed class DtrEntries : IDisposable
{
    private static readonly TimeSpan RefreshEvery      = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);

    /// <summary> UIColor row used for the muted "Lamina: Off" text. </summary>
    private const ushort MutedColor = 3;

    private readonly IDtrBar _bar;
    private readonly IFramework _framework;
    private readonly ILaminaEngine _engine;
    private readonly ProfileStore _profiles;
    private readonly IPluginLog _log;
    private readonly Action _openWindow;

    private IDtrBarEntry? _reapply;
    private IDtrBarEntry? _toggle;
    private DateTime _nextRefresh = DateTime.MinValue;
    private DateTime _retryAt = DateTime.MinValue;
    private string? _reapplyTooltip;
    private string? _toggleText;
    private string? _toggleTooltip;

    public DtrEntries(IDtrBar bar, IFramework framework, ILaminaEngine engine, ProfileStore profiles, IPluginLog log, Action openWindow)
    {
        _bar        = bar;
        _framework  = framework;
        _engine     = engine;
        _profiles   = profiles;
        _log        = log;
        _openWindow = openWindow;

        _framework.Update += OnUpdate;
        _engine.Applied   += Refresh;
        Sync();
        Refresh();
    }

    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < _nextRefresh)
            return;
        _nextRefresh = now + RefreshEvery;
        Sync();
        Refresh();
    }

    /// <summary> Creates or removes the entries so they match the config flags. </summary>
    private void Sync()
    {
        var cfg = _profiles.Config;
        Sync(ref _reapply, cfg.DtrReapply, DtrText.ReapplyTitle, OnReapplyClick);
        Sync(ref _toggle,  cfg.DtrToggle,  DtrText.ToggleTitle,  OnToggleClick);
    }

    private void Sync(ref IDtrBarEntry? entry, bool wanted, string title, Action<DtrInteractionEvent> onClick)
    {
        if (wanted == entry is not null)
            return;

        if (!wanted)
        {
            Remove(ref entry);
            return;
        }

        if (DateTime.UtcNow < _retryAt)
            return;

        try
        {
            entry = _bar.Get(title);
            entry.OnClick = onClick;
            entry.Shown   = true;
        }
        catch (Exception ex)
        {
            // Dalamud refuses a title another entry already holds (a stale one from an unclean unload, say); try again later.
            _log.Warning(ex, $"Server info bar entry \"{title}\" could not be created; retrying in {RetryAfterFailure.TotalSeconds:0} s.");
            _retryAt = DateTime.UtcNow + RetryAfterFailure;
            entry    = null;
        }
    }

    private void Remove(ref IDtrBarEntry? entry)
    {
        if (entry is null)
            return;
        try
        {
            entry.Remove();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Removing a server info bar entry failed.");
        }
        entry = null;
        // A re-created entry starts blank, so forget the cached texts and let the next Refresh write them again.
        _reapplyTooltip = _toggleText = _toggleTooltip = null;
    }

    /// <summary> Pushes current texts into the entries; only assigns what changed so Dalamud does not redraw the bar for nothing. </summary>
    private void Refresh()
    {
        if (_reapply is not null)
        {
            var tooltip = DtrText.ReapplyTooltip(_profiles.ActiveProfileFor(Target.LocalPlayer())?.Name, _engine.LastApply?.AtUtc, DateTime.UtcNow);
            if (tooltip != _reapplyTooltip)
            {
                _reapply.Text    = DtrText.ReapplyLabel;
                _reapply.Tooltip = tooltip;
                _reapplyTooltip  = tooltip;
            }
        }

        if (_toggle is not null)
        {
            var enabled = _engine.Enabled;
            var text    = DtrText.ToggleLabel(enabled);
            var tooltip = DtrText.ToggleTooltip(enabled);
            if (text != _toggleText)
            {
                _toggle.Text = enabled ? new SeString(new TextPayload(text)) : new SeStringBuilder().AddUiForeground(text, MutedColor).Build();
                _toggleText  = text;
            }
            if (tooltip != _toggleTooltip)
            {
                _toggle.Tooltip = tooltip;
                _toggleTooltip  = tooltip;
            }
        }
    }

    private void OnReapplyClick(DtrInteractionEvent e)
    {
        if (e.ClickType == MouseClickType.Right)
        {
            _openWindow();
            return;
        }
        _engine.RequestReapply();
    }

    private void OnToggleClick(DtrInteractionEvent e)
    {
        if (e.ClickType == MouseClickType.Right)
        {
            _openWindow();
            return;
        }
        _engine.SetEnabled(!_engine.Enabled);
        Refresh();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _engine.Applied   -= Refresh;
        Remove(ref _reapply);
        Remove(ref _toggle);
    }
}
