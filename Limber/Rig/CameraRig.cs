using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Limber.Core;

namespace Limber.Rig;

/// <summary>A camera prop: a place and a way it looks (what a monitor shows), like Koikatsu's studio cameras.</summary>
[Serializable]
public sealed class CameraProp
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Camera";
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity; // looks along its +Z
    public float FoV { get; set; }                                    // degrees (0: the game camera's own)
}

/// <summary>A monitor: a flat screen in the world showing what one camera prop sees.</summary>
[Serializable]
public sealed class MonitorProp
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Monitor";
    public bool On { get; set; } = true;
    public Guid Camera { get; set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity; // the screen faces its +Z
    public float Width { get; set; } = 1.2f;                         // metres
    public float Aspect { get; set; } = 16f / 9f;
    public float Brightness { get; set; } = 1f;
    public float Bezel { get; set; } = 0.04f;                        // of the width
    public float[] BezelColour { get; set; } = [0.05f, 0.05f, 0.06f, 1f];
    public bool Mirror { get; set; }
    public bool BackPanel { get; set; } = true;                      // the back drawn as a dark panel
}

/// <summary>
/// Camera props and monitors (Camera page): where they are, which camera each monitor shows, and the timing of the
/// pictures: the game can only draw the world from one camera at a time, so every so often one frame is drawn from a
/// camera prop instead (Limber moves the game camera there for that one frame), copied into that camera's picture, and the
/// last normal frame is put back on screen (Gpu/MonitorRenderer). The cameras take turns.
/// </summary>
public static class CameraRig
{
    public static RigSettings? Config;

    private static int _next;
    private static double _wait;
    private static int _framesSinceGrab = 10;

    public static List<CameraProp> Cameras => Config?.CameraProps ??= new();
    public static List<MonitorProp> Monitors => Config?.Monitors ??= new();

    public static CameraProp? CameraOf(MonitorProp m) => Cameras.FirstOrDefault(c => c.Id == m.Camera);

    /// <summary>Monitors that are on and have a camera (anything to do at all).</summary>
    public static bool Live => Config != null && Config.MonitorsOn && Monitors.Any(m => m.On && CameraOf(m) != null);

    public static Vector3 Forward(Quaternion q) => Vector3.Transform(Vector3.UnitZ, q);

    /// <summary>Framework thread, before the game moves its camera: whether this frame is one drawn from a camera prop.</summary>
    public static void Update(bool here, double seconds)
    {
        _framesSinceGrab++;
        if(!here || !Live)
        {
            CameraControl.SetGrab(null, 0f);
            return;
        }
        var wanted = Monitors.Where(m => m.On).Select(CameraOf).Where(c => c != null).Distinct().ToList();
        if(wanted.Count == 0)
            return;
        float rate = Math.Clamp(Config!.MonitorRefresh, 0.5f, 30f) * wanted.Count;
        _wait -= seconds;
        // (never two in a row: the screen would show the same old frame twice running)
        if(_wait > 0 || _framesSinceGrab < 2)
            return;
        _wait = 1.0 / rate;
        _framesSinceGrab = 0;
        var cam = wanted[_next++ % wanted.Count]!;
        var fwd = Forward(cam.Rotation);
        if(!MathUtil.IsFinite(fwd) || fwd.LengthSquared() < 1e-6f)
            return;
        CameraControl.SetGrab(new CameraControl.Grab(Cameras.IndexOf(cam), cam.Position, cam.Position + Vector3.Normalize(fwd), cam.FoV), 0f);
    }

    /// <summary>Where the game camera is and what it looks at now (where new props go).</summary>
    public static bool Eye(out Vector3 position, out Vector3 forward)
    {
        position = default;
        forward = Vector3.UnitZ;
        if(!CameraUtil.TryGetMatrices(out var view, out _) || !Matrix4x4.Invert(view, out var world))
            return false;
        position = world.Translation;
        // (the view looks down its -Z)
        forward = -Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33));
        return MathUtil.IsFinite(position) && MathUtil.IsFinite(forward);
    }

    /// <summary>A rotation whose +Z is this direction, upright.</summary>
    public static Quaternion Facing(Vector3 dir)
    {
        dir = Vector3.Normalize(dir);
        float yaw = MathF.Atan2(dir.X, dir.Z);
        float pitch = -MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
        return Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0f);
    }

    // ---- The overlay: the cameras drawn as small wireframes with their view, and a gizmo on the chosen camera / monitor

    public static Guid Selected;
    public static bool GizmoMove = true;
    private static int _tabFrame = -10;
    private static bool _wantInput;
    private const int GizmoId = 0x4C43414D;

    public static void TabOpen() => _tabFrame = ImGui.GetFrameCount();

    private static bool TabIsOpen => ImGui.GetFrameCount() - _tabFrame <= 1;

    public static bool OwnsMouse => _wantInput;

    public static void Draw()
    {
        if(Config == null || !Config.MonitorsOn || (Cameras.Count == 0 && Monitors.Count == 0))
        {
            _wantInput = false;
            return;
        }
        bool show = Config.ShowCameraProps && (TabIsOpen || Config.ShowCameraPropsAlways);
        bool gizmo = TabIsOpen && Selected != Guid.Empty;
        if(!show && !gizmo)
        {
            _wantInput = false;
            return;
        }
        if(!CameraUtil.TryGetMatrices(out var view, out var projection))
            return;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking;
        if(!_wantInput)
            flags |= ImGuiWindowFlags.NoInputs;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool usingGizmo = false;
        try
        {
            if(ImGui.Begin("##limber_camera_rig", flags))
            {
                var dl = ImGui.GetWindowDrawList();
                var vp = view * projection;
                bool Project(Vector3 w, out Vector2 s)
                {
                    var c = Vector4.Transform(new Vector4(w, 1f), vp);
                    s = default;
                    if(c.W <= 0.01f)
                        return false;
                    s = new Vector2((c.X / c.W * 0.5f + 0.5f) * viewport.Size.X, (0.5f - c.Y / c.W * 0.5f) * viewport.Size.Y) + viewport.Pos;
                    return true;
                }
                void Line(Vector3 a, Vector3 b, uint col, float t = 1.5f)
                {
                    if(Project(a, out var sa) && Project(b, out var sb))
                        dl.AddLine(sa, sb, col, t);
                }
                if(show)
                {
                    foreach(var cam in Cameras)
                    {
                        uint col = cam.Id == Selected ? 0xFF40D0FFu : 0xFFE0E0E0u;
                        var q = cam.Rotation;
                        var f = Vector3.Transform(Vector3.UnitZ, q);
                        var r = Vector3.Transform(Vector3.UnitX, q);
                        var u = Vector3.Transform(Vector3.UnitY, q);
                        var p = cam.Position;
                        // body (a box), lens, and its view (a short frustum)
                        var bx = new[] { -1f, 1f };
                        Vector3 Corner(float x, float y, float z) => p + r * x * 0.08f + u * y * 0.06f + f * z * 0.1f;
                        foreach(float y in bx)
                            foreach(float z in bx)
                                Line(Corner(-1, y, z), Corner(1, y, z), col);
                        foreach(float x in bx)
                            foreach(float z in bx)
                                Line(Corner(x, -1, z), Corner(x, 1, z), col);
                        foreach(float x in bx)
                            foreach(float y in bx)
                                Line(Corner(x, y, -1), Corner(x, y, 1), col);
                        var lens = p + f * 0.1f;
                        float half = MathF.Tan((cam.FoV > 1f ? cam.FoV : 45f) * MathF.PI / 360f) * 0.5f;
                        var far = p + f * 0.5f;
                        foreach(var (sx, sy) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                            Line(lens, far + r * sx * half * 16f / 9f + u * sy * half, col & 0x80FFFFFF, 1f);
                        if(Project(p, out var label))
                            dl.AddText(label + new Vector2(8, -8), col, cam.Name);
                    }
                    foreach(var m in Monitors)
                    {
                        uint col = m.Id == Selected ? 0xFF40D0FFu : 0x80E0E0E0u;
                        var r = Vector3.Transform(Vector3.UnitX, m.Rotation);
                        var u = Vector3.Transform(Vector3.UnitY, m.Rotation);
                        float w = m.Width * 0.5f, h = m.Width / Math.Max(m.Aspect, 0.1f) * 0.5f;
                        var a = m.Position - r * w - u * h; var b = m.Position + r * w - u * h;
                        var c2 = m.Position + r * w + u * h; var d = m.Position - r * w + u * h;
                        if(m.Id == Selected || !m.On || CameraOf(m) == null)
                        {
                            Line(a, b, col); Line(b, c2, col); Line(c2, d, col); Line(d, a, col);
                        }
                        if((!m.On || CameraOf(m) == null) && Project(m.Position, out var label))
                            dl.AddText(label, col, m.Name + (m.On ? " (no camera)" : " (off)"));
                    }
                }
                if(gizmo)
                    usingGizmo = Gizmo(view, projection, viewport.Pos, viewport.Size);
            }
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
        }
        _wantInput = usingGizmo;
        if(_wantInput)
            ImGui.SetNextFrameWantCaptureMouse(true);
    }

    private static bool _dragging;

    private static bool Gizmo(Matrix4x4 view, Matrix4x4 projection, Vector2 pos, Vector2 size)
    {
        var cam = Cameras.FirstOrDefault(c => c.Id == Selected);
        var mon = cam == null ? Monitors.FirstOrDefault(m => m.Id == Selected) : null;
        if(cam == null && mon == null)
            return false;
        var position = cam?.Position ?? mon!.Position;
        var rotation = cam?.Rotation ?? mon!.Rotation;
        var matrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        ImGuizmo.BeginFrame();
        ImGuizmo.SetID(GizmoId);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.SetDrawlist();
        ImGuizmo.SetRect(pos.X, pos.Y, size.X, size.Y);
        ImGuizmo.Enable(true);
        if(ImGuizmo.Manipulate(ref view, ref projection, GizmoMove ? ImGuizmoOperation.Translate : ImGuizmoOperation.Rotate, ImGuizmoMode.Local, ref matrix)
           && Matrix4x4.Decompose(matrix, out _, out var q, out var t) && MathUtil.IsFinite(t))
        {
            q = Quaternion.Normalize(q);
            if(cam != null)
            {
                cam.Position = t;
                cam.Rotation = q;
            }
            else
            {
                mon!.Position = t;
                mon.Rotation = q;
            }
            _dragging = true;
        }
        bool active = ImGuizmo.IsUsing() || ImGuizmo.IsOver();
        if(!ImGuizmo.IsUsing() && _dragging)
        {
            _dragging = false;
            Config?.Save();
        }
        ImGuizmo.SetID(0);
        return active;
    }
}
