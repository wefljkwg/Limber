using System.Numerics;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace Limber.Core;

public static unsafe class CameraUtil
{
    // The active camera's view (world -> camera) and projection matrices, in the row-vector form
    // System.Numerics and ImGuizmo use.
    public static bool TryGetMatrices(out Matrix4x4 view, out Matrix4x4 projection)
    {
        view = Matrix4x4.Identity;
        projection = Matrix4x4.Identity;

        var manager = SceneCameraManager.Instance();
        if(manager == null)
            return false;

        var camera = manager->CurrentCamera;
        if(camera == null || camera->RenderCamera == null)
            return false;

        view = camera->ViewMatrix;
        view.M44 = 1f;

        var render = camera->RenderCamera;
        projection = render->ProjectionMatrix;

        // The game's projection has an infinite or reversed far plane; rebuild the depth terms as a regular
        // perspective projection so the gizmo can work out depth.
        float near = render->NearPlane;
        float far = render->FarPlane;
        if(far - near > 1e-3f)
        {
            projection.M33 = -(far + near) / (far - near);
            projection.M43 = -(2f * far * near) / (far - near);
        }

        return true;
    }

    // World-space directions of the camera's right and up axes.
    public static bool TryGetAxes(out Vector3 right, out Vector3 up)
    {
        right = Vector3.UnitX;
        up = Vector3.UnitY;

        if(!TryGetMatrices(out var view, out _) || !Matrix4x4.Invert(view, out var cameraToWorld))
            return false;

        right = Vector3.Normalize(new Vector3(cameraToWorld.M11, cameraToWorld.M12, cameraToWorld.M13));
        up = Vector3.Normalize(new Vector3(cameraToWorld.M21, cameraToWorld.M22, cameraToWorld.M23));
        return MathUtil.IsFinite(right) && MathUtil.IsFinite(up);
    }
}
