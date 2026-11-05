using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Hooking;

namespace Limber.Gpu;

/// <summary>
/// Separate wobbles: the game keeps one copy of a model's vertices for everyone wearing the same file, so the live rig
/// can only bend it for one character. This gives each other character its own copy: while the game records the draws
/// of one character's model (its draw commands are recorded on worker threads and run later), every draw command that
/// points to the shared vertices is pointed at that character's own copy instead. Nothing is loaded or redrawn, and
/// everyone else (and every other model) is drawn exactly as before.
/// Only this game client's own memory and GPU are touched.
/// </summary>
public static unsafe class LiveRigInstances
{
    // The game's per-mesh draw (records one mesh of one model): its first argument's first field leads to the model.
    private const string DrawMeshSig = "E8 ?? ?? ?? ?? 41 8B 4E ?? 8B C1";
    // Kernel::Context.PushBackCommand: every recorded command goes through it.
    private const string PushCommandSig = "E8 ?? ?? ?? ?? 8B 6E 6C";

    private delegate void DrawMeshDelegate(nint renderer, nint payload, uint a2, uint a3, uint a4, uint a5);
    private delegate void PushCommandDelegate(nint context, nint command);

    private static Hook<DrawMeshDelegate>? _drawMesh;
    private static Hook<PushCommandDelegate>? _pushCommand;
    private static bool _tried;

    /// <summary>Why it isn't available, or "" when it is.</summary>
    public static string Problem { get; private set; } = "";

    [ThreadStatic] private static nint _drawing; // the Render::Model whose mesh this thread is recording now

    // Render::Model (one character's model) -> (the shared vertex buffer, that character's own copy).
    private static volatile Dictionary<nint, (nint Shared, nint Own)[]> _redirects = new();

    // What it did (for the page and for finding out what the game's commands hold).
    private static long _swapped, _seen;
    public static long Swapped => Interlocked.Read(ref _swapped);
    public static long Seen => Interlocked.Read(ref _seen);

    // Command bytes looked through for the buffer (a draw command holds its buffers near its start).
    private const int ScanBytes = 0xC0;

    /// <summary>Hooks the game's draw recording (once); false if it couldn't be found (a game update).</summary>
    public static bool Ensure()
    {
        if (_drawMesh != null && _pushCommand != null)
            return true;
        if (_tried)
            return false;
        _tried = true;
        try
        {
            if (!Plugin.SigScanner.TryScanText(DrawMeshSig, out var draw) || !Plugin.SigScanner.TryScanText(PushCommandSig, out var push))
            {
                Problem = "the game's draw functions weren't found (after a game update this needs a Limber update)";
                return false;
            }
            _drawMesh = Plugin.GameInterop.HookFromAddress<DrawMeshDelegate>(draw, DrawMeshDetour);
            _pushCommand = Plugin.GameInterop.HookFromAddress<PushCommandDelegate>(push, PushCommandDetour);
            Problem = ""; // (enabled when there's something to redirect)
            return true;
        }
        catch (Exception e)
        {
            Problem = "couldn't hook the game's draws: " + e.Message;
            _drawMesh?.Dispose();
            _pushCommand?.Dispose();
            _drawMesh = null;
            _pushCommand = null;
            return false;
        }
    }

    /// <summary>Sets every model that's drawn from its own copy right now (render thread; replaces the last set).</summary>
    public static void Set(Dictionary<nint, (nint Shared, nint Own)[]> redirects)
    {
        _redirects = redirects;
        // (hooked only while there's something to redirect: the game's draws cost nothing extra otherwise)
        try
        {
            if (redirects.Count > 0)
            {
                if (_drawMesh is { IsEnabled: false })
                    _drawMesh.Enable();
                if (_pushCommand is { IsEnabled: false })
                    _pushCommand.Enable();
            }
            else
            {
                if (_drawMesh is { IsEnabled: true })
                    _drawMesh.Disable();
                if (_pushCommand is { IsEnabled: true })
                    _pushCommand.Disable();
            }
        }
        catch
        {
        }
    }

    public static int Count => _redirects.Count;

    private static void DrawMeshDetour(nint renderer, nint payload, uint a2, uint a3, uint a4, uint a5)
    {
        if (_redirects.Count == 0 || payload == 0)
        {
            _drawMesh!.Original(renderer, payload, a2, a3, a4, a5);
            return;
        }
        var before = _drawing;
        try
        {
            // payload -> OnRenderModelParams* -> Model*
            var parameters = *(nint*)payload;
            _drawing = parameters != 0 ? *(nint*)parameters : 0;
        }
        catch
        {
            _drawing = 0;
        }
        try
        {
            _drawMesh!.Original(renderer, payload, a2, a3, a4, a5);
        }
        finally
        {
            _drawing = before;
        }
    }

    private static void PushCommandDetour(nint context, nint command)
    {
        var model = _drawing;
        if (model != 0 && command != 0)
        {
            var redirects = _redirects;
            if (redirects.TryGetValue(model, out var swaps))
            {
                try
                {
                    Swap(command, swaps);
                }
                catch
                {
                    // never stop the game drawing
                }
            }
        }
        _pushCommand!.Original(context, command);
    }

    private static void Swap(nint command, (nint Shared, nint Own)[] swaps)
    {
        int type = *(int*)command;
        if (type is not (5 or 6 or 7)) // Draw, DrawIndexed, DrawIndexedInstanced
            return;
        int bytes = ScanBytes;
        // (only memory that's there: the command sits in the context's own block, but not past a page that isn't)
        if (((command & 0xFFF) + bytes) > 0x1000 && !LiveMesh.IsReadable(command, bytes))
            bytes = (int)(0x1000 - (command & 0xFFF));
        Interlocked.Increment(ref _seen);
        for (int off = 8; off + 8 <= bytes; off += 8)
        {
            nint value = *(nint*)(command + off);
            if (value == 0)
                continue;
            foreach (var (shared, own) in swaps)
                if (value == shared)
                {
                    *(nint*)(command + off) = own;
                    Interlocked.Increment(ref _swapped);
                    break;
                }
        }
    }

    public static void Dispose()
    {
        _redirects = new();
        _drawMesh?.Dispose();
        _pushCommand?.Dispose();
        _drawMesh = null;
        _pushCommand = null;
    }
}
