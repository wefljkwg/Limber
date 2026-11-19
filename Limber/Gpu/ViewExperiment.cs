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
/// Research for live monitors: the game's own second-view rendering. Render::Manager keeps 87 views (0x190 each, at +0x8).
/// Which picture a view draws into is fixed by its number (RendererUtility, 0x14027a060): 30 the main world picture,
/// 0-19 the character windows, 85 one other, and 25 the "chara view" slots: up to 8 extra world drawings a frame, slot n
/// drawn when Manager+0x3835c+n*4 is above 0, from view 25's sub-view n camera, into RenderTargetManager's
/// _charaViewTextures[n] (+0x2c8). Every other number has no picture (drawing one crashes the game). The experiment puts
/// a camera of Limber's own (the main render camera copied, moved to a camera prop) into an idle chara-view slot that has
/// a picture, and shows that picture on the page.
/// </summary>
public static unsafe class ViewExperiment
{
    private const int ViewSize = 0x190;
    private const int MainView = 30;
    private const int MainSub = 12;
    private const int CharaView = 25;
    private const int CameraSize = 0x400;

    /// <summary>On: the chara-view slot is set up every frame (before the game draws).</summary>
    public static bool On;
    /// <summary>Which chara-view slot (0-7).</summary>
    public static int Slot = 7;
    /// <summary>Off: Limber's camera is marked as already updated, so the game doesn't work its view out again.</summary>
    public static bool LetGameUpdateCamera = true;
    public static string Error = "";
    public static string State = "";

    private static int _usedSlot = -1;
    private static nint _savedCamera;
    private static int _savedCount;
    private static nint _camera;
    private static int _applied;

    private static nint Views(out nint manager)
    {
        var m = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
        manager = (nint)m;
        return m == null ? 0 : (nint)m + 0x8;
    }

    private static nint SlotCameraAt(nint views, int n) => views + CharaView * ViewSize + 0x10 + n * 0x18 + 0x10;
    private static nint CountAt(nint manager, int n) => manager + 0x3835c + n * 4;

    /// <summary>Chara-view slot n's picture (a Kernel::Texture*), or 0.</summary>
    public static nint SlotTexture(int n)
    {
        var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
        if(rtm == null || n < 0 || n > 7)
            return 0;
        return SafeMemory.ReadPointer((nint)rtm + 0x2c8 + n * 8);
    }

    /// <summary>Its shader view (for showing it on the page), or 0.</summary>
    public static nint SlotPicture(int n, out int width, out int height)
    {
        width = height = 0;
        nint t = SlotTexture(n);
        if(!SafeMemory.Plausible(t))
            return 0;
        var tex = (FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture*)t;
        width = (int)tex->ActualWidth;
        height = (int)tex->ActualHeight;
        return (nint)tex->D3D11ShaderResourceView;
    }

    /// <summary>What the chara-view slots hold now (into the views box).</summary>
    public static void Inspect()
    {
        var log = new StringBuilder("Limber: chara-view slots (view 25):\n");
        try
        {
            nint views = Views(out var manager);
            var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
            if(views == 0 || rtm == null)
            {
                CameraRig.LastViews = "No render manager.";
                return;
            }
            for(int n = 0; n < 8; n++)
            {
                int count = SafeMemory.Read<int>(CountAt(manager, n), -1);
                nint cam = SafeMemory.ReadPointer(SlotCameraAt(views, n));
                nint t = SlotTexture(n);
                string tex = "no picture";
                if(SafeMemory.Plausible(t))
                {
                    var k = (FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture*)t;
                    tex = $"picture {k->ActualWidth}x{k->ActualHeight} (allocated {k->AllocatedWidth}x{k->AllocatedHeight}) format 0x{(uint)k->TextureFormat:X}";
                }
                log.Append($"slot {n}: count {count}, camera 0x{cam:X}, {tex}{(n == _usedSlot ? " (Limber's)" : "")}\n");
            }
            for(int g = 0; g < 5; g++)
                log.Append($"chara-view g-buffer {g}: 0x{SafeMemory.ReadPointer((nint)rtm + 0x308 + g * 8):X}\n");
        }
        catch(Exception e)
        {
            log.Append("failed: " + e.Message + "\n");
        }
        CameraRig.LastViews = log.ToString();
        foreach(var line in CameraRig.LastViews.Split('\n'))
            if(line.Length > 0)
                Plugin.Log.Information(line);
    }

    // ---- Every frame, just before the game draws (the bone hook, where the monitors' view is set too)

    // ---- Right before the game draws: Render::Manager.Render (0x1402ba070) hooked, so nothing the game does later in the
    // frame (the preview windows put their own cameras into the slots) can undo Limber's setup.
    private delegate void RenderDelegate(nint manager);
    private const string RenderSig = "40 53 57 41 54 41 55 48 83 EC 58 65 48 8B 04 25 58 00 00 00 4C 8B E9";
    private static Hook<RenderDelegate>? _render;
    private static bool _renderTried;
    /// <summary>Whose camera sat in the slot right after the game drew (Limber's: the slot was drawn from it).</summary>
    public static string DuringDraw = "";

    private static void EnsureRenderHook()
    {
        if(_renderTried)
            return;
        _renderTried = true;
        try
        {
            if(!Plugin.SigScanner.TryScanText(RenderSig, out var at))
            {
                Error = "couldn't find the game's render function (using the bone update instead)";
                return;
            }
            _render = Plugin.GameInterop.HookFromAddress<RenderDelegate>(at, RenderDetour);
            _render.Enable();
        }
        catch(Exception e)
        {
            Error = "couldn't hook the game's render function: " + e.Message;
            _render = null;
        }
    }

    private static void RenderDetour(nint manager)
    {
        try
        {
            if(On || _usedSlot >= 0)
                ApplyCore();
        }
        catch
        {
        }
        _render!.Original(manager);
        try
        {
            if(On && _usedSlot >= 0)
            {
                nint views = manager + 0x8;
                nint sc = *(nint*)SlotCameraAt(views, _usedSlot);
                DuringDraw = sc == _camera && sc != 0 ? "drawn from the slot's own camera, moved by Limber" : $"after the draw the slot held 0x{sc:X}, not Limber's camera";
            }
        }
        catch
        {
        }
    }

    /// <summary>From the bone update: only when the render function couldn't be hooked.</summary>
    public static void Apply()
    {
        if(On)
            EnsureRenderHook();
        if(_render != null)
            return;
        ApplyCore();
    }

    private static void ApplyCore()
    {
        if(!On)
        {
            Restore();
            return;
        }
        try
        {
            nint views = Views(out var manager);
            if(views == 0)
                return;
            if(_usedSlot != Slot)
            {
                Restore();
                if(!SafeMemory.Plausible(SlotTexture(Slot)))
                {
                    State = $"slot {Slot} has no picture (try another slot, or open Try On / the Character window first)";
                    return;
                }
                int count = *(int*)CountAt(manager, Slot);
                if(count > 0)
                {
                    State = $"slot {Slot} is in use by the game";
                    return;
                }
                _savedCamera = *(nint*)SlotCameraAt(views, Slot);
                _savedCount = count;
                _usedSlot = Slot;
            }
            if(!SafeMemory.Plausible(SlotTexture(_usedSlot)))
            {
                Restore();
                State = $"slot {Slot} lost its picture";
                return;
            }
            nint main = views + MainView * ViewSize;
            nint mainCamera = *(nint*)(main + 0x10 + MainSub * 0x18 + 0x10);
            if(!SafeMemory.Plausible(mainCamera))
                return;
            var cam = Pick();
            if(cam == null)
            {
                State = "no camera prop";
                return;
            }
            // The game puts each slot's own camera into the slot right before drawing it (0x14026a200, from a table at a
            // global's +0xc8 + n*8), so Limber moves that camera instead of swapping in its own.
            nint slotCam = SlotOwnCamera(_usedSlot);
            if(!SafeMemory.Plausible(slotCam))
            {
                State = $"slot {_usedSlot} has no camera of its own";
                return;
            }
            if(_slotCam != slotCam)
            {
                RestoreCamera();
                _slotCam = slotCam;
                _savedView = *(Matrix4x4*)(slotCam + 0x10);
                _savedOrigin = *(Vector3*)(slotCam + 0x90);
                _saved205 = *(byte*)(slotCam + 0x205);
            }
            _camera = slotCam;
            if(!RenderViewFor(cam, out var render))
                return;
            *(Matrix4x4*)(slotCam + 0x10) = render;
            *(Vector3*)(slotCam + 0x90) = *(Vector3*)(mainCamera + 0x90);
            *(byte*)(slotCam + 0x205) = (byte)(LetGameUpdateCamera ? 0 : 1);
            *(int*)CountAt(manager, _usedSlot) = 1;
            Light(_usedSlot);
            _applied++;
            State = $"drawing into slot {_usedSlot}";
        }
        catch(Exception e)
        {
            On = false;
            Error = e.Message;
            Plugin.Log.Warning(e, "Limber: the second-view experiment stopped");
            Restore();
        }
    }

    // Each chara-view slot has a small lighting block (a global's +(n+0x2b)*0x40), which the game fills when a preview
    // window takes the slot (0x1402691d0): a key light, a sky-ish direction, ambient 0.2, the rest 1. A slot nobody took
    // has it all zero, so it draws black: Limber fills it the same way.
    private const string LightSig = "83 FA 07 77 ?? 48 63 C2 48 83 C0 2B 8B CA 48 C1 E0 06 48 03 05";
    private static nint _lightGlobal = -1;
    public static string LightState = "";

    private static void Light(int n)
    {
        if(_lightGlobal == -1)
        {
            _lightGlobal = 0;
            try
            {
                if(Plugin.SigScanner.TryScanText(LightSig, out var at))
                    _lightGlobal = at + 25 + *(int*)(at + 21);
            }
            catch
            {
            }
            if(_lightGlobal == 0)
                LightState = "couldn't find the slots' lighting";
        }
        if(_lightGlobal == 0)
            return;
        nint table = *(nint*)_lightGlobal;
        if(!SafeMemory.Plausible(table))
            return;
        float* b = (float*)(table + (n + 0x2b) * 0x40);
        bool empty = true;
        for(int i = 0; i < 16; i++)
            if(b[i] != 0)
                empty = false;
        if(LightState.Length == 0)
            LightState = empty ? $"slot {n}'s lighting was empty: filled in" : $"slot {n}'s lighting was already set";
        *(int*)b = 0;
        *(uint*)(b + 1) = 0x3f2ac083;
        b[4] = b[5] = b[6] = 0.57735f;
        b[8] = b[9] = b[10] = 0.2f;
        b[11] = b[12] = b[13] = b[14] = b[15] = 1f;
    }

    private const string SlotCamerasSig = "48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 63 C7 48 8D 0C 40 49 8B 8C CD";
    private static nint _slotCamerasGlobal = -1;
    private static nint _slotCam;
    private static Matrix4x4 _savedView;
    private static Vector3 _savedOrigin;
    private static byte _saved205;

    // Slot n's own camera (the one the game puts into view 25's sub-view n before drawing it).
    private static nint SlotOwnCamera(int n)
    {
        if(_slotCamerasGlobal == -1)
        {
            _slotCamerasGlobal = 0;
            try
            {
                if(Plugin.SigScanner.TryScanText(SlotCamerasSig, out var at))
                    _slotCamerasGlobal = at + 7 + *(int*)(at + 3);
            }
            catch
            {
            }
        }
        if(_slotCamerasGlobal == 0)
            return 0;
        nint obj = *(nint*)_slotCamerasGlobal;
        if(!SafeMemory.Plausible(obj))
            return 0;
        return SafeMemory.ReadPointer(obj + 0xc8 + n * 8);
    }

    private static void RestoreCamera()
    {
        if(_slotCam == 0)
            return;
        try
        {
            *(Matrix4x4*)(_slotCam + 0x10) = _savedView;
            *(Vector3*)(_slotCam + 0x90) = _savedOrigin;
            *(byte*)(_slotCam + 0x205) = _saved205;
        }
        catch
        {
        }
        _slotCam = 0;
        _camera = 0;
    }

    // The slot as the game had it.
    private static void Restore()
    {
        if(_usedSlot < 0)
            return;
        try
        {
            nint views = Views(out var manager);
            if(views != 0)
            {
                *(int*)CountAt(manager, _usedSlot) = _savedCount;
            }
        }
        catch
        {
        }
        RestoreCamera();
        _usedSlot = -1;
        State = "";
        DuringDraw = "";
        LightState = "";
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
        var cm = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
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
    /// <summary>Clears of a picture the size of the borrowed slot's, per frame (the slot being drawn), and whether the
    /// game's chara-view loop saw the slot as wanted last frame (Manager+0x3837c+n).</summary>
    public static float SlotClears;
    public static string LoopSaw = "";
    private static int _slotClears;
    private static uint _slotW, _slotH;

    // A view's texture size (false: not a 2D texture).
    private static bool SizeOf(nint view, out uint width, out uint height)
    {
        width = height = 0;
        if(view == 0)
            return false;
        nint res = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(view, 7))(view, &res);
        if(res == 0)
            return false;
        try
        {
            uint dim = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint*, void>)V(res, 7))(res, &dim);
            if(dim != 3)
                return false;
            uint* desc = stackalloc uint[11];
            ((delegate* unmanaged[Stdcall]<nint, uint*, void>)V(res, 10))(res, desc);
            width = desc[0];
            height = desc[1];
            return true;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)V(res, 2))(res);
        }
    }
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
            if(_slotW > 0 && SizeOf(rtv, out var w, out var h) && w == _slotW && h == _slotH)
                _slotClears++;
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
            if(_slotW > 0 && SizeOf(dsv, out var w, out var h) && w == _slotW && h == _slotH)
                _slotClears++;
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
        var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
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
        SlotClears += (_slotClears - SlotClears) * k;
        _slotClears = 0;
        try
        {
            SlotPicture(Slot, out var sw, out var sh);
            _slotW = (uint)Math.Max(sw, 0);
            _slotH = (uint)Math.Max(sh, 0);
            nint views = Views(out var manager);
            if(views != 0)
            {
                var sb = new StringBuilder();
                for(int n = 0; n < 8; n++)
                    sb.Append(*(byte*)(manager + 0x3837c + n) != 0 ? '1' : '0');
                nint sc = *(nint*)SlotCameraAt(views, Slot);
                string where = "";
                if(SafeMemory.Plausible(sc))
                {
                    var m = SafeMemory.Read<Matrix4x4>(sc + 0x10, default);
                    m.M14 = m.M24 = m.M34 = 0;
                    m.M44 = 1;
                    if(Matrix4x4.Invert(m, out var w))
                        where = $" at ({w.M41:0.0}, {w.M42:0.0}, {w.M43:0.0})";
                }
                LoopSaw = $"slots drawn by the game last frame: {sb} (0-7); slot {Slot}: count {*(int*)CountAt(manager, Slot)}, camera {(sc == _camera && _camera != 0 ? "the slot's own, moved by Limber" : $"0x{sc:X} (the one Limber moves is 0x{_camera:X})")}{where} (at the present); {DuringDraw}; {LightState}";
            }
        }
        catch
        {
        }
        _rtvClears = 0;
        _dsvClears = 0;
        _mainClears = 0;
    }

    public static void Dispose()
    {
        On = false;
        Restore();
        try { _render?.Dispose(); } catch { }
        _render = null;
        try { _clearRtv?.Dispose(); } catch { }
        try { _clearDsv?.Dispose(); } catch { }
        _clearRtv = null;
        _clearDsv = null;
        // (the camera copy is left allocated: a frame being drawn may still point at it)
    }
}
