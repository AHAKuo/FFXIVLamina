using System.IO;
using Dalamud.Plugin.Services;
using Lamina.Ipc;
using XivLiveMesh;

namespace Lamina.Decals.Mesh;

/// <summary>
/// The racial bone deformer (<c>chara/xls/boneDeformer/human.pbd</c>) the player's collection resolves, else the game's own,
/// parsed once and shared by the mesh provider (rest-pose deformation) and the picker (posing). Gear is authored for one
/// race and bent onto the character's race at draw time; without this file gear skin sits where a Midlander's would.
/// Framework thread only (Penumbra path resolution).
/// </summary>
public sealed class RacialDeformer
{
    public const string GamePath = "chara/xls/boneDeformer/human.pbd";

    private readonly IPenumbraBridge _penumbra;
    private readonly IDataManager    _data;
    private readonly IPluginLog      _log;

    private PbdFile? _pbd;
    private bool     _tried;

    /// <param name="penumbra"> Penumbra bridge (the collection may replace the file). </param>
    /// <param name="data"> Dalamud data manager (the game's own file). </param>
    /// <param name="log"> Plugin log; the outcome is logged once. </param>
    public RacialDeformer(IPenumbraBridge penumbra, IDataManager data, IPluginLog log)
    {
        _penumbra = penumbra;
        _data     = data;
        _log      = log;
    }

    /// <summary> The parsed deformer, loaded on first use; null when neither Penumbra nor the game data could provide it. </summary>
    public PbdFile? Pbd
    {
        get
        {
            Load();
            return _pbd;
        }
    }

    /// <summary> True when <see cref="Pbd"/> is available. </summary>
    public bool Loaded
        => Pbd is not null;

    /// <summary> Where the file came from (a mod file, the game path) or why it is missing, for the overlay. </summary>
    public string Source { get; private set; } = "not loaded yet";

    /// <summary> Forgets the file so the next use loads it again (after a mod change). </summary>
    public void Reset()
    {
        _tried = false;
        _pbd   = null;
    }

    private void Load()
    {
        if (_tried)
            return;

        _tried = true;
        try
        {
            var     resolved = _penumbra.Available ? _penumbra.ResolveGameObjectPath(GamePath, LivePlayer.ObjectIndex) : GamePath;
            byte[]? bytes    = null;
            if (!string.IsNullOrEmpty(resolved) && Path.IsPathRooted(resolved) && File.Exists(resolved))
            {
                bytes  = File.ReadAllBytes(resolved);
                Source = resolved;
            }

            if (bytes is null)
            {
                bytes  = _data.GetFile(GamePath)?.Data;
                Source = GamePath;
            }

            if (bytes is null)
            {
                Source = "missing: neither Penumbra nor the game data provided the file";
                _log.Warning($"Lamina: racial deformer {GamePath} could not be read; gear skin is posed without racial deformation.");
                return;
            }

            _pbd = new PbdFile(bytes);
            _log.Information($"Lamina: racial deformer loaded from {Source} ({bytes.Length} bytes).");
        }
        catch (Exception ex)
        {
            _pbd   = null;
            Source = $"unreadable: {ex.Message}";
            _log.Warning(ex, $"Lamina: racial deformer {GamePath} could not be parsed; gear skin is posed without racial deformation.");
        }
    }
}
