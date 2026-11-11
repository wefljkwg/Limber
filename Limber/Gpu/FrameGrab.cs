using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Limber.Gpu;

/// <summary>
/// The game's picture as it is right now, copied off the graphics card (Direct3D 11 through the device's own tables,
/// like the liquid renderer). Called from Limber's draw (render thread), before the plugin windows are drawn into the
/// picture, so it holds only what the game drew: no Limber window, no dots, nothing from other plugins.
/// </summary>
public static unsafe class FrameGrab
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
    private struct Mapped
    {
        public void* Data;
        public uint RowPitch, DepthPitch;
    }

    private static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>The picture as RGBA bytes (4 per pixel, rows top to bottom), or null with the reason.</summary>
    public static byte[]? Grab(out int width, out int height, out string error)
    {
        width = height = 0;
        error = "";
        var kernel = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if(kernel == null || kernel->SwapChain == null || kernel->SwapChain->DXGISwapChain == null)
        {
            error = "the game's picture isn't there";
            return null;
        }
        nint device = (nint)kernel->D3D11Forwarder, context = (nint)kernel->D3D11DeviceContext;
        if(device == 0 || context == 0)
        {
            error = "no graphics device";
            return null;
        }
        nint back = 0, stage = 0;
        try
        {
            var swap = (nint)kernel->SwapChain->DXGISwapChain;
            var iid = Texture2DId;
            nint t = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)V(swap, 9))(swap, 0, &iid, &t);
            if(hr < 0 || t == 0)
            {
                error = $"couldn't get the picture ({hr:X8})";
                return null;
            }
            back = t;
            Texture2DDesc d;
            ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)V(back, 10))(back, &d);
            if(d.Width == 0 || d.Height == 0 || d.SampleCount != 1)
            {
                error = "the picture can't be copied (multisampled)";
                return null;
            }
            uint format = d.Format;
            if(format is not (28 or 29 or 87 or 91 or 24))
            {
                error = $"the picture's format isn't one Limber reads ({format})";
                return null;
            }
            var sd = new Texture2DDesc
            {
                Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1,
                Usage = 3 /* staging */, CpuAccessFlags = 0x20000 /* read */,
            };
            nint s = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)V(device, 5))(device, &sd, null, &s);
            if(hr < 0 || s == 0)
            {
                error = $"couldn't make a copy ({hr:X8})";
                return null;
            }
            stage = s;
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)V(context, 47))(context, stage, back);
            Mapped m;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(context, 14))(context, stage, 0, 1 /* read */, 0, &m);
            if(hr < 0 || m.Data == null)
            {
                error = $"couldn't read the copy ({hr:X8})";
                return null;
            }
            int w = (int)d.Width, h = (int)d.Height;
            var rgba = new byte[w * h * 4];
            try
            {
                for(int y = 0; y < h; y++)
                {
                    byte* row = (byte*)m.Data + (long)y * m.RowPitch;
                    int o = y * w * 4;
                    if(format is 28 or 29)
                    {
                        new ReadOnlySpan<byte>(row, w * 4).CopyTo(rgba.AsSpan(o, w * 4));
                        for(int x = 0; x < w; x++)
                            rgba[o + x * 4 + 3] = 255;
                    }
                    else if(format is 87 or 91)
                    {
                        for(int x = 0; x < w; x++)
                        {
                            rgba[o + x * 4] = row[x * 4 + 2];
                            rgba[o + x * 4 + 1] = row[x * 4 + 1];
                            rgba[o + x * 4 + 2] = row[x * 4];
                            rgba[o + x * 4 + 3] = 255;
                        }
                    }
                    else // R10G10B10A2
                    {
                        uint* p = (uint*)row;
                        for(int x = 0; x < w; x++)
                        {
                            uint v = p[x];
                            rgba[o + x * 4] = (byte)((v & 0x3FF) >> 2);
                            rgba[o + x * 4 + 1] = (byte)(((v >> 10) & 0x3FF) >> 2);
                            rgba[o + x * 4 + 2] = (byte)(((v >> 20) & 0x3FF) >> 2);
                            rgba[o + x * 4 + 3] = 255;
                        }
                    }
                }
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(context, 15))(context, stage, 0);
            }
            width = w;
            height = h;
            return rgba;
        }
        catch(Exception e)
        {
            error = e.Message;
            return null;
        }
        finally
        {
            Release(stage);
            Release(back);
        }
    }

    /// <summary>
    /// A part of an RGBA picture (<paramref name="cx"/>, <paramref name="cy"/>: its centre; <paramref name="cw"/> x
    /// <paramref name="ch"/>: its size, anywhere, the outside filled with the edge's pixels) scaled to
    /// <paramref name="ow"/> x <paramref name="oh"/> (bilinear, averaged over several samples when shrinking). RGB out.
    /// </summary>
    public static byte[] CropScale(byte[] rgba, int w, int h, float cx, float cy, float cw, float ch, int ow, int oh)
    {
        var rgb = new byte[ow * oh * 3];
        float sx = cw / ow, sy = ch / oh;
        int taps = Math.Clamp((int)MathF.Ceiling(MathF.Max(sx, sy)), 1, 4);
        float x0 = cx - cw / 2f, y0 = cy - ch / 2f;
        for(int y = 0; y < oh; y++)
            for(int x = 0; x < ow; x++)
            {
                float r = 0, g = 0, b = 0;
                for(int ty = 0; ty < taps; ty++)
                    for(int tx = 0; tx < taps; tx++)
                    {
                        float fx = x0 + (x + (tx + 0.5f) / taps) * sx - 0.5f;
                        float fy = y0 + (y + (ty + 0.5f) / taps) * sy - 0.5f;
                        Sample(rgba, w, h, fx, fy, out float sr, out float sg, out float sb);
                        r += sr;
                        g += sg;
                        b += sb;
                    }
                float n = taps * taps;
                int o = (y * ow + x) * 3;
                rgb[o] = (byte)Math.Clamp(r / n + 0.5f, 0f, 255f);
                rgb[o + 1] = (byte)Math.Clamp(g / n + 0.5f, 0f, 255f);
                rgb[o + 2] = (byte)Math.Clamp(b / n + 0.5f, 0f, 255f);
            }
        return rgb;
    }

    private static void Sample(byte[] p, int w, int h, float x, float y, out float r, out float g, out float b)
    {
        x = Math.Clamp(x, 0f, w - 1f);
        y = Math.Clamp(y, 0f, h - 1f);
        int ix = (int)x, iy = (int)y;
        int jx = Math.Min(ix + 1, w - 1), jy = Math.Min(iy + 1, h - 1);
        float fx = x - ix, fy = y - iy;
        int a = (iy * w + ix) * 4, bb = (iy * w + jx) * 4, c = (jy * w + ix) * 4, d = (jy * w + jx) * 4;
        float L(int k) => (p[a + k] * (1 - fx) + p[bb + k] * fx) * (1 - fy) + (p[c + k] * (1 - fx) + p[d + k] * fx) * fy;
        r = L(0);
        g = L(1);
        b = L(2);
    }

    // ---- PNG (RGB, 8 bits)

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for(uint n = 0; n < 256; n++)
        {
            uint c = n;
            for(int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(byte[] data, int start, int length)
    {
        uint c = 0xFFFFFFFFu;
        for(int i = start; i < start + length; i++)
            c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void Chunk(Stream o, string type, byte[] data)
    {
        var buf = new byte[4 + data.Length];
        for(int i = 0; i < 4; i++)
            buf[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, buf, 4, data.Length);
        WriteBig(o, (uint)data.Length);
        o.Write(buf, 0, buf.Length);
        WriteBig(o, Crc(buf, 0, buf.Length));
    }

    private static void WriteBig(Stream o, uint v)
    {
        o.WriteByte((byte)(v >> 24));
        o.WriteByte((byte)(v >> 16));
        o.WriteByte((byte)(v >> 8));
        o.WriteByte((byte)v);
    }

    public static void SavePng(string path, byte[] rgb, int w, int h)
    {
        using var o = File.Create(path);
        o.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
        ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
        ihdr[8] = 8; // bits
        ihdr[9] = 2; // RGB
        Chunk(o, "IHDR", ihdr);
        using(var raw = new MemoryStream())
        {
            using(var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
            {
                var line = new byte[1 + w * 3];
                for(int y = 0; y < h; y++)
                {
                    line[0] = 0; // no filter
                    Buffer.BlockCopy(rgb, y * w * 3, line, 1, w * 3);
                    z.Write(line, 0, line.Length);
                }
            }
            Chunk(o, "IDAT", raw.ToArray());
        }
        Chunk(o, "IEND", Array.Empty<byte>());
    }
}
