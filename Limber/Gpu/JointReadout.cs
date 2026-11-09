using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace Limber.Gpu;

/// <summary>
/// Research readout (Advanced page): the game's final per-mesh skinning matrices, read the moment the game builds them.
///
/// From the engine research (2026-11-09, build 2026.10.16): each Model has one 0xE0-byte bone-table object per mdl bone
/// table (array at Model+0x58, count at +0x60). Every frame the table's task Execute (hooked here) writes count 3x4
/// matrices (camera space, invBind x pose x skeleton x view) into a CPU block at Buffer+0x88 of one of four ring
/// buffers (+0x90 + 8 x slot), uploaded later that frame. This only reads them (to check the layout in game); it changes
/// nothing. Off until started; the hook is removed when stopped.
/// </summary>
public static unsafe class JointReadout
{
    private const string ExecuteSig = "48 89 5C 24 20 55 56 41 54 41 55 41 57 48 8D AC 24 20 FF FF FF 48 81 EC E0 01 00 00";

    private delegate void ExecuteDelegate(nint table, nint arg);
    private static Hook<ExecuteDelegate>? _hook;

    public sealed class Table
    {
        public string Model = "";
        public int Slot, Index, Count, Ring = -1;
        public ushort[] Refs = [];
        public float[] Matrices = [];  // Count x 12, as found
        public long Frames;
    }

    private static readonly object Sync = new();
    private static Dictionary<nint, Table> _watch = new();   // table object -> what is known of it
    public static string Problem { get; private set; } = "";
    public static bool Running => _hook is { IsEnabled: true };

    public static bool Start()
    {
        if(_hook != null)
        {
            _hook.Enable();
            return true;
        }
        try
        {
            if(!Plugin.SigScanner.TryScanText(ExecuteSig, out var address))
            {
                Problem = "the game's bone-table function wasn't found (a game update?)";
                return false;
            }
            _hook = Plugin.GameInterop.HookFromAddress<ExecuteDelegate>(address, Detour);
            _hook.Enable();
            Problem = "";
            return true;
        }
        catch(Exception e)
        {
            Problem = "couldn't hook it: " + e.Message;
            _hook = null;
            return false;
        }
    }

    public static void Stop() => _hook?.Disable();

    public static void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
    }

    /// <summary>UI thread: the character whose tables are read (its draw object).</summary>
    public static void Watch(nint drawObject)
    {
        var next = new Dictionary<nint, Table>();
        if(drawObject != 0)
        {
            var cb = (CharacterBase*)drawObject;
            int slots = cb->SlotCount;
            if(slots > 0 && slots <= 64 && cb->Models != null)
                for(int slot = 0; slot < slots; slot++)
                {
                    var model = cb->Models[slot];
                    if(model == null)
                        continue;
                    nint list = *(nint*)((nint)model + 0x58);
                    int count = *(int*)((nint)model + 0x60);
                    if(list == 0 || count <= 0 || count > 16)
                        continue;
                    string file = "?";
                    try
                    {
                        nint handle = *(nint*)((nint)model + 0x30); // (Model.ModelResourceHandle)
                        if(handle != 0)
                            file = System.IO.Path.GetFileName(LiveMesh.StdString(handle + 0x48));
                    }
                    catch { }
                    for(int i = 0; i < count; i++)
                    {
                        nint table = list + i * 0xE0;
                        lock(Sync)
                            next[table] = _watch.TryGetValue(table, out var old) ? old : new Table { Model = file, Slot = slot, Index = i };
                    }
                }
        }
        lock(Sync)
            _watch = next;
    }

    public static List<Table> Snapshot()
    {
        lock(Sync)
            return new List<Table>(_watch.Values);
    }

    // Worker threads (the game runs these in parallel): only the watched tables are copied.
    private static void Detour(nint table, nint arg)
    {
        _hook!.Original(table, arg);
        Table? t;
        lock(Sync)
            if(!_watch.TryGetValue(table, out t))
                return;
        try
        {
            int count = *(int*)(table + 0xC8);
            if(count <= 0 || count > 1024)
                return;
            // The ring buffer just written is the one holding a CPU block now.
            int ring = -1;
            nint block = 0;
            for(int k = 0; k < 4; k++)
            {
                nint buffer = *(nint*)(table + 0x90 + 8 * k);
                if(buffer == 0)
                    continue;
                nint b = *(nint*)(buffer + 0x88);
                if(b != 0)
                {
                    ring = k;
                    block = b;
                }
            }
            if(block == 0)
                return;
            var floats = new float[count * 12];
            fixed(float* to = floats)
                if(!ReadProcessMemory(-1, block, to, count * 48, out var read) || read != count * 48)
                    return;
            var refs = new ushort[count];
            nint refAt = *(nint*)(table + 0xC0);
            if(refAt != 0)
                fixed(ushort* to = refs)
                    ReadProcessMemory(-1, refAt, to, count * 2, out _);
            lock(Sync)
            {
                t.Count = count;
                t.Ring = ring;
                t.Matrices = floats;
                t.Refs = refs;
                t.Frames++;
            }
        }
        catch
        {
            // (a model going away mid-read: skipped)
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nint size, out nint read);
}
