using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace Lamina.Decals.Mesh;

/// <summary> The local player's draw object, the way the mesh part reads it. Framework thread only; pointers are never kept across frames. </summary>
internal static unsafe class LivePlayer
{
    /// <summary> Object table index of the local player. </summary>
    public const int ObjectIndex = 0;

    /// <summary> The local player's CharacterBase, or null while there is none to draw (not logged in, mid-redraw). </summary>
    public static CharacterBase* Character(IObjectTable objects)
    {
        var player  = objects.LocalPlayer ?? objects[ObjectIndex];
        var address = player?.Address ?? nint.Zero;
        if (address == nint.Zero)
            return null;

        var draw = ((GameObject*)address)->DrawObject;
        return draw != null && draw->GetObjectType() == SceneObjectType.CharacterBase ? (CharacterBase*)draw : null;
    }

    /// <summary> The body shape family of a human draw object as a cXXXX number (801 for c0801), 0 for anything else. </summary>
    public static ushort Race(CharacterBase* character)
        => character != null && character->GetModelType() == CharacterBase.ModelType.Human ? ((Human*)character)->RaceSexId : (ushort)0;
}
