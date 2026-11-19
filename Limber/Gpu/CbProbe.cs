using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Limber.Core;

namespace Limber.Gpu;

/// <summary>
/// Research for the monitors' live character layer: which constant buffers the game's character shaders read the camera
/// from, and where in them. On request (the research console's "cbdump"), the next draw of a chosen character into the
/// main picture has every vertex and pixel shader constant buffer read back, and searched for the main render camera's
/// matrices (view, projection, and view x projection, either way round).
/// </summary>
public static unsafe class CbProbe
{
    public static volatile bool Asked;
    public static volatile string Result = "";
    public static volatile bool Ready;

    private static nint V(nint obj, int index) => ((nint*)*(nint*)obj)[index];

    private static void Release(nint obj)
    {
        if(obj != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)V(obj, 2))(obj);
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

    /// <summary>From EffectRenderer's draw hook, right after a chosen character's draw into the main picture.</summary>
    public static void OnCharacterDraw(nint ctx)
    {
        if(!Asked)
            return;
        Asked = false;
        var sb = new StringBuilder("Limber: constant buffers at a character draw\n");
        try
        {
            var cm = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
            if(cm == null || cm->CurrentCamera == null || cm->CurrentCamera->RenderCamera == null)
            {
                Result = "no camera";
                Ready = true;
                return;
            }
            nint rc = (nint)cm->CurrentCamera->RenderCamera;
            var view = *(Matrix4x4*)(rc + 0x10);
            var proj = *(Matrix4x4*)(rc + 0x1A0);
            var proj2 = *(Matrix4x4*)(rc + 0x50);
            var origin = *(Vector3*)(rc + 0x90);
            sb.Append($"camera: origin {origin}\n  view {Row(view)}\n  proj {Row(proj)}\n  proj2 {Row(proj2)}\n");
            var known = new List<(string Name, Matrix4x4 M)>
            {
                ("view", view), ("proj", proj), ("proj2", proj2),
                ("view*proj", view * proj), ("view*proj2", view * proj2),
            };
            var kernel = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
            nint device = (nint)kernel->D3D11Forwarder;
            for(int stage = 0; stage < 2; stage++)
            {
                nint* cbs = stackalloc nint[14];
                // VSGetConstantBuffers 72, PSGetConstantBuffers 77
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)V(ctx, stage == 0 ? 72 : 77))(ctx, 0, 14, cbs);
                for(int slot = 0; slot < 14; slot++)
                {
                    if(cbs[slot] == 0)
                        continue;
                    try
                    {
                        sb.Append(Probe(device, ctx, cbs[slot], stage == 0 ? "VS" : "PS", slot, known));
                    }
                    finally
                    {
                        Release(cbs[slot]);
                    }
                }
            }
        }
        catch(Exception e)
        {
            sb.Append("failed: " + e.Message + "\n");
        }
        Result = sb.ToString();
        Ready = true;
    }

    private static string Row(Matrix4x4 m) => string.Create(CultureInfo.InvariantCulture,
        $"[{m.M11:0.###} {m.M12:0.###} {m.M13:0.###} {m.M14:0.###} | {m.M21:0.###} {m.M22:0.###} {m.M23:0.###} {m.M24:0.###} | {m.M31:0.###} {m.M32:0.###} {m.M33:0.###} {m.M34:0.###} | {m.M41:0.###} {m.M42:0.###} {m.M43:0.###} {m.M44:0.###}]");

    private static string Probe(nint device, nint ctx, nint buffer, string stage, int slot, List<(string Name, Matrix4x4 M)> known)
    {
        BufferDesc d;
        ((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void>)V(buffer, 10))(buffer, &d);
        var sd = new BufferDesc { ByteWidth = d.ByteWidth, Usage = 3, CpuAccessFlags = 0x20000 };
        nint stage2 = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void*, nint*, int>)V(device, 3))(device, &sd, null, &stage2);
        if(hr < 0 || stage2 == 0)
            return $"{stage} cb{slot}: 0x{buffer:X} {d.ByteWidth} bytes, can't copy ({hr:X8})\n";
        try
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)V(ctx, 47))(ctx, stage2, buffer);
            Mapped m;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(ctx, 14))(ctx, stage2, 0, 1, 0, &m);
            if(hr < 0)
                return $"{stage} cb{slot}: 0x{buffer:X} {d.ByteWidth} bytes, can't read ({hr:X8})\n";
            try
            {
                int n = (int)d.ByteWidth / 4;
                var f = new float[n];
                Marshal.Copy((nint)m.Data, f, 0, n);
                var sb = new StringBuilder($"{stage} cb{slot}: 0x{buffer:X} {d.ByteWidth} bytes, usage {d.Usage}\n");
                // the known matrices, as 16 floats in either order, anywhere (on a 4-byte step)
                foreach(var (name, mat) in known)
                    for(int order = 0; order < 2; order++)
                    {
                        var want = Floats(order == 0 ? mat : Matrix4x4.Transpose(mat));
                        for(int at = 0; at + 12 <= n; at++)
                            if(Match(f, at, want, 12))
                                sb.Append($"    {name}{(order == 1 ? " (transposed)" : "")} at byte {at * 4} (0x{at * 4:X})\n");
                    }
                // the first 64 floats, for the eye
                sb.Append("    first: ");
                for(int i = 0; i < Math.Min(n, 96); i++)
                {
                    if(i % 16 == 0 && i > 0)
                        sb.Append("\n           ");
                    sb.Append(f[i].ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
                }
                sb.Append('\n');
                return sb.ToString();
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(ctx, 15))(ctx, stage2, 0);
            }
        }
        finally
        {
            Release(stage2);
        }
    }

    private static float[] Floats(Matrix4x4 m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };

    // the first `count` floats (a 4x3 is enough, and games often keep matrices as 3 rows)
    private static bool Match(float[] f, int at, float[] want, int count)
    {
        int nonzero = 0;
        for(int i = 0; i < count; i++)
        {
            float a = f[at + i], b = want[i];
            if(MathF.Abs(a - b) > 1e-3f + 1e-3f * MathF.Abs(b))
                return false;
            if(b != 0)
                nonzero++;
        }
        return nonzero >= 4;
    }
}
