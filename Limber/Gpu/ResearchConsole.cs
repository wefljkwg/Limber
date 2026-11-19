using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Limber.Core;

namespace Limber.Gpu;

/// <summary>
/// A research console Claude drives through two files, so experiments run without a rebuild and without anyone copying
/// text out of the game: Limber reads commands from pluginConfigs/Limber/research/cmd.txt (when its first line,
/// "id N", changes) and writes everything to out.txt (and pictures to PNGs next to it). It only runs while the user has
/// it switched on (on the Camera page, never saved), and every command is written to out.txt before it runs, so a crash
/// shows which one did it.
///
/// Commands (one per line; addresses are expressions like manager+0x3835c or [rtm+0x2c8]+0x38, see Eval):
///   read EXPR LEN            hex dump (with floats and pointers)
///   write EXPR HEXBYTES      write bytes (applied right before the game draws; undo puts them back)
///   writef EXPR F F F...     write floats
///   writei EXPR N            write an int
///   writep EXPR EXPR         write a pointer
///   undo                     every write put back
///   set on|off / set slot N / set update 0|1    the chara-view experiment
///   wait N                   N frames
///   snap NAME EXPR           a Kernel::Texture* read back: stats, and NAME.png
///   snapd3d NAME EXPR        the same for an ID3D11Texture2D*
///   snapslots                every chara-view picture and g-buffer
///   clears                   the next frame's clears
///   views                    the render views list
///   status                   the experiment's state
/// </summary>
public static unsafe class ResearchConsole
{
    public static bool Enabled;
    public static string Last = "";

    private static string Dir => Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "research");
    private static string CmdPath => Path.Combine(Dir, "cmd.txt");
    private static string OutPath => Path.Combine(Dir, "out.txt");

    private static string _lastId = "";
    private static int _poll;
    private static readonly Queue<string> Pending = new();
    private static int _waitFrames;
    private static bool _clearsAsked;
    private static bool _cbAsked;
    private static int _cbWait;

    // Writes: queued here, applied in the game's render function (main thread, before it draws).
    private sealed record Write(nint At, byte[] Bytes);
    private static readonly Queue<Write> WriteQueue = new();
    private static readonly List<(nint At, byte[] Old)> Undo = new();
    private static bool _undoAsked;
    private static readonly object Lock = new();

    // ---- Once a frame (Limber's draw)

    public static void Frame()
    {
        if(!Enabled)
            return;
        try
        {
            ViewExperiment.EnsureRenderHook();
            ViewExperiment.StartCounting();
            if(++_poll >= 20 && Pending.Count == 0)
            {
                _poll = 0;
                Poll();
            }
            if(_cbAsked)
            {
                if(CbProbe.Ready)
                {
                    _cbAsked = false;
                    Out(CbProbe.Result);
                }
                else if(++_cbWait > 300)
                {
                    _cbAsked = false;
                    CbProbe.Asked = false;
                    Out("  no chosen character was drawn (give a character a shader effect first)");
                }
                return;
            }
            if(_clearsAsked && ViewExperiment.LastRecordReady)
            {
                _clearsAsked = false;
                Out(ViewExperiment.TakeRecord());
            }
            if(_waitFrames > 0)
            {
                _waitFrames--;
                return;
            }
            // a few commands a frame (a picture read back counts as the frame's work)
            for(int i = 0; i < 8 && Pending.Count > 0 && _waitFrames == 0 && !_clearsAsked; i++)
            {
                var line = Pending.Dequeue();
                Out($"> {line}");
                bool heavy;
                try
                {
                    heavy = Run(line);
                }
                catch(Exception e)
                {
                    Out($"  error: {e.Message}");
                    heavy = false;
                }
                if(Pending.Count == 0)
                    Out("(done)");
                if(heavy)
                    break;
            }
        }
        catch(Exception e)
        {
            Last = e.Message;
        }
    }

    private static void Poll()
    {
        if(!File.Exists(CmdPath))
        {
            Directory.CreateDirectory(Dir);
            return;
        }
        var lines = File.ReadAllLines(CmdPath);
        if(lines.Length == 0)
            return;
        var id = lines[0].Trim();
        if(id == _lastId || !id.StartsWith("id "))
            return;
        _lastId = id;
        Out($"==== {id} ({DateTime.Now:HH:mm:ss})");
        foreach(var l in lines.Skip(1))
        {
            var t = l.Trim();
            if(t.Length > 0 && !t.StartsWith('#'))
                Pending.Enqueue(t);
        }
        Last = $"running {id} ({Pending.Count} commands)";
    }

    private static void Out(string text)
    {
        Directory.CreateDirectory(Dir);
        using var w = new StreamWriter(OutPath, true, Encoding.UTF8);
        w.WriteLine(text);
        w.Flush();
    }

    // ---- Commands (true: heavy, stop for this frame)

    private static bool Run(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        string Arg(int i) => i < parts.Length ? parts[i] : throw new ArgumentException($"{cmd} needs more arguments");
        switch(cmd)
        {
            case "read":
                Out(Dump(Eval(Arg(1)), (int)Number(Arg(2))));
                return false;
            case "write":
            {
                var hex = string.Concat(parts.Skip(2));
                var bytes = Convert.FromHexString(hex);
                QueueWrite(Eval(Arg(1)), bytes);
                return false;
            }
            case "writef":
            {
                var fs = parts.Skip(2).Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                var bytes = new byte[fs.Length * 4];
                Buffer.BlockCopy(fs, 0, bytes, 0, bytes.Length);
                QueueWrite(Eval(Arg(1)), bytes);
                return false;
            }
            case "writei":
                QueueWrite(Eval(Arg(1)), BitConverter.GetBytes((int)Number(Arg(2))));
                return false;
            case "writep":
                QueueWrite(Eval(Arg(1)), BitConverter.GetBytes((long)Eval(Arg(2))));
                return false;
            case "undo":
                lock(Lock)
                    _undoAsked = true;
                Out("  undo queued");
                return false;
            case "set":
                Set(Arg(1).ToLowerInvariant(), parts.Length > 2 ? parts[2] : "");
                return false;
            case "wait":
                _waitFrames = (int)Number(Arg(1));
                return false;
            case "snap":
            {
                nint tex = Eval(Arg(2));
                if(!SafeMemory.Plausible(tex))
                    throw new ArgumentException("no texture there");
                nint d3d = SafeMemory.ReadPointer(tex + 0x68);
                Out(Snap(Arg(1), d3d));
                return true;
            }
            case "snapd3d":
                Out(Snap(Arg(1), Eval(Arg(2))));
                return true;
            case "snapslots":
                SnapSlots();
                return true;
            case "cbdump":
                CbProbe.Ready = false;
                CbProbe.Asked = true;
                _cbAsked = true;
                _cbWait = 0;
                return true;
            case "clears":
                ViewExperiment.RecordOneFrame();
                _clearsAsked = true;
                return true;
            case "views":
                Limber.Rig.CameraRig.DumpViews();
                Out(Limber.Rig.CameraRig.LastViews);
                return false;
            case "status":
                Out($"  experiment {(ViewExperiment.On ? "on" : "off")}, slot {ViewExperiment.Slot}, update {(ViewExperiment.LetGameUpdateCamera ? 1 : 0)}, preview seen {ViewExperiment.PreviewSeen}; {ViewExperiment.State}");
                Out($"  {ViewExperiment.LoopSaw}");
                Out($"  per frame: {ViewExperiment.TargetClears:0.0} target clears, {ViewExperiment.DepthClears:0.0} depth clears, {ViewExperiment.SlotClears:0.0} of the slot's size, {ViewExperiment.FrameMs:0.0} ms");
                Out($"  live characters: camera {LiveCharacters.LiveCamera}, {LiveCharacters.Targets.Count} characters, {LiveCharacters.LastReplays} draws again last frame, albedo {LiveCharacters.AlbedoIndex} normal {LiveCharacters.NormalIndex}; {LiveCharacters.Error}");
                Out($"  camera buffer patched: {LiveCharacters.LastPatch}");
                Out($"  world pass: {(WorldPass.On ? "on" : "off")}, drawn twice {WorldPass.Doubled} times, captured {WorldPass.Captured} times, main depth clears last frame {WorldPass.LastClears}, boundary 0x{WorldPass.Boundary:X} cleared {WorldPass.LastBoundary} times, capture {WorldPass.CaptureWidth}x{WorldPass.CaptureHeight}; {WorldPass.Error}");
                Out($"  bases: {string.Join(", ", Bases().Select(b => $"{b.Key} 0x{b.Value:X}"))}");
                return false;
            default:
                throw new ArgumentException($"unknown command {cmd}");
        }
    }

    private static void Set(string what, string value)
    {
        switch(what)
        {
            case "on":
                ViewExperiment.On = true;
                break;
            case "off":
                ViewExperiment.On = false;
                break;
            case "slot":
                if(ViewExperiment.On)
                    throw new ArgumentException("the slot can only change with the experiment off");
                ViewExperiment.Slot = Math.Clamp((int)Number(value), 0, 7);
                break;
            case "update":
                ViewExperiment.LetGameUpdateCamera = value != "0";
                break;
            case "world":
                WorldPass.Ensure();
                WorldPass.On = value == "on" || value == "1";
                break;
            case "worldupdate":
                WorldPass.UpdateCamera = value != "0";
                break;
            case "worldsrc":
                WorldPass.SourceOffset = (int)Number(value);
                break;
            case "albedo":
                LiveCharacters.AlbedoIndex = (int)Number(value);
                break;
            case "normal":
                LiveCharacters.NormalIndex = (int)Number(value);
                break;
            case "brightness":
                LiveCharacters.Brightness = float.Parse(value, CultureInfo.InvariantCulture);
                break;
            case "ambient":
                LiveCharacters.Ambient = float.Parse(value, CultureInfo.InvariantCulture);
                break;
            case "retake":
                Limber.Rig.CameraRig.RetakeRoom();
                break;
            case "boundary":
                WorldPass.Boundary = value == "0" ? 0 : Eval(value);
                break;
            case "src0":
            case "src1":
            case "src2":
                WorldPass.Sources[what[3] - '0'] = value == "0" ? 0 : Eval(value);
                break;
            default:
                throw new ArgumentException($"set what? ({what})");
        }
        Out($"  experiment {(ViewExperiment.On ? "on" : "off")}, slot {ViewExperiment.Slot}, update {(ViewExperiment.LetGameUpdateCamera ? 1 : 0)}; world {(WorldPass.On ? "on" : "off")}, worldupdate {(WorldPass.UpdateCamera ? 1 : 0)}, worldsrc 0x{WorldPass.SourceOffset:X}, boundary 0x{WorldPass.Boundary:X}, sources {string.Join(" ", WorldPass.Sources.Select(x => $"0x{x:X}"))}");
    }

    private static void QueueWrite(nint at, byte[] bytes)
    {
        if(!SafeMemory.Plausible(at))
            throw new ArgumentException($"0x{at:X} isn't an address");
        var old = new byte[bytes.Length];
        if(!SafeMemory.Copy(at, old, 0, old.Length))
            throw new ArgumentException($"0x{at:X} can't be read");
        lock(Lock)
            WriteQueue.Enqueue(new Write(at, bytes));
        Out($"  queued {bytes.Length} bytes at 0x{at:X} (was {Convert.ToHexString(old)})");
    }

    /// <summary>In the game's render function, before it draws: the queued writes (and undo).</summary>
    public static void ApplyWrites()
    {
        lock(Lock)
        {
            while(WriteQueue.Count > 0)
            {
                var w = WriteQueue.Dequeue();
                var old = new byte[w.Bytes.Length];
                if(!SafeMemory.Copy(w.At, old, 0, old.Length))
                    continue;
                Undo.Add((w.At, old));
                Marshal.Copy(w.Bytes, 0, w.At, w.Bytes.Length);
            }
            if(_undoAsked)
            {
                _undoAsked = false;
                for(int i = Undo.Count - 1; i >= 0; i--)
                    Marshal.Copy(Undo[i].Old, 0, Undo[i].At, Undo[i].Old.Length);
                Undo.Clear();
            }
        }
    }

    // ---- Addresses: numbers (0x.. hex or decimal), names (see Bases), + and -, and [..] for the pointer stored there

    private static Dictionary<string, nint> Bases()
    {
        var b = new Dictionary<string, nint>();
        var manager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager.Instance();
        b["manager"] = (nint)manager;
        b["views"] = manager == null ? 0 : (nint)manager + 0x8;
        b["rtm"] = (nint)FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
        var cm = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
        b["cm"] = (nint)cm;
        b["maincam"] = cm != null && cm->CurrentCamera != null ? (nint)cm->CurrentCamera->RenderCamera : 0;
        b["scenecam"] = cm != null ? (nint)cm->CurrentCamera : 0;
        b["slotcams"] = ViewExperiment.SlotCamerasObject();
        b["lights"] = ViewExperiment.LightsObject();
        var d = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        b["device"] = (nint)d;
        b["d3d"] = d == null ? 0 : (nint)d->D3D11Forwarder;
        b["ctx"] = d == null ? 0 : (nint)d->D3D11DeviceContext;
        b["cap"] = WorldPass.Captures[0];
        for(int i = 0; i < 4; i++)
            b[$"mrt{i}"] = LiveCharacters.TargetTexture(i);
        b["wdepth"] = LiveCharacters.WorkDepth;
        b["livetex"] = LiveCharacters.LiveTexture;
        b["cap0"] = WorldPass.Captures[0];
        b["cap1"] = WorldPass.Captures[1];
        b["cap2"] = WorldPass.Captures[2];
        b["module"] = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? 0;
        return b;
    }

    private static long Number(string s)
    {
        s = s.Trim();
        bool neg = s.StartsWith('-');
        if(neg)
            s = s[1..];
        long v = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(s[2..], NumberStyles.HexNumber)
            : long.Parse(s, CultureInfo.InvariantCulture);
        return neg ? -v : v;
    }

    /// <summary>An address expression, e.g. [rtm+0x2c8]+0x68 or views+25*0x190 (products of numbers allowed).</summary>
    public static nint Eval(string expr)
    {
        var bases = Bases();
        int i = 0;
        long result = Sum(expr.Replace(" ", ""), ref i, bases);
        if(i != expr.Replace(" ", "").Length)
            throw new ArgumentException($"couldn't read '{expr}' past position {i}");
        return (nint)result;
    }

    private static long Sum(string s, ref int i, Dictionary<string, nint> bases)
    {
        long v = Product(s, ref i, bases);
        while(i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            char op = s[i++];
            long t = Product(s, ref i, bases);
            v = op == '+' ? v + t : v - t;
        }
        return v;
    }

    private static long Product(string s, ref int i, Dictionary<string, nint> bases)
    {
        long v = Atom(s, ref i, bases);
        while(i < s.Length && s[i] == '*')
        {
            i++;
            v *= Atom(s, ref i, bases);
        }
        return v;
    }

    private static long Atom(string s, ref int i, Dictionary<string, nint> bases)
    {
        if(s[i] == '[')
        {
            i++;
            long inner = Sum(s, ref i, bases);
            if(i >= s.Length || s[i] != ']')
                throw new ArgumentException("missing ]");
            i++;
            nint at = (nint)inner;
            if(!SafeMemory.Plausible(at))
                throw new ArgumentException($"[0x{at:X}] isn't an address");
            return SafeMemory.ReadPointer(at);
        }
        int start = i;
        while(i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
            i++;
        var tok = s[start..i];
        if(tok.Length == 0)
            throw new ArgumentException($"expected a number or name at {start}");
        if(char.IsDigit(tok[0]))
            return Number(tok);
        if(bases.TryGetValue(tok.ToLowerInvariant(), out var b))
        {
            if(b == 0)
                throw new ArgumentException($"{tok} isn't there right now");
            return b;
        }
        throw new ArgumentException($"unknown name {tok}");
    }

    // ---- Memory

    private static string Dump(nint at, int len)
    {
        len = Math.Clamp(len, 1, 4096);
        var bytes = new byte[len];
        if(!SafeMemory.Copy(at, bytes, 0, len))
            return $"  0x{at:X}: can't be read";
        nint module = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? 0;
        var sb = new StringBuilder();
        for(int o = 0; o < len; o += 16)
        {
            int n = Math.Min(16, len - o);
            sb.Append($"  +{o:X4} {Convert.ToHexString(bytes, o, n)}");
            if(n == 16)
            {
                sb.Append("  f ");
                for(int k = 0; k < 4; k++)
                    sb.Append(BitConverter.ToSingle(bytes, o + k * 4).ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
                for(int k = 0; k < 2; k++)
                {
                    long p = BitConverter.ToInt64(bytes, o + k * 8);
                    if(module != 0 && p >= module && p < module + 0x4000000)
                        sb.Append($" p{k}=exe+0x{p - module:X}");
                }
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---- Pictures

    private static void SnapSlots()
    {
        var rtm = (nint)FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
        if(rtm == 0)
        {
            Out("  no render target manager");
            return;
        }
        for(int n = 0; n < 8; n++)
            SnapKernel($"slot{n}", SafeMemory.ReadPointer(rtm + 0x2c8 + n * 8));
        for(int g = 0; g < 5; g++)
            SnapKernel($"cgbuf{g}", SafeMemory.ReadPointer(rtm + 0x308 + g * 8));
        for(int g = 0; g < 5; g++)
            SnapKernel($"csemi{g}", SafeMemory.ReadPointer(rtm + 0x378 + g * 8));
    }

    private static void SnapKernel(string name, nint tex)
    {
        if(!SafeMemory.Plausible(tex))
        {
            Out($"  {name}: none");
            return;
        }
        Out(Snap(name, SafeMemory.ReadPointer(tex + 0x68)));
    }

    private static nint V(nint obj, int index) => ((nint*)*(nint*)obj)[index];

    private static void Release(nint obj)
    {
        if(obj != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)V(obj, 2))(obj);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Desc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped
    {
        public void* Data;
        public uint RowPitch, DepthPitch;
    }

    /// <summary>A texture read back (first mip, first slice): per-channel min / max / mean, how much isn't zero, and a PNG
    /// (each channel stretched over its own range so a dim or HDR picture still shows).</summary>
    private static string Snap(string name, nint tex)
    {
        if(!SafeMemory.Plausible(tex))
            return $"  {name}: no texture";
        var kernel = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        nint device = (nint)kernel->D3D11Forwarder, context = (nint)kernel->D3D11DeviceContext;
        Desc d;
        ((delegate* unmanaged[Stdcall]<nint, Desc*, void>)V(tex, 10))(tex, &d);
        if(d.SampleCount != 1)
            return $"  {name}: {d.Width}x{d.Height} format {d.Format} multisampled, skipped";
        var sd = new Desc
        {
            Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format, SampleCount = 1,
            Usage = 3, CpuAccessFlags = 0x20000,
        };
        nint stage = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Desc*, void*, nint*, int>)V(device, 5))(device, &sd, null, &stage);
        if(hr < 0 || stage == 0)
            return $"  {name}: {d.Width}x{d.Height} format {d.Format}: no copy ({hr:X8})";
        try
        {
            // CopySubresourceRegion (46): mip 0 of slice 0 only
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, void*, void>)V(context, 46))(context, stage, 0, 0, 0, 0, tex, 0, null);
            Mapped m;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)V(context, 14))(context, stage, 0, 1, 0, &m);
            if(hr < 0 || m.Data == null)
                return $"  {name}: {d.Width}x{d.Height} format {d.Format}: can't read ({hr:X8})";
            try
            {
                return Measure(name, d, m);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)V(context, 15))(context, stage, 0);
            }
        }
        finally
        {
            Release(stage);
        }
    }

    private static string Measure(string name, Desc d, Mapped m)
    {
        int w = (int)d.Width, h = (int)d.Height;
        var px = new float[w * h * 4];
        bool known = true;
        for(int y = 0; y < h && known; y++)
        {
            byte* row = (byte*)m.Data + (long)y * m.RowPitch;
            for(int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                if(!Decode(d.Format, row, x, px, o))
                {
                    known = false;
                    break;
                }
            }
        }
        if(!known)
        {
            // unknown format: just how many bytes aren't zero
            long nz = 0, total = 0;
            for(int y = 0; y < h; y++)
            {
                byte* row = (byte*)m.Data + (long)y * m.RowPitch;
                for(int k = 0; k < m.RowPitch; k++, total++)
                    if(row[k] != 0)
                        nz++;
            }
            return $"  {name}: {w}x{h} format {d.Format} (not decoded): {100.0 * nz / Math.Max(total, 1):0.0}% of bytes not zero";
        }
        var min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
        var max = new float[] { float.MinValue, float.MinValue, float.MinValue, float.MinValue };
        var sum = new double[4];
        long nonzero = 0;
        for(int i = 0; i < w * h; i++)
        {
            bool any = false;
            for(int c = 0; c < 4; c++)
            {
                float v = px[i * 4 + c];
                if(!float.IsFinite(v))
                    continue;
                min[c] = Math.Min(min[c], v);
                max[c] = Math.Max(max[c], v);
                sum[c] += v;
                if(c < 3 && v != 0)
                    any = true;
            }
            if(any)
                nonzero++;
        }
        var rgb = new byte[w * h * 3];
        for(int i = 0; i < w * h; i++)
            for(int c = 0; c < 3; c++)
            {
                float lo = min[c], hi = max[c];
                float v = px[i * 4 + c];
                rgb[i * 3 + c] = hi > lo && float.IsFinite(v) ? (byte)Math.Clamp((v - lo) / (hi - lo) * 255f, 0f, 255f) : (byte)0;
            }
        var file = Path.Combine(Dir, name + ".png");
        try
        {
            FrameGrab.SavePng(file, rgb, w, h);
        }
        catch
        {
        }
        var sb = new StringBuilder($"  {name}: {w}x{h} format {d.Format}, {100.0 * nonzero / Math.Max(w * h, 1):0.0}% not black; ");
        for(int c = 0; c < 4; c++)
            sb.Append($"{"RGBA"[c]} {min[c]:0.###}..{max[c]:0.###} avg {sum[c] / Math.Max(w * h, 1):0.###}  ");
        sb.Append($"-> {name}.png");
        return sb.ToString();
    }

    private static float Half(ushort h) => (float)BitConverter.UInt16BitsToHalf(h);

    // One pixel of a DXGI format into 4 floats (false: a format this doesn't read).
    private static bool Decode(uint format, byte* row, int x, float[] o, int at)
    {
        switch(format)
        {
            case 28: case 29: case 27: case 30: // R8G8B8A8
                for(int c = 0; c < 4; c++)
                    o[at + c] = row[x * 4 + c] / 255f;
                return true;
            case 87: case 91: case 88: case 92: case 90: // B8G8R8A8 / X8
                o[at] = row[x * 4 + 2] / 255f;
                o[at + 1] = row[x * 4 + 1] / 255f;
                o[at + 2] = row[x * 4] / 255f;
                o[at + 3] = row[x * 4 + 3] / 255f;
                return true;
            case 24: case 25: case 23: // R10G10B10A2
            {
                uint v = ((uint*)row)[x];
                o[at] = (v & 0x3FF) / 1023f;
                o[at + 1] = ((v >> 10) & 0x3FF) / 1023f;
                o[at + 2] = ((v >> 20) & 0x3FF) / 1023f;
                o[at + 3] = (v >> 30) / 3f;
                return true;
            }
            case 10: case 9: // R16G16B16A16_FLOAT
            {
                ushort* p = (ushort*)row + x * 4;
                for(int c = 0; c < 4; c++)
                    o[at + c] = Half(p[c]);
                return true;
            }
            case 11: case 13: // R16G16B16A16 UNORM / SNORM-ish
            {
                ushort* p = (ushort*)row + x * 4;
                for(int c = 0; c < 4; c++)
                    o[at + c] = p[c] / 65535f;
                return true;
            }
            case 2: case 1: // R32G32B32A32_FLOAT
            {
                float* p = (float*)row + x * 4;
                for(int c = 0; c < 4; c++)
                    o[at + c] = p[c];
                return true;
            }
            case 26: // R11G11B10_FLOAT
            {
                uint v = ((uint*)row)[x];
                o[at] = Small(v & 0x7FF, 6);
                o[at + 1] = Small((v >> 11) & 0x7FF, 6);
                o[at + 2] = Small((v >> 22) & 0x3FF, 5);
                o[at + 3] = 1;
                return true;
            }
            case 34: case 33: // R16G16_FLOAT
            {
                ushort* p = (ushort*)row + x * 2;
                o[at] = Half(p[0]);
                o[at + 1] = Half(p[1]);
                o[at + 2] = 0;
                o[at + 3] = 1;
                return true;
            }
            case 35: case 36: // R16G16 UNORM
            {
                ushort* p = (ushort*)row + x * 2;
                o[at] = p[0] / 65535f;
                o[at + 1] = p[1] / 65535f;
                o[at + 2] = 0;
                o[at + 3] = 1;
                return true;
            }
            case 16: case 15: // R32G32_FLOAT
            {
                float* p = (float*)row + x * 2;
                o[at] = p[0];
                o[at + 1] = p[1];
                o[at + 2] = 0;
                o[at + 3] = 1;
                return true;
            }
            case 41: case 39: case 40: // R32 float / typeless / depth
            {
                float v = ((float*)row)[x];
                o[at] = o[at + 1] = o[at + 2] = v;
                o[at + 3] = 1;
                return true;
            }
            case 44: case 45: case 46: // R24G8 (depth in the low 24 bits)
            {
                uint v = ((uint*)row)[x];
                float dv = (v & 0xFFFFFF) / 16777215f;
                o[at] = o[at + 1] = o[at + 2] = dv;
                o[at + 3] = (v >> 24) / 255f;
                return true;
            }
            case 54: case 53: case 55: case 56: // R16 float / typeless / unorm
            {
                ushort v = ((ushort*)row)[x];
                float f = format == 54 ? Half(v) : v / 65535f;
                o[at] = o[at + 1] = o[at + 2] = f;
                o[at + 3] = 1;
                return true;
            }
            case 61: case 60: case 62: case 65: // R8 / A8
            {
                float f = row[x] / 255f;
                o[at] = o[at + 1] = o[at + 2] = f;
                o[at + 3] = 1;
                return true;
            }
            case 49: case 48: case 50: // R8G8
                o[at] = row[x * 2] / 255f;
                o[at + 1] = row[x * 2 + 1] / 255f;
                o[at + 2] = 0;
                o[at + 3] = 1;
                return true;
            default:
                return false;
        }
    }

    // An unsigned small float (R11G11B10): 5-bit exponent, 6 or 5 bit mantissa.
    private static float Small(uint v, int mantissaBits)
    {
        uint e = v >> mantissaBits, mnt = v & ((1u << mantissaBits) - 1);
        if(e == 0)
            return mnt / (float)(1 << mantissaBits) * MathF.Pow(2, -14);
        if(e == 31)
            return float.PositiveInfinity;
        return (1 + mnt / (float)(1 << mantissaBits)) * MathF.Pow(2, (int)e - 15);
    }
}
