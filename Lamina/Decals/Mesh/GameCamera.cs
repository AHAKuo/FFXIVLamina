using System.Numerics;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using XivLiveMesh;
using CsMatrix4x4 = FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4;
using GameCameraStruct = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace Lamina.Decals.Mesh;

/// <summary>
/// The active game camera as a mouse ray. Screen positions are pixels with the origin at the top-left of the ImGui main
/// viewport, the space <c>IGameGui.WorldToScreen</c> returns and <c>ImGui.GetMousePos()</c> reports.
/// Primary source is the game's own <c>Scene.Camera.ScreenPointToRay</c> through <see cref="ScreenProjection.TryScreenRay"/>
/// (what Dalamud's <c>ScreenToWorld</c> calls); the fallback unprojects with the camera's view / projection matrices
/// (<see cref="MeshMath.TryUnproject"/>). Framework thread only.
/// </summary>
public static unsafe class GameCamera
{
    /// <summary> Which method produced a ray. </summary>
    public enum RaySource
    {
        None,
        Native,
        Matrices,
    }

    /// <summary> World-space ray (unit direction) under <paramref name="screen"/>. </summary>
    public static bool TryGetRay(Vector2 screen, out MeshMath.Ray ray, out RaySource source)
    {
        var local = screen - MainViewportPosition();
        try
        {
            if (ScreenProjection.TryScreenRay(local, out var origin, out var direction) && IsFinite(direction) && IsFinite(origin))
            {
                ray    = new MeshMath.Ray(origin, direction);
                source = RaySource.Native;
                return true;
            }
        }
        catch (Exception)
        {
            // An unresolved game function throws; the matrix fallback takes over.
        }

        if (TryGetMatrices(out var view, out var projection, out var viewport) && MeshMath.TryUnproject(local, viewport, view, projection, out ray))
        {
            source = RaySource.Matrices;
            return true;
        }

        ray    = default;
        source = RaySource.None;
        return false;
    }

    /// <summary> View matrix, projection matrix and render size of the active camera. </summary>
    public static bool TryGetMatrices(out Matrix4x4 view, out Matrix4x4 projection, out Vector2 viewport)
    {
        view       = Matrix4x4.Identity;
        projection = Matrix4x4.Identity;
        viewport   = default;

        var manager = CameraManager.Instance();
        if (manager == null)
            return false;

        GameCameraStruct* camera = null;
        try
        {
            camera = manager->GetActiveCamera();
        }
        catch (Exception)
        {
            // fall back to the main camera below
        }

        if (camera == null)
            camera = manager->Camera;
        if (camera == null)
            return false;

        var render = camera->SceneCamera.RenderCamera;
        if (render == null)
            return false;

        view       = ToNumerics(camera->SceneCamera.ViewMatrix);
        projection = ToNumerics(render->ProjectionMatrix);

        var device = Device.Instance();
        if (device == null)
            return false;

        viewport = new Vector2(device->Width, device->Height);
        return viewport.X > 0 && viewport.Y > 0;
    }

    /// <summary> The game's combined view-projection matrix (what <c>IGameGui.WorldToScreen</c> uses), for self-checks. </summary>
    public static bool TryGetViewProjection(out Matrix4x4 viewProjection)
    {
        viewProjection = Matrix4x4.Identity;
        var control = Control.Instance();
        if (control == null)
            return false;

        viewProjection = ToNumerics(control->ViewProjectionMatrix);
        return true;
    }

    /// <summary> Top-left corner of the game window in screen space (zero unless ImGui viewports move it). </summary>
    public static Vector2 MainViewportPosition()
    {
        try
        {
            return ImGuiHelpers.MainViewport.Pos;
        }
        catch (Exception)
        {
            return Vector2.Zero;
        }
    }

    private static bool IsFinite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static Matrix4x4 ToNumerics(in CsMatrix4x4 m)
        => new(
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44);
}
