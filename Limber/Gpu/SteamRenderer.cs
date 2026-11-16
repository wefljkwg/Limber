using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Limber.Rig;

namespace Limber.Gpu;

/// <summary>
/// Draws the steam / musk puffs (<see cref="SteamService"/>) into the game's picture: each a soft, wispy, camera-facing
/// puff (its wisps from moving noise), faded where it meets what's behind it (so it never shows a hard edge on the
/// skin) and hidden behind anything the game drew in front of it (its depth buffer, read the way the liquid renderer
/// reads it). Direct3D 11 through the device's own tables; the shaders compiled once by Windows' own compiler.
/// Render thread, before the plugin windows are drawn; every piece of the device's state it changes is put back.
/// </summary>
public static unsafe class SteamRenderer
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
    private struct BufferSrvDesc
    {
        public uint Format, ViewDimension, FirstElement, NumElements, Pad0, Pad1;
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

    private const uint FormatR32F = 41;
    private const uint BindSrv = 0x8, BindRt = 0x20, BindConstant = 0x4;
    private const uint UsageDefault = 0, UsageDynamic = 2, CpuWrite = 0x10000, MiscStructured = 0x40;

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(byte* src, nuint size, byte* name, void* defines, void* include, byte* entry, byte* target, uint flags1, uint flags2, nint* code, nint* errors);

    private static bool _failed;
    private static string _error = "";
    private static nint _vs, _ps, _vsFull, _psProbe, _cb, _puffs, _puffsSrv, _blendAlpha, _blendNone, _depthOff, _rasterNone, _samplerPoint;
    private static nint _probe, _probeRt, _probeSrv, _probeStage;
    private static int _capacity;
    private static SteamService.Puff[] _take = new SteamService.Puff[256];

    /// <summary>How the game's depth was found to be stored (0..3), 4 = couldn't tell, -1 = not looked at yet.</summary>
    public static int FoundDepth { get; private set; } = -1;

    /// <summary>Why it isn't drawing (empty when it's fine).</summary>
    public static string Error => _error;

    public static void Retry()
    {
        _failed = false;
        _error = "";
    }

    public static void Dispose()
    {
        foreach(var o in new[] { _vs, _ps, _vsFull, _psProbe, _cb, _puffs, _puffsSrv, _blendAlpha, _blendNone, _depthOff, _rasterNone, _samplerPoint, _probe, _probeRt, _probeSrv, _probeStage })
            Release(o);
        _vs = _ps = _vsFull = _psProbe = _cb = _puffs = _puffsSrv = _blendAlpha = _blendNone = _depthOff = _rasterNone = _samplerPoint = 0;
        _probe = _probeRt = _probeSrv = _probeStage = 0;
        _capacity = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProj, InvViewProj;
        public Vector4 Cam;      // camera position, time
        public Vector4 Screen;   // w, h, 1/w, 1/h
        public Vector4 SceneUv;  // depth uv scale (x, y), 0, use the depth
        public Vector4 Color;    // rgb, opacity
        public Vector4 Right;    // camera right
        public Vector4 Up;       // camera up
        public Vector4 Fwd;      // camera forward
        public Vector4 Extra;    // camera near, far, how the depth reads (0..3, 4 unknown), 0
        public Vector4 Probe0, Probe1, Probe2, Probe3;
    }

    private const string Hlsl = @"
cbuffer C : register(b0)
{
    row_major float4x4 VP; row_major float4x4 IVP;
    float4 Cam; float4 Screen; float4 SceneUv; float4 Color; float4 Right; float4 Up; float4 Fwd; float4 Extra;
    float4 Probe[4];
};
struct Puff { float4 PosSize; float4 Data; };
StructuredBuffer<Puff> Puffs : register(t0);
Texture2D<float> SceneDepth : register(t1);
Texture2D<float> DepthMode : register(t2);
SamplerState SampPoint : register(s0);

static const float2 Quad[6] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,-1), float2(1,1), float2(-1,1) };

struct PuffOut { float4 pos : SV_Position; float2 q : TEXCOORD0; float3 wp : TEXCOORD1; nointerpolation float4 data : TEXCOORD2; nointerpolation float size : TEXCOORD3; };

PuffOut VsPuff(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Puff p = Puffs[iid];
    float2 q = Quad[vid];
    // (each puff turned a little its own way, so they don't all line up)
    float ang = p.Data.y * 6.2831 + Cam.w * (p.Data.y - 0.5) * 0.6;
    float cs = cos(ang), sn = sin(ang);
    float2 r = float2(q.x * cs - q.y * sn, q.x * sn + q.y * cs);
    float3 wp = p.PosSize.xyz + (Right.xyz * r.x + Up.xyz * r.y) * p.PosSize.w;
    PuffOut o; o.pos = mul(float4(wp, 1), VP); o.q = q; o.wp = wp; o.data = p.Data; o.size = p.PosSize.w; return o;
}

float Hash(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
float Noise(float2 p)
{
    float2 i = floor(p), f = frac(p); float2 u = f * f * (3 - 2 * f);
    return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), u.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), u.x), u.y);
}
float Fbm(float2 p) { float v = 0, a = 0.55; [unroll] for (int k = 0; k < 4; k++) { v += a * Noise(p); p = p * 2.03 + 17.1; a *= 0.5; } return v; }

float DistanceAt(float2 uv, float d)
{
    float4 w = mul(float4(uv.x * 2 - 1, 1 - uv.y * 2, d, 1), IVP);
    if (abs(w.w) < 1e-8) return 1e9;
    float dist = length(w.xyz / w.w - Cam.xyz);
    return (dist > 0 && dist < 1e8) ? dist : 1e9;
}
float3 RayAt(float2 uv)
{
    float4 w = mul(float4(uv.x * 2 - 1, 1 - uv.y * 2, 0.5, 1), IVP);
    return normalize(w.xyz / w.w - Cam.xyz);
}
float DistanceIn(float2 uv, float d, int mode)
{
    if (mode == 0) return DistanceAt(uv, d);
    if (mode == 1) return DistanceAt(uv, 1 - d);
    float z;
    if (mode == 2) z = Extra.x / max(d, 1e-7);
    else z = Extra.x * Extra.y / max(Extra.y - d * (Extra.y - Extra.x), 1e-6);
    float along = dot(RayAt(uv), Fwd.xyz);
    return (z > 0 && z < 1e8 && along > 1e-4) ? z / along : 1e9;
}
int Mode()
{
    if (Extra.z > 0.5) return (int)round(Extra.z) - 1;
    return (int)round(DepthMode.Load(int3(0, 0, 0)));
}

float4 PsPuff(PuffOut i) : SV_Target0
{
    float r = length(i.q);
    if (r > 1) discard;
    // wisps: noise drifting up through the puff, thinner toward its edge
    float t = Cam.w;
    float2 p = i.q * 1.3 + float2(i.data.y * 37.0, i.data.y * 11.0) + float2(0, -t * 0.35 - i.data.z * 0.8);
    float n = Fbm(p * 1.7);
    float wisp = saturate((n - 0.32 - i.data.z * 0.15) * 2.4);
    float edge = pow(saturate(1 - r), 1.6);
    float density = wisp * edge * i.data.x;
    // soft where it meets what's behind it, gone behind what's in front
    float2 uv = i.pos.xy * Screen.zw;
    float fade = 1;
    int mode = Mode();
    if (SceneUv.w > 0.5 && mode < 4)
    {
        float sceneT = DistanceIn(uv, SceneDepth.SampleLevel(SampPoint, uv * SceneUv.xy, 0), mode);
        float myT = length(i.wp - Cam.xyz);
        fade = saturate((sceneT - myT) / max(i.size * 0.7, 0.02));
    }
    // (a little fainter right up at the camera)
    float near = saturate((length(i.wp - Cam.xyz) - 0.15) / 0.4);
    float a = saturate(density * fade * near * Color.a);
    if (a < 0.002) discard;
    float3 col = Color.rgb * (0.85 + 0.25 * n);
    return float4(col, a);
}

float4 VsFull(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

// Which way the game's depth is stored: at points inside the bodies, the way that puts the scene about where they are.
float PsProbe(float4 pos : SV_Position) : SV_Target0
{
    float votes[4] = { 0, 0, 0, 0 };
    float errs[4] = { 0, 0, 0, 0 };
    [unroll] for (int i = 0; i < 4; i++)
    {
        if (Probe[i].w < 0.5) continue;
        float4 c = mul(float4(Probe[i].xyz, 1), VP);
        if (c.w <= 1e-4) continue;
        float2 ndc = c.xy / c.w;
        if (abs(ndc.x) > 0.97 || abs(ndc.y) > 0.97) continue;
        float2 uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        float d = SceneDepth.SampleLevel(SampPoint, uv * SceneUv.xy, 0);
        float expected = length(Probe[i].xyz - Cam.xyz);
        float tol = max(0.4, expected * 0.25);
        [unroll] for (int m = 0; m < 4; m++)
        {
            float e = abs(DistanceIn(uv, d, m) - expected);
            if (e < tol) { votes[m] += 1; errs[m] += e; }
        }
    }
    int best = 4; float bestVotes = 0; float bestErr = 1e9;
    [unroll] for (int m = 0; m < 4; m++)
        if (votes[m] > bestVotes || (votes[m] == bestVotes && votes[m] > 0 && errs[m] < bestErr))
        {
            best = m; bestVotes = votes[m]; bestErr = errs[m];
        }
    return best;
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
        var name = Encoding.ASCII.GetBytes("LimberSteam\0");
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
        _vs = Compile("VsPuff", "vs_5_0");
        _ps = Compile("PsPuff", "ps_5_0");
        _vsFull = Compile("VsFull", "vs_5_0");
        _psProbe = Compile("PsProbe", "ps_5_0");

        nint o = 0;
        var cbDesc = new BufferDesc { ByteWidth = (uint)((sizeof(Constants) + 15) / 16 * 16), Usage = UsageDynamic, BindFlags = BindConstant, CpuAccessFlags = CpuWrite };
        _cb = Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &cbDesc, null, &o), o, "constants");

        nint MakeBlend(bool on)
        {
            var b = new BlendDesc();
            // (premultiplied by alpha: src alpha, 1 - src alpha; the picture's own alpha kept)
            b.RT0 = new RtBlend { BlendEnable = on ? 1 : 0, Src = 5, Dest = 6, Op = 1, SrcAlpha = 1, DestAlpha = 2, OpAlpha = 1, WriteMask = on ? 0x7u : 0xFu };
            nint s = 0;
            return Check(((delegate* unmanaged[Stdcall]<nint, BlendDesc*, nint*, int>)V(device, 20))(device, &b, &s), s, "blend");
        }
        _blendAlpha = MakeBlend(true);
        _blendNone = MakeBlend(false);

        var d = new DepthDesc
        {
            DepthEnable = 0, WriteMask = 0, Func = 8, StencilEnable = 0, StencilMasks = 0xFFFF,
            Front = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 }, Back = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 },
        };
        nint ds = 0;
        _depthOff = Check(((delegate* unmanaged[Stdcall]<nint, DepthDesc*, nint*, int>)V(device, 21))(device, &d, &ds), ds, "depth state");

        var r = new RasterDesc { Fill = 3, Cull = 1, DepthClip = 1 };
        nint rs = 0;
        _rasterNone = Check(((delegate* unmanaged[Stdcall]<nint, RasterDesc*, nint*, int>)V(device, 22))(device, &r, &rs), rs, "rasterizer");

        var sd = new SamplerDesc { Filter = 0, U = 3, Vv = 3, W = 3, MaxAnisotropy = 1, Comparison = 1, MaxLod = float.MaxValue };
        nint ss = 0;
        _samplerPoint = Check(((delegate* unmanaged[Stdcall]<nint, SamplerDesc*, nint*, int>)V(device, 23))(device, &sd, &ss), ss, "sampler");

        // the depth probe's 1x1 target and a copy the CPU can read
        var pd = new Texture2DDesc { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = FormatR32F, SampleCount = 1, Usage = UsageDefault, BindFlags = BindSrv | BindRt };
        nint t = 0;
        _probe = Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &pd, null, &t), t, "probe");
        nint sv = 0;
        _probeSrv = Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 7))(device, _probe, null, &sv), sv, "probe view");
        nint rt = 0;
        _probeRt = Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 9))(device, _probe, null, &rt), rt, "probe target");
        var st = new Texture2DDesc { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = FormatR32F, SampleCount = 1, Usage = 3, CpuAccessFlags = 0x20000 };
        nint stage = 0;
        _probeStage = Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &st, null, &stage), stage, "probe copy");
    }

    private static void EnsurePuffs(int count)
    {
        if(count <= _capacity)
            return;
        Release(_puffsSrv);
        Release(_puffs);
        _puffs = _puffsSrv = 0;
        _capacity = 0;
        int capacity = Math.Max(512, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)count));
        var device = Device();
        var desc = new BufferDesc { ByteWidth = (uint)(capacity * 32), Usage = UsageDynamic, BindFlags = BindSrv, CpuAccessFlags = CpuWrite, MiscFlags = MiscStructured, StructureByteStride = 32 };
        nint b = 0;
        _puffs = Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &desc, null, &b), b, "puffs");
        var srv = new BufferSrvDesc { Format = 0, ViewDimension = 1, FirstElement = 0, NumElements = (uint)capacity };
        nint v = 0;
        _puffsSrv = Check(((delegate* unmanaged[Stdcall]<nint, nint, BufferSrvDesc*, nint*, int>)V(device, 7))(device, _puffs, &srv, &v), v, "puffs view");
        _capacity = capacity;
    }

    // ---- The device's state, kept and put back

    private struct Saved
    {
        public fixed long Rtv[8];
        public nint Dsv, Blend, Depth, Raster, Vs, Ps, Layout, VsCb, PsCb, Sampler0, VsSrv0;
        public fixed long PsSrv[3];
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
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 73))(c, 0, 3, s->PsSrv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 84))(c, 0, 1, &s->VsSrv0);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 75))(c, 0, 1, &s->Sampler0);
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
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 8))(c, 0, 3, s->PsSrv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 25))(c, 0, 1, &s->VsSrv0);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 10))(c, 0, 1, &s->Sampler0);
        for(int i = 0; i < 8; i++)
            Release((nint)s->Rtv[i]);
        for(int i = 0; i < 3; i++)
            Release((nint)s->PsSrv[i]);
        for(uint i = 0; i < s->VsInstances; i++)
            Release((nint)s->VsClass[i]);
        for(uint i = 0; i < s->PsInstances; i++)
            Release((nint)s->PsClass[i]);
        foreach(var o in new[] { s->Dsv, s->Blend, s->Depth, s->Raster, s->Vs, s->Ps, s->Layout, s->VsCb, s->PsCb, s->VsSrv0, s->Sampler0 })
            Release(o);
        NativeMemory.Clear(s, (nuint)sizeof(Saved));
    }

    private static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>Draws the puffs (render thread). Does nothing when there are none.</summary>
    public static void Draw(SteamService steam, bool hideBehind)
    {
        if(_failed)
            return;
        int count = steam.Take(ref _take, out var probes);
        if(count == 0)
            return;
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
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)V(swap, 9))(swap, 0, &iid, &t);
            if(hr < 0 || t == 0)
                return;
            backTex = t;
            Texture2DDesc bd;
            ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)V(backTex, 10))(backTex, &bd);
            if(bd.Width == 0 || bd.Height == 0 || bd.SampleCount != 1)
                return;
            nint r = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 9))(device, backTex, null, &r);
            if(hr < 0 || r == 0)
                return;
            backRtv = r;
            EnsurePuffs(count);

            nint sceneSrv = 0;
            var uvScale = Vector2.One;
            var rtm = RenderTargetManager.Instance();
            if(hideBehind && rtm != null && rtm->DepthStencil != null && rtm->DepthStencil->D3D11ShaderResourceView != null)
            {
                var ds = rtm->DepthStencil;
                sceneSrv = (nint)ds->D3D11ShaderResourceView;
                if(ds->AllocatedWidth > 0 && ds->AllocatedHeight > 0)
                    uvScale = new Vector2((float)ds->ActualWidth / ds->AllocatedWidth, (float)ds->ActualHeight / ds->AllocatedHeight);
            }
            var constants = MakeConstants(steam, bd.Width, bd.Height, uvScale, sceneSrv != 0, probes);
            if(constants == null)
                return;

            Save(c);
            saved = true;

            Mapped m;
            if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _puffs, 0, 4, 0, &m) < 0)
                return;
            fixed(SteamService.Puff* p = _take)
                Buffer.MemoryCopy(p, m.Data, (long)_capacity * 32, (long)count * 32);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _puffs, 0);
            if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _cb, 0, 4, 0, &m) < 0)
                return;
            *(Constants*)m.Data = constants.Value;
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _cb, 0);

            ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 17))(c, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, void>)V(c, 24))(c, 4); // triangle list
            nint cb = _cb;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 7))(c, 0, 1, &cb);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 0, 1, &cb);
            nint sampler = _samplerPoint;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 10))(c, 0, 1, &sampler);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _depthOff, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 43))(c, _rasterNone);
            float* zeroFactor = stackalloc float[4];
            nint* none = stackalloc nint[3];

            // 0. which way the game's depth reads (from points inside the bodies; read back a frame or so late)
            if(sceneSrv != 0)
            {
                nint prt = _probeRt;
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 3, none);
                ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &prt, 0);
                var one = new Viewport { Width = 1, Height = 1, MinDepth = 0, MaxDepth = 1 };
                ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(c, 44))(c, 1, &one);
                nint* probeIn = stackalloc nint[2] { 0, sceneSrv };
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 2, probeIn);
                ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendNone, zeroFactor, 0xFFFFFFFF);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vsFull, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psProbe, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 3, 0);
                Mapped pm;
                if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _probeStage, 0, 1, 0x100000, &pm) >= 0)
                {
                    float found = *(float*)pm.Data;
                    ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _probeStage, 0);
                    if(float.IsFinite(found))
                        FoundDepth = (int)MathF.Round(found);
                }
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)V(c, 47))(c, _probeStage, _probe);
            }
            else
                FoundDepth = 4;

            // 1. the puffs, onto the picture
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 3, none);
            nint rtv = backRtv;
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rtv, 0);
            var vp = new Viewport { Width = bd.Width, Height = bd.Height, MinDepth = 0, MaxDepth = 1 };
            ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(c, 44))(c, 1, &vp);
            nint puffs = _puffsSrv;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 25))(c, 0, 1, &puffs);
            nint* inputs = stackalloc nint[3] { 0, sceneSrv, _probeSrv };
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 3, inputs);
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendAlpha, zeroFactor, 0xFFFFFFFF);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vs, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _ps, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, void>)V(c, 21))(c, 6, (uint)count, 0, 0);

            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 3, none);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 25))(c, 0, 1, none);
            _error = "";
        }
        catch(Exception e)
        {
            _failed = true;
            _error = e.Message;
            Plugin.Log.Warning(e, "Limber: the steam can't be drawn");
        }
        finally
        {
            if(saved)
                Restore(c);
            Release(backRtv);
            Release(backTex);
        }
    }

    private static Constants? MakeConstants(SteamService steam, uint w, uint h, Vector2 uvScale, bool useScene, Vector3[] probes)
    {
        var control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Instance();
        if(control == null)
            return null;
        var vp = control->ViewProjectionMatrix;
        if(!Matrix4x4.Invert(vp, out var ivp))
            return null;
        Vector3 Unproject(float x, float y, float z)
        {
            var v = Vector4.Transform(new Vector4(x, y, z, 1), ivp);
            return new Vector3(v.X, v.Y, v.Z) / v.W;
        }
        var ch = Vector4.Transform(new Vector4(0, 0, 1, 0), ivp);
        if(MathF.Abs(ch.W) < 1e-12f)
            return null;
        var cam = new Vector3(ch.X, ch.Y, ch.Z) / ch.W;
        var center = Vector3.Normalize(Unproject(0, 0, 0.5f) - cam);
        var right = Vector3.Normalize(Unproject(1, 0, 0.5f) - Unproject(-1, 0, 0.5f));
        var up = Vector3.Normalize(Unproject(0, 1, 0.5f) - Unproject(0, -1, 0.5f));
        if(!Finite(cam) || !Finite(right) || !Finite(up) || !Finite(center))
            return null;
        float camNear = 0.1f, camFar = 1000f;
        var cm = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
        if(cm != null && cm->CurrentCamera != null && cm->CurrentCamera->RenderCamera != null)
        {
            var rc = cm->CurrentCamera->RenderCamera;
            if(float.IsFinite(rc->NearPlane) && rc->NearPlane > 0f)
                camNear = rc->NearPlane;
            if(float.IsFinite(rc->FarPlane) && rc->FarPlane > camNear)
                camFar = rc->FarPlane;
        }
        // (the way the liquid renderer found the depth reads, if it has: else this one's own probe)
        int mode = FluidRenderer.FoundDepth is >= 0 and <= 3 ? FluidRenderer.FoundDepth + 1 : 0;
        return new Constants
        {
            ViewProj = vp,
            InvViewProj = ivp,
            Cam = new Vector4(cam, steam.Time),
            Screen = new Vector4(w, h, 1f / w, 1f / h),
            SceneUv = new Vector4(uvScale.X, uvScale.Y, 0, useScene ? 1 : 0),
            Color = new Vector4(steam.Color, steam.Opacity),
            Right = new Vector4(right, 0),
            Up = new Vector4(up, 0),
            Fwd = new Vector4(center, 0),
            Extra = new Vector4(camNear, camFar, mode, 0),
            Probe0 = probes.Length > 0 ? new Vector4(probes[0], 1) : default,
            Probe1 = probes.Length > 1 ? new Vector4(probes[1], 1) : default,
            Probe2 = probes.Length > 2 ? new Vector4(probes[2], 1) : default,
            Probe3 = probes.Length > 3 ? new Vector4(probes[3], 1) : default,
        };
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
