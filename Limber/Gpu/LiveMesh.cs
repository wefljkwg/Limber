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

    // A std::string (MSVC): its text inline when shorter than 16 bytes, otherwise behind a pointer.
    private static string StdString(nint at)
    {
        ulong length = *(ulong*)(at + 0x10), capacity = *(ulong*)(at + 0x18);
        if (length == 0 || length > 1024)
            return "";
        byte* text = capacity >= 16 ? *(byte**)at : (byte*)at;
        if (capacity >= 16 && !Readable((nint)text & ~7, (int)length + 8))
            return "";
        return System.Text.Encoding.UTF8.GetString(text, (int)length);
    }

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

    private const int ScanBytes = 0x300, MaxDepth = 4, MaxNodes = 6000;

    /// <summary>The Direct3D buffers reachable from the character's worn models (null character: nothing).</summary>
    public static List<Found> Probe(nint drawObject, List<string> log)
    {
        var found = new List<Found>();
        var cls = BufferClass();
        if (cls == 0)
        {
            log.Add("Couldn't make a test buffer (no Direct3D device?).");
            return found;
        }
        if (drawObject == 0 || !Readable(drawObject, 0x100))
        {
            log.Add("No character model.");
            return found;
        }
        var cb = (CharacterBase*)drawObject;
        int slots = cb->SlotCount;
        if (slots <= 0 || slots > 64 || cb->Models == null)
        {
            log.Add("The character has no model slots.");
            return found;
        }
        for (int slot = 0; slot < slots; slot++)
        {
            var model = cb->Models[slot];
            if (model == null || !Readable((nint)model, 0x180))
                continue;
            var handle = model->ModelResourceHandle;
            string file = handle != null && Readable((nint)handle, 0x2A0) ? StdString((nint)handle + 0x48) : "?";
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
                if (!seen.Add(at) || !Readable(at, ScanBytes))
                    continue;
                for (int off = 0; off < ScanBytes; off += 8)
                {
                    nint value = *(nint*)(at + off);
                    if (value == 0 || seen.Contains(value) || !Readable(value, 16))
                        continue;
                    if (*(nint*)value == cls)
                    {
                        var known = found.FirstOrDefault(f => f.Buffer == value);
                        if (known != null)
                        {
                            if (!known.Holders.Contains(at + off))
                                known.Holders.Add(at + off);
                            continue;
                        }
                        var desc = GetDesc(value);
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
