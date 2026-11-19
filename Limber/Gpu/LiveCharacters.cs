using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Limber.Core;
using Limber.Rig;

namespace Limber.Gpu;

/// <summary>
/// The monitors' live layer: the characters near the player, drawn again from the live camera prop every frame, over
/// the room that camera saw when its picture was last taken (MonitorRenderer, taken with the characters hidden).
///
/// How: as the game draws a chosen character into its g-buffer (EffectRenderer's draw hook spots the draws), the same
/// draw is run once more into Limber's own copies of those g-buffer targets and of the depth (started each frame from
/// the room picture's depth, so furniture hides them), with the game's camera constant buffer swapped for a copy whose
/// matrices are the camera prop's (the game's character shaders read the camera from one buffer: view at byte 0,
/// view x projection at 0x60, projection at 0x120 ..., found with CbProbe). At present the g-buffer copies are lit
/// simply into one picture with coverage in alpha, which the screens lay over the room.
/// </summary>
public static unsafe class LiveCharacters
{
    public static RigSettings? Config;

    // ---- Main thread: who, and from where

    /// <summary>The characters drawn live (address, object index), read by EffectRenderer when it picks draws.</summary>
    public static volatile List<(nint Address, ushort Index)> Targets = new();
    /// <summary>The camera prop drawn live (an index into the camera props), -1: none.</summary>
    public static volatile int LiveCamera = -1;
    public static bool On => Config != null && Config.MonitorsOn && Config.MonitorLiveCharacters;

    private sealed class Prop
    {
        public Matrix4x4 View;
        public float Zoom = 1f;
        public int Camera;
    }

    private static volatile Prop? _prop;

    /// <summary>Framework thread: the live camera, its matrices, the characters.</summary>
    public static void Update()
    {
        ShowAfterGrab();
        if(!On || !CameraRig.Live)
        {
            LiveCamera = -1;
            _prop = null;
            if(Targets.Count > 0)
                Targets = new();
            return;
        }
        CameraProp? cam = null;
        foreach(var m in CameraRig.Monitors)
            if(m.On && CameraRig.CameraOf(m) is { } c)
            {
                cam = c;
                break;
            }
        if(cam == null)
        {
            LiveCamera = -1;
            _prop = null;
            return;
        }
        int index = CameraRig.Cameras.IndexOf(cam);
        LiveCamera = index;
        if(ViewExperiment.RenderViewFor(cam, out var view))
        {
            view.M14 = view.M24 = view.M34 = 0;
            view.M44 = 1;
            float zoom = 1f;
            var cm = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
            if(cam.FoV > 1f && cm != null && cm->CurrentCamera != null && cm->CurrentCamera->RenderCamera != null && cm->CurrentCamera->RenderCamera->FoV > 0.01f)
                zoom = MathF.Tan(cm->CurrentCamera->RenderCamera->FoV * 0.5f) / MathF.Tan(cam.FoV * MathF.PI / 360f);
            _prop = new Prop { View = view, Zoom = zoom, Camera = index };
        }
        // the characters: the player and the ones close by
        var list = new List<(nint, ushort)>();
        var me = ActorSkeleton.GetObject(0);
        var at = me != null ? new Vector3(me->Position.X, me->Position.Y, me->Position.Z) : Vector3.Zero;
        for(ushort i = 0; i < 200 && list.Count < 12; i++)
        {
            var go = ActorSkeleton.GetObject(i);
            if(go == null || go->DrawObject == null)
                continue;
            var kind = (byte)go->ObjectKind;
            if(kind is not (1 or 2 or 3 or 9)) // players, battle and event NPCs, companions
                continue;
            if(Vector3.DistanceSquared(new Vector3(go->Position.X, go->Position.Y, go->Position.Z), at) > 30f * 30f)
                continue;
            list.Add(((nint)go, i));
        }
        Targets = list;
    }

    // ---- The room picture's frame: the characters kept out of it (hidden for that one frame)

    private static readonly List<nint> Hidden = new();

    /// <summary>Just before the game draws a camera prop's frame (CameraRig.ApplyView).</summary>
    public static void HideForGrab()
    {
        if(!On)
            return;
        foreach(var (address, index) in Targets)
        {
            var go = ActorSkeleton.GetObject(index);
            if(go == null || (nint)go != address || go->DrawObject == null)
                continue;
            Hide(go->DrawObject);
            // (what's attached: weapons, hats, ornaments)
            var first = go->DrawObject->ChildObject;
            var child = first;
            for(int n = 0; child != null && n < 32; n++)
            {
                Hide((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject*)child);
                child = child->NextSiblingObject;
                if(child == first)
                    break;
            }
        }
    }

    private static void Hide(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject* d)
    {
        if(d == null || !d->IsVisible)
            return;
        d->IsVisible = false;
        Hidden.Add((nint)d);
    }

    /// <summary>The next frame: everything hidden for the room picture shown again.</summary>
    public static void ShowAfterGrab()
    {
        foreach(var d in Hidden)
            try
            {
                ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject*)d)->IsVisible = true;
            }
            catch
            {
            }
        Hidden.Clear();
    }

    // ---- Render thread

    private static nint V(nint obj, int index) => ((nint*)*(nint*)obj)[index];

    private static void Release(nint obj)
    {
        if(obj != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)V(obj, 2))(obj);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc
    {
        public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped
    {
        public void* Data;
        public uint RowPitch, DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Viewport
    {
        public float X, Y, Width, Height, MinDepth, MaxDepth;
    }

    private const int MaxTargets = 8;
    private static readonly nint[] Tex = new nint[MaxTargets], Rtv = new nint[MaxTargets], Srv = new nint[MaxTargets];
    private static readonly uint[] TexW = new uint[MaxTargets], TexH = new uint[MaxTargets], TexF = new uint[MaxTargets];
    private static nint _bgDepth, _workDepth, _workDsv;
    private static uint _depthW, _depthH, _depthF;
    private static bool _hasBackground;
    private static int _bgCamera = -1;
    private static bool _frameStarted, _drewThisFrame;
    private static int _targetCount;
    public static int Replays, LastReplays;
    public static string Error = "";
    /// <summary>Which of the g-buffer copies holds the colour, and which the normals (set from the console).</summary>
    public static int AlbedoIndex = 1, NormalIndex;

    /// <summary>For the console: Limber's g-buffer copies and depth.</summary>
    public static nint TargetTexture(int i) => i >= 0 && i < MaxTargets ? Tex[i] : 0;
    public static nint WorkDepth => _workDepth;
    public static nint LiveTexture => _live;

    private static nint _gameCamCb, _ourCb, _stage;
    private static uint _cbBytes;
    private static bool _stagePending, _cbReady;
    private static readonly float[] CbFloats = new float[1024];

    /// <summary>EffectRenderer's draw hook, right after the game drew a chosen character: that draw again from the prop.</summary>
    /// <summary>Why draws weren't run again (for the console): no camera / no room picture / not the g-buffer pass /
    /// no camera buffer / targets / camera buffer not ready; and how many came at all.</summary>
    public static readonly int[] Skips = new int[8];
    public static int Calls, Backgrounds, LastTargetCount;

    public static void Replay(nint c, Action draw)
    {
        Calls++;
        if(LiveCamera < 0 || _prop == null)
        {
            Skips[0]++;
            return;
        }
        if(!_hasBackground || _bgCamera != LiveCamera)
        {
            Skips[1]++;
            return;
        }
        nint* rtvs = stackalloc nint[8];
        nint dsv = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint*, void>)V(c, 89))(c, 8, rtvs, &dsv);
        nint* vsCb = stackalloc nint[14];
        nint* psCb = stackalloc nint[14];
        bool gotCbs = false;
        try
        {
            int n = 0;
            while(n < 8 && rtvs[n] != 0)
                n++;
            // the g-buffer pass only: several targets and the main depth
            LastTargetCount = n;
            if(n < 2 || n > MaxTargets || dsv == 0 || !IsMainDepth(dsv))
            {
                Skips[2]++;
                return;
            }
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 72))(c, 0, 14, vsCb);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 77))(c, 0, 14, psCb);
            gotCbs = true;
            nint camCb = vsCb[0];
            if(camCb == 0)
            {
                Skips[3]++;
                return;
            }
            if(!EnsureTargets(rtvs, n, dsv))
            {
                Skips[4]++;
                return;
            }
            if(!_frameStarted)
            {
                _frameStarted = true;
                StartFrame(c, camCb);
            }
            if(!_cbReady)
            {
                Skips[5]++;
                return;
            }
            // the camera swapped wherever the game's camera buffer is bound
            nint* vsNew = stackalloc nint[14];
            nint* psNew = stackalloc nint[14];
            for(int i = 0; i < 14; i++)
            {
                vsNew[i] = vsCb[i] == camCb ? _ourCb : vsCb[i];
                psNew[i] = psCb[i] == camCb ? _ourCb : psCb[i];
            }
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 7))(c, 0, 14, vsNew);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 0, 14, psNew);
            nint* ours = stackalloc nint[MaxTargets];
            for(int i = 0; i < n; i++)
                ours[i] = Rtv[i];
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, (uint)n, ours, _workDsv);
            _targetCount = n;
            draw();
            _drewThisFrame = true;
            Replays++;
            // back as the game had it
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 8, rtvs, dsv);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 7))(c, 0, 14, vsCb);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 0, 14, psCb);
        }
        catch(Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            for(int i = 0; i < 8; i++)
                Release(rtvs[i]);
            Release(dsv);
            if(gotCbs)
                for(int i = 0; i < 14; i++)
                {
                    Release(vsCb[i]);
                    Release(psCb[i]);
                }
        }
    }

    private static bool IsMainDepth(nint dsv)
    {
        var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
        if(rtm == null || rtm->DepthStencil == null || rtm->DepthStencil->D3D11Texture2D == null)
            return false;
        nint res = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(dsv, 7))(dsv, &res);
        bool main = res != 0 && res == (nint)rtm->DepthStencil->D3D11Texture2D;
        Release(res);
        return main;
    }

    private static nint Device()
    {
        var d = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        return d == null ? 0 : (nint)d->D3D11Forwarder;
    }

    // Limber's copies of the game's g-buffer targets (same formats and size) and of its depth.
    private static bool EnsureTargets(nint* rtvs, int n, nint dsv)
    {
        nint device = Device();
        for(int i = 0; i < n; i++)
        {
            nint res = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(rtvs[i], 7))(rtvs[i], &res);
            if(res == 0)
                return false;
            Texture2DDesc d;
            ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)V(res, 10))(res, &d);
            Release(res);
            uint* rd = stackalloc uint[5];
            ((delegate* unmanaged[Stdcall]<nint, uint*, void>)V(rtvs[i], 8))(rtvs[i], rd);
            if(Tex[i] != 0 && TexW[i] == d.Width && TexH[i] == d.Height && TexF[i] == rd[0])
                continue;
            Release(Srv[i]);
            Release(Rtv[i]);
            Release(Tex[i]);
            Srv[i] = Rtv[i] = Tex[i] = 0;
            var nd = new Texture2DDesc
            {
                Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format, SampleCount = 1,
                Usage = 0, BindFlags = 0x20 | 0x8,
            };
            nint t = 0;
            if(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &nd, null, &t) < 0 || t == 0)
            {
                Error = $"couldn't copy g-buffer target {i} (format {d.Format})";
                return false;
            }
            Tex[i] = t;
            uint* rtvDesc = stackalloc uint[5] { rd[0], 4 /* TEXTURE2D */, 0, 0, 0 };
            nint r = 0;
            if(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 9))(device, t, rtvDesc, &r) < 0 || r == 0)
            {
                Error = $"couldn't draw into g-buffer copy {i}";
                return false;
            }
            Rtv[i] = r;
            uint* srvDesc = stackalloc uint[6] { rd[0], 4, 0, 1, 0, 0 };
            nint s = 0;
            if(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 7))(device, t, srvDesc, &s) >= 0)
                Srv[i] = s;
            TexW[i] = d.Width;
            TexH[i] = d.Height;
            TexF[i] = rd[0];
        }
        // the depth: the main depth's description, a writable view like the game's
        nint dres = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(dsv, 7))(dsv, &dres);
        if(dres == 0)
            return false;
        Texture2DDesc dd;
        ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)V(dres, 10))(dres, &dd);
        Release(dres);
        if(_workDepth != 0 && _depthW == dd.Width && _depthH == dd.Height && _depthF == dd.Format)
            return true;
        Release(_workDsv);
        Release(_workDepth);
        _workDsv = _workDepth = 0;
        var wd = dd;
        wd.MipLevels = 1;
        wd.ArraySize = 1;
        wd.Usage = 0;
        wd.CpuAccessFlags = 0;
        wd.MiscFlags = 0;
        nint w = 0;
        if(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &wd, null, &w) < 0 || w == 0)
        {
            Error = $"couldn't copy the depth (format {dd.Format})";
            return false;
        }
        _workDepth = w;
        uint* gd = stackalloc uint[6];
        ((delegate* unmanaged[Stdcall]<nint, uint*, void>)V(dsv, 8))(dsv, gd);
        uint* dsvDesc = stackalloc uint[6] { gd[0], 3 /* TEXTURE2D */, 0, 0, 0, 0 };
        nint dv = 0;
        if(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 10))(device, w, dsvDesc, &dv) < 0 || dv == 0)
        {
            Error = "couldn't draw into the depth copy";
            return false;
        }
        _workDsv = dv;
        _depthW = dd.Width;
        _depthH = dd.Height;
        _depthF = dd.Format;
        return true;
    }

    // The first replay of a frame: the copies cleared, the depth started from the room picture's, the camera buffer.
    private static void StartFrame(nint c, nint camCb)
    {
        float* zero = stackalloc float[4];
        for(int i = 0; i < MaxTargets; i++)
            if(Rtv[i] != 0)
                ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)V(c, 50))(c, Rtv[i], zero);
        if(_bgDepth != 0)
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)V(c, 47))(c, _workDepth, _bgDepth);
        UpdateCamera(c, camCb);
    }

    // The game's camera buffer read back (a frame late, without waiting), its matrices swapped for the prop's, uploaded.
    private static void UpdateCamera(nint c, nint camCb)
    {
        nint device = Device();
        BufferDesc bd;
        ((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void>)V(camCb, 10))(camCb, &bd);
        if(bd.ByteWidth == 0 || bd.ByteWidth > 4096)
            return;
        if(_ourCb == 0 || _cbBytes != bd.ByteWidth)
        {
            Release(_ourCb);
            Release(_stage);
            _ourCb = _stage = 0;
            _cbReady = _stagePending = false;
            var od = new BufferDesc { ByteWidth = bd.ByteWidth, Usage = 0, BindFlags = 0x4 };
            nint b = 0;
            if(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &od, null, &b) < 0 || b == 0)
            {
                Error = "couldn't make the camera buffer";
                return;
            }
            _ourCb = b;
            var sd = new BufferDesc { ByteWidth = bd.ByteWidth, Usage = 3, CpuAccessFlags = 0x20000 };
            nint s = 0;
            if(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &sd, null, &s) < 0 || s == 0)
            {
                Error = "couldn't make the camera read-back";
                return;
            }
            _stage = s;
            _cbBytes = bd.ByteWidth;
        }
        if(_stagePending)
        {
            Mapped m;
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _stage, 0, 1, 0x100000 /* don't wait */, &m);
            if(hr >= 0)
            {
                int count = (int)_cbBytes / 4;
                Marshal.Copy((nint)m.Data, CbFloats, 0, count);
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _stage, 0);
                _stagePending = false;
                var prop = _prop;
                if(prop != null && Patch(CbFloats, count, prop))
                {
                    fixed(float* f = CbFloats)
                        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void*, void*, uint, uint, void>)V(c, 48))(c, _ourCb, 0, null, f, 0, 0);
                    _cbReady = true;
                }
            }
        }
        if(!_stagePending)
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)V(c, 47))(c, _stage, camCb);
            _stagePending = true;
        }
    }

    public static string LastPatch = "";

    // Every copy of the main camera's matrices (and its position) in the buffer swapped for the prop's, in the same
    // layout (the game keeps them transposed; views as 3 rows).
    private static bool Patch(float[] f, int n, Prop prop)
    {
        if(n < 0x160 / 4)
            return false;
        var mv = new Matrix4x4(f[0], f[4], f[8], 0, f[1], f[5], f[9], 0, f[2], f[6], f[10], 0, f[3], f[7], f[11], 1);
        int p0 = 0x120 / 4;
        var mp = new Matrix4x4(
            f[p0 + 0], f[p0 + 4], f[p0 + 8], f[p0 + 12],
            f[p0 + 1], f[p0 + 5], f[p0 + 9], f[p0 + 13],
            f[p0 + 2], f[p0 + 6], f[p0 + 10], f[p0 + 14],
            f[p0 + 3], f[p0 + 7], f[p0 + 11], f[p0 + 15]);
        if(!Matrix4x4.Invert(mv, out var miv) || !Matrix4x4.Invert(mp, out var mip))
            return false;
        var pv = prop.View;
        var pp = mp;
        pp.M11 *= prop.Zoom;
        pp.M22 *= prop.Zoom;
        if(!Matrix4x4.Invert(pv, out var piv) || !Matrix4x4.Invert(pp, out var pip))
            return false;
        var mvp = mv * mp;
        var pvp = pv * pp;
        if(!Matrix4x4.Invert(mvp, out var mivp) || !Matrix4x4.Invert(pvp, out var pivp))
            return false;
        var kinds = new (Matrix4x4 Main, Matrix4x4 Prop, int Size, string Name)[]
        {
            (mv, pv, 12, "view"), (miv, piv, 12, "inverse view"), (mvp, pvp, 16, "view-projection"),
            (mivp, pivp, 16, "inverse view-projection"), (mp, pp, 16, "projection"), (mip, pip, 16, "inverse projection"),
        };
        var done = new bool[n];
        var log = new StringBuilder();
        foreach(var (main, propM, size, name) in kinds)
            for(int order = 0; order < 2; order++)
            {
                var want = Floats(order == 0 ? Matrix4x4.Transpose(main) : main);
                var put = Floats(order == 0 ? Matrix4x4.Transpose(propM) : propM);
                for(int at = 0; at + size <= n; at++)
                {
                    if(done[at] || !Match(f, at, want, 12))
                        continue;
                    if(size == 16 && !Match(f, at, want, 16))
                        continue;
                    for(int k = 0; k < size; k++)
                    {
                        f[at + k] = put[k];
                        done[at + k] = true;
                    }
                    log.Append($"{name}{(order == 1 ? " (as is)" : "")} at 0x{at * 4:X}; ");
                }
            }
        // the camera's position, wherever it's on its own
        var eye = new Vector3(miv.M41, miv.M42, miv.M43);
        var peye = new Vector3(piv.M41, piv.M42, piv.M43);
        for(int at = 0; at + 3 <= n; at++)
        {
            if(done[at] || done[at + 1] || done[at + 2])
                continue;
            if(Near(f[at], eye.X) && Near(f[at + 1], eye.Y) && Near(f[at + 2], eye.Z) && eye.LengthSquared() > 1e-4f)
            {
                f[at] = peye.X;
                f[at + 1] = peye.Y;
                f[at + 2] = peye.Z;
                done[at] = done[at + 1] = done[at + 2] = true;
                log.Append($"position at 0x{at * 4:X}; ");
            }
        }
        LastPatch = log.ToString();
        return true;
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) <= 1e-4f + 1e-5f * MathF.Abs(b);

    private static float[] Floats(Matrix4x4 m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };

    private static bool Match(float[] f, int at, float[] want, int count)
    {
        int nonzero = 0;
        for(int i = 0; i < count; i++)
        {
            float a = f[at + i], b = want[i];
            if(MathF.Abs(a - b) > 1e-4f + 1e-4f * MathF.Abs(b))
                return false;
            if(b != 0)
                nonzero++;
        }
        return nonzero >= 4;
    }

    // ---- The room picture taken (MonitorRenderer, at the present of a camera prop's frame): its depth kept

    public static void TakeBackground(nint c, int camera)
    {
        if(!On || camera != LiveCamera)
            return;
        try
        {
            var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
            if(rtm == null || rtm->DepthStencil == null || rtm->DepthStencil->D3D11Texture2D == null)
                return;
            nint src = (nint)rtm->DepthStencil->D3D11Texture2D;
            Texture2DDesc d;
            ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)V(src, 10))(src, &d);
            if(d.SampleCount != 1)
                return;
            if(_bgDepth != 0)
            {
                Texture2DDesc have;
                ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)V(_bgDepth, 10))(_bgDepth, &have);
                if(have.Width != d.Width || have.Height != d.Height || have.Format != d.Format)
                {
                    Release(_bgDepth);
                    _bgDepth = 0;
                }
            }
            if(_bgDepth == 0)
            {
                var nd = d;
                nd.MipLevels = 1;
                nd.ArraySize = 1;
                nd.Usage = 0;
                nd.BindFlags = 0;
                nd.CpuAccessFlags = 0;
                nd.MiscFlags = 0;
                nint t = 0;
                if(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(Device(), 5))(Device(), &nd, null, &t) < 0 || t == 0)
                {
                    Error = "couldn't keep the room's depth";
                    return;
                }
                _bgDepth = t;
            }
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)V(c, 47))(c, _bgDepth, src);
            _hasBackground = true;
            _bgCamera = camera;
            Backgrounds++;
        }
        catch(Exception e)
        {
            Error = e.Message;
        }
    }

    /// <summary>The live camera's room picture is gone (its camera moved, or changed): no characters until the next.</summary>
    public static void Forget()
    {
        _hasBackground = false;
    }

    // ---- At present: the g-buffer copies lit into one picture (colour, coverage in alpha) for the screens

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(byte* src, nuint size, byte* name, void* defines, void* include, byte* entry, byte* target, uint flags1, uint flags2, nint* code, nint* errors);

    private const string Hlsl = @"
Texture2D<float4> A : register(t0);
Texture2D<float4> N : register(t1);
cbuffer C : register(b0) { float4 P; };
float4 Vs(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 Ps(float4 pos : SV_Position) : SV_Target0
{
    int3 p = int3(pos.xy, 0);
    float4 a = A.Load(p);
    float4 n = N.Load(p);
    bool hit = any(n != 0) || any(a != 0);
    if (!hit) return float4(0, 0, 0, 0);
    float3 nn = n.xyz * 2 - 1;
    float l = dot(nn, nn) > 0.01 ? saturate(dot(normalize(nn), normalize(float3(0.3, 0.8, 0.5)))) : 0.6;
    float3 c = a.rgb * (P.y + (1 - P.y) * l) * P.x;
    return float4(c, 1);
}
";

    private static nint _vs, _ps, _cb, _live, _liveRtv, _liveSrv;
    private static uint _liveW, _liveH;
    private static bool _shadersFailed;
    public static float Brightness = 1.1f, Ambient = 0.55f;

    private static nint Compile(string entry, string target)
    {
        var src = Encoding.ASCII.GetBytes(Hlsl);
        var e = Encoding.ASCII.GetBytes(entry + "\0");
        var t = Encoding.ASCII.GetBytes(target + "\0");
        nint code = 0, errors = 0;
        int hr;
        fixed(byte* s = src)
        fixed(byte* ep = e)
        fixed(byte* tp = t)
            hr = D3DCompile(s, (nuint)src.Length, null, null, null, ep, tp, 0, 0, &code, &errors);
        if(hr < 0 || code == 0)
        {
            string message = errors != 0 ? Marshal.PtrToStringAnsi(((delegate* unmanaged[Stdcall]<nint, nint>)V(errors, 3))(errors)) ?? "" : "";
            Release(errors);
            throw new InvalidOperationException($"the live layer's shader didn't compile: {message}");
        }
        Release(errors);
        return code;
    }

    private static void Setup()
    {
        nint device = Device();
        nint vsCode = Compile("Vs", "vs_5_0"), psCode = Compile("Ps", "ps_5_0");
        try
        {
            nint p = ((delegate* unmanaged[Stdcall]<nint, nint>)V(vsCode, 3))(vsCode);
            nuint s = ((delegate* unmanaged[Stdcall]<nint, nuint>)V(vsCode, 4))(vsCode);
            nint vs = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)V(device, 12))(device, p, s, 0, &vs);
            _vs = vs;
            p = ((delegate* unmanaged[Stdcall]<nint, nint>)V(psCode, 3))(psCode);
            s = ((delegate* unmanaged[Stdcall]<nint, nuint>)V(psCode, 4))(psCode);
            nint ps = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)V(device, 15))(device, p, s, 0, &ps);
            _ps = ps;
        }
        finally
        {
            Release(vsCode);
            Release(psCode);
        }
        var cd = new BufferDesc { ByteWidth = 16, Usage = 2, BindFlags = 0x4, CpuAccessFlags = 0x10000 };
        nint cb = 0;
        ((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &cd, null, &cb);
        _cb = cb;
        if(_vs == 0 || _ps == 0 || _cb == 0)
            throw new InvalidOperationException("the live layer's shaders couldn't be made");
    }

    /// <summary>Whether this frame has a live picture (for the screens), made here (state saved by the caller).</summary>
    public static bool Compose(nint c)
    {
        bool drew = _drewThisFrame;
        LastReplays = Replays;
        Replays = 0;
        _drewThisFrame = false;
        _frameStarted = false;
        if(!drew || _shadersFailed || Srv[AlbedoIndex % MaxTargets] == 0)
            return false;
        try
        {
            if(_vs == 0)
                Setup();
            uint w = TexW[0], h = TexH[0];
            if(_live == 0 || _liveW != w || _liveH != h)
            {
                Release(_liveSrv);
                Release(_liveRtv);
                Release(_live);
                _live = _liveRtv = _liveSrv = 0;
                var d = new Texture2DDesc { Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = 28, SampleCount = 1, Usage = 0, BindFlags = 0x20 | 0x8 };
                nint t = 0, r = 0, s = 0;
                ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(Device(), 5))(Device(), &d, null, &t);
                if(t == 0)
                    return false;
                ((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(Device(), 9))(Device(), t, null, &r);
                ((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(Device(), 7))(Device(), t, null, &s);
                _live = t;
                _liveRtv = r;
                _liveSrv = s;
                _liveW = w;
                _liveH = h;
            }
            Mapped m;
            if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _cb, 0, 4, 0, &m) >= 0)
            {
                *(Vector4*)m.Data = new Vector4(Brightness, Ambient, 0, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _cb, 0);
            }
            nint rtv = _liveRtv;
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rtv, 0);
            var vp = new Viewport { Width = w, Height = h, MinDepth = 0, MaxDepth = 1 };
            ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(c, 44))(c, 1, &vp);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 17))(c, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, void>)V(c, 24))(c, 4);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 43))(c, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, 0, 0);
            float* f = stackalloc float[4];
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, 0, f, 0xFFFFFFFF);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vs, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _ps, null, 0);
            nint cb = _cb;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 0, 1, &cb);
            nint* srvs = stackalloc nint[2] { Srv[AlbedoIndex % MaxTargets], Srv[NormalIndex % MaxTargets] };
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 2, srvs);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 3, 0);
            nint* none = stackalloc nint[2];
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 2, none);
            return _liveSrv != 0;
        }
        catch(Exception e)
        {
            _shadersFailed = true;
            Error = e.Message;
            return false;
        }
    }

    public static nint LiveView => _liveSrv;

    public static void Dispose()
    {
        ShowAfterGrab();
        for(int i = 0; i < MaxTargets; i++)
        {
            Release(Srv[i]);
            Release(Rtv[i]);
            Release(Tex[i]);
            Srv[i] = Rtv[i] = Tex[i] = 0;
        }
        Release(_workDsv);
        Release(_workDepth);
        Release(_bgDepth);
        Release(_ourCb);
        Release(_stage);
        Release(_liveSrv);
        Release(_liveRtv);
        Release(_live);
        Release(_vs);
        Release(_ps);
        Release(_cb);
        _workDsv = _workDepth = _bgDepth = _ourCb = _stage = _liveSrv = _liveRtv = _live = _vs = _ps = _cb = 0;
        _hasBackground = false;
    }
}
