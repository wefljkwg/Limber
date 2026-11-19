using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Limber.BodyRig;
using Limber.Core;
using Limber.Rig;

namespace Limber.Gpu;

/// <summary>
/// The Shaders page's drawn effects (Limber's own, on any material, skin included): a glow over the surface, a rim
/// light round the edges, an outline, and seeing them through walls. The chosen characters' worn models are read once
/// (their first level of detail), skinned on the CPU every frame with the very matrices the game skins them with (each
/// model's own bone tables: racial fitting and body swaps included), and drawn over the game's picture with Limber's own
/// shaders: first into a depth buffer of Limber's own (so only the front layer of each character shows, and clothes
/// cover the skin under them), then the outline (the back of a slightly blown-up copy: what peeks out round the edge),
/// then the glow and rim (added on). Hidden behind anything the game drew in front of them (its depth buffer, read the
/// way the steam reads it). Render thread, before the plugin windows; every piece of the device's state it changes is
/// put back. Everything stays in this game client.
/// </summary>
public static unsafe class EffectRenderer
{
    // ---- Direct3D 11 (by vtable)

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

    private const uint FormatR32F = 41, FormatD32F = 40;
    private const uint BindSrv = 0x8, BindRt = 0x20, BindConstant = 0x4, BindDepth = 0x40;
    private const uint UsageDefault = 0, UsageDynamic = 2, CpuWrite = 0x10000, MiscStructured = 0x40;

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(byte* src, nuint size, byte* name, void* defines, void* include, byte* entry, byte* target, uint flags1, uint flags2, nint* code, nint* errors);

    private static bool _failed;
    private static string _error = "";
    private static nint _vsMesh, _vsHull, _psSurface, _psHull, _vsFull, _psProbe, _cb;
    private static nint _blendAdd, _blendSolid, _blendNone, _depthOff, _rasterNone, _samplerPoint;
    private static nint _depthWrite, _depthEqual, _depthBehind, _depthWriteR, _depthEqualR, _depthBehindR;
    private static nint _probe, _probeRt, _probeSrv, _probeStage;
    private static nint _ownDepth, _ownDsv;
    private static uint _ownW, _ownH;
    private static readonly Buf Verts = new(32), Indices = new(4), Looks = new(64);

    /// <summary>Why it isn't drawing (empty when it's fine).</summary>
    public static string Error => _error;

    /// <summary>How many vertices it drew last frame (for the page).</summary>
    public static int Drawn { get; private set; }

    public static void Retry()
    {
        _failed = false;
        _error = "";
    }

    public static void Dispose()
    {
        foreach(var o in new[] { _vsMesh, _vsHull, _psSurface, _psHull, _vsFull, _psProbe, _cb, _blendAdd, _blendSolid, _blendNone, _depthOff, _rasterNone, _samplerPoint,
                     _depthWrite, _depthEqual, _depthBehind, _depthWriteR, _depthEqualR, _depthBehindR, _probe, _probeRt, _probeSrv, _probeStage, _ownDepth, _ownDsv })
            Release(o);
        _vsMesh = _vsHull = _psSurface = _psHull = _vsFull = _psProbe = _cb = 0;
        _blendAdd = _blendSolid = _blendNone = _depthOff = _rasterNone = _samplerPoint = 0;
        _depthWrite = _depthEqual = _depthBehind = _depthWriteR = _depthEqualR = _depthBehindR = 0;
        _probe = _probeRt = _probeSrv = _probeStage = 0;
        _ownDepth = _ownDsv = 0;
        _ownW = _ownH = 0;
        Verts.Free();
        Indices.Free();
        Looks.Free();
        _uploadedIndices = 0;
        _indicesIn = 0;
    }

    // ---- A structured buffer the CPU writes (grown when it must)

    private sealed class Buf
    {
        public readonly int Stride;
        public nint Buffer, Srv;
        public int Capacity;
        public Buf(int stride) => Stride = stride;

        public void Ensure(int count)
        {
            if(count <= Capacity && Buffer != 0)
                return;
            Free();
            int capacity = Math.Max(1024, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(count, 1)));
            var device = Device();
            var desc = new BufferDesc { ByteWidth = (uint)(capacity * Stride), Usage = UsageDynamic, BindFlags = BindSrv, CpuAccessFlags = CpuWrite, MiscFlags = MiscStructured, StructureByteStride = (uint)Stride };
            nint b = 0;
            Buffer = Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &desc, null, &b), b, "effect buffer");
            var srv = new BufferSrvDesc { Format = 0, ViewDimension = 1, FirstElement = 0, NumElements = (uint)capacity };
            nint v = 0;
            Srv = Check(((delegate* unmanaged[Stdcall]<nint, nint, BufferSrvDesc*, nint*, int>)V(device, 7))(device, Buffer, &srv, &v), v, "effect buffer view");
            Capacity = capacity;
        }

        public bool Write(nint c, void* data, int count)
        {
            Mapped m;
            if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, Buffer, 0, 4, 0, &m) < 0)
                return false;
            System.Buffer.MemoryCopy(data, m.Data, (long)Capacity * Stride, (long)count * Stride);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, Buffer, 0);
            return true;
        }

        public void Free()
        {
            Release(Srv);
            Release(Buffer);
            Srv = Buffer = 0;
            Capacity = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vert
    {
        public Vector4 P;   // world position, 1
        public Vector4 N;   // world normal, which look (-1: only hides what's behind it)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LookGpu
    {
        public Vector4 Light;    // rgb, strength
        public Vector4 Rim;      // rgb, strength
        public Vector4 Outline;  // rgb, width (m)
        public Vector4 Misc;     // rim tightness, pulse (per second), see through walls, 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProj, InvViewProj;
        public Vector4 Cam;      // camera position, time
        public Vector4 Screen;   // w, h, 1/w, 1/h
        public Vector4 SceneUv;  // depth uv scale (x, y), 0, use the depth
        public Vector4 Fwd;      // camera forward
        public Vector4 Extra;    // camera near, far, 0, 0
        public Vector4 Probe0, Probe1, Probe2, Probe3;
    }

    private const string Hlsl = @"
cbuffer C : register(b0)
{
    row_major float4x4 VP; row_major float4x4 IVP;
    float4 Cam; float4 Screen; float4 SceneUv; float4 Fwd; float4 Extra;
    float4 Probe[4];
};
struct Vert { float4 P; float4 N; };
struct Look { float4 Light; float4 Rim; float4 Outline; float4 Misc; };
StructuredBuffer<Vert> Verts : register(t0);
Texture2D<float> SceneDepth : register(t1);
Texture2D<float> DepthMode : register(t2);
StructuredBuffer<uint> Indices : register(t3);
StructuredBuffer<Look> Looks : register(t4);
SamplerState SampPoint : register(s0);

struct VOut { float4 pos : SV_Position; float3 wp : TEXCOORD0; float3 n : TEXCOORD1; nointerpolation int look : TEXCOORD2; };

VOut VsMesh(uint vid : SV_VertexID)
{
    Vert v = Verts[Indices[vid]];
    VOut o; o.wp = v.P.xyz; o.n = v.N.xyz; o.look = (int)round(v.N.w);
    o.pos = mul(float4(v.P.xyz, 1), VP);
    return o;
}

// The outline: the model blown up along its normals (only the looks with one; the rest left out).
VOut VsHull(uint vid : SV_VertexID)
{
    Vert v = Verts[Indices[vid]];
    VOut o; o.n = v.N.xyz; o.look = (int)round(v.N.w);
    float w = o.look >= 0 ? Looks[o.look].Outline.w : 0;
    // (a little wider further away, so it doesn't vanish into a hairline)
    w *= 1 + 0.08 * length(v.P.xyz - Cam.xyz);
    o.wp = v.P.xyz + v.N.xyz * w;
    o.pos = w > 0 ? mul(float4(o.wp, 1), VP) : float4(0, 0, -2, 1);
    return o;
}

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

// Something the game drew in front of this point (a few centimetres of leeway: the game's own copy of the same skin
// may be shaped a touch differently, by shape keys or soft bodies).
bool Hidden(float2 uv, float myT)
{
    int mode = (int)round(DepthMode.Load(int3(0, 0, 0)));
    if (SceneUv.w < 0.5 || mode >= 4 || mode < 0) return false;
    float sceneT = DistanceIn(uv, SceneDepth.SampleLevel(SampPoint, uv * SceneUv.xy, 0), mode);
    return myT > sceneT + 0.05 + myT * 0.01;
}

float4 PsSurface(VOut i) : SV_Target0
{
    if (i.look < 0) discard;
    Look L = Looks[i.look];
    float2 uv = i.pos.xy * Screen.zw;
    float myT = length(i.wp - Cam.xyz);
    bool hidden = Hidden(uv, myT);
    if (hidden && L.Misc.z < 0.5) discard;
    float3 V = normalize(Cam.xyz - i.wp);
    float3 N = normalize(i.n);
    float facing = saturate(abs(dot(N, V)));
    float rim = pow(saturate(1 - facing), max(L.Misc.x, 0.5));
    float pulse = L.Misc.y > 0 ? 0.6 + 0.4 * sin(Cam.w * L.Misc.y * 6.2831853) : 1;
    float3 c;
    if (hidden)
    {
        // seen through what's in front: a ghostly silhouette in the rim's colour
        float3 rc = L.Rim.w > 0 ? L.Rim.rgb : (L.Light.w > 0 ? L.Light.rgb : float3(0.55, 0.8, 1));
        c = rc * (0.12 + 0.6 * rim) * pulse;
    }
    else
        c = (L.Light.rgb * L.Light.w * 0.5 + L.Rim.rgb * L.Rim.w * rim) * pulse;
    if (max(c.r, max(c.g, c.b)) < 0.002) discard;
    return float4(c, 0);
}

float4 PsHull(VOut i) : SV_Target0
{
    if (i.look < 0) discard;
    Look L = Looks[i.look];
    // (only the blown-up copy's side facing away: what shows round the character's edge)
    float3 V = normalize(Cam.xyz - i.wp);
    if (dot(normalize(i.n), V) > 0.02) discard;
    float2 uv = i.pos.xy * Screen.zw;
    if (Hidden(uv, length(i.wp - Cam.xyz))) discard;
    return float4(L.Outline.rgb, 1);
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
        var name = Encoding.ASCII.GetBytes("LimberEffects\0");
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
        _vsMesh = Compile("VsMesh", "vs_5_0");
        _vsHull = Compile("VsHull", "vs_5_0");
        _psSurface = Compile("PsSurface", "ps_5_0");
        _psHull = Compile("PsHull", "ps_5_0");
        _vsFull = Compile("VsFull", "vs_5_0");
        _psProbe = Compile("PsProbe", "ps_5_0");

        nint o = 0;
        var cbDesc = new BufferDesc { ByteWidth = (uint)((sizeof(Constants) + 15) / 16 * 16), Usage = UsageDynamic, BindFlags = BindConstant, CpuAccessFlags = CpuWrite };
        _cb = Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &cbDesc, null, &o), o, "constants");

        nint MakeBlend(RtBlend rt)
        {
            var b = new BlendDesc { RT0 = rt };
            nint s = 0;
            return Check(((delegate* unmanaged[Stdcall]<nint, BlendDesc*, nint*, int>)V(device, 20))(device, &b, &s), s, "blend");
        }
        // added on (ONE + ONE, colour only); drawn over (colour only); nothing written (the depth pass)
        _blendAdd = MakeBlend(new RtBlend { BlendEnable = 1, Src = 2, Dest = 2, Op = 1, SrcAlpha = 1, DestAlpha = 2, OpAlpha = 1, WriteMask = 0x7 });
        _blendSolid = MakeBlend(new RtBlend { BlendEnable = 0, Src = 2, Dest = 1, Op = 1, SrcAlpha = 2, DestAlpha = 1, OpAlpha = 1, WriteMask = 0x7 });
        _blendNone = MakeBlend(new RtBlend { BlendEnable = 0, Src = 2, Dest = 1, Op = 1, SrcAlpha = 2, DestAlpha = 1, OpAlpha = 1, WriteMask = 0xF });

        nint MakeDepth(bool on, bool write, int func)
        {
            var d = new DepthDesc
            {
                DepthEnable = on ? 1 : 0, WriteMask = write ? 1 : 0, Func = func, StencilEnable = 0, StencilMasks = 0xFFFF,
                Front = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 }, Back = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 },
            };
            nint ds = 0;
            return Check(((delegate* unmanaged[Stdcall]<nint, DepthDesc*, nint*, int>)V(device, 21))(device, &d, &ds), ds, "depth state");
        }
        _depthOff = MakeDepth(false, false, 8);
        // (2 less, 4 less or equal, 5 greater, 7 greater or equal: the game's camera may put near at 1)
        _depthWrite = MakeDepth(true, true, 2);
        _depthEqual = MakeDepth(true, false, 4);
        _depthBehind = MakeDepth(true, false, 2);
        _depthWriteR = MakeDepth(true, true, 5);
        _depthEqualR = MakeDepth(true, false, 7);
        _depthBehindR = MakeDepth(true, false, 5);

        var r = new RasterDesc { Fill = 3, Cull = 1, DepthClip = 1 };
        nint rs = 0;
        _rasterNone = Check(((delegate* unmanaged[Stdcall]<nint, RasterDesc*, nint*, int>)V(device, 22))(device, &r, &rs), rs, "rasterizer");

        var sd = new SamplerDesc { Filter = 0, U = 3, Vv = 3, W = 3, MaxAnisotropy = 1, Comparison = 1, MaxLod = float.MaxValue };
        nint ss = 0;
        _samplerPoint = Check(((delegate* unmanaged[Stdcall]<nint, SamplerDesc*, nint*, int>)V(device, 23))(device, &sd, &ss), ss, "sampler");

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

    // Limber's own depth buffer, the picture's size.
    private static void EnsureOwnDepth(uint w, uint h)
    {
        if(_ownDsv != 0 && _ownW == w && _ownH == h)
            return;
        Release(_ownDsv);
        Release(_ownDepth);
        _ownDsv = _ownDepth = 0;
        var device = Device();
        var desc = new Texture2DDesc { Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = FormatD32F, SampleCount = 1, Usage = UsageDefault, BindFlags = BindDepth };
        nint t = 0;
        _ownDepth = Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &desc, null, &t), t, "depth");
        nint d = 0;
        _ownDsv = Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 10))(device, _ownDepth, null, &d), d, "depth view");
        _ownW = w;
        _ownH = h;
    }

    // ---- The models (read once per file, in the background)

    private sealed class MeshData
    {
        public string Part = "Gear";
        public string[] Bones = [];
        public Vector3[] Pos = [];
        public Vector3[] Nrm = [];
        public byte[] Bi = [];       // 4 per vertex: the mesh's own bone (its table's index)
        public float[] Bw = [];      // 4 per vertex
        public int Table = -1;       // its bone table (the game's matrices for it)
        public uint LodMask;         // parts only for far away (atr_lod): never drawn this close
        public uint HatMask;         // parts a hat hides (atr_kam)
        public ushort[] Raw = [];    // the mesh's own index list, as the file has it
        public List<(int Start, int Count, uint Attributes)> Subs = new();
        public List<(int Shape, (int Index, int Vertex)[] Values)> Shapes = new();
        private ushort[] _tris = [];
        private uint _attr, _shape;
        private bool _made;

        /// <summary>The triangles the game draws for these settings of the model: its shape keys on (a face's chosen
        /// features, gear fitting) and only the parts whose attributes are on (what the character really has).</summary>
        public ushort[] Tris(uint attributes, uint shapes)
        {
            if(_made && attributes == _attr && shapes == _shape)
                return _tris;
            var idx = (ushort[])Raw.Clone();
            foreach(var (shape, values) in Shapes)
                if(shape < 32 && (shapes & (1u << shape)) != 0)
                    foreach(var (index, vertex) in values)
                        if(index < idx.Length)
                            idx[index] = (ushort)vertex;
            int n = Pos.Length;
            var tris = new List<ushort>(idx.Length);
            void Range(int start, int count)
            {
                for(int t = start; t + 2 < start + count && t + 2 < idx.Length; t += 3)
                {
                    ushort a = idx[t], b = idx[t + 1], c = idx[t + 2];
                    if(a < n && b < n && c < n)
                    {
                        tris.Add(a);
                        tris.Add(b);
                        tris.Add(c);
                    }
                }
            }
            if(Subs.Count == 0)
                Range(0, idx.Length);
            else
                foreach(var (start, count, needs) in Subs)
                    if((needs & attributes) == needs && (needs & LodMask) == 0)
                        Range(start, count);
            _tris = tris.ToArray();
            _attr = attributes;
            _shape = shapes;
            _made = true;
            return _tris;
        }
    }

    private sealed class ModelData
    {
        public List<MeshData> Meshes = new();
    }

    private static readonly ConcurrentDictionary<string, Task<ModelData?>> Models = new(StringComparer.OrdinalIgnoreCase);

    private static ModelData? ReadModel(string file)
    {
        byte[]? data;
        try
        {
            data = Limber.UI.LimberTab.ReadModelFile(file);
        }
        catch
        {
            return null;
        }
        if(data == null)
            return null;
        try
        {
            var (offset, size, layouts) = MdlRigger.Layout(data);
            if(size[0] == 0 || offset[0] + size[0] > data.Length)
                return null;
            int baseAt = (int)offset[0];
            int end = (int)(offset[0] + size[0]);
            var skins = MdlRigger.Skin(data, 0);
            var materials = MdlRigger.MeshMaterials(data);
            var tables = MdlRigger.MeshBoneTables(data);
            var attributeNames = MdlRigger.AttributeNames(data);
            uint lodMask = 0, hatMask = 0;
            for(int a = 0; a < attributeNames.Length && a < 32; a++)
            {
                if(attributeNames[a].Equals("atr_lod", StringComparison.OrdinalIgnoreCase))
                    lodMask |= 1u << a;
                if(attributeNames[a].Equals("atr_kam", StringComparison.OrdinalIgnoreCase))
                    hatMask |= 1u << a;
            }
            var result = new ModelData();
            Span<int> bi = stackalloc int[4];
            Span<float> bw = stackalloc float[4];
            foreach(var layout in layouts.Where(l => l.Lod == 0))
            {
                var skin = skins.FirstOrDefault(k => k.Mesh == layout.Mesh);
                if(skin == null || skin.Triangles.Length < 3 || layout.VertexCount <= 0)
                    continue;
                int n = layout.VertexCount;
                var mesh = new MeshData
                {
                    Bones = skin.Bones, Pos = new Vector3[n], Nrm = new Vector3[n], Bi = new byte[n * 4], Bw = new float[n * 4],
                };
                string material = layout.Mesh < materials.Length ? materials[layout.Mesh] : "";
                mesh.Part = MdlRigger.IsSkinMaterial(material) ? "Skin" : Shaders.PartOf(material, 0, false);
                bool normals = layout.NormalAt >= 0 && layout.NormalType is (2 or 3 or 14);
                int stride = skin.Stride;
                bool bad = false;
                for(int v = 0; v < n && !bad; v++)
                {
                    int pa = baseAt + layout.PositionAt + v * layout.PositionStride;
                    if(pa + 12 > end)
                    {
                        bad = true;
                        break;
                    }
                    var p = MdlRigger.ReadVector(data, pa, layout.PositionType);
                    mesh.Pos[v] = MathUtil.IsFinite(p) ? p : Vector3.Zero;
                    var nn = Vector3.UnitY;
                    if(normals)
                    {
                        int na = baseAt + layout.NormalAt + v * layout.NormalStride;
                        if(na + 4 <= end)
                        {
                            var r = MdlRigger.ReadVector(data, na, layout.NormalType);
                            if(MathUtil.IsFinite(r) && r.LengthSquared() > 1e-8f)
                                nn = Vector3.Normalize(r);
                        }
                    }
                    mesh.Nrm[v] = nn;
                    // its four strongest bones
                    bi.Fill(-1);
                    bw.Clear();
                    for(int k = 0; k < skin.Slots; k++)
                    {
                        float w = skin.Weights[v * stride + k];
                        int local = skin.Indices[v * stride + k];
                        if(w <= 0f || local >= skin.Bones.Length)
                            continue;
                        int weakest = 0;
                        for(int q = 1; q < 4; q++)
                            if(bw[q] < bw[weakest])
                                weakest = q;
                        if(w > bw[weakest])
                        {
                            bw[weakest] = w;
                            bi[weakest] = local;
                        }
                    }
                    float total = bw[0] + bw[1] + bw[2] + bw[3];
                    for(int q = 0; q < 4; q++)
                    {
                        mesh.Bi[v * 4 + q] = (byte)Math.Max(bi[q], 0);
                        mesh.Bw[v * 4 + q] = total > 1e-4f && bi[q] >= 0 ? bw[q] / total : 0f;
                    }
                }
                if(bad)
                    continue;
                mesh.Raw = skin.Triangles;
                mesh.Table = layout.Mesh < tables.Length ? tables[layout.Mesh] : -1;
                mesh.LodMask = lodMask;
                mesh.HatMask = hatMask;
                mesh.Subs = MdlRigger.MeshSubmeshes(data, layout.Mesh);
                mesh.Shapes = MdlRigger.MeshShapesIndexed(data, 0, layout.Mesh);
                if(mesh.Raw.Length >= 3)
                    result.Meshes.Add(mesh);
            }
            return result.Meshes.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static ModelData? ModelOf(string file)
    {
        if(Models.Count > 256)
            Models.Clear();
        var task = Models.GetOrAdd(file, f => Task.Run(() => ReadModel(f)));
        if(!task.IsCompleted)
            return null;
        try
        {
            return task.Result;
        }
        catch
        {
            return null;
        }
    }

    // ---- The game's skinning matrices for a model (its bone tables), read now and then

    private sealed class Binds
    {
        public long Next;
        public Dictionary<string, (int Partial, int Bone, Matrix4x4 Inverse)> ByName = new(StringComparer.Ordinal);
        public nint[] Tables = [];   // the game's bone-table objects (one per file bone table)
        public int[] Counts = [];
        public bool GameOk;          // the game's own matrices agree with where the bones are (used, not worked out here)
        public long GameChecked;
    }

    private static readonly Dictionary<nint, Binds> BindsOf = new();
    private static readonly HashSet<(string, uint, uint)> Reported = new();
    private static readonly HashSet<string> ReportedGame = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SkeletonInfoCache Cache = new();

    private static Binds? ReadBinds(nint model, in ActorSkeleton sk, long now)
    {
        if(BindsOf.TryGetValue(model, out var known) && now < known.Next)
            return known;
        var binds = new Binds { Next = now + 1000 };
        nint list = SafeMemory.ReadPointer(model + 0x58);
        int tables = SafeMemory.Read<int>(model + 0x60, 0);
        if(!SafeMemory.Plausible(list) || tables <= 0 || tables > 16)
        {
            BindsOf[model] = binds;
            return binds;
        }
        var raw = new List<(int Partial, int Bone, float[] F)>();
        binds.Tables = new nint[tables];
        binds.Counts = new int[tables];
        for(int t = 0; t < tables; t++)
        {
            nint table = list + t * 0xE0;
            binds.Tables[t] = table;
            binds.Counts[t] = Math.Clamp(SafeMemory.Read<int>(table + 0xC8, 0), 0, 1024);
            nint matrices = SafeMemory.ReadPointer(table + 0xB8);
            nint refsAt = SafeMemory.ReadPointer(table + 0xC0);
            int count = SafeMemory.Read<int>(table + 0xC8, 0);
            if(!SafeMemory.Plausible(matrices) || !SafeMemory.Plausible(refsAt) || count <= 0 || count > 1024)
                continue;
            var floats = new float[count * 12];
            var refs = new ushort[count];
            fixed(float* to = floats)
                if(!SafeMemory.Copy(matrices, to, count * 48))
                    continue;
            fixed(ushort* to = refs)
                if(!SafeMemory.Copy(refsAt, to, count * 2))
                    continue;
            for(int e = 0; e < count; e++)
            {
                var one = new float[12];
                Array.Copy(floats, e * 12, one, 0, 12);
                raw.Add((refs[e] >> 12, refs[e] & 0xFFF, one));
            }
        }
        // How the 3x4s are laid out: whichever puts the bones nearer where they are.
        float errA = 0f, errB = 0f;
        foreach(var (partial, bone, f) in raw)
        {
            if(partial != 0 || bone >= sk.Info.Count)
                continue;
            var at = sk.GetModelPosition(bone);
            if(Matrix4x4.Invert(BodySwap.ToRowVector(f, 0, true), out var a))
                errA += MathF.Min(Vector3.Distance(a.Translation, at), 10f);
            else
                errA += 10f;
            if(Matrix4x4.Invert(BodySwap.ToRowVector(f, 0, false), out var b))
                errB += MathF.Min(Vector3.Distance(b.Translation, at), 10f);
            else
                errB += 10f;
        }
        bool columns = errA <= errB;
        var partialNames = new Dictionary<int, string[]>();
        foreach(var (partial, bone, f) in raw)
        {
            string[]? names;
            if(partial == 0)
                names = sk.Info.Names;
            else if(!partialNames.TryGetValue(partial, out names))
            {
                names = partial < sk.PartialCount ? sk.PartialInfo(partial, Cache)?.Names : null;
                partialNames[partial] = names ?? [];
            }
            if(names == null || bone >= names.Length)
                continue;
            string name = names[bone];
            if(!binds.ByName.ContainsKey(name))
                binds.ByName[name] = (partial, bone, BodySwap.ToRowVector(f, 0, columns));
        }
        if(known != null)
        {
            binds.GameOk = known.GameOk;
            binds.GameChecked = known.GameChecked;
        }
        BindsOf[model] = binds;
        return binds;
    }

    // ---- The game's own skinning matrices for this frame (the very ones it drew the character with)
    // Each bone table keeps four ring buffers (+0x90), the frame's slot at Render::Manager+0x38384; each buffer's CPU copy
    // (+0x88) holds a 3x4 per bone, column by column, in the camera's view space. Which slot is this frame's at the time
    // Limber draws is learned: the one whose contents just changed.

    private static int _ringOffset;
    private static nint _ringTable;
    private static readonly Vector3[] _ringLast = new Vector3[4];

    private static int CurrentSlot()
    {
        var manager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
        if(manager == null)
            return -1;
        int slot = SafeMemory.Read<int>((nint)manager + 0x38384, -1);
        return slot is >= 0 and <= 3 ? slot : -1;
    }

    private static nint RingData(nint table, int slot)
    {
        nint buffer = SafeMemory.ReadPointer(table + 0x90 + slot * 8);
        if(!SafeMemory.Plausible(buffer))
            return 0;
        nint data = SafeMemory.ReadPointer(buffer + 0x88);
        return SafeMemory.Plausible(data) ? data : 0;
    }

    private static void LearnRing(nint table)
    {
        int s = CurrentSlot();
        if(s < 0)
            return;
        Span<Vector3> now = stackalloc Vector3[4];
        for(int k = 0; k < 4; k++)
        {
            nint data = RingData(table, k);
            now[k] = data == 0 ? Vector3.Zero : SafeMemory.Read<Vector3>(data + 36, Vector3.Zero);
        }
        if(table == _ringTable)
        {
            int changed = -1, count = 0;
            for(int k = 0; k < 4; k++)
                if(Vector3.DistanceSquared(now[k], _ringLast[k]) > 1e-12f)
                {
                    changed = k;
                    count++;
                }
            if(count == 1)
                _ringOffset = (changed - s) & 3;
        }
        _ringTable = table;
        for(int k = 0; k < 4; k++)
            _ringLast[k] = now[k];
    }

    private static bool GameMatrices(nint table, int count, in Matrix4x4 viewToWorld, Matrix4x4[] into)
    {
        int s = CurrentSlot();
        if(s < 0 || count <= 0)
            return false;
        nint data = RingData(table, (s + _ringOffset) & 3);
        if(data == 0)
            return false;
        int n = Math.Min(count, into.Length);
        var f = new float[n * 12];
        fixed(float* to = f)
            if(!SafeMemory.Copy(data, to, n * 48))
                return false;
        for(int e = 0; e < n; e++)
        {
            int o = e * 12;
            var g = new Matrix4x4(f[o], f[o + 1], f[o + 2], 0f, f[o + 3], f[o + 4], f[o + 5], 0f, f[o + 6], f[o + 7], f[o + 8], 0f, f[o + 9], f[o + 10], f[o + 11], 1f);
            into[e] = g * viewToWorld;
        }
        return true;
    }

    private static bool BoneWorld(in ActorSkeleton sk, int partial, int bone, out Matrix4x4 m)
    {
        m = Matrix4x4.Identity;
        Vector3 position, scale;
        Quaternion rotation;
        if(partial == 0)
        {
            if(bone >= sk.Info.Count)
                return false;
            var f = sk.GetModelFrame(bone);
            position = f.Position;
            rotation = f.Rotation;
            scale = sk.GetModelScale(bone);
        }
        else
        {
            if(partial >= sk.PartialCount)
                return false;
            var pose = sk.PartialPose(partial);
            if(pose == null || pose->Skeleton == null || bone >= pose->Skeleton->ReferencePose.Length)
                return false;
            var f = sk.GetPartialFrame(partial, bone);
            position = f.Position;
            rotation = f.Rotation;
            scale = sk.GetPartialScale(partial, bone);
        }
        m = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position) * sk.ModelToWorld;
        return MathUtil.IsFinite(m.Translation);
    }

    // ---- Each frame: the chosen characters skinned into one list

    private static Vert[] _verts = new Vert[4096];
    private static uint[] _indices = new uint[4096];
    private static uint[] _sent = new uint[4096];
    private static int _uploadedIndices;
    private static nint _indicesIn;
    private static readonly List<LookGpu> _looks = new();
    private static readonly Vector3[] _probes = new Vector3[4];
    private static int _probeCount;

    private static void Grow<T>(ref T[] array, int need)
    {
        if(array.Length < need)
            Array.Resize(ref array, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)need));
    }

    private static LookGpu ToGpu(ShaderLook l)
    {
        Vector3 C(float[]? a, Vector3 d) => a is { Length: >= 3 } ? new Vector3(a[0], a[1], a[2]) : d;
        return new LookGpu
        {
            Light = new Vector4(C(l.Light, new(1f, 0.55f, 0.85f)), Math.Max(0f, l.LightStrength)),
            Rim = new Vector4(C(l.Rim, new(0.55f, 0.8f, 1f)), Math.Max(0f, l.RimStrength)),
            Outline = new Vector4(C(l.Outline, Vector3.Zero), Math.Clamp(l.OutlineWidth, 0f, 50f) * 0.001f),
            Misc = new Vector4(Math.Clamp(l.RimTightness, 0.5f, 12f), Math.Max(0f, l.Pulse), l.SeeThrough ? 1f : 0f, 0f),
        };
    }

    private static (int Verts, int Indices, bool Hull, bool Surface) Gather(IReadOnlyList<Shaders.EffectTarget> targets)
    {
        int nv = 0, ni = 0;
        bool hull = false, surface = false;
        _looks.Clear();
        _probeCount = 0;
        long now = Environment.TickCount64;
        var seenModels = new HashSet<nint>();
        bool haveView = CameraUtil.TryGetMatrices(out var cameraView, out _);
        var viewToWorld = Matrix4x4.Identity;
        haveView = haveView && Matrix4x4.Invert(cameraView, out viewToWorld);
        bool ringLearned = false;
        foreach(var target in targets)
        {
            if(!ActorSkeleton.TryGet(target.Address, target.Index, Cache, out var sk))
                continue;
            // its looks (a part's own look wins over All)
            var lookOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach(var l in target.Looks.Where(l => l.Enabled && l.HasEffects()).OrderBy(l => l.Part == "All" ? 0 : 1))
            {
                lookOf[l.Part] = _looks.Count;
                var g = ToGpu(l);
                _looks.Add(g);
                hull |= g.Outline.W > 0f;
                surface |= g.Light.W > 0f || g.Rim.W > 0f || g.Misc.Z > 0.5f;
            }
            if(lookOf.Count == 0)
                continue;
            if(_probeCount < 4)
                foreach(var name in new[] { "j_sebo_b", "j_kosi", "j_kubi", "j_sebo_c" })
                {
                    int b = sk.Info.IndexOf(name);
                    if(b >= 0 && _probeCount < 4)
                        _probes[_probeCount++] = sk.ModelToWorldPoint(sk.GetModelPosition(b));
                }
            var cb = sk.CharacterBase;
            int slots = SafeMemory.Read<int>((nint)(&cb->SlotCount), 0);
            nint models = SafeMemory.ReadPointer((nint)(&cb->Models));
            if(slots <= 0 || slots > 64 || models == 0)
                continue;
            var worn = LiveMesh.ModelFiles((nint)cb);
            bool hat = worn.Any(w => w.File.EndsWith("_met.mdl", StringComparison.OrdinalIgnoreCase));
            foreach(var (slot, file) in worn)
            {
                if(!file.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || slot >= slots)
                    continue;
                var data = ModelOf(file);
                nint model = SafeMemory.ReadPointer(models + slot * 8);
                if(data == null || !SafeMemory.Plausible(model))
                    continue;
                seenModels.Add(model);
                var binds = ReadBinds(model, sk, now);
                if(binds == null || binds.ByName.Count == 0)
                    continue;
                // which of its parts and shape keys the game has on for this character
                var rm = (FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model*)model;
                uint attributes = SafeMemory.Read<uint>((nint)(&rm->EnabledAttributeIndexMask), uint.MaxValue);
                uint shapes = SafeMemory.Read<uint>((nint)(&rm->EnabledShapeKeyIndexMask), 0u);
                // (a hat on: the hair's parts it covers go, as the game draws them)
                if(hat && data.Meshes.Count > 0)
                    attributes &= ~data.Meshes[0].HatMask;
                if(Reported.Add((file, attributes, shapes)))
                    Plugin.Log.Information($"Limber: effects on {file}: attributes {attributes:X8}, shape keys {shapes:X8}; "
                        + string.Join(", ", data.Meshes.Select((m, i) => $"mesh {i}: {m.Subs.Count} parts ({string.Join("/", m.Subs.Select(x => $"{x.Attributes:X}"))}), {m.Shapes.Count} shapes, {m.Tris(attributes, shapes).Length / 3} of {m.Raw.Length / 3} triangles")));
                foreach(var mesh in data.Meshes)
                {
                    var meshTris = mesh.Tris(attributes, shapes);
                    if(meshTris.Length == 0)
                        continue;
                    // the mesh's bones, where they are now (a bone the game doesn't skin it with: the mesh left out)
                    var mats = new Matrix4x4[mesh.Bones.Length];
                    var known = new bool[mesh.Bones.Length];
                    bool game = false;
                    // the game's own matrices for this frame when they can be read (and agree with the bones)
                    if(haveView && mesh.Table >= 0 && mesh.Table < binds.Tables.Length && binds.Counts[mesh.Table] >= mesh.Bones.Length
                       && GameMatrices(binds.Tables[mesh.Table], mesh.Bones.Length, viewToWorld, mats))
                    {
                        if(_ringTable == 0 || !ringLearned)
                        {
                            LearnRing(binds.Tables[mesh.Table]);
                            ringLearned = true;
                        }
                        if(now >= binds.GameChecked)
                        {
                            binds.GameChecked = now + 250;
                            float err = 0f;
                            int tested = 0;
                            for(int b = 0; b < mesh.Bones.Length && tested < 4; b++)
                            {
                                if(!binds.ByName.TryGetValue(mesh.Bones[b], out var bind) || bind.Partial != 0
                                   || !BoneWorld(sk, 0, bind.Bone, out var world) || !Matrix4x4.Invert(bind.Inverse, out var rest))
                                    continue;
                                err += Vector3.Distance(Vector3.Transform(rest.Translation, mats[b]), world.Translation);
                                tested++;
                            }
                            bool was = binds.GameOk;
                            if(tested > 0)
                                binds.GameOk = err / tested < 0.3f;
                            if(tested > 0 && (binds.GameOk != was || !ReportedGame.Contains(file)))
                            {
                                ReportedGame.Add(file);
                                Plugin.Log.Information($"Limber: effects on {file}: the game's own matrices {(binds.GameOk ? "used" : "not used")} (off by {(tested > 0 ? err / tested : -1f):0.000} m, ring {_ringOffset})");
                            }
                        }
                        game = binds.GameOk;
                    }
                    bool ok = true;
                    for(int b = 0; b < mesh.Bones.Length && ok && !game; b++)
                    {
                        if(!binds.ByName.TryGetValue(mesh.Bones[b], out var bind))
                        {
                            mats[b] = Matrix4x4.Identity;
                            continue; // (checked per vertex below)
                        }
                        ok = BoneWorld(sk, bind.Partial, bind.Bone, out var world);
                        mats[b] = bind.Inverse * world;
                    }
                    if(!ok)
                        continue;
                    for(int b = 0; b < known.Length; b++)
                        known[b] = game || binds.ByName.ContainsKey(mesh.Bones[b]);
                    int look = lookOf.TryGetValue(mesh.Part, out var own) ? own : lookOf.TryGetValue("All", out var all) ? all : -1;
                    int n = mesh.Pos.Length;
                    Grow(ref _verts, nv + n);
                    Grow(ref _indices, ni + meshTris.Length);
                    int start = nv;
                    var verts = _verts;
                    var toWorld = sk.ModelToWorld;
                    void One(int v)
                    {
                        var p = Vector3.Zero;
                        var nn = Vector3.Zero;
                        float used = 0f;
                        for(int q = 0; q < 4; q++)
                        {
                            float w = mesh.Bw[v * 4 + q];
                            int b = mesh.Bi[v * 4 + q];
                            if(w <= 0f || b >= mats.Length || !known[b])
                                continue;
                            p += Vector3.Transform(mesh.Pos[v], mats[b]) * w;
                            nn += Vector3.TransformNormal(mesh.Nrm[v], mats[b]) * w;
                            used += w;
                        }
                        if(used < 0.01f)
                            p = Vector3.Transform(mesh.Pos[v], toWorld); // (unskinned: where the file has it)
                        else
                            p /= used;
                        nn = nn.LengthSquared() > 1e-12f ? Vector3.Normalize(nn) : Vector3.UnitY;
                        verts[start + v] = new Vert { P = new Vector4(p, 1f), N = new Vector4(nn, look) };
                    }
                    if(n >= 4096)
                        Parallel.For(0, n, One);
                    else
                        for(int v = 0; v < n; v++)
                            One(v);
                    for(int t = 0; t < meshTris.Length; t++)
                        _indices[ni + t] = (uint)(start + meshTris[t]);
                    nv += n;
                    ni += meshTris.Length;
                }
            }
        }
        // (models gone let go of)
        if(BindsOf.Count > seenModels.Count + 32)
            foreach(var k in BindsOf.Keys.Where(k => !seenModels.Contains(k)).ToList())
                BindsOf.Remove(k);
        return (nv, ni, hull, surface);
    }

    // ---- The device's state, kept and put back

    private struct Saved
    {
        public fixed long Rtv[8];
        public nint Dsv, Blend, Depth, Raster, Vs, Ps, Layout, VsCb, PsCb, Sampler0;
        public fixed long PsSrv[5];
        public fixed long VsSrv[5];
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
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 73))(c, 0, 5, s->PsSrv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 84))(c, 0, 5, s->VsSrv);
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
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 8))(c, 0, 5, s->PsSrv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 25))(c, 0, 5, s->VsSrv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 10))(c, 0, 1, &s->Sampler0);
        for(int i = 0; i < 8; i++)
            Release((nint)s->Rtv[i]);
        for(int i = 0; i < 5; i++)
        {
            Release((nint)s->PsSrv[i]);
            Release((nint)s->VsSrv[i]);
        }
        for(uint i = 0; i < s->VsInstances; i++)
            Release((nint)s->VsClass[i]);
        for(uint i = 0; i < s->PsInstances; i++)
            Release((nint)s->PsClass[i]);
        foreach(var o in new[] { s->Dsv, s->Blend, s->Depth, s->Raster, s->Vs, s->Ps, s->Layout, s->VsCb, s->PsCb, s->Sampler0 })
            Release(o);
        NativeMemory.Clear(s, (nuint)sizeof(Saved));
    }

    private static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>Draws the effects on the characters that have them (render thread). Nothing when nobody has one.</summary>
    // What was gathered on the last few frames (the picture on screen may be a frame or more older than the bones read now).
    private sealed class Snap
    {
        public Vert[] V = [];
        public uint[] I = [];
        public int Nv, Ni;
        public LookGpu[] Looks = [];
        public bool Hull, Surface, Valid;
        public Matrix4x4 Vp;
        public Vector3[] Probes = new Vector3[4];
        public int ProbeCount;
    }

    private static readonly Snap[] Ring = [new(), new(), new(), new()];
    private static int _snapAt;

    public static void Draw(IReadOnlyList<Shaders.EffectTarget> targets, int delay)
    {
        Drawn = 0;
        if(_failed || targets.Count == 0)
        {
            foreach(var old in Ring)
                old.Valid = false;
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
            var (gv, gi, gh, gs) = Gather(targets);
            var control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Instance();
            var now = Ring[_snapAt];
            (now.V, _verts) = (_verts, now.V);
            (now.I, _indices) = (_indices, now.I);
            now.Nv = gv;
            now.Ni = gi;
            now.Looks = _looks.ToArray();
            now.Hull = gh;
            now.Surface = gs;
            now.Vp = control != null ? control->ViewProjectionMatrix : Matrix4x4.Identity;
            now.ProbeCount = _probeCount;
            Array.Copy(_probes, now.Probes, 4);
            now.Valid = control != null && gv > 0 && gi > 0 && now.Looks.Length > 0 && (gh || gs);
            _snapAt = (_snapAt + 1) & 3;
            // (drawn as it was the given number of frames ago: in step with the picture on screen)
            var snap = Ring[(_snapAt - 1 - Math.Clamp(delay, 0, 3)) & 3];
            if(!snap.Valid)
                return;
            int nv = snap.Nv, ni = snap.Ni;
            bool hull = snap.Hull, surface = snap.Surface;
            if(_vsMesh == 0)
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
            EnsureOwnDepth(bd.Width, bd.Height);
            Verts.Ensure(nv);
            Indices.Ensure(ni);
            Looks.Ensure(snap.Looks.Length);

            nint sceneSrv = 0;
            var uvScale = Vector2.One;
            var rtm = RenderTargetManager.Instance();
            if(rtm != null && rtm->DepthStencil != null && rtm->DepthStencil->D3D11ShaderResourceView != null)
            {
                var ds = rtm->DepthStencil;
                sceneSrv = (nint)ds->D3D11ShaderResourceView;
                if(ds->AllocatedWidth > 0 && ds->AllocatedHeight > 0)
                    uvScale = new Vector2((float)ds->ActualWidth / ds->AllocatedWidth, (float)ds->ActualHeight / ds->AllocatedHeight);
            }
            var constants = MakeConstants(snap, bd.Width, bd.Height, uvScale, sceneSrv != 0, out bool reversed);
            if(constants == null)
                return;

            Save(c);
            saved = true;

            fixed(Vert* p = snap.V)
                if(!Verts.Write(c, p, nv))
                    return;
            // (the triangles sent again only when they change)
            if(ni != _uploadedIndices || Indices.Buffer != _indicesIn || !snap.I.AsSpan(0, ni).SequenceEqual(_sent.AsSpan(0, ni)))
            {
                fixed(uint* p = snap.I)
                    if(!Indices.Write(c, p, ni))
                        return;
                Grow(ref _sent, ni);
                Array.Copy(snap.I, _sent, ni);
                _uploadedIndices = ni;
                _indicesIn = Indices.Buffer;
            }
            var looks = snap.Looks;
            fixed(LookGpu* p = looks)
                if(!Looks.Write(c, p, looks.Length))
                    return;
            Mapped m;
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
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 43))(c, _rasterNone);
            float* zeroFactor = stackalloc float[4];
            nint* none = stackalloc nint[5];

            // 0. which way the game's depth reads (from points inside the bodies)
            nint prt = _probeRt;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 5, none);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 25))(c, 0, 5, none);
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &prt, 0);
            var one = new Viewport { Width = 1, Height = 1, MinDepth = 0, MaxDepth = 1 };
            ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(c, 44))(c, 1, &one);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _depthOff, 0);
            if(sceneSrv != 0)
            {
                nint* probeIn = stackalloc nint[2] { 0, sceneSrv };
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 2, probeIn);
                ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendNone, zeroFactor, 0xFFFFFFFF);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vsFull, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psProbe, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 3, 0);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 5, none);
            }

            var vp = new Viewport { Width = bd.Width, Height = bd.Height, MinDepth = 0, MaxDepth = 1 };
            ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(c, 44))(c, 1, &vp);
            nint* inputs = stackalloc nint[5] { Verts.Srv, sceneSrv, _probeSrv, Indices.Srv, Looks.Srv };

            // 1. the characters' depth, into Limber's own buffer (everything they wear: clothes hide the skin under them)
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, float, byte, void>)V(c, 53))(c, _ownDsv, 1, reversed ? 0f : 1f, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 0, null, _ownDsv);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 25))(c, 0, 5, inputs);
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendNone, zeroFactor, 0xFFFFFFFF);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, reversed ? _depthWriteR : _depthWrite, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vsMesh, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, 0, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, (uint)ni, 0);

            // then onto the picture, tested against that depth
            nint rtv = backRtv;
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rtv, _ownDsv);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 5, inputs);

            // 2. the outline: the back of the blown-up copy, behind the character's own front
            if(hull)
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendSolid, zeroFactor, 0xFFFFFFFF);
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, reversed ? _depthBehindR : _depthBehind, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vsHull, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psHull, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, (uint)ni, 0);
            }

            // 3. the glow and rim, added on (the front layer only)
            if(surface)
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendAdd, zeroFactor, 0xFFFFFFFF);
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, reversed ? _depthEqualR : _depthEqual, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vsMesh, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psSurface, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, (uint)ni, 0);
            }

            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 5, none);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 25))(c, 0, 5, none);
            Drawn = nv;
            _error = "";
        }
        catch(Exception e)
        {
            _failed = true;
            _error = e.Message;
            Plugin.Log.Warning(e, "Limber: the shader effects can't be drawn");
        }
        finally
        {
            if(saved)
                Restore(c);
            Release(backRtv);
            Release(backTex);
        }
    }

    private static Constants? MakeConstants(Snap snap, uint w, uint h, Vector2 uvScale, bool useScene, out bool reversed)
    {
        reversed = false;
        var vp = snap.Vp;
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
        if(!MathUtil.IsFinite(cam) || !MathUtil.IsFinite(center))
            return null;
        // which way depth runs: a point a step ahead against one far ahead
        float Depth(Vector3 at)
        {
            var c = Vector4.Transform(new Vector4(at, 1), vp);
            return MathF.Abs(c.W) < 1e-8f ? 0f : c.Z / c.W;
        }
        reversed = Depth(cam + center * 50f) < Depth(cam + center * 1f);
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
        Vector4 P(int i) => i < snap.ProbeCount ? new Vector4(snap.Probes[i], 1) : default;
        return new Constants
        {
            ViewProj = vp,
            InvViewProj = ivp,
            Cam = new Vector4(cam, (float)(Environment.TickCount64 % 3_600_000) / 1000f),
            Screen = new Vector4(w, h, 1f / w, 1f / h),
            SceneUv = new Vector4(uvScale.X, uvScale.Y, 0, useScene ? 1 : 0),
            Fwd = new Vector4(center, 0),
            Extra = new Vector4(camNear, camFar, 0, 0),
            Probe0 = P(0),
            Probe1 = P(1),
            Probe2 = P(2),
            Probe3 = P(3),
        };
    }
}
