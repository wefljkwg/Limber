using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Limber.Rig;

namespace Limber.Gpu;

/// <summary>
/// Draws the Climax liquid into the game's picture, as one smooth wet surface: every drop and rope join is drawn as a
/// real 3D shape (worked out per pixel), hidden behind anything the game drew in front of it (its depth buffer); then
/// the drops' depths are smoothed together so they read as one liquid, lit, made glossy, and shown through where
/// thin. Direct3D 11 through the device's own tables (no extra libraries); the shaders are compiled once by Windows'
/// own shader compiler. Render thread, just before the frame is presented (before the plugin windows are drawn).
/// Every piece of the device's state it changes is put back afterwards.
/// </summary>
public static unsafe class FluidRenderer
{
    // ---- Direct3D 11 by vtable

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
        public uint WriteMask; // (a byte, padded)
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
        public uint StencilMasks; // read mask, write mask (bytes), padded
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

    // Formats and flags
    private const uint FormatR32F = 41, FormatR16F = 54, FormatD32F = 40;
    private const uint BindSrv = 0x8, BindRt = 0x20, BindDepth = 0x40, BindConstant = 0x4;
    private const uint UsageDefault = 0, UsageDynamic = 2, CpuWrite = 0x10000, MiscStructured = 0x40;

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(byte* src, nuint size, byte* name, void* defines, void* include, byte* entry, byte* target, uint flags1, uint flags2, nint* code, nint* errors);

    // ---- What it keeps

    private static bool _failed;
    private static string _error = "";
    private static nint _vsShape, _psDepth, _psThick, _vsFull, _psBlur, _psComposite, _psProbe, _probe, _probeRt, _probeSrv, _probeStage;

    /// <summary>How the game's depth was found to be stored (0..3, see the shader's DistanceIn), 4 = couldn't tell,
    /// -1 = not looked at yet (read back from the GPU a frame or so late).</summary>
    public static int FoundDepth { get; private set; } = -1;
    private static nint _cb, _pieces, _piecesSrv;
    private static int _piecesCapacity;
    private static nint _blendNone, _blendAdd, _blendAlpha, _depthLess, _depthOff, _rasterBack, _rasterNone, _samplerPoint, _samplerLinear;
    // screen-sized: the liquid's depth (two, for smoothing), its thickness, our own depth buffer, a copy of the picture
    private static uint _w, _h, _bgFormat;
    private static nint _depthA, _depthArt, _depthAsrv, _depthB, _depthBrt, _depthBsrv, _thick, _thickRt, _thickSrv, _thickB, _thickBrt, _thickBsrv, _zbuf, _zbufDsv, _bg, _bgSrv;
    private static ClimaxService.Piece[] _take = new ClimaxService.Piece[1024];

    /// <summary>Why it isn't drawing (empty when it is fine).</summary>
    public static string Error => _error;

    public static void Dispose()
    {
        FreeScreen();
        foreach(var o in new[] { _psProbe, _probe, _probeRt, _probeSrv, _probeStage })
            Release(o);
        _psProbe = _probe = _probeRt = _probeSrv = _probeStage = 0;
        foreach(var o in new[] { _vsShape, _psDepth, _psThick, _vsFull, _psBlur, _psComposite, _cb, _pieces, _piecesSrv, _blendNone, _blendAdd, _blendAlpha, _depthLess, _depthOff, _rasterBack, _rasterNone, _samplerPoint, _samplerLinear })
            Release(o);
        _vsShape = _psDepth = _psThick = _vsFull = _psBlur = _psComposite = _cb = _pieces = _piecesSrv = 0;
        _blendNone = _blendAdd = _blendAlpha = _depthLess = _depthOff = _rasterBack = _rasterNone = _samplerPoint = _samplerLinear = 0;
        _piecesCapacity = 0;
    }

    private static void FreeScreen()
    {
        foreach(var o in new[] { _depthA, _depthArt, _depthAsrv, _depthB, _depthBrt, _depthBsrv, _thick, _thickRt, _thickSrv, _thickB, _thickBrt, _thickBsrv, _zbuf, _zbufDsv, _bg, _bgSrv })
            Release(o);
        _depthA = _depthArt = _depthAsrv = _depthB = _depthBrt = _depthBsrv = _thick = _thickRt = _thickSrv = _thickB = _thickBrt = _thickBsrv = _zbuf = _zbufDsv = _bg = _bgSrv = 0;
        _w = _h = 0;
    }

    // ---- The constants the shaders read (must match the HLSL below)

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProj, InvViewProj;
        public Vector4 Cam;      // camera position
        public Vector4 Screen;   // w, h, 1/w, 1/h
        public Vector4 SceneUv;  // depth texture uv scale (x, y), flip, use
        public Vector4 Blur;     // direction x, y, smoothing (m), pixels per metre at 1 m
        public Vector4 Blur2;    // merge depth (m), most pixels, 0, 0
        public Vector4 Color;    // rgb, opacity
        public Vector4 Shade1;   // gloss, shine, rim, ambient
        public Vector4 Shade2;   // light direction xyz, light strength
        public Vector4 Shade3;   // translucency, absorb, refraction, edge sharpness
        public Vector4 Shade4;   // reflection, softness, gloss2, shine2
        public Vector4 Right;    // camera right
        public Vector4 Up;       // camera up
        public Vector4 Probe0, Probe1, Probe2, Probe3; // points inside bodies (w = 1: there), to tell how the depth reads
        public Vector4 Extra;    // transparency, camera near, camera far, subsurface glow
        public Vector4 Fwd;      // camera forward
        public Vector4 SceneLight; // the game's colour (albedo) buffer: uv scale (x, y), 1 = there (else 2: light buffer only), how much
        public Vector4 Blur3;    // edge smoothing (pixels), 0, 0, 0
    }

    private const string Hlsl = @"
cbuffer C : register(b0)
{
    row_major float4x4 VP; row_major float4x4 IVP;
    float4 Cam; float4 Screen; float4 SceneUv; float4 Blur; float4 Blur2; float4 Color;
    float4 Shade1; float4 Shade2; float4 Shade3; float4 Shade4; float4 Right; float4 Up;
    float4 Probe[4];
    float4 Extra;
    float4 Fwd;
    float4 SceneLight;
    float4 Blur3;
};
struct Piece { float4 A; float4 B; float4 U; float4 V; };
StructuredBuffer<Piece> Pieces : register(t0);
Texture2D<float> SceneDepth : register(t1);
Texture2D<float> LiquidDepth : register(t2);
Texture2D<float> Thickness : register(t3);
Texture2D<float4> Picture : register(t4);
Texture2D<float> DepthMode : register(t5);
Texture2D<float4> LightDiffuse : register(t6);
Texture2D<float4> Albedo : register(t7);
SamplerState SampPoint : register(s0);
SamplerState SampLinear : register(s1);

static const float3 Corners[8] = { float3(-1,-1,-1), float3(1,-1,-1), float3(1,1,-1), float3(-1,1,-1), float3(-1,-1,1), float3(1,-1,1), float3(1,1,1), float3(-1,1,1) };
static const uint Faces[36] = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };

struct ShapeOut { float4 pos : SV_Position; nointerpolation float4 A : TEXCOORD0; nointerpolation float4 B : TEXCOORD1; float3 wp : TEXCOORD2; nointerpolation float4 U : TEXCOORD3; nointerpolation float4 V : TEXCOORD4; };

ShapeOut VsShape(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Piece p = Pieces[iid];
    float3 a = p.A.xyz, b = p.B.xyz; float r = max(abs(p.A.w), abs(p.B.w)) * (p.B.w < 0 ? Blur2.z : 1) * 1.05;
    float3 ax = b - a; float len = length(ax);
    float3 X = len > 1e-6 ? ax / len : float3(0, 1, 0);
    float3 hint = abs(X.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 Y = normalize(cross(X, hint)); float3 Z = cross(X, Y);
    float3 c = Corners[Faces[vid]];
    float3 wp = (a + b) * 0.5 + X * c.x * (len * 0.5 + r) + Y * c.y * r + Z * c.z * r;
    // (an ellipsoid: its box along its own axes)
    if (p.U.w > 0.5) wp = a + (p.U.xyz * c.x + p.V.xyz * c.y + p.B.xyz * c.z) * 1.02;
    ShapeOut o; o.pos = mul(float4(wp, 1), VP); o.A = p.A; o.B = p.B; o.U = p.U; o.V = p.V; o.wp = wp; return o;
}

// distance along the ray to the capsule (or sphere), -1 if missed
float Hit(float3 ro, float3 rd, float3 pa, float3 pb, float r)
{
    float3 ba = pb - pa; float3 oa = ro - pa;
    float baba = dot(ba, ba);
    if (baba < 1e-12)
    {
        float b0 = dot(oa, rd); float c0 = dot(oa, oa) - r * r; float h0 = b0 * b0 - c0;
        return h0 < 0 ? -1 : -b0 - sqrt(h0);
    }
    float bard = dot(ba, rd), baoa = dot(ba, oa), rdoa = dot(rd, oa), oaoa = dot(oa, oa);
    float a = baba - bard * bard, b = baba * rdoa - baoa * bard, c = baba * oaoa - baoa * baoa - r * r * baba;
    float h = b * b - a * c;
    if (h >= 0 && a > 1e-12)
    {
        float t = (-b - sqrt(h)) / a; float y = baoa + t * bard;
        if (y > 0 && y < baba) return t;
        float3 oc = (y <= 0) ? oa : ro - pb;
        b = dot(rd, oc); c = dot(oc, oc) - r * r; h = b * b - c;
        if (h > 0) return -b - sqrt(h);
    }
    else
    {
        float3 oc = oa; b = dot(rd, oc); c = dot(oc, oc) - r * r; h = b * b - c;
        if (h > 0) return -b - sqrt(h);
    }
    return -1;
}

float DistanceAt(float2 uv, float d)
{
    float4 w = mul(float4(uv.x * 2 - 1, 1 - uv.y * 2, d, 1), IVP);
    if (abs(w.w) < 1e-8) return 1e9;
    float3 p = w.xyz / w.w;
    float dist = length(p - Cam.xyz);
    return (dist > 0 && dist < 1e8) ? dist : 1e9;
}

float3 RayAt(float2 uv)
{
    float4 w = mul(float4(uv.x * 2 - 1, 1 - uv.y * 2, 0.5, 1), IVP);
    return normalize(w.xyz / w.w - Cam.xyz);
}

// The ways the game's depth might be stored: 0 through the view matrix as it is, 1 the same the other way round,
// 2 the camera's near plane over the distance (reversed, no far plane), 3 between the near and far planes.
float DistanceIn(float2 uv, float d, int mode)
{
    if (mode == 0) return DistanceAt(uv, d);
    if (mode == 1) return DistanceAt(uv, 1 - d);
    float z;
    if (mode == 2) z = Extra.y / max(d, 1e-7);
    else z = Extra.y * Extra.z / max(Extra.z - d * (Extra.z - Extra.y), 1e-6);
    float along = dot(RayAt(uv), Fwd.xyz);
    return (z > 0 && z < 1e8 && along > 1e-4) ? z / along : 1e9;
}

// which way it's read (4: unknown, nothing is hidden)
int Mode()
{
    if (SceneUv.z > 0.5) return (int)round(SceneUv.z) - 1;
    return (int)round(DepthMode.Load(int3(0, 0, 0)));
}

// how far the scene is along this pixel's ray (huge where nothing was drawn, or the depth can't be read)
float SceneDistance(float2 uv)
{
    int mode = Mode();
    if (mode > 3) return 1e9;
    float d = SceneDepth.SampleLevel(SampPoint, uv * SceneUv.xy, 0);
    return DistanceIn(uv, d, mode);
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

float3 RayOf(float2 uv)
{
    float4 w = mul(float4(uv.x * 2 - 1, 1 - uv.y * 2, 0.5, 1), IVP);
    return normalize(w.xyz / w.w - Cam.xyz);
}

struct DepthOut { float t : SV_Target0; float z : SV_Depth; };

// squared distance from the ray to the segment's nearest point, and how far along the ray that point is
float RaySeg(float3 ro, float3 rd, float3 a, float3 b, out float along)
{
    float3 ba = b - a; float L2 = dot(ba, ba); float k = 0;
    if (L2 > 1e-12)
    {
        float3 oa = a - ro; float bd = dot(ba, rd); float den = L2 - bd * bd;
        k = den > 1e-12 ? saturate((bd * dot(oa, rd) - dot(ba, oa)) / den) : 0;
    }
    float3 c = a + ba * k - ro; along = dot(c, rd); float3 perp = c - rd * along; return dot(perp, perp);
}

// Liquid lying on a surface (a negative radius): a low dome laid on whatever the game drew there, so it takes the
// body's real shape (not the rough shapes it was stuck to); where nothing near was drawn, a dome at its own place.
// The distance along the ray, -1 if missed; h = how deep the liquid is there.
float Coat(float3 ro, float3 rd, float4 A, float4 B, float sceneT, out float h)
{
    float R = abs(A.w) * Blur2.z; float along; float d2 = RaySeg(ro, rd, A.xyz, B.xyz, along);
    h = 0;
    if (d2 >= R * R) return -1;
    h = sqrt(R * R - d2) * Blur2.w;
    float tol = max(R * 2.5, 0.03);
    if (sceneT > 1e8 || abs(sceneT - along) > tol) return along - h;
    return sceneT - h;
}

// A ray through an ellipsoid (centre c, axes a1 a2 a3, each as long as that radius): the distance in (-1 if missed),
// and the chord through it.
float Ell(float3 ro, float3 rd, float3 c, float3 a1, float3 a2, float3 a3, out float chord)
{
    float3 r0 = cross(a2, a3), r1 = cross(a3, a1), r2 = cross(a1, a2); float det = dot(a1, r0);
    chord = 0;
    if (abs(det) < 1e-15) return -1;
    float3 o = ro - c;
    float3 lo = float3(dot(r0, o), dot(r1, o), dot(r2, o)) / det;
    float3 ld = float3(dot(r0, rd), dot(r1, rd), dot(r2, rd)) / det;
    float A = dot(ld, ld), B = dot(lo, ld), C = dot(lo, lo) - 1; float h = B * B - A * C;
    if (h < 0) return -1;
    float sq = sqrt(h); chord = 2 * sq / A; return (-B - sq) / A;
}

DepthOut PsDepth(ShapeOut i)
{
    float3 ro = Cam.xyz; float3 rd = normalize(i.wp - ro);
    float2 uv = i.pos.xy * Screen.zw;
    float sceneT = SceneUv.w > 0.5 ? SceneDistance(uv) : 1e9;
    float t, h;
    if (i.U.w > 0.5) t = Ell(ro, rd, i.A.xyz, i.U.xyz, i.V.xyz, i.B.xyz, h);
    else if (i.B.w < 0) t = Coat(ro, rd, i.A, i.B, sceneT, h);
    else t = Hit(ro, rd, i.A.xyz, i.B.xyz, min(i.A.w, i.B.w));
    if (t <= 0) discard;
    if (t > sceneT + 0.004) discard;
    DepthOut o; o.t = t; o.z = saturate(t / 2000.0); return o;
}

float PsThick(ShapeOut i) : SV_Target0
{
    float3 ro = Cam.xyz; float3 rd = normalize(i.wp - ro);
    float2 uv = i.pos.xy * Screen.zw;
    float sceneT = SceneUv.w > 0.5 ? SceneDistance(uv) : 1e9;
    if (i.U.w > 0.5)
    {
        float ch; float te = Ell(ro, rd, i.A.xyz, i.U.xyz, i.V.xyz, i.B.xyz, ch);
        if (te <= 0 || te > sceneT + 0.004) discard;
        return ch;
    }
    if (i.B.w < 0)
    {
        float h; float tc = Coat(ro, rd, i.A, i.B, sceneT, h);
        if (tc <= 0 || tc > sceneT + 0.004) discard;
        return h;
    }
    float r = min(i.A.w, i.B.w);
    float t = Hit(ro, rd, i.A.xyz, i.B.xyz, r);
    if (t <= 0) discard;
    if (t > sceneT + 0.004) discard;
    // the chord through it: from how close the ray passes to its middle line
    float3 ba = i.B.xyz - i.A.xyz; float3 p = ro + rd * (t + r);
    float k = dot(ba, ba) > 1e-12 ? saturate(dot(p - i.A.xyz, ba) / dot(ba, ba)) : 0;
    float3 c = i.A.xyz + ba * k;
    float3 toC = c - ro; float along = dot(toC, rd); float d2 = max(dot(toC, toC) - along * along, 0);
    return 2 * sqrt(max(r * r - d2, 0));
}

struct FullOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

FullOut VsFull(uint vid : SV_VertexID)
{
    FullOut o; float2 uv = float2((vid << 1) & 2, vid & 2);
    o.uv = uv; o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1); return o;
}

struct BlurOut { float t : SV_Target0; float th : SV_Target1; };

// Smooths the liquid's depth so the drops read as one surface (a narrow-range filter: a sample further behind than
// the merge depth is another surface and left out, one closer is held to it), smooths its outline (a pixel goes with
// what most of its neighbours are: dents between drops fill, bumps go) and blurs its thickness (soft edges).
BlurOut PsBlur(FullOut i)
{
    int2 px = int2(i.pos.xy);
    float t0 = LiquidDepth.Load(int3(px, 0));
    BlurOut o;
    // (one radius each for the whole frame, in pixels: a radius changing pixel to pixel leaves a speckle)
    // mass: the surface inside the liquid; edge: its outline and the thickness that softens it
    float massR = clamp(Blur.z, 0, Blur2.y), edgeR = clamp(Blur3.x, 0, Blur2.y);
    float radius = max(massR, edgeR);
    if (radius < 0.5) { o.t = t0; o.th = Thickness.Load(int3(px, 0)); return o; }
    float sum = 0, wsum = 0, fill = 0, fillW = 0, covW = 0, totW = 0, th = 0;
    [loop] for (float s = -radius; s <= radius + 0.001; s += 1.0)
    {
        int2 q = px + int2(Blur.xy * s);
        float t = LiquidDepth.Load(int3(q, 0));
        float xm = massR >= 0.5 ? s / massR : (abs(s) < 0.5 ? 0 : 9);
        float xe = edgeR >= 0.5 ? s / edgeR : (abs(s) < 0.5 ? 0 : 9);
        float gm = abs(xm) <= 1.001 ? exp(-xm * xm * 2.5) : 0;
        float ge = abs(xe) <= 1.001 ? exp(-xe * xe * 2.5) : 0;
        totW += ge; th += Thickness.Load(int3(q, 0)) * ge;
        if (t <= 0) continue;
        covW += ge; fill += t * ge; fillW += ge;
        if (t0 > 0 && t <= t0 + Blur2.x) { sum += max(t, t0 - Blur2.x) * gm; wsum += gm; }
    }
    float cov = covW / max(totW, 1e-6);
    if (t0 <= 0) o.t = (cov > 0.5 && fillW > 0) ? fill / fillW : 0;
    else o.t = cov < 0.3 ? 0 : (wsum > 0 ? sum / wsum : t0);
    o.th = totW > 0 ? th / totW : Thickness.Load(int3(px, 0));
    return o;
}

float3 PosAt(int2 px, float t)
{
    float2 uv = (px + 0.5) * Screen.zw;
    return Cam.xyz + RayOf(uv) * t;
}

float4 PsComposite(FullOut i) : SV_Target0
{
    int2 px = int2(i.pos.xy);
    float t = LiquidDepth.Load(int3(px, 0));
    if (t <= 0) discard;
    float2 uv = (px + 0.5) * Screen.zw;
    float3 rd = RayOf(uv);
    float3 p = Cam.xyz + rd * t;
    float tl = LiquidDepth.Load(int3(px - int2(1, 0), 0)), tr = LiquidDepth.Load(int3(px + int2(1, 0), 0));
    float tu = LiquidDepth.Load(int3(px - int2(0, 1), 0)), td = LiquidDepth.Load(int3(px + int2(0, 1), 0));
    float3 dx, dy;
    float3 pr = PosAt(px + int2(1, 0), tr), pl = PosAt(px - int2(1, 0), tl);
    float3 pd = PosAt(px + int2(0, 1), td), pu = PosAt(px - int2(0, 1), tu);
    float edge = max(t * 0.02, 0.01);
    bool okR = tr > 0 && abs(tr - t) < edge, okL = tl > 0 && abs(tl - t) < edge, okD = td > 0 && abs(td - t) < edge, okU = tu > 0 && abs(tu - t) < edge;
    if (okR && okL) dx = (pr - pl) * 0.5; else if (okR) dx = pr - p; else if (okL) dx = p - pl; else dx = Right.xyz * 0.001;
    if (okD && okU) dy = (pd - pu) * 0.5; else if (okD) dy = pd - p; else if (okU) dy = p - pu; else dy = -Up.xyz * 0.001;
    float3 n = normalize(cross(dy, dx));
    if (dot(n, -rd) < 0) n = -n;

    float thick = Thickness.Load(int3(px, 0));
    float3 v = -rd;
    float3 l = normalize(Shade2.xyz);
    float3 h = normalize(l + v);
    float ndl = dot(n, l), ndh = saturate(dot(n, h)), ndv = saturate(dot(n, v));
    float diffuse = pow(saturate(ndl * 0.5 + 0.5), Shade4.y);
    float spec = pow(ndh, max(Shade1.x, 1)) * Shade1.y + pow(ndh, max(Shade4.z, 1)) * Shade4.w;
    float fres = pow(1 - ndv, 4);
    float2 nScreen = float2(dot(n, Right.xyz), -dot(n, Up.xyz));
    float3 behind = Picture.SampleLevel(SampLinear, uv + nScreen * Shade3.z * 0.03, 0).rgb;
    float3 around = Picture.SampleLevel(SampLinear, uv - nScreen * 0.08, 0).rgb;
    float3 lit = Color.rgb * (Shade1.w + diffuse * Shade2.w + Extra.w) + spec * Shade2.w + around * fres * Shade4.x + fres * Shade1.z * 0.35;
    // Lit like the scene: from the game's own light buffer (the light falling on each spot this frame), taken
    // relative to the frame's light overall (so it doesn't matter how bright the game keeps it), and over a little
    // area (so the details of what's under it don't show through): its shadows, bright spots and lamp colours.
    if (SceneLight.z > 1.5 && SceneLight.w > 0)
    {
        float2 suv = uv * SceneLight.xy;
        float avg = 0;
        [unroll] for (int gy = 0; gy < 4; gy++)
            [unroll] for (int gx = 0; gx < 4; gx++)
                avg += dot(max(LightDiffuse.SampleLevel(SampLinear, (float2(gx, gy) + 0.5) * 0.25 * SceneLight.xy, 0).rgb, 0), float3(0.3, 0.59, 0.11));
        avg /= 16;
        float3 local = 0;
        [unroll] for (int ly = -1; ly <= 1; ly++)
            [unroll] for (int lx = -1; lx <= 1; lx++)
                local += max(LightDiffuse.SampleLevel(SampLinear, suv + float2(lx, ly) * 0.012 * SceneLight.xy, 0).rgb, 0);
        local /= 9;
        float3 rel = local / max(avg, 1e-4);
        float lum = dot(rel, float3(0.3, 0.59, 0.11));
        float3 tint = lum > 1e-4 ? saturate(rel / lum) : float3(1, 1, 1);
        float3 light = clamp(lum, 0.45, 1.4) * lerp(float3(1, 1, 1), tint, 0.3);
        float relief = lerp(1, 0.55 + diffuse * 0.6, 0.6);
        float3 sceneLit = Color.rgb * light * relief * (0.85 + Extra.w) + spec * saturate(0.25 + clamp(lum, 0, 1.4)) * Shade2.w + around * fres * Shade4.x * 0.5;
        lit = lerp(lit, sceneLit, saturate(SceneLight.w));
    }
    float through = saturate(exp(-thick * Shade3.y) * Shade3.x);
    through = through + (1 - through) * Extra.x;
    float3 col = lerp(lit, behind * lerp(float3(1, 1, 1), Color.rgb, 0.5) + spec * Shade2.w, through);
    float alpha = saturate(thick * Shade3.w) * Color.a;
    return float4(col, alpha);
}
";

    // ---- Setting up

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

    private static nint Compile(string entry, string target)
    {
        var src = Encoding.ASCII.GetBytes(Hlsl);
        var name = Encoding.ASCII.GetBytes("LimberLiquid\0");
        var e = Encoding.ASCII.GetBytes(entry + "\0");
        var t = Encoding.ASCII.GetBytes(target + "\0");
        nint code = 0, errors = 0;
        int hr;
        fixed(byte* ps = src, pn = name, pe = e, pt = t)
            hr = D3DCompile(ps, (nuint)src.Length, pn, null, null, pe, pt, 1u << 15 /* optimisation level 3 */, 0, &code, &errors);
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

    private static nint Check(int hr, nint obj, string what)
    {
        if(hr < 0 || obj == 0)
            throw new InvalidOperationException($"{what} couldn't be made ({hr:X8})");
        return obj;
    }

    private static void Setup()
    {
        var device = Device();
        if(device == 0)
            throw new InvalidOperationException("no device");
        _vsShape = Compile("VsShape", "vs_5_0");
        _psDepth = Compile("PsDepth", "ps_5_0");
        _psThick = Compile("PsThick", "ps_5_0");
        _vsFull = Compile("VsFull", "vs_5_0");
        _psBlur = Compile("PsBlur", "ps_5_0");
        _psComposite = Compile("PsComposite", "ps_5_0");
        _psProbe = Compile("PsProbe", "ps_5_0");
        MakeTarget(1, 1, FormatR32F, true, out _probe, out _probeRt, out _probeSrv);
        {
            // (a copy the CPU can read: what the probe found, for the page)
            var sd = new Texture2DDesc { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = FormatR32F, SampleCount = 1, Usage = 3 /* staging */, CpuAccessFlags = 0x20000 /* read */ };
            nint st = 0;
            _probeStage = Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &sd, null, &st), st, "probe copy");
        }

        nint o;
        var cbDesc = new BufferDesc { ByteWidth = (uint)((sizeof(Constants) + 15) / 16 * 16), Usage = UsageDynamic, BindFlags = BindConstant, CpuAccessFlags = CpuWrite };
        o = 0;
        _cb = Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &cbDesc, null, &o), o, "constants");

        BlendDesc BlendOf(bool on, int src, int dst, int srcA, int dstA)
        {
            var b = new BlendDesc();
            b.RT0 = new RtBlend { BlendEnable = on ? 1 : 0, Src = src, Dest = dst, Op = 1, SrcAlpha = srcA, DestAlpha = dstA, OpAlpha = 1, WriteMask = 0xF };
            return b;
        }
        nint MakeBlend(BlendDesc d)
        {
            nint s = 0;
            return Check(((delegate* unmanaged[Stdcall]<nint, BlendDesc*, nint*, int>)V(device, 20))(device, &d, &s), s, "blend");
        }
        _blendNone = MakeBlend(BlendOf(false, 2, 1, 2, 1));
        _blendAdd = MakeBlend(BlendOf(true, 2, 2, 2, 2));        // one + one
        _blendAlpha = MakeBlend(BlendOf(true, 5, 6, 2, 6));      // src alpha, 1 - src alpha

        nint MakeDepth(bool on)
        {
            var d = new DepthDesc
            {
                DepthEnable = on ? 1 : 0, WriteMask = on ? 1 : 0, Func = 2 /* less */, StencilEnable = 0, StencilMasks = 0xFFFF,
                Front = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 }, Back = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 },
            };
            nint s = 0;
            return Check(((delegate* unmanaged[Stdcall]<nint, DepthDesc*, nint*, int>)V(device, 21))(device, &d, &s), s, "depth state");
        }
        _depthLess = MakeDepth(true);
        _depthOff = MakeDepth(false);

        nint MakeRaster(int cull)
        {
            var r = new RasterDesc { Fill = 3, Cull = cull, DepthClip = 1 };
            nint s = 0;
            return Check(((delegate* unmanaged[Stdcall]<nint, RasterDesc*, nint*, int>)V(device, 22))(device, &r, &s), s, "rasterizer");
        }
        _rasterBack = MakeRaster(2); // (draw the boxes' far sides: works with the camera inside one too)
        _rasterNone = MakeRaster(1);

        nint MakeSampler(int filter)
        {
            var d = new SamplerDesc { Filter = filter, U = 3, Vv = 3, W = 3, MaxAnisotropy = 1, Comparison = 1, MaxLod = float.MaxValue };
            nint s = 0;
            return Check(((delegate* unmanaged[Stdcall]<nint, SamplerDesc*, nint*, int>)V(device, 23))(device, &d, &s), s, "sampler");
        }
        _samplerPoint = MakeSampler(0);
        _samplerLinear = MakeSampler(0x15);
    }

    private static void EnsurePieces(int count)
    {
        if(count <= _piecesCapacity)
            return;
        Release(_piecesSrv);
        Release(_pieces);
        _pieces = _piecesSrv = 0;
        _piecesCapacity = 0;
        int capacity = Math.Max(1024, (int)BitOperations.RoundUpToPowerOf2((uint)count));
        var device = Device();
        var desc = new BufferDesc { ByteWidth = (uint)(capacity * 64), Usage = UsageDynamic, BindFlags = BindSrv, CpuAccessFlags = CpuWrite, MiscFlags = MiscStructured, StructureByteStride = 64 };
        nint b = 0;
        _pieces = Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &desc, null, &b), b, "pieces");
        var srv = new BufferSrvDesc { Format = 0, ViewDimension = 1 /* buffer */, FirstElement = 0, NumElements = (uint)capacity };
        nint v = 0;
        _piecesSrv = Check(((delegate* unmanaged[Stdcall]<nint, nint, BufferSrvDesc*, nint*, int>)V(device, 7))(device, _pieces, &srv, &v), v, "pieces view");
        _piecesCapacity = capacity;
    }

    private static void MakeTarget(uint w, uint h, uint format, bool rt, out nint tex, out nint view, out nint srv)
    {
        var device = Device();
        var desc = new Texture2DDesc { Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1, Usage = UsageDefault, BindFlags = BindSrv | (rt ? BindRt : 0) };
        nint t = 0;
        tex = Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &desc, null, &t), t, "texture");
        nint s = 0;
        srv = Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 7))(device, tex, null, &s), s, "texture view");
        view = 0;
        if(rt)
        {
            nint r = 0;
            view = Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 9))(device, tex, null, &r), r, "render target");
        }
    }

    private static void EnsureScreen(uint w, uint h, uint bgFormat)
    {
        if(w == _w && h == _h && bgFormat == _bgFormat && _depthA != 0)
            return;
        FreeScreen();
        MakeTarget(w, h, FormatR32F, true, out _depthA, out _depthArt, out _depthAsrv);
        MakeTarget(w, h, FormatR32F, true, out _depthB, out _depthBrt, out _depthBsrv);
        MakeTarget(w, h, FormatR16F, true, out _thick, out _thickRt, out _thickSrv);
        MakeTarget(w, h, FormatR16F, true, out _thickB, out _thickBrt, out _thickBsrv);
        MakeTarget(w, h, bgFormat, false, out _bg, out _, out _bgSrv);
        var device = Device();
        var zd = new Texture2DDesc { Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = FormatD32F, SampleCount = 1, Usage = UsageDefault, BindFlags = BindDepth };
        nint z = 0;
        _zbuf = Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &zd, null, &z), z, "depth buffer");
        nint dsv = 0;
        _zbufDsv = Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 10))(device, _zbuf, null, &dsv), dsv, "depth view");
        _w = w;
        _h = h;
        _bgFormat = bgFormat;
    }

    // ---- The device's state, kept and put back

    private struct Saved
    {
        public fixed long Rtv[8];
        public nint Dsv, Blend, Depth, Raster, Vs, Ps, Layout, VsCb, PsCb, Sampler0, Sampler1;
        public fixed long PsSrv[8];
        public nint VsSrv0;
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
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 73))(c, 0, 8, s->PsSrv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 84))(c, 0, 1, &s->VsSrv0);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 75))(c, 0, 1, &s->Sampler0);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 75))(c, 1, 1, &s->Sampler1);
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
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 8))(c, 0, 8, s->PsSrv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 25))(c, 0, 1, &s->VsSrv0);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 10))(c, 0, 1, &s->Sampler0);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 10))(c, 1, 1, &s->Sampler1);
        // (the Get calls added a reference to each)
        for(int i = 0; i < 8; i++)
            Release((nint)s->Rtv[i]);
        for(int i = 0; i < 8; i++)
            Release((nint)s->PsSrv[i]);
        for(uint i = 0; i < s->VsInstances; i++)
            Release((nint)s->VsClass[i]);
        for(uint i = 0; i < s->PsInstances; i++)
            Release((nint)s->PsClass[i]);
        foreach(var o in new[] { s->Dsv, s->Blend, s->Depth, s->Raster, s->Vs, s->Ps, s->Layout, s->VsCb, s->PsCb, s->VsSrv0, s->Sampler0, s->Sampler1 })
            Release(o);
        NativeMemory.Clear(s, (nuint)sizeof(Saved));
    }

    // ---- Each frame

    private static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>Draws the liquid (render thread). Does nothing when there is none.</summary>
    public static void Draw(ClimaxService climax, ClimaxLook look)
    {
        if(_failed)
            return;
        int count = climax.Take(ref _take);
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
            if(_vsShape == 0)
                Setup();

            // the picture: the swap chain's buffer, and a render target on it for this frame only (so the game can resize it)
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
            EnsureScreen(bd.Width, bd.Height, bd.Format);
            EnsurePieces(count);

            // the game's depth (to hide the liquid behind what's in front of it)
            nint sceneSrv = 0;
            var uvScale = Vector2.One;
            var rtm = RenderTargetManager.Instance();
            if(look.HideBehind && rtm != null && rtm->DepthStencil != null && rtm->DepthStencil->D3D11ShaderResourceView != null)
            {
                var ds = rtm->DepthStencil;
                sceneSrv = (nint)ds->D3D11ShaderResourceView;
                if(ds->AllocatedWidth > 0 && ds->AllocatedHeight > 0)
                    uvScale = new Vector2((float)ds->ActualWidth / ds->AllocatedWidth, (float)ds->ActualHeight / ds->AllocatedHeight);
            }

            // the game's colour buffer (what colour it drew each pixel, before lighting), and its light buffer
            nint lightDiffuse = 0, albedoSrv = 0;
            var lightUv = Vector2.One;
            int sceneMode = 0;
            if(look.SceneLight > 0f && rtm != null)
            {
                var ldt = *(Texture**)((byte*)rtm + 0x58);
                if(ldt != null && ldt->D3D11ShaderResourceView != null)
                    lightDiffuse = (nint)ldt->D3D11ShaderResourceView;
                // (the light buffer, relative to the frame's light: the colour buffer route showed what's under it through)
                var uvOf = ldt;
                if(lightDiffuse != 0)
                    sceneMode = 2;
                if(uvOf != null && uvOf->AllocatedWidth > 0 && uvOf->AllocatedHeight > 0)
                    lightUv = new Vector2((float)uvOf->ActualWidth / uvOf->AllocatedWidth, (float)uvOf->ActualHeight / uvOf->AllocatedHeight);
            }
            var constants = MakeConstants(look, bd.Width, bd.Height, uvScale, sceneSrv != 0, climax.Probes, count);
            if(constants is { } kk)
            {
                kk.SceneLight = new Vector4(lightUv.X, lightUv.Y, sceneMode, Math.Clamp(look.SceneLight, 0f, 1f));
                constants = kk;
            }
            if(constants == null)
                return;

            Save(c);
            saved = true;

            // upload
            Mapped m;
            if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _pieces, 0, 4, 0, &m) < 0)
                return;
            fixed(ClimaxService.Piece* p = _take)
                Buffer.MemoryCopy(p, m.Data, (long)_piecesCapacity * 64, (long)count * 64);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _pieces, 0);
            var k = constants.Value;
            if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _cb, 0, 4, 0, &m) < 0)
                return;
            *(Constants*)m.Data = k;
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _cb, 0);

            // a copy of the picture (seen through the liquid, and reflected in it)
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)V(c, 47))(c, _bg, backTex);

            var vp = new Viewport { Width = bd.Width, Height = bd.Height, MinDepth = 0, MaxDepth = 1 };
            ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(c, 44))(c, 1, &vp);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 17))(c, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, void>)V(c, 24))(c, 4); // triangle list
            nint cb = _cb;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 7))(c, 0, 1, &cb);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 0, 1, &cb);
            nint* samplers = stackalloc nint[2] { _samplerPoint, _samplerLinear };
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 10))(c, 0, 2, samplers);
            nint pieces = _piecesSrv;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 25))(c, 0, 1, &pieces);
            nint* none = stackalloc nint[8];
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 8, none);

            float* zero = stackalloc float[4];
            // 0. which way the game's depth reads (worked out on the GPU, from points inside the bodies)
            nint rt = _probeRt;
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rt, 0);
            float* two = stackalloc float[4] { 2f, 2f, 2f, 2f };
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)V(c, 50))(c, _probeRt, two);
            if(sceneSrv != 0)
            {
                var one = new Viewport { Width = 1, Height = 1, MinDepth = 0, MaxDepth = 1 };
                ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(c, 44))(c, 1, &one);
                nint* probeIn = stackalloc nint[2] { 0, sceneSrv };
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 2, probeIn);
                Blend(c, _blendNone);
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _depthOff, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 43))(c, _rasterNone);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vsFull, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psProbe, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 3, 0);
                ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(c, 44))(c, 1, &vp);
                // what it found last time (if the GPU has got there; never waited for), then this frame's copied for next time
                Mapped pm;
                if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _probeStage, 0, 1 /* read */, 0x100000 /* don't wait */, &pm) >= 0)
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
            nint probeView = _probeSrv;
            // 1. the liquid's nearest surface: how far along each pixel's ray
            rt = _depthArt;
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rt, _zbufDsv);
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)V(c, 50))(c, _depthArt, zero);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, float, byte, void>)V(c, 53))(c, _zbufDsv, 1, 1f, 0);
            nint* ps = stackalloc nint[2] { _piecesSrv, sceneSrv };
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 2, ps);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 5, 1, &probeView);
            Blend(c, _blendNone);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _depthLess, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 43))(c, _rasterNone);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vsShape, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psDepth, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, void>)V(c, 21))(c, 36, (uint)count, 0, 0);

            // 2. how thick it is (everything added up)
            rt = _thickRt;
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rt, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)V(c, 50))(c, _thickRt, zero);
            Blend(c, _blendAdd);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _depthOff, 0);
            // (one side of each box only: each pixel adds its thickness once)
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 43))(c, _rasterBack);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psThick, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, void>)V(c, 21))(c, 36, (uint)count, 0, 0);

            // 3. smoothing the depths together (across, then down; as many times as asked)
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 43))(c, _rasterNone);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vsFull, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psBlur, null, 0);
            Blend(c, _blendNone);
            int passes = Math.Clamp(look.SmoothingPasses, 0, 10);
            nint* into = stackalloc nint[2];
            nint* from = stackalloc nint[2];
            for(int pass = 0; pass < passes && (look.Smoothing > 0f || look.EdgeSmoothing > 0f); pass++)
            {
                for(int dir = 0; dir < 2; dir++)
                {
                    k.Blur = new Vector4(dir == 0 ? 1 : 0, dir == 0 ? 0 : 1, k.Blur.Z, k.Blur.W);
                    if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _cb, 0, 4, 0, &m) < 0)
                        break;
                    *(Constants*)m.Data = k;
                    ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _cb, 0);
                    // (depth and thickness together: A into B, then B back into A)
                    into[0] = dir == 0 ? _depthBrt : _depthArt;
                    into[1] = dir == 0 ? _thickBrt : _thickRt;
                    from[0] = dir == 0 ? _depthAsrv : _depthBsrv;
                    from[1] = dir == 0 ? _thickSrv : _thickBsrv;
                    // (targets first: a texture can't be read while it's a target)
                    ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 2, 2, none);
                    ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 2, into, 0);
                    ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 2, 2, from);
                    ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 3, 0);
                }
            }

            // 4. onto the picture: lit, glossy, seen through where thin
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 2, 2, none);
            rt = backRtv;
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rt, 0);
            nint* last = stackalloc nint[3] { _depthAsrv, _thickSrv, _bgSrv };
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 2, 3, last);
            nint* lights = stackalloc nint[2] { lightDiffuse, albedoSrv };
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 6, 2, lights);
            Blend(c, _blendAlpha);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psComposite, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 3, 0);

            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 8, none);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 25))(c, 0, 1, none);
            _error = "";
        }
        catch(Exception e)
        {
            _failed = true;
            _error = e.Message;
            Plugin.Log.Warning(e, "Limber: the liquid can't be drawn");
        }
        finally
        {
            if(saved)
                Restore(c);
            Release(backRtv);
            Release(backTex);
        }
    }

    /// <summary>Try again after it failed (from the page).</summary>
    public static void Retry()
    {
        _failed = false;
        _error = "";
    }

    private static void Blend(nint c, nint state)
    {
        float* f = stackalloc float[4];
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, state, f, 0xFFFFFFFF);
    }

    private static Constants? MakeConstants(ClimaxLook look, uint w, uint h, Vector2 uvScale, bool useScene, Vector3[] probes, int count)
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
        // the camera: where every ray meets (the point the matrix sends to w = 0)
        var ch = Vector4.Transform(new Vector4(0, 0, 1, 0), ivp);
        if(MathF.Abs(ch.W) < 1e-12f)
            return null;
        var cam = new Vector3(ch.X, ch.Y, ch.Z) / ch.W;
        var center = Vector3.Normalize(Unproject(0, 0, 0.5f) - cam);
        var right = Vector3.Normalize(Unproject(1, 0, 0.5f) - Unproject(-1, 0, 0.5f));
        var up = Vector3.Normalize(Unproject(0, 1, 0.5f) - Unproject(0, -1, 0.5f));
        var topRay = Vector3.Normalize(Unproject(0, 1, 0.5f) - cam);
        float tanHalf = MathF.Tan(MathF.Acos(Math.Clamp(Vector3.Dot(center, topRay), -1f, 1f)));
        float pixelsPerMetre = h / MathF.Max(2f * tanHalf, 1e-4f);
        // The smoothing radius in pixels, at the liquid's typical distance (the nearest tenth of it): one for the frame.
        var near = new System.Collections.Generic.List<float>();
        int every = Math.Max(1, count / 64);
        for(int i = 0; i < count; i += every)
            near.Add(Vector3.Distance(new Vector3(_take[i].A.X, _take[i].A.Y, _take[i].A.Z), cam));
        near.Sort();
        float typical = near.Count > 0 ? MathF.Max(near[near.Count / 10], 0.05f) : 1f;
        float blurPixels = look.Smoothing * pixelsPerMetre / typical;
        float edgePixels = look.EdgeSmoothing * pixelsPerMetre / typical;
        float cap = Math.Clamp(48f * h / 1080f, 24f, 96f);

        // the light: from the camera's view, turned right and up
        float yaw = look.LightYaw * MathF.PI / 180f, pitch = look.LightPitch * MathF.PI / 180f;
        var light = Vector3.Normalize(-center * (MathF.Cos(pitch) * MathF.Cos(yaw)) + right * (MathF.Cos(pitch) * MathF.Sin(yaw)) + up * MathF.Sin(pitch));

        if(!MathUtilFinite(cam) || !MathUtilFinite(right) || !MathUtilFinite(up))
            return null;
        // the camera's own near and far planes (for depth stored by distance)
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
        return new Constants
        {
            ViewProj = vp,
            InvViewProj = ivp,
            Cam = new Vector4(cam, 0),
            Screen = new Vector4(w, h, 1f / w, 1f / h),
            // (z: 0 worked out on the GPU, 1 as it is, 2 the other way round)
            SceneUv = new Vector4(uvScale.X, uvScale.Y, Math.Clamp(look.DepthMode, 0, 4), useScene ? 1 : 0),
            Probe0 = probes.Length > 0 ? new Vector4(probes[0], 1) : default,
            Probe1 = probes.Length > 1 ? new Vector4(probes[1], 1) : default,
            Probe2 = probes.Length > 2 ? new Vector4(probes[2], 1) : default,
            Probe3 = probes.Length > 3 ? new Vector4(probes[3], 1) : default,
            Blur = new Vector4(1, 0, blurPixels, pixelsPerMetre),
            // (the widest smoothing, in pixels: big enough at this resolution to melt the drops into one surface)
            Blur2 = new Vector4(MathF.Max(look.MergeDepth, 0.001f), cap, MathF.Max(look.CoatSpread, 0.2f), MathF.Max(look.CoatHeight, 0.01f)),
            Blur3 = new Vector4(MathF.Min(edgePixels, cap), 0, 0, 0),
            // (the yellow hue: less blue, a little less green)
            Color = new Vector4(look.Color.X, look.Color.Y * (1f - 0.1f * look.Yellow), look.Color.Z * (1f - 0.38f * look.Yellow), Math.Clamp(look.Opacity, 0f, 1f)),
            Extra = new Vector4(Math.Clamp(look.Transparency, 0f, 1f), camNear, camFar, MathF.Max(look.Subsurface, 0f)),
            Fwd = new Vector4(center, 0),
            Shade1 = new Vector4(look.Gloss, look.Shine, look.Rim, look.Ambient),
            Shade2 = new Vector4(light, look.LightStrength),
            Shade3 = new Vector4(look.Translucency, look.Absorb, look.Refraction, look.EdgeSharpness),
            Shade4 = new Vector4(look.Reflection, MathF.Max(look.Softness, 0.1f), look.Gloss2, look.Shine2),
            Right = new Vector4(right, 0),
            Up = new Vector4(up, 0),
        };
    }

    private static bool MathUtilFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
