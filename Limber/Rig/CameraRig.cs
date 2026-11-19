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
public static unsafe class CameraRig
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

    // The camera prop the next frame is drawn from (set at the frame's start, used just before the game draws it).
    private static (int Index, CameraProp Camera)? _pending;
    private static bool _hudHiddenByUs;
    private static bool _wrote;
    private static Matrix4x4 _wroteView, _gameView, _wroteRender, _gameRender, _gameProj, _gameProj2;
    private static bool _wroteProj;

    /// <summary>Framework thread, at the start of a frame: whether this frame is one drawn from a camera prop.</summary>
    public static unsafe void Update(bool here, double seconds)
    {
        _framesSinceGrab++;
        CameraControl.GrabFrame = false;
        PutBack();
        Limber.Gpu.LiveCharacters.Update();
        if(_hudHiddenByUs)
        {
            SetHud(true);
            _hudHiddenByUs = false;
        }
        if(!here || !Live)
        {
            _pending = null;
            RoomPose.Clear();
            return;
        }
        var wanted = Monitors.Where(m => m.On).Select(CameraOf).Where(c => c != null).Distinct().ToList();
        if(wanted.Count == 0)
            return;
        if(Config!.MonitorLiveCharacters)
        {
            PlanRoom(wanted!);
            return;
        }
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
        _pending = (Cameras.IndexOf(cam), cam);
        // (the game's HUD, nameplates and markers kept off this frame: it's the one the screens get)
        if(Config!.MonitorHideHud && HudVisible())
        {
            SetHud(false);
            _hudHiddenByUs = true;
        }
    }

    // ---- Live characters: each camera's room picture taken only when needed (none yet, the camera moved, asked for, or
    // older than the chosen time); the characters are drawn live over it (Gpu/LiveCharacters).

    private static readonly Dictionary<Guid, ((Vector3, Quaternion, float) Pose, double Time)> RoomPose = new();
    private static readonly HashSet<Guid> RetakeAsked = new();

    /// <summary>Every screen's room picture taken again (one camera a frame or two apart).</summary>
    public static void RetakeRoom()
    {
        foreach(var c in Cameras)
            RetakeAsked.Add(c.Id);
    }

    private static void PlanRoom(List<CameraProp> wanted)
    {
        if(_framesSinceGrab < 2)
            return;
        double now = Environment.TickCount64 / 1000.0;
        double minGap = 1.0 / Math.Clamp(Config!.MonitorRefresh, 0.5f, 30f);
        CameraProp? need = null;
        foreach(var cam in wanted)
        {
            var pose = (cam.Position, cam.Rotation, cam.FoV);
            bool have = RoomPose.TryGetValue(cam.Id, out var last);
            bool moved = !have || last.Pose != pose;
            bool old = have && Config.MonitorRoomEvery > 0 && now - last.Time > Config.MonitorRoomEvery;
            bool asked = RetakeAsked.Contains(cam.Id);
            if(!moved && !old && !asked)
                continue;
            // (while it's being moved: no faster than the refresh)
            if(have && now - last.Time < minGap)
                continue;
            need = cam;
            break;
        }
        if(need == null)
            return;
        var fwd = Forward(need.Rotation);
        if(!MathUtil.IsFinite(fwd) || fwd.LengthSquared() < 1e-6f)
            return;
        RoomPose[need.Id] = ((need.Position, need.Rotation, need.FoV), now);
        RetakeAsked.Remove(need.Id);
        _framesSinceGrab = 0;
        _pending = (Cameras.IndexOf(need), need);
        if(Config.MonitorHideHud && HudVisible())
        {
            SetHud(false);
            _hudHiddenByUs = true;
        }
    }

    private static unsafe bool HudVisible()
    {
        var ui = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        if(ui == null)
            return false;
        var atk = ui->GetRaptureAtkModule();
        return atk != null && atk->IsUiVisible;
    }

    private static unsafe void SetHud(bool visible)
    {
        try
        {
            var ui = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
            if(ui == null)
                return;
            var atk = ui->GetRaptureAtkModule();
            if(atk != null)
                atk->SetUiVisibility(visible);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Just before the game draws the frame (end of its bone update): for a camera prop's frame, the view the world is
    /// drawn with is put at the camera prop. Only the drawing's own view: the game camera, the HUD, nameplates, markers and
    /// sound stay where they are.
    /// </summary>
    public static unsafe void ApplyView()
    {
        Limber.Gpu.ViewExperiment.Apply();
        if(_pending is not { } pending)
            return;
        _pending = null;
        try
        {
            var cm = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
            if(cm == null || cm->CurrentCamera == null || cm->CurrentCamera->RenderCamera == null)
                return;
            var sc = cm->CurrentCamera;
            var rc = sc->RenderCamera;
            Matrix4x4 view = *(Matrix4x4*)&sc->ViewMatrix;
            Matrix4x4 render = *(Matrix4x4*)&rc->ViewMatrix;
            var vfix = Affine(view);
            if(!Matrix4x4.Invert(vfix, out var world) || !Matrix4x4.Invert(vfix, out var invView))
                return;
            var xc = new Vector3(world.M11, world.M12, world.M13);
            var yc = new Vector3(world.M21, world.M22, world.M23);
            var zc = new Vector3(world.M31, world.M32, world.M33);
            var so = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)sc;
            var position = *(Vector3*)&so->Position;
            var look = *(Vector3*)&sc->LookAtVector;
            var fcur = look - position;
            if(fcur.LengthSquared() < 1e-8f)
                return;
            // (the game's own way round: which way its view axes point)
            float s = MathF.Sign(Vector3.Dot(zc, Vector3.Normalize(fcur)));
            float t = MathF.Sign(Vector3.Dot(xc, Vector3.Cross(Vector3.UnitY, zc)));
            float u = MathF.Sign(Vector3.Dot(yc, Vector3.Cross(zc, xc)));
            if(s == 0 || t == 0 || u == 0)
                return;
            var cam = pending.Camera;
            var f = Vector3.Normalize(Forward(cam.Rotation));
            var z = s * f;
            var xr = Vector3.Cross(Vector3.UnitY, z);
            xr = xr.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(xr);
            var x = t * xr;
            var y = u * Vector3.Normalize(Vector3.Cross(z, x));
            var w = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, cam.Position.X, cam.Position.Y, cam.Position.Z, 1);
            if(!Matrix4x4.Invert(w, out var newView))
                return;
            // the drawing's own view keeps however it relates to the scene's (an origin it draws round)
            var relation = Affine(render) * invView;
            var newRender = relation * newView;
            _gameView = view;
            _gameRender = render;
            _wroteView = Keep(view, newView);
            _wroteRender = Keep(render, newRender);
            *(Matrix4x4*)&sc->ViewMatrix = _wroteView;
            *(Matrix4x4*)&rc->ViewMatrix = _wroteRender;
            _wroteProj = false;
            if(cam.FoV > 1f && rc->FoV > 0.01f)
            {
                float k = MathF.Tan(rc->FoV * 0.5f) / MathF.Tan(cam.FoV * MathF.PI / 360f);
                _gameProj = *(Matrix4x4*)&rc->ProjectionMatrix;
                _gameProj2 = *(Matrix4x4*)&rc->ProjectionMatrix2;
                var p1 = _gameProj;
                p1.M11 *= k;
                p1.M22 *= k;
                var p2 = _gameProj2;
                p2.M11 *= k;
                p2.M22 *= k;
                *(Matrix4x4*)&rc->ProjectionMatrix = p1;
                *(Matrix4x4*)&rc->ProjectionMatrix2 = p2;
                _wroteProj = true;
            }
            _wrote = true;
            // (the characters drawn live kept out of the room picture)
            if(pending.Index == Limber.Gpu.LiveCharacters.LiveCamera)
            {
                Limber.Gpu.LiveCharacters.HideForGrab();
                Limber.Gpu.LiveCharacters.NoteRoomProjection(pending.Index, *(Matrix4x4*)&rc->ProjectionMatrix2);
            }
            CameraControl.Grabbed = pending.Index;
            CameraControl.GrabFrame = true;
        }
        catch
        {
        }
    }

    // The game's own view back, if it didn't make a new one this frame.
    private static unsafe void PutBack()
    {
        if(!_wrote)
            return;
        _wrote = false;
        try
        {
            var cm = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
            if(cm == null || cm->CurrentCamera == null || cm->CurrentCamera->RenderCamera == null)
                return;
            var sc = cm->CurrentCamera;
            var rc = sc->RenderCamera;
            if(*(Matrix4x4*)&sc->ViewMatrix == _wroteView)
                *(Matrix4x4*)&sc->ViewMatrix = _gameView;
            if(*(Matrix4x4*)&rc->ViewMatrix == _wroteRender)
                *(Matrix4x4*)&rc->ViewMatrix = _gameRender;
            if(_wroteProj)
            {
                *(Matrix4x4*)&rc->ProjectionMatrix = _gameProj;
                *(Matrix4x4*)&rc->ProjectionMatrix2 = _gameProj2;
                _wroteProj = false;
            }
        }
        catch
        {
        }
    }

    private static Matrix4x4 Affine(Matrix4x4 m)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return m;
    }

    // (a new view with the old one's fourth column, however the game uses it)
    private static Matrix4x4 Keep(Matrix4x4 old, Matrix4x4 m)
    {
        m.M14 = old.M14;
        m.M24 = old.M24;
        m.M34 = old.M34;
        m.M44 = old.M44;
        return m;
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

    // ---- Research: the game's render views (Render::Manager+0x8: 87 views of 0x190, each 16 sub-views of 0x18: flags,
    // ?, Render::Camera*). The game draws every view whose flags have both low bits set, the world's renderers run per
    // view (RenderView, 0x1402baa50). Listed to the log to find which view is the world, which the character previews.

    /// <summary>The last list (shown on the page too).</summary>
    public static string LastViews = "";

    public static unsafe void DumpViews()
    {
        try
        {
            DumpViewsCore();
        }
        catch(Exception e)
        {
            LastViews = "Couldn't list the views: " + e.Message;
            Plugin.Log.Warning(e, "Limber: couldn't list the render views");
        }
    }

    private static unsafe void DumpViewsCore()
    {
        var log = new System.Text.StringBuilder("Limber: the game's render views:\n");
        var manager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
        if(manager == null)
        {
            LastViews = "No render manager.";
            Plugin.Log.Information("Limber: no render manager");
            return;
        }
        nint baseAt = (nint)manager + 0x8;
        nint mainCamera = 0;
        var cm = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
        if(cm != null && cm->CurrentCamera != null)
            mainCamera = (nint)cm->CurrentCamera->RenderCamera;
        log.Append($"main render camera 0x{mainCamera:X}, slot {SafeMemory.Read<int>((nint)manager + 0x38384, -1)}\n");
        for(int v = 0; v < 87; v++)
        {
            nint view = baseAt + v * 0x190;
            uint flags = SafeMemory.Read<uint>(view + 0x8, 0);
            uint unk = SafeMemory.Read<uint>(view + 0xC, 0);
            var subs = new System.Text.StringBuilder();
            for(int sv = 0; sv < 16; sv++)
            {
                nint sub = view + 0x10 + sv * 0x18;
                uint sf = SafeMemory.Read<uint>(sub + 0x8, 0);
                uint su = SafeMemory.Read<uint>(sub + 0xC, 0);
                nint cam = SafeMemory.ReadPointer(sub + 0x10);
                if(sf == 0 && su == 0 && cam == 0)
                    continue;
                string where = "";
                if(SafeMemory.Plausible(cam))
                {
                    var m = SafeMemory.Read<Matrix4x4>(cam + 0x10, default);
                    m.M14 = m.M24 = m.M34 = 0;
                    m.M44 = 1;
                    if(Matrix4x4.Invert(m, out var w))
                        where = $" at ({w.M41:0.0},{w.M42:0.0},{w.M43:0.0})";
                    if(cam == mainCamera)
                        where += " MAIN";
                }
                subs.Append($" [{sv}: flags 0x{sf:X} unk 0x{su:X} cam 0x{cam:X}{where}]");
            }
            if(flags == 0 && subs.Length == 0)
                continue;
            log.Append($"view {v}: flags 0x{flags:X} unk 0x{unk:X}{((flags & 3) == 3 ? " DRAWN" : "")}{subs}\n");
        }
        LastViews = log.ToString();
        foreach(var line in LastViews.Split('\n'))
            if(line.Length > 0)
                Plugin.Log.Information(line);
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
