using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Hooking;

namespace Limber.Gpu;

/// <summary>
/// Research for exact mesh capture: for a few frames, looks at every block of data the game writes into a
/// graphics buffer (Direct3D's Map / Unmap and UpdateSubresource on the game's own device) and reports where given
/// runs of 12 floats (a bone's expected final matrix, in the layouts it might be in) turn up: which buffer, how big,
/// and where in it. Read only: the game's writes go through unchanged. Hooked only while a search runs.
/// </summary>
public static unsafe class SkinCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped
    {
        public void* Data;
        public uint RowPitch, DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc
    {
        public uint ByteWidth, Usage, BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride;
    }

    private delegate int MapDelegate(nint context, nint resource, uint subresource, uint type, uint flags, Mapped* mapped);
    private delegate void UnmapDelegate(nint context, nint resource, uint subresource);
    private delegate void UpdateDelegate(nint context, nint resource, uint subresource, void* box, void* source, uint rowPitch, uint depthPitch);

    private static Hook<MapDelegate>? _map;
    private static Hook<UnmapDelegate>? _unmap;
    private static Hook<UpdateDelegate>? _update;

    private static readonly Dictionary<nint, (nint Data, uint Size, uint Bind)> Open = new();
    private static (string Label, float[] Values)[] _targets = [];
    private static float _tolerance = 0.002f;
    private static int _framesLeft;
    private static int _writes;
    private static readonly List<string> Found = new();

    /// <summary>The finished report (null while a search runs or before one).</summary>
    public static volatile List<string>? Result;
    public static bool Searching => _framesLeft > 0;

    private static nint Vtable(nint obj, int index) => ((nint*)*(nint*)obj)[index];

    /// <summary>Starts a search over the next few frames (render thread).</summary>
    public static string Start(IReadOnlyList<(string Label, float[] Values)> targets, int frames = 3)
    {
        var device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        nint context = device == null ? 0 : (nint)device->D3D11DeviceContext;
        if(context == 0)
            return "No graphics device.";
        try
        {
            _map ??= Plugin.GameInterop.HookFromAddress<MapDelegate>(Vtable(context, 14), MapDetour);
            _unmap ??= Plugin.GameInterop.HookFromAddress<UnmapDelegate>(Vtable(context, 15), UnmapDetour);
            _update ??= Plugin.GameInterop.HookFromAddress<UpdateDelegate>(Vtable(context, 48), UpdateDetour);
        }
        catch(Exception e)
        {
            return "Couldn't watch the graphics writes: " + e.Message;
        }
        lock(Found)
        {
            Found.Clear();
            Open.Clear();
            _targets = [.. targets];
            _writes = 0;
            _framesLeft = frames;
            Result = null;
        }
        _map.Enable();
        _unmap.Enable();
        _update.Enable();
        return "Watching the next frames...";
    }

    /// <summary>Once a frame (render thread): finishes the search after its frames.</summary>
    public static void Tick()
    {
        if(_framesLeft <= 0)
            return;
        if(--_framesLeft > 0)
            return;
        _map?.Disable();
        _unmap?.Disable();
        _update?.Disable();
        lock(Found)
        {
            var report = new List<string> { $"Watched {_writes} graphics writes." };
            report.AddRange(Found);
            if(Found.Count == 0)
                report.Add("No matches.");
            Result = report;
        }
    }

    public static void Dispose()
    {
        _map?.Dispose();
        _unmap?.Dispose();
        _update?.Dispose();
        _map = null;
        _unmap = null;
        _update = null;
    }

    private static bool BufferInfo(nint resource, out BufferDesc desc)
    {
        desc = default;
        if(resource == 0)
            return false;
        uint dimension = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint*, void>)Vtable(resource, 7))(resource, &dimension);
        if(dimension != 1) // a buffer (not a texture)
            return false;
        BufferDesc d;
        ((delegate* unmanaged[Stdcall]<nint, BufferDesc*, void>)Vtable(resource, 10))(resource, &d);
        desc = d;
        return true;
    }

    private static int MapDetour(nint context, nint resource, uint subresource, uint type, uint flags, Mapped* mapped)
    {
        int hr = _map!.Original(context, resource, subresource, type, flags, mapped);
        try
        {
            // Written by the processor (write, or write-discard / no-overwrite): watch what it puts there.
            if(hr >= 0 && _framesLeft > 0 && mapped != null && mapped->Data != null && type is 2 or 4 or 5 && BufferInfo(resource, out var desc))
                lock(Found)
                    Open[resource] = ((nint)mapped->Data, desc.ByteWidth, desc.BindFlags);
        }
        catch
        {
            // never break the game's drawing
        }
        return hr;
    }

    private static void UnmapDetour(nint context, nint resource, uint subresource)
    {
        try
        {
            if(_framesLeft > 0)
                lock(Found)
                    if(Open.Remove(resource, out var open))
                        Scan("Map", resource, (float*)open.Data, open.Size, open.Bind);
        }
        catch
        {
        }
        _unmap!.Original(context, resource, subresource);
    }

    private static void UpdateDetour(nint context, nint resource, uint subresource, void* box, void* source, uint rowPitch, uint depthPitch)
    {
        try
        {
            if(_framesLeft > 0 && source != null && box == null && BufferInfo(resource, out var desc))
                lock(Found)
                    Scan("Update", resource, (float*)source, desc.ByteWidth, desc.BindFlags);
        }
        catch
        {
        }
        _update!.Original(context, resource, subresource, box, source, rowPitch, depthPitch);
    }

    private static void Scan(string how, nint resource, float* data, uint size, uint bind)
    {
        _writes++;
        if(size < 48 || size > 16 * 1024 * 1024 || Found.Count > 80)
            return;
        int n = (int)(size / 4);
        foreach(var (label, values) in _targets)
        {
            int first = Array.FindIndex(values, v => !float.IsNaN(v));
            if(first < 0)
                continue;
            float lead = values[first];
            for(int i = 0; i + 12 <= n; i++)
            {
                if(MathF.Abs(data[i + first] - lead) > _tolerance)
                    continue;
                bool match = true;
                for(int k = 0; k < 12 && match; k++)
                    match = float.IsNaN(values[k]) || MathF.Abs(data[i + k] - values[k]) <= _tolerance;
                if(!match)
                    continue;
                Found.Add($"{how} buffer 0x{resource:X} ({size} bytes, bind 0x{bind:X}): {label} at +0x{i * 4:X} (= 48-byte slot {i * 4 / 48}, {i * 4 % 48} bytes in)");
                if(Found.Count > 80)
                    return;
            }
        }
    }
}
