using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace Limber.Core;

/// <summary>
/// (perf) Everything in the object table with its name, read once per drawn frame and shared. A dozen features each went
/// through the whole table every frame looking for their characters by name, and reading a name decodes the game's text
/// each time: one pass instead of a dozen. Only on the thread that draws (see <see cref="NewFrame"/>); anywhere else
/// the table is read fresh, as before.
/// </summary>
public static class FrameObjects
{
    public readonly record struct Entry(IGameObject Object, string Name);

    private static Entry[] _entries = [];
    private static IObjectTable? _table;
    private static long _frame, _builtFor = -1;
    private static int _thread = -1;
    private static long _gameFrame = -1;

    private static unsafe long GameFrame()
    {
        var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        return fw == null ? 0 : (long)fw->FrameCounter;
    }

    /// <summary>Start of a drawn frame (the draw thread): what was read last frame is read again when next asked for.</summary>
    public static void NewFrame()
    {
        _frame++;
        _thread = Environment.CurrentManagedThreadId;
    }

    /// <summary>Every object in the table (address not 0) and its name, as the table has them this frame.</summary>
    public static Entry[] Of(IObjectTable objects)
    {
        bool cached = Environment.CurrentManagedThreadId == _thread;
        // (keyed by the game's own frame count too: when the plugin's drawing is skipped, e.g. in cutscenes, the draw
        // never marks a new frame, and the framework update would otherwise go on using an old list)
        long gameFrame = GameFrame();
        if (gameFrame != _gameFrame)
        {
            _gameFrame = gameFrame;
            _frame++;
        }
        if (cached && _builtFor == _frame && ReferenceEquals(_table, objects))
            return _entries;
        var build = new List<Entry>();
        foreach (var obj in objects)
        {
            if (obj == null || obj.Address == 0)
                continue;
            build.Add(new Entry(obj, obj.Name.TextValue));
        }
        var entries = build.ToArray();
        if (cached)
        {
            _entries = entries;
            _table = objects;
            _builtFor = _frame;
        }
        return entries;
    }
}
