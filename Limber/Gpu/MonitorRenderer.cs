using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Limber.Core;
using Limber.Rig;

namespace Limber.Gpu;

/// <summary>
/// Monitors (Camera page): flat screens in the world showing what a camera prop sees. On a frame the game drew from a
/// camera prop (Rig/CameraRig moved the game camera there for that one frame), the finished picture is copied into that
/// camera's picture and the last normal frame is put back on screen; on normal frames the monitors are drawn into the
/// world (hidden behind anything in front of them, by the game's depth) and the frame is kept for next time.
/// Render thread, before the plugin windows; every piece of the device's state it changes is put back.
/// </summary>
public static unsafe class MonitorRenderer
{
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RtBlend
    {
        public int BlendEnable, Src, Dest, Op, SrcAlpha, DestAlpha, OpAlpha;
        public uint WriteMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendDesc
    {
        public int AlphaToCoverage, Independent;
        public RtBlend RT0, RT1, RT2, RT3, RT4, RT5, RT6, RT7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StencilOp
    {
        public int Fail, DepthFail, Pass, Func;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DepthDesc
    {
        public int DepthEnable, WriteMask, Func, StencilEnable;
        public uint StencilMasks;
        public StencilOp Front, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RasterDesc
    {
        public int Fill, Cull, FrontCcw, DepthBias;
        public float DepthBiasClamp, SlopeScaledDepthBias;
        public int DepthClip, Scissor, Multisample, AntialiasedLine;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SamplerDesc
    {
        public int Filter, U, Vv, W;
        public float MipLodBias;
        public uint MaxAnisotropy;
        public int Comparison;
        public float B0, B1, B2, B3, MinLod, MaxLod;
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(byte* src, nuint size, byte* name, void* defines, void* include, byte* entry, byte* target, uint flags1, uint flags2, nint* code, nint* errors);

    private const uint BindSrv = 0x8, BindConstant = 0x4, UsageDefault = 0, UsageDynamic = 2, CpuWrite = 0x10000;

    private static bool _failed;
    private static string _error = "";
    private static nint _vs, _ps, _cb, _blend, _depthOff, _raster, _sampler, _samplerPoint;
    private static nint _last, _lastSrv;
    private static uint _w, _h, _format;
    private static readonly Dictionary<int, (nint Tex, nint Srv)> Feeds = new();
    private static bool _haveLast;
    private static bool _liveNow;

    public static string Error => _error;

    public static void Retry()
    {
        _failed = false;
        _error = "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProj;
        public Vector4 C0, C1, C2, C3;   // the screen's corners (bottom left, bottom right, top right, top left)
        public Vector4 UvRect;           // the picture: scale (x, y), offset (z, w)
        public Vector4 Screen;           // w, h, 1/w, 1/h
        public Vector4 DepthUv;          // depth uv scale (x, y), depth runs backwards, has a picture
        public Vector4 Look;             // brightness, bezel (of the width), aspect, back side
        public Vector4 BezelColour;
        public Vector4 Time;             // seconds, 0, 0, 0
    }

    private const string Hlsl = @"
cbuffer C : register(b0)
{
    row_major float4x4 VP;
    float4 C0; float4 C1; float4 C2; float4 C3;
    float4 UvRect; float4 Screen; float4 DepthUv; float4 Look; float4 BezelColour; float4 Time;
};
Texture2D<float4> Feed : register(t0);
Texture2D<float> SceneDepth : register(t1);
Texture2D<float4> Live : register(t2);
SamplerState Lin : register(s0);
SamplerState Pt : register(s1);

struct VOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VOut VsQuad(uint vid : SV_VertexID)
{
    static const int idx[6] = { 0, 1, 2, 0, 2, 3 };
    static const float2 uvs[4] = { float2(0, 1), float2(1, 1), float2(1, 0), float2(0, 0) };
    int i = idx[vid];
    float4 p = i == 0 ? C0 : i == 1 ? C1 : i == 2 ? C2 : C3;
    VOut o; o.pos = mul(float4(p.xyz, 1), VP); o.uv = uvs[i]; return o;
}

float Hash(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }

float4 PsQuad(VOut i) : SV_Target0
{
    // behind something the game drew: hidden
    float2 suv = i.pos.xy * Screen.zw;
    float scene = SceneDepth.SampleLevel(Pt, suv * DepthUv.xy, 0);
    float me = i.pos.z;
    float tol = 1e-6 + abs(me) * 1e-4;
    if (DepthUv.z > 0.5 ? scene > me + tol : scene < me - tol) discard;
    if (Look.w > 0.5) return float4(BezelColour.rgb * 0.6, 1);
    float bx = Look.y, by = Look.y * Look.z;
    if (i.uv.x < bx || i.uv.x > 1 - bx || i.uv.y < by || i.uv.y > 1 - by) return float4(BezelColour.rgb, 1);
    float2 uv = (i.uv - float2(bx, by)) / (1 - 2 * float2(bx, by));
    if (DepthUv.w < 0.5)
    {
        // no picture yet: static
        float n = Hash(floor(uv * 160) + floor(Time.x * 24));
        return float4(n.xxx * 0.35, 1);
    }
    float3 c = Feed.SampleLevel(Lin, uv * UvRect.xy + UvRect.zw, 0).rgb * Look.x;
    if (Time.y > 0.5)
    {
        // the characters, live, over the room
        float4 l = Live.SampleLevel(Lin, uv * UvRect.xy + UvRect.zw, 0);
        c = lerp(c, l.rgb * Look.x, l.a);
    }
    return float4(c, 1);
}
";

    private static nint Device()
    {
        var d = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        return d == null ? 0 : (nint)d->D3D11Forwarder;
    }

    private static nint Context()
    {
        var d = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        return d == null ? 0 : (nint)d->D3D11DeviceContext;
    }

    private static nint Check(int hr, nint obj, string what)
    {
        if(hr < 0 || obj == 0)
            throw new InvalidOperationException($"{what} couldn't be made ({hr:X8})");
        return obj;
    }

    private static nint Compile(string entry, string target)
    {
        var src = Encoding.ASCII.GetBytes(Hlsl);
        var name = Encoding.ASCII.GetBytes("LimberMonitors\0");
        var e = Encoding.ASCII.GetBytes(entry + "\0");
        var t = Encoding.ASCII.GetBytes(target + "\0");
        nint code = 0, errors = 0;
        int hr;
        fixed(byte* ps = src, pn = name, pe = e, pt = t)
            hr = D3DCompile(ps, (nuint)src.Length, pn, null, null, pe, pt, 1u << 15, 0, &code, &errors);
        if(hr < 0 || code == 0)
        {
            string message = "";
            if(errors != 0)
            {
                var p = ((delegate* unmanaged[Stdcall]<nint, byte*>)V(errors, 3))(errors);
                var n = ((delegate* unmanaged[Stdcall]<nint, nuint>)V(errors, 4))(errors);
                message = Encoding.ASCII.GetString(p, (int)Math.Min(n, 2000));
            }
            Release(errors);
            Release(code);
            throw new InvalidOperationException($"shader {entry}: {message}");
        }
        Release(errors);
        var bytes = ((delegate* unmanaged[Stdcall]<nint, byte*>)V(code, 3))(code);
        var size = ((delegate* unmanaged[Stdcall]<nint, nuint>)V(code, 4))(code);
        nint shader = 0;
        var device = Device();
        int made = target.StartsWith("vs", StringComparison.Ordinal)
            ? ((delegate* unmanaged[Stdcall]<nint, byte*, nuint, nint, nint*, int>)V(device, 12))(device, bytes, size, 0, &shader)
            : ((delegate* unmanaged[Stdcall]<nint, byte*, nuint, nint, nint*, int>)V(device, 15))(device, bytes, size, 0, &shader);
        Release(code);
        if(made < 0 || shader == 0)
            throw new InvalidOperationException($"shader {entry}: couldn't be made ({made:X8})");
        return shader;
    }

    private static void Setup()
    {
        var device = Device();
        if(device == 0)
            throw new InvalidOperationException("no device");
        _vs = Compile("VsQuad", "vs_5_0");
        _ps = Compile("PsQuad", "ps_5_0");
        nint o = 0;
        var cbDesc = new BufferDesc { ByteWidth = (uint)((sizeof(Constants) + 15) / 16 * 16), Usage = UsageDynamic, BindFlags = BindConstant, CpuAccessFlags = CpuWrite };
        _cb = Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &cbDesc, null, &o), o, "constants");
        var b = new BlendDesc { RT0 = new RtBlend { BlendEnable = 0, Src = 2, Dest = 1, Op = 1, SrcAlpha = 2, DestAlpha = 1, OpAlpha = 1, WriteMask = 0x7 } };
        nint bs = 0;
        _blend = Check(((delegate* unmanaged[Stdcall]<nint, BlendDesc*, nint*, int>)V(device, 20))(device, &b, &bs), bs, "blend");
        var d = new DepthDesc
        {
            DepthEnable = 0, WriteMask = 0, Func = 8, StencilEnable = 0, StencilMasks = 0xFFFF,
            Front = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 }, Back = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 },
        };
        nint ds = 0;
        _depthOff = Check(((delegate* unmanaged[Stdcall]<nint, DepthDesc*, nint*, int>)V(device, 21))(device, &d, &ds), ds, "depth state");
        var r = new RasterDesc { Fill = 3, Cull = 1, DepthClip = 1 };
        nint rs = 0;
        _raster = Check(((delegate* unmanaged[Stdcall]<nint, RasterDesc*, nint*, int>)V(device, 22))(device, &r, &rs), rs, "rasterizer");
        var sd = new SamplerDesc { Filter = 0x15, U = 3, Vv = 3, W = 3, MaxAnisotropy = 1, Comparison = 1, MaxLod = float.MaxValue };
        nint ss = 0;
        _sampler = Check(((delegate* unmanaged[Stdcall]<nint, SamplerDesc*, nint*, int>)V(device, 23))(device, &sd, &ss), ss, "sampler");
        var sp = new SamplerDesc { Filter = 0, U = 3, Vv = 3, W = 3, MaxAnisotropy = 1, Comparison = 1, MaxLod = float.MaxValue };
        nint sps = 0;
        _samplerPoint = Check(((delegate* unmanaged[Stdcall]<nint, SamplerDesc*, nint*, int>)V(device, 23))(device, &sp, &sps), sps, "sampler");
    }

    private static (nint Tex, nint Srv) MakePicture(uint w, uint h, uint format)
    {
        var device = Device();
        var desc = new Texture2DDesc { Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1, Usage = UsageDefault, BindFlags = BindSrv };
        nint t = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &desc, null, &t), t, "picture");
        nint v = 0;
        if(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 7))(device, t, null, &v) < 0 || v == 0)
        {
            Release(t);
            throw new InvalidOperationException("picture view couldn't be made");
        }
        return (t, v);
    }

    private static void FreePictures()
    {
        foreach(var (t, v) in Feeds.Values)
        {
            Release(v);
            Release(t);
        }
        Feeds.Clear();
        Release(_lastSrv);
        Release(_last);
        _last = _lastSrv = 0;
        _haveLast = false;
        _w = _h = _format = 0;
    }

    public static void Dispose()
    {
        FreePictures();
        foreach(var o in new[] { _vs, _ps, _cb, _blend, _depthOff, _raster, _sampler, _samplerPoint })
            Release(o);
        _vs = _ps = _cb = _blend = _depthOff = _raster = _sampler = _samplerPoint = 0;
    }

    private static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // ---- The device's state, kept and put back

    private struct Saved
    {
        public fixed long Rtv[8];
        public nint Dsv, Blend, Depth, Raster, Vs, Ps, Layout, VsCb, PsCb;
        public fixed long PsSrv[2];
        public fixed long Samplers[2];
        public fixed float BlendFactor[4];
        public uint SampleMask, StencilRef, Topology, ViewportCount, VsInstances, PsInstances;
        public Viewport Vp0;
        public fixed long VsClass[256];
        public fixed long PsClass[256];
    }

    private static Saved* _saved;

    private static void Save(nint c)
    {
        if(_saved == null)
            _saved = (Saved*)NativeMemory.AllocZeroed((nuint)sizeof(Saved));
        var s = _saved;
        ((delegate* unmanaged[Stdcall]<nint, uint, long*, nint*, void>)V(c, 89))(c, 8, s->Rtv, &s->Dsv);
        ((delegate* unmanaged[Stdcall]<nint, nint*, float*, uint*, void>)V(c, 91))(c, &s->Blend, s->BlendFactor, &s->SampleMask);
        ((delegate* unmanaged[Stdcall]<nint, nint*, uint*, void>)V(c, 92))(c, &s->Depth, &s->StencilRef);
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(c, 94))(c, &s->Raster);
        s->ViewportCount = 1;
        ((delegate* unmanaged[Stdcall]<nint, uint*, Viewport*, void>)V(c, 95))(c, &s->ViewportCount, &s->Vp0);
        s->VsInstances = 256;
        ((delegate* unmanaged[Stdcall]<nint, nint*, long*, uint*, void>)V(c, 76))(c, &s->Vs, s->VsClass, &s->VsInstances);
        s->PsInstances = 256;
        ((delegate* unmanaged[Stdcall]<nint, nint*, long*, uint*, void>)V(c, 74))(c, &s->Ps, s->PsClass, &s->PsInstances);
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(c, 78))(c, &s->Layout);
        ((delegate* unmanaged[Stdcall]<nint, uint*, void>)V(c, 83))(c, &s->Topology);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 72))(c, 0, 1, &s->VsCb);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 77))(c, 0, 1, &s->PsCb);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 73))(c, 0, 2, s->PsSrv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 75))(c, 0, 2, s->Samplers);
    }

    private static void Restore(nint c)
    {
        var s = _saved;
        ((delegate* unmanaged[Stdcall]<nint, uint, long*, nint, void>)V(c, 33))(c, 8, s->Rtv, s->Dsv);
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, s->Blend, s->BlendFactor, s->SampleMask);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, s->Depth, s->StencilRef);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 43))(c, s->Raster);
        if(s->ViewportCount > 0)
            ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(c, 44))(c, 1, &s->Vp0);
        ((delegate* unmanaged[Stdcall]<nint, nint, long*, uint, void>)V(c, 11))(c, s->Vs, s->VsClass, s->VsInstances);
        ((delegate* unmanaged[Stdcall]<nint, nint, long*, uint, void>)V(c, 9))(c, s->Ps, s->PsClass, s->PsInstances);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 17))(c, s->Layout);
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)V(c, 24))(c, s->Topology);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 7))(c, 0, 1, &s->VsCb);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 0, 1, &s->PsCb);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 8))(c, 0, 2, s->PsSrv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 10))(c, 0, 2, s->Samplers);
        for(int i = 0; i < 8; i++)
            Release((nint)s->Rtv[i]);
        for(int i = 0; i < 2; i++)
        {
            Release((nint)s->PsSrv[i]);
            Release((nint)s->Samplers[i]);
        }
        for(uint i = 0; i < s->VsInstances; i++)
            Release((nint)s->VsClass[i]);
        for(uint i = 0; i < s->PsInstances; i++)
            Release((nint)s->PsClass[i]);
        foreach(var o in new[] { s->Dsv, s->Blend, s->Depth, s->Raster, s->Vs, s->Ps, s->Layout, s->VsCb, s->PsCb })
            Release(o);
        NativeMemory.Clear(s, (nuint)sizeof(Saved));
    }

    private static void Copy(nint c, nint to, nint from) => ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)V(c, 47))(c, to, from);

    /// <summary>Once a frame (render thread), after Limber's other effects are drawn into the picture.</summary>
    // Which camera prop each of the last few frames was set up from (-1: the normal camera): the picture on screen is a
    // frame or more behind the camera the game was given (it draws one frame while the next is being set up).
    private static readonly int[] Recent = [-1, -1, -1, -1];
    private static int _recentAt;

    public static void Draw(int delay)
    {
        Recent[_recentAt] = CameraControl.Grabbed;
        CameraControl.Grabbed = -1;
        int grabbed = Recent[(_recentAt - Math.Clamp(delay, 0, 3)) & 3];
        _recentAt = (_recentAt + 1) & 3;
        if(_failed || !CameraRig.Live)
        {
            if(_last != 0)
                FreePictures();
            return;
        }
        var c = Context();
        var device = Device();
        var kernel = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if(c == 0 || device == 0 || kernel == null || kernel->SwapChain == null || kernel->SwapChain->DXGISwapChain == null)
            return;
        nint backTex = 0, backRtv = 0;
        bool saved = false;
        try
        {
            if(_vs == 0)
                Setup();
            var swap = (nint)kernel->SwapChain->DXGISwapChain;
            var iid = Texture2DId;
            nint t = 0;
            if(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)V(swap, 9))(swap, 0, &iid, &t) < 0 || t == 0)
                return;
            backTex = t;
            Texture2DDesc bd;
            ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)V(backTex, 10))(backTex, &bd);
            if(bd.Width == 0 || bd.Height == 0 || bd.SampleCount != 1)
                return;
            if(_last == 0 || _w != bd.Width || _h != bd.Height || _format != bd.Format)
            {
                FreePictures();
                (_last, _lastSrv) = MakePicture(bd.Width, bd.Height, bd.Format);
                _w = bd.Width;
                _h = bd.Height;
                _format = bd.Format;
            }

            Save(c);
            saved = true;
            // (nothing drawing into the picture while it's copied)
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 0, null, 0);

            // 1. A frame from a camera prop: into its picture, and the last normal frame back on screen.
            if(grabbed >= 0 && _haveLast)
            {
                if(!Feeds.TryGetValue(grabbed, out var feed))
                {
                    feed = MakePicture(bd.Width, bd.Height, bd.Format);
                    Feeds[grabbed] = feed;
                }
                Copy(c, feed.Tex, backTex);
                LiveCharacters.TakeBackground(c, grabbed);
                Copy(c, backTex, _last);
                return;
            }

            // 2. A normal frame: the monitors drawn into it, then it's kept.
            nint r = 0;
            if(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 9))(device, backTex, null, &r) >= 0 && r != 0)
            {
                backRtv = r;
                _liveNow = LiveCharacters.Compose(c);
                DrawMonitors(c, backRtv, bd);
            }
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 0, null, 0);
            Copy(c, _last, backTex);
            _haveLast = true;
            _error = "";
        }
        catch(Exception e)
        {
            _failed = true;
            _error = e.Message;
            Plugin.Log.Warning(e, "Limber: the monitors can't be drawn");
        }
        finally
        {
            if(saved)
                Restore(c);
            Release(backRtv);
            Release(backTex);
        }
    }

    private static void DrawMonitors(nint c, nint rtv, Texture2DDesc bd)
    {
        var control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Instance();
        var rtm = RenderTargetManager.Instance();
        if(control == null || rtm == null || rtm->DepthStencil == null || rtm->DepthStencil->D3D11ShaderResourceView == null)
            return;
        var vpm = control->ViewProjectionMatrix;
        var ds = rtm->DepthStencil;
        var depthUv = ds->AllocatedWidth > 0 && ds->AllocatedHeight > 0
            ? new Vector2((float)ds->ActualWidth / ds->AllocatedWidth, (float)ds->ActualHeight / ds->AllocatedHeight) : Vector2.One;
        bool reversed = EffectRenderer.DepthBackwards;
        if(!Matrix4x4.Invert(vpm, out var ivp))
            return;
        var ch = Vector4.Transform(new Vector4(0, 0, 1, 0), ivp);
        var eye = MathF.Abs(ch.W) > 1e-12f ? new Vector3(ch.X, ch.Y, ch.Z) / ch.W : Vector3.Zero;

        nint view = rtv;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &view, 0);
        var vp = new Viewport { Width = bd.Width, Height = bd.Height, MinDepth = 0, MaxDepth = 1 };
        ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(c, 44))(c, 1, &vp);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 17))(c, 0);
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)V(c, 24))(c, 4);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 43))(c, _raster);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _depthOff, 0);
        float* zero = stackalloc float[4];
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blend, zero, 0xFFFFFFFF);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vs, null, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _ps, null, 0);
        nint cb = _cb;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 7))(c, 0, 1, &cb);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 0, 1, &cb);
        nint* samplers = stackalloc nint[2] { _sampler, _samplerPoint };
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 10))(c, 0, 2, samplers);
        float screenAspect = (float)bd.Width / bd.Height;
        nint* inputs = stackalloc nint[3];

        foreach(var m in CameraRig.Monitors)
        {
            if(!m.On)
                continue;
            var cam = CameraRig.CameraOf(m);
            int camIndex = cam == null ? -1 : CameraRig.Cameras.IndexOf(cam);
            bool has = camIndex >= 0 && Feeds.ContainsKey(camIndex);
            var q = m.Rotation;
            var right = Vector3.Transform(Vector3.UnitX, q);
            var up = Vector3.Transform(Vector3.UnitY, q);
            var normal = Vector3.Transform(Vector3.UnitZ, q);
            float aspect = Math.Clamp(m.Aspect, 0.1f, 10f);
            float hw = m.Width * 0.5f, hh = m.Width / aspect * 0.5f;
            // (as seen from the front: its left on the viewer's left)
            var side = Vector3.Normalize(Vector3.Cross(-normal, up));
            if(Vector3.Dot(side, right) < 0)
                right = -right;
            if(m.Mirror)
                right = -right;
            bool back = Vector3.Dot(eye - m.Position, normal) < 0;
            // the picture cropped to fill the screen
            var uv = new Vector4(1, 1, 0, 0);
            if(aspect > screenAspect)
            {
                float s = screenAspect / aspect;
                uv = new Vector4(1, s, 0, (1 - s) * 0.5f);
            }
            else
            {
                float s = aspect / screenAspect;
                uv = new Vector4(s, 1, (1 - s) * 0.5f, 0);
            }
            var k = new Constants
            {
                ViewProj = vpm,
                C0 = new Vector4(m.Position - right * hw - up * hh, 1),
                C1 = new Vector4(m.Position + right * hw - up * hh, 1),
                C2 = new Vector4(m.Position + right * hw + up * hh, 1),
                C3 = new Vector4(m.Position - right * hw + up * hh, 1),
                UvRect = uv,
                Screen = new Vector4(bd.Width, bd.Height, 1f / bd.Width, 1f / bd.Height),
                DepthUv = new Vector4(depthUv, reversed ? 1f : 0f, has ? 1f : 0f),
                Look = new Vector4(Math.Max(0f, m.Brightness), Math.Clamp(m.Bezel, 0f, 0.3f), aspect, back && m.BackPanel ? 1f : 0f),
                BezelColour = new Vector4(MaterialFx.Colour(m.BezelColour), 1f),
                Time = new Vector4((float)(Environment.TickCount64 % 3_600_000) / 1000f, _liveNow && has && camIndex == LiveCharacters.LiveCamera ? 1f : 0f, 0, 0),
            };
            Mapped mp;
            if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _cb, 0, 4, 0, &mp) < 0)
                continue;
            *(Constants*)mp.Data = k;
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _cb, 0);
            inputs[0] = has ? Feeds[camIndex].Srv : 0;
            inputs[1] = (nint)ds->D3D11ShaderResourceView;
            inputs[2] = LiveCharacters.LiveView;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 3, inputs);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 6, 0);
        }
        nint* none = stackalloc nint[3];
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 3, none);
    }
}
