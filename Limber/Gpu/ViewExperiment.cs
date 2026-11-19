using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using Limber.Core;
using Limber.Rig;

namespace Limber.Gpu;

/// <summary>
/// Research for live monitors: the game's own second-view rendering. Render::Manager keeps 87 views (0x190 each, at +0x8);
/// views 24-31 are world views (every render pass on), drawn when their flags have both low bits set, in the order
/// 24, 25 ... 29, 30 (30 is the main world view: sub-views 0-4 the shadow cascades, 12 the main camera). The experiment
/// copies view 30 into an idle world slot drawn before it, with a camera of Limber's own (a copy of the main render camera
/// moved to a camera prop), and counts what the GPU clears per frame, so it shows whether that extra view is drawn and
/// which targets it uses.
/// </summary>
public static unsafe class ViewExperiment
{
    private const int ViewCount = 87;
    private const int ViewSize = 0x190;
    private const int MainView = 30;
    private const int MainSub = 12;
    private const int CameraSize = 0x400;

    /// <summary>On: the extra view is set up every frame (before the game draws).</summary>
    public static bool On;
    /// <summary>Which idle world view slot gets the copy (24-29 are drawn before the main one; 25 is the game's own).</summary>
    public static int Slot = 27;
    public static string Error = "";

    private static byte[]? _saved;
    private static int _savedSlot = -1;
    private static nint _camera;
    private static int _applied;

    // ---- Every frame, just before the game draws (the bone hook, where the monitors' view is set too)

    public static void Apply()
    {
        if(!On)
        {
            Restore();
            return;
        }
        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
            if(manager == null)
                return;
            nint views = (nint)manager + 0x8;
            if(_savedSlot != Slot)
            {
                Restore();
                _saved = new byte[ViewSize];
                Marshal.Copy(views + Slot * ViewSize, _saved, 0, ViewSize);
                _savedSlot = Slot;
            }
            nint main = views + MainView * ViewSize;
            nint mainCamera = *(nint*)(main + 0x10 + MainSub * 0x18 + 0x10);
            if(!SafeMemory.Plausible(mainCamera))
                return;
            var cam = Pick();
            if(cam == null)
                return;
            if(_camera == 0)
                _camera = (nint)NativeMemory.AllocZeroed(CameraSize);
            if(!SafeMemory.Copy(mainCamera, (void*)_camera, CameraSize))
                return;
            if(!RenderViewFor(cam, out var render))
                return;
            var rc = (FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera*)_camera;
            *(Matrix4x4*)&rc->ViewMatrix = render;
            if(cam.FoV > 1f && rc->FoV > 0.01f)
            {
                float k = MathF.Tan(rc->FoV * 0.5f) / MathF.Tan(cam.FoV * MathF.PI / 360f);
                var p1 = *(Matrix4x4*)&rc->ProjectionMatrix;
                var p2 = *(Matrix4x4*)&rc->ProjectionMatrix2;
                p1.M11 *= k;
                p1.M22 *= k;
                p2.M11 *= k;
                p2.M22 *= k;
                *(Matrix4x4*)&rc->ProjectionMatrix = p1;
                *(Matrix4x4*)&rc->ProjectionMatrix2 = p2;
            }
            nint slot = views + Slot * ViewSize;
            System.Buffer.MemoryCopy((void*)main, (void*)slot, ViewSize, ViewSize);
            for(int sv = 0; sv < 16; sv++)
            {
                nint at = slot + 0x10 + sv * 0x18 + 0x10;
                if(*(nint*)at == mainCamera)
                    *(nint*)at = _camera;
            }
            *(uint*)(slot + 0x8) |= 3;
            _applied++;
        }
        catch(Exception e)
        {
            On = false;
            Error = e.Message;
            Plugin.Log.Warning(e, "Limber: the second-view experiment stopped");
            Restore();
        }
    }

    // The slot as the game had it.
    private static void Restore()
    {
        if(_saved == null || _savedSlot < 0)
            return;
        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
            if(manager != null)
                Marshal.Copy(_saved, 0, (nint)manager + 0x8 + _savedSlot * ViewSize, ViewSize);
        }
        catch
        {
        }
        _saved = null;
        _savedSlot = -1;
    }

    // The camera of the first monitor that's on, or the first camera prop.
    private static CameraProp? Pick()
    {
        foreach(var m in CameraRig.Monitors)
            if(m.On && CameraRig.CameraOf(m) is { } c)
                return c;
        return CameraRig.Cameras.FirstOrDefault();
    }

    // The main render camera's view, put at a camera prop (the same sum as the monitors' frame swap).
    private static bool RenderViewFor(CameraProp cam, out Matrix4x4 newRender)
    {
        newRender = default;
        var cm = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraFFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
        if(cm == null || cm->CurrentCamera == null || cm->CurrentCamera->RenderCamera == null)
            return false;
        var sc = cm->CurrentCamera;
        var rc = sc->RenderCamera;
        Matrix4x4 view = *(Matrix4x4*)&sc->ViewMatrix;
        Matrix4x4 render = *(Matrix4x4*)&rc->ViewMatrix;
        var vfix = Affine(view);
        if(!Matrix4x4.Invert(vfix, out var world))
            return false;
        var xc = new Vector3(world.M11, world.M12, world.M13);
        var yc = new Vector3(world.M21, world.M22, world.M23);
        var zc = new Vector3(world.M31, world.M32, world.M33);
        var so = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)sc;
        var position = *(Vector3*)&so->Position;
        var look = *(Vector3*)&sc->LookAtVector;
        var fcur = look - position;
        if(fcur.LengthSquared() < 1e-8f)
            return false;
        float s = MathF.Sign(Vector3.Dot(zc, Vector3.Normalize(fcur)));
        float t = MathF.Sign(Vector3.Dot(xc, Vector3.Cross(Vector3.UnitY, zc)));
        float u = MathF.Sign(Vector3.Dot(yc, Vector3.Cross(zc, xc)));
        if(s == 0 || t == 0 || u == 0)
            return false;
        var f = Vector3.Normalize(CameraRig.Forward(cam.Rotation));
        var z = s * f;
        var xr = Vector3.Cross(Vector3.UnitY, z);
        xr = xr.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(xr);
        var x = t * xr;
        var y = u * Vector3.Normalize(Vector3.Cross(z, x));
        var w = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, cam.Position.X, cam.Position.Y, cam.Position.Z, 1);
        if(!Matrix4x4.Invert(w, out var newView))
            return false;
        var relation = Affine(render) * world;
        var r = relation * newView;
        r.M14 = render.M14;
        r.M24 = render.M24;
        r.M34 = render.M34;
        r.M44 = render.M44;
        newRender = r;
        return true;
    }

    private static Matrix4x4 Affine(Matrix4x4 m)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return m;
    }

    // ---- What the GPU clears per frame (ClearRenderTargetView / ClearDepthStencilView on the game's context)

    private delegate void ClearRtvDelegate(nint ctx, nint rtv, nint colour);
    private delegate void ClearDsvDelegate(nint ctx, nint dsv, uint flags, float depth, byte stencil);

    private static Hook<ClearRtvDelegate>? _clearRtv;
    private static Hook<ClearDsvDelegate>? _clearDsv;
    private static bool _hooked;

    private static int _rtvClears, _dsvClears, _mainClears;
    private static bool _recording, _recordNext;
    private static readonly StringBuilder Record = new();
    private static long _lastTicks;

    /// <summary>Clears per frame, smoothed (shown on the page).</summary>
    public static float TargetClears, DepthClears, MainDepthClears, FrameMs;
    public static int Applied => _applied;
    public static bool Counting => _hooked;

    /// <summary>The next whole frame's clears listed into the views box.</summary>
    public static void RecordOneFrame()
    {
        EnsureHooks();
        _recordNext = true;
    }

    public static void StartCounting() => EnsureHooks();

    private static nint V(nint obj, int index) => ((nint*)*(nint*)obj)[index];

    private static void EnsureHooks()
    {
        if(_hooked)
            return;
        _hooked = true;
        try
        {
            var d = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
            nint c = d == null ? 0 : (nint)d->D3D11DeviceContext;
            if(c == 0)
                return;
            _clearRtv = Plugin.GameInterop.HookFromAddress<ClearRtvDelegate>(V(c, 50), ClearRtvDetour);
            _clearDsv = Plugin.GameInterop.HookFromAddress<ClearDsvDelegate>(V(c, 53), ClearDsvDetour);
            _clearRtv.Enable();
            _clearDsv.Enable();
        }
        catch(Exception e)
        {
            Error = "couldn't watch the clears: " + e.Message;
            Plugin.Log.Warning(e, "Limber: the second-view experiment can't watch the clears");
        }
    }

    private static void ClearRtvDetour(nint ctx, nint rtv, nint colour)
    {
        try
        {
            _rtvClears++;
            if(_recording)
                Record.Append($"  clear target {Describe(rtv)}\n");
        }
        catch
        {
        }
        _clearRtv!.Original(ctx, rtv, colour);
    }

    private static void ClearDsvDetour(nint ctx, nint dsv, uint flags, float depth, byte stencil)
    {
        try
        {
            _dsvClears++;
            bool main = IsMainDepth(dsv);
            if(main)
                _mainClears++;
            if(_recording)
                Record.Append($"  clear depth {Describe(dsv)} flags {flags} to {depth}{(main ? " MAIN DEPTH" : "")}\n");
        }
        catch
        {
        }
        _clearDsv!.Original(ctx, dsv, flags, depth, stencil);
    }

    private static bool IsMainDepth(nint view)
    {
        var rtm = RenderTargetFFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
        if(rtm == null || rtm->DepthStencil == null || rtm->DepthStencil->D3D11Texture2D == null || view == 0)
            return false;
        nint res = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(view, 7))(view, &res);
        bool main = res != 0 && res == (nint)rtm->DepthStencil->D3D11Texture2D;
        if(res != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)V(res, 2))(res);
        return main;
    }

    // A view's texture: size, format and address.
    private static string Describe(nint view)
    {
        if(view == 0)
            return "(none)";
        nint res = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(view, 7))(view, &res);
        if(res == 0)
            return "(no texture)";
        try
        {
            uint dim = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint*, void>)V(res, 7))(res, &dim);
            if(dim != 3)
                return $"(dimension {dim}) 0x{res:X}";
            uint* desc = stackalloc uint[11];
            ((delegate* unmanaged[Stdcall]<nint, uint*, void>)V(res, 10))(res, desc);
            return $"{desc[0]}x{desc[1]} format {desc[4]} 0x{res:X}";
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)V(res, 2))(res);
        }
    }

    /// <summary>Once a frame (the UI draw, at Present): the frame's counts.</summary>
    public static void Frame()
    {
        if(!_hooked)
            return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float ms = _lastTicks == 0 ? 0 : (float)((now - _lastTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        _lastTicks = now;
        const float k = 0.1f;
        TargetClears += (_rtvClears - TargetClears) * k;
        DepthClears += (_dsvClears - DepthClears) * k;
        MainDepthClears += (_mainClears - MainDepthClears) * k;
        if(ms > 0 && ms < 1000)
            FrameMs += (ms - FrameMs) * k;
        if(_recording)
        {
            _recording = false;
            Record.Insert(0, $"Limber: one frame's clears (extra view {(On ? $"ON in slot {Slot}" : "off")}): {_rtvClears} target clears, {_dsvClears} depth clears ({_mainClears} of the main depth)\n");
            CameraRig.LastViews = Record.ToString();
            foreach(var line in CameraRig.LastViews.Split('\n'))
                if(line.Length > 0)
                    Plugin.Log.Information(line);
        }
        if(_recordNext)
        {
            _recordNext = false;
            Record.Clear();
            _recording = true;
        }
        _rtvClears = 0;
        _dsvClears = 0;
        _mainClears = 0;
    }

    public static void Dispose()
    {
        On = false;
        Restore();
        try { _clearRtv?.Dispose(); } catch { }
        try { _clearDsv?.Dispose(); } catch { }
        _clearRtv = null;
        _clearDsv = null;
        // (the camera copy is left allocated: a frame being drawn may still point at it)
    }
}
