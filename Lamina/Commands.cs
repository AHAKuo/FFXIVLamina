using Dalamud.Game.Command;
using Lamina.Models;

namespace Lamina;

/// <summary> Chat commands: /lam (and subcommands) and /vit (TDD 7.2). </summary>
public sealed class Commands : IDisposable
{
    /// <summary> The main command. </summary>
    public const string LaminaCommand = "/lam";

    /// <summary> Opens Vitrine. </summary>
    public const string VitrineCommand = "/vit";

    private readonly LaminaServices _services;
    private readonly Action _toggleWindow;

    /// <param name="services"> Service bundle. </param>
    /// <param name="toggleWindow"> Opens / closes the Lamina window. </param>
    public Commands(LaminaServices services, Action toggleWindow)
    {
        _services     = services;
        _toggleWindow = toggleWindow;

        _services.CommandManager.AddHandler(LaminaCommand, new CommandInfo(OnLamina)
        {
            HelpMessage = "Open Lamina. Subcommands: on, off, profile <name>, reapply, reroll, redraw, why, help.",
        });
        _services.CommandManager.AddHandler(VitrineCommand, new CommandInfo(OnVitrine)
        {
            HelpMessage = "Open Vitrine (separate plugin) when it is installed.",
        });
    }

    /// <summary> Runs a /lam subcommand line (without the command itself). Exposed for the UI's command box. </summary>
    public void Run(string args)
        => OnLamina(LaminaCommand, args);

    private void OnLamina(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub   = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var rest  = parts.Length > 1 ? parts[1] : string.Empty;

        switch (sub)
        {
            case "":
                _toggleWindow();
                break;
            case "on":
                _services.Engine.SetEnabled(true);
                Print("Lamina enabled.");
                break;
            case "off":
                _services.Engine.SetEnabled(false);
                Print("Lamina disabled; character reverted.");
                break;
            case "profile":
                SwitchProfile(rest);
                break;
            case "reapply":
                _services.Engine.RequestReapply();
                Print("Re-applying.");
                break;
            case "reroll":
                _services.Compositor.RandomPicks.Reroll();
                _services.Engine.Invalidate();
                Print("Random layers rolled again.");
                break;
            case "redraw":
                Print(_services.Engine.RedrawNow() ? "Redrawn." : "Redraw requested; Penumbra or your character is not available right now.");
                break;
            case "why":
                foreach (var line in _services.Engine.Why().Split('\n'))
                    Print(line.TrimEnd('\r'));
                break;
            case "help":
            default:
                PrintHelp();
                break;
        }
    }

    private void SwitchProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Print("Profiles: " + string.Join(", ", _services.Profiles.Profiles.Select(p => p.Enabled ? $"[{p.Name}]" : p.Name)));
            return;
        }

        var profile = _services.Profiles.FindProfile(name)
                   ?? _services.Profiles.Profiles.FirstOrDefault(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            Print($"No profile named '{name}'.");
            return;
        }

        if (!profile.Targets.Contains(Target.LocalPlayer()))
            profile.Targets.Add(Target.LocalPlayer());
        _services.Profiles.SetProfileEnabled(profile, true);
        Print($"Profile '{profile.Name}' enabled.");
    }

    private void PrintHelp()
    {
        Print("/lam — open the Lamina window");
        Print("/lam on | off — master toggle (off reverts the character)");
        Print("/lam profile <name> — enable a profile (no name lists them)");
        Print("/lam reapply — re-evaluate and apply now");
        Print("/lam reroll — pick the random layers of every pass again");
        Print("/lam redraw — redraw the character now");
        Print("/lam why — explain the current design");
        Print("/vit — open Vitrine (separate plugin, optional)");
    }

    private void OnVitrine(string command, string args)
    {
        if (!_services.Vitrine.Available)
        {
            Print("Vitrine is not installed. It is a separate plugin from ahadev's repository; install it to browse and try on your mods.");
            return;
        }

        _services.Vitrine.OpenWindow(null);
    }

    private void Print(string message)
        => _services.ChatGui.Print($"[Lamina] {message}");

    /// <inheritdoc/>
    public void Dispose()
    {
        _services.CommandManager.RemoveHandler(LaminaCommand);
        _services.CommandManager.RemoveHandler(VitrineCommand);
    }
}
