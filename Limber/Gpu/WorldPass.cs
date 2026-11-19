using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Limber.Core;

namespace Limber.Gpu;

/// <summary>
/// Research: the whole world drawn a second time from a camera prop, by the game's own world drawing. Preview slots only
/// draw objects bound to them (DrawObject vfuncs +0xf0/+0xf8 hold a slot handle), so the room can't go there; the world
/// is only ever drawn as view 30. So Render::Manager.RenderView (0x1402baa50) is hooked, and for view 30 it's called
/// twice: first with the main render camera's view matrix moved to the camera prop, then as usual. On the render thread
/// the main depth is cleared once per world drawing, so at its second clear in a frame the first (the prop's) drawing is
/// finished: the picture it left (by default RenderTargetManager +0x68) is copied out then, into a texture of Limber's own.
/// </summary>
public static unsafe class WorldPass
{
    public static bool On;
    /// <summary>Also run the camera's own update (vfunc +0x30) after moving it (and after putting it back).</summary>
    public static bool UpdateCamera;
    /// <summary>Where the prop's picture is copied from: RenderTargetManager offset of a Kernel::Texture*.</summary>
    public static int SourceOffset = 0x68;
    public static string Error = "";
    public static int Doubled;
    public static int Captured;

    private delegate void RenderViewDelegate(nint manager, byte flag, int view);
    private const string RenderViewSig = "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 54 41 55 41 56 41 57 48 83 EC 20 44 8B 0D ?? ?? ?? ?? 45 8B E0";
    private static Hook<RenderViewDelegate>? _renderView;
    private static bool _tried;
    private static bool _inside;

    public static void Ensure()
    {
        ViewExperiment.StartCounting();
        if(_tried)
            return;
        _tried = true;
        try
        {
            if(!Plugin.SigScanner.TryScanText(RenderViewSig, out var at))
            {
                Error = "couldn't find the game's view drawing function";
                return;
            }
            _renderView = Plugin.GameInterop.HookFromAddress<RenderViewDelegate>(at, RenderViewDetour);
            _renderView.Enable();
        }
        catch(Exception e)
        {
            Error = "couldn't hook the view drawing: " + e.Message;
            _renderView = null;
        }
    }

    private static void RenderViewDetour(nint manager, byte flag, int view)
    {
        if(On && view == 30 && !_inside)
        {
            nint cam = 0;
            Matrix4x4 saved = default;
            bool moved = false;
            try
            {
                var prop = ViewExperiment.Pick();
                nint views = manager + 0x8;
                cam = *(nint*)(views + 30 * 0x190 + 0x10 + 12 * 0x18 + 0x10);
                if(prop != null && SafeMemory.Plausible(cam) && ViewExperiment.RenderViewFor(prop, out var render))
                {
                    saved = *(Matrix4x4*)(cam + 0x10);
                    *(Matrix4x4*)(cam + 0x10) = render;
                    if(UpdateCamera)
                        UpdateOf(cam);
                    moved = true;
                }
            }
            catch(Exception e)
            {
                Error = e.Message;
            }
            if(moved)
            {
                _inside = true;
                try
                {
                    _renderView!.Original(manager, flag, view);
                    Doubled++;
                }
                finally
                {
                    _inside = false;
                    *(Matrix4x4*)(cam + 0x10) = saved;
                    if(UpdateCamera)
                        UpdateOf(cam);
                }
            }
        }
        _renderView!.Original(manager, flag, view);
    }

    private static void UpdateOf(nint cam)
    {
        if(*(byte*)(cam + 0x205) != 0)
            return;
        ((delegate* unmanaged<nint, void>)((nint*)*(nint*)cam)[6])(cam);
    }

    // ---- Render thread: copies at a boundary. The game clears some targets once per world drawing (inside RenderView)
    // and others once a frame; Boundary is a resource (ID3D11Resource*) cleared once per drawing: at its second clear
    // in a frame the first drawing (the prop's) is finished, and the Sources (ID3D11Texture2D*) are copied into
    // Limber's own textures, Captures[i]. With no Boundary set, the main depth's second clear is used.

    public static nint Boundary;
    public static readonly nint[] Sources = new nint[3];
    public static readonly nint[] Captures = new nint[3];
    private static readonly nint[] CaptureViews = new nint[3];
    private static readonly uint[] CapW = new uint[3], CapH = new uint[3], CapF = new uint[3];
    public static nint CaptureTexture => Captures[0];
    public static nint CaptureView => CaptureViews[0];
    public static uint CaptureWidth => CapW[0];
    public static uint CaptureHeight => CapH[0];

    private static int _clearsThisFrame, _boundaryThisFrame;
    public static int LastClears, LastBoundary;

    /// <summary>From ViewExperiment's clear hooks: the resource behind every cleared view, before it's cleared.</summary>
    public static void OnClear(nint ctx, nint view, bool mainDepth)
    {
        if(mainDepth)
            _clearsThisFrame++;
        if(!On)
            return;
        bool hit;
        if(Boundary != 0)
        {
            nint res = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(view, 7))(view, &res);
            bool same = res == Boundary;
            if(res != 0)
                ((delegate* unmanaged[Stdcall]<nint, uint>)V(res, 2))(res);
            if(!same)
                return;
            hit = ++_boundaryThisFrame == 2;
        }
        else
            hit = mainDepth && _clearsThisFrame == 2;
        if(!hit)
            return;
        try
        {
            if(Sources[0] == 0)
            {
                var rtm = (nint)FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
                nint tex = rtm == 0 ? 0 : SafeMemory.ReadPointer(rtm + SourceOffset);
                if(SafeMemory.Plausible(tex))
                    Copy(ctx, 0, SafeMemory.ReadPointer(tex + 0x68));
            }
            for(int i = 0; i < Sources.Length; i++)
                if(Sources[i] != 0)
                    Copy(ctx, i, Sources[i]);
            Captured++;
        }
        catch(Exception e)
        {
            Error = e.Message;
        }
    }

    private static void Copy(nint ctx, int i, nint src)
    {
        if(!SafeMemory.Plausible(src))
            return;
        Desc d;
        ((delegate* unmanaged[Stdcall]<nint, Desc*, void>)V(src, 10))(src, &d);
        if(d.SampleCount != 1)
        {
            Error = $"source {i} is multisampled";
            return;
        }
        if(Captures[i] == 0 || d.Width != CapW[i] || d.Height != CapH[i] || d.Format != CapF[i])
            Make(i, d);
        if(Captures[i] == 0)
            return;
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, void*, void>)V(ctx, 46))(ctx, Captures[i], 0, 0, 0, 0, src, 0, null);
    }

    /// <summary>Once a frame (Present).</summary>
    public static void Frame()
    {
        LastClears = _clearsThisFrame;
        LastBoundary = _boundaryThisFrame;
        _clearsThisFrame = 0;
        _boundaryThisFrame = 0;
    }

    private static nint V(nint obj, int index) => ((nint*)*(nint*)obj)[index];

    private static void Release(ref nint obj)
    {
        if(obj != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)V(obj, 2))(obj);
        obj = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Desc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    private static void Make(int i, Desc d)
    {
        Release(ref CaptureViews[i]);
        Release(ref Captures[i]);
        var kernel = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        nint device = (nint)kernel->D3D11Forwarder;
        var nd = new Desc
        {
            Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format, SampleCount = 1,
            Usage = 0, BindFlags = 0x8 /* shader resource */,
        };
        nint t = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Desc*, void*, nint*, int>)V(device, 5))(device, &nd, null, &t);
        if(hr < 0 || t == 0)
        {
            // typeless depth formats can't always be shader resources: try without
            nd.BindFlags = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint, Desc*, void*, nint*, int>)V(device, 5))(device, &nd, null, &t);
            if(hr < 0 || t == 0)
            {
                Error = $"couldn't make capture {i} ({hr:X8}, format {d.Format})";
                return;
            }
        }
        Captures[i] = t;
        if(nd.BindFlags != 0)
        {
            uint* srvDesc = stackalloc uint[6];
            srvDesc[0] = Typed(d.Format);
            srvDesc[1] = 4; // TEXTURE2D
            srvDesc[2] = 0;
            srvDesc[3] = 1;
            srvDesc[4] = 0;
            srvDesc[5] = 0;
            nint v = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 7))(device, t, srvDesc, &v);
            if(hr >= 0)
                CaptureViews[i] = v;
        }
        CapW[i] = d.Width;
        CapH[i] = d.Height;
        CapF[i] = d.Format;
    }

    // Typeless formats to a typed one a shader can read.
    private static uint Typed(uint f) => f switch
    {
        27 => 28,
        90 => 87,
        9 => 10,
        23 => 24,
        1 => 2,
        _ => f,
    };

    public static void Dispose()
    {
        On = false;
        try { _renderView?.Dispose(); } catch { }
        _renderView = null;
        for(int i = 0; i < Captures.Length; i++)
        {
            Release(ref CaptureViews[i]);
            Release(ref Captures[i]);
        }
    }
}
