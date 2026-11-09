using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace Limber.Gpu;

/// <summary>
/// Experimental: changing a worn model's vertices live on the GPU (what a per-vertex soft body needs), instead of
/// through bones. Step 1 is finding where the game keeps each model's Direct3D vertex buffers (not mapped by
/// ClientStructs): starting from the character's render models, it follows pointers and recognises Direct3D buffers by
/// their class (the same as a buffer Limber makes itself), then matches each to the model file's vertex blocks.
/// Step 2, the wobble test, rewrites one of those buffers every frame from the file's own vertices, the breasts' moved
/// along their normals, to show the game draws what Limber writes.
/// Everything here only touches this game client's own memory and GPU; nothing is sent anywhere.
/// </summary>
public static unsafe class LiveMesh
{
    // ---- Direct3D 11 (by vtable: no extra libraries)

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferDesc
    {
        public uint ByteWidth, Usage, BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SubresourceData
    {
        public void* SysMem;
        public uint Pitch, SlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public void* Data;
        public uint RowPitch, DepthPitch;
    }

    private const uint UsageDefault = 0, UsageImmutable = 1, UsageDynamic = 2;
    private const uint BindVertexBuffer = 1, BindIndexBuffer = 2;

    private static nint Vtable(nint obj, int index) => ((nint*)*(nint*)obj)[index];

    private static void Release(nint obj)
    {
        if (obj != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Vtable(obj, 2))(obj);
    }

    private static nint DeviceHandle()
    {
        var device = Device.Instance();
        return device == null ? 0 : (nint)device->D3D11Forwarder;
    }

    private static nint ContextHandle()
    {
        var device = Device.Instance();
        return device == null ? 0 : (nint)device->D3D11DeviceContext;
    }

    private static nint CreateBuffer(BufferDesc desc, byte[]? initial)
    {
        var device = DeviceHandle();
        if (device == 0)
            return 0;
        nint buffer = 0;
        int hr;
        if (initial != null)
        {
            fixed (byte* p = initial)
            {
                var data = new SubresourceData { SysMem = p };
                hr = ((delegate* unmanaged[Stdcall]<nint, BufferDesc*, SubresourceData*, nint*, int>)Vtable(device, 3))(device, &desc, &data, &buffer);
            }
        }
        else
            hr = ((delegate* unmanaged[Stdcall]<nint, BufferDesc*, SubresourceData*, nint*, int>)Vtable(device, 3))(device, &desc, null, &buffer);
        return hr >= 0 ? buffer : 0;
    }

    private static BufferDesc GetDesc(nint buffer)
    {
        BufferDesc desc;
        ((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void>)Vtable(buffer, 10))(buffer, &desc);
        return desc;
    }

    // The class every Direct3D buffer has (taken from one Limber makes and frees straight away).
    private static nint _bufferClass;

    private static nint BufferClass()
    {
        if (_bufferClass != 0)
            return _bufferClass;
        var buffer = CreateBuffer(new BufferDesc { ByteWidth = 16, Usage = UsageDefault, BindFlags = BindVertexBuffer }, null);
        if (buffer == 0)
            return 0;
        _bufferClass = *(nint*)buffer;
        Release(buffer);
        return _bufferClass;
    }

    // ---- Safe memory reads (only committed, readable pages)

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint address, out MemoryBasicInformation info, nint length);

    /// <summary>
    /// Memory regions already looked up (readable or not), for a search that reads many pointers: most land in the same
    /// few regions, so it asks the system once per region instead of once per pointer (a search used to take long
    /// enough to hitch the frame).
    /// </summary>
    private sealed class Regions
    {
        // (by 64 KB block: the region last found there)
        private readonly Dictionary<long, (nint From, nint To, bool Ok)> _known = new();

        public bool Readable(nint address, int length)
        {
            if (address < 0x10000 || address > 0x7FFFFFFF0000 || (address & 7) != 0)
                return false;
            long block = (long)address >> 16;
            if (_known.TryGetValue(block, out var r) && address >= r.From && address < r.To)
                return r.Ok && address + length <= r.To;
            if (VirtualQuery(address, out var info, sizeof(MemoryBasicInformation)) == 0)
                return false;
            const uint commit = 0x1000;
            uint p = info.Protect & 0xFF;
            bool read = info.State == commit && (p is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80) && (info.Protect & 0x100) == 0;
            nint end = info.BaseAddress + info.RegionSize;
            if (_known.Count < 65536)
                _known[block] = (info.BaseAddress, end, read);
            return read && address + length <= end;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nint size, out nint read);

    /// <summary>
    /// Copies memory that may be freed while it is read (a character still loading: its models are made and thrown
    /// away on the game's other threads). Unlike reading through a pointer, this fails instead of crashing the game.
    /// </summary>
    private static bool Copy(nint address, void* to, int length)
        => ReadProcessMemory(-1, address, to, length, out var read) && read == length;

    private static bool Readable(nint address, int length)
    {
        if (address < 0x10000 || address > 0x7FFFFFFF0000 || (address & 7) != 0)
            return false;
        if (VirtualQuery(address, out var info, sizeof(MemoryBasicInformation)) == 0)
            return false;
        const uint commit = 0x1000;
        uint p = info.Protect & 0xFF;
        bool read = p is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
        if (info.State != commit || !read || (info.Protect & 0x100) != 0)
            return false;
        return address + length <= info.BaseAddress + info.RegionSize;
    }

    // The same, read with Copy (for a model that may be freed meanwhile).
    private static string SafeStdString(nint at)
    {
        ulong* head = stackalloc ulong[4];
        if (!Copy(at, head, 32))
            return "";
        ulong length = head[2], capacity = head[3];
        if (length == 0 || length > 1024)
            return "";
        var bytes = new byte[length];
        fixed (byte* to = bytes)
            if (!Copy(capacity >= 16 ? (nint)head[0] : at, to, (int)length))
                return "";
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    // A std::string (MSVC): its text inline when shorter than 16 bytes, otherwise behind a pointer.
    // (copied, not read through the pointer: the resource holding it may be let go of meanwhile)
    internal static string StdString(nint at) => Limber.Core.SafeMemory.ReadStdString(at, 1024);

    // ---- Finding the buffers

    /// <summary>One Direct3D buffer found under a model: where the pointer to it is kept, and what it is.</summary>
    public sealed record Found(int Slot, string File, string Path, nint Holder, nint Buffer, BufferDesc Desc)
    {
        /// <summary>Every place found that points to this buffer (the game may keep more than one copy of the pointer).</summary>
        public List<nint> Holders { get; } = new();
        public bool IsVertex => (Desc.BindFlags & BindVertexBuffer) != 0;
        public string Usage => Desc.Usage switch { UsageDefault => "default", UsageImmutable => "immutable", UsageDynamic => "dynamic", 3 => "staging", _ => Desc.Usage.ToString() };
        public string Kind => IsVertex ? "vertices" : (Desc.BindFlags & BindIndexBuffer) != 0 ? "indices" : $"bind {Desc.BindFlags}";
    }

    /// <summary>The model files a character is wearing right now (slot, game path or Penumbra's replacement).</summary>
    public static List<(int Slot, string File)> ModelFiles(nint drawObject)
    {
        var list = new List<(int, string)>();
        // (every step copied, not read through the pointers: a character still loading or leaving has its models made
        // and let go of on the game's other threads while this runs)
        if (drawObject == 0)
            return list;
        var cb = (CharacterBase*)drawObject;
        if (!Limber.Core.SafeMemory.TryRead<int>((nint)(&cb->SlotCount), out var slots) || slots <= 0 || slots > 64)
            return list;
        nint models = Limber.Core.SafeMemory.ReadPointer((nint)(&cb->Models));
        if (models == 0)
            return list;
        for (int slot = 0; slot < slots; slot++)
        {
            var model = (Model*)Limber.Core.SafeMemory.ReadPointer(models + slot * 8);
            if (model == null)
                continue;
            var handle = Limber.Core.SafeMemory.ReadPointer((nint)(&model->ModelResourceHandle));
            if (handle == 0)
                continue;
            var file = StdString(handle + 0x48);
            if (file.Length > 0)
                list.Add((slot, file));
        }
        return list;
    }

    private const int ScanBytes = 0x300, MaxDepth = 4, MaxNodes = 6000;

    /// <summary>The Direct3D buffers reachable from the character's worn models (null character: nothing).</summary>
    /// <summary>
    /// Research for exact mesh capture: searches the memory reachable from a character's draw object (its models,
    /// skeleton and what they point to) for given runs of 12 floats (a bone's final skinning matrix, in the layouts
    /// the game might keep it in), and reports where each was found (the pointer path, and the offset in that block).
    /// Read only. Slow (a second or so): for the research button, not every frame.
    /// </summary>
    public static List<string> FindFloats(nint drawObject, IReadOnlyList<(string Label, float[] Values)> targets, float tolerance, int maxHits = 60)
    {
        var report = new List<string>();
        if (drawObject == 0 || !Readable(drawObject, 0x100))
        {
            report.Add("No character model.");
            return report;
        }
        const int Window = 0x4000, Pointers = 0x400, Depth = 4, MaxBlocks = 20000;
        var seen = new HashSet<nint>();
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var cb = (CharacterBase*)drawObject;
        // (everything copied before it's looked at, not read through the pointers: this walks memory it doesn't know,
        // which the game may let go of on its other threads while the search runs)
        int slotCount = Limber.Core.SafeMemory.Read<int>((nint)(&cb->SlotCount));
        nint models = Limber.Core.SafeMemory.ReadPointer((nint)(&cb->Models));
        nint* head = stackalloc nint[6];
        // The models first (and what they point to), then the rest of the character.
        if (slotCount > 0 && slotCount <= 64 && models != 0)
            for (int slot = 0; slot < slotCount; slot++)
            {
                nint model = Limber.Core.SafeMemory.ReadPointer(models + slot * 8);
                if (model == 0)
                    continue;
                queue.Enqueue((model, $"Model[{slot}]", 0));
                // Its bone list: the constant buffer(s) the game hands the graphics card with the bone matrices.
                if (!Limber.Core.SafeMemory.TryRead<nint>(model + 0x58, out var list) || !Limber.Core.SafeMemory.TryRead<int>(model + 0x60, out var count))
                    continue;
                report.Add($"Model[{slot}]: bone list 0x{list:X}, count {count}");
                // Raw look at the list (what each entry holds), and every buffer object found inside it.
                var buffers = new List<nint>();
                int bytes = Math.Clamp(count, 1, 16) * 0x40;
                var q = new nint[bytes / 8];
                bool listRead;
                fixed (nint* to = q)
                    listRead = Limber.Core.SafeMemory.Copy(list, to, bytes);
                if (!listRead)
                    continue;
                if (slot <= 2)
                {
                    var raw = new System.Text.StringBuilder("  raw:");
                    for (int i = 0; i < Math.Min(bytes / 8, 24); i++)
                        raw.Append($" {(ulong)q[i]:X}");
                    report.Add(raw.ToString());
                }
                for (int i = 0; i < bytes / 8; i++)
                {
                    nint value = q[i];
                    if (value == 0 || (value & 7) != 0 || value > 0x7FF000000000)
                        continue;
                    // A constant buffer: a code address first, a plausible size at +0x20.
                    if (!Limber.Core.SafeMemory.Copy(value, head, 0x30))
                        continue;
                    int size = *(int*)((byte*)head + 0x20);
                    if (head[0] > 0x7FF000000000 && size > 0 && size <= 0x40000 && size % 16 == 0)
                        buffers.Add(value);
                }
                foreach (var buffer in buffers)
                {
                    if (!Limber.Core.SafeMemory.TryRead<ConstantBuffer>(buffer, out var cbuffer))
                        continue;
                    nint source = (nint)cbuffer.UnsafeSourcePointer;
                    report.Add($"  buffer 0x{buffer:X}: {cbuffer.ByteSize} bytes, flags 0x{cbuffer.Flags:X}, data 0x{source:X}");
                    if (source == 0 || cbuffer.ByteSize < 48 || cbuffer.ByteSize > 0x40000)
                        continue;
                    // The whole buffer copied, then searched (offset / 48 = which bone of this model it is).
                    int n = cbuffer.ByteSize / 4;
                    var f = new float[n];
                    bool read;
                    fixed (float* to = f)
                        read = Limber.Core.SafeMemory.Copy(source, to, n * 4);
                    if (!read)
                        continue;
                    report.Add($"    first: {f[0]:0.###} {f[1]:0.###} {f[2]:0.###} {f[3]:0.###} | {f[4]:0.###} {f[5]:0.###} {f[6]:0.###} {f[7]:0.###} | {f[8]:0.###} {f[9]:0.###} {f[10]:0.###} {f[11]:0.###}");
                    foreach (var (label, values) in targets)
                        for (int i = 0; i + 12 <= n; i++)
                        {
                            bool match = true;
                            for (int k = 0; k < 12 && match; k++)
                                match = float.IsNaN(values[k]) || MathF.Abs(f[i + k] - values[k]) <= tolerance;
                            if (match)
                                report.Add($"    MATCH {label} at +0x{i * 4:X} (bone {i / 12}, {i % 12} floats in)");
                        }
                }
            }
        queue.Enqueue((drawObject, "CharacterBase", 0));
        int blocks = 0, hits = 0;
        var block = new byte[Window];
        while (queue.Count > 0 && blocks++ < MaxBlocks && hits < maxHits)
        {
            var (at, path, depth) = queue.Dequeue();
            if (!seen.Add(at) || !Readable(at, 16))
                continue;
            // How much of this block can be read (up to the window), copied before it's searched.
            int length = 16;
            while (length < Window && Readable(at, length * 2))
                length *= 2;
            if (!Limber.Core.SafeMemory.Copy(at, block, 0, length))
                continue;
            fixed (byte* copied = block)
            {
                var floats = (float*)copied;
                int count = length / 4;
                foreach (var (label, values) in targets)
                {
                    if (values.Length != 12)
                        continue;
                    for (int i = 0; i + 12 <= count && hits < maxHits; i++)
                    {
                        // (a NaN in the pattern matches anything: a part that may be measured from somewhere else)
                        if (!float.IsNaN(values[0]) && MathF.Abs(floats[i] - values[0]) > tolerance)
                            continue;
                        bool match = true;
                        for (int k = 1; k < 12 && match; k++)
                            match = float.IsNaN(values[k]) || MathF.Abs(floats[i + k] - values[k]) <= tolerance;
                        if (match && float.IsNaN(values[0]) && !float.IsFinite(floats[i]))
                            match = false;
                        if (!match)
                            continue;
                        report.Add($"{label}: {path} +0x{i * 4:X} (block 0x{at:X}, depth {depth})");
                        hits++;
                    }
                }
                if (depth >= Depth)
                    continue;
                int scan = Math.Min(length, Pointers);
                for (int off = 0; off + 8 <= scan; off += 8)
                {
                    nint value = *(nint*)(copied + off);
                    if (value == 0 || (value & 7) != 0 || seen.Contains(value) || !Readable(value, 16))
                        continue;
                    queue.Enqueue((value, $"{path}+0x{off:X}", depth + 1));
                }
            }
        }
        report.Add($"Searched {blocks} memory blocks, {hits} match(es).");
        return report;
    }

    public static List<Found> Probe(nint drawObject, List<string> log, Func<string, bool>? onlyFiles = null)
    {
        var cls = BufferClass();
        if (cls == 0)
        {
            log.Add("Couldn't make a test buffer (no Direct3D device?).");
            return new List<Found>();
        }
        return ProbeCore(drawObject, log, onlyFiles, cls, describe: true);
    }

    // (perf) The search itself, safe to run on a worker thread when describe is false: every read of the game's memory
    // goes through Copy (a character freed meanwhile makes it fail, never crash), and no Direct3D call is made on the
    // buffers found (their descriptions are read later, on the render thread, by Search.Take). The search reads
    // thousands of blocks one system call at a time and took 100+ ms: on the render thread that was a visible hitch,
    // repeated every few seconds while a model wasn't found.
    private static List<Found> ProbeCore(nint drawObject, List<string> log, Func<string, bool>? onlyFiles, nint cls, bool describe)
    {
        var found = new List<Found>();
        if (drawObject == 0 || !Readable(drawObject, 0x100))
        {
            log.Add("No character model.");
            return found;
        }
        var cb = (CharacterBase*)drawObject;
        int slots = 0;
        FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model** models = null;
        if (!Copy((nint)(&cb->SlotCount), &slots, sizeof(int)) || !Copy((nint)(&cb->Models), &models, 8))
            slots = 0;
        if (slots <= 0 || slots > 64 || models == null)
        {
            log.Add("The character has no model slots.");
            return found;
        }
        var regions = new Regions();
        nint* chunk = stackalloc nint[ScanBytes / 8];
        for (int slot = 0; slot < slots; slot++)
        {
            FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model* model;
            if (!Copy((nint)(models + slot), &model, 8) || model == null || !Readable((nint)model, 0x180))
                continue;
            FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.ModelResourceHandle* handle;
            if (!Copy((nint)(&model->ModelResourceHandle), &handle, 8))
                continue;
            string file = handle != null && Readable((nint)handle, 0x2A0) ? SafeStdString((nint)handle + 0x48) : "?";
            if (onlyFiles != null && !onlyFiles(file))
                continue; // (searching a model's memory is slow: only the ones asked for)
            int before = found.Count;
            var seen = new HashSet<nint>();
            var queue = new Queue<(nint Address, string Path, int Depth)>();
            queue.Enqueue(((nint)model, "Model", 0));
            if (handle != null)
                queue.Enqueue(((nint)handle, "ModelResource", 0));
            int nodes = 0;
            while (queue.Count > 0 && nodes++ < MaxNodes)
            {
                var (at, path, depth) = queue.Dequeue();
                if (!seen.Add(at) || !regions.Readable(at, ScanBytes) || !Copy(at, chunk, ScanBytes))
                    continue;
                for (int off = 0; off < ScanBytes; off += 8)
                {
                    nint value = chunk[off / 8];
                    if (value == 0 || seen.Contains(value) || !regions.Readable(value, 16))
                        continue;
                    nint head;
                    if (!Copy(value, &head, 8))
                        continue;
                    if (head == cls)
                    {
                        var known = found.FirstOrDefault(f => f.Buffer == value);
                        if (known != null)
                        {
                            if (!known.Holders.Contains(at + off))
                                known.Holders.Add(at + off);
                            continue;
                        }
                        var desc = describe ? GetDesc(value) : default;
                        var entry = new Found(slot, file, $"{path}+0x{off:X}", at + off, value, desc);
                        entry.Holders.Add(at + off);
                        found.Add(entry);
                    }
                    else if (depth < MaxDepth)
                        queue.Enqueue((value, $"{path}+0x{off:X}", depth + 1));
                }
            }
            log.Add($"Slot {slot}: {Path.GetFileName(file.Split('|').Last())}: {found.Count - before} buffer(s).");
        }
        return found;
    }

    /// <summary>
    /// The Direct3D buffers reachable from any objects (a placed prop, its model resource...), found the same way as a
    /// character's: by following pointers a few steps and recognising buffers by their class.
    /// </summary>
    public static List<Found> ProbeFrom(nint[] roots, string file)
    {
        var cls = BufferClass();
        if (cls == 0)
            return new List<Found>();
        return ProbeFromCore(roots, file, cls, describe: true);
    }

    // (perf) See ProbeCore: worker-thread safe when describe is false.
    private static List<Found> ProbeFromCore(nint[] roots, string file, nint cls, bool describe)
    {
        var found = new List<Found>();
        var regions = new Regions();
        nint* chunk = stackalloc nint[ScanBytes / 8];
        var seen = new HashSet<nint>();
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        foreach (var root in roots)
            if (root != 0)
                queue.Enqueue((root, "Root", 0));
        int nodes = 0;
        while (queue.Count > 0 && nodes++ < MaxNodes)
        {
            var (at, path, depth) = queue.Dequeue();
            if (!seen.Add(at) || !regions.Readable(at, ScanBytes) || !Copy(at, chunk, ScanBytes))
                continue;
            for (int off = 0; off < ScanBytes; off += 8)
            {
                nint value = chunk[off / 8];
                if (value == 0 || seen.Contains(value) || !regions.Readable(value, 16))
                    continue;
                nint head;
                if (!Copy(value, &head, 8))
                    continue;
                if (head == cls)
                {
                    var known = found.FirstOrDefault(f => f.Buffer == value);
                    if (known != null)
                    {
                        if (!known.Holders.Contains(at + off))
                            known.Holders.Add(at + off);
                        continue;
                    }
                    var entry = new Found(0, file, $"{path}+0x{off:X}", at + off, value, describe ? GetDesc(value) : default);
                    entry.Holders.Add(at + off);
                    found.Add(entry);
                }
                else if (depth < MaxDepth)
                    queue.Enqueue((value, $"{path}+0x{off:X}", depth + 1));
            }
        }
        return found;
    }

    // ---- Searching on a worker thread

    /// <summary>
    /// A search for a model's buffers running on a worker thread (perf: the search took long enough to hitch the frame,
    /// and was repeated every few seconds while a model wasn't found). Start it with <see cref="ProbeInBackground"/> or
    /// <see cref="ProbeFromInBackground"/>; once <see cref="Done"/>, <see cref="Take"/> on the render thread gives what
    /// <see cref="Probe"/> / <see cref="ProbeFrom"/> would have.
    /// </summary>
    public sealed class Search
    {
        private readonly System.Threading.Tasks.Task<List<Found>> _task;
        private readonly nint _owner, _draw, _class;

        internal Search(System.Threading.Tasks.Task<List<Found>> task, nint owner = 0, nint draw = 0, nint cls = 0)
        {
            _task = task;
            _owner = owner;
            _draw = draw;
            _class = cls;
        }

        /// <summary>The character the search was started for still has the same model (not redrawn meanwhile: a
        /// redraw frees the buffers found, and freed memory can still hold the old pointers).</summary>
        public bool StillDrawn => _owner == 0 || Limber.Core.SafeMemory.ReadPointer(_owner + 0x100) == _draw;

        public bool Done => _task.IsCompleted;

        /// <summary>Render thread, once done: the buffers found, each described now; only those still where they were
        /// found (one let go of meanwhile is left out rather than asked about). Empty if the search failed.</summary>
        public List<Found> Take()
        {
            var list = new List<Found>();
            if (!_task.IsCompletedSuccessfully || !StillDrawn)
                return list;
            foreach (var f in _task.Result)
            {
                if (!Limber.Core.SafeMemory.TryRead<nint>(f.Holder, out var held) || held != f.Buffer)
                    continue;
                // (still a buffer object: asked about only then, never through a pointer to something else)
                if (_class != 0 && (!Limber.Core.SafeMemory.TryRead<nint>(f.Buffer, out var vt) || vt != _class))
                    continue;
                list.Add(f with { Desc = GetDesc(f.Buffer) }); // (keeps the same Holders list)
            }
            return list;
        }
    }

    /// <summary>Starts <see cref="Probe"/> for one model file on a worker thread (render thread; null if it can't start).</summary>
    public static Search? ProbeInBackground(nint drawObject, string file, nint owner = 0) => ProbeInBackground(drawObject, f => f == file, owner);

    /// <summary>Starts <see cref="Probe"/> for the model files picked on a worker thread (render thread; null if it can't
    /// start). <paramref name="onlyFiles"/> is called on the worker thread.</summary>
    public static Search? ProbeInBackground(nint drawObject, Func<string, bool> onlyFiles, nint owner = 0)
    {
        var cls = BufferClass();
        if (cls == 0 || drawObject == 0)
            return null;
        return new Search(OnOwnThread(() => ProbeCore(drawObject, new List<string>(), onlyFiles, cls, describe: false)), owner, drawObject, cls);
    }

    /// <summary>Starts <see cref="ProbeFrom"/> on a worker thread (render thread; null if it can't start).</summary>
    public static Search? ProbeFromInBackground(nint[] roots, string file)
    {
        var cls = BufferClass();
        if (cls == 0)
            return null;
        return new Search(OnOwnThread(() => ProbeFromCore(roots, file, cls, describe: false)), 0, 0, cls);
    }

    // (a thread of its own: a search takes a while, and shouldn't hold up the shared worker threads others wait on)
    private static System.Threading.Tasks.Task<List<Found>> OnOwnThread(Func<List<Found>> search)
        => System.Threading.Tasks.Task.Factory.StartNew(search, System.Threading.CancellationToken.None,
            System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);

    // ---- Shared by the wobble test and the live soft body

    /// <summary>The pointer the game reads is still <paramref name="buffer"/> (not freed or replaced by a reload).</summary>
    public static bool StillHolds(Found target, nint buffer) => Readable(target.Holder, 8) && *(nint*)target.Holder == buffer;

    /// <summary>An identical but writable copy of an immutable buffer, put where the game reads it. 0 if that failed.</summary>
    public static nint Replace(Found target, byte[] contents)
    {
        if (!StillHolds(target, target.Buffer))
            return 0;
        var desc = target.Desc;
        desc.Usage = UsageDefault;
        desc.CPUAccessFlags = 0;
        var copy = CreateBuffer(desc, contents);
        if (copy == 0)
            return 0;
        // Every copy of the pointer (otherwise the game draws ours one frame and its own the next: flicker).
        foreach (var h in target.Holders.Append(target.Holder).Distinct())
            if (Readable(h, 8) && *(nint*)h == target.Buffer)
                *(nint*)h = copy;
        return copy;
    }

    /// <summary>The game's own buffer back in place of a copy from <see cref="Replace"/> (if the copy is still in use).</summary>
    public static void Restore(Found target, nint copy)
    {
        if (StillHolds(target, copy))
        {
            foreach (var h in target.Holders.Append(target.Holder).Distinct())
                if (Readable(h, 8) && *(nint*)h == copy)
                    *(nint*)h = target.Buffer;
            Release(copy);
        }
    }

    /// <summary>Write a whole buffer (render thread).</summary>
    public static void Upload(nint buffer, BufferDesc desc, byte[] data)
    {
        if (desc.Usage == UsageImmutable)
            desc.Usage = UsageDefault; // a copy made by Replace
        Write(buffer, desc, data);
    }

    /// <summary>A new writable buffer like the found one, holding <paramref name="contents"/> (0 if it couldn't be made).
    /// Nothing points to it until someone is given it; free it with <see cref="Free"/>.</summary>
    public static nint CreateCopy(BufferDesc desc, byte[] contents)
    {
        desc.Usage = UsageDefault;
        desc.CPUAccessFlags = 0;
        return CreateBuffer(desc, contents);
    }

    /// <summary>Frees a buffer made by <see cref="CreateCopy"/>.</summary>
    public static void Free(nint buffer) => Release(buffer);

    /// <summary>The bytes are committed, readable memory (any thread).</summary>
    public static bool IsReadable(nint address, int length) => Readable(address, length);

    /// <summary>Write only bytes <paramref name="from"/> to <paramref name="to"/> of a buffer (render thread): less to
    /// send each frame when only part of a model moves. A dynamic buffer is always written whole.</summary>
    public static void UploadRange(nint buffer, BufferDesc desc, byte[] data, int from, int to)
    {
        if (desc.Usage == UsageImmutable)
            desc.Usage = UsageDefault; // a copy made by Replace
        int length = Math.Min(data.Length, (int)desc.ByteWidth);
        from = Math.Clamp(from, 0, length);
        to = Math.Clamp(to, 0, length);
        if (desc.Usage == UsageDynamic || (from == 0 && to == length))
        {
            Write(buffer, desc, data);
            return;
        }
        if (to <= from)
            return;
        var context = ContextHandle();
        if (context == 0)
            return;
        // D3D11_BOX: left, top, front, right, bottom, back (a buffer's bytes are its x).
        uint* box = stackalloc uint[6];
        box[0] = (uint)from;
        box[1] = 0;
        box[2] = 0;
        box[3] = (uint)to;
        box[4] = 1;
        box[5] = 1;
        fixed (byte* p = data)
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void*, void*, uint, uint, void>)Vtable(context, 48))(context, buffer, 0, box, p + from, 0, 0);
    }

    // ---- The wobble test

    private sealed class Wobble
    {
        public required Found Target;
        public required byte[] Original;       // the buffer's contents (the file's vertex block for that LoD)
        public required List<Limber.BodyRig.MdlRigger.MeshLayout> Meshes;
        public required Dictionary<int, float[]> Mask;  // per mesh: how much each vertex is breast (0..1)
        public nint Replacement;               // our own buffer swapped in (immutable originals), or 0
        public byte[] Work = [];
        public long Start;
    }

    private static Wobble? _wobble;
    public static bool Wobbling => _wobble != null;
    public static string? LastError;

    /// <summary>
    /// Start the wobble test on one vertex buffer: matched to the model file's vertex block for one LoD by size, it's
    /// rewritten every frame with every vertex moved back and forth along its normal.
    /// </summary>
    public static string StartWobble(Found target, Func<string, byte[]?> readFile, Func<string, bool> onlyBones)
    {
        StopWobble();
        if (!target.IsVertex)
            return "That's not a vertex buffer.";
        var data = readFile(target.File);
        if (data == null)
            return "Couldn't read the model file.";
        var (offset, size, meshes) = Limber.BodyRig.MdlRigger.Layout(data);
        int lod = Array.FindIndex(size, s => s == target.Desc.ByteWidth);
        if (lod < 0)
            return $"No vertex block in the file is {target.Desc.ByteWidth} bytes (blocks: {string.Join(", ", size)}); the game may split it per mesh.";
        if (offset[lod] + size[lod] > data.Length)
            return "The file is shorter than its header says.";
        var original = data.AsSpan((int)offset[lod], (int)size[lod]).ToArray();
        var mask = Limber.BodyRig.MdlRigger.PartWeights(data, lod, onlyBones);
        if (!mask.Values.Any(m => m.Any(x => x > 0.05f)))
            return "No breast vertices in this model (pick the body / top model's vertex buffer).";
        var w = new Wobble
        {
            Target = target,
            Original = original,
            Meshes = meshes.Where(x => x.Lod == lod && x.NormalAt >= 0 && mask.ContainsKey(x.Mesh)).ToList(),
            Mask = mask,
            Work = (byte[])original.Clone(),
            Start = Environment.TickCount64,
        };
        if (target.Desc.Usage == UsageImmutable)
        {
            // Can't be written: put an identical, writable buffer in its place (the original is put back on stop).
            var desc = target.Desc;
            desc.Usage = UsageDefault;
            desc.CPUAccessFlags = 0;
            w.Replacement = CreateBuffer(desc, original);
            if (w.Replacement == 0)
                return "Couldn't make a writable copy of the buffer.";
            if (*(nint*)target.Holder != target.Buffer)
            {
                Release(w.Replacement);
                return "The model changed since probing; probe again.";
            }
            foreach (var h in target.Holders.Append(target.Holder).Distinct())
                if (Readable(h, 8) && *(nint*)h == target.Buffer)
                    *(nint*)h = w.Replacement;
        }
        _wobble = w;
        return $"Wobbling LoD {lod} of slot {target.Slot} ({w.Meshes.Count} mesh(es), {target.Usage} buffer{(w.Replacement != 0 ? ", swapped for a writable copy" : "")}).";
    }

    public static void StopWobble()
    {
        var w = _wobble;
        _wobble = null;
        if (w == null)
            return;
        try
        {
            if (w.Replacement != 0)
            {
                // Put the original back if ours is still the one in use (if the model was unloaded, the game freed
                // ours itself).
                if (Readable(w.Target.Holder, 8) && *(nint*)w.Target.Holder == w.Replacement)
                {
                    foreach (var h in w.Target.Holders.Append(w.Target.Holder).Distinct())
                        if (Readable(h, 8) && *(nint*)h == w.Replacement)
                            *(nint*)h = w.Target.Buffer;
                    Release(w.Replacement);
                }
            }
            else if (Readable(w.Target.Holder, 8) && *(nint*)w.Target.Holder == w.Target.Buffer)
                Write(w.Target.Buffer, w.Target.Desc, w.Original);
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
    }

    /// <summary>Every frame, on the render thread (UiBuilder.Draw).</summary>
    public static void Update()
    {
        var w = _wobble;
        if (w == null)
            return;
        try
        {
            var buffer = w.Replacement != 0 ? w.Replacement : w.Target.Buffer;
            // Still there? (A redraw or gear change frees the model's buffers.)
            if (!Readable(w.Target.Holder, 8) || *(nint*)w.Target.Holder != buffer)
            {
                _wobble = null;
                LastError = "The model was reloaded; the test stopped.";
                return;
            }
            float t = (Environment.TickCount64 - w.Start) / 1000f;
            float amount = 0.01f * MathF.Sin(t * 2f * MathF.PI * 0.7f); // 1 cm, a bit under once a second
            Buffer.BlockCopy(w.Original, 0, w.Work, 0, w.Original.Length);
            foreach (var mesh in w.Meshes)
            {
                var mask = w.Mask[mesh.Mesh];
                for (int v = 0; v < mesh.VertexCount && v < mask.Length; v++)
                {
                    if (mask[v] <= 0f)
                        continue;
                    int pa = mesh.PositionAt + v * mesh.PositionStride, na = mesh.NormalAt + v * mesh.NormalStride;
                    if (pa + 12 > w.Work.Length || na + 12 > w.Work.Length)
                        break;
                    var p = Limber.BodyRig.MdlRigger.ReadVector(w.Original, pa, mesh.PositionType);
                    var n = Limber.BodyRig.MdlRigger.ReadVector(w.Original, na, mesh.NormalType);
                    // A ripple over the surface, so it can't be mistaken for scaling.
                    float wave = MathF.Sin(p.Y * 40f + t * 4f);
                    Limber.BodyRig.MdlRigger.WriteVector(w.Work, pa, mesh.PositionType, p + n * (amount * wave * mask[v]));
                }
            }
            Write(buffer, w.Target.Desc, w.Work);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            _wobble = null;
        }
    }

    private static void Write(nint buffer, BufferDesc desc, byte[] data)
    {
        var context = ContextHandle();
        if (context == 0)
            return;
        fixed (byte* p = data)
        {
            if (desc.Usage == UsageDynamic)
            {
                MappedSubresource mapped;
                int hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)Vtable(context, 14))(context, buffer, 0, 4 /* write discard */, 0, &mapped);
                if (hr < 0)
                    return;
                Buffer.MemoryCopy(p, mapped.Data, desc.ByteWidth, Math.Min(data.Length, (int)desc.ByteWidth));
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Vtable(context, 15))(context, buffer, 0);
            }
            else
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void*, void*, uint, uint, void>)Vtable(context, 48))(context, buffer, 0, null, p, 0, 0);
        }
    }
}
