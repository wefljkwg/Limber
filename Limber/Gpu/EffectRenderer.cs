using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Limber.BodyRig;
using Limber.Core;
using Limber.Rig;

namespace Limber.Gpu;

/// <summary>
/// The Shaders page's drawn effects (Limber's own, on any material, skin included): a glow over the surface, a rim
/// light round the edges, an outline, and seeing them through walls.
/// They're made from the game's own draws of the character, not a copy of it: while the game draws the chosen
/// characters' meshes (recognised by the bone matrices it skins them with, which are theirs alone), each of those draws
/// is made once more into a mask of Limber's own, with the very same vertices, skinning, shape keys and visible parts, in
/// the same frame, tested against the game's depth so only what shows is marked. When the frame is done, the mask is
/// turned into the effects over the picture: light and rim on the marked pixels (the surface's direction from the game's
/// depth), the outline round their edge, and the parts hidden behind something (marked with no depth test) for seeing
/// through walls. Direct3D 11 through the device's own tables; every piece of state it changes is put back. Everything
/// stays in this game client.
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

    private const uint FormatR32G32F = 16, FormatR8 = 61;
    private const uint BindSrv = 0x8, BindRt = 0x20, BindConstant = 0x4;
    private const uint UsageDefault = 0, UsageDynamic = 2, CpuWrite = 0x10000, MiscStructured = 0x40;

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(byte* src, nuint size, byte* name, void* defines, void* include, byte* entry, byte* target, uint flags1, uint flags2, nint* code, nint* errors);

    private static bool _failed;
    private static string _error = "";
    private static nint _vsFull, _psMask, _psXray, _psLight, _psOutline, _psPixel, _cb, _maskCb;
    private static nint _sceneCopy, _sceneCopySrv, _psMaterial, _fxBuf, _fxSrv;
    private static int _fxCap;
    private static uint _sceneW, _sceneH, _sceneFormat;
    private static nint _blendAdd, _blendSolid, _blendWrite, _depthOff, _depthEqual, _rasterNone;
    private static nint _looksBuf, _looksSrv;
    private static int _looksCap;
    private static nint _maskA, _maskARtv, _maskASrv, _maskB, _maskBRtv, _maskBSrv;
    private static nint _ownDepth, _ownDsv, _depthOwn, _depthOwnR, _blendNoColour, _psSlime, _blendAlpha;
    private static bool _reversed;
    private static volatile bool[] _hidden = [];
    private static uint _maskW, _maskH;
    private static nint _srvClass;

    /// <summary>Why it isn't drawing (empty when it's fine).</summary>
    public static string Error => _error;

    /// <summary>How many of the game's draws were marked last frame (for the page).</summary>
    public static int Drawn { get; private set; }

    /// <summary>How many of the game's views were found for the chosen characters' bone matrices (for the page).</summary>
    public static int Views { get; private set; }

    public static void Retry()
    {
        _failed = false;
        _error = "";
    }

    public static void Dispose()
    {
        _map = new();
        _drawIndexed?.Dispose();
        _drawIndexedInstanced?.Dispose();
        _drawIndexed = null;
        _drawIndexedInstanced = null;
        Release(_psPixel);
        Release(_psMaterial);
        Release(_fxSrv);
        Release(_fxBuf);
        _psMaterial = _fxSrv = _fxBuf = 0;
        _fxCap = 0;
        Release(_sceneCopySrv);
        Release(_sceneCopy);
        _psPixel = _sceneCopySrv = _sceneCopy = 0;
        _sceneW = _sceneH = _sceneFormat = 0;
        Release(_ownDsv);
        Release(_ownDepth);
        Release(_depthOwn);
        Release(_depthOwnR);
        Release(_blendNoColour);
        Release(_psSlime);
        Release(_blendAlpha);
        _ownDsv = _ownDepth = _depthOwn = _depthOwnR = _blendNoColour = _psSlime = _blendAlpha = 0;
        foreach(var o in new[] { _vsFull, _psMask, _psXray, _psLight, _psOutline, _cb, _maskCb, _blendAdd, _blendSolid, _blendWrite, _depthOff, _depthEqual, _rasterNone,
                     _looksSrv, _looksBuf, _maskARtv, _maskASrv, _maskA, _maskBRtv, _maskBSrv, _maskB })
            Release(o);
        _vsFull = _psMask = _psXray = _psLight = _psOutline = _cb = _maskCb = 0;
        _blendAdd = _blendSolid = _blendWrite = _depthOff = _depthEqual = _rasterNone = 0;
        _looksSrv = _looksBuf = 0;
        _looksCap = 0;
        _maskA = _maskARtv = _maskASrv = _maskB = _maskBRtv = _maskBSrv = 0;
        _maskW = _maskH = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LookGpu
    {
        public Vector4 Light;    // rgb, strength
        public Vector4 Rim;      // rgb, strength
        public Vector4 Outline;  // rgb, width (pixels)
        public Vector4 Misc;     // rim tightness, pulse (per second), see through walls, 0
        public Vector4 Extra;    // pixelate (block size, pixels), Game Boy colours, first material effect, how many
        public Vector4 Origin;   // the character's position (patterns stay on them as they move)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FxGpu
    {
        public Vector4 Head;     // kind, strength, speed, 0
        public Vector4 A, B, P;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProj, InvViewProj;
        public Vector4 Cam;      // camera position, time
        public Vector4 Screen;   // w, h, 1/w, 1/h (the picture)
        public Vector4 Mask;     // the depth's drawn size (w, h), widest outline (pixels), pixelate block (pixels)
        public Vector4 Right, Up; // the camera's (Up.w: 1 when depth runs far to near, 0 to 1 is far to near)
    }

    private const string Hlsl = @"
cbuffer C : register(b0)
{
    row_major float4x4 VP; row_major float4x4 IVP;
    float4 Cam; float4 Screen; float4 Mask; float4 Right; float4 Up;
};
cbuffer M : register(b1) { float4 Id; };
struct Look { float4 Light; float4 Rim; float4 Outline; float4 Misc; float4 Extra; float4 Origin; };
Texture2D<float2> MaskA : register(t0);
Texture2D<float> MaskB : register(t1);
Texture2D<float> SceneDepth : register(t2);
StructuredBuffer<Look> Looks : register(t3);
Texture2D<float4> SceneColor : register(t4);

// ---- Marking (drawn with the game's own vertex shader, straight after its own draw)
float4 PsMask(float4 pos : SV_Position) : SV_Target0 { return float4(Id.x, pos.z, 0, 0); }
float4 PsXray(float4 pos : SV_Position) : SV_Target0 { return float4(Id.x / 255.0, 0, 0, 0); }

// ---- Over the picture
float4 VsFull(uint vid : SV_VertexID) : SV_Position
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

int2 MaskPixel(float2 uv) { return int2(clamp(uv * Mask.xy, 0, Mask.xy - 1)); }

// The look marked at this pixel and still on top (nothing the game drew later covers it); -1 none.
int VisibleLook(int2 p)
{
    float2 a = MaskA.Load(int3(p, 0));
    int look = (int)round(a.x) - 1;
    if (look < 0) return -1;
    float d = SceneDepth.Load(int3(p, 0));
    // (hidden only where the game drew something nearer: with the body hidden, the scene behind it doesn't count)
    float tol = 2e-6 + abs(a.y) * 2e-4;
    bool covered = Up.w > 0.5 ? d > a.y + tol : d < a.y - tol;
    return covered ? -1 : look;
}

// The character's own depth where it's marked (exact, and there even with its body hidden), else the scene's.
float DepthAt(int2 p)
{
    float2 a = MaskA.Load(int3(p, 0));
    return a.x > 0.5 ? a.y : SceneDepth.Load(int3(p, 0));
}

float3 WorldAt(int2 p)
{
    p = clamp(p, 0, (int2)Mask.xy - 1);
    float2 uv = (p + 0.5) / Mask.xy;
    float d = DepthAt(p);
    float4 w = mul(float4(uv.x * 2 - 1, 1 - uv.y * 2, d, 1), IVP);
    return w.xyz / w.w;
}

float4 PsLight(float4 pos : SV_Position) : SV_Target0
{
    float2 uv = pos.xy * Screen.zw;
    int2 p = MaskPixel(uv);
    int look = VisibleLook(p);
    float3 c = 0;
    if (look >= 0)
    {
        Look L = Looks[look];
        float pulse = L.Misc.y > 0 ? 0.6 + 0.4 * sin(Cam.w * L.Misc.y * 6.2831853) : 1;
        float rim = 0;
        if (L.Rim.w > 0)
        {
            // the surface's direction, from the depth (the smaller step each way, so edges don't smear)
            float3 c0 = WorldAt(p);
            float3 xl = c0 - WorldAt(p - int2(1, 0)), xr = WorldAt(p + int2(1, 0)) - c0;
            float3 yu = c0 - WorldAt(p - int2(0, 1)), yd = WorldAt(p + int2(0, 1)) - c0;
            float3 dx = dot(xl, xl) < dot(xr, xr) ? xl : xr;
            float3 dy = dot(yu, yu) < dot(yd, yd) ? yu : yd;
            float3 n = normalize(cross(dx, dy));
            float3 v = normalize(Cam.xyz - c0);
            float facing = saturate(abs(dot(n, v)));
            rim = pow(saturate(1 - facing), max(L.Misc.x, 0.5));
        }
        c = (L.Light.rgb * L.Light.w * 0.5 + L.Rim.rgb * L.Rim.w * rim) * pulse;
    }
    else
    {
        // hidden behind something: a ghostly silhouette for the looks that see through walls
        int x = (int)round(MaskB.Load(int3(p, 0)) * 255) - 1;
        if (x >= 0)
        {
            Look L = Looks[x];
            if (L.Misc.z > 0.5)
            {
                float pulse = L.Misc.y > 0 ? 0.6 + 0.4 * sin(Cam.w * L.Misc.y * 6.2831853) : 1;
                float3 rc = L.Rim.w > 0 ? L.Rim.rgb : (L.Light.w > 0 ? L.Light.rgb : float3(0.55, 0.8, 1));
                // brighter toward the silhouette's edge
                int edge = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    int2 o = int2(k == 0 ? 3 : k == 1 ? -3 : 0, k == 2 ? 3 : k == 3 ? -3 : 0);
                    edge += MaskB.Load(int3(clamp(p + o, 0, (int2)Mask.xy - 1), 0)) > 0 ? 0 : 1;
                }
                c = rc * (0.18 + 0.15 * edge) * pulse;
            }
        }
    }
    if (max(c.r, max(c.g, c.b)) < 0.002) discard;
    return float4(c, 0);
}

// Pixelate (the censor mosaic): the picture in big blocks wherever most of a block is a pixelated look; each block the
// average of its character pixels (so the background doesn't bleed in), optionally in the Game Boy's four greens.
float4 PsPixel(float4 pos : SV_Position) : SV_Target0
{
    float size = max(Mask.w, 2);
    float2 block = floor(pos.xy / size) * size;
    float3 sum = 0;
    int hits = 0, gb = 0;
    [unroll] for (int y = 0; y < 4; y++)
        [unroll] for (int x = 0; x < 4; x++)
        {
            float2 at = min(block + (float2(x, y) + 0.5) * (size / 4), Screen.xy - 1);
            int look = VisibleLook(MaskPixel(at * Screen.zw));
            if (look < 0 || Looks[look].Extra.x < 1) continue;
            sum += SceneColor.Load(int3((int2)at, 0)).rgb;
            hits++;
            gb += Looks[look].Extra.y > 0.5 ? 1 : 0;
        }
    if (hits < 8) discard;
    float3 c = sum / hits;
    if (gb * 2 >= hits)
    {
        float l = dot(c, float3(0.299, 0.587, 0.114));
        c = l < 0.25 ? float3(0.06, 0.22, 0.06) : l < 0.5 ? float3(0.19, 0.38, 0.19) : l < 0.75 ? float3(0.55, 0.67, 0.06) : float3(0.61, 0.74, 0.06);
    }
    return float4(c, 1);
}

// ---- Material effects (Koikatsu Material Editor style): each look's list, applied in order on its visible pixels
struct Fx { float4 Head; float4 A; float4 B; float4 P; }; // Head: kind, strength, speed, 0
StructuredBuffer<Fx> Effects : register(t5);

float Lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float Hash1(float n) { return frac(sin(n) * 43758.5453); }
float Hash2(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
float Hash3(float3 p) { p = frac(p * 0.3183099 + 0.1); p *= 17.0; return frac(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float Noise3(float3 x)
{
    float3 i = floor(x), f = frac(x); f = f * f * (3 - 2 * f);
    return lerp(lerp(lerp(Hash3(i), Hash3(i + float3(1, 0, 0)), f.x), lerp(Hash3(i + float3(0, 1, 0)), Hash3(i + float3(1, 1, 0)), f.x), f.y),
                lerp(lerp(Hash3(i + float3(0, 0, 1)), Hash3(i + float3(1, 0, 1)), f.x), lerp(Hash3(i + float3(0, 1, 1)), Hash3(i + float3(1, 1, 1)), f.x), f.y), f.z);
}
float Fbm3(float3 p) { float v = 0, a = 0.5; [unroll] for (int k = 0; k < 5; k++) { v += a * Noise3(p); p = p * 2.02 + 3.1; a *= 0.5; } return v; }
// distance to the nearest cell edge (cracks, cells)
float2 Voronoi(float3 p)
{
    float3 i = floor(p), f = frac(p);
    float d1 = 8, d2 = 8;
    [unroll] for (int z = -1; z <= 1; z++) [unroll] for (int y = -1; y <= 1; y++) [unroll] for (int x = -1; x <= 1; x++)
    {
        float3 g = float3(x, y, z);
        float3 o = float3(Hash3(i + g), Hash3(i + g + 17.3), Hash3(i + g + 41.7));
        float d = length(g + o - f);
        if (d < d1) { d2 = d1; d1 = d; } else if (d < d2) d2 = d;
    }
    return float2(d1, d2 - d1);
}
float3 HueShift(float3 c, float deg)
{
    float a = radians(deg); float cs = cos(a), sn = sin(a);
    float3x3 toY = float3x3(0.299, 0.587, 0.114, 0.596, -0.274, -0.322, 0.211, -0.523, 0.312);
    float3 yiq = mul(toY, c);
    yiq = float3(yiq.x, yiq.y * cs - yiq.z * sn, yiq.y * sn + yiq.z * cs);
    float3x3 toR = float3x3(1, 0.956, 0.621, 1, -0.272, -0.647, 1, -1.106, 1.703);
    return mul(toR, yiq);
}
float3 Rainbow(float h) { return saturate(abs(frac(h + float3(0, 2.0 / 3, 1.0 / 3)) * 6 - 3) - 1); }
float3 Pic(float2 px) { return SceneColor.Load(int3(clamp((int2)px, 0, (int2)Screen.xy - 1), 0)).rgb; }
bool Mine(float2 px) { return VisibleLook(MaskPixel(px * Screen.zw)) >= 0; }
// a direction on the surface as a step across the picture (pixels)
float2 ScreenStep(float3 wp, float3 d)
{
    float4 a = mul(float4(wp, 1), VP), b = mul(float4(wp + d * 0.05, 1), VP);
    float2 sa = a.xy / a.w, sb = b.xy / b.w;
    float2 s = (sb - sa) * float2(0.5, -0.5) * Screen.xy;
    float len = length(s);
    return len > 1e-4 ? s / len : 0;
}
float Sobel(float2 px, float w)
{
    float tl = Lum(Pic(px + float2(-w, -w))), t = Lum(Pic(px + float2(0, -w))), tr = Lum(Pic(px + float2(w, -w)));
    float l = Lum(Pic(px + float2(-w, 0))), r = Lum(Pic(px + float2(w, 0)));
    float bl = Lum(Pic(px + float2(-w, w))), b = Lum(Pic(px + float2(0, w))), br = Lum(Pic(px + float2(w, w)));
    float gx = -tl - 2 * l - bl + tr + 2 * r + br, gy = -tl - 2 * t - tr + bl + 2 * b + br;
    return sqrt(gx * gx + gy * gy);
}
float3 Thermal(float t)
{
    t = saturate(t);
    float3 c = lerp(float3(0, 0, 0.1), float3(0.2, 0, 0.6), saturate(t * 4));
    c = lerp(c, float3(0.9, 0, 0.3), saturate(t * 4 - 1));
    c = lerp(c, float3(1, 0.6, 0), saturate(t * 4 - 2));
    return lerp(c, float3(1, 1, 0.85), saturate(t * 4 - 3));
}

float3 ApplyFx(Fx f, float3 c, float2 px, int2 mp, float3 wp, float3 rel, float3 n, float3 v, float rim)
{
    int kind = (int)round(f.Head.x);
    float t = Cam.w * f.Head.z;
    float3 A = f.A.rgb, B = f.B.rgb;
    float4 P = f.P;
    float3 L = normalize(v + Up.xyz * 0.7 + Right.xyz * 0.4); // a light from above the camera
    float3 o = c;
    [branch] switch (kind)
    {
    case 1: { // Colour grade
        o = HueShift(c, P.x); float l = Lum(o); o = lerp(l, o, P.y); o = (o - 0.5) * P.z + 0.5; o *= P.w * A; break; }
    case 2: o = 1 - c; break; // Invert
    case 3: { float l = Lum(c); o = lerp(l, float3(1.07, 0.74, 0.43) * l, P.x); break; } // Greyscale / sepia
    case 4: { float lv = max(P.x, 2); o = floor(c * lv + 0.5) / lv; break; } // Posterize
    case 5: { // Toon
        float l = Lum(c); float bands = max(P.x, 2);
        float q = floor(l * bands) / (bands - 1);
        float s = lerp(q, l, P.y);
        o = c / max(l, 1e-3) * s;
        o *= lerp(A, 1, saturate(s * 2));
        o += B * pow(saturate(dot(reflect(-v, n), L)), 30) * P.z;
        break; }
    case 6: o = A + B * pow(rim, 2) * P.x; break; // Silhouette
    case 7: { // Hologram
        float lines = frac(rel.y * P.x - t * 0.5);
        float scan = 0.55 + 0.45 * smoothstep(0.0, 0.2, lines) * smoothstep(1.0, 0.6, lines);
        float flick = 1 - P.y * step(0.85, Hash1(floor(t * 12)));
        float grain = 1 + (Hash2(px + floor(t * 30)) - 0.5) * P.z;
        o = (A * (0.35 + Lum(c) * 1.2) * scan + B * pow(rim, 1.5) * 1.5) * flick * grain;
        break; }
    case 8: o = A * P.y * (1 - rim) + B * pow(rim, max(0.2, 1 / max(P.x, 0.05))) * 1.5; break; // X-ray
    case 9: o = Thermal(Lum(c) * P.y + P.x); break; // Thermal vision
    case 10: { // Night vision
        float l = Lum(c) * 2.2;
        o = A * l + (Hash2(px + frac(t) * 100) - 0.5) * P.x;
        o *= 1 - P.y * 0.5 * (0.5 + 0.5 * sin(px.y * 3.14159));
        break; }
    case 11: { // Metal
        float3 r = reflect(-v, n);
        float env = saturate(r.y * 0.5 + 0.5);
        env = pow(env, max(P.y, 0.1)) + 0.15 * sin(r.x * 6 + r.z * 4);
        float spec = pow(saturate(dot(r, L)), max(P.x, 1)) * 1.5;
        float3 m = A * (0.25 + env) + spec;
        o = lerp(m, m * Lum(c) * 2, P.z);
        break; }
    case 12: { // Glass
        float2 off = ScreenStep(wp, n) * P.x * (1 - rim * 0.5);
        o = Pic(px + off) * lerp(1, A, P.z) + B * pow(rim, 2) * P.y;
        break; }
    case 13: { // Goo / slime (rounded: drawn by its own pass, PsSlime)
        if (P.w > 0.5) break;
        float w = Noise3(rel * 8 + t * 0.6) - 0.5;
        float2 off = (ScreenStep(wp, n) + w) * P.x;
        float3 behind = Pic(px + off);
        o = lerp(behind, A * (0.5 + Lum(behind)), P.z);
        o += pow(saturate(dot(reflect(-v, n), L)), 40) * P.y + A * rim * 0.6;
        break; }
    case 14: { // Ice / frost
        float2 vo = Voronoi(rel * max(P.w, 0.5) * 10);
        o = lerp(c, A, P.x) + rim * 0.5 * A;
        o += smoothstep(0.06, 0.0, vo.y) * P.y * 0.8;
        o += step(0.995 - P.z * 0.02, Hash2(floor(px / 2) + floor(t * 3))) * P.z * 2;
        break; }
    case 15: { // Stone
        float l = Lum(c);
        float ns = Fbm3(rel * max(P.x, 0.5) * 12);
        float2 vo = Voronoi(rel * max(P.x, 0.5) * 4);
        o = A * (0.55 + 0.45 * l) * (0.65 + 0.7 * ns) * (1 - P.y * 0.6);
        o *= 1 - smoothstep(0.05, 0.0, vo.y) * P.z * 0.7;
        break; }
    case 16: { // Space
        float3 q = rel * max(P.y, 0.1) * 3 + float3(0, 0, t * 0.05);
        float neb = Fbm3(q);
        o = lerp(A, B, saturate(neb * 1.6 - 0.3)) * smoothstep(0.25, 0.85, neb) * 1.4;
        float3 sc = floor(rel * max(P.y, 0.1) * 60);
        float s = Hash3(sc);
        o += step(1 - P.x * 0.03, s) * (0.6 + 0.4 * sin(t * 3 + s * 40));
        break; }
    case 17: { // Lava
        float3 q = rel * max(P.x, 0.1) * 5 + float3(0, -t * 0.15, 0);
        float a = Fbm3(q);
        float glow = pow(saturate(1 - abs(a * 2 - 1) * 2.5), 3) * P.y;
        o = lerp(B * (0.4 + a), A, saturate(glow)) * (1 + glow * 2);
        break; }
    case 18: { // Water caustics
        float3 q = rel * max(P.x, 0.1) * 6 + float3(t * 0.2, t * 0.3, 0);
        float2 vo = Voronoi(q);
        float caust = pow(saturate(1 - vo.y * 3), 6);
        o = lerp(c, A, 0.35) + caust * P.y;
        break; }
    case 19: { // Stripes
        float a = radians(P.y); float s = rel.x * cos(a) + rel.y * sin(a);
        o = frac(s / max(P.x * 0.01, 0.002) - t) < 0.5 ? A : B; break; }
    case 20: { float s = max(P.x * 0.01, 0.002); float3 g = floor(rel / s); o = fmod(abs(g.x + g.y + g.z), 2) < 1 ? A : B; break; } // Checker
    case 21: { // Polka dots
        float s = max(P.x * 0.01, 0.002); float3 g = frac(rel / s + float3(0, t * 0.2, 0)) - 0.5;
        o = length(g) < P.y * 0.5 ? A : B; break; }
    case 22: { // Camouflage
        float nz = Fbm3(rel * max(P.x, 0.1) * 6);
        float k = max(P.y, 1) * 8;
        o = nz < 0.42 ? A : nz < 0.55 ? lerp(A, B, 0.5) : B;
        o *= 0.8 + 0.4 * Lum(c); break; }
    case 23: { // Iridescent
        float h = (1 - saturate(dot(n, v))) * P.x + rel.y * 0.5 + t * P.y * 0.2;
        o = lerp(c, Rainbow(h) * (0.6 + Lum(c)), P.z); break; }
    case 24: o = HueShift(c, t * 60); break; // Hue cycle
    case 25: { // CRT
        float2 sp = float2(P.y, 0);
        o = float3(Pic(px + sp).r, c.g, Pic(px - sp).b);
        o *= 1 - P.x * 0.5 * (0.5 + 0.5 * sin(px.y * 3.14159));
        o *= 1 - 0.25 * P.z * smoothstep(0.9, 1.0, frac(px.y / Screen.y - t * 0.3));
        o += (Hash2(px + frac(t) * 61) - 0.5) * P.w;
        break; }
    case 26: { // Glitch
        float bs = max(P.x, 2);
        float rw = floor(px.y / bs);
        float h = Hash2(float2(rw, floor(t * 8)));
        float2 sh = h < P.y ? float2((Hash1(rw + floor(t * 8) * 7) - 0.5) * bs * 6, 0) : 0;
        float2 sp = float2(P.z, 0);
        o = float3(Pic(px + sh + sp).r, Pic(px + sh).g, Pic(px + sh - sp).b);
        break; }
    case 27: { // Blur
        float r = max(P.x, 0.5); float3 sum = 0; float wsum = 0;
        [loop] for (int y = -3; y <= 3; y++) [loop] for (int x = -3; x <= 3; x++)
        {
            float w = exp(-(x * x + y * y) / 6.0);
            sum += Pic(px + float2(x, y) * r / 3) * w; wsum += w;
        }
        o = sum / wsum; break; }
    case 28: { // Pencil sketch
        float e = Sobel(px, 1) * P.x;
        float l = Lum(c);
        float hatch = (1 - l) * P.y * step(0.5, frac((px.x + px.y) / 5));
        o = lerp(A, B, saturate(e + hatch)); break; }
    case 29: { // Oil painting (Kuwahara)
        int r = clamp((int)P.x, 1, 6);
        float3 m[4]; float3 s2[4];
        [unroll] for (int k = 0; k < 4; k++) { m[k] = 0; s2[k] = 0; }
        [loop] for (int y = 0; y <= r; y++) [loop] for (int x = 0; x <= r; x++)
        {
            float3 a = Pic(px + float2(-x, -y)), b = Pic(px + float2(x, -y)), cc = Pic(px + float2(-x, y)), d = Pic(px + float2(x, y));
            m[0] += a; s2[0] += a * a; m[1] += b; s2[1] += b * b; m[2] += cc; s2[2] += cc * cc; m[3] += d; s2[3] += d * d;
        }
        float cnt = (r + 1) * (r + 1); float best = 1e9;
        [unroll] for (int k = 0; k < 4; k++)
        {
            float3 mu = m[k] / cnt; float3 vr = abs(s2[k] / cnt - mu * mu);
            float sv = vr.r + vr.g + vr.b;
            if (sv < best) { best = sv; o = mu; }
        }
        break; }
    case 30: { // Ink lines
        float e = Sobel(px, max(P.x, 1));
        float ln = smoothstep(P.y, P.y + 0.1, e);
        o = lerp(lerp(1, c, P.z), A, ln); break; }
    case 31: { // Halftone
        float a = radians(P.y); float2 rp = float2(px.x * cos(a) - px.y * sin(a), px.x * sin(a) + px.y * cos(a));
        float s = max(P.x, 2); float2 g = frac(rp / s) - 0.5;
        float r = sqrt(1 - Lum(c)) * 0.7;
        o = length(g) < r ? A : B; break; }
    case 32: { // Dither
        float ps = max(P.y, 1); int2 q = (int2)(px / ps) & 3;
        float bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
        float lv = max(P.x, 2) - 1;
        float l = Lum(Pic(floor(px / ps) * ps));
        float d = floor(l * lv + bayer[q.y * 4 + q.x] / 16.0) / lv;
        o = lerp(A, B, saturate(d)); break; }
    case 33: { // Cross-hatch
        float l = Lum(c); float s = max(P.x, 3); float w = max(P.y, 0.5);
        float ink = 0;
        if (l < 0.8 && abs(fmod(px.x + px.y, s)) < w) ink = 1;
        if (l < 0.6 && abs(fmod(px.x - px.y + 1000, s)) < w) ink = 1;
        if (l < 0.4 && abs(fmod(px.x, s)) < w) ink = 1;
        if (l < 0.2 && abs(fmod(px.y, s)) < w) ink = 1;
        o = ink > 0 ? A : B; break; }
    case 34: { // Emboss
        float e = Lum(Pic(px - 1)) - Lum(Pic(px + 1));
        float3 relief = 0.5 + e * P.x * 2;
        o = lerp(relief, c * relief * 2, P.y); break; }
    case 35: { // Chromatic aberration
        float2 dir = normalize(px - Screen.xy * 0.5 + 0.001) * P.x;
        o = float3(Pic(px + dir).r, c.g, Pic(px - dir).b); break; }
    case 36: o = c + (Hash2(px + frac(t) * 97) - 0.5) * P.x; break; // Film grain
    case 37: { // Sparkle
        float3 cell = floor(rel * max(P.y, 1) * 40);
        float h = Hash3(cell);
        float tw = saturate(sin(t * 4 + h * 60));
        o = c + A * step(1 - P.x * 0.05, h) * pow(tw, 6) * 3; break; }
    case 38: { // Snow
        float cover = smoothstep(1 - P.x * 1.5, 1.1 - P.x * 1.5, n.y);
        o = lerp(c, A, cover);
        o += cover * step(0.993 - P.y * 0.02, Hash2(floor(px / 2))) * 1.5; break; }
    case 39: { // Wet / oily
        o = c * (1 - P.x * 0.45);
        o += pow(saturate(dot(reflect(-v, n), L)), max(P.z, 2)) * P.y * 1.5; break; }
    case 40: { // Hex tiles
        float s = max(P.x, 3);
        float2 r = float2(1, 1.732);
        float2 h = r * 0.5;
        float2 a = fmod(px, r * s) - h * s, b = fmod(px - h * s, r * s) - h * s;
        float2 g = dot(a, a) < dot(b, b) ? a : b;
        o = Pic(px - g) * (1 - P.y * smoothstep(0.42 * s, 0.5 * s, max(abs(g.x) * 0.866 + abs(g.y) * 0.5, abs(g.y)))); break; }
    case 41: { // Energy bands
        float b = frac(rel.y / max(P.x * 0.01, 0.01) - t);
        float band = 1 - smoothstep(0, max(P.y, 0.01), abs(b - 0.5) * 2);
        o = c + A * band * 2; break; }
    case 42: o = lerp(A, B, saturate(Lum(c))); break; // Gradient map
    case 43: { // Heat haze
        float2 w = float2(Noise3(float3(px * max(P.y, 0.1) * 0.05, t)), Noise3(float3(px * max(P.y, 0.1) * 0.05 + 7, t))) - 0.5;
        o = Pic(px + w * P.x * 2); break; }
    case 44: o = c * (1 - P.x) * A; break; // Shadow
    case 45: { // Cel shading (light-direction bands from the surface)
        float d = saturate(dot(n, L) * 0.5 + 0.5);
        float bands = max(P.x, 2);
        float s = floor(d * bands) / (bands - 1);
        o = c * lerp(A, 1, s) + B * step(1 - P.y * 0.1, pow(rim, 1)) ; break; }
    case 46: { // Gold statue / chrome (metal keeping the shape)
        float3 r = reflect(-v, n);
        float env = 0.35 + 0.65 * saturate(r.y * 0.5 + 0.5) + 0.2 * sin(r.x * 8);
        float spec = pow(saturate(dot(r, L)), 60) * 2;
        o = A * env * (0.6 + 0.8 * Lum(c)) + spec; break; }
    case 47: { // Pulse (brightness throb)
        o = c * (1 + P.x * (0.5 + 0.5 * sin(t * 6.2831853))) + A * P.y * (0.5 + 0.5 * sin(t * 6.2831853)); break; }
    case 48: { // Rainbow shift (over the body)
        o = lerp(c, Rainbow(rel.y * max(P.x, 0.1) - t * 0.3) * (0.4 + Lum(c)), P.y); break; }
    case 49: { // Noise dissolve to colour
        float nz = Fbm3(rel * max(P.y, 0.1) * 8);
        float th = P.x + (P.z > 0.5 ? 0.5 + 0.5 * sin(t) : 0) * 0.5;
        float edge = smoothstep(th - 0.05, th, nz) - smoothstep(th, th + 0.02, nz);
        o = nz < th ? A : c;
        o += B * edge * 3; break; }
    case 50: { // Outline glow inside (fresnel colour)
        o = c + A * pow(rim, max(P.x, 0.5)) * P.y * 2; break; }
    }
    return lerp(c, o, saturate(f.Head.y));
}

float4 PsMaterial(float4 pos : SV_Position) : SV_Target0
{
    float2 px = pos.xy;
    int2 mp = MaskPixel(px * Screen.zw);
    int look = VisibleLook(mp);
    if (look < 0) discard;
    Look Lk = Looks[look];
    int first = (int)Lk.Extra.z, count = (int)Lk.Extra.w;
    if (count <= 0) discard;
    float3 c = Pic(px);
    float3 wp = WorldAt(mp);
    float3 xl = wp - WorldAt(mp - int2(1, 0)), xr = WorldAt(mp + int2(1, 0)) - wp;
    float3 yu = wp - WorldAt(mp - int2(0, 1)), yd = WorldAt(mp + int2(0, 1)) - wp;
    float3 n = normalize(cross(dot(xl, xl) < dot(xr, xr) ? xl : xr, dot(yu, yu) < dot(yd, yd) ? yu : yd));
    float3 v = normalize(Cam.xyz - wp);
    if (dot(n, v) < 0) n = -n;
    float rim = saturate(1 - saturate(dot(n, v)));
    float3 rel = wp - Lk.Origin.xyz;
    [loop] for (int i = 0; i < count; i++)
        c = ApplyFx(Effects[first + i], c, px, mp, wp, rel, n, v, rim);
    return float4(saturate(c), 1);
}

// ---- Rounded slime: the character as one wobbling blob of jelly. Its shape is how much of a disc round each pixel is
// slime (thin bits melt away, gaps between fingers and legs fill in, edges round off), its surface tilts with that, and
// what's behind is bent through it, tinted deeper where it's thicker, with a wet shine.
int SlimeOf(int look)
{
    Look Lk = Looks[look];
    int first = (int)Lk.Extra.z, count = (int)Lk.Extra.w;
    for (int i = 0; i < count; i++)
    {
        Fx f = Effects[first + i];
        if ((int)round(f.Head.x) == 13 && f.P.w > 0.5) return first + i;
    }
    return -1;
}

float4 PsSlime(float4 pos : SV_Position) : SV_Target0
{
    float2 px = pos.xy;
    float R = max(Right.w, 2);
    float2 g = 0;
    float covered = 0, total = 0;
    int fx = -1;
    float tBase = Cam.w;
    // the centre counts double
    int lc = VisibleLook(MaskPixel(px * Screen.zw));
    int fc = lc >= 0 ? SlimeOf(lc) : -1;
    if (fc >= 0) { covered += 2; fx = fc; }
    total += 2;
    [loop] for (int ring = 1; ring <= 3; ring++)
    {
        [loop] for (int k = 0; k < 12; k++)
        {
            float a = (k + ring * 0.5) * 0.5235988;
            float2 dir = float2(cos(a), sin(a));
            float rr = R * ring / 3.0;
            // (the edge breathes a little)
            rr *= 1 + 0.12 * sin(tBase * 2.3 + a * 3 + ring);
            float2 q = px + dir * rr;
            if (any(q < 0) || any(q >= Screen.xy)) { total += 1; continue; }
            int lk = VisibleLook(MaskPixel(q * Screen.zw));
            int f = lk >= 0 ? SlimeOf(lk) : -1;
            total += 1;
            if (f >= 0)
            {
                covered += 1;
                g += dir * (1.0 - (ring - 1) * 0.25);
                if (fx < 0) fx = f;
            }
        }
    }
    if (fx < 0) discard;
    float C = covered / total;
    float alpha = smoothstep(0.42, 0.58, C);
    if (alpha <= 0.001) discard;
    Fx F = Effects[fx];
    float t = Cam.w * F.Head.z;
    float3 A = F.A.rgb;
    float4 P = F.P;
    // the surface: tipped outward where the blob thins, a slow wobble on top
    float2 outward = -g / max(covered, 1);
    float2 wob = float2(Noise3(float3(px * 0.02, t * 0.7)), Noise3(float3(px * 0.02 + 9, t * 0.7))) - 0.5;
    float2 tilt = outward * 2.2 + wob * 0.6;
    float3 V = normalize(cross(Right.xyz, Up.xyz));
    float3 N = normalize(Right.xyz * tilt.x - Up.xyz * tilt.y + V * (0.35 + C));
    if (dot(N, V) < 0) N = -N;
    float3 L = normalize(V + Up.xyz * 0.8 + Right.xyz * 0.35);
    // what's behind, bent through it
    float2 off = (tilt * P.x * 1.5) + wob * P.x;
    float3 behind = Pic(px + off);
    float thick = saturate(C * 1.3);
    float3 o = lerp(behind, A * (0.3 + Lum(behind) * 0.9), saturate(P.z * (0.35 + thick * 0.65)));
    float facing = saturate(dot(N, V));
    o += A * pow(1 - facing, 2.5) * 0.7;
    o += pow(saturate(dot(reflect(-V, N), L)), 70) * P.y * 1.8;
    o += pow(saturate(dot(reflect(-V, N), normalize(V - Up.xyz * 0.3 - Right.xyz * 0.6))), 25) * P.y * 0.25;
    return float4(saturate(o), alpha * saturate(F.Head.y));
}

float4 PsOutline(float4 pos : SV_Position) : SV_Target0
{
    float2 uv = pos.xy * Screen.zw;
    int2 p = MaskPixel(uv);
    if (VisibleLook(p) >= 0) discard;
    // a marked pixel round about, within its look's outline width
    float scale = max(Mask.x * Screen.z, 0.25); // (mask pixels per picture pixel)
    float reach = Mask.z * scale;
    float step = max(1.0, reach / 6.0);
    [loop] for (float r = 1; r <= reach + 0.01; r += step)
    {
        [loop] for (int k = 0; k < 12; k++)
        {
            float a = k * 0.5235988;
            int2 q = p + int2(round(cos(a) * r), round(sin(a) * r));
            if (any(q < 0) || any(q >= (int2)Mask.xy)) continue;
            int look = VisibleLook(q);
            if (look < 0) continue;
            Look L = Looks[look];
            if (L.Outline.w * scale >= r) return float4(L.Outline.rgb, 1);
        }
    }
    discard;
    return 0;
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
        _vsFull = Compile("VsFull", "vs_5_0");
        _psMask = Compile("PsMask", "ps_5_0");
        _psXray = Compile("PsXray", "ps_5_0");
        _psLight = Compile("PsLight", "ps_5_0");
        _psOutline = Compile("PsOutline", "ps_5_0");
        _psPixel = Compile("PsPixel", "ps_5_0");
        _psMaterial = Compile("PsMaterial", "ps_5_0");
        _psSlime = Compile("PsSlime", "ps_5_0");

        nint o = 0;
        var cbDesc = new BufferDesc { ByteWidth = (uint)((sizeof(Constants) + 15) / 16 * 16), Usage = UsageDynamic, BindFlags = BindConstant, CpuAccessFlags = CpuWrite };
        _cb = Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &cbDesc, null, &o), o, "constants");
        var mDesc = new BufferDesc { ByteWidth = 16, Usage = UsageDynamic, BindFlags = BindConstant, CpuAccessFlags = CpuWrite };
        nint mo = 0;
        _maskCb = Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &mDesc, null, &mo), mo, "mask constants");

        nint MakeBlend(RtBlend rt)
        {
            var b = new BlendDesc { RT0 = rt };
            nint s = 0;
            return Check(((delegate* unmanaged[Stdcall]<nint, BlendDesc*, nint*, int>)V(device, 20))(device, &b, &s), s, "blend");
        }
        _blendAdd = MakeBlend(new RtBlend { BlendEnable = 1, Src = 2, Dest = 2, Op = 1, SrcAlpha = 1, DestAlpha = 2, OpAlpha = 1, WriteMask = 0x7 });
        _blendSolid = MakeBlend(new RtBlend { BlendEnable = 0, Src = 2, Dest = 1, Op = 1, SrcAlpha = 2, DestAlpha = 1, OpAlpha = 1, WriteMask = 0x7 });
        _blendWrite = MakeBlend(new RtBlend { BlendEnable = 0, Src = 2, Dest = 1, Op = 1, SrcAlpha = 2, DestAlpha = 1, OpAlpha = 1, WriteMask = 0xF });
        _blendNoColour = MakeBlend(new RtBlend { BlendEnable = 0, Src = 2, Dest = 1, Op = 1, SrcAlpha = 2, DestAlpha = 1, OpAlpha = 1, WriteMask = 0 });
        _blendAlpha = MakeBlend(new RtBlend { BlendEnable = 1, Src = 5, Dest = 6, Op = 1, SrcAlpha = 1, DestAlpha = 2, OpAlpha = 1, WriteMask = 0x7 });

        nint MakeDepth(bool on, int func)
        {
            var d = new DepthDesc
            {
                DepthEnable = on ? 1 : 0, WriteMask = 0, Func = func, StencilEnable = 0, StencilMasks = 0xFFFF,
                Front = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 }, Back = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 },
            };
            nint ds = 0;
            return Check(((delegate* unmanaged[Stdcall]<nint, DepthDesc*, nint*, int>)V(device, 21))(device, &d, &ds), ds, "depth state");
        }
        _depthOff = MakeDepth(false, 8);
        _depthEqual = MakeDepth(true, 3);
        // (Limber's own depth for a hidden body: nearest wins; 4 less or equal, 7 greater or equal for depth that runs backwards)
        var dw = new DepthDesc
        {
            DepthEnable = 1, WriteMask = 1, Func = 4, StencilEnable = 0, StencilMasks = 0xFFFF,
            Front = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 }, Back = new StencilOp { Fail = 1, DepthFail = 1, Pass = 1, Func = 8 },
        };
        nint dws = 0;
        _depthOwn = Check(((delegate* unmanaged[Stdcall]<nint, DepthDesc*, nint*, int>)V(device, 21))(device, &dw, &dws), dws, "depth state");
        dw.Func = 7;
        nint dwr = 0;
        _depthOwnR = Check(((delegate* unmanaged[Stdcall]<nint, DepthDesc*, nint*, int>)V(device, 21))(device, &dw, &dwr), dwr, "depth state");

        var r = new RasterDesc { Fill = 3, Cull = 1, DepthClip = 1 };
        nint rs = 0;
        _rasterNone = Check(((delegate* unmanaged[Stdcall]<nint, RasterDesc*, nint*, int>)V(device, 22))(device, &r, &rs), rs, "rasterizer");

        EnsureLooks(16);
        // (what a shader resource view's class looks like: how the game's own are recognised in its memory)
        _srvClass = *(nint*)_looksSrv;
    }

    private static void EnsureLooks(int count)
    {
        if(count <= _looksCap && _looksBuf != 0)
            return;
        Release(_looksSrv);
        Release(_looksBuf);
        int cap = Math.Max(16, (int)BitOperations.RoundUpToPowerOf2((uint)count));
        var device = Device();
        var desc = new BufferDesc { ByteWidth = (uint)(cap * sizeof(LookGpu)), Usage = UsageDynamic, BindFlags = BindSrv, CpuAccessFlags = CpuWrite, MiscFlags = MiscStructured, StructureByteStride = (uint)sizeof(LookGpu) };
        nint b = 0;
        _looksBuf = Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &desc, null, &b), b, "looks");
        var srv = new BufferSrvDesc { Format = 0, ViewDimension = 1, FirstElement = 0, NumElements = (uint)cap };
        nint v = 0;
        _looksSrv = Check(((delegate* unmanaged[Stdcall]<nint, nint, BufferSrvDesc*, nint*, int>)V(device, 7))(device, _looksBuf, &srv, &v), v, "looks view");
        _looksCap = cap;
    }

    private static void EnsureFx(int count)
    {
        if(count <= _fxCap && _fxBuf != 0)
            return;
        Release(_fxSrv);
        Release(_fxBuf);
        int cap = Math.Max(16, (int)BitOperations.RoundUpToPowerOf2((uint)count));
        var device = Device();
        var desc = new BufferDesc { ByteWidth = (uint)(cap * sizeof(FxGpu)), Usage = UsageDynamic, BindFlags = BindSrv, CpuAccessFlags = CpuWrite, MiscFlags = MiscStructured, StructureByteStride = (uint)sizeof(FxGpu) };
        nint b = 0;
        _fxBuf = Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &desc, null, &b), b, "material effects");
        var srv = new BufferSrvDesc { Format = 0, ViewDimension = 1, FirstElement = 0, NumElements = (uint)cap };
        nint v = 0;
        _fxSrv = Check(((delegate* unmanaged[Stdcall]<nint, nint, BufferSrvDesc*, nint*, int>)V(device, 7))(device, _fxBuf, &srv, &v), v, "material effects view");
        _fxCap = cap;
    }

    // The masks, the game depth's size.
    private static void EnsureMasks(uint w, uint h)
    {
        if(_maskARtv != 0 && _maskW == w && _maskH == h)
            return;
        foreach(var o in new[] { _maskARtv, _maskASrv, _maskA, _maskBRtv, _maskBSrv, _maskB, _ownDsv, _ownDepth })
            Release(o);
        _maskA = _maskARtv = _maskASrv = _maskB = _maskBRtv = _maskBSrv = _ownDsv = _ownDepth = 0;
        var device = Device();
        (nint Tex, nint Rtv, nint Srv) Make(uint format, string what)
        {
            var desc = new Texture2DDesc { Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1, Usage = UsageDefault, BindFlags = BindSrv | BindRt };
            nint t = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &desc, null, &t), t, what);
            nint rt = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 9))(device, t, null, &rt), rt, what + " target");
            nint sv = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 7))(device, t, null, &sv), sv, what + " view");
            return (t, rt, sv);
        }
        (_maskA, _maskARtv, _maskASrv) = Make(FormatR32G32F, "mask");
        (_maskB, _maskBRtv, _maskBSrv) = Make(FormatR8, "see-through mask");
        var dd = new Texture2DDesc { Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = 40, SampleCount = 1, Usage = UsageDefault, BindFlags = 0x40 };
        nint dt = 0;
        _ownDepth = Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &dd, null, &dt), dt, "depth");
        nint dv = 0;
        _ownDsv = Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 10))(device, _ownDepth, null, &dv), dv, "depth view");
        _maskW = w;
        _maskH = h;
        ClearMasks(Context());
    }

    private static void ClearMasks(nint c)
    {
        if(c == 0)
            return;
        float* zero = stackalloc float[4];
        if(_maskARtv != 0)
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)V(c, 50))(c, _maskARtv, zero);
        if(_maskBRtv != 0)
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)V(c, 50))(c, _maskBRtv, zero);
        if(_ownDsv != 0)
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, float, byte, void>)V(c, 53))(c, _ownDsv, 1, _reversed ? 0f : 1f, 0);
    }

    // ---- Which of the game's draws are the chosen characters' (by their own bone matrices) and which look each gets

    private sealed class Entry
    {
        public (uint Start, uint Count, int Look)[] Ranges = [];
        public int Fallback = -1;
        public bool SeeThrough;
    }

    // a bone matrix buffer's view -> what's drawn with it
    private static volatile Dictionary<nint, Entry> _map = new();

    private sealed class ModelData
    {
        public List<(uint Start, uint Count, int Table, string Part)> Meshes = new();
    }

    private static readonly ConcurrentDictionary<string, Task<ModelData?>> Models = new(StringComparer.OrdinalIgnoreCase);

    private static ModelData? ReadModel(string file)
    {
        try
        {
            var data = Limber.UI.LimberTab.ReadModelFile(file);
            if(data == null)
                return null;
            var result = new ModelData();
            foreach(var (_, start, count, table, material) in MdlRigger.MeshRanges(data, 0))
                result.Meshes.Add((start, count, table, MdlRigger.IsSkinMaterial(material) ? "Skin" : Shaders.PartOf(material, 0, false)));
            return result;
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

    // A bone table's ring buffers -> their shader resource views (found in the game's objects by their class; only views
    // of buffers).
    private static readonly Dictionary<nint, nint[]> ViewsOf = new();
    private static readonly HashSet<string> Reported = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsBufferView(nint view)
    {
        nint res = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(view, 7))(view, &res);
        if(res == 0)
            return false;
        int dimension = 0;
        ((delegate* unmanaged[Stdcall]<nint, int*, void>)V(res, 7))(res, &dimension);
        Release(res);
        return dimension == 1;
    }

    private static nint[] TableViews(nint table)
    {
        if(ViewsOf.TryGetValue(table, out var known) && known.Length > 0)
            return known;
        var found = new List<nint>();
        void Scan(nint at, int bytes, int depth)
        {
            if(!SafeMemory.Plausible(at))
                return;
            for(int off = 0; off + 8 <= bytes; off += 8)
            {
                nint p = SafeMemory.ReadPointer(at + off);
                if(!SafeMemory.Plausible(p))
                    continue;
                if(SafeMemory.ReadPointer(p) == _srvClass)
                {
                    if(!found.Contains(p) && IsBufferView(p))
                        found.Add(p);
                }
                else if(depth > 0)
                    Scan(p, 0x80, depth - 1);
            }
        }
        for(int k = 0; k < 4; k++)
            Scan(SafeMemory.ReadPointer(table + 0x90 + k * 8), 0x100, 0);
        if(found.Count == 0)
            for(int k = 0; k < 4; k++)
                Scan(SafeMemory.ReadPointer(table + 0x90 + k * 8), 0x100, 1);
        var views = found.ToArray();
        ViewsOf[table] = views;
        return views;
    }

    private static readonly List<LookGpu> _looks = new();
    private static float _widest, _block, _slime;
    private static readonly List<FxGpu> _fx = new();
    private static readonly List<bool> _hide = new();

    private static LookGpu ToGpu(ShaderLook l)
    {
        Vector3 C(float[]? a, Vector3 d) => a is { Length: >= 3 } ? new Vector3(a[0], a[1], a[2]) : d;
        return new LookGpu
        {
            Light = new Vector4(C(l.Light, new(1f, 0.55f, 0.85f)), Math.Max(0f, l.LightStrength)),
            Rim = new Vector4(C(l.Rim, new(0.55f, 0.8f, 1f)), Math.Max(0f, l.RimStrength)),
            Outline = new Vector4(C(l.Outline, Vector3.Zero), Math.Clamp(l.OutlineWidth, 0f, 20f)),
            Misc = new Vector4(Math.Clamp(l.RimTightness, 0.5f, 12f), Math.Max(0f, l.Pulse), l.SeeThrough ? 1f : 0f, 0f),
            Extra = new Vector4(Math.Clamp(l.Pixelate, 0f, 64f), l.GameBoy ? 1f : 0f, 0f, 0f),
        };
    }

    // The map for the next frame's draws, and the looks.
    private static void BuildMap(IReadOnlyList<Shaders.EffectTarget> targets)
    {
        var map = new Dictionary<nint, Entry>();
        var shared = new HashSet<nint>();
        _looks.Clear();
        _hide.Clear();
        _fx.Clear();
        _widest = 0f;
        _block = 0f;
        _slime = 0f;
        foreach(var target in targets)
        {
            var gameObject = ActorSkeleton.GetObject(target.Index);
            if(gameObject == null || (nint)gameObject != target.Address || gameObject->DrawObject == null)
                continue;
            var lookOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // (the material Glow can't light skin, the game's skin shader has no glow: for skin, that look's drawn light
            // takes it instead)
            var skinOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var glowOnlyOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var defOf = new Dictionary<string, ShaderLook>(StringComparer.OrdinalIgnoreCase);
            bool seeThrough = false;
            foreach(var l in target.Looks.Where(l => l.Enabled && l.HasEffects()).OrderBy(l => l.Part == "All" ? 0 : 1))
            {
                lookOf[l.Part] = _looks.Count;
                defOf[l.Part] = l;
                var g = ToGpu(l);
                int fxFirst = _fx.Count;
                foreach(var f in (l.Fx ?? new()).Where(f => f.On).Take(8))
                {
                    int kind = MaterialFx.IdOf(f.Kind);
                    if(kind <= 0)
                        continue;
                    if(kind == 13 && f.P is { Length: > 3 } && f.P[3] > 0.5f)
                        _slime = MathF.Max(_slime, f.P[3]);
                    _fx.Add(new FxGpu
                    {
                        Head = new Vector4(kind, Math.Clamp(f.Strength, 0f, 1f), f.Speed, 0f),
                        A = new Vector4(MaterialFx.Colour(f.A), 1f),
                        B = new Vector4(MaterialFx.Colour(f.B), 1f),
                        P = new Vector4(f.P is { Length: > 0 } ? f.P[0] : 0f, f.P is { Length: > 1 } ? f.P[1] : 0f, f.P is { Length: > 2 } ? f.P[2] : 0f, f.P is { Length: > 3 } ? f.P[3] : 0f),
                    });
                }
                g.Extra.Z = fxFirst;
                g.Extra.W = _fx.Count - fxFirst;
                g.Origin = new Vector4(gameObject->Position.X, gameObject->Position.Y, gameObject->Position.Z, 0f);
                _looks.Add(g);
                _hide.Add(l.HideBody);
                if(l.GlowStrength > 0f)
                {
                    var glow = l.Glow is { Length: >= 3 } gc ? new Vector3(gc[0], gc[1], gc[2]) : Vector3.One;
                    var sg = g;
                    var mixed = sg.Light.W > 0f ? (new Vector3(sg.Light.X, sg.Light.Y, sg.Light.Z) * sg.Light.W + glow * l.GlowStrength) / (sg.Light.W + l.GlowStrength) : glow;
                    sg.Light = new Vector4(mixed, sg.Light.W + l.GlowStrength);
                    skinOf[l.Part] = _looks.Count;
                    _looks.Add(sg);
                    _hide.Add(l.HideBody);
                    // (the glow alone, for a look whose effects are kept off the body but whose material isn't)
                    var go = new LookGpu { Light = new Vector4(glow, l.GlowStrength), Rim = default, Outline = default, Misc = new Vector4(3f, 0f, 0f, 0f), Extra = default, Origin = g.Origin };
                    glowOnlyOf[l.Part] = _looks.Count;
                    _looks.Add(go);
                    _hide.Add(false);
                }
                _widest = MathF.Max(_widest, g.Outline.W);
                _block = MathF.Max(_block, g.Extra.X);
                seeThrough |= l.SeeThrough;
            }
            if(lookOf.Count == 0)
                continue;
            // A part's look: its own, else the one for everything; the effects only where that look lets them (body: skin,
            // face, hair; outfit: gear), and the skin's drawn glow only where its material changes are let on the body.
            int LookFor(string part)
            {
                string from = lookOf.ContainsKey(part) ? part : "All";
                if(!defOf.TryGetValue(from, out var def))
                    return -1;
                bool effects = def.EffectOn(part);
                bool glow = part == "Skin" && def.MaterialOn(part) && skinOf.ContainsKey(from);
                if(effects && glow)
                    return skinOf[from];
                if(effects)
                    return lookOf[from];
                if(glow)
                    return glowOnlyOf[from];
                return -1;
            }
            int all = defOf.TryGetValue("All", out var allDef) && allDef.EffectBody && allDef.EffectOutfit ? lookOf["All"] : -1;
            var cb = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)gameObject->DrawObject;
            int slots = SafeMemory.Read<int>((nint)(&cb->SlotCount), 0);
            nint models = SafeMemory.ReadPointer((nint)(&cb->Models));
            if(slots <= 0 || slots > 64 || models == 0)
                continue;
            foreach(var (slot, file) in LiveMesh.ModelFiles((nint)cb))
            {
                if(slot >= slots || !file.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                    continue;
                nint model = SafeMemory.ReadPointer(models + slot * 8);
                if(!SafeMemory.Plausible(model))
                    continue;
                var data = ModelOf(file);
                nint list = SafeMemory.ReadPointer(model + 0x58);
                int tables = SafeMemory.Read<int>(model + 0x60, 0);
                if(!SafeMemory.Plausible(list) || tables <= 0 || tables > 16)
                    continue;
                int views = 0;
                for(int t = 0; t < tables; t++)
                {
                    var entry = new Entry { Fallback = all, SeeThrough = seeThrough };
                    if(data != null)
                    {
                        int table = t;
                        entry.Ranges = data.Meshes.Where(m => m.Table == table)
                            .Select(m => (m.Start, m.Count, LookFor(m.Part))).ToArray();
                    }
                    foreach(var view in TableViews(list + t * 0xE0))
                    {
                        // (a view found under more than one table isn't one character's own: left out)
                        if(!map.TryAdd(view, entry))
                            shared.Add(view);
                        views++;
                    }
                }
                if(Reported.Add(file))
                    Plugin.Log.Information($"Limber: effects on {file}: {tables} bone tables, {views} of the game's views found for them");
            }
        }
        foreach(var view in shared)
            map.Remove(view);
        Views = map.Count;
        _hidden = _hide.ToArray();
        _map = map;
        if(map.Count > 0)
            EnsureHooks();
        SetHooks(map.Count > 0);
    }

    // ---- The game's draws, marked as they happen

    private delegate void DrawIndexedDelegate(nint ctx, uint count, uint start, int baseVertex);
    private delegate void DrawIndexedInstancedDelegate(nint ctx, uint count, uint instances, uint start, int baseVertex, uint startInstance);

    private static Hook<DrawIndexedDelegate>? _drawIndexed;
    private static Hook<DrawIndexedInstancedDelegate>? _drawIndexedInstanced;
    private static bool _hookTried;
    private static int _marked;
    private static nint _mainDepth;

    private static void EnsureHooks()
    {
        if(_hookTried)
            return;
        _hookTried = true;
        try
        {
            var c = Context();
            if(c == 0)
                return;
            _drawIndexed = Plugin.GameInterop.HookFromAddress<DrawIndexedDelegate>(V(c, 12), DrawIndexedDetour);
            _drawIndexedInstanced = Plugin.GameInterop.HookFromAddress<DrawIndexedInstancedDelegate>(V(c, 20), DrawIndexedInstancedDetour);
        }
        catch(Exception e)
        {
            _error = "couldn't watch the game's draws: " + e.Message;
            _failed = true;
            Plugin.Log.Warning(e, "Limber: the shader effects can't watch the game's draws");
        }
    }

    private static void SetHooks(bool on)
    {
        try
        {
            if(on)
            {
                if(_drawIndexed is { IsEnabled: false })
                    _drawIndexed.Enable();
                if(_drawIndexedInstanced is { IsEnabled: false })
                    _drawIndexedInstanced.Enable();
            }
            else
            {
                if(_drawIndexed is { IsEnabled: true })
                    _drawIndexed.Disable();
                if(_drawIndexedInstanced is { IsEnabled: true })
                    _drawIndexedInstanced.Disable();
            }
        }
        catch
        {
        }
    }

    private static void DrawIndexedDetour(nint ctx, uint count, uint start, int baseVertex)
    {
        int look = -1;
        bool seeThrough = false, ours = false;
        try
        {
            ours = Which(ctx, start, out look, out seeThrough);
            // a hidden body: not drawn into the picture, only into Limber's depth and mask
            if(ours && Hidden(look) && MarkHidden(ctx, look, seeThrough, () => _drawIndexed!.Original(ctx, count, start, baseVertex)))
                return;
        }
        catch
        {
            // never stop the game drawing
        }
        _drawIndexed!.Original(ctx, count, start, baseVertex);
        if(!ours)
            return;
        try
        {
            Mark(ctx, look, seeThrough, () => _drawIndexed!.Original(ctx, count, start, baseVertex));
        }
        catch
        {
        }
    }

    private static bool Hidden(int look)
    {
        var hidden = _hidden;
        return look >= 0 && look < hidden.Length && hidden[look];
    }

    private static void DrawIndexedInstancedDetour(nint ctx, uint count, uint instances, uint start, int baseVertex, uint startInstance)
    {
        int look = -1;
        bool seeThrough = false, ours = false;
        try
        {
            ours = Which(ctx, start, out look, out seeThrough);
            if(ours && Hidden(look) && MarkHidden(ctx, look, seeThrough, () => _drawIndexedInstanced!.Original(ctx, count, instances, start, baseVertex, startInstance)))
                return;
        }
        catch
        {
        }
        _drawIndexedInstanced!.Original(ctx, count, instances, start, baseVertex, startInstance);
        if(!ours)
            return;
        try
        {
            Mark(ctx, look, seeThrough, () => _drawIndexedInstanced!.Original(ctx, count, instances, start, baseVertex, startInstance));
        }
        catch
        {
        }
    }

    // A hidden body's draw (into the main picture only; false: not that, so the game draws it as usual): the game's own
    // draw with no colour into Limber's depth (its own shader still cuts out see-through hair), then the mask against it.
    private static bool MarkHidden(nint c, int look, bool seeThrough, Action draw)
    {
        if(_ownDsv == 0)
            return false;
        nint* rtvs = stackalloc nint[8];
        nint dsv = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint*, void>)V(c, 89))(c, 8, rtvs, &dsv);
        bool main = false;
        if(dsv != 0)
        {
            nint res = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(dsv, 7))(dsv, &res);
            main = res != 0 && res == _mainDepth;
            Release(res);
        }
        if(!main)
        {
            for(int i = 0; i < 8; i++)
                Release(rtvs[i]);
            Release(dsv);
            return false;
        }
        nint blend = 0, depth = 0, ps = 0, psCb = 0;
        float* factor = stackalloc float[4];
        uint sampleMask = 0, stencilRef = 0, classes = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, float*, uint*, void>)V(c, 91))(c, &blend, factor, &sampleMask);
        ((delegate* unmanaged[Stdcall]<nint, nint*, uint*, void>)V(c, 92))(c, &depth, &stencilRef);
        ((delegate* unmanaged[Stdcall]<nint, nint*, nint*, uint*, void>)V(c, 74))(c, &ps, null, &classes);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 77))(c, 1, 1, &psCb);
        try
        {
            float* none = stackalloc float[4];
            // 1. its depth, with the game's own shaders (no colour anywhere)
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 0, null, _ownDsv);
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendNoColour, none, 0xFFFFFFFF);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _reversed ? _depthOwnR : _depthOwn, 0);
            draw();

            // 2. the mask where that depth is this draw
            Mapped m;
            if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _maskCb, 0, 4, 0, &m) < 0)
                return true;
            *(Vector4*)m.Data = new Vector4(look + 1, 0, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _maskCb, 0);
            nint mcb = _maskCb;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 1, 1, &mcb);
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendWrite, none, 0xFFFFFFFF);
            nint rt = _maskARtv;
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rt, _ownDsv);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _depthEqual, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psMask, null, 0);
            draw();
            if(seeThrough)
            {
                rt = _maskBRtv;
                ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rt, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _depthOff, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psXray, null, 0);
                draw();
            }
            _marked++;
            return true;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 8, rtvs, dsv);
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, blend, factor, sampleMask);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, depth, stencilRef);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, ps, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 1, 1, &psCb);
            for(int i = 0; i < 8; i++)
                Release(rtvs[i]);
            Release(dsv);
            Release(blend);
            Release(depth);
            Release(ps);
            Release(psCb);
        }
    }

    // Is this draw one of the chosen characters' meshes? Which look then.
    private static bool Which(nint ctx, uint start, out int look, out bool seeThrough)
    {
        look = -1;
        seeThrough = false;
        var map = _map;
        if(map.Count == 0 || _maskARtv == 0)
            return false;
        nint* views = stackalloc nint[16];
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(ctx, 84))(ctx, 0, 16, views);
        Entry? entry = null;
        for(int i = 0; i < 16; i++)
        {
            if(views[i] == 0)
                continue;
            if(entry == null && map.TryGetValue(views[i], out var e))
                entry = e;
            Release(views[i]);
        }
        if(entry == null)
            return false;
        look = entry.Fallback;
        foreach(var (s, c, l) in entry.Ranges)
            if(start >= s && start < s + c)
            {
                look = l;
                break;
            }
        seeThrough = entry.SeeThrough;
        return look >= 0;
    }

    private static void Mark(nint c, int look, bool seeThrough, Action draw)
    {
        // into the main picture only (not shadows or reflections): its depth buffer is the game's own
        nint* rtvs = stackalloc nint[8];
        nint dsv = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint*, void>)V(c, 89))(c, 8, rtvs, &dsv);
        bool main = false;
        if(dsv != 0)
        {
            nint res = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(dsv, 7))(dsv, &res);
            main = res != 0 && res == _mainDepth;
            Release(res);
        }
        if(!main)
        {
            for(int i = 0; i < 8; i++)
                Release(rtvs[i]);
            Release(dsv);
            return;
        }
        nint blend = 0, depth = 0, ps = 0, psCb = 0;
        float* factor = stackalloc float[4];
        uint sampleMask = 0, stencilRef = 0, classes = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, float*, uint*, void>)V(c, 91))(c, &blend, factor, &sampleMask);
        ((delegate* unmanaged[Stdcall]<nint, nint*, uint*, void>)V(c, 92))(c, &depth, &stencilRef);
        ((delegate* unmanaged[Stdcall]<nint, nint*, nint*, uint*, void>)V(c, 74))(c, &ps, null, &classes);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 77))(c, 1, 1, &psCb);
        try
        {
            Mapped m;
            if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _maskCb, 0, 4, 0, &m) < 0)
                return;
            *(Vector4*)m.Data = new Vector4(look + 1, 0, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _maskCb, 0);
            nint mcb = _maskCb;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 1, 1, &mcb);
            float* none = stackalloc float[4];
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendWrite, none, 0xFFFFFFFF);

            // what shows: the same draw again, only where it's what the depth now holds
            nint rt = _maskARtv;
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rt, dsv);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _depthEqual, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psMask, null, 0);
            draw();
            // what's behind things (for seeing through walls): no depth test
            if(seeThrough)
            {
                rt = _maskBRtv;
                ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rt, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _depthOff, 0);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psXray, null, 0);
                draw();
            }
            _marked++;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 8, rtvs, dsv);
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, blend, factor, sampleMask);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, depth, stencilRef);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, ps, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 1, 1, &psCb);
            for(int i = 0; i < 8; i++)
                Release(rtvs[i]);
            Release(dsv);
            Release(blend);
            Release(depth);
            Release(ps);
            Release(psCb);
        }
    }

    // ---- The device's state, kept and put back (for the effects over the picture)

    private struct Saved
    {
        public fixed long Rtv[8];
        public nint Dsv, Blend, Depth, Raster, Vs, Ps, Layout, PsCb;
        public fixed long PsSrv[6];
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
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 77))(c, 0, 1, &s->PsCb);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 73))(c, 0, 6, s->PsSrv);
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
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 0, 1, &s->PsCb);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 8))(c, 0, 6, s->PsSrv);
        for(int i = 0; i < 8; i++)
            Release((nint)s->Rtv[i]);
        for(int i = 0; i < 6; i++)
            Release((nint)s->PsSrv[i]);
        for(uint i = 0; i < s->VsInstances; i++)
            Release((nint)s->VsClass[i]);
        for(uint i = 0; i < s->PsInstances; i++)
            Release((nint)s->PsClass[i]);
        foreach(var o in new[] { s->Dsv, s->Blend, s->Depth, s->Raster, s->Vs, s->Ps, s->Layout, s->PsCb })
            Release(o);
        NativeMemory.Clear(s, (nuint)sizeof(Saved));
    }

    private static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>
    /// Once a frame, when the game has drawn it (render thread): the effects from what was marked over the picture, the
    /// masks cleared, and which draws to mark next frame. Nothing when nobody has an effect on.
    /// </summary>
    public static void Draw(IReadOnlyList<Shaders.EffectTarget> targets)
    {
        Drawn = _marked;
        _marked = 0;
        if(_failed)
        {
            if(_map.Count > 0)
            {
                _map = new();
                SetHooks(false);
            }
            return;
        }
        if(targets.Count == 0)
        {
            if(_map.Count > 0)
            {
                _map = new();
                SetHooks(false);
                ClearMasks(Context());
            }
            Views = 0;
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
            if(_vsFull == 0)
                Setup();
            // the game's depth: what the masks match in size, and how its draws into the picture are told apart
            var rtm = RenderTargetManager.Instance();
            if(rtm == null || rtm->DepthStencil == null || rtm->DepthStencil->D3D11ShaderResourceView == null || rtm->DepthStencil->D3D11Texture2D == null)
                return;
            var ds = rtm->DepthStencil;
            _mainDepth = (nint)ds->D3D11Texture2D;
            Texture2DDesc dd;
            ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)V(_mainDepth, 10))(_mainDepth, &dd);
            if(dd.SampleCount != 1 || dd.Width == 0 || dd.Height == 0)
            {
                _error = "the game's depth can't be read (multisampled)";
                return;
            }
            _reversed = DepthRunsBackwards();
            bool sameSize = _maskW == dd.Width && _maskH == dd.Height;
            EnsureMasks(dd.Width, dd.Height);
            var looks = _looks.ToArray();

            // 1. the effects from this frame's marks
            if(sameSize && looks.Length > 0)
            {
                var swap = (nint)kernel->SwapChain->DXGISwapChain;
                var iid = Texture2DId;
                nint t = 0;
                int hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)V(swap, 9))(swap, 0, &iid, &t);
                if(hr >= 0 && t != 0)
                {
                    backTex = t;
                    Texture2DDesc bd;
                    ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)V(backTex, 10))(backTex, &bd);
                    nint r = 0;
                    if(bd.Width > 0 && bd.Height > 0 && bd.SampleCount == 1
                       && ((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 9))(device, backTex, null, &r) >= 0 && r != 0)
                    {
                        backRtv = r;
                        uint aw = ds->ActualWidth > 0 ? (uint)ds->ActualWidth : dd.Width, ah = ds->ActualHeight > 0 ? (uint)ds->ActualHeight : dd.Height;
                        var constants = MakeConstants(bd.Width, bd.Height, aw, ah, looks.Length);
                        if(constants != null)
                        {
                            Save(c);
                            saved = true;
                            Composite(c, backRtv, backTex, bd, constants.Value, looks, (nint)ds->D3D11ShaderResourceView);
                            Restore(c);
                            saved = false;
                        }
                    }
                }
            }
            ClearMasks(c);

            // 2. which draws to mark next frame
            BuildMap(targets);
            _error = "";
        }
        catch(Exception e)
        {
            _failed = true;
            _error = e.Message;
            _map = new();
            SetHooks(false);
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

    // A copy of the picture (what the pixelate reads from), the picture's size and format.
    private static bool CopyPicture(nint c, nint backTex, Texture2DDesc bd)
    {
        if(_sceneCopy == 0 || _sceneW != bd.Width || _sceneH != bd.Height || _sceneFormat != bd.Format)
        {
            Release(_sceneCopySrv);
            Release(_sceneCopy);
            _sceneCopySrv = _sceneCopy = 0;
            var device = Device();
            var desc = new Texture2DDesc { Width = bd.Width, Height = bd.Height, MipLevels = 1, ArraySize = 1, Format = bd.Format, SampleCount = 1, Usage = UsageDefault, BindFlags = BindSrv };
            nint t = 0;
            if(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &desc, null, &t) < 0 || t == 0)
                return false;
            nint v = 0;
            if(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)V(device, 7))(device, t, null, &v) < 0 || v == 0)
            {
                Release(t);
                return false;
            }
            _sceneCopy = t;
            _sceneCopySrv = v;
            _sceneW = bd.Width;
            _sceneH = bd.Height;
            _sceneFormat = bd.Format;
        }
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)V(c, 47))(c, _sceneCopy, backTex);
        return true;
    }

    private static void Composite(nint c, nint backRtv, nint backTex, Texture2DDesc bd, Constants constants, LookGpu[] looks, nint sceneSrv)
    {
        bool material = _fx.Count > 0;
        // (nothing drawing into the picture while it's copied)
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 0, null, 0);
        bool copied = (material || _block >= 1f) && CopyPicture(c, backTex, bd);
        bool pixelate = _block >= 1f && copied;
        material &= copied;
        EnsureLooks(looks.Length);
        if(material)
        {
            EnsureFx(_fx.Count);
            Mapped fm;
            if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _fxBuf, 0, 4, 0, &fm) < 0)
                return;
            var fx = _fx.ToArray();
            fixed(FxGpu* p = fx)
                Buffer.MemoryCopy(p, fm.Data, (long)_fxCap * sizeof(FxGpu), (long)fx.Length * sizeof(FxGpu));
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _fxBuf, 0);
        }
        Mapped m;
        if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _looksBuf, 0, 4, 0, &m) < 0)
            return;
        fixed(LookGpu* p = looks)
            Buffer.MemoryCopy(p, m.Data, (long)_looksCap * sizeof(LookGpu), (long)looks.Length * sizeof(LookGpu));
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _looksBuf, 0);
        if(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(c, 14))(c, _cb, 0, 4, 0, &m) < 0)
            return;
        *(Constants*)m.Data = constants;
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 15))(c, _cb, 0);

        nint* none = stackalloc nint[6];
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 6, none);
        nint rtv = backRtv;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rtv, 0);
        var vp = new Viewport { Width = bd.Width, Height = bd.Height, MinDepth = 0, MaxDepth = 1 };
        ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)V(c, 44))(c, 1, &vp);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 17))(c, 0);
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)V(c, 24))(c, 4);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(c, 43))(c, _rasterNone);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(c, 36))(c, _depthOff, 0);
        nint cb = _cb;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 16))(c, 0, 1, &cb);
        nint* inputs = stackalloc nint[6] { _maskASrv, _maskBSrv, sceneSrv, _looksSrv, copied ? _sceneCopySrv : 0, material ? _fxSrv : 0 };
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 6, inputs);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vsFull, null, 0);
        float* zero = stackalloc float[4];

        // the material effects first (they turn the picture there into something else)
        if(material)
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendSolid, zero, 0xFFFFFFFF);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psMaterial, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 3, 0);
            // the rounded slime (over and round the character, blended in at its soft edge)
            if(_slime > 0.5f)
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendAlpha, zero, 0xFFFFFFFF);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psSlime, null, 0);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 3, 0);
            }
            // (the mosaic reads what they made)
            if(pixelate)
            {
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 6, none);
                ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 0, null, 0);
                CopyPicture(c, backTex, bd);
                ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)V(c, 33))(c, 1, &rtv, 0);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 6, inputs);
            }
        }
        // the mosaic (it replaces the picture there), then the rest over it
        if(pixelate)
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendSolid, zero, 0xFFFFFFFF);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psPixel, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 3, 0);
        }

        // the outline (drawn over), then the light, rim and see-through silhouette (added on)
        if(_widest > 0f)
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendSolid, zero, 0xFFFFFFFF);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psOutline, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 3, 0);
        }
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)V(c, 35))(c, _blendAdd, zero, 0xFFFFFFFF);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 9))(c, _psLight, null, 0);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)V(c, 13))(c, 3, 0);

        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 6, none);
    }

    // Whether the game's depth goes from 1 near to 0 far (most modern games), from its camera.
    private static bool DepthRunsBackwards()
    {
        var control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Instance();
        if(control == null)
            return _reversed;
        var vp = control->ViewProjectionMatrix;
        if(!Matrix4x4.Invert(vp, out var ivp))
            return _reversed;
        var ch = Vector4.Transform(new Vector4(0, 0, 1, 0), ivp);
        var mid = Vector4.Transform(new Vector4(0, 0, 0.5f, 1), ivp);
        if(MathF.Abs(ch.W) < 1e-12f || MathF.Abs(mid.W) < 1e-12f)
            return _reversed;
        var cam = new Vector3(ch.X, ch.Y, ch.Z) / ch.W;
        var fwd = new Vector3(mid.X, mid.Y, mid.Z) / mid.W - cam;
        if(fwd.LengthSquared() < 1e-12f)
            return _reversed;
        fwd = Vector3.Normalize(fwd);
        float Z(Vector3 at)
        {
            var c = Vector4.Transform(new Vector4(at, 1), vp);
            return MathF.Abs(c.W) < 1e-8f ? 0f : c.Z / c.W;
        }
        return Z(cam + fwd * 50f) < Z(cam + fwd * 1f);
    }

    private static Constants? MakeConstants(uint w, uint h, uint maskW, uint maskH, int looks)
    {
        var control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Instance();
        if(control == null)
            return null;
        var vp = control->ViewProjectionMatrix;
        if(!Matrix4x4.Invert(vp, out var ivp))
            return null;
        var ch = Vector4.Transform(new Vector4(0, 0, 1, 0), ivp);
        if(MathF.Abs(ch.W) < 1e-12f)
            return null;
        var cam = new Vector3(ch.X, ch.Y, ch.Z) / ch.W;
        if(!MathUtil.IsFinite(cam))
            return null;
        Vector3 Unproject(float x, float y)
        {
            var q = Vector4.Transform(new Vector4(x, y, 0.5f, 1), ivp);
            return new Vector3(q.X, q.Y, q.Z) / q.W;
        }
        var right = Unproject(1, 0) - Unproject(-1, 0);
        var up = Unproject(0, 1) - Unproject(0, -1);
        right = right.LengthSquared() > 1e-12f ? Vector3.Normalize(right) : Vector3.UnitX;
        up = up.LengthSquared() > 1e-12f ? Vector3.Normalize(up) : Vector3.UnitY;
        return new Constants
        {
            ViewProj = vp,
            InvViewProj = ivp,
            Cam = new Vector4(cam, (float)(Environment.TickCount64 % 3_600_000) / 1000f),
            Screen = new Vector4(w, h, 1f / w, 1f / h),
            Mask = new Vector4(maskW, maskH, _widest, _block),
            Right = new Vector4(right, _slime),
            Up = new Vector4(up, _reversed ? 1f : 0f),
        };
    }
}
