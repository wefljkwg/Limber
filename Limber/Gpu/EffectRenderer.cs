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
    private static nint _vsFull, _psMask, _psXray, _psLight, _psOutline, _cb, _maskCb;
    private static nint _blendAdd, _blendSolid, _blendWrite, _depthOff, _depthEqual, _rasterNone;
    private static nint _looksBuf, _looksSrv;
    private static int _looksCap;
    private static nint _maskA, _maskARtv, _maskASrv, _maskB, _maskBRtv, _maskBSrv;
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProj, InvViewProj;
        public Vector4 Cam;      // camera position, time
        public Vector4 Screen;   // w, h, 1/w, 1/h (the picture)
        public Vector4 Mask;     // the depth's drawn size (w, h), widest outline (pixels), how many looks
    }

    private const string Hlsl = @"
cbuffer C : register(b0)
{
    row_major float4x4 VP; row_major float4x4 IVP;
    float4 Cam; float4 Screen; float4 Mask;
};
cbuffer M : register(b1) { float4 Id; };
struct Look { float4 Light; float4 Rim; float4 Outline; float4 Misc; };
Texture2D<float2> MaskA : register(t0);
Texture2D<float> MaskB : register(t1);
Texture2D<float> SceneDepth : register(t2);
StructuredBuffer<Look> Looks : register(t3);

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
    return abs(d - a.y) <= 2e-6 + abs(a.y) * 2e-4 ? look : -1;
}

float3 WorldAt(int2 p)
{
    p = clamp(p, 0, (int2)Mask.xy - 1);
    float2 uv = (p + 0.5) / Mask.xy;
    float d = SceneDepth.Load(int3(p, 0));
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

    // The masks, the game depth's size.
    private static void EnsureMasks(uint w, uint h)
    {
        if(_maskARtv != 0 && _maskW == w && _maskH == h)
            return;
        foreach(var o in new[] { _maskARtv, _maskASrv, _maskA, _maskBRtv, _maskBSrv, _maskB })
            Release(o);
        _maskA = _maskARtv = _maskASrv = _maskB = _maskBRtv = _maskBSrv = 0;
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
    private static float _widest;

    private static LookGpu ToGpu(ShaderLook l)
    {
        Vector3 C(float[]? a, Vector3 d) => a is { Length: >= 3 } ? new Vector3(a[0], a[1], a[2]) : d;
        return new LookGpu
        {
            Light = new Vector4(C(l.Light, new(1f, 0.55f, 0.85f)), Math.Max(0f, l.LightStrength)),
            Rim = new Vector4(C(l.Rim, new(0.55f, 0.8f, 1f)), Math.Max(0f, l.RimStrength)),
            Outline = new Vector4(C(l.Outline, Vector3.Zero), Math.Clamp(l.OutlineWidth, 0f, 20f)),
            Misc = new Vector4(Math.Clamp(l.RimTightness, 0.5f, 12f), Math.Max(0f, l.Pulse), l.SeeThrough ? 1f : 0f, 0f),
        };
    }

    // The map for the next frame's draws, and the looks.
    private static void BuildMap(IReadOnlyList<Shaders.EffectTarget> targets)
    {
        var map = new Dictionary<nint, Entry>();
        var shared = new HashSet<nint>();
        _looks.Clear();
        _widest = 0f;
        foreach(var target in targets)
        {
            var gameObject = ActorSkeleton.GetObject(target.Index);
            if(gameObject == null || (nint)gameObject != target.Address || gameObject->DrawObject == null)
                continue;
            var lookOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            bool seeThrough = false;
            foreach(var l in target.Looks.Where(l => l.Enabled && l.HasEffects()).OrderBy(l => l.Part == "All" ? 0 : 1))
            {
                lookOf[l.Part] = _looks.Count;
                var g = ToGpu(l);
                _looks.Add(g);
                _widest = MathF.Max(_widest, g.Outline.W);
                seeThrough |= l.SeeThrough;
            }
            if(lookOf.Count == 0)
                continue;
            int all = lookOf.TryGetValue("All", out var a) ? a : -1;
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
                            .Select(m => (m.Start, m.Count, lookOf.TryGetValue(m.Part, out var own) ? own : all)).ToArray();
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
        _drawIndexed!.Original(ctx, count, start, baseVertex);
        try
        {
            if(Which(ctx, start, out var look, out bool seeThrough))
                Mark(ctx, look, seeThrough, () => _drawIndexed!.Original(ctx, count, start, baseVertex));
        }
        catch
        {
            // never stop the game drawing
        }
    }

    private static void DrawIndexedInstancedDetour(nint ctx, uint count, uint instances, uint start, int baseVertex, uint startInstance)
    {
        _drawIndexedInstanced!.Original(ctx, count, instances, start, baseVertex, startInstance);
        try
        {
            if(Which(ctx, start, out var look, out bool seeThrough))
                Mark(ctx, look, seeThrough, () => _drawIndexedInstanced!.Original(ctx, count, instances, start, baseVertex, startInstance));
        }
        catch
        {
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
        public fixed long PsSrv[4];
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
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 73))(c, 0, 4, s->PsSrv);
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
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, long*, void>)V(c, 8))(c, 0, 4, s->PsSrv);
        for(int i = 0; i < 8; i++)
            Release((nint)s->Rtv[i]);
        for(int i = 0; i < 4; i++)
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
                            Composite(c, backRtv, bd, constants.Value, looks, (nint)ds->D3D11ShaderResourceView);
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

    private static void Composite(nint c, nint backRtv, Texture2DDesc bd, Constants constants, LookGpu[] looks, nint sceneSrv)
    {
        EnsureLooks(looks.Length);
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

        nint* none = stackalloc nint[4];
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 4, none);
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
        nint* inputs = stackalloc nint[4] { _maskASrv, _maskBSrv, sceneSrv, _looksSrv };
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 4, inputs);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)V(c, 11))(c, _vsFull, null, 0);
        float* zero = stackalloc float[4];

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

        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(c, 8))(c, 0, 4, none);
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
        return new Constants
        {
            ViewProj = vp,
            InvViewProj = ivp,
            Cam = new Vector4(cam, (float)(Environment.TickCount64 % 3_600_000) / 1000f),
            Screen = new Vector4(w, h, 1f / w, 1f / h),
            Mask = new Vector4(maskW, maskH, _widest, looks),
        };
    }
}
